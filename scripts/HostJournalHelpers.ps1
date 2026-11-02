# Read committed JSONL envelopes for scripted acceptance. This is a projection,
# never an operation journal writer or a substitute for the CLI's validation.
function Read-HostJournalRecords([string]$Path) {
    foreach ($line in [IO.File]::ReadAllLines($Path)) {
        if (-not [string]::IsNullOrWhiteSpace($line)) {
            ($line | ConvertFrom-Json).Payload | ConvertFrom-Json
        }
    }
}

function Assert-NoLegacyHostFiles([string]$Store) {
    foreach ($relative in @('operations.jsonl', 'branch-operations', 'gym/events.jsonl', 'gym/events.v2.jsonl')) {
        if (Test-Path -LiteralPath (Join-Path $Store $relative)) {
            throw "Unexpected legacy host output: $relative"
        }
    }
}
