# Archived engineering script; its command mode was already retired in pre-rename commit 8401bf0.
# Its legacy arguments are unsupported by the current CLI; this is not a current smoke entry.
# Use scripts/Smoke-Hillside.ps1, Smoke-HillsideContent.ps1, or Smoke-HillsideGym.ps1 for current verification.
# Source is available in commit 8401bf0; consult historical acceptance documents for the matching runnable revision.
param([string]$Configuration = 'Release', [string]$OutputDirectory)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$cliPath = Join-Path $repoRoot "src/Playground.Cli/bin/$Configuration/net10.0/DramaBoard.Playground.Cli.dll"
if (-not (Test-Path -LiteralPath $cliPath)) { throw "Build first: $cliPath" }
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repoRoot ('artifacts/playground-play-smoke/' + [Guid]::NewGuid().ToString('N'))
}
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$storePath = Join-Path $outputRoot 'play-store'
$batchPath = Join-Path $outputRoot 'batch-store'
if ((Test-Path -LiteralPath $storePath) -or (Test-Path -LiteralPath $batchPath)) {
    throw 'Use a fresh output directory; this smoke does not reset existing stores.'
}

function Run-Cli([string]$Name, [string[]]$Arguments, [string]$InputText = '', [int]$ExpectedExit = 0) {
    # Capture the latest saved observation, not the frozen Opening plus later Context Items.
    # A terminal game ignores this trailing read-only preview. Meta/EOF fingerprint probes verify its purity.
    if ($Arguments -contains '--play' -or $Arguments -contains '--survival') {
        $InputText = $InputText.Replace(":exit`n", ":look`n:exit`n")
        if (-not $InputText.EndsWith(":exit`n", [StringComparison]::Ordinal)) { $InputText += ":look`n" }
    }
    $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.ArgumentList.Add($cliPath)
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardInputEncoding = [Text.UTF8Encoding]::new($false)
    $start.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    $start.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $process.StandardInput.Write($InputText)
        $process.StandardInput.Close()
        if (-not $process.WaitForExit(30000)) { $process.Kill($true); throw "$Name exceeded 30 seconds." }
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        [IO.File]::WriteAllText((Join-Path $outputRoot "$Name.out.txt"), $stdout)
        [IO.File]::WriteAllText((Join-Path $outputRoot "$Name.err.txt"), $stderr)
        if ($process.ExitCode -ne $ExpectedExit) { throw "$Name exit $($process.ExitCode), expected $ExpectedExit. $stderr" }
        return $stdout
    } finally { $process.Dispose() }
}

function Store-Fingerprint([string]$Path) {
    # Queries now append host operation receipts; only game E/S must remain unchanged.
    return ((Get-ChildItem -LiteralPath $Path -Recurse -File | Where-Object Name -ne 'operations.jsonl' | Sort-Object FullName | ForEach-Object {
        [IO.Path]::GetRelativePath($Path, $_.FullName) + ':' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }) -join "`n")
}


# Only new receipts from this probe may satisfy rejection checks; old Context feedback is not evidence.
function Operation-LineCount([string]$Path) {
    return @(Get-Content -LiteralPath (Join-Path $Path 'operations.jsonl')).Count
}
function Assert-RejectedOperations([string]$Path, [int]$AfterLine, [int]$ExpectedCount) {
    $receipts = @{}
    $results = @{}
    foreach ($line in (Get-Content -LiteralPath (Join-Path $Path 'operations.jsonl') | Select-Object -Skip $AfterLine)) {
        $record = ($line | ConvertFrom-Json).Payload | ConvertFrom-Json
        if ($null -ne $record.Received -and -not $record.Received.ToolName.StartsWith(':', [StringComparison]::Ordinal)) {
            $receipts[$record.Received.OperationRecordId] = $record.Received
        }
        if ($null -ne $record.Completed) { $results[$record.OperationRecordId] = $record.Completed }
    }
    if ($receipts.Count -ne $ExpectedCount) { throw "Expected $ExpectedCount new game submissions, found $($receipts.Count)." }
    foreach ($id in $receipts.Keys) {
        $result = $results[$id]
        if ($null -eq $result -or $result.Status -ne 1 -or $result.ToolCallId -ne $receipts[$id].ToolCallId -or
            [string]::IsNullOrWhiteSpace($result.WorldVersion) -or [string]::IsNullOrWhiteSpace($result.DomainEvent)) {
            throw 'A probe did not produce a paired, committed game rejection.'
        }
        $result
    }
}

