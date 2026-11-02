param([Parameter(Mandatory)][string]$OutputDirectory, [string]$Configuration = 'Release')
# Scripted CLI acceptance. No model calls or builds.
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$cli = Join-Path $repo "src/Playground.Cli/bin/$Configuration/net10.0/DramaBoard.Playground.Cli.dll"
$root = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $root) { throw 'OutputDirectory must be new.' }
if (-not (Test-Path -LiteralPath $cli)) { throw "Build CLI first: $cli" }
New-Item -ItemType Directory -Path $root | Out-Null
$utf8 = [Text.UTF8Encoding]::new($false)
$runs = [Collections.Generic.List[object]]::new()
$report = [ordered]@{ Kind = 'Home CLI scripted acceptance, no model calls'; Status = 'running'; CliSha256 = (Get-FileHash $cli).Hash; Processes = $runs }
function Save-Report { [IO.File]::WriteAllText((Join-Path $root 'manifest.json'), ($report | ConvertTo-Json -Depth 20), $utf8) }
function Assert([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Invoke-Cli([string]$Name, [string]$Cwd, [string[]]$Arguments, [string[]]$Commands = @(), [int]$ExpectedExit = 0) {
    $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
    foreach ($arg in @($cli) + $Arguments) { $start.ArgumentList.Add($arg) }
    $start.WorkingDirectory = $Cwd
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardInputEncoding = $utf8
    $start.StandardOutputEncoding = $utf8
    $start.StandardErrorEncoding = $utf8
    $null = $start.Environment.Remove('DRAMABOARD_HOME_FORBIDDEN_KEY')
    $inputText = ($Commands -join "`n") + "`n"
    [IO.File]::WriteAllText((Join-Path $root "$Name.input.txt"), $inputText, $utf8)
    $entry = [ordered]@{ Name = $Name; Cwd = $Cwd; Arguments = $Arguments; Commands = $Commands }
    $runs.Add($entry)
    Save-Report
    $p = [Diagnostics.Process]::Start($start)
    try {
        $outTask = $p.StandardOutput.ReadToEndAsync()
        $errTask = $p.StandardError.ReadToEndAsync()
        $p.StandardInput.Write($inputText)
        $p.StandardInput.Close()
        $timeout = -not $p.WaitForExit(45000)
        if ($timeout) { $p.Kill($true); $p.WaitForExit() }
        $stdout = $outTask.GetAwaiter().GetResult()
        $stderr = $errTask.GetAwaiter().GetResult()
        [IO.File]::WriteAllText((Join-Path $root "$Name.stdout.txt"), $stdout, $utf8)
        [IO.File]::WriteAllText((Join-Path $root "$Name.stderr.txt"), $stderr, $utf8)
        $entry.ExitCode = $p.ExitCode
        $entry.TimedOut = $timeout
        Assert (-not $timeout -and $p.ExitCode -eq $ExpectedExit) "$Name failed; inspect preserved stdout/stderr."
        if ($ExpectedExit -eq 0) { Assert ([string]::IsNullOrEmpty($stderr)) "$Name unexpected stderr." }
        return $stdout
    } finally { Save-Report; $p.Dispose() }
}
function Prepare-Home([string]$Path) {
    New-Item -ItemType Directory -Path $Path | Out-Null
    [IO.File]::WriteAllText((Join-Path $Path 'playground.json'), '{"version":1,"characterControls":"controls.json"}', $utf8)
    [IO.File]::WriteAllText((Join-Path $Path 'controls.json'), '{"version":1,"overrides":{"a":{"kind":"human-console"},"b":{"kind":"human-console"},"c":{"kind":"human-console"}}}', $utf8)
    $connections = @{ version = 1; defaultConnectionId = 'forbidden'; catalog = @{ v = 3; connections = @(@{ id = 'forbidden'; kind = 'openai-chat'; completionSurfaceId = 'openai-chat/strict'; modelId = 'forbidden'; baseAddress = 'https://example.invalid/'; apiKeyEnv = 'DRAMABOARD_HOME_FORBIDDEN_KEY' }) } }
    [IO.File]::WriteAllText((Join-Path $Path 'llm-connections.json'), ($connections | ConvertTo-Json -Depth 10), $utf8)
}
function Hash-Tree([string]$Path) {
    return (@(Get-ChildItem -LiteralPath $Path -Recurse -File | Sort-Object FullName | ForEach-Object { [IO.Path]::GetRelativePath($Path, $_.FullName) + ':' + (Get-FileHash -LiteralPath $_.FullName).Hash }) -join "`n")
}
try {
    $initHome = Join-Path $root 'init home'
    $null = Invoke-Cli '01-init' $repo @('init', '--home', $initHome)
    Assert (@(Get-ChildItem $initHome -Force).Count -eq 1) 'init created more than marker.'
    Assert ((Get-Content (Join-Path $initHome 'playground.json') -Raw | ConvertFrom-Json).version -eq 1) 'init version mismatch.'
    $markerHash = Hash-Tree $initHome
    $null = Invoke-Cli '02-init-refuse' $repo @('init', '--home', $initHome) @() 1
    Assert ($markerHash -ceq (Hash-Tree $initHome)) 'init overwrote marker.'
    $null = Invoke-Cli '02-run-needs-connections' $repo @('--home', $initHome) @() 1
    Assert ($markerHash -ceq (Hash-Tree $initHome)) 'Missing connection configuration left store or logs.'
    $bad = Join-Path $root 'bad home'
    New-Item -ItemType Directory $bad | Out-Null
    [IO.File]::WriteAllText((Join-Path $bad 'playground.json'), '{bad', $utf8)
    $null = Invoke-Cli '03-help' $bad @('--help')
    $playHome = Join-Path $root 'play home'
    Prepare-Home $playHome
    # Opening a Context is itself a committed transition; two actors need four steps.
    $null = Invoke-Cli '04-explicit-multihuman' $repo @('--home', $playHome, '--steps', '4') @('handoff first-private', 'handoff second-private')
    $store = Join-Path $playHome 'store'
    Assert (Test-Path $store) 'Human run did not save.'
    $null = Invoke-Cli '05-cwd-coldopen' $playHome @('--steps', '1') @(':quit')
    $operations = Get-Content (Join-Path $store 'operations.jsonl') -Raw
    Assert ($operations.Contains('first-private') -and $operations.Contains('second-private')) 'Missing actual multi-Human operations.'
    foreach ($log in Get-ChildItem (Join-Path $store 'llm-runs') -File) {
        foreach ($line in Get-Content $log.FullName) {
            $record = $line | ConvertFrom-Json
            Assert ($record.Kind -cnotin @('binding','model-input','model-output')) 'Human attempted model initialization.'
        }
    }
    foreach ($mode in @('human','llm')) {
        $fresh = Join-Path $root "zero $mode"
        Prepare-Home $fresh
        $arguments = @('--home', $fresh, '--until-hour', '0')
        if ($mode -eq 'llm') { $arguments += '--no-character-controls' }
        $null = Invoke-Cli "06-zero-$mode" $repo $arguments @() 1
        Assert (-not (Test-Path (Join-Path $fresh 'store')) -and -not (Test-Path (Join-Path $fresh 'llm-runs'))) 'Invalid budget left store/log.'
    }
    $before = Hash-Tree $store
    foreach ($location in @($bad, (Join-Path $root 'missing home'))) {
        $name = if ($location -eq $bad) { '07-bad-home-management' } else { '08-missing-home-management' }
        $jsonl = Invoke-Cli $name $repo @('--home', $location, '--store', $store, '--branches')
        $rows = @($jsonl -split '\r?\n' | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json })
        Assert ($rows.Count -gt 0) 'No machine-readable branches.'
    }
    Assert ($before -ceq (Hash-Tree $store)) 'Management changed source store.'
    $memory = Join-Path $root 'memory home'
    Prepare-Home $memory
    $null = Invoke-Cli '09-ephemeral' $repo @('--home', $memory, '--ephemeral', '--steps', '1') @('handoff memory-private')
    Assert (-not (Test-Path (Join-Path $memory 'store'))) 'Ephemeral created store.'
    Assert (@(Get-ChildItem (Join-Path $memory 'llm-runs') -File).Count -gt 0) 'Ephemeral audit missing from home.'
    $report.Status = 'passed'
    Write-Output "Home CLI smoke passed. Evidence: $root"
} catch { $report.Status = 'failed'; $report.Error = $_.Exception.Message; throw } finally { Save-Report }
