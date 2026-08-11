<#
.SYNOPSIS
  Build a double-click AIExplorer installer for Win11 x64 that does not require
  pre-installed .NET / Windows App Runtime on the target machine.

.DESCRIPTION
  WinUI SelfContained publish crashes on this project (0xC000027B). Instead:
    1) Build framework-dependent Release|x64 (VS MSBuild)
    2) Bundle a private .NET 8 Desktop runtime copied from the build machine
    3) Bundle Windows App Runtime 1.6 MSIX packages from the WindowsAppSDK NuGet
    4) Emit Install-AIExplorer.cmd that registers WAR + installs app + launcher

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\package.ps1 -RepoRoot .
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = "",
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",
    [switch]$SkipZip,
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    if ([string]::IsNullOrWhiteSpace($PSScriptRoot)) {
        throw "Cannot resolve repo root; pass -RepoRoot"
    }
    $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
}

function Write-Step([string]$Message) {
    Write-Host ""
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Find-MsBuild {
    $candidates = @(
        "${env:ProgramFiles}\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\amd64\MSBuild.exe",
        "${env:ProgramFiles}\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe",
        "${env:ProgramFiles}\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\amd64\MSBuild.exe",
        "${env:ProgramFiles}\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe",
        "${env:ProgramFiles}\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\amd64\MSBuild.exe",
        "${env:ProgramFiles}\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe",
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\amd64\MSBuild.exe",
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
    )
    foreach ($p in $candidates) {
        if (Test-Path $p) { return $p }
    }
    throw "VS 2022 MSBuild not found."
}

function Ensure-DotNetSdk {
    $userDotnet = Join-Path $env:USERPROFILE ".dotnet"
    if (Test-Path (Join-Path $userDotnet "dotnet.exe")) {
        $env:DOTNET_ROOT = $userDotnet
        $env:PATH = "$userDotnet;$env:PATH"
    }
    $sdks = & dotnet --list-sdks 2>$null
    if (-not ($sdks | Where-Object { $_ -match '^8\.' })) {
        throw "Need .NET 8 SDK. Current: $($sdks -join '; ')"
    }
}

function Get-LatestDir([string]$Parent, [string]$MajorPrefix = "") {
    if (-not (Test-Path $Parent)) { return $null }
    $dirs = Get-ChildItem $Parent -Directory | Where-Object { $_.Name -match '^\d+\.' }
    if ($MajorPrefix) {
        $dirs = $dirs | Where-Object { $_.Name.StartsWith($MajorPrefix) }
    }
    return $dirs |
        Sort-Object { [version]($_.Name -replace '-.*$','') } -Descending |
        Select-Object -First 1
}

function Find-SystemDotNetRoot {
    foreach ($candidate in @(
        (Join-Path $env:ProgramFiles "dotnet"),
        (Join-Path ${env:ProgramFiles(x86)} "dotnet"),
        (Join-Path $env:USERPROFILE ".dotnet")
    )) {
        if (Test-Path (Join-Path $candidate "dotnet.exe")) {
            return $candidate
        }
    }
    throw "Cannot find a local .NET installation to bundle."
}

function Find-WindowsAppSdkMsixDir {
    $pkgRoot = Join-Path $env:USERPROFILE ".nuget\packages\microsoft.windowsappsdk"
    if (-not (Test-Path $pkgRoot)) {
        throw "WindowsAppSDK NuGet cache not found: $pkgRoot (restore the app project first)"
    }
    $ver = Get-ChildItem $pkgRoot -Directory |
        Where-Object { $_.Name -like "1.6*" } |
        Sort-Object Name -Descending |
        Select-Object -First 1
    if (-not $ver) {
        throw "No WindowsAppSDK 1.6.* package in NuGet cache."
    }
    $msix = Join-Path $ver.FullName "tools\MSIX\win10-x64"
    if (-not (Test-Path (Join-Path $msix "Microsoft.WindowsAppRuntime.1.6.msix"))) {
        throw "Missing WAR MSIX under $msix"
    }
    return $msix
}

Write-Step "Repo: $RepoRoot"
Set-Location $RepoRoot
Ensure-DotNetSdk
Write-Host "dotnet: $(dotnet --version)"

$project = Join-Path $RepoRoot "src\AIExplorer.App\AIExplorer.App.csproj"
$outDir = Join-Path $RepoRoot "src\AIExplorer.App\bin\x64\$Configuration\net8.0-windows10.0.19041.0\win-x64"
$artifacts = Join-Path $RepoRoot "artifacts"
$publishDir = Join-Path $artifacts "AIExplorer-win-x64"
$zipPath = Join-Path $artifacts "AIExplorer-Setup-win-x64.zip"

if (-not $SkipBuild) {
    Write-Step "Build $Configuration|x64"
    $msbuild = Find-MsBuild
    Write-Host "MSBuild: $msbuild"
    # Never leave SelfContained publish residue in bin (causes 0xC000027B)
    & $msbuild $project /t:Clean "/p:Configuration=$Configuration" /p:Platform=x64 /v:minimal | Out-Null
    & $msbuild $project /restore `
        "/p:Configuration=$Configuration" `
        /p:Platform=x64 `
        /p:SelfContained=false `
        /p:WindowsAppSDKSelfContained=false `
        /m `
        /v:minimal
    if ($LASTEXITCODE -ne 0) {
        throw "build failed (exit $LASTEXITCODE)"
    }
}
else {
    Write-Step "SkipBuild - reuse $outDir"
}

$builtExe = Join-Path $outDir "AIExplorer.App.exe"
if (-not (Test-Path $builtExe)) {
    throw "Missing build output: $builtExe"
}

function Resolve-DotNet8BundleRoot {
    $candidates = @(
        (Join-Path $env:USERPROFILE ".dotnet"),
        (Join-Path $env:ProgramFiles "dotnet"),
        (Join-Path ${env:ProgramFiles(x86)} "dotnet")
    )
    foreach ($root in $candidates) {
        if (-not (Test-Path (Join-Path $root "dotnet.exe"))) { continue }
        $hostDir = Get-LatestDir (Join-Path $root "host\fxr") "8."
        $netCoreDir = Get-LatestDir (Join-Path $root "shared\Microsoft.NETCore.App") "8."
        $desktopDir = Get-LatestDir (Join-Path $root "shared\Microsoft.WindowsDesktop.App") "8."
        if ($hostDir -and $netCoreDir -and $desktopDir) {
            return [pscustomobject]@{
                Root = $root
                Host = $hostDir
                NetCore = $netCoreDir
                Desktop = $desktopDir
            }
        }
    }
    throw "Need a .NET 8 install with host + Microsoft.NETCore.App + Microsoft.WindowsDesktop.App"
}

$bundle = Resolve-DotNet8BundleRoot
$dotnetRoot = $bundle.Root
$hostDir = $bundle.Host
$netCoreDir = $bundle.NetCore
$desktopDir = $bundle.Desktop

$warMsixDir = Find-WindowsAppSdkMsixDir
Write-Host "Bundle .NET from: $dotnetRoot"
Write-Host "  host fxr: $($hostDir.Name)"
Write-Host "  NETCore:  $($netCoreDir.Name)"
Write-Host "  Desktop:  $($desktopDir.Name)"
Write-Host "Bundle WAR MSIX from: $warMsixDir"

Write-Step "Stage package folder"
if (Test-Path $publishDir) {
    Remove-Item $publishDir -Recurse -Force
}
$appStage = Join-Path $publishDir "app"
$dotnetStage = Join-Path $publishDir "dotnet"
$warStage = Join-Path $publishDir "runtimes\war-msix"
New-Item -ItemType Directory -Force -Path $appStage | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $dotnetStage "host\fxr\$($hostDir.Name)") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $dotnetStage "shared\Microsoft.NETCore.App\$($netCoreDir.Name)") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $dotnetStage "shared\Microsoft.WindowsDesktop.App\$($desktopDir.Name)") | Out-Null
New-Item -ItemType Directory -Force -Path $warStage | Out-Null

