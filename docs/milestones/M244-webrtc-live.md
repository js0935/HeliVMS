# M244 定義：WebRTC 即時監看（WHEP）

> 本檔為里程碑定義，實作前先寫定，避免邊做邊改需求。
> 對應 `docs/ARCHITECTURE.md` §14.7 #1（P0 剩餘項）、§17 即時監看、§3.3 管道。

## 1. 為什麼是這件事

現況：遠程已能回放**已錄影**內容（M239 HLS VOD），但看不到「現在這一刻」。
P0 缺口就是這一項。延遲需求（警報確認要看到當下）在多分割監看下尤其明顯。

## 2. 技術選型與理由

評估過三條路：

| 方案 | 結論 |
|---|---|
| libwebrtc 原生綁定 | 需 C++ 建置鏈，Windows-only，與 repo「純託管」現況不符 |
| 外部媒體閘道（MediaMTX／Janus） | 多一個必須部署與版本綁定的常駐服務；repo 雖已依賴 ffmpeg，但那是**呼叫端**而非**伺服器端**元件 |
| **.NET 內建 SFU** | 採此方案 |

採用的函式庫是 **SIPSorcery**（BSD 3-Clause，與本 repo 的 MIT 相容；純託管、
有明確 `net10.0` target）。先前提到「Pion.WebRTC」是錯的——Pion 是 Go 實作，
沒有官方 C# 套件；而 NuGet 上同名的 `WebRTC` 套件屬於 webrtc-uwp（Optical Tone），
不是伺服器端 SFU 實作，不可誤用。

### 關鍵設計：SFU 不解碼

ffmpeg 對每路通道做**一次** RTSP → H.264 RTP 轉碼，SFU 只把同一份 RTP payload
轉送給 N 個瀏覽器（各自獨立 SRTP 加密）。不解碼的好處：

- CPU 只隨**通道數**成長，不隨**同時觀看人數**成長。這對 §17 的 16/25/32 分割是決定性的。
- 不引入解碼器相依，也不必在伺服器維護編碼器 profile。
- 延遲由 ffmpeg 的 GOP／關鍵幀間距決定，實測目標 < 1 秒（對比 HLS 的 2–6 秒）。

代價是**所有觀看者必須支援同一編碼**。選 H.264 而非 VP8，因為 Safari／iOS 的
WebRTC 只認 H.264；Chrome／Edge／Firefox 皆支援。

#### 編碼器可用性檢查

若伺服器端 ffmpeg 沒有 `libx264`（H.264 RTP 需要 Annex-B 位元流），啟動時要明確失敗
並說明原因，不能靜默退成沒有畫面。`FfmpegEncoderProbe` 在啟動 publisher **之前**先問一次
`ffmpeg -encoders`：

- 探測結果依 ffmpeg 路徑快取（每次都開行程會讓第一位觀看者多等一次）。
- 比較的是**名稱欄整欄相等**，不是 `Contains`：`libx264` 是 `libx264rgb` 的前綴，
  而 RGB 變體輸出 4:4:4，SFU 端與瀏覽器都解不出來——症狀又是「連上了沒有畫面」。
- **「缺少」與「問不到」是兩件事，不能合併。** `Unavailable`（ffmpeg 不存在、路徑打錯、
  探測逾時）刻意**不**擋下 publisher：該由 `Process.Start` 回出帶
  `HELIVMS_WHEP_FFMPEG` 的診斷。若把它說成「沒有 libx264」，維運會在正確的主機上
  去換 ffmpeg build，而真正要做的只是改一個環境變數。

沒有這道檢查時的症狀值得記錄：ffmpeg 照樣被啟動、讀完 RTSP、然後才以
`Unknown encoder 'libx264'` 退出，使用者等滿 `PublisherStartTimeout` 才看到一整段
ffmpeg stderr——而「遠端看不到畫面」與「ffmpeg 沒裝 libx264」之間沒有任何可見的連結。

