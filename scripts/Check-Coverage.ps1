param(
    [string]$CoverageDir = "coverage",
    [double]$MinOverall = 75.0,
    [double]$MinBranch = 60.0,
    [double]$MinCore = 92.0,
    [string]$FloorsFile = "tests/coverage-floors.json"
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

# Calculate Core package/classes coverage (19 security, persistence, HBA, launch-guard & shutdown files)
$classes = $doc.coverage.packages.package.classes.class
$coreClasses = $classes | Where-Object {
    $_.filename -match '(?:^|[\\/\.])(Cryptography\.cs|VaultKey\.cs|AccountDataSerializer\.cs|AccountStore\.cs|StoragePaths\.cs|WebAuthPolicy\.cs|LogRedactor\.cs|AccountFileDecoder\.cs|CryptoHelper\.cs|VaultExportService\.cs|VaultKeyRecoveryService\.cs|InMemorySecretProtector\.cs|RobloxHbaService\.cs|RobloxHbaService\.Binding\.cs|RobloxHbaService\.Network\.cs|RobloxHbaService\.BrowserHooks\.cs|RobloxDirectLauncher\.CookieGuard\.cs|RobloxDirectLauncher\.DeElevation\.cs|ShutdownCoordinator\.cs)$'
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

if ($MinBranch -gt 0 -and $branchRate -lt $MinBranch) {
    Write-Host "FAILED: Branch coverage $("{0:N2}" -f $branchRate)% is below required minimum $MinBranch%" -ForegroundColor Red
    $failed = $true
}

if ($MinCore -gt 0 -and $coreRate -lt $MinCore) {
    Write-Host "FAILED: Core coverage $("{0:N2}" -f $coreRate)% is below required core minimum $MinCore%" -ForegroundColor Red
    $failed = $true
}

if ($FloorsFile -and (Test-Path $FloorsFile)) {
    $byFileBranch = @{}
    foreach ($cls in $classes) {
        $rawPath = ([string]$cls.filename) -replace '\\', '/'
        $relPath = if ($rawPath -match 'src/Shiro/(.+)$') { $Matches[1] } else { $rawPath }
        if (-not $byFileBranch.ContainsKey($relPath)) {
            $byFileBranch[$relPath] = @{ Cov = 0; Tot = 0 }
        }
        $lines = $cls.lines.line
        if (-not $lines) { continue }
        foreach ($line in $lines) {
            if (([string]$line.branch) -eq "True" -and ([string]$line.'condition-coverage' -match '\((\d+)/(\d+)\)')) {
                $byFileBranch[$relPath].Cov += [int]$Matches[1]
                $byFileBranch[$relPath].Tot += [int]$Matches[2]
            }
        }
    }

    $floorsJson = Get-Content $FloorsFile -Raw | ConvertFrom-Json
    foreach ($prop in $floorsJson.PSObject.Properties) {
        $fileKey = $prop.Name
        $minFileBranch = [double]$prop.Value.branch
        if ($byFileBranch.ContainsKey($fileKey) -and $byFileBranch[$fileKey].Tot -gt 0) {
            $actualFileBranch = ($byFileBranch[$fileKey].Cov * 100.0) / $byFileBranch[$fileKey].Tot
            if ($actualFileBranch + 1e-9 -lt $minFileBranch) {
                Write-Host ("FAILED: {0} branch {1:N1}% < {2:N1}%" -f $fileKey, $actualFileBranch, $minFileBranch) -ForegroundColor Red
                $failed = $true
            }
        } else {
            Write-Host ("FAILED: {0} not found in coverage report" -f $fileKey) -ForegroundColor Red
            $failed = $true
        }
    }
}

if ($failed) {
    exit 1
}

Write-Host "SUCCESS: Coverage gates passed." -ForegroundColor Green
exit 0
