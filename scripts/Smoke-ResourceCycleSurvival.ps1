param([string]$Configuration = 'Release', [string]$OutputDirectory)

# Spatial exploration, survival needs and weather engineering smoke, not independent playtest acceptance.
# Uses the real Console process and never rebuilds, resets stores, or removes prior evidence.
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$cliPath = Join-Path $repoRoot "src/ResourceCycle.Cli/bin/$Configuration/net10.0/DramaBoard.ResourceCycle.Cli.dll"
if (-not (Test-Path -LiteralPath $cliPath)) { throw "Build first: $cliPath" }
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repoRoot ('artifacts/resource-cycle-survival-smoke/' + [Guid]::NewGuid().ToString('N'))
}
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
if ((Test-Path -LiteralPath $outputRoot) -and (Get-ChildItem -LiteralPath $outputRoot -Force | Select-Object -First 1)) {
    throw 'Use a fresh output directory; this smoke never overwrites existing evidence.'
}
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$roadStore = Join-Path $outputRoot 'road-store'
$signalStore = Join-Path $outputRoot 'signal-store'
$failureStore = Join-Path $outputRoot 'failure-store'

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
        if ($ExpectedExit -eq 0 -and $stderr.Trim().Length -ne 0) { throw "$Name emitted unexpected stderr: $stderr" }
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
    $index = $text.LastIndexOf('# 山林求生', [StringComparison]::Ordinal)
    if ($index -lt 0) { throw 'No survival observation in CLI output.' }
    return ($text.Substring($index) -split '(?m)^a> ', 2)[0].Trim()
}

function Assert-Legal([string]$OutputText) {
    if ($OutputText -match '无法行动|无法识别命令') { throw 'The supposedly legal route included a rejected command.' }
}

function Assert-Ending([string]$Frame, [string]$Place) {
    if (-not $Frame.Contains($Place) -or $Frame -notmatch '## 结局\s+获救。' -or
        $Frame -notmatch '本局已经结束，不再推进普通活动。' -or $Frame -match '## 可用命令|下次窗口') {
        throw "Expected the completed rescue ending at $Place."
    }
}

function Vital([string]$Frame, [string]$Name) {
    $match = [regex]::Match($Frame, ('(?m)^\| ' + [regex]::Escape($Name) + ' \| ([0-9.]+) \|'))
    if (-not $match.Success) { throw "No $Name vital in player observation." }
    return [decimal]::Parse($match.Groups[1].Value, [Globalization.CultureInfo]::InvariantCulture)
}

function Assert-WarningInTransit([string]$Frame) {
    if ($Frame -notmatch '第 0 天 17:30' -or $Frame -notmatch '生存警告（历史记录，待确认）' -or
        $Frame -notmatch '正在途中；预计 第 0 天 18:45 到达') {
        throw 'The water warning did not preserve the original in-flight arrival boundary.'
    }
}

$initial = Run-Cli 'create' @('--survival', '--store', $roadStore) "quit`n"
$initialFrame = Last-Frame $initial
if ($initialFrame -notmatch '迷途营地' -or $initialFrame -notmatch 'go trail-1' -or $initialFrame -notmatch 'go trail-2') {
    throw 'The initial observation does not expose its local route choices.'
}
if ($initial -match '溪流湾|狭谷旧桥|山脚公路|风口山脊|废弃林屋|巡山信号站|ridge-herbs|野浆果|干木|go trail-[3-7]') {
    throw 'The initial observation disclosed an unvisited place, distant resource, or nonlocal route.'
}
if ($initialFrame -notmatch '## 天气与暴露' -or $initialFrame -notmatch '下次窗口：第 0 天 17:00' -or
    $initialFrame -notmatch '尚无已发生的天气记录' -or $initialFrame -match '最近实际天气：|20261028|RandomSeed|Sequence') {
    throw 'Initial weather observation omitted the known risk window or exposed a future result/internal random state.'
}
$before = Store-Fingerprint $roadStore
$invalid = Run-Cli 'readonly-invalid-eof' @('--survival', '--store', $roadStore) "look`nhelp`ngather berries 0`nwait 0`ncontinue`ncancel`nuse item.food`nuse nonexistent`nunknown`nlook`n"
if ($before -ne (Store-Fingerprint $roadStore) -or $initialFrame -ne (Last-Frame $invalid)) {
    throw 'Read-only/invalid commands or EOF changed the initial saved boundary.'
}
if ($invalid -match '山脚公路|巡山信号站|野浆果|干木|ridge-herbs') {
    throw 'Invalid target probing disclosed hidden world content.'
}

