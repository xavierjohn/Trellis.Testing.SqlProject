namespace Trellis.Testing.SqlProject;

using System.Text.RegularExpressions;

internal static partial class EfCreateScript
{
    // EF guards each non-default schema with dynamic SQL, which DacFx does not parse as a schema,
    // leaving every table in it with an unresolved schema reference.
    [GeneratedRegex(@"IF SCHEMA_ID\(N'(?:[^']|'')+'\) IS NULL EXEC\(N'CREATE SCHEMA (?<schema>(?:\[(?:[^\]]|\]\])+\]));'\);")]
    private static partial Regex DynamicSchemaGuard();

    internal static string ForDacFx(string script) =>
        DynamicSchemaGuard().Replace(script, "CREATE SCHEMA ${schema};");
}
