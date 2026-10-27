param(
    [string]$Configuration = 'Release',
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$cliPath = Join-Path $repoRoot "src/ResourceCycle.Cli/bin/$Configuration/net10.0/DramaBoard.ResourceCycle.Cli.dll"
if (-not (Test-Path -LiteralPath $cliPath)) { throw "Build the solution before running this smoke: $cliPath" }
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repoRoot ('artifacts/resource-cycle-smoke/' + [Guid]::NewGuid().ToString('N'))
}
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null

function Run-Cycle([string]$Name, [string[]]$Arguments) {
    $logPath = Join-Path $outputRoot "$Name.log"
    & dotnet $cliPath @Arguments *> $logPath
    if ($LASTEXITCODE -ne 0) { throw "ResourceCycle $Name failed; inspect $logPath" }
    return $logPath
}

$continuousPath = Join-Path $outputRoot 'continuous.md'
$fullPath = Join-Path $outputRoot 'full.md'
$resumedPath = Join-Path $outputRoot 'resumed.md'
$reopenedPath = Join-Path $outputRoot 'reopened.md'
$storePath = Join-Path $outputRoot 'store'
$continuousLog = Run-Cycle 'continuous' @('--until-hour', '88', '--snapshot', $continuousPath)
$fullLog = Run-Cycle 'full' @('--until-hour', '88', '--full-recompute', '--snapshot', $fullPath)
$firstLog = Run-Cycle 'first' @('--store', $storePath, '--until-hour', '88', '--steps', '5')
$resumeLog = Run-Cycle 'resume' @('--store', $storePath, '--until-hour', '88', '--snapshot', $resumedPath)
$reopenLog = Run-Cycle 'reopen' @('--store', $storePath, '--until-hour', '88', '--snapshot', $reopenedPath)

$expectedHash = (Get-FileHash -LiteralPath $continuousPath -Algorithm SHA256).Hash
foreach ($path in @($fullPath, $resumedPath, $reopenedPath)) {
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $expectedHash) {
        throw "Final snapshots differ: $continuousPath and $path"
    }
}

function Read-Events([string]$Path) {
    return @(Get-Content -LiteralPath $Path | Where-Object { $_ -match '^D\d+ .* \| ' })
}
$continuousEvents = (Read-Events $continuousLog) -join "`n"
$fullEvents = (Read-Events $fullLog) -join "`n"
$resumedEvents = @((Read-Events $firstLog) + (Read-Events $resumeLog)) -join "`n"
if (-not $continuousEvents -or $continuousEvents -ne $fullEvents -or $continuousEvents -ne $resumedEvents) {
    throw 'Event traces differ between continuous, full and resumed runs.'
}
if ((Read-Events $reopenLog).Count -ne 0) { throw 'Reopening a completed run repeated an event.' }

@{
    snapshotSha256 = $expectedHash
    eventCount = (Read-Events $continuousLog).Count
    continuousFullAndResumedEqual = $true
    reopenedWithoutRepeatedEvents = $true
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $outputRoot 'acceptance.json') -Encoding utf8
Write-Output "ResourceCycle smoke passed: $outputRoot"