& robocopy $outDir $appStage /E /XD publish /XF *.pdb /NFL /NDL /NJH /NJS /nc /ns /np | Out-Null
if ($LASTEXITCODE -ge 8) { throw "copy app failed (robocopy exit $LASTEXITCODE)" }
$global:LASTEXITCODE = 0

& robocopy $hostDir.FullName (Join-Path $dotnetStage "host\fxr\$($hostDir.Name)") /E /NFL /NDL /NJH /NJS /nc /ns /np | Out-Null
if ($LASTEXITCODE -ge 8) { throw "copy host failed" }
$global:LASTEXITCODE = 0

& robocopy $netCoreDir.FullName (Join-Path $dotnetStage "shared\Microsoft.NETCore.App\$($netCoreDir.Name)") /E /NFL /NDL /NJH /NJS /nc /ns /np | Out-Null
if ($LASTEXITCODE -ge 8) { throw "copy NETCore failed" }
$global:LASTEXITCODE = 0

& robocopy $desktopDir.FullName (Join-Path $dotnetStage "shared\Microsoft.WindowsDesktop.App\$($desktopDir.Name)") /E /NFL /NDL /NJH /NJS /nc /ns /np | Out-Null
if ($LASTEXITCODE -ge 8) { throw "copy Desktop failed" }
$global:LASTEXITCODE = 0

