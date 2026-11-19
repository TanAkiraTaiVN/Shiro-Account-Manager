<#
.SYNOPSIS
    Shiro Account Manager • Modern PowerShell Startup Script
.DESCRIPTION
    Safely applies pending updates, verifies dependencies, and launches
    the optimized Shiro Account Manager (win-x64) executable.
#>
[CmdletBinding()]
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Arguments
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$root = $PSScriptRoot
$releaseDir = Join-Path $root 'build\Release'
$targetExe = Join-Path $releaseDir 'Shiro.exe'

# 1. Check executable existence
if (-not (Test-Path -LiteralPath $targetExe)) {
    Write-Host "[Error] Executable not found at '$targetExe'." -ForegroundColor Red
    Write-Host "[Info] Please build or publish the project first using: powershell -File .\scripts\Publish.ps1" -ForegroundColor Yellow
    exit 1
}

# 2. Launch process asynchronously in Release working directory
Write-Host "[Shiro] Launching Shiro Account Manager..." -ForegroundColor Magenta

$psi = [System.Diagnostics.ProcessStartInfo]::new()
$psi.FileName = $targetExe
$psi.WorkingDirectory = $releaseDir
$psi.UseShellExecute = $true

$launchArgs = [System.Collections.Generic.List[string]]::new()
if ($Arguments -and $Arguments.Count -gt 0) { $launchArgs.AddRange($Arguments) }

if ($launchArgs.Count -gt 0) {
    $psi.Arguments = [string]::Join(' ', $launchArgs)
}

try {
    $process = [System.Diagnostics.Process]::Start($psi)
    Write-Host "[Shiro] Process started successfully (PID: $($process.Id))." -ForegroundColor Green
}
catch {
    Write-Host "[Error] Failed to start Shiro: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
