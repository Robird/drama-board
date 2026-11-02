param(
    [Parameter(Mandatory)][string]$Store,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$Configuration = 'Release'
)

# Engineering acceptance against an existing real-request store. Never invokes a model,
# edits stored data, or builds. Only ordinary CLI fork and Human commands mutate the store.
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$cliPath = Join-Path $repoRoot "src/Playground.Cli/bin/$Configuration/net10.0/DramaBoard.Playground.Cli.dll"
$storePath = [IO.Path]::GetFullPath($Store)
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
if (-not (Test-Path -LiteralPath $cliPath -PathType Leaf)) { throw "Build the CLI first: $cliPath" }
if (-not (Test-Path -LiteralPath $storePath -PathType Container)) { throw 'Store must already exist.' }
if (Test-Path -LiteralPath $outputRoot) { throw 'OutputDirectory must be new; previous evidence is never overwritten.' }
$relativeOutput = [IO.Path]::GetRelativePath($storePath, $outputRoot)
if ($relativeOutput -eq '.' -or (-not [IO.Path]::IsPathRooted($relativeOutput) -and
    $relativeOutput -ne '..' -and -not $relativeOutput.StartsWith('..' + [IO.Path]::DirectorySeparatorChar))) {
    throw 'OutputDirectory must be outside Store so evidence cannot affect store hashes.'
}
$journalPath = Join-Path $storePath 'gym/events.jsonl'
$sourceOperationsPath = Join-Path $storePath 'operations.jsonl'
foreach ($required in @($journalPath, $sourceOperationsPath)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Missing existing evidence: $required" }
}
New-Item -ItemType Directory -Path $outputRoot | Out-Null
$utf8 = [Text.UTF8Encoding]::new($false)
$processEvidence = [Collections.Generic.List[object]]::new()
$checks = [Collections.Generic.List[string]]::new()
$manifest = [ordered]@{
    Kind = 'Scripted Gym branch-management acceptance; existing real requests, no new model calls or free play'
    Status = 'running'; Store = $storePath; OutputDirectory = $outputRoot
    CliSha256 = (Get-FileHash -LiteralPath $cliPath -Algorithm SHA256).Hash
    StartedUtc = [DateTime]::UtcNow.ToString('O'); Processes = $processEvidence; Checks = $checks
}

