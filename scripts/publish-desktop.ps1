# 自包含發佈（self-contained）：把 .NET 10 Desktop Runtime 與 ASP.NET Core
# （桌面程式內建的 Web 服務用）一併烤進輸出資料夾。
# 目標機器不需要安裝或下載任何 .NET 元件——解壓資料夾即用。
#
# 用法（repo 根目錄）：
#   powershell -File scripts\publish-desktop.ps1
#   powershell -File scripts\publish-desktop.ps1 -Output D:\dist\HeliVMS
#
# 注意：ffmpeg／ffprobe 是外部元件（GPL 授權考量，刻意不隨包），
# 目標機器仍需依既有慣例提供（PATH 或程式資料夾旁）。

[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$Output = 'publish/HeliVMS'
)

$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')

dotnet publish src/HeliVMS.App -c $Configuration -r $Runtime --self-contained true `
    -p:PublishReadyToRun=true -o $Output
if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish 失敗（exit $LASTEXITCODE）"
    exit $LASTEXITCODE
}

# 驗證輸出真的是自包含：hostfxr／coreclr 必須在資料夾裡。它們缺席代表發佈退化成
# framework-dependent——目標機器會去下載 Runtime，正是這個腳本要避免的失敗模式。
$required = @(
    'HeliVMS.App.exe',
    'hostfxr.dll',
    'coreclr.dll',
    'clrjit.dll',
    'PresentationFramework.dll',
    'WindowsBase.dll',
    'Microsoft.AspNetCore.dll'
)
$missing = @($required | Where-Object { -not (Test-Path (Join-Path $Output $_)) })
if ($missing.Count -gt 0) {
    Write-Error ("自包含輸出缺檔（發佈不是 self-contained？）：" + ($missing -join ', '))
    exit 1
}

$size = [math]::Round((Get-ChildItem $Output -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
Write-Host "OK: $Output（$size MB，自包含，免安裝 Runtime；ffmpeg 仍需另行提供）"
