namespace Trellis.Testing.SqlProject;

using System.Globalization;
using Microsoft.SqlServer.Dac.Compare;
using Microsoft.SqlServer.Dac.Model;

/// <summary>
/// Turns a DacFx schema comparison (project = source, EF model = target) into readable lines.
/// </summary>
internal static class SchemaComparisonReport
{
    internal static IReadOnlyList<string> Compare(
        string projectDacpac,
        string efDacpac,
        TSqlModel projectModel,
        TSqlModel efModel,
        bool ignoreColumnOrder,
        CancellationToken cancellationToken)
    {
        var comparison = new SchemaComparison(
            new SchemaCompareDacpacEndpoint(projectDacpac),
            new SchemaCompareDacpacEndpoint(efDacpac));

        comparison.Options.IgnoreWhitespace = true;
        comparison.Options.IgnoreComments = true;
        comparison.Options.IgnoreKeywordCasing = true;
        comparison.Options.IgnoreSemicolonBetweenStatements = true;
        comparison.Options.IgnoreColumnOrder = ignoreColumnOrder;

        var result = comparison.Compare(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var report = result.GetErrors().Select(error => $"Schema comparison diagnostic: {error.Message}").ToList();

        // Identifiers match under the project's collation, exactly as DacFx matched them above:
        // [Score] and [score] are one column in a case-insensitive project and two in a case-sensitive one.
        var identifiers = projectModel.CollationComparer;
        foreach (var difference in result.Differences)
            Describe(difference, report, ignoreColumnOrder, identifiers);

        report.AddRange(DefaultConstraintComparison.Describe(projectModel, efModel, identifiers));
        return report;
    }

    private static void Describe(SchemaDifference difference, List<string> report, bool ignoreColumnOrder, IEqualityComparer<string> identifiers)
    {
        if (difference.DifferenceType == SchemaDifferenceType.Property)
        {
            DescribeProperty(difference, report);
            return;
        }

        // DacFx matches constraints by name, which EF's unnamed defaults can never satisfy.
        // DefaultConstraintComparison reports defaults by column instead.
        if (IsDefaultConstraint(difference))
            return;

        var name = difference.SourceObject?.Name?.ToString()
            ?? difference.TargetObject?.Name?.ToString()
            ?? difference.Name
            ?? "(unnamed)";
        var typeName = difference.SourceObject?.ObjectType?.Name
            ?? difference.TargetObject?.ObjectType?.Name
            ?? "object";

        // An added or deleted object already explains the drift; its default properties add noise.
        switch (difference.UpdateAction)
        {
            case SchemaUpdateAction.Add:
                report.Add($"{typeName} {name} is in the SQL project but not in the EF model");
                return;
            case SchemaUpdateAction.Delete:
                report.Add($"{typeName} {name} is in the EF model but not in the SQL project");
                return;
        }

        var before = report.Count;
        if (!ignoreColumnOrder
            && difference.SourceObject?.ObjectType == Table.TypeClass
            && difference.TargetObject?.ObjectType == Table.TypeClass)
            DescribeColumnOrder(difference.SourceObject, difference.TargetObject, report, identifiers);

        // Included controls deployment, not whether a child is a meaningful schema difference.
        foreach (var child in difference.Children)
            Describe(child, report, ignoreColumnOrder, identifiers);

        var onlyDefaultConstraintChildren = difference.Children.Any() && difference.Children.All(IsDefaultConstraint);
        if (report.Count == before && !onlyDefaultConstraintChildren)
            report.Add($"{typeName} {name} differs between the SQL project and the EF model");
    }

    private static bool IsDefaultConstraint(SchemaDifference difference) =>
        (difference.SourceObject ?? difference.TargetObject)?.ObjectType == DefaultConstraint.TypeClass;

    private static void DescribeProperty(SchemaDifference difference, List<string> report)
    {
        var source = difference.Parent?.SourceObject;
        var target = difference.Parent?.TargetObject;
        var ownerName = source?.Name?.ToString() ?? target?.Name?.ToString() ?? "(unnamed)";
        report.Add($"{ownerName}.{difference.Name}: "
            + $"SQL project={DescribeValue(source, difference.Name)}; "
            + $"EF model={DescribeValue(target, difference.Name)}");
    }

    private static string DescribeValue(TSqlObject? sqlObject, string name)
    {
        if (sqlObject is null)
            return "(absent)";

        foreach (var metadataName in MetadataNameCandidates(sqlObject, name))
        {
            var property = sqlObject.ObjectType.Properties.FirstOrDefault(p => p.Name == metadataName);
            if (property is not null)
            {
                var value = FormatValue(sqlObject.GetProperty(property), property.DataType);

                // DacFx keeps an expression's outer parentheses as the project wrote them while EF omits
                // them, and the comparison already treats the two as equal, so show neither.
                return IsExpressionLabel(name) ? SqlExpressionText.Display(value) : value;
            }

            var relationship = sqlObject.ObjectType.Relationships.FirstOrDefault(r => r.Name == metadataName);
            if (relationship is not null)
            {
                var names = sqlObject.GetReferencedRelationshipInstances(relationship)
                    .Select(r => r.ObjectName?.ToString() ?? "(unresolved reference)").ToArray();
                return names.Length == 0 ? "(none)" : string.Join(", ", names);
            }
        }

        return "(value unavailable in DacFx metadata)";
    }

    // DacFx returns enum-typed properties (a foreign key's delete action, for one) as their underlying
    // number, so render them by the enum member's name.
    private static string FormatValue(object? value, Type propertyType) => value switch
    {
        null => "(null)",
        bool flag => flag ? "true" : "false",
        _ when propertyType.IsEnum => Enum.ToObject(propertyType, value).ToString() ?? "(null)",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "(null)",
    };

    // Comparison labels and public model metadata name the same member differently: the label carries
    // an Is or On prefix (IsNullable, IsUnique, OnDeleteAction) that the metadata descriptor drops
    // (Nullable, Unique, DeleteAction). Resolving by that rule covers every such member rather than a
    // hand-picked few. Every expression-bearing object (check constraint, computed column, default)
    // names its descriptor Expression while the label says ExpressionScript, so that suffix is
    // resolved the same way; the one remaining rename that follows no rule is listed explicitly.
    private static bool IsExpressionLabel(string label) =>
        label.EndsWith("ExpressionScript", StringComparison.Ordinal);

    private static IEnumerable<string> MetadataNameCandidates(TSqlObject sqlObject, string label)
    {
        if (label == "Type" && sqlObject.ObjectType == Column.TypeClass)
            yield return Column.DataType.Name;
        if (IsExpressionLabel(label))
            yield return "Expression";

        yield return label;
        if (label.Length > 2 && (label.StartsWith("Is", StringComparison.Ordinal) || label.StartsWith("On", StringComparison.Ordinal)))
            yield return label[2..];
    }

    private static void DescribeColumnOrder(TSqlObject projectTable, TSqlObject modelTable, List<string> report, IEqualityComparer<string> identifiers)
    {
        // Pure reorders have no child differences, so inspect the ordered table relationships.
        var projectColumns = projectTable.GetReferenced(Table.Columns).Select(c => c.Name.ToString()).ToArray();
        var modelPositions = modelTable.GetReferenced(Table.Columns)
            .Select((column, index) => (Name: column.Name.ToString(), Position: index + 1))
            .ToDictionary(c => c.Name, c => c.Position, identifiers);

        for (var index = 0; index < projectColumns.Length; index++)
        {
            var name = projectColumns[index];
            if (modelPositions.TryGetValue(name, out var modelPosition) && modelPosition != index + 1)
                report.Add(FormattableString.Invariant(
                    $"Column {name} is at position {index + 1} in the SQL project but {modelPosition} in the EF model"));
        }
    }
}