## 3. 元件

```
HeliVMS.Rtc（新增專案）
├─ LivePublisher      每通道一個 ffmpeg 進程 → 127.0.0.1:<port> H.264 RTP
├─ RtpIngest          接收 RTP，記錄 SSRC/時間戳，供 SFU 轉送
├─ WebRtcSfu          每個 WHEP 會話一個 PeerConnection；轉送不解碼
├─ WhepSessionStore   會話帳本（建立／查找／計數／逾時回收）
├─ IceCandidateRewriter  把 answer 裡的 host 候選換成對外位址（NAT 1:1 映射）
├─ FfmpegEncoderProbe  啟動前確認這台 ffmpeg 有 H.264 編碼器（見 §2）
└─ WhepOptions        STUN／TURN／公開位址／逾時等設定
```

### 3.1 跨 NAT 設定（環境變數）

| 變數 | 用途 | 備註 |
|---|---|---|
| `HELIVMS_WHEP_STUN` | STUN 網址，逗號分隔 | 只影響 srflx 候選產生 |
| `HELIVMS_WHEP_TURN` | TURN 網址，逗號分隔 | 格式 `url[;username;credential]` |
| `HELIVMS_WHEP_TURN_USERNAME` / `..._CREDENTIAL` | 給沒自帶認證的 TURN 項目套用 | 支援 TURN REST 動態密碼（寫在項目上優先） |
| `HELIVMS_WHEP_PUBLIC_HOST` | 改寫 answer 中 host 候選的**對外位址** | **必須是 IP**；DNS 名稱會被拒並記錄 |
| `HELIVMS_WHEP_PUBLIC_PORT` | 對外映射埠 | 不設則保留實際 listen 埠 |

兩個設定層次不可互相取代：`PUBLIC_HOST` 解決「1:1 NAT／固定對外位址」，
`TURN` 解決「對稱 NAT、沒有固定對外位址」。實務上跨網段部署**兩者都要**——
瀏覽器仍需以 TURN relay 作為最後手段，即使有 `PUBLIC_HOST`。
格式錯誤一律退回預設並寫 `stderr`（不讓單一設定值擋住整台主機開機）。

## 4. API 契約（WHEP）

採 WHEP 而非自訂協商：它是純 HTTP 的 SDP 交換，**瀏覽器端不需要任何 JS 相依**，
與 M239 SPA「無外部相依」的既有原則一致。

| 方法 | 路徑 | 行為 |
|---|---|---|
| `POST` | `/api/stream/{channelId}/whep` | 請求主控 `application/sdp`，回 `201` ＋ `Location` ＋ `application/sdp`（answer） |
| `DELETE` | `/api/stream/whep/{sessionId}` | 關閉會話，回 `204` |
| `GET` | `/api/stream/{channelId}/whep` | 該通道目前的觀看狀態（subscriber 數、publisher 是否就緒），供 UI 顯示 |

`GET` 另外回報 `turnConfigured` / `publicHostConfigured`。理由見 §7：這兩個值是環境變數，
操作員在畫面上看不到自己沒設定，而「遠端偶爾連不上」正是它們最常見的症狀。前端若只能
猜，會犯兩種錯之一——對已配好 TURN 的主機說「沒有 TURN」（操作員去重設一個已生效的
設定），或對沒 TURN 的主機什麼都不說。因此由伺服器據實回答。舊版伺服器沒有這兩個欄位時
前端**不顯示**任何提示：寧可少一句提醒，也不要在沒有證據時給出一個可能是錯的結論。

不實作 trickle ICE（PATCH）：本機／同網段情境非必要，且 SDP 一次交換更少出錯。
此取捨要在架構文件寫明。

## 5. 必須遵守的既有契約

- **授權**：`LicenseService` 的合併檢查（§19.4），即時串流要對應一個 feature key；
  未授權時 403 必須**說明缺哪幾項**，不能只回空串流。
