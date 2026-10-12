---
軟體屬名：禾秝軟體開發團隊
代碼撰寫：洪俊士
版本：v0.1.0
---

# HeliVMS Web 主控台操作說明

網頁主控台（`src/HeliVMS.Web`）是一組**零 npm 依賴**的純靜態 SPA（ES modules ＋
vanilla JS），由 `src/HeliVMS.WebApi`（ASP.NET Core Minimal API）在**同一個行程**內
託管：靜態檔走 `UseStaticFiles`，所有非 `/api` 路徑 fallback 到 `index.html`，資料
與桌面程式共用同一份 SQLite。它不是另一個後端，而是桌面系統的遠端操作面。

> 這份文件講的是**網頁主控台**。WPF 桌面程式的即時監看請看 `docs\LIVEVIEW.md`；
> WebRTC／WHEP 的里程碑細節在 `docs\milestones\M244-webrtc-live.md`。

## 1. 啟動

需要 .NET 10 SDK，且 `ffmpeg`（含 `libx264`）在 `PATH`（或設 `HELIVMS_FFMPEG`）——
只有「即時監看」與「遠程回放」需要它，其餘面板不需要。

```powershell
# 一定要先設 API 金鑰，否則非 Development 環境會 fail-closed（所有 /api/* 回 401）
$env:HELIVMS_API_KEY = "換成你自己的長字串"
dotnet run -c Release --project src/HeliVMS.WebApi
```

- repo 內**沒有任何** `launchSettings.json`／`appsettings.json`，所以監聽位址用框架
  預設值：`http://localhost:5000`。要讓其他機器連進來：

  ```powershell
  dotnet run -c Release --project src/HeliVMS.WebApi --urls http://0.0.0.0:8080
  ```

- 也可以直接跑建置好的 `src\HeliVMS.WebApi\bin\Release\net10.0\HeliVMS.WebApi.exe`；
  `Program.FindWebRoot` 會從執行檔目錄往上找 `src\HeliVMS.Web\index.html`，找不到就
  只提供 API（沒有網頁）。找不到時可用 `HELIVMS_WEB` 明確指定 SPA 目錄。
- **沒有 HTTPS**：`Program.cs` 不呼叫 `UseHttps`／`UseUrls`。要對外曝露請自己在前方
  放一個 TLS 反向代理（CSP 已允許 `wss:`）。
- 瀏覽器開 `http://localhost:5000`。

## 2. 環境變數