# Isolate each probe so unrelated validation failures cannot satisfy the equality assertion.
foreach ($probe in @(
    @{ Name = 'resource'; Existing = 'gather berries 1'; Missing = 'gather nonexistent 1'; Reason = '当前不可见或不可用的资源点。' },
    @{ Name = 'passage'; Existing = 'go trail-4'; Missing = 'go nonexistent'; Reason = '当前不可见或不可用的通路。' }
)) {
    $rejections = @()
    foreach ($kind in @('Existing', 'Missing')) {
        $outputText = Run-Cli ("probe-" + $probe.Name + '-' + $kind.ToLowerInvariant()) @('--survival', '--store', $roadStore) ($probe[$kind] + "`nquit`n")
        $matches = [regex]::Matches($outputText.Replace("`r`n", "`n"), '(?m)^a> (无法行动：[^\n]+)$')
        if ($matches.Count -ne 1 -or -not $matches[0].Groups[1].Value.Contains($probe.Reason)) {
            throw "The $($probe.Name) $kind probe did not yield exactly one scoped rejection."
        }
        $rejections += $matches[0].Groups[1].Value
        if ($outputText -match '山脚公路|巡山信号站|野浆果|干木|ridge-herbs') {
            throw 'A target probe disclosed hidden world content.'
        }
    }
    if ($rejections[0] -ne $rejections[1]) { throw "Existing hidden and missing $($probe.Name) targets produced different rejection text." }
}
if ($before -ne (Store-Fingerprint $roadStore)) { throw 'Target probes changed the saved boundary.' }

# At 16:00 arrival coincides with the food warning. Acknowledge, gather and eat,
# then collect water. Food is absent initially, so this route exercises real supply acquisition.
$roadPrefix = "go trail-1`ncontinue`ngather berries 1`nuse item.food`ngather water 1`n"
$stream = Run-Cli 'stream-gather-save' @('--survival', '--store', $roadStore) ($roadPrefix + "quit`n")
Assert-Legal $stream
$streamFrame = Last-Frame $stream
if ($streamFrame -notmatch '溪流湾' -or $streamFrame -match 'item.food`）×' -or $streamFrame -notmatch 'item.water`）×2' -or
    (Vital $streamFrame '食物') -ne 56.25 -or (Vital $streamFrame '饮水') -ne 31) {
    throw 'Local food acquisition/consumption and water gathering did not produce the expected body/inventory boundary.'
}
$before = Store-Fingerprint $roadStore
$streamReopen = Run-Cli 'stream-reopen-readonly' @('--survival', '--store', $roadStore) "look`nhelp`nquit`n"
if ($before -ne (Store-Fingerprint $roadStore) -or $streamFrame -ne (Last-Frame $streamReopen)) {
    throw 'The cold-open local observation or saved boundary changed.'
}

