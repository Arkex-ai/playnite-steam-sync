Param(
    [string]$Configuration = 'Release',
    [string]$PlayniteVersion = '10.63',
    [string]$ToolboxPath = ''
)

$ErrorActionPreference = 'Stop'

$proj = Join-Path $PSScriptRoot '..\src\SteamShortcutsImporter\SteamShortcutsImporter.csproj'
dotnet build $proj -c $Configuration

$outDir = Join-Path $PSScriptRoot "..\src\SteamShortcutsImporter\bin\$Configuration\net462"
if (!(Test-Path $outDir)) { throw "Build output not found: $outDir" }

Copy-Item (Join-Path $PSScriptRoot '..\src\SteamShortcutsImporter\extension.yaml') $outDir -Force
if (Test-Path (Join-Path $PSScriptRoot '..\src\SteamShortcutsImporter\icon.png')) {
    Copy-Item (Join-Path $PSScriptRoot '..\src\SteamShortcutsImporter\icon.png') $outDir -Force
}

$toolboxCandidates = @()
if (![string]::IsNullOrWhiteSpace($ToolboxPath)) {
    $toolboxCandidates += $ToolboxPath
}
$toolboxCandidates += @(
    (Join-Path $env:LOCALAPPDATA 'Playnite\Toolbox.exe'),
    (Join-Path $env:ProgramFiles 'Playnite\Toolbox.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Playnite\Toolbox.exe'),
    (Join-Path $env:TEMP "Playnite-$PlayniteVersion\Toolbox.exe")
)

$toolbox = $toolboxCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (!$toolbox) {
    throw "Toolbox.exe not found. Install Playnite $PlayniteVersion or pass -ToolboxPath with a compatible Toolbox.exe."
}

Push-Location $outDir
try {
    $packOutput = & $toolbox pack $outDir $PSScriptRoot
    $packOutput | ForEach-Object { Write-Host $_ }
    $packedLine = $packOutput | Where-Object { $_ -match 'successfully packed as "(.+\.pext)"' } | Select-Object -First 1
    $packMatch = [regex]::Match([string]$packedLine, 'successfully packed as "(.+\.pext)"')
    if (!$packMatch.Success) {
        throw "Toolbox did not report the generated .pext path."
    }
    $packedPath = $packMatch.Groups[1].Value
}
finally {
    Pop-Location
}

Write-Host "Packed: " $packedPath

