#requires -Version 7.0
[CmdletBinding()]
param(
    [string] $DurableGraphSource = (Join-Path $PSScriptRoot '../../durable-graph'),
    [string] $AteliaSource = (Join-Path $PSScriptRoot '../../atelia'),
    [string] $CheckoutRoot = (Join-Path $PSScriptRoot '../artifacts/durablegraph-integration/fixed'),
    [string] $Feed = (Join-Path $PSScriptRoot '../artifacts/durablegraph-integration/feed'),
    [ValidatePattern('^0\.0\.0-dramaboard\.[0-9]{8}\.f68388f\.[1-9][0-9]*$')]
    [string] $PackageVersion = '0.0.0-dramaboard.20260912.f68388f.1'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$durableGraphRevision = 'f68388f88ba09354e9fa90420dc2cf22b146b6cf'
$ateliaRevision = '742fcd62e691b6b6acca4113a3ac3638bc7275ba'
$CheckoutRoot = [IO.Path]::GetFullPath($CheckoutRoot)
$Feed = [IO.Path]::GetFullPath($Feed)

function Invoke-Git {
    param([string[]] $Arguments)
    $output = & git @Arguments
    if ($LASTEXITCODE -ne 0) { throw "git $($Arguments -join ' ') failed ($LASTEXITCODE)." }
    return $output
}

function Assert-Checkout {
    param([string] $Path, [string] $Revision)
    $actualRoot = [IO.Path]::GetFullPath((Invoke-Git @('-C', $Path, 'rev-parse', '--show-toplevel')))
    if ($actualRoot -ne $Path) { throw "Expected a repository root at $Path; found $actualRoot." }
    $actualRevision = Invoke-Git @('-C', $Path, 'rev-parse', 'HEAD')
    if ($actualRevision -ne $Revision) { throw "Wrong HEAD at ${Path}: $actualRevision; expected $Revision. No checkout/reset performed." }
    $dirty = @(Invoke-Git @('-C', $Path, 'status', '--porcelain', '--untracked-files=no'))
    if ($dirty.Count -ne 0) { throw "Tracked changes at $Path. Preserve them and use another -CheckoutRoot." }
    if (-not (Test-Path -LiteralPath (Join-Path $Path 'Directory.Build.props') -PathType Leaf)) {
        throw "Missing repository Directory.Build.props at $Path; ancestor build settings could leak in."
    }
}

function Prepare-Checkout {
    param([string] $Name, [string] $Source, [string] $Revision)
    $path = [IO.Path]::GetFullPath((Join-Path $CheckoutRoot $Name))
    $rootPrefix = $CheckoutRoot.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $path.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Checkout path escaped -CheckoutRoot: $path."
    }
    if (-not (Test-Path -LiteralPath $path)) {
        $Source = (Resolve-Path -LiteralPath $Source).Path
        $null = Invoke-Git @('-C', $Source, 'cat-file', '-e', "$Revision^{commit}")
        $null = New-Item -ItemType Directory -Path $CheckoutRoot -Force
        Invoke-Git @('-C', $Source, 'worktree', 'add', '--detach', $path, $Revision) | Out-Host
    }
    Assert-Checkout $path $Revision
    return $path
}

$durableGraphRoot = Prepare-Checkout 'durable-graph' $DurableGraphSource $durableGraphRevision
$ateliaRoot = Prepare-Checkout 'atelia' $AteliaSource $ateliaRevision

