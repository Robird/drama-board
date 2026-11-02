# Archived engineering script; its command mode was already retired in pre-rename commit 8401bf0.
# Its legacy arguments are unsupported by the current CLI; this is not a current smoke entry.
# Use scripts/Smoke-Hillside.ps1, Smoke-HillsideContent.ps1, or Smoke-HillsideGym.ps1 for current verification.
# Source is available in commit 8401bf0; consult historical acceptance documents for the matching runnable revision.
param([string]$Configuration = 'Release', [string]$OutputDirectory)

# Engineering Console smoke driven by a Coding Agent script, not human or LLM acceptance.
# Uses an already-built CLI; fresh evidence only, no store reset or direct world editing.
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$cliPath = Join-Path $repoRoot "src/Playground.Cli/bin/$Configuration/net10.0/DramaBoard.Playground.Cli.dll"
if (-not (Test-Path -LiteralPath $cliPath)) { throw "Build first: $cliPath" }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repoRoot ('artifacts/playground-goal-console/' + [Guid]::NewGuid().ToString('N')) }
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
if ((Test-Path -LiteralPath $outputRoot) -and (Get-ChildItem -LiteralPath $outputRoot -Force | Select-Object -First 1)) {
    throw 'Use a fresh output directory; prior evidence is never overwritten.'
}
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$utf8 = [Text.UTF8Encoding]::new($false)
$assertions = [Collections.Generic.List[string]]::new()

function Assert-That([bool]$Condition, [string]$Name) {
    if (-not $Condition) { throw "Assertion failed: $Name" }
    $assertions.Add($Name)
}
function Run-Cli([string]$Name, [string]$StoreName, [string[]]$Commands) {
    $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
    foreach ($argument in @($cliPath, '--survival', '--store', (Join-Path $outputRoot $StoreName))) { $start.ArgumentList.Add($argument) }
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardInputEncoding = $utf8
    $start.StandardOutputEncoding = $utf8
    $start.StandardErrorEncoding = $utf8
    $inputText = ($Commands -join "`n") + "`n:exit`n"
    [IO.File]::WriteAllText((Join-Path $outputRoot "$Name.in.txt"), $inputText, $utf8)
    $process = [Diagnostics.Process]::Start($start)
    try {
        $outTask = $process.StandardOutput.ReadToEndAsync()
        $errTask = $process.StandardError.ReadToEndAsync()
        $process.StandardInput.Write($inputText)
        $process.StandardInput.Close()
        $timedOut = -not $process.WaitForExit(120000)
        if ($timedOut) { $process.Kill($true); $process.WaitForExit() }
        $stdout = $outTask.GetAwaiter().GetResult()
        $stderr = $errTask.GetAwaiter().GetResult()
        [IO.File]::WriteAllText((Join-Path $outputRoot "$Name.out.txt"), $stdout, $utf8)
        [IO.File]::WriteAllText((Join-Path $outputRoot "$Name.err.txt"), $stderr, $utf8)
        if ($timedOut) { throw "$Name exceeded 120 seconds; partial output was preserved." }
        Assert-That ($process.ExitCode -eq 0 -and $stderr.Length -eq 0) "$Name exited cleanly"
        Assert-That (-not $stdout.Contains([char]0xfffd)) "$Name retained valid UTF8"
        return $stdout
    } finally { $process.Dispose() }
}
function Assert-Operations([string]$StoreName, [string[]]$RejectedInputs) {
    $received = [ordered]@{}
    $completed = @{}
    foreach ($line in Get-Content -LiteralPath (Join-Path $outputRoot "$StoreName/operations.jsonl")) {
        $record = ($line | ConvertFrom-Json).Payload | ConvertFrom-Json
        if ($null -ne $record.Received -and -not $record.Received.ToolName.StartsWith(':', [StringComparison]::Ordinal)) {
            $received[$record.Received.OperationRecordId] = $record.Received
        }
        if ($null -ne $record.Completed) { $completed[$record.OperationRecordId] = $record.Completed }
    }
    $rejected = @()
    foreach ($id in $received.Keys) {
        $result = $completed[$id]
        Assert-That ($null -ne $result -and $result.ToolCallId -eq $received[$id].ToolCallId -and
            -not [string]::IsNullOrWhiteSpace($result.WorldVersion) -and -not [string]::IsNullOrWhiteSpace($result.DomainEvent)) "$StoreName paired committed operation $id"
        if ($result.Status -eq 1) { $rejected += $received[$id].RawInput }
        else { Assert-That ($result.Status -eq 0) "$StoreName no interrupted game operation $id" }
    }
    Assert-That (($rejected | Sort-Object) -join "`n" -ceq (($RejectedInputs | Sort-Object) -join "`n")) "$StoreName exact expected rejection set"
    return $received.Count
}