Copy-Item -Force (Join-Path $dotnetRoot "dotnet.exe") $dotnetStage -ErrorAction SilentlyContinue
Copy-Item -Force (Join-Path $warMsixDir "*.msix") $warStage
Copy-Item -Force (Join-Path $warMsixDir "MSIX.inventory") $warStage -ErrorAction SilentlyContinue

# Microsoft recommended: WindowsAppRuntimeInstall.exe --quiet (deploy-unpackaged-apps Option 1)
Write-Step "Bundle official WindowsAppRuntimeInstall-x64.exe"
$cacheDir = Join-Path $artifacts "_runtime_cache"
$warInstallerCache = Join-Path $cacheDir "WindowsAppRuntimeInstall-x64.exe"
$warInstallerUrls = @(
    "https://aka.ms/windowsappsdk/1.6/1.6.240829007/windowsappruntimeinstall-x64.exe",
    "https://aka.ms/windowsappsdk/1.6/latest/windowsappruntimeinstall-x64.exe"
)
if (-not ((Test-Path $warInstallerCache) -and ((Get-Item $warInstallerCache).Length -gt 1MB))) {
    New-Item -ItemType Directory -Force -Path $cacheDir | Out-Null
    $downloaded = $false
    foreach ($url in $warInstallerUrls) {
        Write-Host "Downloading $url"
        $partial = "$warInstallerCache.partial"
        & curl.exe -L --fail --retry 3 --retry-delay 2 --connect-timeout 20 --max-time 300 -o $partial $url
        if ($LASTEXITCODE -eq 0 -and (Test-Path $partial) -and ((Get-Item $partial).Length -gt 1MB)) {
            Move-Item -Force $partial $warInstallerCache
            $downloaded = $true
            break
        }
    }
    if (-not $downloaded) {
        throw "Failed to download WindowsAppRuntimeInstall-x64.exe. Put it at $warInstallerCache and re-run."
    }
}
else {
    Write-Host "Cache hit: $warInstallerCache"
}
Copy-Item -Force $warInstallerCache (Join-Path $publishDir "runtimes\WindowsAppRuntimeInstall-x64.exe")
Write-Host "WAR installer: $([math]::Round((Get-Item $warInstallerCache).Length/1MB,1)) MB"

# Launcher uses private DOTNET_ROOT so system runtime is not required
$launcher = @"
@echo off
setlocal
set "ROOT=%~dp0"
set "DOTNET_ROOT=%ROOT%dotnet"
set "DOTNET_MULTILEVEL_LOOKUP=0"
start "" "%ROOT%AIExplorer.App.exe"
"@
Set-Content -Path (Join-Path $appStage "AIExplorer.cmd") -Value $launcher -Encoding ASCII

