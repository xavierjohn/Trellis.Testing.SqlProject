# Trellis.Testing.SqlProject

[![NuGet Package](https://img.shields.io/nuget/v/Trellis.Testing.SqlProject.svg)](https://www.nuget.org/packages/Trellis.Testing.SqlProject)

Proves a SQL Server database project (DACPAC) matches an EF Core model, and says exactly what differs.

## Installation
```bash
dotnet add package Trellis.Testing.SqlProject
```

## Quick Example
```csharp
using Microsoft.EntityFrameworkCore;
using Trellis.Testing.SqlProject;

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
```

When the two drift, the failure names the object and both values:

```text
Expected the SQL database project to match the EF Core model, but found 2 difference(s):
  - Column [dbo].[Restaurants].[Location_LocatedIn] is at position 4 in the SQL project but 3 in the EF model
  - [dbo].[Restaurants].[Name].Length: SQL project=200; EF model=100
Update the database project (not EF migrations) when the model changes.
```

The test project must copy the built `.dacpac` to its output directory, for example with a
`ProjectReference` to the database project (`ReferenceOutputAssembly="false"`) plus a
`<None Include="...dacpac" CopyToOutputDirectory="PreserveNewest" />` item.

## Why

Teams that keep a SQL project as the schema source of truth and an EF Core model beside it need a
test that catches drift. The usual version compares two DACPACs and reports only
`Change Table: [dbo].[Restaurants]`, which does not say which column or why. Column order is part
of what the project deploys and EF Core orders owned-type columns by declaration, so a column
appended to the end of the SQL table fails the check with no hint that order was the cause.

## Key Features
- **Plain-language differences** — missing, extra, and mismatched columns, indexes, and constraints, each with both values
- **Column positions** — `is at position 4 in the SQL project but 3 in the EF model`
- **No running database** — the model side comes from `Database.GenerateCreateScript()`, so there is no LocalDB or container to start
- **Column order is opt-out** — `IgnoreColumnOrder = true` when order genuinely does not matter
- **Non-default schemas work** — EF's dynamic-SQL schema guard is rewritten so DacFx resolves tables in them

## AI-native

This package carries an API reference for coding agents. Restoring a package never installs agent
instructions. To opt in, restore your consuming project or solution, then run from its Git root:

```bash
dotnet new tool-manifest --output .config
dotnet tool install Trellis.AgentDocs --version 0.1.0-preview.19 --tool-manifest .config/dotnet-tools.json
dotnet tool run agentdocs init <solution-or-project>
```

If the repository already has `.config/dotnet-tools.json`, reuse it instead of creating another manifest.
`init` lists `Trellis.Testing.SqlProject` as pending and prints the package IDs to add to
`approvedPackages` in `.agentdocs/policy.json`. Add it, then run `dotnet tool run agentdocs sync` to install
the reference under Git-root `.agentdocs/`. The reference is on demand: the generated index describes it,
and an agent opens it when its task concerns proving a SQL database project matches an EF Core model. After a
package upgrade, run `dotnet restore` and then `dotnet tool run agentdocs sync`.

## Links

- [Repository](https://github.com/xavierjohn/Trellis.Testing.SqlProject)
- [License: MIT](https://github.com/xavierjohn/Trellis.Testing.SqlProject/blob/main/LICENSE)