# Water reaches its warning at 17:30, before the already scheduled 18:45 arrival.
# Cold-open this pending warning, then drink without advancing time or silently acknowledging it.
$warning = Run-Cli 'moving-warning-save' @('--survival', '--store', $roadStore) "go trail-3`nquit`n"
Assert-Legal $warning
$warningFrame = Last-Frame $warning
Assert-WarningInTransit $warningFrame
if ((Vital $warningFrame '饮水') -ne 25) { throw 'The water warning was not delivered at its threshold.' }
if ((Vital $warningFrame '温暖') -ne 65 -or
    $warningFrame -notmatch '最近实际天气：第 0 天 17:00，寒风；当时暴露在外，温暖实际减少 10 点。') {
    throw 'The first recorded cold wind did not apply its actual ten-point loss to the exposed traveller.'
}
$before = Store-Fingerprint $roadStore
$warningReopen = Run-Cli 'moving-warning-readonly-invalid' @('--survival', '--store', $roadStore) "look`nhelp`nwait 1`ncancel`ngather berries 1`nuse nonexistent`nlook`n"
if ($warningFrame -ne (Last-Frame $warningReopen) -or $before -ne (Store-Fingerprint $roadStore)) {
    throw 'Read-only/rejected commands consumed a pending survival warning or changed its in-flight movement.'
}
$supplied = Run-Cli 'moving-warning-drink-save' @('--survival', '--store', $roadStore) "use item.water`nquit`n"
Assert-Legal $supplied
$suppliedFrame = Last-Frame $supplied
Assert-WarningInTransit $suppliedFrame
if ((Vital $suppliedFrame '饮水') -ne 70 -or $suppliedFrame -notmatch 'item.water`）×1' -or
    (Vital $suppliedFrame '食物') -ne (Vital $warningFrame '食物')) {
    throw 'Drinking did not atomically deduct one item and add water without elapsed time.'
}
$before = Store-Fingerprint $roadStore
$suppliedReopen = Run-Cli 'moving-supplied-warning-reopen' @('--survival', '--store', $roadStore) "look`nquit`n"
if ($suppliedFrame -ne (Last-Frame $suppliedReopen) -or $before -ne (Store-Fingerprint $roadStore)) {
    throw 'Consumption or the still-unacknowledged warning did not survive cold open unchanged.'
}

# Continue acknowledges the historical warning; it must not restart the original trip.
$ravine = Run-Cli 'ravine-save' @('--survival', '--store', $roadStore) "continue`nquit`n"
Assert-Legal $ravine
$ravineFrame = Last-Frame $ravine
if ($ravineFrame -notmatch '狭谷旧桥' -or $ravineFrame -notmatch '第 0 天 18:45' -or
    $ravineFrame -match '生存警告（历史记录，待确认）' -or $ravineFrame -notmatch '## 曾到访地点' -or
    $ravineFrame -notmatch '历史观察.*可能已改变' -or $ravineFrame -notmatch '溪流湾（第 0 天') {
    throw 'Departure did not distinguish dated remembered resources from current observation.'
}
$resourceSection = [regex]::Match($ravineFrame, '(?ms)^## 资源点\n(.*?)(?=^## |\z)').Groups[1].Value
if ($resourceSection.Length -eq 0 -or $resourceSection -match 'berries|water|野浆果|溪水' -or
    $ravineFrame -match 'gather berries|gather water|山脚公路|巡山信号站') {
    throw 'The remote stream or an undiscovered exit leaked into current facts/actions.'
}
# A second cold wind crosses the warmth warning at 21:00 while arrival remains 21:45.
# Save after the random result and compare reopening before acknowledging its consequence.
$cold = Run-Cli 'road-coldwind-warning-save' @('--survival', '--store', $roadStore) "go trail-4`nquit`n"
Assert-Legal $cold
$coldFrame = Last-Frame $cold
if ((Vital $coldFrame '温暖') -ne 22 -or $coldFrame -notmatch '生存警告（历史记录，待确认）' -or
    $coldFrame -notmatch '正在途中；预计 第 0 天 21:45 到达' -or
    $coldFrame -notmatch '最近实际天气：第 0 天 21:00，寒风；当时暴露在外，温暖实际减少 10 点。') {
    throw 'The later cold wind did not cause the observed warmth warning without changing the journey.'
}
$before = Store-Fingerprint $roadStore
$coldReopen = Run-Cli 'road-coldwind-readonly-reopen' @('--survival', '--store', $roadStore) "look`nhelp`nlook`nquit`n"
if ($coldFrame -ne (Last-Frame $coldReopen) -or $before -ne (Store-Fingerprint $roadStore)) {
    throw 'Reading after a random result changed its saved outcome, warning, or remaining journey.'
}
$road = Run-Cli 'road-resumed-ending' @('--survival', '--store', $roadStore) "continue`n"
$roadContinuous = Run-Cli 'road-continuous-ending' @('--survival') ($roadPrefix + "go trail-3`nuse item.water`ncontinue`ngo trail-4`ncontinue`n")
Assert-Legal $road
Assert-Legal $roadContinuous
$roadFrame = Last-Frame $road
Assert-Ending $roadFrame '山脚公路'
if ($roadFrame -ne (Last-Frame $roadContinuous)) { throw 'Cold-resumed and continuous low-route endings differ.' }
$before = Store-Fingerprint $roadStore
$roadReopen = Run-Cli 'road-ending-reopen' @('--survival', '--store', $roadStore) "wait 24`ngo trail-4`n"
if ($roadFrame -ne (Last-Frame $roadReopen) -or $before -ne (Store-Fingerprint $roadStore) -or $roadReopen -match '(?m)^a> ') {
    throw 'A completed ending accepted input, advanced time, or changed after cold open.'
}

