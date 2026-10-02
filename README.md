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

Verify the packed output, including the AgentDocs guidance manifest. This packs into its own empty
temporary directory, so it always checks the package built from the current sources:

```powershell
./build/test-apireference-packaging.ps1
```

To verify packages that are already packed (CI does this so the bytes checked are the bytes uploaded),
pass a directory that holds exactly one `.nupkg`, this package. The script fails if it finds any other
`.nupkg` (the workflows publish every package in the directory, so an unverified one must not be there):

```powershell
./build/test-apireference-packaging.ps1 -PackageDirectory <directory-with-one-package>
```

Versions come from `version.json` through Nerdbank.GitVersioning, so a full git history is
required to build. This package is versioned and published independently of the Trellis framework
packages.
