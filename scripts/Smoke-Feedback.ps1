param([Parameter(Mandatory)][string]$OutputDirectory, [string]$Configuration = 'Release')
# Scripted Human Console acceptance. No model calls or builds.
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$cli = Join-Path $repo "src/Playground.Cli/bin/$Configuration/net10.0/DramaBoard.Playground.Cli.dll"
$root = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $root) { throw 'OutputDirectory must be new.' }
if (-not (Test-Path -LiteralPath $cli)) { throw "Build CLI first: $cli" }
New-Item -ItemType Directory -Path $root | Out-Null
$utf8 = [Text.UTF8Encoding]::new($false)
$runs = [Collections.Generic.List[object]]::new()
$report = [ordered]@{ Kind = 'Independent feedback scripted acceptance; no model calls'; Status = 'running'; CliSha256 = (Get-FileHash $cli).Hash; Processes = $runs }
function Save-Report { [IO.File]::WriteAllText((Join-Path $root 'manifest.json'), ($report | ConvertTo-Json -Depth 20), $utf8) }
function Assert([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Invoke-Cli([string]$Name, [string[]]$Arguments, [string[]]$Commands = @(), [int]$ExpectedExit = 0) {
    $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
    foreach ($arg in @($cli) + $Arguments) { $start.ArgumentList.Add($arg) }
    $start.WorkingDirectory = $repo
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardInputEncoding = $utf8
    $start.StandardOutputEncoding = $utf8
    $start.StandardErrorEncoding = $utf8
    $null = $start.Environment.Remove('DRAMABOARD_FEEDBACK_FORBIDDEN_KEY')
    $inputText = ($Commands -join "`n") + "`n"
    [IO.File]::WriteAllText((Join-Path $root "$Name.input.txt"), $inputText, $utf8)
    $entry = [ordered]@{ Name = $Name; Arguments = $Arguments; Commands = $Commands }
    $runs.Add($entry)
    Save-Report
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $process.StandardInput.Write($inputText)
        $process.StandardInput.Close()
        $timeout = -not $process.WaitForExit(45000)
        if ($timeout) { $process.Kill($true); $process.WaitForExit() }
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        [IO.File]::WriteAllText((Join-Path $root "$Name.stdout.txt"), $stdout, $utf8)
        [IO.File]::WriteAllText((Join-Path $root "$Name.stderr.txt"), $stderr, $utf8)
        $entry.ExitCode = $process.ExitCode
        $entry.TimedOut = $timeout
        Assert (-not $timeout -and $process.ExitCode -eq $ExpectedExit) "$Name failed; inspect stdout/stderr."
        if ($ExpectedExit -eq 0) { Assert ([string]::IsNullOrEmpty($stderr)) "$Name unexpected stderr." }
        return $stdout
    } finally { Save-Report; $process.Dispose() }
}
function Hash-Tree([string]$Path) {
    return (@(Get-ChildItem -LiteralPath $Path -Recurse -File | Sort-Object FullName | ForEach-Object {
        [IO.Path]::GetRelativePath($Path, $_.FullName) + ':' + (Get-FileHash -LiteralPath $_.FullName).Hash
    }) -join "`n")
}
try {
    $playHome = Join-Path $root 'home'
    New-Item -ItemType Directory -Path $playHome | Out-Null
    [IO.File]::WriteAllText((Join-Path $playHome 'playground.json'), '{"version":1,"characterControls":"controls.json"}', $utf8)
    [IO.File]::WriteAllText((Join-Path $playHome 'controls.json'), '{"version":1,"overrides":{"a":{"kind":"human-console"}}}', $utf8)
    $connections = @{ version = 1; defaultConnectionId = 'forbidden'; catalog = @{ v = 3; connections = @(@{ id = 'forbidden'; kind = 'openai-chat'; completionSurfaceId = 'openai-chat/strict'; modelId = 'forbidden'; baseAddress = 'https://example.invalid/'; apiKeyEnv = 'DRAMABOARD_FEEDBACK_FORBIDDEN_KEY' }) } }
    [IO.File]::WriteAllText((Join-Path $playHome 'llm-connections.json'), ($connections | ConvertTo-Json -Depth 10), $utf8)
    $store = Join-Path $playHome 'store'
    $first = Invoke-Cli '01-human' @('--home', $playHome, '--characters', '1') @(':feedback 第一份反馈：想更容易找到回家的路。')
    Assert ($first.Contains('反馈已保存：')) 'Human save receipt missing.'
    Assert (-not (Test-Path (Join-Path $store 'gym'))) 'Feedback must not start Gym.'
    $feedbackFile = Join-Path $store 'feedback.jsonl'
    $firstRecord = Get-Content -LiteralPath $feedbackFile | ConvertFrom-Json
    Assert ($firstRecord.Source -eq 'HumanConsole' -and $null -eq $firstRecord.Model -and $null -ne $firstRecord.Boundary) 'Human metadata invalid.'
    $before = Hash-Tree $store
    $listed = Invoke-Cli '02-query' @('--store', $store, '--feedbacks', '--actor', 'a')
    Assert (($listed | ConvertFrom-Json).FeedbackId -eq $firstRecord.FeedbackId) 'Query did not find saved feedback.'
    Assert ((Hash-Tree $store) -eq $before) 'Feedback query changed store.'
    $null = Invoke-Cli '03-resume' @('--home', $playHome) @(':feedback 同一输入窗口的第二份反馈。')
    $records = @(Get-Content -LiteralPath $feedbackFile | ForEach-Object { $_ | ConvertFrom-Json })
    Assert ($records.Count -eq 2) 'Cold resume feedback missing.'
    Assert ($records[0].Boundary.StateCommitId -eq $records[1].Boundary.StateCommitId) 'Feedback advanced state.'
    Assert ($records[0].RunId -ne $records[1].RunId) 'Cold runs need distinct identities.'
    $beforeFork = (Get-FileHash -LiteralPath $feedbackFile).Hash
    $null = Invoke-Cli '04-fork' @('--store', $store, '--fork-feedback', $firstRecord.FeedbackId, '--fork-label', 'feedback-child')
    Assert (-not (Test-Path (Join-Path $store 'gym'))) 'Feedback fork created Gym.'
    Assert ((Get-FileHash -LiteralPath $feedbackFile).Hash -eq $beforeFork) 'Fork rewrote report.'
    $branches = @( ((Invoke-Cli '05-branches' @('--store', $store, '--branches')).Trim() -split "`n") | ForEach-Object { $_ | ConvertFrom-Json })
    $child = @($branches | Where-Object { $_.BranchId -ne $firstRecord.Boundary.BranchId })
    Assert ($branches.Count -eq 2 -and $child.Count -eq 1) 'Feedback fork missing.'
    Assert ($child[0].HeadCommitId -eq $firstRecord.Boundary.StateCommitId) 'Fork used wrong state.'
    $null = Invoke-Cli '05-parent-future' @('--home', $playHome, '--branch', $firstRecord.Boundary.BranchId, '--gym', 'record') @('handoff parent-future-secret')
    $null = Invoke-Cli '06-child' @('--home', $playHome, '--branch', $child[0].BranchId, '--gym', 'record') @(':feedback 子分支反馈。')
    $export = Join-Path $root 'child.md'
    $beforeExport = Hash-Tree $store
    $null = Invoke-Cli '07-export' @('--store', $store, '--branch', $child[0].BranchId, '--actor', 'a', '--gym-export', $export)
    Assert ((Hash-Tree $store) -eq $beforeExport) 'Export modified source.'
    Assert ((Get-Content -Raw -LiteralPath $export).Contains('无可证明的宿主日志切点；祖先宿主材料未继承')) 'Missing fork evidence must be explicit.'
    Assert (-not (Get-Content -Raw -LiteralPath $export).Contains('parent-future-secret')) 'Parent future leaked into child export.'
    $null = Invoke-Cli '08-ephemeral' @('--home', $playHome, '--ephemeral', '--characters', '1') @(':feedback 内存世界反馈。')
    $ephemeral = Get-Content -LiteralPath (Join-Path $playHome 'feedback.jsonl') | ConvertFrom-Json
    Assert ($null -eq $ephemeral.Boundary -and $null -eq $ephemeral.StorePath) 'Ephemeral fabricated a durable boundary.'
    $ephemeralListed = Invoke-Cli '09-ephemeral-query' @('--home', $playHome, '--ephemeral', '--feedbacks')
    Assert (($ephemeralListed | ConvertFrom-Json).FeedbackId -eq $ephemeral.FeedbackId) 'Ephemeral query read the wrong file.'
    $report.Status = 'passed'
    $report.FeedbackId = $firstRecord.FeedbackId
    $report.StateCommitId = $firstRecord.Boundary.StateCommitId
    $report.ChildBranchId = $child[0].BranchId
    Save-Report
    Write-Output "Passed: $root"
} catch {
    $report.Status = 'failed'
    $report.Error = $_.Exception.Message
    Save-Report
    throw
}
