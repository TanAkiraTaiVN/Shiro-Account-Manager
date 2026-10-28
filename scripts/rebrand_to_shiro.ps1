# Rebrand namespaces from RBX_Alt_Manager to Shiro across all source and test files

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
Write-Host "Repo root: $root"

$extensions = @("*.cs", "*.axaml", "*.csproj")
$files = Get-ChildItem -Path $root -Recurse -Include $extensions | Where-Object {
    $_.FullName -notmatch '\\(bin|obj|build|brain|\.git)\\'
}

$replaceCount = 0

foreach ($file in $files) {
    $content = [System.IO.File]::ReadAllText($file.FullName, [System.Text.Encoding]::UTF8)
    $original = $content
    
    # Replace namespaces, usings, type references, and xaml namespaces
    $content = $content.Replace("RBX_Alt_Manager", "Shiro")
    
    if ($content -ne $original) {
        [System.IO.File]::WriteAllText($file.FullName, $content, [System.Text.Encoding]::UTF8)
        $replaceCount++
        Write-Host "Updated: $($file.FullName.Substring($root.Path.Length + 1))"
    }
}

Write-Host "Total files updated: $replaceCount"
