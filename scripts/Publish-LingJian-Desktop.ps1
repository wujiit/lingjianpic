param(
    [ValidateSet("win-x64", "win-arm64", "osx-x64", "osx-arm64", "all")]
    [string]$Runtime = "win-x64",
    [ValidateSet("folder", "single-file", "all")]
    [string]$PackageMode = "single-file",
    [switch]$NoRestore
)

$ErrorActionPreference = "Stop"
$appName = [string]::Concat(
    [char]0x7075,
    [char]0x7B80,
    [char]0x56FE,
    [char]0x7247,
    [char]0x52A9,
    [char]0x624B)
$distributionName = "{0}-desktop" -f $appName

$projectRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $projectRoot "src\ModernImageViewer.Desktop\ModernImageViewer.Desktop.csproj"
$distRoot = Join-Path $projectRoot "dist"
$nugetConfigPath = Join-Path $projectRoot "NuGet.Config"
$appDataRoot = Join-Path $projectRoot ".appdata"
$localNuGetConfigDir = Join-Path $appDataRoot "NuGet"
$localNuGetConfigPath = Join-Path $localNuGetConfigDir "NuGet.Config"
$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"

if (-not (Test-Path $distRoot)) {
    New-Item -ItemType Directory -Path $distRoot | Out-Null
}

if (-not (Test-Path $appDataRoot)) {
    New-Item -ItemType Directory -Path $appDataRoot | Out-Null
}

if (-not (Test-Path $localNuGetConfigDir)) {
    New-Item -ItemType Directory -Path $localNuGetConfigDir | Out-Null
}

if (Test-Path $nugetConfigPath) {
    Copy-Item -LiteralPath $nugetConfigPath -Destination $localNuGetConfigPath -Force
}

$env:APPDATA = $appDataRoot

if ([string]::IsNullOrWhiteSpace($env:DOTNET_CLI_HOME)) {
    $env:DOTNET_CLI_HOME = Join-Path $projectRoot ".dotnet-cli-home"
}

if (-not (Test-Path $env:DOTNET_CLI_HOME)) {
    New-Item -ItemType Directory -Path $env:DOTNET_CLI_HOME | Out-Null
}

$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:AVALONIA_TELEMETRY_OPTOUT = "1"

function Get-LauncherInfo {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RuntimeTarget
    )

    if ($RuntimeTarget.StartsWith("win-", [System.StringComparison]::OrdinalIgnoreCase)) {
        return [PSCustomObject]@{
            SourceName = "ModernImageViewer.Desktop.exe"
            TargetName = "{0}.exe" -f $appName
        }
    }

    return [PSCustomObject]@{
        SourceName = "ModernImageViewer.Desktop"
        TargetName = $appName
    }
}

function ConvertTo-BigEndianUInt32 {
    param(
        [Parameter(Mandatory = $true)]
        [UInt32]$Value
    )

    $bytes = [System.BitConverter]::GetBytes($Value)
    if ([System.BitConverter]::IsLittleEndian) {
        [System.Array]::Reverse($bytes)
    }

    return $bytes
}

function Write-AsciiBytes {
    param(
        [Parameter(Mandatory = $true)]
        [byte[]]$Buffer,
        [Parameter(Mandatory = $true)]
        [int]$Offset,
        [Parameter(Mandatory = $true)]
        [string]$Value
    )

    $bytes = [System.Text.Encoding]::ASCII.GetBytes($Value)
    [System.Array]::Copy($bytes, 0, $Buffer, $Offset, $bytes.Length)
}

function Write-BigEndianUInt32 {
    param(
        [Parameter(Mandatory = $true)]
        [byte[]]$Buffer,
        [Parameter(Mandatory = $true)]
        [int]$Offset,
        [Parameter(Mandatory = $true)]
        [UInt32]$Value
    )

    $bytes = ConvertTo-BigEndianUInt32 -Value $Value
    [System.Array]::Copy($bytes, 0, $Buffer, $Offset, $bytes.Length)
}

function New-MacIcnsFromPng {
    param(
        [Parameter(Mandatory = $true)]
        [string]$SourcePngPath,
        [Parameter(Mandatory = $true)]
        [string]$DestinationIcnsPath
    )

    if (-not (Test-Path $SourcePngPath)) {
        throw "Cannot build macOS icon: missing $SourcePngPath"
    }

    $pngBytes = [System.IO.File]::ReadAllBytes($SourcePngPath)
    $iconEntryLength = 8 + $pngBytes.Length
    $totalLength = 8 + $iconEntryLength
    $buffer = New-Object byte[] $totalLength

    Write-AsciiBytes -Buffer $buffer -Offset 0 -Value "icns"
    Write-BigEndianUInt32 -Buffer $buffer -Offset 4 -Value ([UInt32]$totalLength)
    Write-AsciiBytes -Buffer $buffer -Offset 8 -Value "ic10"
    Write-BigEndianUInt32 -Buffer $buffer -Offset 12 -Value ([UInt32]$iconEntryLength)
    [System.Array]::Copy($pngBytes, 0, $buffer, 16, $pngBytes.Length)
    [System.IO.File]::WriteAllBytes($DestinationIcnsPath, $buffer)
}