function New-Interaction([string]$OutputText) {
    $firstPrompt = $OutputText.IndexOf('[Console 输入]', [StringComparison]::Ordinal)
    return $(if ($firstPrompt -ge 0) { $OutputText.Substring($firstPrompt) } else { '' })
}

function Last-Frame([string]$OutputText) {
    $text = $OutputText.Replace("`r`n", "`n")
    $index = $text.LastIndexOf('# 林间苗圃', [StringComparison]::Ordinal)
    if ($index -lt 0) { throw 'No player observation in CLI output.' }
    return ($text.Substring($index) -split '(?m)^(?:\[Console 输入\] [abc]> |┄┄ Console 带外信息结束|━━━━━━━━|### 本轮操作)', 2)[0].Trim()
}

# The first entry commits its Opening; read-only byte comparisons start at that established boundary.
$created = Run-Cli 'create' @('--play', '--store', $storePath) ":exit`n"
$initialFrame = Last-Frame $created
$before = Store-Fingerprint $storePath
$readonly = Run-Cli 'readonly-eof' @('--play', '--store', $storePath) ":look`n:help`n"
if ($before -ne (Store-Fingerprint $storePath) -or $initialFrame -ne (Last-Frame $readonly)) { throw 'Pure meta/EOF changed the committed game boundary.' }
$operationsBefore = Operation-LineCount $storePath
$invalid = Run-Cli 'invalid' @('--play', '--store', $storePath) "gather sunflower`ngather sunflower 0`ngather sunflower -1`ngather sunflower 2147483648`nwait 0`nwait 25`ncontinue`ncancel`nunknown`n:exit`n"
Assert-RejectedOperations $storePath $operationsBefore 9 | Out-Null
if ($before -eq (Store-Fingerprint $storePath) -or $initialFrame -ne (Last-Frame $invalid)) { throw 'Rejections must be persisted without changing the latest visible game state.' }

# A and B compete for the same three units; C waits. A acknowledges B's joining.
# B completes at 16:48 and 17:36, A at 17:00. Depletion wakes B then A at 17:36.
$competitionPrefix = "gather sunflower all`ngather sunflower all`nwait 12`ncontinue`n"
$first = Run-Cli 'first-depletion' @('--play', '--store', $storePath) ($competitionPrefix + ":exit`n")
$pendingFrame = Last-Frame $first
if ($pendingFrame -notmatch '（b） · 第 0 天 17:36' -or $pendingFrame -notmatch '曾被取空' -or
    $pendingFrame -notmatch '当前余量 0/5' -or $pendingFrame -notmatch '已取得 2/3' -or
    $pendingFrame -notmatch '第 0 天 18:24' -or $first -notmatch '曾加入') {
    throw 'Competition did not wake the still-busy B with its fixed target and original next deadline.'
}

# Observing, rejected busy actions, and EOF must leave the pending notice and both activities intact.
$before = Store-Fingerprint $storePath
$operationsBefore = Operation-LineCount $storePath
$pendingReopen = Run-Cli 'pending-readonly-invalid-eof' @('--play', '--store', $storePath) ":help`ngather moongrass 1`nwait 1`n:look`n"
Assert-RejectedOperations $storePath $operationsBefore 2 | Out-Null
if ($pendingFrame -ne (Last-Frame $pendingReopen) -or $before -eq (Store-Fingerprint $storePath)) {
    throw 'Observing or rejecting a command consumed the pending notice or changed stored activity.'
}

# Both choose to preserve their current cycles. A fails at 18:00, B at 18:24;
# neither waits indefinitely for tomorrow's regeneration. C becomes ready at D1 04:00.
$competitionTail = "continue`ncontinue`nwait 12`nwait 12`n:exit`n"
$resumed = Run-Cli 'resumed-competition' @('--play', '--store', $storePath) $competitionTail
$continuous = Run-Cli 'continuous-competition' @('--play') ($competitionPrefix + $competitionTail)
$frame = Last-Frame $resumed
if ($frame -ne (Last-Frame $continuous)) { throw 'Continuous and cold-resumed player observations differ.' }
if ($frame -notmatch '（c） · 第 1 天 04:00' -or $resumed -notmatch '第 0 天 18:00' -or $resumed -notmatch '第 0 天 18:24') {
    throw 'Finite competing batches did not end at their hand-calculated completion boundaries.'
}
if ((New-Interaction $resumed) -match '### 本轮操作 [0-9]+（未执行）' -or (New-Interaction $continuous) -match '### 本轮操作 [0-9]+（未执行）') {
    throw 'The supposedly legal competition script contained a rejected action.'
}

