#requires -Version 7.0
# Historical fixed-package reproduction only. Current development builds sibling source directly.
[CmdletBinding()]
param(
    [string] $DurableGraphSource = (Join-Path $PSScriptRoot '../../durable-graph'),
    [string] $StorageSource = (Join-Path $PSScriptRoot '../../atelia-storage'),
    [string] $CheckoutRoot = (Join-Path $PSScriptRoot '../artifacts/durablegraph-integration/fixed-c549124'),
    [string] $CandidateDirectory = (Join-Path $PSScriptRoot '../artifacts/durablegraph-integration/core-candidate'),
    [ValidatePattern('^0\.0\.0-dramaboard\.[0-9]{8}\.gc549124\.[1-9][0-9]*$')]
    [string] $PackageVersion = '0.0.0-dramaboard.20261027.gc549124.1'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$durableGraphRevision = 'c549124f1db53fe028f4640ddcafd8f338bb076b'
$storageRevision = '70b406cd8d1117733eee81a33e7f978399aa98b2'
$storageVersion = '0.1.3-dev.20261019.g70b406c'
$requiredSdk = '10.0.201'
$CheckoutRoot = [IO.Path]::GetFullPath($CheckoutRoot)
$CandidateDirectory = [IO.Path]::GetFullPath($CandidateDirectory)
$feed = Join-Path $CandidateDirectory 'feed'
$receiptPath = Join-Path $CandidateDirectory 'source.json'
$expectedPackages = [ordered]@{}
foreach ($name in @('Primitives', 'Data', 'Rbf', 'Binary', 'FrameStore', 'HeadStore')) {
    $expectedPackages.Add("Atelia.$name", $storageVersion)
}
foreach ($name in @('DurableGraph.Serialization', 'DurableGraph', 'DurableGraph.Storage', 'DurableGraph.Persistence')) {
    $expectedPackages.Add("Atelia.$name", $PackageVersion)
}
$expectedPackages.Add('K4os.Compression.LZ4', '1.3.8')

function Invoke-Git {
    param([string[]] $Arguments)
    $output = & git @Arguments
    if ($LASTEXITCODE -ne 0) { throw "git $($Arguments -join ' ') failed ($LASTEXITCODE)." }
    return $output
}

function Assert-Checkout {
    param([string] $Path, [string] $Revision, [string] $Repository)
    $actualRoot = [IO.Path]::GetFullPath((Invoke-Git @('-C', $Path, 'rev-parse', '--show-toplevel')))
    if ($actualRoot -ne $Path) { throw "Expected repository root $Path; found $actualRoot." }
    if ((Invoke-Git @('-C', $Path, 'rev-parse', 'HEAD')) -cne $Revision) {
        throw "Wrong HEAD at $Path. No checkout/reset performed; use another -CheckoutRoot."
    }
    $origin = (Invoke-Git @('-C', $Path, 'remote', 'get-url', 'origin')) -replace '\.git$', ''
    if ($origin -cne $Repository) { throw "Unexpected origin at ${Path}: $origin." }
    if (@(Invoke-Git @('-C', $Path, 'status', '--porcelain', '--untracked-files=normal')).Count -ne 0) {
        throw "Dirty checkout at $Path. Preserve it and use another -CheckoutRoot."
    }
    if (!(Test-Path -LiteralPath (Join-Path $Path 'Directory.Build.props') -PathType Leaf)) {
        throw "Missing build boundary at $Path."
    }
}

function Prepare-Checkout {
    param([string] $Name, [string] $Source, [string] $Revision, [string] $Repository)
    $path = [IO.Path]::GetFullPath((Join-Path $CheckoutRoot $Name))
    if (!(Test-Path -LiteralPath $path)) {
        $Source = (Resolve-Path -LiteralPath $Source).Path
        $null = Invoke-Git @('-C', $Source, 'cat-file', '-e', "$Revision^{commit}")
        $null = New-Item -ItemType Directory -Path $CheckoutRoot -Force
        Invoke-Git @('-C', $Source, 'worktree', 'add', '--detach', $path, $Revision) | Out-Host
    }
    Assert-Checkout $path $Revision $Repository
    return $path
}

function Read-CandidatePackages {
    $files = @(Get-ChildItem -LiteralPath $feed -Filter '*.nupkg' -File)
    if ($files.Count -ne $expectedPackages.Count) { throw 'Expected exactly eleven candidate packages.' }
    foreach ($id in $expectedPackages.Keys) {
        $version = $expectedPackages[$id]
        $filename = "$id.$version.nupkg"
        $path = Join-Path $feed $filename
        $archive = [IO.Compression.ZipFile]::OpenRead($path)
        try {
            $entries = @($archive.Entries | Where-Object { $_.FullName.EndsWith('.nuspec', [StringComparison]::OrdinalIgnoreCase) })
            if ($entries.Count -ne 1) { throw "Expected one nuspec: $filename." }
            $reader = [IO.StreamReader]::new($entries[0].Open())
            try { $nuspec = [xml]$reader.ReadToEnd() } finally { $reader.Dispose() }
            $metadata = $nuspec.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]')
            if ($metadata.SelectSingleNode('*[local-name()="id"]').InnerText -cne $id -or
                $metadata.SelectSingleNode('*[local-name()="version"]').InnerText -cne $version) {
                throw "Unexpected package identity: $filename."
            }
            if ($id.StartsWith('Atelia.', [StringComparison]::Ordinal)) {
                $isGraph = $id.StartsWith('Atelia.DurableGraph', [StringComparison]::Ordinal)
                $revision = if ($isGraph) { $durableGraphRevision } else { $storageRevision }
                $repository = $metadata.SelectSingleNode('*[local-name()="repository"]')
                if (!$repository -or $repository.GetAttribute('commit') -ine $revision) {
                    throw "Unexpected package source commit: $filename."
                }
            } elseif (!$archive.GetEntry('.signature.p7s')) {
                throw 'Expected the original signed K4os package.'
            }
        } finally { $archive.Dispose() }
        [pscustomobject]@{ id = $id; version = $version; file = $filename; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
    }
}

function Assert-Receipt {
    $receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
    if ($receipt.schemaVersion -ne 1 -or $receipt.version -cne $PackageVersion -or
        $receipt.durableGraphRevision -cne $durableGraphRevision -or $receipt.storageRevision -cne $storageRevision -or
        $receipt.storageVersion -cne $storageVersion -or $receipt.sdkVersion -cne $requiredSdk -or
        @($receipt.packages).Count -ne $expectedPackages.Count) {
        throw 'Candidate receipt does not match the pinned inputs. Use a fresh version and directory.'
    }
    foreach ($package in @(Read-CandidatePackages)) {
        $recorded = @($receipt.packages | Where-Object { $_.id -ceq $package.id })
        if ($recorded.Count -ne 1 -or $recorded[0].version -cne $package.version -or
            $recorded[0].file -cne $package.file -or $recorded[0].sha256 -cne $package.sha256) {
            throw "Missing or changed package $($package.id). Never overwrite existing versions."
        }
    }
    $hashFile = Join-Path $CandidateDirectory 'packages.sha256'
    if ((Get-FileHash -LiteralPath $hashFile -Algorithm SHA256).Hash.ToLowerInvariant() -cne $receipt.packageListSha256) {
        throw 'The upstream package hash list changed.'
    }
}

# Completed immutable delivery is reusable without touching producer checkouts or caches.
if (Test-Path -LiteralPath $receiptPath -PathType Leaf) {
    Assert-Receipt
    Write-Host "Verified and reused eleven pinned packages: $feed ($PackageVersion)."
    return
}
if (Test-Path -LiteralPath $CandidateDirectory) {
    throw 'Candidate directory exists without a complete receipt. Preserve it; choose a fresh version and directory.'
}
# A new checkout/output path cannot make an already consumed G reusable for packing.
# Directory.Build.props fixes this consumer cache independently of NUGET_PACKAGES.
$consumerCache = Join-Path $PSScriptRoot '../artifacts/nuget-packages'
foreach ($id in $expectedPackages.Keys | Where-Object { $_.StartsWith('Atelia.DurableGraph', [StringComparison]::Ordinal) }) {
    $cachedVersion = Join-Path $consumerCache "$($id.ToLowerInvariant())/$PackageVersion"
    if (Test-Path -LiteralPath $cachedVersion) {
        throw "Version already exists in the consumer cache: $cachedVersion. Choose a fresh G; cached bytes are never replaced."
    }
}
$durableGraphRoot = Prepare-Checkout 'durable-graph' $DurableGraphSource $durableGraphRevision 'https://github.com/Atelia-org/durable-graph'
$storageRoot = Prepare-Checkout 'atelia-storage' $StorageSource $storageRevision 'https://github.com/Atelia-org/atelia-storage'
$pin = [xml](Get-Content -LiteralPath (Join-Path $durableGraphRoot 'eng/StorageDependency.props') -Raw)
if ($pin.Project.PropertyGroup.StoragePackageVersion.InnerText -cne $storageVersion -or
    $pin.Project.PropertyGroup.StorageSourceRevision -cne $storageRevision) { throw 'Upstream storage pin changed.' }
$attemptPath = Join-Path $CheckoutRoot "attempt-$PackageVersion.json"
if (Test-Path -LiteralPath $attemptPath) {
    throw 'This G was previously attempted without a completed receipt. Choose a fresh version and directory.'
}
$logRoot = Join-Path $CheckoutRoot "logs/$PackageVersion"
$null = New-Item -ItemType Directory -Path $logRoot -Force
$storageFeed = Join-Path $CheckoutRoot "storage-feed/$storageVersion"
# The producer worktrees inherit configuration from this directory. Clear the consumer
# feed mapping: its candidate does not exist yet, and storage must obtain K4os publicly.
# This file is outside both upstream worktrees, preserving their clean-source checks.
@'
<configuration>
  <packageSources><clear /><add key="nuget.org" value="https://api.nuget.org/v3/index.json" /></packageSources>
  <packageSourceMapping><clear /><packageSource key="nuget.org"><package pattern="*" /></packageSource></packageSourceMapping>
  <fallbackPackageFolders><clear /></fallbackPackageFolders>
</configuration>
'@ | Set-Content -LiteralPath (Join-Path $CheckoutRoot 'NuGet.Config') -Encoding utf8NoBOM
$previousCache = $env:NUGET_PACKAGES
try {
    $env:NUGET_PACKAGES = Join-Path $CheckoutRoot 'producer-packages'
    Push-Location $storageRoot
    try {
        $sdk = (& dotnet --version | Out-String).Trim()
        if ($LASTEXITCODE -ne 0 -or $sdk -cne $requiredSdk) { throw "Exact SDK $requiredSdk is required." }
        Write-Host "Preparing storage $storageVersion; log: $logRoot/storage.log"
        & (Join-Path $storageRoot 'eng/Pack.ps1') -Version $storageVersion -OutputDirectory $storageFeed *> (Join-Path $logRoot 'storage.log')
    } finally { Pop-Location }
    @{ version = $PackageVersion; durableGraphRevision = $durableGraphRevision } | ConvertTo-Json |
        Set-Content -LiteralPath $attemptPath -Encoding utf8NoBOM
    Write-Host "Preparing DG $PackageVersion; log: $logRoot/durablegraph.log"
    # Candidate/ProbeSupport use absolute project paths and retain the caller's cwd.
    # Select DramaBoard's exact SDK instead of DG's latestFeature roll-forward.
    Push-Location (Join-Path $PSScriptRoot '..')
    try {
        $sdk = (& dotnet --version | Out-String).Trim()
        if ($LASTEXITCODE -ne 0 -or $sdk -cne $requiredSdk) { throw "DG packing requires exact SDK $requiredSdk." }
        & (Join-Path $durableGraphRoot 'eng/Prepare-DurableGraphCandidate.ps1') -Version $PackageVersion `
            -OutputDirectory $CandidateDirectory -StoragePackageSource $storageFeed *> (Join-Path $logRoot 'durablegraph.log')
    } finally { Pop-Location }
} finally { $env:NUGET_PACKAGES = $previousCache }
Assert-Checkout $durableGraphRoot $durableGraphRevision 'https://github.com/Atelia-org/durable-graph'
Assert-Checkout $storageRoot $storageRevision 'https://github.com/Atelia-org/atelia-storage'
$packages = @(Read-CandidatePackages)
@{
    schemaVersion = 1; version = $PackageVersion; durableGraphRevision = $durableGraphRevision
    storageVersion = $storageVersion; storageRevision = $storageRevision; sdkVersion = $requiredSdk
    packages = $packages
    packageListSha256 = (Get-FileHash -LiteralPath (Join-Path $CandidateDirectory 'packages.sha256') -Algorithm SHA256).Hash.ToLowerInvariant()
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $receiptPath -Encoding utf8NoBOM
Assert-Receipt
Write-Host "Prepared eleven pinned packages: $feed ($PackageVersion). Receipt: $receiptPath"
