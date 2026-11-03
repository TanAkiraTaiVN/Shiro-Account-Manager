[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$MapFile,

    [switch]$WhatIf
)

$ErrorActionPreference = "Stop"

# 1. Resolve MapFile
if (-not (Test-Path -Path $MapFile)) {
    Write-Error "MapFile not found: $MapFile"
    exit 1
}

$resolvedMapPath = (Resolve-Path -Path $MapFile).Path
Write-Host "Loading key mapping from: $resolvedMapPath"

$rawMap = Get-Content -Path $resolvedMapPath -Encoding UTF8 -Raw | ConvertFrom-Json
$mapping = [ordered]@{}
foreach ($prop in $rawMap.PSObject.Properties) {
    $mapping[$prop.Name] = $prop.Value
}

Write-Host "Total mappings to process: $($mapping.Count)"

# 2. Validate all new keys against regex ^[A-Z][A-Za-z0-9]*(\.[A-Z][A-Za-z0-9]*)+$
$keyPattern = '^[A-Z][A-Za-z0-9]*(\.[A-Z][A-Za-z0-9]*)+$'
$invalidKeys = @()
foreach ($entry in $mapping.GetEnumerator()) {
    $newKey = $entry.Value
    if ($newKey -notmatch $keyPattern) {
        $invalidKeys += "Old: '$($entry.Key)' -> New: '$newKey'"
    }
}

if ($invalidKeys.Count -gt 0) {
    Write-Error ("The following target keys do not match '$keyPattern':`n" + ($invalidKeys -join "`n"))
    exit 1
}

# 3. Locate repo root and localization files
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $scriptDir) { $scriptDir = Get-Location }
$repoRoot = (Resolve-Path -Path (Join-Path $scriptDir "..")).Path
$viPath = Join-Path $repoRoot "src/Shiro/Resources/Strings/vi.json"
$enPath = Join-Path $repoRoot "src/Shiro/Resources/Strings/en.json"

if (-not (Test-Path -Path $viPath) -or -not (Test-Path -Path $enPath)) {
    Write-Error "Localization files not found at expected paths: $viPath or $enPath"
    exit 1
}

# 4. Load JSON dictionaries
$viRaw = Get-Content -Path $viPath -Encoding UTF8 -Raw | ConvertFrom-Json
$enRaw = Get-Content -Path $enPath -Encoding UTF8 -Raw | ConvertFrom-Json

$viDict = [ordered]@{}
foreach ($prop in $viRaw.PSObject.Properties) { $viDict[$prop.Name] = $prop.Value }

$enDict = [ordered]@{}
foreach ($prop in $enRaw.PSObject.Properties) { $enDict[$prop.Name] = $prop.Value }

# 5. Check collision and consistency
$mergedGroups = @{} # newKey -> list of oldKeys
$collisions = @()

foreach ($entry in $mapping.GetEnumerator()) {
    $oldKey = $entry.Key
    $newKey = $entry.Value

    if (-not $viDict.Contains($oldKey) -or -not $enDict.Contains($oldKey)) {
        $collisions += "Source key '$oldKey' does not exist in vi.json or en.json."
        continue
    }

    $oldVi = $viDict[$oldKey]
    $oldEn = $enDict[$oldKey]

    # Check if newKey already exists in dictionaries and is NOT being renamed
    if ($viDict.Contains($newKey) -and -not $mapping.Contains($newKey)) {
        if ($viDict[$newKey] -ne $oldVi) {
            $collisions += "Target key '$newKey' already exists in vi.json with different value. Existing: '$($viDict[$newKey])', New: '$oldVi'"
        }
    }

    if ($enDict.Contains($newKey) -and -not $mapping.Contains($newKey)) {
        if ($enDict[$newKey] -ne $oldEn) {
            $collisions += "Target key '$newKey' already exists in en.json with different value. Existing: '$($enDict[$newKey])', New: '$oldEn'"
        }
    }

    if (-not $mergedGroups.ContainsKey($newKey)) {
        $mergedGroups[$newKey] = [System.Collections.Generic.List[string]]::new()
    }
    $mergedGroups[$newKey].Add($oldKey)
}

# Check merge consistency across old keys mapping to same new key
foreach ($newKey in $mergedGroups.Keys) {
    $oldKeys = $mergedGroups[$newKey]
    if ($oldKeys.Count -gt 1) {
        $firstOld = $oldKeys[0]
        $expectedVi = $viDict[$firstOld]
        $expectedEn = $enDict[$firstOld]

        for ($i = 1; $i -lt $oldKeys.Count; $i++) {
            $k = $oldKeys[$i]
            if ($viDict[$k] -ne $expectedVi) {
                $collisions += "Merge conflict for '$newKey': '$firstOld' (vi: '$expectedVi') vs '$k' (vi: '$($viDict[$k])')"
            }
            if ($enDict[$k] -ne $expectedEn) {
                $collisions += "Merge conflict for '$newKey': '$firstOld' (en: '$expectedEn') vs '$k' (en: '$($enDict[$k])')"
            }
        }
    }
}

