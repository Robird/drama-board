param([string]$Configuration = 'Release', [string]$OutputDirectory,
    [ValidateSet('Economy', 'Trade')][string]$Scenario = 'Economy')

# Scripted engineering acceptance, not free play or real LLM evidence.
# Uses ordinary Console commands and two processes against one fresh save.
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$cliPath = Join-Path $repoRoot "src/Playground.Cli/bin/$Configuration/net10.0/DramaBoard.Playground.Cli.dll"
if (-not (Test-Path -LiteralPath $cliPath)) { throw "Build the CLI first: $cliPath" }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repoRoot ('artifacts/hillside-console/' + [Guid]::NewGuid().ToString('N')) }
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
if ((Test-Path -LiteralPath $outputRoot) -and (Get-ChildItem -LiteralPath $outputRoot -Force | Select-Object -First 1)) {
    throw 'Use a fresh output directory; previous evidence is never overwritten.'
}
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$storePath = Join-Path $outputRoot 'world'
$utf8 = [Text.UTF8Encoding]::new($false)

function Invoke-Console([string]$Name, [string[]]$Commands, [bool]$NewWorld) {
    $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
    foreach ($argument in @($cliPath, '--store', $storePath, '--steps', '1000', '--until-hour', '32')) {
        $start.ArgumentList.Add($argument)
    }
    if ($NewWorld) {
        $start.ArgumentList.Add('--characters')
        $start.ArgumentList.Add($(if ($Scenario -eq 'Trade') { '2' } else { '1' }))
    }
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardInputEncoding = $utf8
    $start.StandardOutputEncoding = $utf8
    $start.StandardErrorEncoding = $utf8
    $inputText = ($Commands -join "`n") + "`n:look`n:quit`n"
    [IO.File]::WriteAllText((Join-Path $outputRoot "$Name.input.txt"), $inputText, $utf8)
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $process.StandardInput.Write($inputText)
        $process.StandardInput.Close()
        $timedOut = -not $process.WaitForExit(45000)
        if ($timedOut) { $process.Kill($true); $process.WaitForExit() }
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        [IO.File]::WriteAllText((Join-Path $outputRoot "$Name.output.txt"), $stdout, $utf8)
        [IO.File]::WriteAllText((Join-Path $outputRoot "$Name.error.txt"), $stderr, $utf8)
        if ($timedOut) { throw "$Name exceeded 45 seconds; partial evidence was preserved." }
        if ($process.ExitCode -ne 0 -or $stderr.Length -ne 0) { throw "$Name failed; inspect its output files." }
    } finally { $process.Dispose() }
}

$first = @(
    'go home-workshop', 'produce ration 1', 'use item.ration',
    'go market-workshop', 'sell item.ration 1', 'buy item.food 2', 'buy item.water 1',
    'use item.water', 'go market-road', 'go road-camp', 'go trail-1',
    'gather berries 2', 'gather water 1', 'use item.water', 'use item.food',
    'go trail-1', 'go road-camp', 'go market-road', 'go market-home'
)
$second = @(
    'go market-home', 'buy item.food 1', 'use item.food', 'use item.water',
    'go market-road', 'go road-camp', 'go trail-1', 'gather berries 2',
    'use item.food', 'gather water 1', 'use item.water',
    'go trail-1', 'go road-camp', 'go market-road', 'go market-workshop',
    'produce ration 1', 'use item.ration', 'go market-workshop',
    'sell item.ration 1', 'go market-home'
)
if ($Scenario -eq 'Trade') {
    $first = @('offer b item.water 1 for money 2 4')
    $second = @('accept trade:a:1:1', 'offer b item.food 1 for item.water 1 4',
        'reject trade:a:1:2', 'offer b money 1 for item.water 1 4', 'wait 1', 'withdraw trade:a:1:3')
    Invoke-Console '01-offer' $first $true
    Invoke-Console '02-cold-open-accept-reject-withdraw' $second $false
} else {
    Invoke-Console '01-production-and-first-trip' $first $true
    Invoke-Console '02-cold-open-and-second-trip' $second $false
}

$received = [ordered]@{}
$completed = @{}
foreach ($line in Get-Content -LiteralPath (Join-Path $storePath 'operations.jsonl')) {
    $record = ($line | ConvertFrom-Json).Payload | ConvertFrom-Json
    if ($null -ne $record.Received -and -not $record.Received.ToolName.StartsWith(':', [StringComparison]::Ordinal)) {
        $received[$record.Received.OperationRecordId] = $record.Received
    }
    if ($null -ne $record.Completed) { $completed[$record.OperationRecordId] = $record.Completed }
}
$actual = @($received.Values | ForEach-Object RawInput)
$expected = @($first) + @($second)
if (($actual -join "`n") -cne ($expected -join "`n")) { throw 'Not all planned ordinary commands were submitted in order.' }
foreach ($id in $received.Keys) {
    $result = $completed[$id]
    if ($null -eq $result -or $result.Status -ne 0 -or $result.ToolCallId -ne $received[$id].ToolCallId -or
        [string]::IsNullOrWhiteSpace($result.DomainEvent) -or [string]::IsNullOrWhiteSpace($result.WorldVersion)) {
        throw "Command did not commit successfully: $($received[$id].RawInput)"
    }
}
$report = [ordered]@{
    Kind = 'Scripted engineering Console smoke; no model calls or free play'
    Scenario = $Scenario
    Commands = $received.Count
    Processes = 2
    Store = $storePath
    CliSha256 = (Get-FileHash -LiteralPath $cliPath -Algorithm SHA256).Hash
    OperationsSha256 = (Get-FileHash -LiteralPath (Join-Path $storePath 'operations.jsonl') -Algorithm SHA256).Hash
}
[IO.File]::WriteAllText((Join-Path $outputRoot 'result.json'), ($report | ConvertTo-Json), $utf8)
Write-Output "Hillside $Scenario Console smoke passed: $($received.Count) committed commands in two processes. Evidence: $outputRoot"
