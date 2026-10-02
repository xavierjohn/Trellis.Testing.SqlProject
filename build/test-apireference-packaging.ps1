#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Verifies that Trellis.Testing.SqlProject publishes the LLM API reference as AgentDocs guidance and
    carries correct listing metadata.

.DESCRIPTION
    The API reference reaches a consumer only through the opt-in Trellis.AgentDocs local tool, and only
    if the package carries a correct guidance manifest. Each part can break silently - the build stays
    green, the tests stay green, and the guidance simply never installs:

      1. The package packs the doc at its root path, and a guidance/reference-manifest.json whose
         SHA-256, onDemand usage and "Open when ..." description match the packed bytes.
      2. Nothing in the package runs in a consumer's build: no build/ or buildTransitive/ assets, no
         trellis/ directory, and no dependency leaking the build-only Trellis.AgentDocs.Packaging
         helper. Restoring a package must never write to a consumer's repository.
      3. The published Trellis.AgentDocs tool accepts the packed package under `validate --strict`:
         the manifest contract plus discoverability (links that leave the package, front matter, size
         budgets). A warning fails this gate like an error.
      4. The README tells a consumer how to opt in: install the tool, approve the package, sync.
      5. No PackagePath declares a backslash. A trailing backslash is a directory marker on Windows
         but not on Linux, where it normalizes and NuGet appends its own separator, producing
         malformed entries such as "dir//name".

    It then checks the nuspec listing metadata: icon, README, and the projectUrl/repository URLs.

.NOTES
    Exit code 0 = all checks passed. Non-zero = at least one check failed.

    By default the script packs into a temporary directory and cleans up after itself. Pass
    -PackageDirectory to verify packages that have ALREADY been packed. The publish workflow uses that
    mode so the artifacts it inspects are byte-for-byte the artifacts it pushes. The directory must hold
    exactly one .nupkg, and it must be Trellis.Testing.SqlProject: the workflows ship every .nupkg in the
    directory, so the script fails rather than guess which of several to verify or let an unverified
    package ride along.
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',

    # Verify pre-packed .nupkg files in this directory instead of packing. The directory is left alone
    # on exit; only a directory this script created is cleaned up.
    [string] $PackageDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repoRoot 'Trellis.Testing.SqlProject.slnx'
$packageId = 'Trellis.Testing.SqlProject'
$guidancePath = 'trellis-api-testing-sqlproject.md'
$expectedRepo = 'https://github.com/xavierjohn/Trellis.Testing.SqlProject'

$packedHere = [string]::IsNullOrWhiteSpace($PackageDirectory)
if ($packedHere) {
    $outDir = Join-Path ([System.IO.Path]::GetTempPath()) "sqlproject-pack-gate-$([System.Guid]::NewGuid().ToString('N'))"
}
else {
    $outDir = (Resolve-Path -Path $PackageDirectory).Path
}

$failures = [System.Collections.Generic.List[string]]::new()

function Assert-True {
    param(
        [bool] $Condition,
        [string] $Message,
        [string] $Detail
    )
    if ($Condition) {
        Write-Host "  PASS  $Message"
    }
    else {
        Write-Host "  FAIL  $Message"
        if ($Detail) { Write-Host "        $Detail" }
        $script:failures.Add($Message)
    }
}