- **API 金鑰**：走既有 `ApiKeyAuthMiddleware`；`?key=` 僅允許 WebSocket 升級請求的
  規則**不得**為了 WHEP 而放寬——WHEP 是普通 POST，秘密必須放標頭。
- **路徑／憑證**：RTSP 位址一律經 `RtspStreamResolver` 組合，帳密不得落進
  日誌、稽核或錯誤訊息（與 M235／M239 同一條紅線）。
- **稽核**：`stream.live.start` / `stream.live.stop` 寫稽核日誌，記錄誰在何時看了哪一路。
- **上限**：同一通道的同時觀看人數要有上限，避免單一操作員開 32 個分頁打爆 CPU。
  超限回 429。

## 6. 測試與驗收

- 單元測試（不需真實攝影機、不需真實 WebRTC 對端）：
  - `RtspStreamResolver` 複用既有測試，不重寫。
  - `WhepSessionStore` 建立／查找／重複 ID／逾時回收／併發安全。
  - ffmpeg 參數組裝（`-c:v libx264 -tune zerolatency -f rtp -payload_type 96` 等）
    以純函式測試，確認 latency 選項與位元流格式真的送出；**並且實際執行 ffmpeg**
    （`lavfi testsrc` → libx264 → RTP → `RtpIngest`），確認 muxer 名稱真的存在。
    純函式測試抓不到拼錯的 muxer 這類錯誤——症狀會是「所有攝影機都連不上」，
    而測試全綠。
  - 授權閕門與 API 金鑰路徑比照既有 API 測試寫法。
- **端到端整合測試**（`PublisherIntegrationTests`）：`PublisherStarters.StartFfmpeg` →
  `LivePublisher.Start` → `ChannelRuntime` 等首包 → `RtpIngest` → 觀看者，整條走正式的
  `LiveEncodeOptions` 參數（含 `-rtsp_transport tcp`）。這一段此前完全沒有被執行過，而
  參數拼錯正是發生在中間那一段。
  - 「攝影機」是測試內自建的 `FakeRtspCamera`：只實作 interleaved TCP 的 OPTIONS／
    DESCRIBE／SETUP／PLAY／TEARDOWN。ffmpeg 的 `rtsp` muxer 沒有 listen 模式，
    所以「用第二個 ffmpeg 假裝攝影機」不可行。
  - 串流內容是 **ffmpeg 自己產生的 RTP 封包**（先用 `-f rtp` 錄進 UDP，再以 interleaved
    TCP 重播）。自己實作 H.264 over RTP（FU-A 分片、marker bit、SSRC）出錯時症狀是
    「ffmpeg 認得串流卻永遠不吐影格」，極難診斷；交給 ffmpeg 產生後這層就消失了。
  - 已用 mutation 驗證：把 `-f rtp` 改回 `-f rte`，此測試會如實失敗。
