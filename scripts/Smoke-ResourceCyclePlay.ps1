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
    return ($text.Substring($index) -split "`n> ", 2)[0].Trim()
}

$null = Run-Cli 'create' @('--play', '--store', $storePath) "quit`n"
$before = Store-Fingerprint $storePath
$null = Run-Cli 'readonly-invalid' @('--play', '--store', $storePath) "look`nhelp`ngather sunflower`nwait 0`nwait 25`nunknown`nquit`n"
if ($before -ne (Store-Fingerprint $storePath)) { throw 'Read-only/invalid commands changed store bytes.' }

$first = Run-Cli 'first' @('--play', '--store', $storePath) "wait 6`ngather moongrass`nquit`n"
if ((Last-Frame $first) -notmatch '月光草（`moongrass`）×1') { throw 'First harvest missing from inventory.' }
$resumed = Run-Cli 'resumed' @('--play', '--store', $storePath) "wait 9`ngather sunflower`ngather moongrass`nquit`n"
$continuous = Run-Cli 'continuous' @('--play') "wait 6`ngather moongrass`nwait 9`ngather sunflower`ngather moongrass`nquit`n"
$frame = Last-Frame $resumed
if ($frame -ne (Last-Frame $continuous)) { throw 'Continuous and cold-resumed player observations differ.' }
if ($frame -notmatch '第 1 天 07:00' -or $frame -notmatch '太阳花（`sunflower`）×1' -or $frame -notmatch '月光草（`moongrass`）×2') {
    throw 'Final observation did not match the hand-calculated harvest.'
}

$before = Store-Fingerprint $storePath
$reopened = Run-Cli 'reopened-eof' @('--play', '--store', $storePath) "look`n"
if ($frame -ne (Last-Frame $reopened)) { throw 'Cold observation changed.' }
$null = Run-Cli 'wrong-batch-mode' @('--store', $storePath) '' 1
if ($before -ne (Store-Fingerprint $storePath)) { throw 'EOF/reopen/wrong mode changed playable store bytes.' }

$null = Run-Cli 'batch-create' @('--store', $batchPath, '--steps', '1')
$before = Store-Fingerprint $batchPath
$null = Run-Cli 'wrong-play-mode' @('--play', '--store', $batchPath) '' 1
if ($before -ne (Store-Fingerprint $batchPath)) { throw 'Wrong mode changed batch store bytes.' }
$null = Run-Cli 'invalid-play-options' @('--play', '--steps', '1') '' 1

[IO.File]::WriteAllText((Join-Path $outputRoot 'final-observation.md'), $frame)
@{
    continuousAndResumedObservationEqual = $true
    readOnlyInvalidEofAndModeErrorsPreserveStoreBytes = $true
    finalTime = 'D1 07:00'
    sunflower = 1
    moongrass = 2
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $outputRoot 'acceptance.json') -Encoding utf8
Write-Output "ResourceCycle play smoke passed: $outputRoot"
