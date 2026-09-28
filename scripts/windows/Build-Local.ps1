param([switch]$Start)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
if (!(Get-Command dotnet -ErrorAction SilentlyContinue)) { throw '.NET 8 SDK is required. Install the official Microsoft SDK, then retry.' }
$sdks = & dotnet --list-sdks
if (!($sdks | Where-Object { $_ -match '^8\.' })) { throw '.NET 8 SDK is required.' }
$port = if ($env:GONIO_PORT) { [int]$env:GONIO_PORT } else { 4173 }
if (Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue) { throw "Port $port is in use. Stop the current application before rebuilding. No process was terminated." }
$install = Join-Path $env:LOCALAPPDATA 'GonioWeb\local-builds'
New-Item -ItemType Directory -Force $install | Out-Null
$build = Join-Path $install (Get-Date -Format 'yyyyMMdd-HHmmss-ffff')
& dotnet publish (Join-Path $repo 'apps/device-bridge/DeviceBridge.csproj') -c Release --self-contained false -p:UseAppHost=false -o $build
if ($LASTEXITCODE -ne 0) { throw 'Build failed. The previous build and measurement data are preserved.' }
& dotnet (Join-Path $build 'GonioWeb.dll') --self-test
if ($LASTEXITCODE -ne 0) { throw 'Self-tests failed. The previous build remains selected.' }
$dotnet = (Get-Command dotnet).Source
$command = "@echo off`r`nchcp 65001 >nul`r`nsetlocal`r`nset GONIO_SOURCE_BUILD=1`r`n`"$dotnet`" `"$(Join-Path $build 'GonioWeb.dll')`" --open`r`nif errorlevel 1 pause`r`n"
$temp = Join-Path $install 'Start-Gonio.cmd.tmp'
[IO.File]::WriteAllText($temp, $command, (New-Object Text.UTF8Encoding($false)))
Move-Item -Force $temp (Join-Path $install 'Start-Gonio.cmd')
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Desktop')) 'Gonio Web Local.lnk'))
$link.TargetPath = Join-Path $install 'Start-Gonio.cmd'
$link.WorkingDirectory = $install
$link.Save()
Write-Host 'Built successfully. Start from Gonio Web Local. Raw logs and browser data are preserved.'
if ($Start) { Start-Process (Join-Path $install 'Start-Gonio.cmd') }
