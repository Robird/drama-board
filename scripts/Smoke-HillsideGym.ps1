param(
    [Parameter(Mandatory)][string]$Store,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$Configuration = 'Release'
)

# Engineering acceptance against an existing real-request store. Never invokes a model,
# edits stored data, or builds. Only ordinary CLI fork and Human commands mutate the store.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'HostJournalHelpers.ps1')
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
$journalPath = Join-Path $storePath 'host/events.jsonl'
foreach ($required in @($journalPath)) {
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
    Read-HostJournalRecords $Path
}
function Get-BranchProjectionHash([object[]]$Records, [string]$Branch) {
    $json = @($Records | Where-Object BranchId -CEQ $Branch | Sort-Object Sequence) | ConvertTo-Json -Depth 100 -Compress
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($utf8.GetBytes($json)))
}
function Invoke-GymCli([string]$Name, [string[]]$Arguments, [string[]]$Commands = @(), [switch]$ReadOnly) {
    $before = if ($ReadOnly) { Get-StoreHashes } else { $null }
    $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
    foreach ($argument in @($cliPath, '--store', $storePath) + $Arguments) { $start.ArgumentList.Add($argument) }
    $start.WorkingDirectory = $repoRoot
    $start.UseShellExecute = $false
    $null = $start.Environment.Remove('DRAMABOARD_GYM_FORBIDDEN_API_KEY')
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
    # Only parse the exporter's small identity markers, never prose/code fences in
    # a character's private materials. Payloads are rendered as readable sections.
    $fenceLength = 0
    foreach ($line in [IO.File]::ReadAllLines($Path)) {
        $fence = [regex]::Match($line, '^(`{3,})(.*)$')
        if ($fence.Success) {
            if ($fenceLength -eq 0) { $fenceLength = $fence.Groups[1].Length }
            elseif ($fence.Groups[1].Length -ge $fenceLength -and [string]::IsNullOrWhiteSpace($fence.Groups[2].Value)) { $fenceLength = 0 }
            continue
        }
        if ($fenceLength -ne 0) { continue }
        $header = [regex]::Match($line, '^<!-- host-record (.+) -->$')
        if ($header.Success) { $header.Groups[1].Value | ConvertFrom-Json }
    }
}
function Assert-Export([string]$Path, [string]$Branch, [string]$Actor, [object[]]$Expected) {
    $headers = @(Read-ExportHeaders $Path)
    $actualSequence = @($headers | ForEach-Object { $_.Sequence }) -join ','
    $expectedSequence = @($Expected | Sort-Object Sequence | ForEach-Object { $_.Sequence }) -join ','
    Assert-True ($actualSequence -ceq $expectedSequence) "Export $Path has incorrect ancestry/cut/actor records."
    Assert-True (@($headers | Where-Object { $null -ne $_.ActorId -and $_.ActorId -cne $Actor }).Count -eq 0) "Export $Path leaks another actor's record."
    Assert-True (@($headers | Where-Object BranchId -CEQ $Branch).Count -gt 0) 'Export branch identity is missing.'
}