function Save-Manifest {
    [IO.File]::WriteAllText((Join-Path $outputRoot 'manifest.json'), ($manifest | ConvertTo-Json -Depth 20), $utf8)
}
function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
function Get-StoreHashes {
    $hashes = [ordered]@{}
    foreach ($file in Get-ChildItem -LiteralPath $storePath -File -Recurse -Force | Sort-Object FullName) {
        $hashes[[IO.Path]::GetRelativePath($storePath, $file.FullName)] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    }
    return ($hashes | ConvertTo-Json -Compress)
}
function Read-EnvelopeRecords([string]$Path) {
    foreach ($line in [IO.File]::ReadAllLines($Path)) {
        if (-not [string]::IsNullOrWhiteSpace($line)) { ($line | ConvertFrom-Json).Payload | ConvertFrom-Json }
    }
}
function Invoke-GymCli([string]$Name, [string[]]$Arguments, [string[]]$Commands = @(), [switch]$ReadOnly) {
    $before = if ($ReadOnly) { Get-StoreHashes } else { $null }
    $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
    foreach ($argument in @($cliPath, '--store', $storePath) + $Arguments) { $start.ArgumentList.Add($argument) }
    $start.WorkingDirectory = $repoRoot
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardInputEncoding = $utf8
    $start.StandardOutputEncoding = $utf8
    $start.StandardErrorEncoding = $utf8
    $inputText = if ($Commands.Count) { ($Commands -join "`n") + "`n" } else { '' }
    [IO.File]::WriteAllText((Join-Path $outputRoot "$Name.input.txt"), $inputText, $utf8)
    $entry = [ordered]@{ Name = $Name; Arguments = $Arguments; ReadOnly = [bool]$ReadOnly; StartedUtc = [DateTime]::UtcNow.ToString('O') }
    $processEvidence.Add($entry)
    Save-Manifest
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if ($inputText.Length) { $process.StandardInput.Write($inputText) }
        $process.StandardInput.Close()
        $timedOut = -not $process.WaitForExit(45000)
        if ($timedOut) { $process.Kill($true); $process.WaitForExit() }
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        [IO.File]::WriteAllText((Join-Path $outputRoot "$Name.output.txt"), $stdout, $utf8)
        [IO.File]::WriteAllText((Join-Path $outputRoot "$Name.error.txt"), $stderr, $utf8)
        $entry.ExitCode = $process.ExitCode
        $entry.TimedOut = $timedOut
        if ($ReadOnly) {
            $entry.StoreUnchanged = $before -ceq (Get-StoreHashes)
            Assert-True $entry.StoreUnchanged "$Name changed store files during a read-only management command."
        }
        Assert-True (-not $timedOut -and $process.ExitCode -eq 0) "$Name failed; inspect preserved stdout/stderr."
        Assert-True ([string]::IsNullOrEmpty($stderr)) "$Name wrote unexpected stderr."
        return $stdout
    } finally {
        $entry.EndedUtc = [DateTime]::UtcNow.ToString('O')
        Save-Manifest
        $process.Dispose()
    }
}
function Read-Branches([string]$Name) {
    $text = Invoke-GymCli $Name @('--branches') -ReadOnly
    foreach ($line in ($text -split '\r?\n')) {
        if (-not [string]::IsNullOrWhiteSpace($line)) { $line | ConvertFrom-Json }
    }
}
function Export-Actor([string]$Name, [string]$Branch, [string]$Actor) {
    $path = Join-Path $outputRoot "$Name.md"
    $null = Invoke-GymCli $Name @('--gym-export', $path, '--branch', $Branch, '--actor', $Actor) -ReadOnly
    return $path
}
function Read-ExportHeaders([string]$Path) {
    # The exporter lengthens fences around embedded backticks. Parse complete JSON
    # fences, not arbitrary headings/ActorId strings inside private recorded text.
    $markdown = [IO.File]::ReadAllText($Path)
    foreach ($match in [regex]::Matches($markdown, '(?ms)^(`{3,})json\r?\n(.*?)\r?\n\1\r?$')) {
        $value = $match.Groups[2].Value | ConvertFrom-Json
        if ($null -ne $value.FormatVersion -and $null -ne $value.Sequence -and $null -ne $value.Kind) { $value }
    }
}
function Assert-Export([string]$Path, [string]$Branch, [string]$Actor, [object[]]$Expected) {
    $headers = @(Read-ExportHeaders $Path)
    $actualSequence = @($headers | ForEach-Object { $_.Sequence }) -join ','
    $expectedSequence = @($Expected | Sort-Object Sequence | ForEach-Object { $_.Sequence }) -join ','
    Assert-True ($actualSequence -ceq $expectedSequence) "Export $Path has incorrect ancestry/cut/actor records."
    Assert-True (@($headers | Where-Object { $null -ne $_.ActorId -and $_.ActorId -cne $Actor }).Count -eq 0) "Export $Path leaks another actor's record."
    $text = [IO.File]::ReadAllText($Path)
    Assert-True ($text.Contains('"BranchId": "' + $Branch + '"') -and $text.Contains('"ActorId": "' + $Actor + '"')) 'Export identity is missing.'
}