try {
    if ($packedHere) {
        New-Item -ItemType Directory -Path $outDir -Force | Out-Null

        Write-Host "Packing $solution -> $outDir"
        $packLog = & dotnet pack $solution -c $Configuration -o $outDir 2>&1
        if ($LASTEXITCODE -ne 0) {
            $packLog | Write-Host
            throw "dotnet pack failed with exit code $LASTEXITCODE."
        }
    }
    else {
        Write-Host "Verifying pre-packed output in $outDir"
        if (-not (Get-ChildItem -Path $outDir -Filter '*.nupkg' -File)) {
            throw "No .nupkg files found in '$outDir'. Run dotnet pack before invoking with -PackageDirectory."
        }
    }

    try { Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction Stop } catch { }
    if (-not ('System.IO.Compression.ZipFile' -as [type])) {
        throw 'System.IO.Compression.ZipFile is unavailable; cannot inspect packages.'
    }

    # Exactly one .nupkg in the whole directory, never "the first match". The workflows upload and
    # publish with a *.nupkg glob, so every package in the directory is shipped: anything this script
    # does not verify must not be there, and a reused directory holding earlier builds would otherwise
    # verify one package while a different one is published. (A legacy .symbols.nupkg also ends in
    # .nupkg, so it is counted here and fails the gate rather than slipping through.)
    $candidates = @(Get-ChildItem -Path $outDir -Filter '*.nupkg' -File)
    if ($candidates.Count -eq 0) { throw "No package produced for '$packageId'. Is it still packable?" }
    if ($candidates.Count -gt 1) {
        throw ("Found $($candidates.Count) .nupkg files in '$outDir' ($(($candidates | ForEach-Object Name) -join ', ')); expected exactly one, " +
            "'$packageId'. Run this script without -PackageDirectory so it packs into its own empty directory, " +
            'or pack into an empty directory first.')
    }
    $pkg = $candidates[0].FullName

    $zip = [System.IO.Compression.ZipFile]::OpenRead($pkg)
    try {
        $entries = @($zip.Entries | ForEach-Object { $_.FullName })

        $nuspecEntry = $zip.Entries | Where-Object { $_.FullName -like '*.nuspec' } | Select-Object -First 1
        if (-not $nuspecEntry) { throw "No .nuspec inside '$pkg'." }
        $reader = [System.IO.StreamReader]::new($nuspecEntry.Open())
        try { $nuspec = [xml]$reader.ReadToEnd() } finally { $reader.Dispose() }

        # The file name is not the identity: the package declares its own ID, and that is what is published.
        $declaredId = $nuspec.package.metadata.id
        if ($declaredId -ne $packageId) { throw "'$pkg' declares package ID '$declaredId', expected '$packageId'." }

        $readmeText = ''
        $readmeEntry = $zip.GetEntry('README.md')
        if ($readmeEntry) {
            $reader = [System.IO.StreamReader]::new($readmeEntry.Open())
            try { $readmeText = $reader.ReadToEnd() } finally { $reader.Dispose() }
        }

        $manifest = $null
        $hash = $null
        $manifestEntry = $zip.GetEntry('guidance/reference-manifest.json')
        $docEntry = $zip.GetEntry($guidancePath)
        if ($manifestEntry -and $docEntry) {
            $reader = [System.IO.StreamReader]::new($manifestEntry.Open())
            try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
            $stream = $docEntry.Open()
            try {
                $memory = [System.IO.MemoryStream]::new()
                $stream.CopyTo($memory)
                $hash = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($memory.ToArray())).ToLowerInvariant()
            }
            finally { $stream.Dispose() }
        }
    }
    finally { $zip.Dispose() }

    # --- Declarations: PackagePath must not contain a backslash ------------------------------------
    $backslashPaths = @(
        Get-ChildItem -Path $repoRoot -Recurse -File -Include '*.csproj', '*.props', '*.targets' |
            Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
            ForEach-Object {
                $file = $_
                $document = [System.Xml.Linq.XDocument]::Load($file.FullName, [System.Xml.Linq.LoadOptions]::SetLineInfo)
                $relative = $file.FullName.Substring($repoRoot.Length + 1)

                $nodes = @()
                $nodes += @($document.Descendants() | Where-Object { $_.Name.LocalName -eq 'PackagePath' })
                $nodes += @($document.Descendants().Attributes() | Where-Object { $_.Name.LocalName -eq 'PackagePath' })

                foreach ($node in $nodes) {
                    if ($node.Value -like '*\*') {
                        "${relative}:$(([System.Xml.IXmlLineInfo]$node).LineNumber) -> PackagePath=$($node.Value)"
                    }
                }
            }
    )

    Write-Host ''
    Write-Host 'PackagePath declarations'
    Assert-True ($backslashPaths.Count -eq 0) `
        'no PackagePath contains a backslash' `
        "offenders: $($backslashPaths -join ', ')"

    # --- Guidance payload, manifest, and nothing that runs in a consumer ---------------------------
    Write-Host ''
    Write-Host "$packageId ($(Split-Path -Leaf $pkg))"

    Assert-True ($entries -contains $guidancePath) "packs $guidancePath" "entries: $($entries -join ', ')"
    Assert-True ($entries -contains 'guidance/reference-manifest.json') 'packs guidance/reference-manifest.json'

    if ($manifest) {
        $document = @($manifest.documents)[0]
        $description = if ($document) { [string] $document.description } else { '' }
        Assert-True ($manifest.schemaVersion -eq 1 -and @($manifest.documents).Count -eq 1 -and
            $document.path -eq $guidancePath -and $document.sha256 -eq $hash -and $document.usage -eq 'onDemand' -and
            $description.Length -gt 0 -and $description.Length -le 200 -and $description -match '^Open when ' -and
            $null -eq $manifest.PSObject.Properties['entryPoints']) `
            'manifest matches the packed bytes, with onDemand usage and an "Open when ..." description' `
            "manifest: $($manifest | ConvertTo-Json -Compress -Depth 5)"
    }

    $runtimeEntries = @($entries | Where-Object { $_ -match '^(build|buildTransitive|trellis)/' })
    Assert-True ($runtimeEntries.Count -eq 0) `
        'packs no build/, buildTransitive/ or trellis/ entries (restore must never touch a consumer repository)' `
        "found: $($runtimeEntries -join ', ')"

    # XPath rather than property access: strict mode turns a missing element into an error.
    $helperDeps = @($nuspec.SelectNodes('//*[local-name()="dependency"]') |
        Where-Object { $_.GetAttribute('id') -match '^Trellis\.AgentDocs' })
    Assert-True ($helperDeps.Count -eq 0) `
        'does not depend on the packaging helper' `
        "found: $(($helperDeps | ForEach-Object { $_.GetAttribute('id') }) -join ', ')"

    # --- The published validator -------------------------------------------------------------------
    # Pinned to the packaging helper's version, which is published in lockstep with the tool.
    $project = [xml](Get-Content -LiteralPath (Join-Path $repoRoot 'src/Trellis.Testing.SqlProject/Trellis.Testing.SqlProject.csproj') -Raw)
    $toolVersion = $project.SelectSingleNode('//PackageReference[@Include="Trellis.AgentDocs.Packaging"]').GetAttribute('Version')
    $toolDirectory = Join-Path ([System.IO.Path]::GetTempPath()) "sqlproject-agentdocs-$([System.Guid]::NewGuid().ToString('N'))"
    try {
        $install = & dotnet tool install Trellis.AgentDocs --version $toolVersion --tool-path $toolDirectory 2>&1
        if ($LASTEXITCODE -ne 0) { throw "Could not install Trellis.AgentDocs $toolVersion for validation:`n$($install | Out-String)" }
        $validation = & (Join-Path $toolDirectory 'agentdocs') validate $pkg --strict 2>&1
        Assert-True ($LASTEXITCODE -eq 0) `
            "agentdocs validate --strict accepts the packed package ($toolVersion)" `
            ($validation | Out-String)
    }
    finally { Remove-Item -LiteralPath $toolDirectory -Recurse -Force -ErrorAction SilentlyContinue }

    # --- The README explains the opt-in ------------------------------------------------------------
    Write-Host ''
    Write-Host "$packageId (README opt-in)"
    Assert-True ($readmeText -match "(?m)^dotnet tool install Trellis\.AgentDocs --version $([regex]::Escape($toolVersion)) --tool-manifest \.config/dotnet-tools\.json\r?$" -and
        $readmeText.Contains('dotnet tool run agentdocs init <solution-or-project>') -and
        $readmeText.Contains('approvedPackages') -and $readmeText.Contains('dotnet tool run agentdocs sync') -and
        $readmeText.Contains($packageId)) `
        "README explains installing the tool, approving $packageId, and syncing"

    # --- Listing metadata --------------------------------------------------------------------------
    # A stale repository URL combined with the SourceLink commit SHA sends debuggers to a commit that
    # does not exist in the repository named, and passes every check that does not inspect the nuspec.
    function Get-MetaValue {
        param($Metadata, [string] $Name)
        $property = $Metadata.PSObject.Properties[$Name]
        if ($property) { return $property.Value }
        return $null
    }

    $meta = $nuspec.package.metadata
    Write-Host ''
    Write-Host "$packageId (listing metadata)"

    $icon = Get-MetaValue $meta 'icon'
    Assert-True ([bool]$icon -and ($entries -contains $icon)) `
        'packs the Trellis icon and declares it' `
        "nuspec <icon>='$icon'; matching entry present: $($entries -contains $icon)"

    $readme = Get-MetaValue $meta 'readme'
    Assert-True ([bool]$readme -and ($entries -contains $readme)) `
        'packs a listing README and declares it' `
        "nuspec <readme>='$readme'; matching entry present: $($entries -contains $readme)"

    $projectUrl = Get-MetaValue $meta 'projectUrl'
    Assert-True ($projectUrl -eq $expectedRepo) `
        'points projectUrl at this repository' `
        "projectUrl='$projectUrl', expected '$expectedRepo'"

    $repository = Get-MetaValue $meta 'repository'
    $repoUrl = if ($repository) { $repository.url } else { $null }
    Assert-True ($repoUrl -eq "$expectedRepo.git") `
        'points repository url at this repository' `
        "repository url='$repoUrl', expected '$expectedRepo.git'"

    # Symbol packages are deliberately not shipped; DotNet.ReproducibleBuilds can turn them on, so
    # assert rather than assume. The legacy .symbols.nupkg ends in .nupkg and would be swept up by the
    # publish workflow's push glob.
    $symbolPackages = @(
        Get-ChildItem -Path $outDir -File |
            Where-Object { $_.Name -like '*.snupkg' -or $_.Name -like '*.symbols.nupkg' } |
            ForEach-Object { $_.Name }
    )
    Assert-True ($symbolPackages.Count -eq 0) `
        'produces no symbol packages' `
        "found: $($symbolPackages -join ', ')"

    Write-Host ''
    if ($failures.Count -gt 0) {
        Write-Host "FAILED - $($failures.Count) check(s) did not pass." -ForegroundColor Red
        exit 1
    }

    Write-Host 'All API reference packaging checks passed.' -ForegroundColor Green
    exit 0
}
finally {
    if ($packedHere -and (Test-Path $outDir)) { Remove-Item $outDir -Recurse -Force -ErrorAction SilentlyContinue }
}
