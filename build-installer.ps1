<#
.SYNOPSIS
  Publishes Twelve Testers Tray and builds its installer.
.EXAMPLE
  .\build-installer.ps1            # artifacts\installer\TwelveTestersTray-Setup-<version>.exe
  .\build-installer.ps1 -Install   # ...and installs it silently on this PC (per-user)
#>
param([switch]$Install)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'TwelveTestersTray\TwelveTestersTray.csproj'
$publish = Join-Path $root 'artifacts\publish'

$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup' }

[xml]$xml = Get-Content $project
$version = ($xml.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
if (-not $version) { $version = '1.0.0' }

Write-Host "== Publishing $version" -ForegroundColor Cyan
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish $project -c Release -r win-x64 --self-contained false -p:PublishReadyToRun=true -o $publish -nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

Write-Host '== Building installer' -ForegroundColor Cyan
& $iscc "/DAppVersion=$version" (Join-Path $root 'installer\TwelveTestersTray.iss')
if ($LASTEXITCODE -ne 0) { throw 'ISCC failed.' }

$setup = Join-Path $root "artifacts\installer\TwelveTestersTray-Setup-$version.exe"
Write-Host "Installer: $setup" -ForegroundColor Green

if ($Install) {
    Write-Host '== Installing on this PC' -ForegroundColor Cyan
    Get-Process TwelveTestersTray -ErrorAction SilentlyContinue | Stop-Process -Force
    $p = Start-Process $setup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/TASKS=startup' -PassThru -Wait
    if ($p.ExitCode -ne 0) { throw "Setup exited with code $($p.ExitCode)." }
    $exe = Join-Path $env:LOCALAPPDATA 'Programs\Twelve Testers Tray\TwelveTestersTray.exe'
    Write-Host "Installed: $exe" -ForegroundColor Green
    Start-Process $exe
}