# The high route acknowledges its first food warning while moving, gathers ridge food,
# and responds to the water warning at 17:30 with both supplies. Shelter then restores warmth.
$signalPrefix = "go trail-2`ncontinue`ngather ridge-herbs 1`nuse item.food`nuse item.water`ncontinue`ngo trail-5`n"
$cabin = Run-Cli 'cabin-save' @('--survival', '--store', $signalStore) ($signalPrefix + "quit`n")
Assert-Legal $cabin
$cabinFrame = Last-Frame $cabin
if ($cabinFrame -notmatch '废弃林屋' -or $cabinFrame -notmatch '当前有庇护' -or
    (Vital $cabinFrame '温暖') -ne 50 -or (Vital $cabinFrame '食物') -ne 42.5 -or
    $cabinFrame -notmatch '最近实际天气：第 0 天 19:00，平静') {
    throw 'The high route did not reach shelter after gathering and consuming its local food.'
}
$warmed = Run-Cli 'cabin-warming-save' @('--survival', '--store', $signalStore) "wait 2`nquit`n"
Assert-Legal $warmed
$warmedFrame = Last-Frame $warmed
if ((Vital $warmedFrame '温暖') -ne 74 -or (Vital $warmedFrame '食物') -ne 32.5 -or
    (Vital $warmedFrame '饮水') -ne 38 -or $warmedFrame -notmatch '第 0 天 21:30' -or
    $warmedFrame -notmatch '最近实际天气：第 0 天 21:00，寒风；当时受到庇护保护，温暖损失 0 点。') {
    throw 'Two sheltered hours did not warm the character and prevent the same cold-wind loss while consuming supplies.'
}
$before = Store-Fingerprint $signalStore
$shelteredReopen = Run-Cli 'cabin-weather-readonly-reopen' @('--survival', '--store', $signalStore) "help`nlook`nquit`n"
if ($warmedFrame -ne (Last-Frame $shelteredReopen) -or $before -ne (Store-Fingerprint $signalStore)) {
    throw 'The recorded sheltered weather outcome changed on a read-only cold open.'
}
# Both later warnings are consciously accepted with enough reserves to finish the fixed journey.
$signalTail = "go trail-6`ncontinue`ncontinue`n"
$signal = Run-Cli 'signal-resumed-ending' @('--survival', '--store', $signalStore) $signalTail
$signalContinuous = Run-Cli 'signal-continuous-ending' @('--survival') ($signalPrefix + "wait 2`n" + $signalTail)
Assert-Legal $signal
Assert-Legal $signalContinuous
$signalFrame = Last-Frame $signal
Assert-Ending $signalFrame '巡山信号站'
if ($signalFrame -ne (Last-Frame $signalContinuous)) { throw 'Cold-resumed and continuous high-route endings differ.' }
$before = Store-Fingerprint $signalStore
$signalReopen = Run-Cli 'signal-ending-reopen' @('--survival', '--store', $signalStore)
if ($signalFrame -ne (Last-Frame $signalReopen) -or $before -ne (Store-Fingerprint $signalStore)) {
    throw 'The second completed ending changed after cold open.'
}

