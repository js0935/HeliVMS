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
只有「即時監看」與「遠程回放」需要它，其餘分頁不需要。

```powershell
# 一定要先設 API 金鑰，否則非 Development 環境會 fail-closed（除了 /api/health 與
# /api/accounts/authenticate 登入外，其餘 /api/* 回 401）
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
- 前端靜態檔（`index.html`／`app.js`／`live.js`…）一律以 `Cache-Control: no-cache` 供應：
  瀏覽器每次都會回伺服器驗證（檔案沒變仍回 `304`），所以換版後重新整理即可看到，不會被
  啟發式快取留在舊版——遠端操作站尤其重要，否則「明明修好了、瀏覽器還在跑舊版」很難查。

## 2. 環境變數

| 變數 | 作用 | 備註 |
|---|---|---|
| `HELIVMS_API_KEY` | 所有 `/api/*`（`/api/health` 與 `POST /api/accounts/authenticate`、`logout` 除外）的 Bearer 金鑰 | 未設時：`Development` 用開發金鑰 `helivms-dev-key`，其餘環境**一律 401** |
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
2. **帳號登入（M306，取代手動金鑰）**：`POST /api/accounts/authenticate` 驗證 **WPF
   同一組身分帳號**（`AuthService`，含失敗鎖定與停用檢查），成功後回
   `{role, displayName, token}`——`token` 是伺服器簽發的工作階段憑證，**與 API 金鑰同層**：
   之後 `Authorization: Bearer <token>` 就能存取全部 `/api/*`。登入端點本身**不需要金鑰**
   （同 `/api/health` 一律放行），所以登入成功即取得 API 存取權，不必先餵一顆 API 金鑰。

- **登入成功後**：SPA 把 token 存進原本的「API 金鑰」框（`localStorage`），所有的
  REST 與 WebSockets（`?key=`）都改用它；角色為 `admin`／`viewer`，`viewer` 隱藏
  12 個管理面板。
- **登出**：`POST /api/accounts/logout` 撤銷該 token（需帶 `Bearer <token>`），之後
  再帶它就回 401。token 有效 12 小時，主機（或桌面程式）重啟即失效。
- **API 金鑰仍可用**：以金鑰為憑的舊呼叫端（腳本、中央站）完全不受影響；金鑰與
  token 是同一層憑證，任一個通過即可。

> ⚠️ **角色只在網頁前端生效**：後端沒有 RBAC，任何持有 API 金鑰或工作階段 token 的
> 呼叫端都能打所有端點。角色只是「少顯示幾個面板」，不是權限邊界；「登入成功即取得
> API 存取權」也意味著 viewer 帳號登入後在後端與 admin 同權。若需要真正的角色權限
> 邊界，請自行在後端加上 RBAC（目前的登入閘門只驗證「這個帳號存在且密碼正確」）。
>
> ⚠️ 啟用帳號登入（`auth.enabled=1`）前**必須先有一個啟用中的 admin**，否則會把自己
> 鎖在門外——桌面端的 `SettingsWindow` 會拒絕這種設定，`App.PerformLogin` 也會 fail-open
> 並記錄 `%TEMP%\helivms-auth-bootstrap.log`。

## 4. 介面面板

主控台是**六個分頁**（頁籤），只保留這六項功能；其餘舊面板已移除。持有效金鑰或工作
階段者皆可見（授權不足時對應動作顯示 `403` 理由）：

- **即時監看**：WebRTC／WHEP 低延遲（§6）
- **遠程回放**：HLS 已錄影區段（§6）
- **截圖**：由即時畫面用 canvas 擷取一格下載 PNG（免後端端點，§6）
- **PTZ 控制**：ONVIF 移動／停止／回預設位與預設點管理（§6）
- **事件搜尋**：關鍵字搜尋事件表（唯讀）＋ WebSocket 即時警報流
- **IO 接點**：攝影機乾接點——列出 DI、列出並切換 DO（§6）

頂列另有健康、連線、授權徽章、API 金鑰／登入、語言選單。後端沒有 RBAC（見 §3 警告），
所以 `admin` 與 `viewer` 目前看到相同的六個分頁。

## 5. 授權旗標（後端閘門）

`LicenseGateMiddleware` 依路徑前綴擋，回 `403` 並在 body 帶 `feature`／`decision`，
SPA 的 `#lic` 徽章會說明缺哪一項。console 本身不受 `remote` 限制（核心版客戶在
自己機器上仍可開）。

| 路徑前綴 | 需要的旗標 |
|---|---|
| `/api/stream`（即時 WHEP ＋ HLS 回放） | `remote` |
| `/api/central/playback`（跨機回放代理） | `remote` |
| `/api/shares` | `remote` |
| `/api/recording/schedules`、`/api/patrols` | `schedule` |
| `/api/detections`、`/api/clip`、`/api/events/search/semantic`、`/api/audio` | `ai` |
| `/api/auth/providers` | `adsso` |
| `/api/health`、`/api/license` | 一律放行 |

> `/api/ptz` 與 `/api/io` **刻意不在閘門內**：兩者都是頻道的基本操作，不屬於任何授權能力旗標。

## 6. 即時監看、回放、截圖、PTZ 與 IO

**即時監看（WebRTC／WHEP）**：面板「即時監看」→ 選版面（1／4／9／16／25 分割）＋多選頻道
→ 開啟。每一格各自一條 WHEP 會話；選超過格數只取前 N 路，「停止」一次收掉全部。前端是原生
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
   日誌卻是乾淨的——這種失敗不會有錯誤訊息，只能靠上面的設定事前避開。
7. 不做 trickle ICE：前端會等 `iceGatheringState === 'complete'` 才送 offer，伺服器要求
   answer 必須帶完整候選。

伺服器在**收到第一個 RTP 封包後**才回 `201`——所以「開啟成功」就代表真的有畫面，
不會出現「連上了卻永遠黑畫面、日誌無訊息」的情況。

**遠程回放（HLS）**：面板「遠程回放」→ 選來源節點（本機／子節點）與頻道、起訖時間
→ 載入。走已錄影區段，同屬 `remote` 旗標；選子節點時改打跨機代理
（`/api/central/playback`）。

**截圖**：面板「截圖」→ 開即時畫面（同 §6 WebRTC）→ 擷取畫面。純前端：把
`#snap-video` 目前解碼的影格畫進 canvas 再 `toBlob('image/png')` 下載——沒有伺服器端
截圖端點，抓到的就是操作員當下看到的那一格。

**PTZ 控制**：面板「PTZ 控制」→ 輸入頻道 → 連線（列出 ONVIF 碼流 profile）。端點：

- `GET /api/ptz/{channelId}/profiles` → `{ profiles: [{ token, name }] }`
- `POST /api/ptz/{channelId}/move`（`{profileToken, pan, tilt, zoom}`）→ 連續移動（速度）
- `POST /api/ptz/{channelId}/stop`、`POST /api/ptz/{channelId}/home`
- `GET /api/ptz/{channelId}/presets?profileToken=…`、`POST …/presets`（新增）、
  `POST …/presets/{token}/goto`、`DELETE …/presets/{token}?profileToken=…`

方向盤與變焦採「按住移動、放開停止」（ContinuousMove 是速度命令，不是單步位移）。
`409` 代表該設備不支援 PTZ，與 `404`（頻道不存在）意義不同。`/api/ptz` 不受授權旗標
閘門保護（見 §5）。

**IO 接點（攝影機乾接點）**：面板「IO 接點」→ 輸入頻道 → 讀取。列出該設備的數位輸入
（DI）與繼電器輸出（DO）；每一路 DO 可用「閉合／斷開」切換。端點：

- `GET /api/io/{channelId}/inputs` → `{ inputs: [{ token, id, name, label }] }`
- `GET /api/io/{channelId}/outputs` → `{ outputs: [{ token, mode, bistable, name, label }] }`
- `POST /api/io/{channelId}/outputs/{relayToken}/state`（`{ active: true|false }`）→ 切換 DO

ONVIF **沒有**「輪詢 DI 目前狀態」的操作——輸入狀態只在事件（PullPoint／Basic
Notification）中回報，所以 DI 只列出有哪些端子，不含即時狀態；DO 則可直接切換。`409`
代表該設備不支援 IO，與 `404`（頻道不存在）意義不同。`/api/io` 不受授權旗標閘門保護
（見 §5）。

## 7. 疑難排解

| 症狀 | 原因 |
|---|---|
| 所有 `/api/*` 回 401 | 沒設 `HELIVMS_API_KEY`（非 Development 會 fail-closed），或金鑰不符 |
| 401 次數過多後變 429 | 觸發暴力破解防護（20 次/60 秒），等 `Retry-After` 秒 |
| 某面板一片空白且提示缺功能 | 對應授權旗標未開（看 `#lic` 徽章） |
| 網頁打不開、只有 API 有回應 | 找不到 SPA 目錄，設 `HELIVMS_WEB` 指向含 `index.html` 的資料夾 |
| 重新整理後前端仍是舊版 | 舊快取；靜態檔已送 `no-cache`，先確認伺服器已重啟（吃到新表頭）再重新整理 |
| 即時監看一直 connecting／failed | 缺 `HELIVMS_WHEP_TURN`／`PUBLIC_HOST`，或 `ffmpeg` 不在 `PATH` |
| 遠端看得到卻沒資料 | 桌面程式與 WebApi 是否指到同一份 `HELIVMS_DB`／`HELIVMS_DATA` |

## 8. 已知限制

- 無 TLS（自行以反向代理處理）。
- 無 JWT、後端無 RBAC；API 金鑰即最高權限。
- 即時監看無音軌、不支援 H.265；無 TURN 時對稱 NAT 跨網段會失敗。
- SPA 目前提供 `zh-TW`／`en` 兩語系。
