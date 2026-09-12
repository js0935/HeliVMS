---
軟體屬名：禾秝軟體開發團隊
代碼撰寫：洪俊士
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
└─ docs/  ARCHITECTURE.md（21 章藍圖）· BRAND.md（品牌規範）
```

## 建置

需要 .NET 10 SDK（Windows）。

```powershell
dotnet restore HeliVMS.slnx
dotnet build HeliVMS.slnx -c Release
dotnet test  HeliVMS.slnx -c Release
```

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