# Same package closure and order as the pinned Run-EventHistoryRecoveryProbe.ps1.
# DG's relative ProjectReferences require these two checkouts to be siblings.
$projects = @(
    @{ Root = $ateliaRoot; Name = 'Data' },
    @{ Root = $ateliaRoot; Name = 'Primitives' },
    @{ Root = $ateliaRoot; Name = 'Rbf' },
    @{ Root = $ateliaRoot; Name = 'RbfSegmentStore' },
    @{ Root = $ateliaRoot; Name = 'EventJournal' },
    @{ Root = $durableGraphRoot; Name = 'DurableGraph.StateStore.Serialization' },
    @{ Root = $durableGraphRoot; Name = 'DurableGraph' },
    @{ Root = $durableGraphRoot; Name = 'DurableGraph.StateStore.Storage' },
    @{ Root = $durableGraphRoot; Name = 'DurableGraph.StateStore' }
)
$packageFiles = @($projects | ForEach-Object { "Atelia.$($_.Name).$PackageVersion.nupkg" })
$manifestPath = Join-Path $Feed "source-$PackageVersion.json"

if (Test-Path -LiteralPath $manifestPath) {
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.version -ne $PackageVersion -or $manifest.durableGraphRevision -ne $durableGraphRevision -or
        $manifest.ateliaRevision -ne $ateliaRevision -or @($manifest.packages).Count -ne $packageFiles.Count) {
        throw "Package source manifest does not match the frozen inputs: $manifestPath. Use a fresh -PackageVersion."
    }
    foreach ($file in $packageFiles) {
        $entry = @($manifest.packages | Where-Object { $_.file -ceq $file })
        $path = Join-Path $Feed $file
        if ($entry.Count -ne 1 -or -not (Test-Path -LiteralPath $path) -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -cne $entry[0].sha256) {
            throw "Missing or changed package $file. Never overwrite a published version; use a fresh -PackageVersion."
        }
    }
    Write-Host "Verified and reused nine frozen packages: $Feed ($PackageVersion)."
    return
}

# A partial earlier pack is also immutable: do not silently replace packages which
# NuGet may already have cached. A new attempt uses a fresh version across all nine.
foreach ($file in $packageFiles) {
    if (Test-Path -LiteralPath (Join-Path $Feed $file)) {
        throw "Package already exists without a complete source manifest: $file. Use a fresh -PackageVersion."
    }
}
$packageCache = if ([string]::IsNullOrWhiteSpace($env:NUGET_PACKAGES)) {
    Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget/packages'
} else { $env:NUGET_PACKAGES }
foreach ($project in $projects) {
    $cachedVersion = Join-Path $packageCache "atelia.$($project.Name.ToLowerInvariant())/$PackageVersion"
    if (Test-Path -LiteralPath $cachedVersion) {
        throw "Version already exists in NuGet cache: $cachedVersion. Use a fresh -PackageVersion."
    }
}

$null = New-Item -ItemType Directory -Path $Feed -Force
$logRoot = Join-Path $Feed "logs/$PackageVersion"
$null = New-Item -ItemType Directory -Path $logRoot -Force
foreach ($project in $projects) {
    $projectPath = Join-Path $project.Root "src/$($project.Name)/$($project.Name).csproj"
    $logPath = Join-Path $logRoot "$($project.Name).log"
    Write-Host "Packing $($project.Name) $PackageVersion; log: $logPath"
    Push-Location $project.Root
    try {
        & dotnet pack $projectPath --configuration Release --output $Feed "-p:PackageVersion=$PackageVersion" -m:1 *> $logPath
        if ($LASTEXITCODE -ne 0) {
            Get-Content -LiteralPath $logPath -Tail 30 | Out-Host
            throw "Pack failed for $($project.Name) ($LASTEXITCODE); see $logPath. Use a fresh version for the next attempt."
        }
    } finally { Pop-Location }
}
Assert-Checkout $durableGraphRoot $durableGraphRevision
Assert-Checkout $ateliaRoot $ateliaRevision
$packages = @($packageFiles | ForEach-Object {
    $path = Join-Path $Feed $_
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Expected package was not produced: $path." }
    @{ file = $_; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
})
@{
    version = $PackageVersion
    durableGraphRevision = $durableGraphRevision
    ateliaRevision = $ateliaRevision
    packages = $packages
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifestPath -Encoding utf8
Write-Host "Prepared nine frozen packages: $Feed ($PackageVersion). Source manifest: $manifestPath"
