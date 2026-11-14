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
$updateDir = Join-Path $root 'build\Release-Update'
$targetExe = Join-Path $releaseDir 'Shiro.exe'
$updateExe = Join-Path $updateDir 'Shiro.exe'

# 1. Apply pending auto-update if present
if (Test-Path -LiteralPath $releaseDir) {
    Get-ChildItem -LiteralPath $releaseDir -Filter '*.old*' -File -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
}
if (Test-Path -LiteralPath $updateExe) {
    Write-Host "[Update] Applying pending Shiro update..." -ForegroundColor Cyan
    try {
        if (-not (Test-Path -LiteralPath $releaseDir)) {
            New-Item -ItemType Directory -Path $releaseDir -Force | Out-Null
        }
        Move-Item -LiteralPath $updateExe -Destination $targetExe -Force
        Remove-Item -LiteralPath $updateDir -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "[Update] Successfully updated to latest version." -ForegroundColor Green
    }
    catch {
        Write-Warning "[Update] Could not apply update: $($_.Exception.Message)"
    }
}

# 2. Check executable existence
if (-not (Test-Path -LiteralPath $targetExe)) {
    Write-Host "[Error] Executable not found at '$targetExe'." -ForegroundColor Red
    Write-Host "[Info] Please build or publish the project first using: powershell -File .\scripts\Publish.ps1" -ForegroundColor Yellow
    exit 1
}

# 3. Launch process asynchronously in Release working directory
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
