# Trellis.Testing.SqlProject

Test helper that proves a SQL Server database project (DACPAC) matches an EF Core model, with plain-language differences and no running database.

The package README, with installation and a quick example, is in
[`src/Trellis.Testing.SqlProject/README.md`](src/Trellis.Testing.SqlProject/README.md). The API
reference for coding agents is in
[`docs/api_reference`](docs/api_reference/trellis-api-testing-sqlproject.md).

## Development

Tests run on Microsoft.Testing.Platform (xUnit v3). Run them from the repository root:

```powershell
dotnet test Trellis.Testing.SqlProject.slnx -c Release
```

Verify the packed output, including the AgentDocs guidance manifest, with:

```powershell
dotnet pack Trellis.Testing.SqlProject.slnx -c Release -o artifacts
./build/test-apireference-packaging.ps1 -PackageDirectory artifacts
```

Versions come from `version.json` through Nerdbank.GitVersioning, so a full git history is
required to build. This package is versioned and published independently of the Trellis framework
packages.
