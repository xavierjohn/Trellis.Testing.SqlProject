namespace Trellis.Testing.SqlProject;

using Microsoft.SqlServer.Dac;
using Microsoft.SqlServer.Dac.Model;

/// <summary>
/// Compares column defaults by column and expression, ignoring constraint names.
/// </summary>
/// <remarks>
/// EF Core's create script emits every default as an unnamed constraint, while a SQL project normally
/// names its defaults. DacFx matches constraints by name, so it would report every default-valued
/// column as drift before ever comparing the expression. The constraint name does not change behavior,
/// so defaults are compared here by the column they belong to instead.
/// </remarks>
internal static class DefaultConstraintComparison
{
    internal static IReadOnlyList<string> Describe(TSqlModel project, TSqlModel model, IEqualityComparer<string> identifiers)
    {
        // A column missing from one side is already reported as a column difference. Columns are named
        // as the project spells them.
        var modelColumns = new HashSet<string>(ColumnNames(model), identifiers);
        var sharedColumns = ColumnNames(project)
            .Where(modelColumns.Contains)
            .OrderBy(column => column, StringComparer.Ordinal);
        var projectDefaults = DefaultsByColumn(project, identifiers);
        var modelDefaults = DefaultsByColumn(model, identifiers);

        var report = new List<string>();
        foreach (var column in sharedColumns)
        {
            projectDefaults.TryGetValue(column, out var projectExpression);
            modelDefaults.TryGetValue(column, out var modelExpression);
            if (!SqlExpressionText.Equivalent(projectExpression, modelExpression, identifiers))
                report.Add($"{column}.DefaultExpression: SQL project={SqlExpressionText.Display(projectExpression)}; EF model={SqlExpressionText.Display(modelExpression)}");
        }

        return report;
    }

    private static IEnumerable<string> ColumnNames(TSqlModel model) =>
        model.GetObjects(DacQueryScopes.UserDefined, Table.TypeClass)
            .SelectMany(table => table.GetReferenced(Table.Columns))
            .Select(column => column.Name.ToString());

    private static Dictionary<string, string> DefaultsByColumn(TSqlModel model, IEqualityComparer<string> identifiers)
    {
        var defaults = new Dictionary<string, string>(identifiers);
        foreach (var constraint in model.GetObjects(DacQueryScopes.UserDefined, DefaultConstraint.TypeClass))
        {
            var column = constraint.GetReferenced(DefaultConstraint.TargetColumn).FirstOrDefault();
            var expression = constraint.GetProperty(DefaultConstraint.Expression)?.ToString();
            if (column is not null && expression is not null)
                defaults[column.Name.ToString()] = expression;
        }

        return defaults;
    }
}
