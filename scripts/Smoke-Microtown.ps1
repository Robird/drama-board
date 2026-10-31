param([string]$Configuration = 'Release', [string]$OutputDirectory)

# Deterministic Human Console engineering probe. This does not replace real LLM acceptance.
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$cli = Join-Path $repoRoot "src/Microtown.Cli/bin/$Configuration/net10.0/DramaBoard.Microtown.Cli.dll"
if (-not (Test-Path -LiteralPath $cli)) { throw "Build first: $cli" }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repoRoot ('artifacts/microtown-console/' + [Guid]::NewGuid().ToString('N')) }
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
if ((Test-Path -LiteralPath $outputRoot) -and (Get-ChildItem -LiteralPath $outputRoot -Force | Select-Object -First 1)) {
    throw 'Use a fresh evidence directory.'
}
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$utf8 = [Text.UTF8Encoding]::new($false)
$assertions = [Collections.Generic.List[string]]::new()
$store = Join-Path $outputRoot 'store'

function Assert-That([bool]$Condition, [string]$Name) {
    if (-not $Condition) { throw "Assertion failed: $Name" }
    $assertions.Add($Name)
}
function Run-Town([string]$Name, [string[]]$Options, [string[]]$Commands) {
    $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
    foreach ($argument in (@($cli, '--store', $store) + $Options)) { $start.ArgumentList.Add($argument) }
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardInputEncoding = $utf8
    $start.StandardOutputEncoding = $utf8
    $start.StandardErrorEncoding = $utf8
    $inputText = ($Commands -join "`n") + "`n"
    [IO.File]::WriteAllText((Join-Path $outputRoot "$Name.in.txt"), $inputText, $utf8)
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $process.StandardInput.Write($inputText)
        $process.StandardInput.Close()
        $timedOut = -not $process.WaitForExit(60000)
        if ($timedOut) { $process.Kill($true); $process.WaitForExit() }
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        [IO.File]::WriteAllText((Join-Path $outputRoot "$Name.out.txt"), $stdout, $utf8)
        [IO.File]::WriteAllText((Join-Path $outputRoot "$Name.err.txt"), $stderr, $utf8)
        if ($timedOut) { throw "$Name timed out; partial evidence preserved." }
        Assert-That ($process.ExitCode -eq 0 -and $stderr.Length -eq 0) "$Name exited cleanly"
        Assert-That (-not $stdout.Contains([char]0xfffd)) "$Name preserved UTF8"
        return $stdout
    } finally { $process.Dispose() }
}

$first = Run-Town '01-create-pause' @('--hours', '25', '--max-operations', '4') @(
    'buy grain 1000', 'buy grain 4', 'buy grain 4', 'buy grain 4'
)
Assert-That ($first.Contains('[Result: a] Rejected: External stock is insufficient.')) 'rejected operation was visible'
Assert-That ($first.Contains('action[a] (:look, :quit)>') -and $first.Contains('action[b] (:look, :quit)>') -and $first.Contains('action[c] (:look, :quit)>')) 'input prompt identifies the currently controlled resident'
Assert-That ($first.Contains('Paused: Operation budget reached.')) 'budget paused at a completed decision boundary'
Assert-That ($first.Contains('Cash=30; initial=54; import expense=24; export income=0.')) 'three resident purchases reconcile before reopening'

$resumed = Run-Town '02-cold-run' @() @(
    ':look', 'go market-workshop', 'go market-workshop', 'go market-workshop',
    'produce meal 1', 'produce meal 1', 'produce meal 1',
    'eat', 'eat', 'eat', 'wait 24', 'wait 24', 'wait 24'
)
Assert-That ($resumed.Contains('Bought 4 grain for 8 money.')) 'saved personal receipt reached the reopened decision'
Assert-That (-not $resumed.Contains('] Rejected:')) 'all resumed game operations accepted'
Assert-That ($resumed.Contains('Experiment endpoint reached.') -and $resumed.Contains('0..25 hours; ended=True')) 'finite endpoint reached after one full economic day'
Assert-That ($resumed.Contains('Market refreshes=1; next refresh at hour 48.')) 'external market refreshed at hour 24'
Assert-That ($resumed.Contains('Item meal: held=6; work-in-progress=0; imported=0; exported=0; produced=6; eaten=3; recipe-inputs=0.')) 'production and consumption reconcile'
foreach ($actor in @('a','b','c')) {
    Assert-That ($resumed.Contains("Resident ${actor}: food=25/100; minimum=25; failed=False; cash=10.")) "resident $actor survived with independent balances"
}

$cold = Run-Town '03-ended-cold-open' @() @()
Assert-That ($cold.Contains('0..25 hours; ended=True') -and -not $cold.Contains('action[')) 'ended save reopens without controller activation'
Assert-That ($cold.Contains('produced=6; eaten=3; recipe-inputs=0.')) 'cold open did not repeat production or consumption'

[IO.File]::WriteAllText((Join-Path $outputRoot 'acceptance.json'), ([ordered]@{
    Operator = 'Coding Agent script via production Human Console; no real model acceptance'
    Cli = $cli
    Assertions = $assertions.ToArray()
} | ConvertTo-Json -Depth 4), $utf8)
Write-Output "Microtown Console smoke passed. Evidence: $outputRoot"
