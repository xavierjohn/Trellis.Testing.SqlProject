---
package: Trellis.Testing.SqlProject
namespaces: [Trellis.Testing.SqlProject]
types: [DatabaseProjectAssert, DatabaseProjectAssertOptions, DatabaseProjectDriftException]
version: v1
last_verified: 2026-10-01
audience: [llm]
---
# Trellis.Testing.SqlProject API Reference

Test helper that proves a SQL Server database project matches an EF Core model.

- **Package:** `Trellis.Testing.SqlProject`
- **Namespace:** `Trellis.Testing.SqlProject`
- **Depends on:** `Microsoft.EntityFrameworkCore.Relational`, `Microsoft.SqlServer.DacFx`. It has no assertion-library dependency, so it works beside any FluentAssertions version.
- **Needs a running database:** no

## Use this file when

- A SQL project (built to a `.dacpac`) is the schema source of truth and an EF Core model sits beside it, and you need a test that fails when they drift.
- An existing drift test fails with only `Change Table: [dbo].[Orders]` and you need to know *which column* and *why*.
- You are deciding whether column order should count as drift.

## Patterns Index

| Goal | Use this | See |
|---|---|---|
| Fail a test when the project and the model differ | `DatabaseProjectAssert.MatchesModel(context, dacpacPath)` | [`MatchesModel`](#matchesmodel) |
| Read the differences from a failure | `DatabaseProjectDriftException.Differences` | [`DatabaseProjectDriftException`](#databaseprojectdriftexception) |
| Get the differences as data instead of an exception | `DatabaseProjectAssert.Compare(context, dacpacPath)` | [`Compare`](#compare) |
| Stop reordered columns counting as drift | `new DatabaseProjectAssertOptions { IgnoreColumnOrder = true }` | [`DatabaseProjectAssertOptions`](#databaseprojectassertoptions) |
| Parse the model with the SQL Server version the project targets | `DatabaseProjectAssertOptions.SqlServerVersion` | [`DatabaseProjectAssertOptions`](#databaseprojectassertoptions) |
| Read a difference line | The wording table | [Report wording](#report-wording) |

## Quick start

```csharp
using Microsoft.EntityFrameworkCore;
using Trellis.Testing.SqlProject;

public class SchemaTests
{
    [Fact]
    public void Database_project_matches_the_ef_core_model()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=.;Database=Unused") // never connected
            .Options;
        using var context = new AppDbContext(options);

        DatabaseProjectAssert.MatchesModel(
            context,
            Path.Combine(AppContext.BaseDirectory, "MyApp.Database.dacpac"));
    }
}
```

The test project must copy the built `.dacpac` to its output directory, for example with a
`ProjectReference` to the database project (`ReferenceOutputAssembly="false"`) plus a `<None
Include="...dacpac" CopyToOutputDirectory="PreserveNewest" />` item.

## How it works

1. `context.Database.GenerateCreateScript()` produces the T-SQL EF Core would use to create the model. No connection is opened, so the context only needs the SQL Server provider configured.
2. EF guards every non-default schema with dynamic SQL (`IF SCHEMA_ID(...) IS NULL EXEC(N'CREATE SCHEMA ...')`), which DacFx does not recognise as a schema. That guard is rewritten to a plain `CREATE SCHEMA` first; without the rewrite every table in the schema fails with an unresolved schema reference.
3. The script is loaded into a DacFx `TSqlModel`, packaged to a temporary `.dacpac`, and compared with the database project's `.dacpac` using DacFx `SchemaComparison`. The project is the **source**, the EF model the **target**.
4. The comparison is translated into the lines below. The temporary package is deleted.

Because the EF side is derived from a script rather than extracted from a live database, there
are no server-scoped objects to filter out and the test runs anywhere DacFx runs.

## Types

### `DatabaseProjectAssert`

```csharp
public static class DatabaseProjectAssert
{
    public static void MatchesModel(
        DbContext context,
        string databaseProjectDacpac,
        DatabaseProjectAssertOptions? options = null);

    public static IReadOnlyList<string> Compare(
        DbContext context,
        string databaseProjectDacpac,
        DatabaseProjectAssertOptions? options = null,
        CancellationToken cancellationToken = default);
}
```

#### `MatchesModel`

Throws `DatabaseProjectDriftException` with a multi-line message listing every difference, then a
reminder to update the database project (not EF migrations). Does nothing when the two match. Test
frameworks report the unhandled exception as a failed test.

#### `Compare`

Returns one line per difference, or an empty list when the two match.

| Throws | When |
|---|---|
| `ArgumentNullException` | `context` is `null`. |
| `ArgumentException` | `databaseProjectDacpac` is null, empty, or whitespace. |
| `OperationCanceledException` | `cancellationToken` is already cancelled; checked before anything is read. |
| `FileNotFoundException` | The `.dacpac` does not exist. The message names the path and says to build the database project. |

### `DatabaseProjectDriftException`

```csharp
public sealed class DatabaseProjectDriftException : Exception
{
    public DatabaseProjectDriftException(IReadOnlyList<string> differences);
    public IReadOnlyList<string> Differences { get; }
}
```

Thrown by `MatchesModel`. `Differences` holds the same lines `Compare` returns; `Message` renders them
one per line. The library throws its own exception rather than an assertion-library type so it never
binds to a particular assertion library's version.

### `DatabaseProjectAssertOptions`

```csharp
public sealed record DatabaseProjectAssertOptions
{
    public bool IgnoreColumnOrder { get; init; }
    public SqlServerVersion SqlServerVersion { get; init; } = SqlServerVersion.Sql160;
}
```

| Property | Default | Meaning |
|---|---|---|
| `IgnoreColumnOrder` | `false` | When `true`, a column present on both sides at different positions is not reported. Missing, extra, and mismatched columns are still reported. |
| `SqlServerVersion` | `Sql160` | The version used to parse the model's script. Set it to the version the SQL project targets. |

Leave `IgnoreColumnOrder` off unless column order genuinely does not matter to you. The SQL project
deploys its column order, and EF Core orders owned-type columns by declaration, so a column added to
the end of the project table but declared earlier in the model is real drift.

## Report wording

"Project" is the SQL database project; "model" is the EF Core model.

| Situation | Line |
|---|---|
| Object only in the project | `Column [dbo].[Orders].[Notes] is in the SQL project but not in the EF model` |
| Object only in the model | `Column [dbo].[Orders].[Notes] is in the EF model but not in the SQL project` |
| Same column, different position | `Column [dbo].[Orders].[Notes] is at position 4 in the SQL project but 3 in the EF model` |
| Same object, different property | `[dbo].[Orders].[Name].Length: SQL project=200; EF model=100` |
| Index uniqueness differs | `[sales].[Orders].[IX_Orders_Number].IsUnique: SQL project=false; EF model=true` |
| Foreign key delete action differs | `[sales].[FK_Orders_Customers_CustomerId].OnDeleteAction: SQL project=NoAction; EF model=Cascade` |
| Default expression differs, or exists on one side only | `[dbo].[Orders].[Status].DefaultExpression: SQL project=0; EF model=1` (`(none)` when a side has no default) |
| Computed column or check constraint expression differs | `[dbo].[Orders].[Total].ExpressionScript: SQL project=[A] * [B]; EF model=[A] + [B]` |
| Differs but DacFx gave no detail | `Table [dbo].[Orders] differs between the SQL project and the EF model` |
| DacFx could not complete part of the comparison | `Schema comparison diagnostic: ...` |

Object types in these lines are DacFx names: `Table`, `Column`, `Index`, `PrimaryKeyConstraint`,
`ForeignKeyConstraint`, and so on. Positions are 1-based. A column added in the middle of a table
also reports the columns it shifted.

## Defaults are compared by column, not by constraint name

EF Core's create script emits every default as an unnamed constraint, while a SQL project normally
names its defaults (`CONSTRAINT [DF_Orders_Status] DEFAULT (0)`). DacFx matches constraints by name, so
left alone it would report every default-valued column as drift before comparing the expression. The
constraint name does not change behavior, so the package compares defaults by the column they belong
to. A default that exists on one side only, or whose expression differs, is reported as
`DefaultExpression`.

Each default expression is parsed with the SQL Server parser and compared by what SQL Server
compares, not as text:

| Part of the expression | Compared |
|---|---|
| Keywords (`NEXT VALUE FOR`, `CAST ... AS`), built-in function names (`GETDATE()`), built-in type names, collation names in a `COLLATE` clause, numbers and operators | Case-insensitively, whatever the project's collation |
| Names of objects, such as a sequence in `NEXT VALUE FOR [dbo].[Seq]` or a schema-qualified function | Under the project's collation, so `[Seq]` and `[seq]` are different sequences in a case-sensitive project |
| String literals | Contents exactly: `'a'` and `'A'` differ, and `N'a'` differs from `'a'`. The `N` prefix itself is case-insensitive, so `n'a'` equals `N'a'` |
| Quoting and redundant outer parentheses | Ignored: `dbo.Seq` equals `[dbo].[Seq]`, and `DEFAULT 5` equals `DEFAULT (5)` |

The expression is otherwise not normalized: `DEFAULT (1 + 1)` and `DEFAULT (2)` differ. An expression
that does not parse is compared exactly, so it can be reported as a difference but never hidden.
Whether a constraint is *named* is never reported.

## Identifier casing follows the project's collation

Schema, table and column names are matched under the SQL project's collation, the same way DacFx
matches them. In a case-insensitive project (the SQL Server default) `[dbo].[defaults].[score]` and
`[dbo].[Defaults].[Score]` are the same column, so a difference in default value or column position
is still reported; the line spells the name as the project does. In a case-sensitive project they are
different columns and are reported as such. This applies to the default and column-order checks as
well as to everything DacFx compares itself.

The EF model's create script is loaded under the same collation, read from the project's `.dacpac`.
That matters when a case-sensitive project and its EF model contain names that differ only by case
(`[Cases]` and `[cases]`): a case-insensitive model would reject them as duplicates before any
comparison ran.

## Common traps

- **The `.dacpac` must be built and copied to the test output.** A missing file throws `FileNotFoundException`; it is never treated as "no differences".
- **The context is never connected, but it must configure the SQL Server provider.** `GenerateCreateScript()` needs the provider to produce SQL Server types; a placeholder connection string is enough.
- **Tables outside the model are drift.** A table in the project that no EF entity maps to is reported as `is in the SQL project but not in the EF model`. Map it, or keep that table in a separate project.
- **Only what EF emits in `GenerateCreateScript()` is compared.** Objects EF has no model for (views, stored procedures, triggers, permissions) appear as project-only objects.
- **Migrations are not involved.** The comparison is the model's create script against the project, so a project that also defines `__EFMigrationsHistory` reports it as `is in the SQL project but not in the EF model`.