if ($collisions.Count -gt 0) {
    Write-Error ("Collision or validation errors found:`n" + ($collisions -join "`n"))
    exit 1
}

Write-Host "Validation passed. Unique target keys: $($mergedGroups.Count) (Merges: $($mapping.Count - $mergedGroups.Count))"

# 6. Find all target files in src/Shiro
$srcDir = Join-Path $repoRoot "src/Shiro"
$sourceFiles = Get-ChildItem -Path $srcDir -Include "*.cs", "*.axaml" -Recurse | Where-Object {
    $_.FullName -notmatch '[\\/](bin|obj)[\\/]' -and $_.Name -notlike '*.g.cs'
}

$filesToModify = @()
foreach ($file in $sourceFiles) {
    $content = [System.IO.File]::ReadAllText($file.FullName, [System.Text.Encoding]::UTF8)
    $hasHit = $false
    foreach ($oldKey in $mapping.Keys) {
        if ($content.Contains($oldKey)) {
            $hasHit = $true
            break
        }
    }
    if ($hasHit) {
        $filesToModify += $file
    }
}

Write-Host "Source/AXAML files containing mapped keys: $($filesToModify.Count)"

if ($WhatIf) {
    Write-Host "[WhatIf] Would rewrite vi.json and en.json with $($mapping.Count) keys updated into $($mergedGroups.Count) keys."
    Write-Host "[WhatIf] Would update $($filesToModify.Count) source files:"
    foreach ($f in $filesToModify) {
        Write-Host "  - $($f.FullName.Substring($repoRoot.Length + 1))"
    }
    Write-Host "[WhatIf] No files modified."
    exit 0
}

# 7. Apply changes: rewrite vi.json and en.json
Write-Host "Updating vi.json and en.json..."

$allNewVi = [ordered]@{}
$allNewEn = [ordered]@{}

# First retain existing non-mapped keys, and insert new mapped keys
foreach ($k in $viDict.Keys) {
    if ($mapping.Contains($k)) {
        $target = $mapping[$k]
        if (-not $allNewVi.Contains($target)) {
            $allNewVi[$target] = $viDict[$k]
        }
    } else {
        $allNewVi[$k] = $viDict[$k]
    }
}

foreach ($k in $enDict.Keys) {
    if ($mapping.Contains($k)) {
        $target = $mapping[$k]
        if (-not $allNewEn.Contains($target)) {
            $allNewEn[$target] = $enDict[$k]
        }
    } else {
        $allNewEn[$k] = $enDict[$k]
    }
}

# Sort keys alphabetically for clean, deterministic JSON
$sortedVi = [ordered]@{}
foreach ($k in ($allNewVi.Keys | Sort-Object)) {
    $sortedVi[$k] = $allNewVi[$k]
}

$sortedEn = [ordered]@{}
foreach ($k in ($allNewEn.Keys | Sort-Object)) {
    $sortedEn[$k] = $allNewEn[$k]
}

function Format-JsonDictionary([System.Collections.IDictionary]$dict) {
    $sb = [System.Text.StringBuilder]::new()
    [void]$sb.AppendLine("{")
    $keys = @($dict.Keys)
    for ($i = 0; $i -lt $keys.Count; $i++) {
        $k = $keys[$i]
        $v = $dict[$k]
        $escapedVal = $v.Replace('\', '\\').Replace('"', '\"').Replace("`r", '\r').Replace("`n", '\n').Replace("`t", '\t')
        $comma = if ($i -lt $keys.Count - 1) { "," } else { "" }
        [void]$sb.AppendLine("  `"$k`": `"$escapedVal`"$comma")
    }
    [void]$sb.Append("}")
    return $sb.ToString()
}

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($viPath, (Format-JsonDictionary $sortedVi), $utf8NoBom)
[System.IO.File]::WriteAllText($enPath, (Format-JsonDictionary $sortedEn), $utf8NoBom)
Write-Host "Updated vi.json and en.json successfully."

# 8. Update source files
$updatedCount = 0
foreach ($file in $filesToModify) {
    $content = [System.IO.File]::ReadAllText($file.FullName, [System.Text.Encoding]::UTF8)
    $modified = $false

    foreach ($entry in $mapping.GetEnumerator()) {
        $oldKey = $entry.Key
        $newKey = $entry.Value

        # Replace in C# ("oldKey" -> "newKey")
        $oldQuoted = "`"$oldKey`""
        $newQuoted = "`"$newKey`""
        if ($content.Contains($oldQuoted)) {
            $content = $content.Replace($oldQuoted, $newQuoted)
            $modified = $true
        }

        # Replace in AXAML ({DynamicResource oldKey} -> {DynamicResource newKey})
        $oldDyn = "{DynamicResource $oldKey}"
        $newDyn = "{DynamicResource $newKey}"
        if ($content.Contains($oldDyn)) {
            $content = $content.Replace($oldDyn, $newDyn)
            $modified = $true
        }
    }

    if ($modified) {
        [System.IO.File]::WriteAllText($file.FullName, $content, $utf8NoBom)
        $updatedCount++
    }
}

Write-Host "Updated $updatedCount source files successfully."