try {
    $manifest.InitialStoreHashes = (Get-StoreHashes | ConvertFrom-Json)
    $recordsBefore = @(Read-EnvelopeRecords $journalPath)
    $request = @($recordsBefore | Where-Object { $_.Kind -ceq 'request' -and $_.ActorId -ceq 'a' } | Sort-Object Sequence | Select-Object -First 1)
    Assert-True ($request.Count -eq 1) 'Store must contain an actual recorded request for actor a.'
    $request = $request[0]
    $source = $request.BranchId
    $boundary = $request.Payload.Boundary
    Assert-True ($boundary.ActorId -ceq 'a' -and $boundary.BranchId -ceq $source -and $boundary.TimeTicks -eq 28800000 -and
        $boundary.ContextSequence -eq 1 -and $boundary.OperationSequence -eq 0) 'Expected a first decision at 08:00, before actor a has acted.'
    Assert-True (@($recordsBefore | Where-Object { $_.Kind -ceq 'response' -and $_.Payload.RequestId -ceq $request.Payload.RequestId }).Count -gt 0) 'Source request has no recorded real response.'
    $beforeBranches = @(Read-Branches '01-branches-before')
    $sourceInfo = @($beforeBranches | Where-Object { $_.BranchId -ceq $source })
    Assert-True ($sourceInfo.Count -eq 1 -and $null -eq $sourceInfo[0].ForkedFromCommitId -and -not $sourceInfo[0].HasPendingEvent) 'Expected a completed, original source branch.'
    $sourceHead = $sourceInfo[0].HeadCommitId
    $sourceOperationsHash = (Get-FileHash -LiteralPath $sourceOperationsPath -Algorithm SHA256).Hash
    $sourceExportBefore = Export-Actor '02-source-a-before' $source 'a'
    $manifest.SourceBranch = $source
    $manifest.SourceHead = $sourceHead
    $manifest.SourceRequest = $request.Payload.RequestId
    $manifest.SourceBoundary = $boundary
    $manifest.SourceOperationsSha256 = $sourceOperationsHash

    $null = Invoke-GymCli '03-fork-request' @('--gym-fork-request', $request.Payload.RequestId, '--fork-label', 'gym-scripted-child')
    $forkBranches = @(Read-Branches '04-branches-after-fork')
    $children = @($forkBranches | Where-Object { $_.BranchId -cnotin $beforeBranches.BranchId })
    Assert-True ($children.Count -eq 1 -and $forkBranches.Count -eq $beforeBranches.Count + 1) 'Fork did not register exactly one new branch.'
    $child = $children[0].BranchId
    Assert-True ($children[0].ForkedFromCommitId -ceq $boundary.StateCommitId -and
        $children[0].HeadCommitId -ceq $boundary.StateCommitId -and
        $children[0].CompletedStateCommitId -ceq $boundary.StateCommitId -and
        -not $children[0].HasPendingEvent) 'Child was not registered at the selected completed S.'
    $manifest.ChildBranch = $child
    $manifest.ChildRegisteredHead = $children[0].HeadCommitId
    $afterForkRecords = @(Read-EnvelopeRecords $journalPath)
    $fork = @($afterForkRecords | Where-Object { $_.BranchId -ceq $child -and $_.Kind -ceq 'branch-fork' })
    Assert-True ($fork.Count -eq 1 -and $fork[0].Payload.SourceThroughSequence -eq ($request.Sequence - 1) -and
        $fork[0].Payload.SourceStateCommitId -ceq $boundary.StateCommitId -and $fork[0].Payload.SourceRequestId -ceq $request.Payload.RequestId) 'Fork evidence does not stop immediately before the selected request.'
    $cut = [long]$fork[0].Payload.SourceThroughSequence
    $manifest.SourceThroughSequence = $cut
    $childBefore = Export-Actor '05-child-a-before' $child 'a'
    $beforeVisible = @($afterForkRecords | Where-Object {
        ($_.BranchId -ceq $child -or ($_.BranchId -ceq $source -and $_.Sequence -le $cut)) -and
        ($_.ActorId -ceq 'a' -or ($null -eq $_.ActorId -and $_.Kind -cin @('run-start', 'run-end', 'branch-fork')))
    })
    Assert-Export $childBefore $child 'a' $beforeVisible
    $checks.Add('Registered child uses the selected completed S and pre-request evidence cut')

    # Deliberately absent connection catalog blocks accidental provider initialization,
    # even when the parent process has usable credentials. Normal Human routing never reads it.
    $absentCatalog = Join-Path $outputRoot 'model-calls-forbidden.catalog.json'
    $common = @('--branch', $child, '--llm', '--gym', '--catalog', $absentCatalog, '--connection', 'forbidden')
    $firstAudit = Join-Path $outputRoot '06-child-handoff.audit.jsonl'
    $firstOutput = Invoke-GymCli '06-child-handoff' ($common + @('--human', 'a', '--steps', '1', '--llm-log', $firstAudit)) @('handoff gym-child-private')
    $secondAudit = Join-Path $outputRoot '07-child-travel.audit.jsonl'
    # Successful a handoff yields fairly to b. The second process opens b's context,
    # then commits b's move; neither transition needs an LLM controller.
    $secondOutput = Invoke-GymCli '07-child-travel' ($common + @('--human', 'b', '--steps', '2', '--llm-log', $secondAudit)) @('go home-workshop')
    Assert-True ($firstOutput.Contains('山野镇住处') -and -not $secondOutput.Contains('gym-child-private')) 'Selected location was lost or actor b saw actor a private handoff.'
    foreach ($auditPath in @($firstAudit, $secondAudit)) {
        $audit = @([IO.File]::ReadAllLines($auditPath) | ForEach-Object { $_ | ConvertFrom-Json })
        Assert-True (@($audit | Where-Object { $_.Kind -cin @('binding', 'model-input', 'model-output') }).Count -eq 0) 'A child process attempted provider initialization or a model request.'
        Assert-True (@($audit | Where-Object { $_.Kind -ceq 'host' -and $_.Payload.State -ceq 'ready' }).Count -eq 1) 'Child cold open lacks ready evidence.'
    }
    $firstAuditRecords = @([IO.File]::ReadAllLines($firstAudit) | ForEach-Object { $_ | ConvertFrom-Json })
    Assert-True (@($firstAuditRecords | Where-Object { $_.Kind -ceq 'host-stop' -and $_.Payload.Reason -ceq 'step-budget' }).Count -eq 1) 'First process did not stop after its one-step budget.'
    $secondAuditRecords = @([IO.File]::ReadAllLines($secondAudit) | ForEach-Object { $_ | ConvertFrom-Json })
    Assert-True (@($secondAuditRecords | Where-Object { $_.Kind -ceq 'committed' -and $_.Payload.Kind -ceq 'MoveStarted' -and $_.Payload.ActorId -ceq 'b' -and $_.Payload.Disposition -ceq 'Succeeded' }).Count -eq 1) 'Second process did not commit exactly one actor b command.'
    Assert-True (@($secondAuditRecords | Where-Object { $_.Kind -ceq 'host-stop' -and $_.Payload.Reason -ceq 'step-budget' }).Count -eq 1) 'Second process did not stop at its two-step budget.'

    $childOperationsPath = Join-Path $storePath "branch-operations/$child.jsonl"
    $childOperations = @(Read-EnvelopeRecords $childOperationsPath)
    $received = @($childOperations | Where-Object { $null -ne $_.Received })
    $ordinary = @($received | Where-Object { -not $_.Received.ToolName.StartsWith(':', [StringComparison]::Ordinal) })
    Assert-True (($ordinary.Received.RawInput -join "`n") -ceq "handoff gym-child-private`ngo home-workshop") 'Child ordinary commands were missing, duplicated, or not isolated in branch-operations.'
    Assert-True (($received.Received.ActorId -join ',') -ceq 'a,b') 'Child operations were not exactly actor a handoff followed by actor b travel.'
    foreach ($operation in $ordinary) {
        $completed = @($childOperations | Where-Object { $_.OperationRecordId -ceq $operation.Received.OperationRecordId -and $null -ne $_.Completed })
        Assert-True ($completed.Count -eq 1 -and $completed[0].Completed.Status -eq 0 -and
            -not [string]::IsNullOrWhiteSpace($completed[0].Completed.DomainEvent)) 'Child command lacks exactly one successful domain result.'
    }
    $checks.Add('Two cold Human runs made zero provider bindings/requests; handoff survived and one travel command committed')

    $afterBranches = @(Read-Branches '08-branches-after-actions')
    $sourceAfter = @($afterBranches | Where-Object { $_.BranchId -ceq $source })
    $childAfter = @($afterBranches | Where-Object { $_.BranchId -ceq $child })
    Assert-True ($sourceAfter.Count -eq 1 -and $sourceAfter[0].HeadCommitId -ceq $sourceHead -and
        $sourceAfter[0].CompletedStateCommitId -ceq $sourceInfo[0].CompletedStateCommitId -and -not $sourceAfter[0].HasPendingEvent) 'Child activity changed source head.'
    Assert-True ($childAfter.Count -eq 1 -and $childAfter[0].HeadCommitId -cne $children[0].HeadCommitId -and -not $childAfter[0].HasPendingEvent) 'Child did not advance to a completed head.'
    Assert-True ((Get-FileHash -LiteralPath $sourceOperationsPath -Algorithm SHA256).Hash -ceq $sourceOperationsHash) 'Child activity changed source operations.jsonl.'
    $recordsAfter = @(Read-EnvelopeRecords $journalPath)
    $childRecords = @($recordsAfter | Where-Object { $_.BranchId -ceq $child })
    $runs = @($childRecords | Where-Object { $_.Kind -ceq 'run-start' })
    Assert-True ($runs.Count -eq 2 -and @($childRecords | Where-Object { $_.Kind -cin @('request', 'response') }).Count -eq 0) 'Expected two child runs without model requests or responses.'
    $coldPerception = @($childRecords | Where-Object { $_.RunId -ceq $runs[1].RunId -and $_.Kind -ceq 'perception' -and
        $_.ActorId -ceq 'a' } | Sort-Object Sequence | Select-Object -First 1)
    Assert-True ($coldPerception.Count -eq 1 -and $coldPerception[0].Payload.Material.Contains('gym-child-private')) 'Second cold run did not restore actor a private handoff in its own perception.'
    Assert-True (@($recordsAfter | Where-Object { $_.Sequence -gt $recordsBefore[-1].Sequence -and $_.BranchId -ceq $source }).Count -eq 0) 'Child run appended evidence to source branch.'
    $sourceRuns = @($recordsBefore | Where-Object { $_.Kind -ceq 'run-start' -and $_.RunId -ceq $request.RunId -and $_.BranchId -ceq $source })
    Assert-True ($sourceRuns.Count -eq 1) 'Selected source request lacks a unique run-start content identity.'
    $sourceContent = $sourceRuns[0].Payload.Content
    for ($runIndex = 0; $runIndex -lt $runs.Count; $runIndex++) {
        $run = $runs[$runIndex]
        $humanActor = @('a', 'b')[$runIndex]
        Assert-True (@($run.Payload.Actors | Where-Object { $_.ActorId -ceq $humanActor -and $_.Controller -ceq 'Human' }).Count -eq 1 -and
            @($run.Payload.Actors | Where-Object { $_.ActorId -cne $humanActor -and $_.Controller -ceq 'LLM' }).Count -eq 2) 'Child controller binding differs from the selected Human and two LLM residents.'
        Assert-True ($run.Payload.Content.ContentId -ceq $sourceContent.ContentId -and
            $run.Payload.Content.Fingerprint -ceq $sourceContent.Fingerprint) 'Child cold open changed source content identity.'
        Assert-True (@($childRecords | Where-Object { $_.Kind -ceq 'run-end' -and $_.RunId -ceq $run.RunId }).Count -eq 1) 'Child run has no unique termination record.'
    }
    $firstEnd = @($childRecords | Where-Object { $_.Kind -ceq 'run-end' -and $_.RunId -ceq $runs[0].RunId })[0]
    Assert-True ($runs[0].Payload.StateCommitId -ceq $boundary.StateCommitId -and
        $runs[1].Payload.StateCommitId -ceq $firstEnd.Payload.StateCommitId) 'Cold runs did not start at the selected S and then the first run completed S.'
    $checks.Add('Cold runs preserve source content identity and continue exactly from the preceding completed S')
    $sourceExportAfter = Export-Actor '09-source-a-after' $source 'a'
    Assert-True ((Get-FileHash -LiteralPath $sourceExportBefore -Algorithm SHA256).Hash -ceq
        (Get-FileHash -LiteralPath $sourceExportAfter -Algorithm SHA256).Hash) 'Source actor export changed after child actions.'
    foreach ($actor in @('a', 'b', 'c')) {
        $export = Export-Actor "10-child-$actor-after" $child $actor
        $expected = @($recordsAfter | Where-Object {
            ($_.BranchId -ceq $child -or ($_.BranchId -ceq $source -and $_.Sequence -le $cut)) -and
            ($_.ActorId -ceq $actor -or ($null -eq $_.ActorId -and $_.Kind -cin @('run-start', 'run-end', 'branch-fork')))
        })
        Assert-Export $export $child $actor $expected
        $hasPrivate = [IO.File]::ReadAllText($export).Contains('gym-child-private')
        Assert-True ($hasPrivate -eq ($actor -ceq 'a')) 'Private child handoff crossed the actor export boundary or was lost.'
    }
    $checks.Add('Source head, operations hash, and export unchanged; child operations and private material isolated')
    $checks.Add('Every branches/export process left the entire store byte-for-byte unchanged')
    $manifest.ChildCompletedHead = $childAfter[0].CompletedStateCommitId
    $manifest.ChildRequestCount = 0
    $manifest.ChildOrdinaryCommands = $ordinary.Received.RawInput
    $manifest.FinalStoreHashes = (Get-StoreHashes | ConvertFrom-Json)
    $manifest.Status = 'passed'
} catch {
    $manifest.Status = 'failed'
    $manifest.Failure = $_.Exception.Message
    throw
} finally {
    $manifest.EndedUtc = [DateTime]::UtcNow.ToString('O')
    Save-Manifest
}
Write-Output "Hillside Gym management acceptance passed. Evidence: $outputRoot"
