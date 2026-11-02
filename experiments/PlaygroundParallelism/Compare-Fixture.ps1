param(
    [Parameter(Mandatory = $true)][string]$Output,
    [string]$Configuration = 'Release',
    [int]$Repetitions = 5,
    [int]$DelayMilliseconds = 250
)
$ErrorActionPreference = 'Stop'
if ($Repetitions -lt 1) { throw 'Repetitions must be positive.' }
$experimentRoot = [IO.Path]::GetFullPath($Output)
if (Test-Path -LiteralPath $experimentRoot) { throw 'Use a new experiment directory.' }
New-Item -ItemType Directory -Path $experimentRoot | Out-Null
$project = Join-Path $PSScriptRoot 'PlaygroundParallelism.csproj'
$orders = [System.Collections.Generic.List[object]]::new()
$orders.Add([string[]]@('serial', 'candidate'))
for ($plannedPair = 0; $plannedPair -lt $Repetitions; $plannedPair++) {
    if ($plannedPair % 2 -eq 0) { $orders.Add([string[]]@('serial', 'candidate')) }
    else { $orders.Add([string[]]@('candidate', 'serial')) }
}
$plan = @{ Version = 1; Repetitions = $Repetitions; DelayMilliseconds = $DelayMilliseconds; Strategies = @('serial', 'candidate'); IncludesWarmup = $true; OrdersIncludingWarmup = $orders }
$plan | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $experimentRoot 'comparison-manifest.json') -Encoding utf8
$samples = @()
for ($pair = -1; $pair -lt $Repetitions; $pair++) {
    $prefix = if ($pair -eq -1) { 'warmup' } else { 'pair-' + $pair }
    foreach ($strategy in $orders[$pair + 1]) {
        $runOutput = Join-Path $experimentRoot ($prefix + '-' + $strategy)
        & dotnet run --no-build --project $project -c $Configuration -- --mode fixture --strategy $strategy --output $runOutput --delay-ms $DelayMilliseconds
        if ($LASTEXITCODE -ne 0) { throw "Fixture $prefix/$strategy failed; preserve its artifacts." }
    }
    $serialPath = Join-Path $experimentRoot ($prefix + '-serial')
    $candidatePath = Join-Path $experimentRoot ($prefix + '-candidate')
    $serialHash = (Get-FileHash -LiteralPath (Join-Path $serialPath 'semantic-trace.json') -Algorithm SHA256).Hash
    $candidateHash = (Get-FileHash -LiteralPath (Join-Path $candidatePath 'semantic-trace.json') -Algorithm SHA256).Hash
    if ($serialHash -ne $candidateHash) { throw "Full semantic trace differs for $prefix; inspect actual inputs, facts, and final materials." }
    $serial = Get-Content -LiteralPath (Join-Path $serialPath 'summary.json') -Raw | ConvertFrom-Json
    $candidate = Get-Content -LiteralPath (Join-Path $candidatePath 'summary.json') -Raw | ConvertFrom-Json
    if ($serial.Calls -ne 2 -or $candidate.Calls -ne 2 -or $candidate.Adopted -ne 2 -or $candidate.PeakInFlight -ne 2) {
        throw "Unexpected provider/adoption timeline for $prefix."
    }
    if ($pair -ge 0) {
        $samples += @{ Pair = $pair; SemanticTraceSha256 = $serialHash; Serial = $serial; Candidate = $candidate }
    }
}
@{ Outcome = 'passed'; Samples = $samples; Scope = 'Fixed input-keyed fixture; independent latency; unmodified Playground rules; no online evidence.' } |
    ConvertTo-Json -Depth 16 | Set-Content -LiteralPath (Join-Path $experimentRoot 'comparison.json') -Encoding utf8
