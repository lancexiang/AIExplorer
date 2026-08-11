<#
.SYNOPSIS
  一键构建并安装当前仓库的 AIExplorer（unpackaged WinUI 3）。

.DESCRIPTION
  - 用 VS 2022 MSBuild 打 Release|x64
  - 复制到 %LocalAppData%\Programs\AIExplorer
  - 创建开始菜单 / 桌面快捷方式
  - 可选：安装后启动、检查 Windows App Runtime

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\install.ps1

.EXAMPLE
  .\tools\install.ps1 -NoLaunch -SkipDesktopShortcut
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = "",
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA "Programs\AIExplorer"),
    [switch]$NoLaunch,
    [switch]$SkipDesktopShortcut,
    [switch]$SkipBuild,
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    if ([string]::IsNullOrWhiteSpace($PSScriptRoot)) {
        throw "无法推断仓库根目录，请传入 -RepoRoot"
    }
    $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
}

function Write-Step([string]$Message) {
    Write-Host ""
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Find-MsBuild {
    $candidates = @(
        "${env:ProgramFiles}\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe",
        "${env:ProgramFiles}\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe",
        "${env:ProgramFiles}\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe",
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
    )
    foreach ($p in $candidates) {
        if (Test-Path $p) { return $p }
    }
    throw "未找到 VS 2022 MSBuild。请安装 Visual Studio 2022（含 .NET 桌面 / WinUI 工作负载）。"
}

function Ensure-DotNetSdk {
    # 仓库 global.json 钉了 8.0.423；优先用户目录 SDK，避免 Program Files 只有 8.0.100 时失败
    $userDotnet = Join-Path $env:USERPROFILE ".dotnet"
    if (Test-Path (Join-Path $userDotnet "dotnet.exe")) {
        $env:DOTNET_ROOT = $userDotnet
        $env:PATH = "$userDotnet;$env:PATH"
    }

    $sdks = & dotnet --list-sdks 2>$null
    if (-not ($sdks | Where-Object { $_ -match '^8\.' })) {
        throw "需要 .NET 8 SDK。当前: $($sdks -join '; ')"
    }
}

function Test-WindowsAppRuntime {
    $pkgs = Get-AppxPackage -Name "Microsoft.WindowsAppRuntime.1.6*" -ErrorAction SilentlyContinue
    if (-not $pkgs) {
        Write-Warning "未检测到 Windows App Runtime 1.6。WinUI 应用可能无法启动。"
        Write-Warning "下载: https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads"
        return $false
    }
    Write-Host ("Windows App Runtime 1.6: " + (($pkgs | Select-Object -ExpandProperty Version) -join ", "))
    return $true
}

function New-Shortcut([string]$LinkPath, [string]$Target, [string]$WorkDir) {
    $dir = Split-Path $LinkPath -Parent
    if (-not (Test-Path $dir)) {
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
    }
    $shell = New-Object -ComObject WScript.Shell
    $sc = $shell.CreateShortcut($LinkPath)
    $sc.TargetPath = $Target
    $sc.WorkingDirectory = $WorkDir
    $sc.WindowStyle = 1
    $sc.Description = "AI Explorer"
    $ico = Join-Path $WorkDir "Assets\AIExplorer.ico"
    if (Test-Path $ico) {
        $sc.IconLocation = $ico
    }
    $sc.Save()
}

Write-Step "Repo: $RepoRoot"
Set-Location $RepoRoot

Ensure-DotNetSdk
Write-Host "dotnet: $(dotnet --version)"
[void](Test-WindowsAppRuntime)

$project = Join-Path $RepoRoot "src\AIExplorer.App\AIExplorer.App.csproj"
$outDir = Join-Path $RepoRoot "src\AIExplorer.App\bin\x64\$Configuration\net8.0-windows10.0.19041.0\win-x64"
$exeName = "AIExplorer.App.exe"

if (-not $SkipBuild) {
    Write-Step "Build $Configuration|x64"
    $msbuild = Find-MsBuild
    Write-Host "MSBuild: $msbuild"
    & $msbuild $project /restore "/p:Configuration=$Configuration" /p:Platform=x64 /m /v:minimal
    if ($LASTEXITCODE -ne 0) {
        throw "构建失败 (exit $LASTEXITCODE)"
    }
}
else {
    Write-Step "SkipBuild — 使用已有输出"
}

$builtExe = Join-Path $outDir $exeName
if (-not (Test-Path $builtExe)) {
    throw "找不到构建产物: $builtExe"
}

Write-Step "Install -> $InstallDir"
Get-Process -Name "AIExplorer.App" -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "结束进程 pid=$($_.Id)"
    Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
}
Start-Sleep -Milliseconds 500

if (Test-Path $InstallDir) {
    Remove-Item $InstallDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null

& robocopy $outDir $InstallDir /E /XD publish /XF *.pdb /NFL /NDL /NJH /NJS /nc /ns /np | Out-Null
# robocopy exit 0-7 = success
if ($LASTEXITCODE -ge 8) {
    throw "复制失败 (robocopy exit $LASTEXITCODE)"
}
$global:LASTEXITCODE = 0

$installedExe = Join-Path $InstallDir $exeName
if (-not (Test-Path $installedExe)) {
    throw "安装后缺少 $exeName"
}

Write-Step "Shortcuts"
$startMenu = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\AIExplorer.lnk"
New-Shortcut -LinkPath $startMenu -Target $installedExe -WorkDir $InstallDir
Write-Host "Start Menu: $startMenu"

if (-not $SkipDesktopShortcut) {
    $desktop = Join-Path ([Environment]::GetFolderPath("Desktop")) "AIExplorer.lnk"
    New-Shortcut -LinkPath $desktop -Target $installedExe -WorkDir $InstallDir
    Write-Host "Desktop: $desktop"
}

$fileCount = (Get-ChildItem $InstallDir -Recurse -File).Count
Write-Host ""
Write-Host "安装完成" -ForegroundColor Green
Write-Host "  目录: $InstallDir"
Write-Host "  程序: $installedExe"
Write-Host "  文件数: $fileCount"

if (-not $NoLaunch) {
    Write-Step "Launch"
    Start-Process -FilePath $installedExe -WorkingDirectory $InstallDir
}

Write-Host ""
Write-Host "以后一键安装:"
Write-Host "  powershell -ExecutionPolicy Bypass -File `"$PSCommandPath`""