# Ignoring two actionable warnings ends in dehydration, even with an unused bottle in the pack.
$failureStart = Run-Cli 'no-supply-warning-save' @('--survival', '--store', $failureStore) "wait 24`nquit`n"
Assert-Legal $failureStart
if ((Last-Frame $failureStart) -notmatch '生存警告（历史记录，待确认）') {
    throw 'A long wait failed to offer a survival-warning response before death.'
}
$failure = Run-Cli 'no-supply-resumed-ending' @('--survival', '--store', $failureStore) "continue`ncontinue`n"
$failureContinuous = Run-Cli 'no-supply-continuous-ending' @('--survival') "wait 24`ncontinue`ncontinue`n"
Assert-Legal $failure
Assert-Legal $failureContinuous
$failureFrame = Last-Frame $failure
if ($failureFrame -notmatch '## 结局' -or $failureFrame -notmatch '脱水耗尽' -or $failureFrame -match '获救|## 可用命令|下次窗口' -or
    (Vital $failureFrame '饮水') -ne 0 -or $failureFrame -notmatch '第 0 天 20:37:30.000' -or
    $failureFrame -ne (Last-Frame $failureContinuous)) {
    throw 'Ignoring supply/warnings did not yield the same explained fatal boundary after cold continuation.'
}
$before = Store-Fingerprint $failureStore
$failureReopen = Run-Cli 'failure-ending-reopen' @('--survival', '--store', $failureStore) "use item.water`ncontinue`n"
if ($failureFrame -ne (Last-Frame $failureReopen) -or $before -ne (Store-Fingerprint $failureStore) -or
    $failureReopen -match '(?m)^a> ') { throw 'A fatal ending accepted input, revived, or advanced after cold open.' }

$before = Store-Fingerprint $roadStore
$null = Run-Cli 'wrong-play-mode' @('--play', '--store', $roadStore) '' 1
$null = Run-Cli 'wrong-batch-mode' @('--store', $roadStore) '' 1
if ($before -ne (Store-Fingerprint $roadStore)) { throw 'Wrong-mode attempts changed the survival store.' }
$null = Run-Cli 'invalid-survival-options' @('--survival', '--steps', '1') '' 1

[IO.File]::WriteAllText((Join-Path $outputRoot 'road-ending.md'), $roadFrame)
[IO.File]::WriteAllText((Join-Path $outputRoot 'signal-ending.md'), $signalFrame)
[IO.File]::WriteAllText((Join-Path $outputRoot 'failure-ending.md'), $failureFrame)
@{
    scope = 'A+B+C: spatial exploration, survival needs and weather engineering smoke; not independent playtest acceptance'
    readOnlyInvalidEofAndWrongModePreserveStoreBytes = $true
    hiddenAndNonexistentTargetsHaveSameRejection = $true
    localGatheringAndDatedRemoteMemoryVerified = $true
    distinctExits = @('山脚公路', '巡山信号站')
    bothColdResumedEndingsEqualContinuous = $true
    completedEndingsDoNotAdvanceOnReopen = $true
    acquiredFoodAndWaterConsumptionVerified = $true
    pendingWarningSurvivesReadOnlyAndSupplyUntilContinue = $true
    inFlightSupplyAndContinuePreserveArrivalDeadline = $true
    shelterWarmsWhileConsumingFoodAndWater = $true
    recordedColdWindReducesExposedWarmth = $true
    sameWeatherWindowHasNoLossInsideShelter = $true
    randomResultAndWarningSurviveReadOnlyColdOpen = $true
    noSupplyFailureMatchesColdContinuation = $true
    notCoveredByThisSmoke = @('independent full survival playtests; see docs/experiments/survival-demo-acceptance.md')
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $outputRoot 'acceptance.json') -Encoding utf8
Write-Output "ResourceCycle spatial exploration, survival needs and weather smoke passed: $outputRoot"