try {
    $manifest.InitialStoreHashes = (Get-StoreHashes | ConvertFrom-Json)
    $recordsBefore = @(Read-EnvelopeRecords $journalPath)
    Assert-NoLegacyHostFiles $storePath
    $request = @($recordsBefore | Where-Object { $_.Kind -ceq 'model-request' -and $_.ActorId -ceq 'a' } | Sort-Object Sequence | Select-Object -First 1)
    Assert-True ($request.Count -eq 1) 'Store must contain an actual recorded request for actor a.'
    $request = $request[0]
    $source = $request.BranchId
    $boundary = $request.Payload.Boundary
    Assert-True ($boundary.ActorId -ceq 'a' -and $boundary.BranchId -ceq $source -and $boundary.TimeTicks -eq 28800000 -and
        $boundary.ContextSequence -eq 1 -and $boundary.OperationSequence -eq 0) 'Expected a first decision at 08:00, before actor a has acted.'
    Assert-True (@($recordsBefore | Where-Object { $_.Kind -ceq 'model-response' -and $_.Payload.RequestId -ceq $request.Payload.RequestId }).Count -gt 0) 'Source request has no recorded real response.'
    $beforeBranches = @(Read-Branches '01-branches-before')
    $sourceInfo = @($beforeBranches | Where-Object { $_.BranchId -ceq $source })
    Assert-True ($sourceInfo.Count -eq 1 -and $null -eq $sourceInfo[0].ForkedFromCommitId -and -not $sourceInfo[0].HasPendingEvent) 'Expected a completed, original source branch.'
    $sourceHead = $sourceInfo[0].HeadCommitId
    $sourceProjectionHash = Get-BranchProjectionHash $recordsBefore $source
    $sourceExportBefore = Export-Actor '02-source-a-before' $source 'a'
    $manifest.SourceBranch = $source
    $manifest.SourceHead = $sourceHead
    $manifest.SourceRequest = $request.Payload.RequestId
    $manifest.SourceBoundary = $boundary
    $manifest.SourceHostProjectionSha256 = $sourceProjectionHash

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

    # Configuration is validated eagerly, but credentials are resolved only for a model decision.
    # The child environment explicitly lacks this key, so accidental model routing fails locally.
    $connectionsPath = Join-Path $outputRoot 'model-calls-forbidden.connections.json'
    $connections = @{
        version = 1; defaultConnectionId = 'forbidden'
        catalog = @{ v = 3; connections = @(@{
            id = 'forbidden'; kind = 'openai-chat'; completionSurfaceId = 'openai-chat/strict'
            modelId = 'forbidden'; baseAddress = 'https://example.invalid/'
            apiKeyEnv = 'DRAMABOARD_GYM_FORBIDDEN_API_KEY'
        }) }
    }
    [IO.File]::WriteAllText($connectionsPath, ($connections | ConvertTo-Json -Depth 10), $utf8)
    $homePath = Join-Path $outputRoot 'home'
    New-Item -ItemType Directory -Path $homePath | Out-Null
    [IO.File]::WriteAllText((Join-Path $homePath 'playground.json'), '{"version":1}', $utf8)
    $controlsA = Join-Path $homePath 'a-human.json'
    $controlsB = Join-Path $homePath 'b-human.json'
    [IO.File]::WriteAllText($controlsA, '{"version":1,"overrides":{"a":{"kind":"human-console"}}}', $utf8)
    [IO.File]::WriteAllText($controlsB, '{"version":1,"overrides":{"b":{"kind":"human-console"}}}', $utf8)
    $common = @('--home', $homePath, '--branch', $child, '--gym', 'record', '--llm-connections', $connectionsPath)
    $firstAudit = Join-Path $outputRoot '06-child-handoff.audit.jsonl'
    $firstOutput = Invoke-GymCli '06-child-handoff' ($common + @('--character-controls', $controlsA, '--steps', '1', '--llm-log', $firstAudit)) @('handoff gym-child-private')
    $secondAudit = Join-Path $outputRoot '07-child-travel.audit.jsonl'
    # Successful a handoff yields fairly to b. The second process opens b's context,
    # then commits b's move; neither transition needs an LLM controller.
    $secondOutput = Invoke-GymCli '07-child-travel' ($common + @('--character-controls', $controlsB, '--steps', '2', '--llm-log', $secondAudit)) @('go e-r6')
    Assert-True ($firstOutput.Contains('山野镇住处') -and -not $secondOutput.Contains('gym-child-private')) 'Selected location was lost or actor b saw actor a private handoff.'
    Assert-True (-not (Test-Path -LiteralPath $firstAudit) -and -not (Test-Path -LiteralPath $secondAudit)) 'Human-only cold runs created raw model audits.'
    $childOperations = @(Read-EnvelopeRecords $journalPath | Where-Object BranchId -CEQ $child)
    $received = @($childOperations | Where-Object Kind -CEQ 'operation-received')
    $ordinary = @($received | Where-Object { -not $_.Payload.ToolName.StartsWith(':', [StringComparison]::Ordinal) })
    Assert-True (($ordinary.Payload.RawInput -join "`n") -ceq "handoff gym-child-private`ngo e-r6") 'Child ordinary commands were missing, duplicated, or associated with another branch.'
    Assert-True (($received.Payload.ActorId -join ',') -ceq 'a,b') 'Child operations were not exactly actor a handoff followed by actor b travel.'
    foreach ($operation in $ordinary) {
        $completed = @($childOperations | Where-Object { $_.Kind -ceq 'operation-completed' -and $_.Payload.OperationRecordId -ceq $operation.Payload.OperationRecordId })
        Assert-True ($completed.Count -eq 1 -and $completed[0].Payload.Completed.Status -eq 0 -and
            -not [string]::IsNullOrWhiteSpace($completed[0].Payload.Completed.DomainEvent)) 'Child command lacks exactly one successful domain result.'
    }
    $checks.Add('Two cold Human runs made zero provider bindings/requests; handoff survived and one travel command committed')

    $afterBranches = @(Read-Branches '08-branches-after-actions')
    $sourceAfter = @($afterBranches | Where-Object { $_.BranchId -ceq $source })
    $childAfter = @($afterBranches | Where-Object { $_.BranchId -ceq $child })
    Assert-True ($sourceAfter.Count -eq 1 -and $sourceAfter[0].HeadCommitId -ceq $sourceHead -and
        $sourceAfter[0].CompletedStateCommitId -ceq $sourceInfo[0].CompletedStateCommitId -and -not $sourceAfter[0].HasPendingEvent) 'Child activity changed source head.'
    Assert-True ($childAfter.Count -eq 1 -and $childAfter[0].HeadCommitId -cne $children[0].HeadCommitId -and -not $childAfter[0].HasPendingEvent) 'Child did not advance to a completed head.'
    $recordsAfter = @(Read-EnvelopeRecords $journalPath)
    Assert-True ((Get-BranchProjectionHash $recordsAfter $source) -ceq $sourceProjectionHash) 'Child activity changed source branch host projection.'
    $childRecords = @($recordsAfter | Where-Object { $_.BranchId -ceq $child })
    $runs = @($childRecords | Where-Object { $_.Kind -ceq 'run-start' })
    Assert-True ($runs.Count -eq 2 -and @($childRecords | Where-Object { $_.Kind -cin @('model-request', 'model-response', 'model-failure') }).Count -eq 0) 'Expected two child runs without model requests or responses.'
    $coldPerception = @($childRecords | Where-Object { $_.RunId -ceq $runs[1].RunId -and $_.Kind -ceq 'character-material' -and
        $_.ActorId -ceq 'a' } | Sort-Object Sequence | Select-Object -First 1)
    Assert-True ($coldPerception.Count -eq 1 -and ($coldPerception[0].Payload | ConvertTo-Json -Depth 100 -Compress).Contains('gym-child-private')) 'Second cold run did not restore actor a private handoff in its own material.'
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
        Assert-True (@($childRecords | Where-Object { $_.Kind -ceq 'run-end' -and $_.RunId -ceq $run.RunId -and $_.Payload.Reason -ceq 'step-budget' }).Count -eq 1) 'Child process did not stop at its step budget.'
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
    Assert-NoLegacyHostFiles $storePath
    $checks.Add('Source head, host projection, and export unchanged; child operations and private material isolated in one journal')
    $checks.Add('Every branches/export process left the entire store byte-for-byte unchanged')
    $manifest.ChildCompletedHead = $childAfter[0].CompletedStateCommitId
    $manifest.ChildRequestCount = 0
    $manifest.ChildOrdinaryCommands = $ordinary.Payload.RawInput
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
