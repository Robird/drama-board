param([string]$Configuration = 'Release', [string]$OutputDirectory)

# Space-exploration slice only: this is not full survival milestone acceptance.
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
        $Frame -notmatch '本局已经结束，不再推进普通活动。' -or $Frame -match '## 可用命令') {
        throw "Expected the completed rescue ending at $Place."
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
$before = Store-Fingerprint $roadStore
$invalid = Run-Cli 'readonly-invalid-eof' @('--survival', '--store', $roadStore) "look`nhelp`ngather berries 0`nwait 0`ncontinue`ncancel`nunknown`nlook`n"
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

# The low route samples both local resources, then pauses at the next decision boundary.
$roadPrefix = "go trail-1`ngather berries 1`ngather water 1`n"
$stream = Run-Cli 'stream-gather-save' @('--survival', '--store', $roadStore) ($roadPrefix + "quit`n")
Assert-Legal $stream
$streamFrame = Last-Frame $stream
if ($streamFrame -notmatch '溪流湾' -or $streamFrame -notmatch 'item.food`）×1' -or $streamFrame -notmatch 'item.water`）×1') {
    throw 'Moving to the stream and gathering its local food/water did not produce inventory.'
}
$before = Store-Fingerprint $roadStore
$streamReopen = Run-Cli 'stream-reopen-readonly' @('--survival', '--store', $roadStore) "look`nhelp`nquit`n"
if ($before -ne (Store-Fingerprint $roadStore) -or $streamFrame -ne (Last-Frame $streamReopen)) {
    throw 'The cold-open local observation or saved boundary changed.'
}

# After departure, stream stock must appear only as explicitly dated historical observation.
$ravine = Run-Cli 'ravine-save' @('--survival', '--store', $roadStore) "go trail-3`nquit`n"
Assert-Legal $ravine
$ravineFrame = Last-Frame $ravine
if ($ravineFrame -notmatch '狭谷旧桥' -or $ravineFrame -notmatch '## 曾到访地点' -or
    $ravineFrame -notmatch '历史观察.*可能已改变' -or $ravineFrame -notmatch '溪流湾（第 0 天') {
    throw 'Departure did not distinguish dated remembered resources from current observation.'
}
$resourceSection = [regex]::Match($ravineFrame, '(?ms)^## 资源点\n(.*?)(?=^## |\z)').Groups[1].Value
if ($resourceSection.Length -eq 0 -or $resourceSection -match 'berries|water|野浆果|溪水' -or
    $ravineFrame -match 'gather berries|gather water|山脚公路|巡山信号站') {
    throw 'The remote stream or an undiscovered exit leaked into current facts/actions.'
}
$road = Run-Cli 'road-resumed-ending' @('--survival', '--store', $roadStore) "go trail-4`n"
$roadContinuous = Run-Cli 'road-continuous-ending' @('--survival') ($roadPrefix + "go trail-3`ngo trail-4`n")
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

# The high route reaches the distinct second exit and demonstrates a different local resource.
$signalPrefix = "go trail-2`ngo trail-5`ngather wood 1`n"
$cabin = Run-Cli 'cabin-save' @('--survival', '--store', $signalStore) ($signalPrefix + "quit`n")
Assert-Legal $cabin
if ((Last-Frame $cabin) -notmatch '废弃林屋' -or (Last-Frame $cabin) -notmatch 'item.wood`）×1') {
    throw 'The high route did not reach the cabin and gather its local wood.'
}
$signal = Run-Cli 'signal-resumed-ending' @('--survival', '--store', $signalStore) "go trail-6`n"
$signalContinuous = Run-Cli 'signal-continuous-ending' @('--survival') ($signalPrefix + "go trail-6`n")
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

$before = Store-Fingerprint $roadStore
$null = Run-Cli 'wrong-play-mode' @('--play', '--store', $roadStore) '' 1
$null = Run-Cli 'wrong-batch-mode' @('--store', $roadStore) '' 1
if ($before -ne (Store-Fingerprint $roadStore)) { throw 'Wrong-mode attempts changed the survival store.' }
$null = Run-Cli 'invalid-survival-options' @('--survival', '--steps', '1') '' 1

[IO.File]::WriteAllText((Join-Path $outputRoot 'road-ending.md'), $roadFrame)
[IO.File]::WriteAllText((Join-Path $outputRoot 'signal-ending.md'), $signalFrame)
@{
    scope = 'A: spatial exploration slice; not full survival milestone acceptance'
    readOnlyInvalidEofAndWrongModePreserveStoreBytes = $true
    hiddenAndNonexistentTargetsHaveSameRejection = $true
    localGatheringAndDatedRemoteMemoryVerified = $true
    distinctExits = @('山脚公路', '巡山信号站')
    bothColdResumedEndingsEqualContinuous = $true
    completedEndingsDoNotAdvanceOnReopen = $true
    remainingMilestoneEvidence = @('needs and consumption', 'random environment risk', 'failure playthrough', 'independent full survival playtests')
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $outputRoot 'acceptance.json') -Encoding utf8
Write-Output "ResourceCycle spatial exploration smoke passed: $outputRoot"
