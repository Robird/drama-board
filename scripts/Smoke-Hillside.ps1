param([string]$Configuration = 'Release', [string]$OutputDirectory,
    [ValidateSet('Economy', 'Trade')][string]$Scenario = 'Economy')

# Scripted engineering acceptance, not free play or real LLM evidence.
# Uses ordinary Console commands and two processes against one fresh save.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'HostJournalHelpers.ps1')
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
$homePath = Join-Path $outputRoot 'home'
New-Item -ItemType Directory -Path $homePath | Out-Null
[IO.File]::WriteAllText((Join-Path $homePath 'playground.json'), '{"version":1,"characterControls":"controls.json"}', $utf8)
Copy-Item (Join-Path $repoRoot 'config/llm-connections.example.json') (Join-Path $homePath 'llm-connections.json')
$overrides = @{ a = @{ kind = 'human-console' } }
if ($Scenario -eq 'Trade') { $overrides.b = @{ kind = 'human-console' } }
[IO.File]::WriteAllText((Join-Path $homePath 'controls.json'), (@{ version = 1; overrides = $overrides } | ConvertTo-Json -Depth 5), $utf8)

function Invoke-Console([string]$Name, [string[]]$Commands, [bool]$NewWorld) {
    $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
    foreach ($argument in @($cliPath, '--home', $homePath, '--store', $storePath, '--steps', '1000', '--until-hour', '32')) {
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
    'go e-r6', 'produce ration 1', 'use item.ration',
    'go e-a9', 'sell item.ration 1', 'buy item.food 2', 'buy item.water 1',
    'use item.water', 'go e-d8', 'go e-w2', 'go e-c7',
    'gather berries 2', 'gather water 1', 'use item.water', 'use item.food',
    'go e-n4', 'go e-g6', 'go e-p5', 'go e-k7'
)
$second = @(
    'go e-m2', 'buy item.food 1', 'use item.food', 'use item.water',
    'go e-d8', 'go e-w2', 'go e-c7', 'gather berries 2',
    'use item.food', 'gather water 1', 'use item.water',
    'go e-n4', 'go e-g6', 'go e-p5', 'go e-v4',
    'produce ration 1', 'use item.ration', 'go e-a9',
    'sell item.ration 1', 'go e-k7'
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
foreach ($record in Read-HostJournalRecords (Join-Path $storePath 'host/events.jsonl')) {
    if ($record.Kind -ceq 'operation-received' -and -not $record.Payload.ToolName.StartsWith(':', [StringComparison]::Ordinal)) {
        $received[$record.Payload.OperationRecordId] = $record.Payload
    }
    if ($record.Kind -ceq 'operation-completed') { $completed[$record.Payload.OperationRecordId] = $record.Payload.Completed }
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
    HostJournalSha256 = (Get-FileHash -LiteralPath (Join-Path $storePath 'host/events.jsonl') -Algorithm SHA256).Hash
}
Assert-NoLegacyHostFiles $storePath
if (Test-Path -LiteralPath (Join-Path $storePath 'llm-runs')) { throw 'Human-only smoke created raw model audits.' }
[IO.File]::WriteAllText((Join-Path $outputRoot 'result.json'), ($report | ConvertTo-Json), $utf8)
Write-Output "Hillside $Scenario Console smoke passed: $($received.Count) committed commands in two processes. Evidence: $outputRoot"
