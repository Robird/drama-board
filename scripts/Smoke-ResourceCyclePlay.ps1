param([string]$Configuration = 'Release', [string]$OutputDirectory)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$cliPath = Join-Path $repoRoot "src/ResourceCycle.Cli/bin/$Configuration/net10.0/DramaBoard.ResourceCycle.Cli.dll"
if (-not (Test-Path -LiteralPath $cliPath)) { throw "Build first: $cliPath" }
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repoRoot ('artifacts/resource-cycle-play-smoke/' + [Guid]::NewGuid().ToString('N'))
}
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$storePath = Join-Path $outputRoot 'play-store'
$batchPath = Join-Path $outputRoot 'batch-store'
if ((Test-Path -LiteralPath $storePath) -or (Test-Path -LiteralPath $batchPath)) {
    throw 'Use a fresh output directory; this smoke does not reset existing stores.'
}

function Run-Cli([string]$Name, [string[]]$Arguments, [string]$InputText = '', [int]$ExpectedExit = 0) {
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
    return ((Get-ChildItem -LiteralPath $Path -Recurse -File | Sort-Object FullName | ForEach-Object {
        [IO.Path]::GetRelativePath($Path, $_.FullName) + ':' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }) -join "`n")
}

function Last-Frame([string]$OutputText) {
    $text = $OutputText.Replace("`r`n", "`n")
    $index = $text.LastIndexOf('# 林间苗圃', [StringComparison]::Ordinal)
    if ($index -lt 0) { throw 'No player observation in CLI output.' }
    return ($text.Substring($index) -split '(?m)^[abc]> ', 2)[0].Trim()
}

$null = Run-Cli 'create' @('--play', '--store', $storePath) "quit`n"
$before = Store-Fingerprint $storePath
$null = Run-Cli 'readonly-invalid' @('--play', '--store', $storePath) "look`nhelp`ngather sunflower`ngather sunflower 0`ngather sunflower -1`ngather sunflower 2147483648`nwait 0`nwait 25`ncontinue`ncancel`nunknown`nquit`n"
if ($before -ne (Store-Fingerprint $storePath)) { throw 'Read-only/invalid commands changed store bytes.' }

# A and B compete for the same three units; C waits. A acknowledges B's joining.
# B completes at 16:48 and 17:36, A at 17:00. Depletion wakes B then A at 17:36.
$competitionPrefix = "gather sunflower all`ngather sunflower all`nwait 12`ncontinue`n"
$first = Run-Cli 'first-depletion' @('--play', '--store', $storePath) ($competitionPrefix + "quit`n")
$pendingFrame = Last-Frame $first
if ($pendingFrame -notmatch '（b） · 第 0 天 17:36' -or $pendingFrame -notmatch '曾被取空' -or
    $pendingFrame -notmatch '当前余量 0/5' -or $pendingFrame -notmatch '已取得 2/3' -or
    $pendingFrame -notmatch '第 0 天 18:24' -or $first -notmatch '曾加入') {
    throw 'Competition did not wake the still-busy B with its fixed target and original next deadline.'
}

# Observing, rejected busy actions, and EOF must leave the pending notice and both activities intact.
$before = Store-Fingerprint $storePath
$pendingReopen = Run-Cli 'pending-readonly-invalid-eof' @('--play', '--store', $storePath) "help`ngather moongrass 1`nwait 1`nlook`n"
if ($pendingFrame -ne (Last-Frame $pendingReopen) -or $before -ne (Store-Fingerprint $storePath)) {
    throw 'Observing or rejecting a command consumed the pending notice or changed stored activity.'
}

# Both choose to preserve their current cycles. A fails at 18:00, B at 18:24;
# neither waits indefinitely for tomorrow's regeneration. C becomes ready at D1 04:00.
$competitionTail = "continue`ncontinue`nwait 12`nwait 12`nquit`n"
$resumed = Run-Cli 'resumed-competition' @('--play', '--store', $storePath) $competitionTail
$continuous = Run-Cli 'continuous-competition' @('--play') ($competitionPrefix + $competitionTail)
$frame = Last-Frame $resumed
if ($frame -ne (Last-Frame $continuous)) { throw 'Continuous and cold-resumed player observations differ.' }
if ($frame -notmatch '（c） · 第 1 天 04:00' -or $resumed -notmatch '第 0 天 18:00' -or $resumed -notmatch '第 0 天 18:24') {
    throw 'Finite competing batches did not end at their hand-calculated completion boundaries.'
}
if ($resumed -match '无法行动|无法识别命令' -or $continuous -match '无法行动|无法识别命令') {
    throw 'The supposedly legal competition script contained a rejected action.'
}

$before = Store-Fingerprint $storePath
$reopened = Run-Cli 'reopened-eof' @('--play', '--store', $storePath) "look`n"
if ($frame -ne (Last-Frame $reopened)) { throw 'Cold observation changed.' }
$null = Run-Cli 'wrong-batch-mode' @('--store', $storePath) '' 1
if ($before -ne (Store-Fingerprint $storePath)) { throw 'EOF/reopen/wrong mode changed playable store bytes.' }

# On depletion B can cancel its next cycle without losing the two units already obtained.
# A continues, B switches resource, A finishes unsuccessfully, then B obtains one moon grass.
$cancelled = Run-Cli 'cancel-and-switch' @('--play') ($competitionPrefix + "cancel`ncontinue`ngather moongrass 1`nwait 12`nquit`n")
$cancelFrame = Last-Frame $cancelled
if ($cancelFrame -notmatch '（b） · 第 0 天 18:24' -or
    $cancelFrame -notmatch '太阳花（`item.sunflower`）×2' -or $cancelFrame -notmatch '月光草（`item.moongrass`）×1' -or
    $cancelled -match '无法行动|无法识别命令') {
    throw 'Cancel/switch lost earned items, retained a cancelled cycle, or failed to switch resources.'
}

# A separate cold-open trajectory crosses sunset and sunrise while all three are waiting.
$nightStore = Join-Path $outputRoot 'night-store'
$null = Run-Cli 'night-first' @('--play', '--store', $nightStore) "wait 14`nwait 14`nquit`n"
$nightResumed = Run-Cli 'night-resumed' @('--play', '--store', $nightStore) "wait 14`nquit`n"
$nightContinuous = Run-Cli 'night-continuous' @('--play') "wait 14`nwait 14`nwait 14`nquit`n"
if ((Last-Frame $nightResumed) -ne (Last-Frame $nightContinuous) -or
    (Last-Frame $nightResumed) -notmatch '（a） · 第 1 天 06:00' -or
    $nightResumed -notmatch '天黑了' -or $nightResumed -notmatch '天亮了') {
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
    readOnlyInvalidEofAndModeErrorsPreserveStoreBytes = $true
    joiningAndDepletionWakeBusyActors = $true
    finiteCompetitionAndCancelSwitchVerified = $true
    crossNightColdOpenEqual = $true
    publicOverviewHidesPrivateState = $true
    competitionFinalTime = 'D1 04:00'
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $outputRoot 'acceptance.json') -Encoding utf8
Write-Output "ResourceCycle play smoke passed: $outputRoot"