$before = Store-Fingerprint $storePath
$reopened = Run-Cli 'reopened-eof' @('--play', '--store', $storePath) ":look`n"
if ($frame -ne (Last-Frame $reopened)) { throw 'Cold observation changed.' }
$null = Run-Cli 'wrong-batch-mode' @('--store', $storePath) '' 1
if ($before -ne (Store-Fingerprint $storePath)) { throw 'EOF/reopen/wrong mode changed playable store bytes.' }

# On depletion B can cancel its next cycle without losing the two units already obtained.
# A continues, B switches resource, A finishes unsuccessfully, then B obtains one moon grass.
$cancelled = Run-Cli 'cancel-and-switch' @('--play') ($competitionPrefix + "cancel`ncontinue`ngather moongrass 1`nwait 12`n:exit`n")
$cancelFrame = Last-Frame $cancelled
if ($cancelFrame -notmatch '（b） · 第 0 天 18:24' -or
    $cancelFrame -notmatch '太阳花（`item.sunflower`）×2' -or $cancelFrame -notmatch '月光草（`item.moongrass`）×1' -or
    (New-Interaction $cancelled) -match '### 本轮操作 [0-9]+（未执行）') {
    throw 'Cancel/switch lost earned items, retained a cancelled cycle, or failed to switch resources.'
}

# A separate cold-open trajectory crosses sunset and sunrise while all three are waiting.
$nightStore = Join-Path $outputRoot 'night-store'
$null = Run-Cli 'night-first' @('--play', '--store', $nightStore) "wait 14`nwait 14`n:exit`n"
$nightResumed = Run-Cli 'night-resumed' @('--play', '--store', $nightStore) "wait 14`n:exit`n"
$nightContinuous = Run-Cli 'night-continuous' @('--play') "wait 14`nwait 14`nwait 14`n:exit`n"
if ((Last-Frame $nightResumed) -ne (Last-Frame $nightContinuous) -or
    (Last-Frame $nightResumed) -notmatch '（a） · 第 1 天 06:00' -or
    $nightResumed -notmatch '白天：是 → 否' -or $nightResumed -notmatch '白天：否 → 是') {
    throw 'Cross-night cold-open trajectory or same-time wake order differs.'
}

# Public other-character sections may expose activity kind, never skill, deadline, or backpack.
foreach ($outputText in @($continuous, $cancelled, $nightResumed)) {
    foreach ($section in [regex]::Matches($outputText, '(?s)## 其他角色\r?\n(.*?)## 你的背包')) {
        if ($section.Groups[1].Value -match '采药等级|分钟|×|\d{2}:\d{2}') {
            throw 'A private skill, period, deadline or inventory leaked through other-character overview.'
        }
    }
}

$null = Run-Cli 'batch-create' @('--store', $batchPath, '--steps', '1')
$before = Store-Fingerprint $batchPath
$null = Run-Cli 'wrong-play-mode' @('--play', '--store', $batchPath) '' 1
if ($before -ne (Store-Fingerprint $batchPath)) { throw 'Wrong mode changed batch store bytes.' }
$null = Run-Cli 'invalid-play-options' @('--play', '--steps', '1') '' 1

[IO.File]::WriteAllText((Join-Path $outputRoot 'final-observation.md'), $frame)
@{
    continuousAndResumedObservationEqual = $true
    pureMetaEofAndModeErrorsPreserveStoreBytes = $true
    invalidActionsPersistRejectionsWithoutChangingVisibleState = $true
    joiningAndDepletionWakeBusyActors = $true
    finiteCompetitionAndCancelSwitchVerified = $true
    crossNightColdOpenEqual = $true
    publicOverviewHidesPrivateState = $true
    competitionFinalTime = 'D1 04:00'
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $outputRoot 'acceptance.json') -Encoding utf8
Write-Output "Playground play smoke passed: $outputRoot"
