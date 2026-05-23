param(
    [string] $OutputZip = "ZERO_PARADES_VoiceOverride_Installer.zip",
    [string] $BuiltPlugin = "src/bin/Release/net6.0/ZeroParadesVoiceOverride.dll"
)

$ErrorActionPreference = "Stop"

$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$OutputZipPath = if ([System.IO.Path]::IsPathRooted($OutputZip)) {
    $OutputZip
} else {
    Join-Path $RepoRoot $OutputZip
}
$BuiltPluginPath = if ([System.IO.Path]::IsPathRooted($BuiltPlugin)) {
    $BuiltPlugin
} else {
    Join-Path $RepoRoot $BuiltPlugin
}

if (-not (Test-Path -LiteralPath $BuiltPluginPath -PathType Leaf)) {
    throw "Built plugin not found: $BuiltPluginPath"
}

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

if (Test-Path -LiteralPath $OutputZipPath) {
    Remove-Item -LiteralPath $OutputZipPath -Force
}

$zip = [System.IO.Compression.ZipFile]::Open($OutputZipPath, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    function Add-Entry {
        param(
            [string] $Source,
            [string] $Entry
        )
        if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) {
            throw "Missing release file: $Source"
        }
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $script:zip,
            $Source,
            $Entry,
            [System.IO.Compression.CompressionLevel]::Optimal
        ) | Out-Null
    }

    Add-Entry (Join-Path $RepoRoot "installer/Install.bat") "Install.bat"
    Add-Entry (Join-Path $RepoRoot "installer/install.ps1") "install.ps1"
    Add-Entry (Join-Path $RepoRoot "installer/installer-config.json") "installer-config.json"
    Add-Entry (Join-Path $RepoRoot "installer/INSTALLER_README.txt") "README.txt"
    Add-Entry $BuiltPluginPath "BepInEx/plugins/ZeroParadesVoiceOverride.dll"
    Add-Entry (Join-Path $RepoRoot "config/spore.zeroparades.voiceoverride.cfg") "BepInEx/config/spore.zeroparades.voiceoverride.cfg"
} finally {
    $zip.Dispose()
}

Write-Host "Wrote $OutputZipPath"