$badCorrection = 'update-goal {"id":"G2","criteria":"新标准：能指出水源"}'
$opening = Run-Cli '01-create-share-query' 'main-store' @(
    ':mode delta', ':help',
    'create-goal {"title":"为两人准备饮水💧","criteria":"出发时备足水","focus":true}',
    'create-goal {"title":"确认溪流湾","criteria":"旧标准：看见溪流","parent":"G1","linkReason":"寻找补给","progress":"已有线索","focus":true}',
    'create-goal {"title":"寻找同伴","criteria":"完成会合"}',
    'link-goal {"parent":"G3","child":"G2","reason":"同伴在那里等待"}',
    'focus-goal {"id":"G2","via":["G3","G2"]}',
    'show-goal {"id":"G2"}', $badCorrection,
    'update-goal {"id":"G2","progress":"已有线索"}', ':look'
)
Assert-That ($opening.Contains('为两人准备饮水💧') -and $opening.Contains('同伴在那里等待')) 'Chinese/emoji prose and both purposes are visible'
Assert-That ($opening.Contains('G3 → G2') -and $opening.Contains('G1 → G2')) 'Shared child exposes both parent links'
Assert-That ($opening.Contains('未改变') -and -not $opening.Contains('详情已过期')) 'Rejected correction and real no-op keep query valid'

$changed = Run-Cli '02-correct-invalidates-query' 'main-store' @(
    'update-goal {"id":"G2","criteria":"新标准：能指出水源","reason":"旧标准过于含糊"}',
    ':mode full', ':look'
)
Assert-That ($changed.Contains('详情已过期')) 'Actual correction explicitly invalidates latest query'
$lifecycle = Run-Cli '03-lifecycle' 'main-store' @(
    'show-goal {"id":"G2"}',
    'pause-goal {"id":"G2","reason":"等待新线索","revisit":"再次遇见同伴时核对"}',
    'resume-goal {"id":"G2"}',
    'focus-goal {"id":"G2","via":["G1","G2"]}',
    'finish-goal {"id":"G2","outcome":"completed","note":"这是角色的实验判断"}',
    'reopen-goal {"id":"G2","reason":"此前判断尚无证据"}',
    'focus-goal {"id":"G2","via":["G3","G2"]}', ':mode delta', ':look'
)
Assert-That ($lifecycle.Contains('旧标准：看见溪流') -and $lifecycle.Contains('旧标准过于含糊')) 'Sensitive correction preserves old standard and reason'
Assert-That ($lifecycle.Contains('等待新线索') -and $lifecycle.Contains('再次遇见同伴时核对')) 'Paused goal retains reason and revisit clue'
Assert-That ($lifecycle.Contains('焦点：原 [G3 → G2]；现 [G3]')) 'Pause receipt distinguishes original focus path from fallback path'
$cold = Run-Cli '04-cold-open' 'main-store' @('show-goal {"id":"G2"}', ':look')
Assert-That ($cold.Contains('G3 → G2') -and $cold.Contains('此前判断尚无证据') -and $cold.Contains('新标准：能指出水源')) 'Cold open restores focus, corrected standard and reopen history'
$next = Run-Cli '05-next-context' 'main-store' @('wait 1', ':look')
Assert-That ($next.Contains('为两人准备饮水💧') -and $next.Contains('再次遇见同伴时核对') -and $next.Contains('G3 → G2')) 'Goals persist across a timed action and new Context'
$mainCount = Assert-Operations 'main-store' @($badCorrection)

