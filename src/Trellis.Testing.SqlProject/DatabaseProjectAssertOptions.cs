namespace Trellis.Testing.SqlProject;

using Microsoft.SqlServer.Dac.Model;

/// <summary>
/// Options for <see cref="DatabaseProjectAssert"/>.
/// </summary>
public sealed record DatabaseProjectAssertOptions
{
    /// <summary>
    /// When <see langword="true"/>, a column that exists on both sides but at a different position
    /// is not reported. Defaults to <see langword="false"/>, because column order is part of the
    /// schema the SQL project deploys and EF Core orders owned-type columns by declaration.
    /// </summary>
    public bool IgnoreColumnOrder { get; init; }

    /// <summary>
    /// The SQL Server version used to parse the EF Core model's create script. Set it to the
    /// version the SQL project targets. Defaults to <see cref="SqlServerVersion.Sql160"/>.
    /// </summary>
    public SqlServerVersion SqlServerVersion { get; init; } = SqlServerVersion.Sql160;
}