- **契約測試**：掃描 `src`，確保每個 WHEP 端點都過授權閘門（呼應 §19.4 既有做法）。
- **真實 WebRTC 堆疊的協商測試**（`WhepLoopbackTests`）：在此之前，整個 repo 沒有任何
  測試讓**真的** `RTCPeerConnection` 走完一次 SDP 協商——不是注入假的 `IWhepPeer`，就是
  餵壞掉的 SDP 換一個 400。而 payload type、profile-level-id、packetization-mode、
  方向（sendonly／recvonly）任一項寫錯，症狀都是「連得上但沒有畫面」，log 乾乾淨淨。
  這條測試用真的 `RTCPeerConnection` 當訂閱端，驗證：
  - answer 確實宣告 `sendonly`、`H264/90000`、PT 96、`packetization-mode=1`、
    `profile-level-id=42e01f`；
  - answer 自帶 ICE ufrag／pwd／候選與 DTLS fingerprint（不做 trickle，缺了就沒路）；
  - 對端 `setRemoteDescription` 回 `OK` 且連線狀態真的走到 `connected`，也就是
    **ICE 選到路徑且 DTLS 握手完成**；
  - `HELIVMS_WHEP_PUBLIC_HOST` 設定時 host 候選真的被改寫成對外位址（NAT 部署情境）。
  - 已用 mutation 驗證：把 `WhepPeer` 的 `SendOnly` 改回 `RecvOnly`，此測試會如實失敗。
  - **邊界（刻意不做）**：不驗證 DTLS-SRTP 收到的媒體內容。SIPSorcery 10 沒有公開的
    per-receiver 收包事件（無 `ontrack`、無 `RTCRtpReceiver`，收包掛在
    `MediaStream.OnRtpPacketReceivedByIndex` 而 `PeerConnection` 不公開那些 MediaStream）。
    為了收包在測試裡重建瀏覽器，等於在測第三方函式庫的正確性且測試極易腐化。
- mutation 驗證至少四個方向：上限失效（回 200 而非 429）、逾時不回收、
  會話帳本不記錄（等於沒有上限）、憑證寫進錯誤訊息。
- **編碼器缺失的啟動閘門**（`LivePublisherEncoderGuardTests`）：缺 `libx264` 必須在碰攝影機之前
  就失敗，且訊息要點名編碼器與解法（裝 `ffmpeg-full`）；「ffmpeg 不存在」則必須繼續走
  原路徑並保留 `HELIVMS_WHEP_FFMPEG` 的診斷。已用 mutation 驗證：把
  `Missing` 判斷反向，整個 Rtc 專案有 8 條測試會紅，含真實 publisher 整合測試。
- SPA 側：來源契約斷言（走單一認證傳輸層、不用裸 fetch 帶 key、
  不引入外部 JS CDN），以及版面回歸斷言（`app.layout.test.js`）。
  現場回報過「按鈕被遮蔽／欄位看不到」，症狀全在 CSS 而當時沒有任何測試會紅，
  因此面板捲動、topbar 換行、表單不拉伸、影片不被裁切等都逐條釘住。

## 7. 明知不做（要寫進架構文件的取捨）

- 不做 16/25/32 分割的完整多分割布局（§17.1），本里程碑先交付單路即時串流。
- 不做 PTZ 下推（§17.4）、數位放大（§17.3）、快照（§8.4）。
- 不做音訊轉送（攝影機音軌編碼多樣，會讓 Safari 相容性複雜化；先純視訊）。
- **不架設 TURN 伺服器**，但支援設定它（見 §3.1）：M244 交付的是「能不能指到 TURN」，
  伺服器本身屬於部署項目（coturn 等），不進這個 repo。沒有 TURN 且沒有 `PUBLIC_HOST`
  時，對稱 NAT 環境會連不上，此限制須在 UI 與文件明說。
  - **UI 已明說**：即時監看面板在 `turnConfigured` 為 `false` 時顯示一則提示，說明
    對稱／多層 NAT 下遠端瀏覽器連不上、要設 `HELIVMS_WHEP_TURN` 才有 relay 候選。
    值來自 `GET`（§4），不是前端推測；問不到就不顯示（見 §4 說明）。
  - 這個提示的價值在於**症狀本身完全沒有錯誤**：`PeerConnection` 會停在 `connecting`
    然後 `failed`，沒有例外、沒有 4xx、伺服器端日誌乾乾淨淨。少了這句話，使用者只能
    得出「壞了」，維運則只能逐一排除設定。
- 閒置計時一律走注入的 clock（`WhepSessionStore`、`LiveStreamService`、`ChannelRuntime`
  共用同一個時間基準）：最後一位觀看者離開時**不**重設計時基準，否則逾時回收會把
  publisher 的倒數重啟，讓「回收後立刻關掉 ffmpeg」變成要多等一個週期。