$commands = [Collections.Generic.List[string]]::new()
for ($i = 1; $i -le 12; $i++) { $commands.Add('create-goal {"title":"目标' + $i + '","criteria":"用于容量探测"}') }
$badUnfinished = 'create-goal {"title":"未结束额度应拒绝","criteria":"不应分配ID"}'
$commands.Add($badUnfinished)
$commands.Add('finish-goal {"id":"G1","outcome":"completed","note":"释放未结束额度"}')
$commands.Add('create-goal {"title":"目标13","criteria":"复用释放额度"}')
$commands.Add('show-goal {}')
$limit12 = Run-Cli '06-unfinished-limit' 'capacity-store' $commands.ToArray()
Assert-That ($limit12.Contains('未结束 12/12') -and $limit12.Contains('G13 [Open]')) 'Twelve unfinished limit has a finish/create exit and rejected ID is not consumed'

$commands.Clear()
for ($i = 2; $i -le 13; $i++) { $commands.Add('finish-goal {"id":"G' + $i + '","outcome":"completed","note":"容量实验归档"}') }
for ($i = 14; $i -le 32; $i++) {
    $commands.Add('create-goal {"title":"目标' + $i + '","criteria":"用于保留额度"}')
    $commands.Add('finish-goal {"id":"G' + $i + '","outcome":"completed","note":"容量实验归档"}')
}
$badRetained = 'create-goal {"title":"保留额度应拒绝","criteria":"不应分配ID"}'
$commands.Add($badRetained)
$commands.Add('forget-goal {"id":"G1"}')
$commands.Add('create-goal {"title":"目标33","criteria":"释放保留额度后创建"}')
$commands.Add('finish-goal {"id":"G33","outcome":"completed","note":"容量实验归档"}')
$commands.Add('show-goal {}')
$limit32 = Run-Cli '07-retained-limit' 'capacity-store' $commands.ToArray()
Assert-That ($limit32.Contains('保留目标 32/32') -and $limit32.Contains('G33 [Completed]')) 'Thirty-two retained limit has explicit forget/create exit and monotonic IDs'

$commands.Clear()
$edges = 0
for ($parentId = 2; $parentId -le 12 -and $edges -lt 48; $parentId++) {
    for ($childId = $parentId + 1; $childId -le 12 -and $edges -lt 48; $childId++) {
        $commands.Add('link-goal {"parent":"G' + $parentId + '","child":"G' + $childId + '","reason":"共享结论"}')
        $edges++
    }
}
$badLink = 'link-goal {"parent":"G12","child":"G13","reason":"关联额度应拒绝"}'
$badForget = 'forget-goal {"id":"G2"}'
$commands.Add($badLink)
$commands.Add('unlink-goal {"id":"L1"}')
$commands.Add('link-goal {"parent":"G2","child":"G3","reason":"释放关联额度后恢复"}')
$commands.Add($badForget)
$commands.Add('show-goal {}')
for ($i = 2; $i -le 10; $i++) { $commands.Add('unlink-goal {"id":"L' + $i + '"}') }
$commands.Add('unlink-goal {"id":"L49"}')
$commands.Add('forget-goal {"id":"G2"}')
$commands.Add('show-goal {}')
$limit48 = Run-Cli '08-link-limit-cleanup' 'capacity-store' $commands.ToArray()
Assert-That ($limit48.Contains('关联 48/48') -and $limit48.Contains('L49')) 'Forty-eight link limit has unlink/relink exit without recycling ID'
Assert-That ($limit48.Contains('保留目标 31/32；关联 38/48')) 'Linked ended goal can be forgotten after explicit removal of its ten links'
$capacityCount = Assert-Operations 'capacity-store' @($badUnfinished, $badRetained, $badLink, $badForget)

$report = [ordered]@{
    Operator = 'Coding Agent scripted Human Console; no real human or LLM acceptance'
    Cli = $cliPath
    MainGameOperations = $mainCount
    CapacityGameOperations = $capacityCount
    ExpectedRejectedOperations = 5
    Assertions = $assertions.ToArray()
}
[IO.File]::WriteAllText((Join-Path $outputRoot 'acceptance.json'), ($report | ConvertTo-Json -Depth 5), $utf8)
Write-Output "Goal Console smoke passed. Evidence: $outputRoot"
