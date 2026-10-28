# Dev helper: (re)build and (re)start the overlay, elevated.
#
# The running instance holds locks on bin\Release (an elevated process a
# plain shell cannot stop), so updating to a fresh build is otherwise a
# three-step dance: tray Exit, rebuild, relaunch. This script does all
# three — it elevates itself first (raw sockets / Sysmon log / firewall
# inventory need admin anyway), stops any running instance, rebuilds
# Release, and starts the new one.
#
#   .\run.ps1            rebuild + restart
#   .\run.ps1 -NoBuild   restart the current build only
param([switch]$NoBuild)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Start-Process powershell -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`"" -WorkingDirectory $root
    exit
}

Set-Location $root
Get-Process AutoCommand.Matrix -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800   # let the file locks release

if (-not $NoBuild) {
    dotnet build AutoCommand.Matrix.csproj -c Release
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

Start-Process (Join-Path $root 'bin\Release\net10.0-windows\AutoCommand.Matrix.exe') -WorkingDirectory $root
Write-Host "AutoCommand.Matrix running."
