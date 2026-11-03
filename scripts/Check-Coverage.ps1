param(
    [string]$CoverageDir = "coverage",
    [double]$MinOverall = 51.3,
    [double]$MinCore = 90.0
)

$ErrorActionPreference = "Stop"

$xmlFiles = Get-ChildItem -Path $CoverageDir -Recurse -Filter "coverage.cobertura.xml" -ErrorAction SilentlyContinue
if (-not $xmlFiles) {
    Write-Error "No coverage.cobertura.xml found in $CoverageDir"
    exit 1
}

$xmlFile = $xmlFiles | Sort-Object LastWriteTime -Descending | Select-Object -First 1
Write-Host "Evaluating coverage from $($xmlFile.FullName)..." -ForegroundColor Cyan

[xml]$doc = Get-Content $xmlFile.FullName
$overallRate = [double]$doc.coverage.'line-rate' * 100.0
$branchRate = [double]$doc.coverage.'branch-rate' * 100.0

Write-Host "Overall Line Coverage:   $("{0:N2}" -f $overallRate)%" -ForegroundColor Yellow
Write-Host "Overall Branch Coverage: $("{0:N2}" -f $branchRate)%" -ForegroundColor Yellow

# Calculate Core package/classes coverage (Cryptography, VaultKey, AccountDataSerializer, AccountStore, StoragePaths)
$classes = $doc.coverage.packages.package.classes.class
$coreClasses = $classes | Where-Object {
    $_.filename -match 'Cryptography\.cs|VaultKey\.cs|AccountDataSerializer\.cs|AccountStore\.cs|StoragePaths\.cs|WebAuthPolicy\.cs|LogRedactor\.cs|AccountFileDecoder\.cs'
}

$coreTotalLines = 0
$coreCoveredLines = 0
if ($coreClasses) {
    foreach ($cls in $coreClasses) {
        $lines = $cls.lines.line
        if ($lines) {
            $coreTotalLines += $lines.Count
            $coreCoveredLines += ($lines | Where-Object { [int]$_.hits -gt 0 }).Count
        }
    }
}

$coreRate = 0.0
if ($coreTotalLines -gt 0) {
    $coreRate = ($coreCoveredLines / $coreTotalLines) * 100.0
    Write-Host "Core Security/Persistence Coverage: $("{0:N2}" -f $coreRate)% ($coreCoveredLines/$coreTotalLines lines)" -ForegroundColor Yellow
}

$failed = $false
if ($overallRate -lt $MinOverall) {
    Write-Host "FAILED: Overall coverage $("{0:N2}" -f $overallRate)% is below required minimum $MinOverall%" -ForegroundColor Red
    $failed = $true
}

if ($MinCore -gt 0 -and $coreRate -lt $MinCore) {
    Write-Host "FAILED: Core coverage $("{0:N2}" -f $coreRate)% is below required core minimum $MinCore%" -ForegroundColor Red
    $failed = $true
}

if ($failed) {
    exit 1
}

Write-Host "SUCCESS: Coverage gates passed." -ForegroundColor Green
exit 0
