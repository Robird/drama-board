[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$webRoot = Join-Path $repoRoot 'src/WebUI'
$publishRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts/server'))
$expectedRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts/server'))
if ($publishRoot -ne $expectedRoot -or -not $publishRoot.StartsWith($repoRoot + [IO.Path]::DirectorySeparatorChar)) {
    throw "Unexpected publish directory: $publishRoot"
}

& npm --prefix $webRoot ci
if ($LASTEXITCODE -ne 0) { throw 'npm ci failed.' }
& npm --prefix $webRoot run build
if ($LASTEXITCODE -ne 0) { throw 'Frontend build failed.' }
$entry = Join-Path $webRoot 'dist/index.html'
if (-not (Test-Path -LiteralPath $entry -PathType Leaf) -or (Get-Item -LiteralPath $entry).Length -eq 0) {
    throw 'Frontend build did not produce a nonempty dist/index.html.'
}
if (Test-Path -LiteralPath $publishRoot) {
    if ((Get-Item -LiteralPath $publishRoot).Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw 'Publish directory must not be a filesystem link.'
    }
    Remove-Item -LiteralPath $publishRoot -Recurse -Force
}
& dotnet publish (Join-Path $repoRoot 'src/Server/Server.csproj') -c Release -o $publishRoot -m:1 -warnaserror -p:DurableGraphSchemaHistoryMode=Verify
if ($LASTEXITCODE -ne 0) { throw 'Server publish failed.' }
$publishedEntry = Join-Path $publishRoot 'wwwroot/index.html'
if (-not (Test-Path -LiteralPath $publishedEntry -PathType Leaf)) { throw 'Published webpage is missing.' }
Write-Output "Published server and web assets: $publishRoot"
