---
軟體屬名：禾秝軟體開發團隊
代碼撰寫：
版本：v0.1.0
---

# HeliVMS

**智慧 32 路 NVR/VMS 錄影系統**（Windows）

即時監看 · 錄影 · 回放 · 事件警報的一站式監控管理系統，支援 ONVIF 相容設備與品牌設備，32 路全時／排程／事件錄影。

## 功能總覽

- **即時監看**：多分割布局（1/4/9/16/25/32）、單路放大、ePTZ 數位放大、PTZ 控制
- **錄影**：全時／排程／事件／混合 Smart 錄影；H.264/H.265 主流直存、區段式存檔、配額汰除
- **回放**：時間軸搜尋、多路同步、倍速播放、匯出證據包
- **事件與警報**：移動偵測、失聯偵測、IO 警報、電子地圖、通知
- **AI（規劃）**：周界／侵入／目標追蹤／人數／車流／語意搜尋
- **授權**：RSA-2048 非對稱簽章序號（`HeliVMS-v2`），機器綁定

## 解決方案結構

```
HeliVMS.slnx
├─ src/
│  ├─ HeliVMS.App        WPF 殼（啟動畫面＋主視窗）
│  ├─ HeliVMS.Core       核心領域（頻道／排程／事件）
│  ├─ HeliVMS.Licensing  RSA-2048 授權驗證（§19）
│  ├─ HeliVMS.Media      媒體管線（拉流／解碼）
│  ├─ HeliVMS.Decoder    FFmpeg 解碼子進程
│  ├─ HeliVMS.Recording  錄影排程與區段管理
│  ├─ HeliVMS.Alarms     警報與事件
│  ├─ HeliVMS.Devices    ONVIF 設備管理
│  ├─ HeliVMS.Storage    SQLite＋磁碟儲存
│  └─ HeliVMS.Shared     共型別／工具
├─ tests/
│  ├─ HeliVMS.Licensing.Tests
│  └─ HeliVMS.Storage.Tests
├─ Tools/
│  ├─ HeliVMS.LicenseProducer  離線授權產生器
│  ├─ HeliVMS.Decoder          回放解碼示範
│  ├─ LicenseKeyGen(停用)      舊 HMAC 版，勿用
│  └─ LicenseKeyGenUI(停用)    舊 RSA 版雛形
└─ docs/  ARCHITECTURE.md（21 章藍圖）· BRAND.md（品牌規範）· WEB.md（Web 主控台操作）
```

## 建置

需要 .NET 10 SDK（Windows）。

```powershell
dotnet restore HeliVMS.slnx
dotnet build HeliVMS.slnx -c Release
dotnet test  HeliVMS.slnx -c Release
```

## 發佈（免安裝 .NET Runtime）

`scripts/publish-desktop.ps1` 以**自包含（self-contained）**方式發佈桌面程式：
.NET 10 Desktop Runtime 與 ASP.NET Core（內建 Web 服務用）直接烤進輸出資料夾，
**目標機器不需要安裝或下載任何 .NET 元件**——整包複製即可執行。

```powershell
powershell -ExecutionPolicy Bypass -File scripts/publish-desktop.ps1
# 輸出：publish/HeliVMS（約 250 MB，含 Runtime；腳本會驗證 hostfxr/coreclr 確實在包內）
# 自訂輸出位置： -Output D:\dist\HeliVMS
```

### ffmpeg／ffprobe 隨包

腳本**預設會隨包**：把 `ffmpeg.exe`／`ffprobe.exe` 複製進輸出的 `ffmpeg\` 子資料夾
（程式解析 ffmpeg 的第一順位就是這個資料夾——比環境變數與 PATH 優先，見 `FfmpegLocator`）。
隨包後輸出約 650 MB，目標機器完全零設定。

```powershell
# 指定來源資料夾（建議：官方 zip 解開的那份，內含授權檔會一併帶走）
powershell -ExecutionPolicy Bypass -File scripts/publish-desktop.ps1 -FfmpegDir D:\ffmpeg-8.0.1-full_build\bin

# 不隨包（輸出仍可用，目標機器自行提供 ffmpeg 到程式資料夾旁的 ffmpeg\ 或 PATH）
powershell -ExecutionPolicy Bypass -File scripts/publish-desktop.ps1 -NoFfmpeg
```

來源解析順序：`-FfmpegDir` → `HELIVMS_FFMPEG` 環境變數 → PATH。PATH 命中的若是
scoop／choco 的 shim（stub 行程），腳本會透過 `.shim` 檔解析到真正的 exe。
隨包時授權檔（來源資料夾的 `*.txt`）會一起複製；來源沒有授權檔時腳本會寫出
`NOTICE-ffmpeg.txt` 提醒補上——隨包 GPL 建置的 ffmpeg 散布時必須附授權聲明與
對應原始碼的取得方式。

## 產生授權

```powershell
dotnet run -c Release --project Tools/HeliVMS.LicenseProducer -- \
  --private .secrets\test-private.pem --cameras 32 --expire 2027-09-30 \
  --features core,ai,gis --out .secrets\license.lic
```

> 私鑰永不進入 repo（`.secrets/` 已加入 `.gitignore`）；測試授權檔位於 `%LOCALAPPDATA%\HeliVMS\license.lic`。

## 開發里程碑

| 里程碑 | 內容 |
|---|---|
| M1 | 解決方案／WPF 殼／主題／授權骨架／RTSP 測試流／CI |
| M2 | 1 路監看＋錄影＋事件閉環 |
| M3 | 回放／匯出 |
| M4 | 多路／排程／儲存管理 |
| M5 | ONVIF 自動探索與大量設備 |
| M6 | 警報／AI |
| M7 | 遠程存取（WebRTC／HLS） |
| M8 | 地圖／證據包／備份／事件工作流 |

## 授權

MIT License（見 LICENSE）。