| 變數 | 作用 | 備註 |
|---|---|---|
| `HELIVMS_API_KEY` | 所有 `/api/*`（`/api/health` 除外）的 Bearer 金鑰 | 未設時：`Development` 用開發金鑰 `helivms-dev-key`，其餘環境**一律 401** |
| `HELIVMS_DATA` | 資料根目錄 | 預設 `C:\HeliVMSData`，失敗則退回 `%LOCALAPPDATA%\HeliVMS` |
| `HELIVMS_DB` | 明確指定 SQLite 檔 | 覆蓋 `HELIVMS_DATA\index.db`；與桌面共用同一份 |
| `HELIVMS_WEB` | SPA 目錄覆寫 | 目錄內需有 `index.html` |
| `HELIVMS_LICENSE` | 授權檔路徑 | 預設 `%LOCALAPPDATA%\HeliVMS\license.lic` |
| `HELIVMS_LICENSE_PUBLIC_KEY` | 授權驗證公鑰（PEM） | 供金鑰輪替 |
| `HELIVMS_RECORDINGS_ROOT` | 錄影根目錄 | 未設時由資料目錄推導，且自動避開系統碟 |
| `HELIVMS_ALLOWED_ROOTS` | 證據／分享／備份可存取的根目錄 | **未設即全部拒絕**（fail-closed） |
| `HELIVMS_FFMPEG` / `HELIVMS_FFPROBE` | 指定執行檔 | 否則找內附 `ffmpeg\` → `PATH` |
| `HELIVMS_WHEP_*` | 即時監看（見 §6） | `TURN`／`PUBLIC_HOST`／`MAX_VIEWERS` 等 |

## 3. 認證

有兩層，彼此獨立：

1. **API 金鑰（連線層）**：`ApiKeyAuthMiddleware` 檢查 `Authorization: Bearer <key>`，
   用 `FixedTimeEquals` 常數時間比對。金鑰只保護 `/api/*`；`index.html`／`app.js` 等
   靜態檔不需要金鑰。WebSocket 才允許 `?key=`，一般請求與 WHEP **刻意不接受** query
   key（避免機密寫進 access log）。連續失敗 20 次/60 秒會回 `429`。
2. **帳號登入（角色層）**：`POST /api/accounts/authenticate`，回 `{role, displayName}`。
   角色為 `admin`／`viewer`。SPA 用它決定顯示哪些面板（`viewer` 隱藏 12 個管理面板）。

在頂列「API 金鑰」框輸入金鑰（存 `localStorage`）；要使用管理面板再用帳號登入。

> ⚠️ **角色只在網頁前端生效**：後端沒有 RBAC，任何持有 API 金鑰的呼叫端都能打所有
> 端點。角色只是「少顯示幾個面板」，不是權限邊界。要真正的權限控管，請把 API 金鑰
> 視為最高權限並妥善保管。
>
> ⚠️ 啟用帳號登入（`auth.enabled=1`）前**必須先有一個啟用中的 admin**，否則會把自己
> 鎖在門外——桌面端的 `SettingsWindow` 會拒絕這種設定，`App.PerformLogin` 也會 fail-open
> 並記錄 `%TEMP%\helivms-auth-bootstrap.log`。

## 4. 介面面板

**所有持有效金鑰者可見**（授權不足時該面板會顯示被擋的旗標）：

- 頻道、警報看板（確認／處置／分診）、事件搜尋、回放時間軸
- **即時監看**（WebRTC／低延遲，見 §6）、**遠程回放**（HLS，見 §6）
- 地圖視界、Smartwall 電視牆、錄影排程、巡檢、日報表
- 門禁事件、AI 偵測、即時警報串流（WebSocket）、通知投遞、匯出工作

**僅 `admin` 可見**（`app.js` 的 `updateChrome`）：

帳號、登入設定、稽核日誌、警示規則、分享連結、遮罩區、證據鎖定、證據包、備份、
登入供應商（LDAP／OIDC）、異地複製、系統健康。

## 5. 授權旗標（後端閘門）

`LicenseGateMiddleware` 依路徑前綴擋，回 `403` 並在 body 帶 `feature`／`decision`，
SPA 的 `#lic` 徽章會說明缺哪一項。console 本身不受 `remote` 限制（核心版客戶在
自己機器上仍可開）。

| 路徑前綴 | 需要的旗標 |
|---|---|
| `/api/stream`（即時 WHEP ＋ HLS 回放） | `remote` |
| `/api/shares` | `remote` |
| `/api/recording/schedules`、`/api/patrols` | `schedule` |
| `/api/detections`、`/api/clip`、`/api/audio` | `ai` |
| `/api/auth/providers` | `ad` |
| `/api/health`、`/api/license` | 一律放行 |

## 6. 即時監看與遠程回放

**即時監看（WebRTC／WHEP）**：面板「即時監看」→ 輸入頻道編號 → 開啟。前端是原生
`RTCPeerConnection`（`live.js`，不用 WHEP 函式庫）。端點：

- `POST /api/stream/{channelId}/whep`（`Content-Type: application/sdp`）→ `201` ＋ answer
- `DELETE /api/stream/whep/{sessionId}` → `204`（冪等）
- `GET /api/stream/{channelId}/whep` → 觀看數／發佈狀態／`turnConfigured`

前置條件：

1. 授權 `remote`。
2. 請求以 **header** 帶 API 金鑰（WHEP 不接受 query key）。
3. `ffmpeg`（含 `libx264`）可用。
4. 該頻道有 RTSP 來源位址。
5. 每頻道觀看數未超過上限（`HELIVMS_WHEP_MAX_VIEWERS`，預設 8），超過回 `429`。
6. 跨網段／對稱 NAT 需設 `HELIVMS_WHEP_TURN`（中繼）或 `HELIVMS_WHEP_PUBLIC_HOST`
   （1:1 NAT，**只接受裸 IP**）。兩者都沒設時，瀏覽器會卡在 connecting 而後 failed，
   日誌卻是乾淨的——SPA 會依 GET 狀態顯示 NAT 警告。
7. 不做 trickle ICE：前端會等 `iceGatheringState === 'complete'` 才送 offer，伺服器要求
   answer 必須帶完整候選。

伺服器在**收到第一個 RTP 封包後**才回 `201`——所以「開啟成功」就代表真的有畫面，
不會出現「連上了卻永遠黑畫面、日誌無訊息」的情況。

**遠程回放（HLS）**：面板「遠程回放」→ 選頻道與起訖時間 → 載入。走已錄影區段，
同屬 `remote` 旗標。

## 7. 疑難排解

| 症狀 | 原因 |
|---|---|
| 所有 `/api/*` 回 401 | 沒設 `HELIVMS_API_KEY`（非 Development 會 fail-closed），或金鑰不符 |
| 401 次數過多後變 429 | 觸發暴力破解防護（20 次/60 秒），等 `Retry-After` 秒 |
| 某面板一片空白且提示缺功能 | 對應授權旗標未開（看 `#lic` 徽章） |
| 網頁打不開、只有 API 有回應 | 找不到 SPA 目錄，設 `HELIVMS_WEB` 指向含 `index.html` 的資料夾 |
| 即時監看一直 connecting／failed | 缺 `HELIVMS_WHEP_TURN`／`PUBLIC_HOST`，或 `ffmpeg` 不在 `PATH` |
| 遠端看得到卻沒資料 | 桌面程式與 WebApi 是否指到同一份 `HELIVMS_DB`／`HELIVMS_DATA` |

## 8. 已知限制

- 無 TLS（自行以反向代理處理）。
- 無 JWT、後端無 RBAC；API 金鑰即最高權限。
- 即時監看無音軌、不支援 H.265；無 TURN 時對稱 NAT 跨網段會失敗。
- SPA 目前提供 `zh-TW`／`en` 兩語系。
