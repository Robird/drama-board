param([string]$Configuration = 'Release', [string]$OutputDirectory)

# Ordinary Console engineering smoke. No online model calls or free-play claim.
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$cliPath = Join-Path $repoRoot "src/ResourceCycle.Cli/bin/$Configuration/net10.0/DramaBoard.ResourceCycle.Cli.dll"
if (-not (Test-Path -LiteralPath $cliPath)) { throw "Build the CLI first: $cliPath" }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repoRoot ('artifacts/hillside-content/' + [Guid]::NewGuid().ToString('N')) }
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
if ((Test-Path -LiteralPath $outputRoot) -and (Get-ChildItem -LiteralPath $outputRoot -Force | Select-Object -First 1)) {
    throw 'Use a fresh output directory; previous evidence is never overwritten.'
}
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$storePath = Join-Path $outputRoot 'world'
$utf8 = [Text.UTF8Encoding]::new($false)

function Invoke-ContentConsole([string]$Name, [string[]]$Commands, [string[]]$ExtraArguments, [int]$ExpectedExit = 0) {
    $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
    foreach ($argument in @($cliPath, '--store', $storePath, '--steps', '1000') + $ExtraArguments) { $start.ArgumentList.Add($argument) }
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardInputEncoding = $utf8
    $start.StandardOutputEncoding = $utf8
    $start.StandardErrorEncoding = $utf8
    $inputText = ($Commands -join "`n") + "`n"
    [IO.File]::WriteAllText((Join-Path $outputRoot "$Name.input.txt"), $inputText, $utf8)
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if ($Commands.Count -gt 0) { $process.StandardInput.Write($inputText) }
        $process.StandardInput.Close()
        $timedOut = -not $process.WaitForExit(45000)
        if ($timedOut) { $process.Kill($true); $process.WaitForExit() }
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        [IO.File]::WriteAllText((Join-Path $outputRoot "$Name.output.txt"), $stdout, $utf8)
        [IO.File]::WriteAllText((Join-Path $outputRoot "$Name.error.txt"), $stderr, $utf8)
        if ($timedOut -or $process.ExitCode -ne $ExpectedExit) { throw "$Name failed; inspect preserved output." }
        if ($ExpectedExit -eq 0 -and $stderr.Length -ne 0) { throw "$Name wrote unexpected stderr." }
        return [pscustomobject]@{ Output = $stdout; Error = $stderr }
    } finally { $process.Dispose() }
}
function Get-StoreHashes {
    $hashes = [ordered]@{}
    foreach ($file in Get-ChildItem -LiteralPath $storePath -File -Recurse | Sort-Object FullName) {
        $hashes[[IO.Path]::GetRelativePath($storePath, $file.FullName)] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    }
    return ($hashes | ConvertTo-Json -Compress)
}

$first = Invoke-ContentConsole '01-gentle-production' @('go home-workshop', 'produce ration 1', 'go market-workshop', 'sell item.ration 1', ':look', ':quit') @('--content', 'gentle', '--characters', '1')
if (-not $first.Output.Contains('hillside-gentle-v1') -or -not $first.Output.Contains('山野镇·舒缓') -or
    -not $first.Output.Contains('30 分钟') -or -not $first.Output.Contains('余额：26 钱')) {
    throw 'Gentle content identity, recipe duration, or sale proceeds were not visible through Console.'
}
$second = Invoke-ContentConsole '02-saved-definition' @(':look', ':quit') @()
if (-not $second.Output.Contains('hillside-gentle-v1') -or -not $second.Output.Contains('余额：26 钱')) {
    throw 'Omitting --content did not preserve the saved definition and assets.'
}
$beforeMismatch = Get-StoreHashes
$mismatch = Invoke-ContentConsole '03-explicit-mismatch' @() @('--content', 'hillside') 1
if (-not $mismatch.Error.Contains('内容身份不符') -or $beforeMismatch -cne (Get-StoreHashes)) {
    throw 'Explicit mismatch did not reject before changing the save.'
}
$matching = Invoke-ContentConsole '04-explicit-match' @(':look', ':quit') @('--content', 'gentle')
if (-not $matching.Output.Contains('hillside-gentle-v1')) { throw 'Explicit matching content failed.' }
$operations = @(Get-Content -LiteralPath (Join-Path $storePath 'operations.jsonl') | ForEach-Object { ($_ | ConvertFrom-Json).Payload | ConvertFrom-Json })
$received = @($operations | Where-Object { $null -ne $_.Received -and -not $_.Received.ToolName.StartsWith(':', [StringComparison]::Ordinal) })
$expected = @('go home-workshop', 'produce ration 1', 'go market-workshop', 'sell item.ration 1')
if (($received.Received.RawInput -join "`n") -cne ($expected -join "`n")) { throw 'Ordinary commands differ from the plan.' }
foreach ($record in $received) {
    $result = @($operations | Where-Object { $_.OperationRecordId -eq $record.Received.OperationRecordId -and $null -ne $_.Completed })
    if ($result.Count -ne 1 -or $result[0].Completed.Status -ne 0) { throw 'An ordinary command did not succeed exactly once.' }
}
$report = [ordered]@{
    Kind = 'Scripted engineering Console content smoke; no model calls or free play'
    Content = 'gentle'; Processes = 4; OrdinaryCommands = $received.Count; Store = $storePath
    ExplicitMismatchLeftStoreUnchanged = $true
    CliSha256 = (Get-FileHash -LiteralPath $cliPath -Algorithm SHA256).Hash
}
[IO.File]::WriteAllText((Join-Path $outputRoot 'result.json'), ($report | ConvertTo-Json), $utf8)
Write-Output "Hillside named content smoke passed. Evidence: $outputRoot"
