namespace Trellis.Testing.SqlProject;

/// <summary>
/// Thrown by <see cref="DatabaseProjectAssert.MatchesModel"/> when the SQL database project and the
/// EF Core model differ.
/// </summary>
/// <remarks>
/// The library throws its own exception rather than an assertion-library type so it never binds to a
/// particular assertion library's version. Test frameworks report any unhandled exception as a failed test.
/// </remarks>
public sealed class DatabaseProjectDriftException : Exception
{
    /// <summary>
    /// Creates an exception describing the given differences.
    /// </summary>
    /// <param name="differences">One line per difference, as returned by <see cref="DatabaseProjectAssert.Compare"/>.</param>
    public DatabaseProjectDriftException(IReadOnlyList<string> differences)
        : base(BuildMessage(differences))
    {
        Differences = differences;
    }

    /// <summary>
    /// One line per difference between the SQL project and the EF Core model.
    /// </summary>
    public IReadOnlyList<string> Differences { get; }

    private static string BuildMessage(IReadOnlyList<string> differences)
    {
        ArgumentNullException.ThrowIfNull(differences);
        return $"Expected the SQL database project to match the EF Core model, but found {differences.Count} " +
            $"difference(s):{Environment.NewLine}" +
            string.Join(Environment.NewLine, differences.Select(difference => $"  - {difference}")) +
            $"{Environment.NewLine}Update the database project (not EF migrations) when the model changes.";
    }
}