function ConvertTo-MacAppBundle {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PublishDirectory,
        [Parameter(Mandatory = $true)]
        [string]$SourceLauncherName,
        [Parameter(Mandatory = $true)]
        [string]$TargetLauncherName
    )

    $appBundlePath = Join-Path $PublishDirectory ("{0}.app" -f $appName)
    $contentsPath = Join-Path $appBundlePath "Contents"
    $macOsPath = Join-Path $contentsPath "MacOS"
    $resourcesPath = Join-Path $contentsPath "Resources"

    if (Test-Path $appBundlePath) {
        Remove-Item -LiteralPath $appBundlePath -Recurse -Force
    }

    New-Item -ItemType Directory -Path $macOsPath -Force | Out-Null
    New-Item -ItemType Directory -Path $resourcesPath -Force | Out-Null

    Get-ChildItem -LiteralPath $PublishDirectory -Force |
        Where-Object { $_.FullName -ne $appBundlePath } |
        ForEach-Object {
            Move-Item -LiteralPath $_.FullName -Destination $macOsPath
        }

    $sourceLauncherPath = Join-Path $macOsPath $SourceLauncherName
    $targetLauncherPath = Join-Path $macOsPath $TargetLauncherName
    if (-not (Test-Path $sourceLauncherPath)) {
        throw "Publish failed: missing $sourceLauncherPath"
    }

    if (-not [System.StringComparer]::OrdinalIgnoreCase.Equals($SourceLauncherName, $TargetLauncherName)) {
        Rename-Item -LiteralPath $sourceLauncherPath -NewName $TargetLauncherName
    }

    $iconSourcePath = Join-Path $projectRoot "src\ModernImageViewer.Desktop\Assets\LingJianImageAssistant.png"
    $iconResourceName = "LingJianImageAssistant.icns"
    if (Test-Path $iconSourcePath) {
        Copy-Item -LiteralPath $iconSourcePath -Destination (Join-Path $resourcesPath "LingJianImageAssistant.png") -Force
        New-MacIcnsFromPng `
            -SourcePngPath $iconSourcePath `
            -DestinationIcnsPath (Join-Path $resourcesPath $iconResourceName)
    }

    $plistPath = Join-Path $contentsPath "Info.plist"
    $plist = @"
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "https://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleDevelopmentRegion</key>
    <string>zh_CN</string>
    <key>CFBundleDisplayName</key>
    <string>$appName</string>
    <key>CFBundleExecutable</key>
    <string>$TargetLauncherName</string>
    <key>CFBundleIconFile</key>
    <string>$iconResourceName</string>
    <key>CFBundleIdentifier</key>
    <string>com.lingjian.imageassistant</string>
    <key>CFBundleInfoDictionaryVersion</key>
    <string>6.0</string>
    <key>CFBundleName</key>
    <string>$appName</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleShortVersionString</key>
    <string>1.5</string>
    <key>CFBundleVersion</key>
    <string>1.5.0</string>
    <key>LSMinimumSystemVersion</key>
    <string>10.15</string>
    <key>NSHighResolutionCapable</key>
    <true/>
</dict>
</plist>
"@
    Set-Content -LiteralPath $plistPath -Value $plist -Encoding UTF8

    return [PSCustomObject]@{
        AppBundle = $appBundlePath
        Launcher = $targetLauncherPath
    }
}

function Add-MacTestHelpers {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PublishDirectory
    )

    $fixScriptPath = Join-Path $PublishDirectory "Fix-MacApp.sh"
    $readmePath = Join-Path $PublishDirectory "README-macOS.txt"
    $appBundleName = "{0}.app" -f $appName

    $fixScript = @"
#!/usr/bin/env bash
set -euo pipefail

APP_DIR="`$(cd "`$(dirname "`${BASH_SOURCE[0]}")" && pwd)"
APP_PATH="`$APP_DIR/$appBundleName"

if [[ ! -d "`$APP_PATH" ]]; then
  echo "Cannot find app bundle: `$APP_PATH" >&2
  exit 1
fi

chmod +x "`$APP_PATH/Contents/MacOS/"*
xattr -dr com.apple.quarantine "`$APP_PATH" 2>/dev/null || true
open "`$APP_PATH"
"@

    $readme = @"
macOS first-run helper

