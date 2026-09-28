param([switch]$Start)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
if (!(Get-Command git -ErrorAction SilentlyContinue)) { throw 'Git is required.' }
$port = if ($env:GONIO_PORT) { [int]$env:GONIO_PORT } else { 4173 }
if (Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue) { throw 'Stop the current application before updating source.' }
$changes = & git -C $repo status --porcelain
if ($LASTEXITCODE -ne 0) { throw 'This folder is not a Git checkout.' }
if ($changes) { throw 'Local source changes exist. Preserve them before updating; no reset or overwrite was performed.' }
& git -C $repo pull --ff-only origin main
if ($LASTEXITCODE -ne 0) { throw 'Update could not be fast-forwarded. No forced reset was performed.' }
& (Join-Path $PSScriptRoot 'Build-Local.ps1') -Start:$Start
