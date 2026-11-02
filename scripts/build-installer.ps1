# 打包安裝檔：自包含發佈 → Inno Setup → 單一 HeliVMS-Setup-<版本>.exe。
#
# 用法（repo 根目錄）：
#   powershell -File scripts\build-installer.ps1
#   powershell -File scripts\build-installer.ps1 -FfmpegDir D:\ffmpeg\bin -Output D:\dist\HeliVMS
#
# 安裝檔內含 .NET Runtime、ASP.NET Core 與 ffmpeg 隨包——目標機器什麼都不用再裝。
# 資料根目錄（C:\HeliVMSData）不在安裝資料夾內，解除安裝不會動錄影與索引。

[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$Output = 'publish/HeliVMS',
    [string]$FfmpegDir = '',
    [switch]$NoFfmpeg
)

$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')

# 1) 自包含發佈（含 Runtime 驗證與 ffmpeg 隨包）。
$publishArgs = @('scripts\publish-desktop.ps1', '-Configuration', $Configuration, '-Runtime', $Runtime, '-Output', $Output)
if ($FfmpegDir -ne '') { $publishArgs += @('-FfmpegDir', $FfmpegDir) }
if ($NoFfmpeg) { $publishArgs += '-NoFfmpeg' }
& powershell -ExecutionPolicy Bypass -File @publishArgs
if ($LASTEXITCODE -ne 0) {
    Write-Error "發佈失敗（exit $LASTEXITCODE），不打包安裝檔"
    exit $LASTEXITCODE
}

# 2) 找 ISCC（Inno Setup 6 命令列編譯器）。
$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($null -eq $iscc) {
    Write-Error "找不到 Inno Setup 6（ISCC.exe）。請安裝：https://jrsoftware.org/isdl.php"
    exit 1
}

# 3) 版本號取自 Directory.Build.props（與所有專案同一來源，不會漂移）。
$props = Get-Content Directory.Build.props -Raw
$version = if ($props -match '<Version>([^<]+)</Version>') { $Matches[1] } else { '0.1.0' }

# 4) 編譯安裝檔。
& $iscc "/DAppVersion=$version" installer\helivms.iss
if ($LASTEXITCODE -ne 0) {
    Write-Error "ISCC 失敗（exit $LASTEXITCODE）"
    exit $LASTEXITCODE
}

$setup = "publish\Setup\HeliVMS-Setup-$version.exe"
if (-not (Test-Path $setup)) {
    Write-Error "ISCC 回報成功但找不到 $setup"
    exit 1
}

$size = [math]::Round((Get-Item $setup).Length / 1MB, 1)
Write-Host "OK: $setup（$size MB）"
Write-Host "安裝：執行 setup；靜默：/VERYSILENT；使用者層級測試：/CURRENTUSER /DIR=<路徑>"