This test build is not Apple-signed or notarized yet. If macOS says the app cannot be opened:

1. Open Terminal in this folder.
2. Run:

   bash ./Fix-MacApp.sh

The helper restores executable permissions, removes the download quarantine flag, and opens $appBundleName.

For a production release, build the .dmg on macOS, then sign and notarize it with an Apple Developer ID.
"@

    Set-Content -LiteralPath $fixScriptPath -Value $fixScript -Encoding UTF8
    Set-Content -LiteralPath $readmePath -Value $readme -Encoding UTF8
}

function Publish-Target {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RuntimeTarget,
        [Parameter(Mandatory = $true)]
        [string]$PackageTarget
    )

    $packageLabel = switch ($PackageTarget) {
        "folder" { "stable" }
        "single-file" { "portable" }
        default { $PackageTarget }
    }

    $publishDir = Join-Path $distRoot ("{0}-{1}-{2}-{3}" -f $distributionName, $RuntimeTarget, $packageLabel, $timestamp)
    $zipPath = "{0}.zip" -f $publishDir
    $launcherInfo = Get-LauncherInfo -RuntimeTarget $RuntimeTarget
    $sourceLauncher = Join-Path $publishDir $launcherInfo.SourceName
    $targetLauncher = Join-Path $publishDir $launcherInfo.TargetName
    $appBundlePath = $null

    $publishArguments = @(
        "publish"
        $projectPath
        "-c"
        "Release"
        "-r"
        $RuntimeTarget
        "--self-contained"
        "true"
        "-p:DebugType=None"
        "-p:DebugSymbols=false"
        "-o"
        $publishDir
    )

    if (Test-Path $nugetConfigPath) {
        $publishArguments += @(
            "--configfile"
            $nugetConfigPath
        )
    }

    if ($NoRestore) {
        $publishArguments += "--no-restore"
    }

    if ($PackageTarget -eq "single-file") {
        $publishArguments += @(
            "-p:PublishSingleFile=true"
            "-p:IncludeNativeLibrariesForSelfExtract=true"
            "-p:EnableCompressionInSingleFile=true"
        )
    }
    else {
        $publishArguments += @(
            "-p:PublishSingleFile=false"
            "-p:IncludeNativeLibrariesForSelfExtract=false"
            "-p:EnableCompressionInSingleFile=false"
        )
    }

    & dotnet @publishArguments | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $RuntimeTarget / $PackageTarget"
    }

    if ($RuntimeTarget.StartsWith("osx-", [System.StringComparison]::OrdinalIgnoreCase)) {
        $bundleInfo = ConvertTo-MacAppBundle `
            -PublishDirectory $publishDir `
            -SourceLauncherName $launcherInfo.SourceName `
            -TargetLauncherName $launcherInfo.TargetName
        $appBundlePath = $bundleInfo.AppBundle
        $targetLauncher = $bundleInfo.Launcher
        Add-MacTestHelpers -PublishDirectory $publishDir
    }
    else {
        if (-not (Test-Path $sourceLauncher)) {
            throw "Publish failed: missing $sourceLauncher"
        }

        if (-not [System.StringComparer]::OrdinalIgnoreCase.Equals($launcherInfo.SourceName, $launcherInfo.TargetName)) {
            Rename-Item -LiteralPath $sourceLauncher -NewName $launcherInfo.TargetName
        }
    }

    if (Test-Path $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }

    Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipPath

    [PSCustomObject]@{
        Runtime    = $RuntimeTarget
        Package    = $PackageTarget
        PublishDir = $publishDir
        AppBundle  = $appBundlePath
        Launcher   = $targetLauncher
        Zip        = $zipPath
    }
}

$targets = if ($Runtime -eq "all") {
    @("win-x64", "win-arm64", "osx-x64", "osx-arm64")
}
else {
    @($Runtime)
}

$packageTargets = if ($PackageMode -eq "all") {
    @("folder", "single-file")
}
else {
    @($PackageMode)
}

$results = foreach ($target in $targets) {
    foreach ($packageTarget in $packageTargets) {
        Publish-Target -RuntimeTarget $target -PackageTarget $packageTarget
    }
}

foreach ($result in $results) {
    Write-Host ("Runtime:    {0}" -f $result.Runtime)
    Write-Host ("Package:    {0}" -f $result.Package)
    Write-Host ("PublishDir: {0}" -f $result.PublishDir)
    if (-not [string]::IsNullOrWhiteSpace($result.AppBundle)) {
        Write-Host ("AppBundle:  {0}" -f $result.AppBundle)
    }
    Write-Host ("Launcher:   {0}" -f $result.Launcher)
    Write-Host ("Zip:        {0}" -f $result.Zip)
    Write-Host ""
}
