[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$OutputDir = 'Release',
    [switch]$Aot
)

$workspace = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $workspace 'src\Shiro\Shiro.csproj'
$buildRoot = Join-Path $workspace 'build'
$output = [System.IO.Path]::GetFullPath((Join-Path $buildRoot $OutputDir))

function Assert-ExitCode([string]$step) {
    if ($LASTEXITCODE -ne 0) {
        throw "$step failed with exit code $LASTEXITCODE."
    }
}

if (-not (Test-Path -LiteralPath $project)) {
    throw "Project file was not found: $project"
}

if (Test-Path -LiteralPath (Join-Path $output 'portable.mode')) {
    throw "Refusing to clean $output because portable.mode is present. Move portable data first or publish to another folder."
}

if (Test-Path -LiteralPath $output) {
    $recovery = Join-Path $buildRoot ('archive\release-data-before-publish-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    foreach ($name in @(
        'AccountData.json', 'AccountData.json.backup', 'AccountData.json.bak', 'AccountRuntimeState.json',
        'RecentGames.json', 'RAMSettings.json', 'RAMSettings.ini', 'RAMTheme.ini', 'proxies.txt',
        'BrowserConfig.json', 'CustomClientAppSettings.json', 'portable.mode', 'vault.key'
    )) {
        $source = Join-Path $output $name
        if (Test-Path -LiteralPath $source) {
            if (-not (Test-Path -LiteralPath $recovery)) {
                New-Item -ItemType Directory -Path $recovery -Force | Out-Null
            }
            Copy-Item -LiteralPath $source -Destination (Join-Path $recovery $name) -Force
        }
    }

    try {
        [System.IO.Directory]::Delete($output, $true)
    }
    catch {
        throw "Could not clean the publish output '$output'. Close the running Shiro executable and try again. $($_.Exception.Message)"
    }
}

New-Item -ItemType Directory -Path $output -Force | Out-Null

Write-Host "Restoring locked packages for $Runtime..."
& dotnet restore $project --runtime $Runtime --locked-mode /p:SelfContained=true
Assert-ExitCode 'dotnet restore'

$modeDesc = if ($Aot) { "(Native AOT)" } else { "(ReadyToRun SingleFile)" }
Write-Host "Publishing $Configuration/$Runtime to $output $modeDesc..."

$publishArgs = @(
    "publish", $project,
    "--configuration", $Configuration,
    "--runtime", $Runtime,
    "--self-contained", "true",
    "--output", $output,
    "--no-restore",
    "/p:PublishDocumentationFiles=false"
)

if ($Aot) {
    $publishArgs += "/p:PublishAot=true"
} else {
    $publishArgs += "/p:PublishSingleFile=true"
    $publishArgs += "/p:IncludeNativeLibrariesForSelfExtract=true"
    $publishArgs += "/p:EnableCompressionInSingleFile=true"
}

& dotnet @publishArgs
Assert-ExitCode 'dotnet publish'

Get-ChildItem -LiteralPath $output -File -Filter '*.xml' | Remove-Item -Force -ErrorAction SilentlyContinue
Get-ChildItem -LiteralPath $output -File -Filter '*.manifest' | Remove-Item -Force -ErrorAction SilentlyContinue
Get-ChildItem -LiteralPath $output -File -Filter '*.pdb' | Remove-Item -Force -ErrorAction SilentlyContinue
foreach ($subDir in @('Nexus', 'Resources')) {
    $dirPath = Join-Path $output $subDir
    if (Test-Path -LiteralPath $dirPath) {
        Remove-Item -LiteralPath $dirPath -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$legacyFolders = @('Release-Latest', 'obj', 'artifacts')
foreach ($folder in $legacyFolders) {
    $legacyPath = Join-Path $buildRoot $folder
    if (Test-Path -LiteralPath $legacyPath) {
        try {
            Remove-Item -LiteralPath $legacyPath -Recurse -Force -ErrorAction SilentlyContinue
        } catch { }
    }
}

$shiroExe = Join-Path $output 'Shiro.exe'
if (-not (Test-Path -LiteralPath $shiroExe)) {
    throw "Expected publish output to contain 'Shiro.exe' in: $output"
}

$executables = @(Get-ChildItem -LiteralPath $output -Filter '*.exe' -File)
if ($executables.Count -ne 1) {
    throw "Expected exactly one distributable EXE (Shiro.exe), found: $($executables.Name -join ', ')"
}

$mutableNames = @(
    'AccountData.json', 'AccountData.json.backup', 'AccountRuntimeState.json', 'RecentGames.json',
    'FavoriteGames.json', 'AccountControlData.json', 'Nexus.lua',
    'RAMSettings.json', 'RAMSettings.ini', 'RAMTheme.ini', 'proxies.txt', 'BrowserConfig.json',
    'CustomClientAppSettings.json', 'no773fix.txt',
    'NoEncryption.IUnderstandTheRisks.iautamor', 'log.txt', 'Cache', 'Logs',
    'AccountDumps', 'NexusOutput', 'BrowserSessions', 'portable.mode', 'vault.key'
)
$unexpected = foreach ($name in $mutableNames) {
    $candidate = Join-Path $output $name
    if (Test-Path -LiteralPath $candidate) { $candidate }
}
if ($unexpected) {
    throw "Publish output contains mutable runtime data: $($unexpected -join ', ')"
}

Write-Host "Publish complete: $($executables[0].FullName)"
Write-Host "Distributable EXE count: $($executables.Count) ($($executables[0].Name))"
