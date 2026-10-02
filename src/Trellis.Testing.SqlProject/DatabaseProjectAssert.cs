namespace Trellis.Testing.SqlProject;

using Microsoft.EntityFrameworkCore;
using Microsoft.SqlServer.Dac;
using Microsoft.SqlServer.Dac.Model;

/// <summary>
/// Asserts that a SQL Server database project (the schema source of truth, built to a DACPAC)
/// matches the relational model of an EF Core <see cref="DbContext"/>.
/// </summary>
/// <remarks>
/// The EF Core side is derived from <c>Database.GenerateCreateScript()</c>, so no database server
/// has to be running. Differences are reported in plain language, naming the object and both values.
/// </remarks>
public static class DatabaseProjectAssert
{
    /// <summary>
    /// Fails when the database project and the EF Core model differ.
    /// </summary>
    /// <param name="context">A context configured with the SQL Server provider. It is never connected.</param>
    /// <param name="databaseProjectDacpac">Path to the DACPAC built from the database project.</param>
    /// <param name="options">Comparison options; <see langword="null"/> uses the defaults.</param>
    /// <exception cref="DatabaseProjectDriftException">The database project and the model differ.</exception>
    public static void MatchesModel(
        DbContext context,
        string databaseProjectDacpac,
        DatabaseProjectAssertOptions? options = null)
    {
        var differences = Compare(context, databaseProjectDacpac, options);
        if (differences.Count > 0)
            throw new DatabaseProjectDriftException(differences);
    }

    /// <summary>
    /// Compares the database project with the EF Core model and returns one line per difference.
    /// </summary>
    /// <param name="context">A context configured with the SQL Server provider. It is never connected.</param>
    /// <param name="databaseProjectDacpac">Path to the DACPAC built from the database project.</param>
    /// <param name="options">Comparison options; <see langword="null"/> uses the defaults.</param>
    /// <param name="cancellationToken">Cancels the comparison.</param>
    /// <returns>An empty list when the two match.</returns>
    /// <exception cref="FileNotFoundException">The DACPAC does not exist; build the database project first.</exception>
    public static IReadOnlyList<string> Compare(
        DbContext context,
        string databaseProjectDacpac,
        DatabaseProjectAssertOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseProjectDacpac);
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(databaseProjectDacpac))
            throw new FileNotFoundException(
                $"The database project DACPAC was not found at '{databaseProjectDacpac}'. Build the database project and copy its .dacpac next to the tests.",
                databaseProjectDacpac);

        options ??= new DatabaseProjectAssertOptions();
        using var projectModel = TSqlModel.LoadFromDacpac(
            databaseProjectDacpac, new ModelLoadOptions(DacSchemaModelStorageType.Memory, loadAsScriptBackedModel: false));
        using var efModel = BuildEfModel(context, options.SqlServerVersion, projectModel.CopyModelOptions().Collation);

        // DacFx compares packages on disk, so the EF model is also written out for the schema comparison.
        var efDacpac = Path.Combine(Path.GetTempPath(), $"TrellisEfModel_{Guid.NewGuid():N}.dacpac");
        try
        {
            DacPackageExtensions.BuildPackage(efDacpac, efModel, new PackageMetadata { Name = "EfModel", Version = "1.0.0" });
            return SchemaComparisonReport.Compare(
                databaseProjectDacpac, efDacpac, projectModel, efModel, options.IgnoreColumnOrder, cancellationToken);
        }
        finally
        {
            if (File.Exists(efDacpac))
                File.Delete(efDacpac);
        }
    }

    // The model is built under the project's collation: in a case-sensitive project [Cases] and [cases]
    // are two tables, which a case-insensitive model rejects as a duplicate before any comparison runs.
    private static TSqlModel BuildEfModel(DbContext context, SqlServerVersion version, string? collation)
    {
        var modelOptions = collation is null ? new TSqlModelOptions() : new TSqlModelOptions { Collation = collation };
        var model = new TSqlModel(version, modelOptions);
        try
        {
            model.AddObjects(EfCreateScript.ForDacFx(context.Database.GenerateCreateScript()));
            return model;
        }
        catch
        {
            model.Dispose();
            throw;
        }
    }
}