Write-Step "Build AIExplorer-Setup.exe (.NET Framework 4.8 WinForms)"
$setupProj = Join-Path $RepoRoot "tools\AIExplorer.Setup\AIExplorer.Setup.csproj"
$msbuild = Find-MsBuild
& $msbuild $setupProj /t:Restore,Rebuild /p:Configuration=Release /p:Platform=AnyCPU /v:minimal
if ($LASTEXITCODE -ne 0) { throw "Setup build failed (exit $LASTEXITCODE)" }
$setupExe = Join-Path $RepoRoot "tools\AIExplorer.Setup\bin\Release\net48\AIExplorer-Setup.exe"
if (-not (Test-Path $setupExe)) {
    $alt = Get-ChildItem (Join-Path $RepoRoot "tools\AIExplorer.Setup\bin") -Recurse -Filter "AIExplorer-Setup.exe" | Select-Object -First 1
    if ($alt) { $setupExe = $alt.FullName }
}
if (-not (Test-Path $setupExe)) { throw "Missing AIExplorer-Setup.exe after build" }
Copy-Item -Force $setupExe $publishDir
Write-Host "Setup.exe: $((Get-Item (Join-Path $publishDir 'AIExplorer-Setup.exe')).Length) bytes"

# Fallback CMD installer (same steps; prefer Setup.exe)
$installCmd = @'
@echo off
setlocal EnableExtensions
cd /d "%~dp0"
echo Prefer double-clicking AIExplorer-Setup.exe
if exist "%~dp0AIExplorer-Setup.exe" (
  start "" "%~dp0AIExplorer-Setup.exe"
  exit /b 0
)
echo ERROR: AIExplorer-Setup.exe missing
pause
exit /b 1
'@
Set-Content -Path (Join-Path $publishDir "Install-AIExplorer.cmd") -Value $installCmd -Encoding ASCII

$readmeLines = @(
    "AIExplorer setup package (Windows 11 / 10 x64)",
    "==============================================",
    "",
    "INSTALL (required)",
    "------------------",
    "1. Unzip the WHOLE folder",
    "2. Double-click AIExplorer-Setup.exe",
    "3. Finish the wizard (it installs Windows App Runtime 1.6 automatically)",
    "",
    "Do NOT double-click app\AIExplorer.App.exe before Setup.",
    "That will show 'requires Windows App Runtime 1.6' and skip the bundled installer.",
    "",
    "UNINSTALL",
    "---------",
    "- Settings -> Apps -> AI Explorer",
    "- Or run Uninstall.exe in the install folder",
    "",
    "Layout: AIExplorer-Setup.exe, app\, dotnet\, runtimes\"
)
Set-Content -Path (Join-Path $publishDir "README-Install.txt") -Value ($readmeLines -join "`r`n") -Encoding ASCII

[System.IO.File]::WriteAllText(
    (Join-Path $publishDir "PLEASE-RUN-Setup-FIRST.txt"),
    ("Please double-click AIExplorer-Setup.exe first.`r`n" +
     "Do NOT run app\AIExplorer.App.exe before Setup, or Windows will prompt for App Runtime 1.6`r`n" +
     "and skip the Runtime installer already bundled in this package.`r`n"),
    [System.Text.UTF8Encoding]::new($false))

$fileCount = (Get-ChildItem $publishDir -Recurse -File).Count
$sizeMb = [math]::Round(((Get-ChildItem $publishDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 1)

if (-not $SkipZip) {
    Write-Step "Zip -> $zipPath"
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipPath -CompressionLevel Optimal
    Write-Host "Zip size: $([math]::Round((Get-Item $zipPath).Length / 1MB, 1)) MB"
}

Write-Host ""
Write-Host "Package ready" -ForegroundColor Green
Write-Host "  Folder:  $publishDir"
Write-Host "  Setup:   $publishDir\AIExplorer-Setup.exe"
if (-not $SkipZip) { Write-Host "  Zip:     $zipPath" }
Write-Host "  Files:   $fileCount"
Write-Host "  Size:    $sizeMb MB"
Write-Host ""
Write-Host "On Win11: unzip -> double-click AIExplorer-Setup.exe"
