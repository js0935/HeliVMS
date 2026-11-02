# 自包含發佈（self-contained）：把 .NET 10 Desktop Runtime 與 ASP.NET Core
# （桌面程式內建的 Web 服務用）一併烤進輸出資料夾。
# 目標機器不需要安裝或下載任何 .NET 元件——解壓資料夾即用。
#
# 用法（repo 根目錄）：
#   powershell -File scripts\publish-desktop.ps1
#   powershell -File scripts\publish-desktop.ps1 -Output D:\dist\HeliVMS
#   powershell -File scripts\publish-desktop.ps1 -FfmpegDir D:\ffmpeg\bin   # 指定隨包的 ffmpeg 來源
#   powershell -File scripts\publish-desktop.ps1 -NoFfmpeg                 # 不隨包（預設會嘗試隨包）
#
# ffmpeg 隨包：程式會優先使用「程式資料夾旁的 ffmpeg\ 子資料夾」（FfmpegLocator 的第一順位），
# 所以把 ffmpeg.exe／ffprobe.exe 複製進輸出的 ffmpeg\ 就是隨包——零 PATH、零環境變數。
# 注意授權：隨附 GPL 建置的 ffmpeg 需依其授權提供授權聲明與原始碼取得方式（腳本會一併複製
# 來源資料夾內的 .txt 授權檔，並在沒有時寫出 NOTICE）。

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

# ── ffmpeg／ffprobe 隨包 ────────────────────────────────────────
# 來源解析順序：-FfmpegDir > HELIVMS_FFMPEG 環境變數 > PATH（Get-Command）。
# PATH 命中的可能是 shim（scoop 的 stub 行程，單獨複製出去會壞），
# 所以若旁邊有同名 .shim 文字檔就解析它指向的真身。
# 找不到時不讓發佈失敗：只警告——輸出仍可用，只是要依舊慣例另行提供 ffmpeg。

function Resolve-RealExe([string]$found) {
    # shim 檔（例如 scoop）內容是一行「path = <真實 exe>」；照著走。
    $shim = [IO.Path]::ChangeExtension($found, '.shim')
    if (Test-Path $shim) {
        foreach ($line in Get-Content $shim) {
            if ($line -match '^\s*path\s*=\s*(.+)$') {
                return $Matches[1].Trim().Trim('"')
            }
        }
    }
    return $found
}

$ffmpegIncluded = $false
if (-not $NoFfmpeg) {
    $ffmpegExe = $null
    $ffprobeExe = $null

    if ($FfmpegDir -ne '') {
        $candidate = Join-Path $FfmpegDir 'ffmpeg.exe'
        if (Test-Path $candidate) { $ffmpegExe = (Resolve-Item $candidate).Path }
        $candidateProbe = Join-Path $FfmpegDir 'ffprobe.exe'
        if (Test-Path $candidateProbe) { $ffprobeExe = (Resolve-Item $candidateProbe).Path }
        if ($null -eq $ffmpegExe) {
            Write-Error "-FfmpegDir 底下沒有 ffmpeg.exe：$FfmpegDir"
            exit 1
        }
    }
    else {
        if ($env:HELIVMS_FFMPEG -and (Test-Path $env:HELIVMS_FFMPEG)) {
            $ffmpegExe = (Resolve-Item $env:HELIVMS_FFMPEG).Path
        }
        else {
            $cmd = Get-Command ffmpeg -ErrorAction SilentlyContinue
            if ($null -ne $cmd) { $ffmpegExe = Resolve-RealExe $cmd.Source }
        }

        if ($null -ne $ffmpegExe) {
            # ffprobe 優先取同資料夾的兄弟檔（官方組建都是同一包）。
            $sibling = Join-Path (Split-Path $ffmpegExe -Parent) 'ffprobe.exe'
            if (Test-Path $sibling) {
                $ffprobeExe = $sibling
            }
            else {
                if ($env:HELIVMS_FFPROBE -and (Test-Path $env:HELIVMS_FFPROBE)) {
                    $ffprobeExe = (Resolve-Item $env:HELIVMS_FFPROBE).Path
                }
                else {
                    $cmdProbe = Get-Command ffprobe -ErrorAction SilentlyContinue
                    if ($null -ne $cmdProbe) { $ffprobeExe = Resolve-RealExe $cmdProbe.Source }
                }
            }
        }
    }

    if ($null -eq $ffmpegExe) {
        Write-Warning "找不到 ffmpeg.exe，發佈不隨包（輸出仍可用，但目標機器需自行提供 ffmpeg）"
    }
    else {
        $dest = Join-Path $Output 'ffmpeg'
        New-Item -ItemType Directory -Force -Path $dest | Out-Null
        Copy-Item $ffmpegExe (Join-Path $dest 'ffmpeg.exe') -Force
        if ($null -ne $ffprobeExe) {
            Copy-Item $ffprobeExe (Join-Path $dest 'ffprobe.exe') -Force
        }

        # 授權隨行：來源資料夾裡的 .txt（LICENSE／README 之類）一併帶走。
        $srcFolder = Split-Path $ffmpegExe -Parent
        Get-ChildItem $srcFolder -Filter '*.txt' -ErrorAction SilentlyContinue |
            ForEach-Object { Copy-Item $_.FullName (Join-Path $dest $_.Name) -Force }

        # 來源沒有任何授權檔時補一份 NOTICE：隨包 GPL 元件必須附授權聲明與原始碼取得方式。
        if (-not (Get-ChildItem $dest -Filter '*.txt' -ErrorAction SilentlyContinue)) {
            @'
ffmpeg / ffprobe are bundled with HeliVMS.

ffmpeg is licensed under the GNU General Public License. If you redistribute
this folder, you must include the corresponding license text and offer the
corresponding source for the exact ffmpeg build you ship
(https://ffmpeg.org/download.html). Replace this notice with the license file
that accompanied your ffmpeg build.
'@ | Set-Content -Encoding UTF8 (Join-Path $dest 'NOTICE-ffmpeg.txt')
        }

        $ffmpegIncluded = $true
        $versionLine = (& $ffmpegExe -version | Select-Object -First 1)
        Write-Host "ffmpeg 隨包：$ffmpegExe"
        Write-Host "  $versionLine"
    }
}

$size = [math]::Round((Get-ChildItem $Output -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
if ($ffmpegIncluded) { $ffmpegNote = '含 ffmpeg 隨包' } else { $ffmpegNote = 'ffmpeg 另行提供' }
Write-Host "OK: $Output（$size MB，自包含免安裝 Runtime；$ffmpegNote）"
