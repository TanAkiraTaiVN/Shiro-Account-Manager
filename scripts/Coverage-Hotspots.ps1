param(
    [string]$CoverageDir = "coverage",
    [int]$Top = 25,
    [string]$File = "",
    [switch]$ShowUncovered
)

$ErrorActionPreference = "Stop"

$xmlFiles = Get-ChildItem -Path $CoverageDir -Recurse -Filter "coverage.cobertura.xml" -ErrorAction SilentlyContinue
if (-not $xmlFiles) {
    Write-Error "No coverage.cobertura.xml found in $CoverageDir"
    exit 1
}

$xmlFile = $xmlFiles | Sort-Object LastWriteTime -Descending | Select-Object -First 1
Write-Host "Reading coverage from $($xmlFile.FullName)" -ForegroundColor Cyan

[xml]$doc = Get-Content $xmlFile.FullName
$classes = $doc.coverage.packages.package.classes.class

$byFile = @{}

foreach ($cls in $classes) {
    $rawPath = ([string]$cls.filename) -replace '\\', '/'
    if ($rawPath -match 'src/Shiro/(.+)$') {
        $relPath = $Matches[1]
    } else {
        $relPath = $rawPath
    }

    if ($File -and ($relPath -notlike "*$File*")) {
        continue
    }

    if (-not $byFile.ContainsKey($relPath)) {
        $byFile[$relPath] = [PSCustomObject]@{
            File      = $relPath
            TotL      = 0
            MissL     = 0
            TotB      = 0
            MissB     = 0
            Uncovered = [System.Collections.Generic.List[string]]::new()
        }
    }

    $entry = $byFile[$relPath]
    $lines = $cls.lines.line
    if (-not $lines) { continue }

    foreach ($line in $lines) {
        $entry.TotL++
        $hits = [int]$line.hits
        $lineNum = [int]$line.number
        if ($hits -eq 0) {
            $entry.MissL++
        }

        $isBranch = ([string]$line.branch) -eq "True"
        $condCov = [string]$line.'condition-coverage'
        $covB = 0
        $totB = 0
        if ($isBranch -and ($condCov -match '\((\d+)/(\d+)\)')) {
            $covB = [int]$Matches[1]
            $totB = [int]$Matches[2]
            $entry.TotB += $totB
            $entry.MissB += ($totB - $covB)
        }

        if ($ShowUncovered) {
            if ($hits -eq 0) {
                $entry.Uncovered.Add("L$lineNum (hits=0)")
            } elseif ($isBranch -and ($covB -lt $totB)) {
                $entry.Uncovered.Add("L$lineNum ($condCov)")
            }
        }
    }
}

$rows = $byFile.Values | Sort-Object -Property MissB, MissL -Descending | Select-Object -First $Top

foreach ($row in $rows) {
    $linePct = if ($row.TotL -gt 0) { (($row.TotL - $row.MissL) * 100.0) / $row.TotL } else { 100.0 }
    $branchPct = if ($row.TotB -gt 0) { (($row.TotB - $row.MissB) * 100.0) / $row.TotB } else { 100.0 }
    Write-Host ("{0,-52} MissL={1,4}/{2,-4} ({3,5:N1}%)  MissB={4,4}/{5,-4} ({6,5:N1}%)" -f `
        $row.File, $row.MissL, $row.TotL, $linePct, $row.MissB, $row.TotB, $branchPct)

    if ($ShowUncovered -and $row.Uncovered.Count -gt 0) {
        $joined = ($row.Uncovered | Select-Object -Unique) -join ", "
        Write-Host "  Uncovered: $joined" -ForegroundColor DarkGray
    }
}
