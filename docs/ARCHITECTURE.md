---
軟體屬名：禾秝軟體開發團隊
代碼撰寫：洪俊士
版本：v1.0.0
---

# HeliVms — 32 路 NVR/VMS 系統架構規劃

> 目標：Windows 桌面應用，同時管理、監看、錄製最多 32 路 IP 攝影機（RTSP/ONVIF），
> 具備即時監看、時間軸回放、排程錄影、警報通知。
> 本規劃參考 GitHub 開源實作之驗證技術（出處另列於文末）。

---

## 0. 核心範圍與優先序

**第一優先（核心路徑，直接決定產品成敗）：**
1. **即時監看** — 32 路同時順暢監看、低延遲、分屏切換、單路放大、狀態指示
2. **錄影與回放** — 區段錄影不間斷、時間軸順滑拖拽、跨區段無縫回放、倍速/逐幀

**第二優先（基礎支撐，核心路徑必須具備的底層）：**
- 每路獨立拉流/解碼管道、RTSP 斷線重連、硬體解碼、SQLite 索引

**後期擴充（模組化導入，不影響第一版核心閉環）：**
- **AI 辨識（§5）**：L0 運動偵測（預設）、L1 物件偵測（ONNX CPU，漏斗 32 路）、
  L2 車牌/人臉（選購、法遵預設關閉）、排程/motion 錄影規則、通知推播、
  ONVIF 探索與 PTZ、雙流錄影、事件中心、匯出。
  推理抽象層與事件引擎介面已在架構中預留，可增量導入。

> 開發順序以此為據：先把「看得順、錄得穩、回得順」閉環做到極致，再談擴充。

---

## 1. 技術棧總覽

| 層級 | 技術 | 選用理由 |
|---|---|---|
| 語言/平台 | C# / .NET 8 + WPF | 介面易做出專業監控調度台、多執行緒成熟 |
| UI 框架 | WPF + CommunityToolkit.Mvvm | 資料繫結、`Image`/自訂控制項渲染影片 |
| 多媒體核心 | **FFmpeg 6.x (Sdcb.FFmpeg 綁定)** | 解碼/封裝/協定支援最完整，C# 友好 |
| 即時播放渲染 | **libmpv (Mpv.NET-lib-)** | 內建硬體解碼、低延遲、seek 支援 |
| 索引資料庫 | SQLite + Microsoft.Data.Sqlite | 零部署、單檔、適合錄影索引 |
| 設備通訊 | onvif / onvif-go 模式（C# 端用 `onvif` NuGet） | 自動探索、Profile S 取流 |
| 運動偵測 | 自訂 .NET 積分圖/幀差比較（在次串流上運算） | 避免 32 路全分析消耗 CPU |

### 1.1 關鍵設計決策（借鏡已驗證專案）

**D1 — 錄影走「直接封裝、不重新編碼」**
> 借鏡 Moonfire NVR / FFmpeg `segment` muxer / rtsp-record。
> 32 路錄影時僅做 `-c copy`，把攝影機原始 H.264/H.265 bitstream 切成固定時長區段寫入磁碟，
> CPU 幾乎零負擔（Moonfire 在 Raspberry Pi 2 上可同時錄 6 路 1080p/30fps 且 CPU < 10%）。
> 此為 32 路錄影可行的根基，不可改為軟體編碼後儲存。

**D2 — 錄影與監看解耦**
> 監看只影響「要不要解碼」，錄影永遠獨立運作。監看關閉不中斷錄影。
> 借鏡 Frigate 的 `record` 與 `detect` 分離概念。

**D3 — 每路一條獨立處理管道**
> 單路故障（斷流/重新連線/解碼錯誤）不得影響其他 31 路。
> 每路 = 獨立的 `ChannelSession`（拉流 + 錄影 + 可選解碼巡迴）。

**D4 — 硬體解碼給「即時監看與回放」**
> 以 NVDEC (CUDA) 或 Intel QSV 解碼。Sdcb.FFmpeg 官方範例即以 `h264_qsv`/`cuda` 解碼器示範。

**D5 — 時間索引資料庫 vs 影像檔案分離**
> 影像位元流存磁碟區段，中繼資料（區段時間、事件、警報）存 SQLite —— Moonfire NVR 的混合架構，
> 讓時間軸回放與事件搜尋不需要掃描影片檔。

---

## 2. 系統模組與目錄結構

```
HeliVms/
├─ HeliVms.App/                 # WPF 殼 + 視圖 + ViewModel + 主題樣式
├─ HeliVms.Core/                # 領域模型（Camera、Channel、RecordingSegment、Event）
├─ HeliVms.Media/               # FFmpeg/mpv 封裝：拉流、解碼、segment 錄影、硬體加速
├─ HeliVms.Decoder/             # 回放解碼子進程（源自 Tools/HeliVMS.Decoder，命名管道隔離）
├─ HeliVms.Recording/           # 錄影排程、區段管理、磁碟配額、索引寫入
├─ HeliVms.Alarms/              # 運動偵測、事件引擎、通知
├─ HeliVms.Devices/             # ONVIF 探索/控制、RTSP 位址生成、網路健康度
├─ HeliVms.Storage/             # SQLite 存取層、Schema、遷移
├─ HeliVms.Licensing/           # 授權驗證核心（公鑰內嵌、設備碼、等級矩陣、導入）— §19
├─ HeliVms.Shared/              # 事件聚合、擴充方法、Logging(Serilog)
├─ Tools/                       # 廠商側工具（不入客戶安裝包）
│  ├─ LicenseProducer/          # 授權生成器（RSA-2048 簽章；源自 Tools/LicenseKeyGenUI）— §19
│  └─ (LicenseKeyGen/           # ⚠ 舊 HMAC 對稱版，安全弱，全面停用 — §19.5）
└─ docs/                        # 本規劃、資料庫結構、營運手冊
```

> Lines 74–77：`HeliVms.Decoder` 已真實存在於 `Tools/HeliVMS.Decoder`（net10.0-windows、命名管道＋JSON 協定，
> Open/Seek/Rate/Pause 指令與 Frame/Status 事件），M3 回放直接繼承此隔離架構。

### 2.1 資料夾 / 檔案儲存結構（磁碟）

```
C:\HeliVmsData\
├─ index.db                    # SQLite：設備、通道、區段索引、事件
├─ recordings\
│  └─ <channelId>\
│     └─ YYYYMMDD\
│        ├─ seg-1030-000.低碼率.mp4   # 10 分鐘一段，`-c copy`
│        └─ seg-1030-000.mp4          # 主串流錄影（可選同時錄兩流）
└─ snapshots\                  # 警報觸發快照
    └─ <channelId>-yyyymmddHHMMSS.jpg
```

---

## 3. 多媒體管線設計（核心）

### 3.1 每路通道的執行緒模型

```
ChannelSession (每路 1 個，可並行 32 個)
├─ PullThread     拉流 → 解封裝 (avformat) → demux 基本串流
│   ├─ RecordPath   打包器(muxer) 直接寫 file (video only)
│   └─ PreviewPath  選擇性：解碼 → YUV → 送入 UI 佇列
└─ HealthTimer    每秒檢查流狀態；斷線→指數退避重連 (3s→60s)
```

- 拉流協定：`rtsp_transport=tcp`（市售 IPC 不少不支援 UDP，TCP 穩定）。
- 緩衝區：`buffer_size`、`fltp`、`stimeout` 設 timeout，避免卡死。
- 斷線重連：重連不影響已錄區段；接續下一段全新 MP4（走時間戳，不嚴格接續）。
- 時基校準：以牆鐘時間寫入區段檔名與資料庫，不依賴設備時間。

### 3.2 錄影管道（D1・不重新編碼）

允許兩種錄影位元流策略（每通道可選其一）：

| 策略 | 作法 | 適用 |
|---|---|---|
| 主串流直錄 | 拉 `main` 串流 `-c copy` 存 H.264/H.265 | 畫質優先（預設）|
| 雙流 | 同時存 `main` + `sub` 兩個系列檔 | 需要畫質 + 體積節省 |

- 分段器：`segment` muxer，`segment_time=600`，`reset_timestamps=1`，
  檔名含 `%Y%m%d-%H%M%S`（借鏡 rtsp-record 的設計，`.mkv` 可容忍未完成段，
  但正式產品用 `.mp4` + 加 `+faststart` 並以「先寫 temp 再改名」保證完整性）。

**錄影格式選定（以「時間軸回放」為首要約束）— Fragmented MP4 (fMP4)：**

| 候選 | 時間軸 seek | 斷電/損壞容忍 | 播放相容性 | 音訊支援 | 結論 |
|---|---|---|---|---|---|
| **fMP4**（每格 moof 自含） | 精確、可即時定位 | ★ 高（fragment 獨立，殘檔仍可播） | ★ 佳（mpv/VLC/WMP/瀏覽器） | AAC 佳（G.711 不友善→轉 AAC） | **採用** |
| 一般 MP4 | 佳（需 moov 前置/faststart） | 差（缺 moov 全檔可讀性受影響） | 佳 | 同左 | 僅作「匯出」格式 |
| MKV | 佳（Cues） | 中 | 較窄（非網頁原生） | 任意（含 G.711） | 備用/錄原始碼流 |
| MPEG-TS | 差（需掃描） | 中 | 中（廣播場） | 佳 | 不採用 |

- **封裝參數**：`-f mp4 -movflags frag_keyframe+empty_moov+default_base_moof[+faststart]`
  使每一 GOP 自成 fragment，時間軸可直接跳到任一關鍵幀，無需整檔掃描。
- **GOP / 關鍵幀要求**：時間軸跳轉只落在 I-frame（解碼原點）。因此對攝影機側要求 GOP ≤ 2 秒
  （1080p 幀率下 I-frame 間距 ≤ 50–60 幀）；GOP 過大即自動打 `force_key_frames` 標記。
- **斷電容忍**：fragment 自含元資料，電源瞬斷時「最後數秒不完整 fragment」可被播放器略過，
  已寫入的完整 fragment 照常可播 — 即「最多丟失最後 1 GOP，其餘全部可回放」。
- **A/V 對齊**：音訊 AAC 128k mono（見音訊段落），兩軌同時間戳入 fragment。
- 每段寫完：`fsync` 後蛇形改名為正式檔 → 將 `(channelId, start, end, path, size, codec)` 寫入 SQLite。
- 崩潰復原：啟動時掃描 `*.tmp` 段，嘗試 `ffmpeg -c copy -f mp4` 修復或直接刪除並刷索引。

**音訊錄影（A/V 同步）：**
- RTSP 內含音訊軌（AAC / G.711 a-law / u-law / PCM）時預設同步錄音；每路可設 `audio_enabled=false` 節省存量。
- **格式相容決策（關鍵）**：G.711/PCM 封進 MP4 相容性差（多數播放器不穩）→
  音訊統一轉碼為 **AAC-LC 128kbps mono**（FFmpeg `-c:a aac`，軟體編碼消耗極低，32 路可負荷），影片維持 `-c copy`。
  若源本就是 AAC → `-c:a copy` 直接存，零消耗。
- A/V 同步：以共同時間戳對齊寫入；不依賴設備時鐘。
- 即時監聽與回放共用同一音訊軌，聲道切換無縫。

### 3.3 即時監看管道（D4・硬體解碼・核心功能）

**目標：32 路同時順暢、低延遲、不拖垮 UI。**

- 監看畫面渲染：WPF `WriteableBitmap` for 硬解 YUV→BGRA（參考 FFMediaToolkit 直接把解碼幀寫入 WriteableBitmap 的範例）。
- 每路監看幀上限（例如 12–15 fps，抽幀），避免 UI 被 32 路塞爆。
- 硬體解碼器選擇（依序嘗試）：`h264_cuvid`/`hevc_cuvid`（NVIDIA）→ `h264_qsv`/`hevc_qsv`（Intel）→ 軟解 fallback。
- 低延遲策略：
  - `rtsp_transport=tcp`、`buffer_size` 調小、`max_delay` 限制，不預載大緩衝（回放才需要快取）
  - 解碼→渲染的幀佇列固定深度（如 2–3 幀），滿則丟棄最舊，維持即時性
  - 渲染交由 UI 執行緒每幀更新 `WriteableBitmap`；解碼與網路在背景執行緒
- 分屏 1/4/9/16/25/32 動態切換；切分屏不重開流，僅改變渲染目標與抽幀數
- 單路放大：點擊網格 → 獨立浮窗（同一個解碼輸出直接放大，不二次解碼）
- 狀態角標：離線(灰)/連線(綠)/錄影中(紅點+時間)/警報(黃)，由 HealthTimer 驅動
- 畫質策略：監看做低延遲保證，可選次串流當監看源以省頻寬
- 即時語音監聽：點格面揚聲器圖示啟用**單路**即時聲音（一次只出聲一路，避免 32 路同時開聲）；32 路同時只解影像不解音訊
- 雙向對講（talk，後期擴充）：ONVIF Audio 上傳通道，模組化 `ITalkProvider` 介面

### 3.4 錄影回放管道（核心功能）

**目標：時間軸隨選即回、多段無縫、流暢拖拽。**

- 索引定位：UI 選通道+時間範圍 → SQLite 查詢該範圍的 `segments`（含時移前的上一段尾巴與時移後的下一段開頭，各多取 3 秒重疊）→ 組出播放檔序列。
- **關鍵幀精準 seek**：時間軸拖到 `T` 秒 → 由 `segment_keyframes` 定位 ≤T 的最近 I-frame
  → 以該關鍵幀為播放起點（解碼原點），誤差只在 GOP 內（≤2 秒，見 §3.2）。
  不仰賴 mpv 盲目掃描、不逐幀數位掃檔案，即時響應拖拽。
- 串接播放：以 libmpv 依序載入多檔（mpv 原生支援多檔序列、跨檔無頓挫 seek）。
  - `--demuxer-seekable-cache` 平滑跨段
  - `--force-seekable=yes` 保證 MP4/TS 都可拖動
- 播放控制：0.5×/1×/2×/4×/8×/16×（mpv `speed`）、逐幀、暫停/步進、畫面快照存檔。
- 時間軸 UI（自訂控制項）：
  - 橫軸 = 時間；區段色塊依 `segments` 繪製；事件標記（若開啟擴充）疊加
  - 拖拽/點選定義起止點；播放頭位置換算回真實時間戳
  - 時間刻度依縮放層級切換（1 分鐘/10 分鐘/1 小時），支援滑鼠滾輪縮放
- 回放不吃錄影管道：讀取端獨立，避免同步互鎖。
- 匯出（後期）：選定範圍 → `ffmpeg concat` 依索引無損拼接 → 單一 MP4。

---

## 4. 資料庫結構（SQLite: index.db）

```sql
-- 設備
CREATE TABLE devices (
  id INTEGER PRIMARY KEY,
  name TEXT NOT NULL,
  ip TEXT NOT NULL UNIQUE,
  port INTEGER DEFAULT 80,
  username TEXT,
  password_encrypted TEXT,        -- 以 DPAPI/使用者金鑰加密儲存
  vendor TEXT,                    -- hikvision / dahua / onvif / generic
  enabled INTEGER DEFAULT 1,
  created_at TEXT DEFAULT (datetime('now'))
);

-- 通道
CREATE TABLE channels (
  id INTEGER PRIMARY KEY,
  device_id INTEGER REFERENCES devices(id) ON DELETE CASCADE,
  name TEXT NOT NULL,
  main_rtsp TEXT NOT NULL,
  sub_rtsp TEXT,
  codec TEXT DEFAULT 'h264',     -- h264 / h265
  audio_enabled INTEGER DEFAULT 1,      -- 是否錄音
  audio_encoder TEXT DEFAULT 'aac',     -- aac(轉碼) / copy / none
  recording_mode TEXT DEFAULT 'always', -- always / schedule / motion / off
  motion_enabled INTEGER DEFAULT 0,
  motion_sensitivity REAL DEFAULT 0.5,
  created_at TEXT DEFAULT (datetime('now'))
);

-- 錄影區段索引
CREATE TABLE segments (
  id INTEGER PRIMARY KEY,
  channel_id INTEGER REFERENCES channels(id) ON DELETE CASCADE,
  stream TEXT DEFAULT 'main',    -- main / sub
  start_time TEXT NOT NULL,
  end_time TEXT NOT NULL,
  file_path TEXT NOT NULL,
  size_bytes INTEGER,
  duration_sec REAL,
  status TEXT DEFAULT 'final',   -- final / tmp / corrupt
  sha256 TEXT,                 -- 寫入時計算，防竄改驗證（§11.5）
  UNIQUE(channel_id, stream, start_time)
);
CREATE INDEX idx_seg_channel_time ON segments(channel_id, start_time);

-- 關鍵幀索引（fMP4 fragment 定位，供時間軸精準 seek）
CREATE TABLE segment_keyframes (
  id INTEGER PRIMARY KEY,
  segment_id INTEGER REFERENCES segments(id) ON DELETE CASCADE,
  time_sec REAL NOT NULL,          -- 相對區段起始的秒數（= PTS 對應）
  moof_offset INTEGER NOT NULL,    -- 該 GOP fragment 於檔內偏移（seek 依據）
  UNIQUE(segment_id, time_sec)
);
CREATE INDEX idx_kf_segment ON segment_keyframes(segment_id);

-- 事件（警報/運動/斷線）
CREATE TABLE alarm_events (
  id INTEGER PRIMARY KEY,
  channel_id INTEGER REFERENCES channels(id) ON DELETE CASCADE,
  event_type TEXT NOT NULL,      -- motion / offline / tamper / ai_person / ai_vehicle / schedule_start / manual / io_input
  start_time TEXT NOT NULL,
  end_time TEXT,
  snapshot_path TEXT,
  detail TEXT,
  acknowledged INTEGER DEFAULT 0
);
CREATE INDEX idx_event_time ON alarm_events(start_time);

-- 事件處置（M38 §14.4；一事件一列，狀態四態）
CREATE TABLE event_dispositions (
  event_id INTEGER PRIMARY KEY REFERENCES alarm_events(id) ON DELETE CASCADE,
  status TEXT NOT NULL DEFAULT 'pending',   -- pending / acknowledged / actioned / false_alarm
  assigned_to TEXT,
  note TEXT,
  updated_at TEXT NOT NULL
);

-- 事件處置軌跡（M38；append-only 稽核）
CREATE TABLE event_disposition_trail (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  event_id INTEGER NOT NULL REFERENCES alarm_events(id) ON DELETE CASCADE,
  status TEXT NOT NULL,
  assigned_to TEXT,
  note TEXT,
  changed_at TEXT NOT NULL
);
CREATE INDEX idx_disp_trail_event ON event_disposition_trail(event_id);

-- 排程規則（每通道每日 7 段窗口）
CREATE TABLE schedules (
  id INTEGER PRIMARY KEY,
  channel_id INTEGER REFERENCES channels(id) ON DELETE CASCADE,
  day_mask INTEGER NOT NULL,     -- bit0=週日 ... bit6=週六
  start_min INTEGER NOT NULL,    -- 0..1439
  end_min INTEGER NOT NULL,      -- 0..1439
  action TEXT DEFAULT 'record'
);
```

- 索引合併：回放/匯出時可用 `ffmpeg concat` 依索引依序拼接多段。
- 配額：磁碟可用空間 < 門檻時，依 `segments.start_time` 由舊到新刪檔並同步刪索引（錄影不中斷）。

---

## 5. AI 辨識與事件引擎

> 參考：Frigate 偵測管線（抽幀→縮放→推理→事件）、CodeProject.AI（Windows 外掛推理服務）、YOLOv8（ultralytics）。
> 設計原則：**AI 永不中斷錄影** —— 推理獨立於錄影管道，崩潰僅損失偵測（呼應 §11.1 進程分離）。

### 5.1 偵測能力分級（漸進導入）

| 級別 | 能力 | 演算法/推理 | 預設 |
|---|---|---|---|
| L0 | **運動偵測**（門檻觸發） | CPU 幀差/積分圖，零佈署 | 開啟 |
| L1 | **物件偵測**：人 / 車輛（種類） | YOLOv8s 小模型 + **ONNX Runtime** | 選配(可全時) |
| L2 | 內容辨識：車牌 OCR、人臉偵測 | 專用模型 | 選購、預設關閉 |

**法規（台灣個資法）**：人臉/車牌屬個資與生物特徵，需告知、同意與保存機制。
產品設計將「**偵測**（畫面中是否有）」與「**識別/比對**（辨識出是誰/車牌號）」**分離**，
比對功能預設關閉並需管理者明確開啟；此為功能面與法遵面的雙重要求。

### 5.2 推理引擎抽象層

```csharp
public interface IDetectionEngine {
    IReadOnlyList<Detection> Run(Frame frame);   // 輸入解碼幀 → 類別/置信度/bbox
}
```
| 引擎實作 | 硬體 | 適合 |
|---|---|---|
| `OnnxRuntimeCpuEngine` | 純 CPU | 免 GPU 通用，L1 主推 |
| `TensorRtEngine` | NVIDIA GPU | 多路或主流畫質（Frigate 同路徑）|
| `OpenVinoEngine` | Intel CPU/iGPU/NPU | Intel 伺服器 |
| `RemoteAiEngine` | 外掛 CodeProject.AI 服務 | 進程分離、模型管理、不拖累 NVR（主推產品化）|

**資源算帳（32 路目標）**：L1 全時 32 路不可行 → 採「**漏斗式管線**」：
L0 運動觸發才餵 L1 物件模型；並在**次串流 (e.g. 640×360) + 抽幀 1–3 fps + ROI 排除**上推理。
目標：32 路 L1 在 ONNX CPU 可負荷；GPU 方案則可主流畫質全時。

### 5.3 偵測管線

```
次流拉流(ffmpeg, 不錄不存) → 抽幀器(1–3fps) → 縮放器(<=416p) → IDetectionEngine
   → 結果 → 事件引擎(Session 化) → 寫 alarm_events + 快照 + 時間軸標記
```
- **事件前後預錄**：偵測訊號前 N 秒 / 後 M 秒（事件模式錄影起點），AI 在次流、保存用主錄影
- 連續偵測去重（同物件多幀合併為單一事件，時間區間跨多段）
- 停車場/門口類「長時間停留/徘徊」識別列為 L2 後期

### 5.4 與時間軸、回放整合

- 事件寫入 `alarm_events`（`event_type='ai_person'|'ai_vehicle'` + confidence + bbox + snapshot）
- 時間軸色帶疊加**物件標記**；回放工作台側欄「AI 事件」分類過濾（見 §8.5）
- 單擊事件 → 跳至該關鍵幀 + 顯示偵測框疊加（僅回放時疊框，不修改錄影內容）

### 5.5 資源下界與硬體建議

| 方案 | 32 路成本 | 說明 |
|---|---|---|
| 純 CPU ONNX + 漏斗 | 低（骨幹伺服器即可） | L1 人/車偵測，次流+抽幀 |
| NVIDIA + TensorRT | 中（1× RTX/T4 級） | 主流畫質全時或多類模型 |
| 外掛 AI 服務（CodeProject.AI） | 中 | 進程隔離，崩潰不影響錄影；模型管理 UI |

> 一版交付：**L0 預設 + L1 ONNX CPU（漏斗 32 路）**；L2 / GPU / 外掛服務列為產品化階段。

### 5.6 模組化分析情境（Analytics Modules，§14.7 #6 落地）

> 除「人/車偵測」外，現代 VMS 以面向場景的「情境模組」販售。全部以規則引擎組合，不寫死。

| 模組 | 輸入 | 輸出事件 | 前置條件 | 狀態 |
|---|---|---|---|---|
| **周界/跨線** | L1 偵測＋規則多邊形 | `ai_line_cross` | 需 L1 | P2 |
| **區域侵入**（進入/離開 ROI） | L1 ＋ polygon | `ai_intrusion` | 需 L1 | P2 |
| **靜止車/遺留物** | L1 ＋長駐區域計時 | `ai_stationary` | 需 L1＋追蹤(§5.7) | P3 |
| **尾隨/逆行** | 連續追蹤軌跡 | `ai_tailgating` | 需 L1＋追蹤 | P3 |
| **人群聚集** | L1 人數＋密度 | `ai_crowd` | 需 L1 | P2 |
| **車流統計/熱區圖** | 追蹤計數＋位置分佈 | `ai_traffic`、`heatmap` | 需追蹤 | P2 |
| **長時間徘徊** | 追蹤停留時間 | `ai_loitering` | 需追蹤（承 5.3 舊 L2 註） | P2 |

- 實作：`IAnalyticsModule` 介面掛在 §5.2 引擎抽象之後，共用事件引擎
- 每個模組單獨販售位元（對應 §19 功能矩陣），未授權模組不顯示

### 5.7 目標追蹤與跨鏡頭（追踪，v2 核心差異）

- **單鏡追蹤**：L1 偵測後以 IoU/運動模型串「物件 ID」（Hungarian 匹配），跨幀穩定輸出
  `Detection` 增加 `track_id`；事件以 track_id 去重（同人連續 30 幀只記一事件）
- **追蹤器**：輕量 `Tracker` 整合進 §5.3 管線（次流 1–3fps 已足）；`Kalman`＋`EMA bbox` 平滑
- **跨鏡頭（ReID，P3 延展）**：物件 embedding 向量 → 跨通道追蹤歸一 ID（同人出現在鏡頭 A→B）；寫 `alarm_events.embedding` 欄位
- **意義**：§5.6 的靜止/尾隨/徘徊都需要穩定追蹤；§5.9 以圖搜圖亦依賴 embedding

### 5.8 音訊事件偵測（Blue Iris audio sensing 對標）

- 目的：槍聲/尖叫/玻璃碎裂/引擎聲 → 觸發事件＋綁定錄影（與 IO 同事件引擎）
- 架構：**音訊軌抽樣（次流 AAC/G.711 解碼到 16k mono）** → 特徵（MFCC）→ 小型分類模型（ONNX）
- 資源：音訊極輕量，32 路同時亦可；僅「啟用音訊事件」通道才計算
- 非語音／語音辨識不做（隱私）；只做「聲響類型」判斷
- 苻合 §15.4（音訊軌已錄製 → 訊號來源為既有音訊軌，不另開流）

### 5.9 智慧搜尋與影片摘要（P2，市場差異化）

- **語意搜尋（CLIP 向量）**：事件縮圖→embedding→向量庫（`sqlite-vec` 或年度剪輯）→ 自然語言/樣本圖查詢「紅色貨車」「有人搬箱」
- **事件檢索**：維持 §5.4 之人/車/時間/ROI 篩選→縮圖牆
- **以圖搜圖（ReID）**：選單一人臉/人/車框→依 embedding 找出其他鏡頭同時段
- **影片摘要（Video Synopsis）**：將 1 小時內偵測物件之「動態幀」合成短片段快速預覽（Frigate/Genetec 同概念）；僅供檢索預覽，不取代原錄影

### 5.10 規則編輯器與複合事件

- **可視化規則**：地圖/畫面上畫 polygon（§16.1 同 UI 技術）、選模組、設時段、可多個「且/或」組合
- 例：`(侵入區域A 或 侵入區域B)` 且 `時段=營業後` 且 `非白名單[高權限=false]` → 觸發「緊急」警報＋Smart 錄影切主流
- 規則屬「設定層」：存 SQLite `ai_rules(id, name, expression_json, actions_json, enabled)`；執行於事件引擎（§5.4 擴充）
- 銷售位元：規則引擎為「進階版」以上功能（§19）

### 5.11 模型治理與信賴分數

- **模型註冊表**：`ai_models(id, name, version, format, hash_sha256, status)`；內建 YOLOv8s/C.LIP 等版本；`status=active`
- **更新與回退**：新模型入庫先 dry-run（於測試流跑 N 幀驗證指標）→ 一鍵切換、一鍵回退；更新不重啟錄影
- **信賴回饋**：使用者「標記誤報/正確」→ 統計每模型 FP/FN 趨勢 → 建議調感度（承 §14.7 #17 Auto-VMD 方向）
- **模型分離**：客戶可自行放入 ONNX 模型（需符合 Schema）；人臉/車牌模型需授權碼解鎖（§19.3）

### 5.12 AI 驗收標準

- [ ] 32 路次流＋漏斗：L1 人/車偵測 CPU 負荷達 §5.5 預算，無掉幀
- [ ] 同一物件連續多幀合併為單一事件（含 track_id 去重）
- [ ] 「週界」「區域侵入」規則於 2 畫面驗證觸發正確
- [ ] 音訊事件在測試樣本（槍聲）觸發，不影響錄影
- [ ] 模型一鍵更新與回退生效，失敗自動回復 active 版

---

## 6. 排程錄影

- `recordings_mode` = `schedule` 時，UI 每通道提供一週 7×24 矩陣編輯 -> 寫入 `schedules`。
- 背景 `SchedulerService` 每 30 秒讀取一次生效規則：
  - 通道生效規則且未在錄 → 啟動該路錄影
  - 通道不再生效 → 封閉當前段後停止
- `always` 模式：全時錄。`motion` 模式：僅警報觸發時錄（含前 10 秒預錄 buffered I 幀）。

---

## 7. 專業 IP 攝影機管理

> 目標：新設備從掃描到上線 < 1 分鐘；32+ 通道可批次管理；設備全生命週期可稽核。
> 基於 ONVIF 標準（WS-Discovery 探索、Profile S 採流、Profile T 回放、Imaging/PTZ/Events/FirmwareUpdate 管理），
> 品牌 SDK（海康/大華/雄邁）以適配層承接差異。

### 7.1 上線流程（Device Onboarding）

1. **探索**：ONVIF WS-Discovery 區網自動掃描 + 手動 IP 新增 + 批次網段掃描
2. **識別**：品牌/型號/韌體/能力集（`GetCapabilities`），對應 Vendor 適配層
3. **統一授權**：全域「設備帳密庫」（DPAPI 密文），避免逐台 Key-in
4. **自動建通道**：抓取各 Profile 媒體設定 → 生成主/次/第三流 RTSP 位址（依設備能力動態生成，不硬編碼）
5. **基底設定**：NTP 校時（§11.1）、錄影時間基準、編碼 GOP 建議值
6. 可存取設備 SD 卡檔案（Profile T）當備援回放源

### 7.2 通道與串流管理

- **三流用途模型**：主流→錄影；次流→監看+AI（§5）；第三流→行動端/備援
- 每通道可重設流用途；串流參數範本（解析度/幀率/碼率/編碼/GOP）建議值 + 頻寬上限控管
- **批次套版**：多通道選取 → 參數範本一鍵套用（32+ 通道手動設定是災難，必須批次）

### 7.3 影像與 PTZ 管理

- **影像調整**：ONVIF Imaging — 亮度/對比/銳利/白平衡/寬動態/IR/DSN（日夜切換），修改前後快照對比
- **PTZ**：絕對/相對移動、速度曲線、**預設點(Presets) 管理**、**巡視/巡航(Tour) 編輯**、限位設置、H.264 stream 內嵌雲台操作
- **隱私遮罩(Privacy Mask)**：物理遮罩設定，法律要求之隱私保護

### 7.4 生命周期與群組管理

- 設備樹三層結構：**群組/分區 → 設備 → 通道**；拖曳歸組、通道排序
- 啟用/停用/移除/批次移除；移除前確認錄影遷移或保留策略
- **韌體遠端升級**：批次檢查版本 → 推送映像(ONVIF FirmwareUpdate) → 升級/重啟驗證
- 設備工具面板：Reboot、音訊/影像測試、訊號診斷

### 7.5 健康與可用性

- 指標：在線狀態、封包遺失率、延遲、訊號強度、**NTP 偏差**、設備本機儲存(SD/HDD) 狀態
- 失敗處置：指數退避重連（§3.1）、離線事件 + 自動告警、主流出問題自動降級次流備援
- 健康儀表彙整（§11.7 管理面之設備面向）

### 7.6 安全管理

- 設備憑證於管理端 DPAPI 密文儲存；可選擇 RTSP-TLS / HTTPS 傳輸
- **稽核日誌 audit_log（M109，v40）已落地**：`AuditLogRepository` Record（actor/action/category 空白→ArgumentException、occurredAt 注入可測）／List（category/actor/action＋時間左閉右開、LIMIT/OFFSET、時序 DESC）／Count／PruneOlderThan（稽核保管期限）；類別常數 `AuditCategories`。設備管理操作掛載點待續（登入/參數變更/匯出/共享/法務保留已於 M110 串入 audit_log）
- 批次密碼輪換提醒（配合 §11.5 稽核）

### 7.7 資料模型（擴充 §4）

```sql
device_groups(id INTEGER PK, name TEXT, parent_id INTEGER);   -- 群組樹

-- devices 增欄
model TEXT, firmware TEXT, vendor_sdk TEXT,
ntp_offset REAL, capabilities TEXT,     -- JSON 能力集
stream_caps TEXT,                       -- 各流 profile 能力 JSON

-- channels 增欄
use_role TEXT DEFAULT 'main',           -- main / sub / third 用途
profile_token TEXT,                     -- ONVIF Profile token
imaging_profile TEXT;

-- 遠端操作紀錄（PTZ/升級/重啟/改參）
device_operations(id INTEGER PK, device_id INTEGER, op_type TEXT,
                  param TEXT, result TEXT, created_at TEXT);
```

### 7.8 驗收標準

- [ ] 新設備自動探索→建通道 < 1 分鐘
- [ ] 32 通道批次套版一鍵完成、可回滾
- [ ] PTZ 預設點/巡視與影像參數在單一管理介面完成
- [ ] 韌體批次升級流程安全可用
- [ ] 全部管理動作皆有稽核軌跡

---

### 7.9 多品牌多型號支援架構（核心競爭力）

> 設計目標：對使用者而言「所有品牌操作統一」，對開發者而言「品牌差異完全隔離」。

#### 7.9.1 三層通訊架構

```
┌────────────────────────────────────────────────────┐
│   統一管理介面（§9 Admin Console）                   │
├────────────────────────────────────────────────────┤
│   DeviceAdapter 抽象層  IDeviceAdapter               │
│   ┌────────────┬──────────────┬──────────────┐     │
│   │ ONVIF      │ BrandSDK     │ GenericRTSP  │     │
│   │ (標準協議) │ (品牌特定)   │ (兜底方案)   │     │
│   └────────────┴──────────────┴──────────────┘     │
├────────────────────────────────────────────────────┤
│   ONVIF WS-Discovery / SDK API / RTSP              │
└────────────────────────────────────────────────────┘
```

| 層級 | 通訊方式 | 適用情境 | 功能完整度 |
|---|---|---|---|
| **L1 通用 ONVIF** | WS-Discovery + Profile S/T + Imaging + PTZ | 已知 ONVIF 相容品牌（海康/大華/Axis/雄邁等） | ★★★★ 高 |
| **L2 品牌 SDK** | 各品牌專屬 SDK（DS-SDK/DMSS SDK/EasySDK 等） | 需要品牌高級功能（海康 Smart 事件、大華_heatmap、專有 PTZ 模式） | ★★★★★ 最高 |
| **L3 通用 RTSP** | 純 RTSP 拉流 + 手動 RTSP URL 輸入 | 未知品牌、雜牌、舊設備、第三方串流源 | ★★ 基本 |

#### 7.9.2 適配器模式（Adapter Pattern）

```csharp
/// <summary>
/// 所有品牌必須實作的核心介面（與 §7.1–7.5 管理需求對齊）
/// </summary>
public interface IDeviceAdapter
{
    // 探索與識別
    IReadOnlyList<DeviceProbeResult> DiscoverNetwork(string subnet);
    DeviceCapabilities GetCapabilities(string ip, int port, string user, string pass);

    // 串流管理
    IReadOnlyList<StreamProfile> GetStreamProfiles(string deviceId);
    string BuildRtspUrl(string deviceId, StreamRole role);

    // 影像參數
    ImageSettings GetImageSettings(string deviceId, int profileId);
    void SetImageSettings(string deviceId, int profileId, ImageSettings settings);

    // PTZ
    void PtzMove(string deviceId, int profileId, PtzDirection dir, int speed);
    void PtzGoToPreset(string deviceId, int profileId, int presetId);
    void PtzSetPreset(string deviceId, int profileId, string name);
    void PtzStartTour(string deviceId, int profileId, TourDefinition tour);

    // 健康
    DeviceHealthSnapshot GetHealth(string deviceId);

    // 韌體
    void Reboot(string deviceId);
    Task FirmwareUpgrade(string deviceId, Stream upgradeImage);

    // 適配器屬性
    string VendorName { get; }        // "hikvision" / "dahua" / "onvif" / "generic"
    int Priority { get; }             // 品牌 SDK > ONVIF > RTSP fallback
    bool IsNative(string model);      // 判斷此型號是否為「原生支援」（最佳品質）
}
```

**策略選擇邏輯**（自動，無需使用者介入）：
```
1. 探索回傳 vendor + model → 查 vendor_capabilities 表
2. 若有品牌 SDK 安裝 → 使用 L2（最高優先）
3. 若 ONVIF 相容且 L2 不可用 → 使用 L1
4. 以上皆無 → 使用 L3（通用 RTSP，使用者手動輸入 URL）
```

#### 7.9.3 支援品牌/型號與能力矩陣

| 品牌 | SDK 狀態 | ONVIF 支援 | 優先適配策略 | 特殊能力 |
|---|---|---|---|---|
| **海康威視 (Hikvision)** | ✅ DS-SDK / ISAPI / EasySDK | ✅ Profile S/T | L2 為主、L1 為輔 | 智慧事件(人車分離)、行為分析、ANPR |
| **大華 (Dahua)** | ✅ DMSS SDK / DSS SDK | ✅ Profile S/T | L2 為主、L1 為輔 | 熱力圖、SMD Plus、PTZ 智慧追蹤 |
| **雄邁 (Uniview/Unv)** | ✅ EasySDK | ✅ Profile S/T | L1 為主、L2 補充 | Smart 行為偵測 |
| **Axis** | ❌ 無公開 SDK | ✅ Profile S/T | L1 | VMD4 事件、ACAP 計算平台 |
| **Arlo / Ring / Nest** | ❌ 雲端封閉 | ❌ 不相容 | 不支援（需雲端存取） | - |
| **雜牌/通用** | ❌ | ⚠️ 部分 | L3 | 手動 RTSP URL |
| **CVI/TVI/AHD（類比+網路）** | 各廠私有 | ❌ | 不支援（需硬體擷取器） | - |

> 產品初期支援：**海康 / 大華 / Axis / ONVIF 通用**（覆蓋 90%+ 市場）。
> 雄邁/其他品牌以 L1 ONVIF 為主。

#### 7.9.4 品牌能力 Profile 表（資料庫）

```sql
-- 品牌/型號能力註記表（離線開發 + 設備探索時動態合併）
CREATE TABLE vendor_capabilities (
  id INTEGER PRIMARY KEY,
  vendor TEXT NOT NULL,          -- "hikvision" / "dahua" / "uniview" / "axis" / "generic_onvif" / "generic_rtsp"
  model_pattern TEXT NOT NULL,   -- "DS-2CD2xxx" / "*" (萬用) / "NVR-xxx"
  adapter_layer TEXT NOT NULL,   -- "sdk" / "onvif" / "rtsp"
  sdk_version TEXT,              -- 支援的 SDK 最低版本
  supported_codecs TEXT,         -- JSON: ["h264","h265"] 
  max_streams INTEGER DEFAULT 3,
  max_resolutions TEXT,          -- JSON: ["1920x1080","3840x2160"]
  max_fps INTEGER DEFAULT 30,
  ptz_support INTEGER DEFAULT 0,
  audio_support INTEGER DEFAULT 0,
  audio_codecs TEXT,             -- JSON: ["aac","g711a","g711u","pcm"]
  smart_events TEXT,             -- JSON: ["motion","line_cross","intrusion","face_detect"]（品牌特有）
  firmware_upgrade_method TEXT,  -- "onvif" / "sdk" / "http" / null
  notes TEXT,                    -- 備註（已知相容性問題等）
  UNIQUE(vendor, model_pattern)
);

-- 設備探索時動態寫入（未知型號 fallback 到 generic_onvif 或 generic_rtsp）
```

- **離線型號資料庫**：內建前 50 大型號（海康 20+ 大華 20+ 其他 10+）的能力資料
- **動態探索補充**：探索到新型號 → 試探性呼叫 → 寫入 `vendor_capabilities`（自動學習）

#### 7.9.5 串流參數差異與統一管理

- 不同品牌的 RTSP URL 格式、Profile 名稱差異 → 統一由 `IDeviceAdapter.BuildRtspUrl()` 產生
- 串流參數（編碼/解析度/碼率/GOP）差異 → 品牌專屬值存 `stream_caps` JSON；設定 UI 顯示此型號「可用範圍」
- GOP 差異：部分品牌預設 GOP 過大（4-5s）→ 自動打 `force_key_frames`（§3.2）或提示使用者

#### 7.9.6 PTZ 指令差異轉譯

不同品牌的 PTZ 協議差異極大（ONVIF PTZ vs 私有協議）：

| 指令 | ONVIF 標準 | 海康私有 | 大華私有 |
|---|---|---|---|
| 絕對移動 | ContinuousMove + Pan/Tilt | ISAPI PTZ cmd | DMSS API |
| 巡視(Tour) | ONVIF Tour | 海康巡航 | 大華巡航 |
| 點擊跟蹤 | 有限 | ✅ 智慧追蹤（需 SDK）| ✅ SMD 追蹤（需 SDK）|

- ONVIF 標準 PTZ 在 L1 完成大部分操作
- L2 SDK 擴充點擊跟蹤、智慧追蹤等高級 PTZ
- UI 端隱藏差異：使用者操作統一，Adapter 內部轉譯

#### 7.9.7 健康監控差異與統一匯報

不同品牌回報健康的方式不同：
- ONVIF：`GetDeviceStatus` / `GetSystemDateAndTime` / Events（硬碟狀態）
- 海康 SDK：`NET_DVR_GetDeviceConfig`（CPU/溫度/風扇/UPS）
- 大華 SDK：`dhlmaster_hdd_info`、CPU 溫度
- 統一匯報格式：`DeviceHealthSnapshot`（溫度/硬碟/網路/功耗/運作時間）→ UI 一致呈現

#### 7.9.8 品牌 SDK 管理（部署與生命週期）

- 各品牌 SDK DLL 放置於 `Sdks/<vendor>/` 目錄
- 適配器以 MEF（Managed Extensibility Framework）或策略模式**動態載入**
- SDK 載入失敗 → 自動降級 L1（ONVIF）→ 降級 L3（RTSP），永不導致系統崩潰
- SDK 版本管理：`vendor_capabilities.sdk_version` 記錄最低需求版本

#### 7.9.9 未來擴充：自訂適配器 API

提供 **REST/WebSocket 介面**或 **C# SDK**，讓客戶自行開發私有品牌適配器：
```csharp
// 客戶自訂適配器（后期擴充）
[Export(typeof(IDeviceAdapter))]
public class MyCustomAdapter : IDeviceAdapter { ... }
```
此為產品化階段的長期策略，不影響核心交付。

---

## 8. UI / UX 設計（專業美觀）

### 8.1 設計語言與方案

- 基底：**Fluent Design 風格深色主題**，搭配 **WPF-UI**（github.com/lepoco/wpfui，MIT）作為現代控件基底，自訂監控領域專屬樣式，避免「工程樣板」感。
- 備選控件庫：HandyControl（rich）、ModernWpf（Fluent）、MaterialDesignInXaml（若走淺色 Material 路線）。
- 字型階層：`Segoe UI Variable` + 中文 `微軟正黑體`；標題/副標/正文/指標 4 級字階。
- 核心理念：**資訊密度高但呼吸感清楚**（8pt 間距網格）、**狀態一眼可讀**、**操作三秒內到達**。

### 8.2 主題令牌（Design Tokens，M1 即建立）

統一集中於 `Themes/Tokens.xaml`，杜絕散落硬編碼：

| 令牌 | 色彩 | 用途 |
|---|---|---|
| `BgBase` | #0F1115 | 主背景 |
| `BgPanel` | #171A21 | 側欄/工具列 |
| `BgTile` | #1E232C | 監看格 |
| `Accent` | #3B82F6 | 選中/焦點/錄影鈕 |
| `StatusOk` / `Warn` / `Alarm` / `SignalOff` | 綠/黃/紅/灰 | 統一語義狀態（不得混用他色）|
| `TextPrimary` / `TextDim` | 近白 / 60%白 | 層級對比 |

### 8.3 主視窗布局（可演化）

```
┌────────────────────────────────────────────────┐
│ 頂欄  品牌標誌  狀態指示(錄影/時間同步/磁碟)  設定 │
├────────────┬───────────────────────────────────┤
│ 左側       │  監看區：1/4/9/16/25/32 分屏       │
│ 設備樹     │  ├ 單路放大浮窗(可拖至副螢幕)      │
│ (群組/通道/ │  └ 拖放通道 ≡ 分屏格調整          │
│  即時狀態) │                                   │
├────────────┴───────────────────────────────────┤
│ 底部時間軸列：監看/回放 Tab、播放控制、縮放滑桿   │
└────────────────────────────────────────────────┘
```

- 全視窗 DPI 感知（Per-Monitor V2），多螢幕正確縮放。
- 側欄可收合；監看區採「響應式格線」自適應留白。

### 8.4 即時監看互動（UX 重點）

- **拖放分屏**：左側通道直接拖入分屏格；分屏格間拖移交換位置；右鍵格面可置換/移除。
- 雙擊即單路放大；再次雙擊還原；放大浮窗可 `Alt+拖` 拖到副螢幕（無縫續看，不重開流）。
- 格面 hover 顯示迷你工具列：錄影開關、快照、**揚聲器(即時語音)**、立即回放、電動變焦畫中畫。
- OSD 浮水印：通道名 + 牆鐘時間 + 主/次流標示 + 錄影紅點計時（專業感與證據力來源）。
- 切分屏與重排全部**無閃爍、零重建**（同一通道 Session，僅更換渲染目標—見 §3.3）。

### 8.5 回放工作台（Playback Workspace，核心介面）

回放不是「看影片的副視窗」，而是獨立工作台，採用商用 VMS 佈局。

```
┌───────────────────────────────────────────────────────┐
│ 上區：回放牆（1/2/4 路同步）                             │
│   主畫面(大) + 次畫面縮放可拖；每路 OSD：通道名/真實時間 │
├───────────────────────────────────────────────────────┤
│ 中區：時間軸導航                                        │
│   ┌ 主時間列：刻度 / 縮放滑桿 / 滾輪無級縮放(日→秒)      │
│   ├ 通道色帶列：每通道一橫排，有錄影/無錄影/事件高亮      │
│   └ 選擇區間窗：拖拽起止手柄 + 播放頭(菱形)              │
├───────────────────────────────────────────────────────┤
│ 底區：控制列 + 側欄 Tab                                 │
│   ⏮◀⏸▶▶⏭  速度(0.5×–16×)  逐幀  書籤  搜尋  匯出       │
│   [事件列表] [書籤列表] [時間跳轉]（可收合側欄）          │
└───────────────────────────────────────────────────────┘
```

**時間軸導航（專業 VMS 標準）**
- 主時間列 + 通道色帶列分離：主刻度共享，色帶按通道橫向排列，多路同時比對一目了然
- 無級縮放：滾輪 1 天→10 分鐘→1 分鐘→10 秒，縮放中心跟隨滑鼠游標
- 播放頭為**關鍵幀吸附**（吸附 I-frame，見 §3.4），使跳轉瞬間出畫
- 拖拽選取範圍 → 立即鍵可「重播此段 / 匯出此段」

**播放控制**
- 速度 0.5×–16×（mpv `speed`，硬解不卡）；逐幀 `←`/`→`
- 前後跳 10s / 1min / 10min 快捷鍵；「跳至事件」從側欄單擊直達
- 暫停時畫面保留，可直接右鍵做**該幀快照**

**多路同步回放**
- 1/2/4 路並排，全部鎖定**同一播放頭**（同軸時間比對：進出貨、多鏡頭交叉）
- 任一畫面點選即為主焦點（時間戳/音量跟隨）

**搜尋與側欄**
- 事件列表：時間線反白的警報/斷線/書籤，點擊跳轉
- 書籤：右鍵「加入書籤」+ 筆記，側欄管理（重命名/刪除/跳轉）
- 時間跳轉：直接輸入 `2026-09-12 14:30:00` 精確定位（含關鍵幀修正）

**匯出與證據**
- 匯出精靈：範圍(拖拽/手輸)、格式(MP4/AVI)、浮水印(通道/時間/操作者)、SHA-256 hash 附檔
- 快照存檔即完成；全程保持 A/V 同步（音訊選軌 AAC）

**效率設計**
- 全鍵盤可操作：格面 `1–4` 選路、`Space` 播放/暫停、`F` 逐幀、`B` 書籤、`E` 匯出
- 拖入分屏「立即回放」直接進入此工作台並定位至該時刻
- 回放時錄影不間斷（獨立 Session，見 §3）

**驗收清單**
- [ ] 時間軸拖拽跳轉 < 300ms 出畫（含跨段）
- [ ] 4 路同步 + 16× 播放不掉幀
- [ ] 任意「無錄影」區間色帶灰顯示，點擊有明確反饋

### 8.6 動效與回饋

- 統一 `Duration=0.15–0.2s` 緩動：視窗開啟淡入、分屏格過場、狀態燈變化。
- 所有可點擊元素 hover/按下/pressed 三態；狀態燈用脈動（錄影中）但節制，避免干擾。

### 8.7 美觀驗收（做對的檢查表）

- [ ] 32 路滿屏下視覺不吵雜、狀態仍可辨識
- [ ] 螢幕錄播示範看不出「工程 UI」感（無默認 WPF 風格殘留）
- [ ] 深色主題無死黑大塊、對比度 WCAG AA
- [ ] 新用戶 30 秒內能完成：加一通道→分屏→開始回放

> 主題系統在 M1 建立，UI 元件隨里程碑漸進美化，不後補。

---

## 9. 設定與管理介面（Admin Console）

管理介面的專業在「組織與可操作性」——所有功能可視化收斂，避免設定深水區。

### 9.1 設定控制台分類導航（左側）

```
設定控制台
├─ 總覽        系統狀態、錄影概況、容量趨勢、健康告警
├─ 設備        設備列表 / 探索嚮導 / 批次套版 / 韌體
├─ 通道        串流參數、用途(§7.2)、錄影策略、AI 開關
├─ 錄影與儲存  錄影目錄、區段長度、保留策略、容量估算器(§11.2)、配額
├─ 事件與 AI   AI 引擎、偵測類別、ROI、通知
├─ 帳戶與安全  角色/權限、密碼政策、稽核檢視器(§11.5)
├─ 系統        NTP/時區、開機自啟動、服務狀態、升級、備份還原
└─ 維護        磁碟健康(SMART)、日誌、診斷包匯出
```

### 9.2 設計原則

- **常用 vs 進階分離**：一級頁只保留高頻操作；低頻（韌體、RAID、升級來源）折疊於「進階」
- **生效語意分明**：串流/錄影參數＝「套用後生效」；影像/PTZ＝「即時調試」；中斷性變更需二次確認
- **每個設定項標準化**：微文案 + 合理預設值 + 內建驗證（非法值即時標紅阻擋）+ 友善錯誤訊息
- **批次第一**：所有「設備/通道級」設定預設提供「套用到所選/全部」，與 §7.2 批次套版一脈相承
- **權限門**：安全/稽核/韌體/刪除類只對管理者可見可改（配合 §11.4 角色）；變更寫 audit_log
- 設定表單風格：欄位→ 說明 → 預設值三欄，深色一致於 §8 主題

### 9.3 重點畫面設計

**(a) 設備探索嚮導（摺疊式 Wizard）**
掃描結果表（IP/品牌/型號/可接入）可多選 → 一組帳密 → 自動建通道；
失敗列就地顯示原因與單列重試，不全流程重來。

**(b) 影像調試頁**
「調整前 / 調整後」即時分割對比（承 §7.3 快照對比）；滑桿即時回饋、一鍵還原出廠。

**(c) 錄影策略編輯器**
每通道：錄影模式(全時/排程/事件)、保留天數、質量；
週排程用 7×24 矩陣滑鼠拖曳填色；底部收縮顯示**容量估算器**結果（此配置約可存 X 天，§11.2）。

**(d) AI 設定**
引擎選擇（ONNX CPU / GPU / 外掛）→ 偵測類別勾選 → 每通道開關 → ROI 區域繪製 → 靈敏度滑桿；
L2 比對（人臉/車牌）置「進階·需權限」區，預設關閉（§5.1 法遵）。

**(e) 帳戶與稽核**
角色權限樹（管理者/操作員/檢閱者）、密碼政策（強度/期限/鎖定）；
稽核檢視器：時間/操作者/類別過濾 + 匯出。

**(f) 系統**
時區與 NTP、開機自啟動、服務健康、升級來源與版本檢查、設定+索引備份與還原、診斷包下載。

### 9.4 設定可搜索

- 全控制台 `Ctrl+K` 設定速查（輸入「保留天數」「NTP」即直達）
- 分類可摺疊、內文錨點定位

### 9.5 設定儲存與可回滾

- 設定入 SQLite `settings` 表（key-value、分組、版本號）；每次變更寫 `audit_log`
- 重大變更前自動快照，可一鍵回滾至先前版本（配合 §11.5）
- 設定檔匯出/匯入（JSON）— 複製部署多台 NVR 用

### 9.6 驗收標準

- [ ] 新管理員 5 分鐘內完成：加設備→設錄影策略→開 AI→設通知
- [ ] 批次操作可一鍵套用 32 通道且可回滾
- [ ] 非法輸入全阻擋、錯誤訊息友善不崩潰
- [ ] 每個設定變更有稽核軌跡，可回滾驗證

---

## 10. 開發藍圖（調度預計週數）

| 里程碑 | 內容 | 驗收標準 |
|---|---|---|
| **M1 骨架** | 方案結構、WPF 殼與主題、設定檔、Serilog、SQLite 初始化、**授權驗證骨架(§19)**；1 路 RTSP 拉流 → 監看 + `-c copy` 區段錄影 | 單路可看、可錄、斷線可重連；授權導入/驗證運作 |
| **M2 即時監看（核心）** | ChannelSession 池化、32 路並行、硬體解碼、抽幀、1/4/9/16/25/32 分屏、單路放大、數位放大 ePTZ(§17.3)、PTZ 控制台(§17.4)、拍照(§17.5)、狀態角標 | 32 路同時監看 CPU/延遲受控、切分屏順滑 |
| **M3 錄影與回放（核心）** | segment 索引寫入、自訂時間軸控制項、mpv 串接回放、倍速/逐幀、單路回放 | 選任意時間可回放、拖拽順滑、跨段無縫 |
| **M4 穩健性** | 配額清理、崩潰復原(.tmp 修復)、長時間穩定性測試 | 72h 連續錄影+回放穩定 |
| **M5 擴充-事件與AI** | L0 運動偵測、L1 AI 物件偵測(ONNX+漏斗)、事件中心、通知、AI 時間軸標記 | 32 路次流可同時觸發 AI 警報與預錄 |
| **M6 擴充-設備與完善** | ONVIF 探索/PTZ、設備精靈、匯出精靈、L2 模組(選購)、安裝包 | 新設備一分鐘內上線 |
| **M7 遠程與地圖** | WebApi + WebRTC 監看 + HLS 回放、Map View 平面圖、多語言 | 手機/瀏覽器可遠程監看與回放 |
| **M8 地圖、IO 與營運** | 電子地圖(§16.1)、警報 IO(§16.2)、證據包+數位簽章、備份/異地備援、事件工作流 | 空間視覺化+外部感測整合；證據具司法敘用完整性 |

---

## 11. 專業等級需求（產品化，7×24 營運）

核心功能做得再好，不足以成為「專業」產品；以下為營運等級的設計要求。

### 11.1 可靠性工程框架

| 面向 | 要求 |
|---|---|
| 進程分離 | **錄影核心 = 背景服務（Windows Service）**；WPF UI 為客戶端。UI 當掉不影響錄影 |
| 看門狗 | 服務與 UI 各自 watchdog；崩潰自動重啟，重啟後自動恢復全部通道錄影 |
| 寫盤保護 | 分段邊寫邊 `fsync`；先寫 `.tmp` 完成後改名；定期 `ffprobe` 抽查已封檔完整性 |
| 磁碟滿策略 | 三階段：預警(>85%)→ 優先刪最舊 `motion` 段再刪全時段 → 最後「停新錄、保全證物」並告警；**不可盲目覆寫最舊** |
| 斷電復原 | 開機自啟動、缺損 `.tmp` 自簽修復或隔離、索引重建掃描 |
| 時間可信 | 設備/伺服器 NTP 校時；錄影時間以伺服器牆鐘為準並記錄偏移 |
| 連續運行 | 目標 99.9% 可用；零「單路故障拖垮全系統」 |

### 11.2 性能與容量規劃（先算再寫，落地依據）

```
所需容量(TB) ≈ 單路平均碼率(Mbps) / 8 × 秒/天 × 路數 × 保留天數 / 1000000
```
- 例：主流 4M + 次流 1M = 5Mbps → 32 路 × 30 天 = 5/8×86400×32×30 ≈ **518TB 不可能**
  → 實務以**次流(1M)做主錄影 + 事件/時段切主流**，或降低主流碼率/保留天數再取 RAID 實體容量。
- 階段：先內建 **「容量估算器」** 於設定頁，輸入碼率/路數/天數即輸出需求與 RAID 建議。
- 網路：32×4Mbps ≈ 128Mbps 吞吐，建議雙網卡（隊列綁定）。
- 儲存：OS+索引放 SSD；錄影區塊放 HDD（WD Purple 等級、SMART 監控）。

### 11.3 監看體驗（專業級）

- 多分屏與**巡視(Tour)**（可編排通道順序與停留秒數）
- **雙螢幕支援**：主屏分屏、附屏可釘選重點通道牆
- 電子放大 ePTZ（在解碼幀上裁切縮放，不重開流）
- 名稱/時間浮水印、訊號源(主/次流)標示

### 11.4 回放與證據能力

- **多通道同步回放**（最多 4 路同軸時間比對）
- 時間軸縮放（滾輪）、**書籤**、事件跳轉
- 匯出功能：範圍選取、**浮水印（時間戳+通道名+操作者）**、完整性 hash 附檔

### 11.5 安全與稽核（台灣個資法/資安要求）

- 角色權限：管理者(全部) / 操作員(監看回放) / 檢閱者(唯讀回放)
- **稽核日誌**：記錄「誰、何時、查閱哪段影像、變更哪項設定」（appends-only）
- 傳輸加密：RTSP 可走 TLS、內部 API 走 HTTPS
- 錄影檔防竄改：寫入時記錄 SHA-256，系統驗證一致性

### 11.6 設備生態與多品牌支援

> 詳見 §7.9 品牌適配架構；本節僅列專業營運需求。

- **覆蓋目標**：海康 + 大華 + Axis 覆蓋 90%+ 市場；雄邁/其他以 ONVIF 標準為主；不明設備 fallback 通用 RTSP
- **三層通訊架構**：L2 品牌 SDK（完整功能）→ L1 ONVIF（標準相容）→ L3 通用 RTSP（兜底），自動選擇無需使用者介入
- **能力型號資料庫**：離線內建 50+ 型號能力表 + 探索時動態學習未知型號
- **品牌差異隔離**：串流 URL / PTZ 指令 / 健康監控等差異全部由 `IDeviceAdapter` 轉譯，UI 呈現統一
- **SDK 降級保障**：SDK 載入失敗自動降級至 ONVIF/RTSP，永不導致系統崩潰
- **法規相容**：設備探索與 SDK 回報的鏡頭角度/視角等資訊納入 GDPR 隱私評估輸入

### 11.7 管理性

- 集中設定（通道批次套用、群組）、設定檔版本化可回滾
- 系統自健診：CPU/RAM/磁碟健康(含 §11.1 寫盤保護)/網路吞吐儀表與閾值告警
- 日誌中心 + 報表（錄影時數、斷線次數、容量趨勢）

### 11.8 架構對應（本文件與以上需求之結點）

| 專業需求 | 對應章節/設計 |
|---|---|
| 進程分離/看門狗 | §3 管線 + 新增 `HeliVms.Service` 專案 |
| 容量與頻寬 | 本節 §11.2（新增容量估算器) |
| 稽核與權限 | §4 資料庫新增 `users`/`audit_log`/`settings` 表 |
| 防竄改 | §4 `segments` 增 `sha256` 欄位；另見 §11.5 |
| NTP/時間 | §3.1 牆鐘校準 + §11.1 時間可信 |
| 證據匯出 | §3.4 匯出 + §11.4 |

> 第一版核心範圍不變（§0）；本節標準在 M4 後逐步納入，避免先行膨脹影響核心閉環。

---

## 12. 引用之 GitHub 開源技術（實作對照）

| 專案 | 出處 | 參考點 |
|---|---|---|
| Moonfire NVR | github.com/scottlamb/moonfire-nvr | 不重新編碼錄影 + SQLite 索引、低 CPU 架構 |
| Frigate | github.com/blakeblackshear/frigate | record/detect 分離、硬體加速、AI 偵測管線（抽幀→縮放→推理→事件） |
| CodeProject.AI Server | github.com/codeproject/CodeProject.AI-Server | 外掛推理服務、模型管理、Windows 部署 |
| YOLOv8 (ultralytics) | github.com/ultralytics/ultralytics | 物件偵測模型與 ONNX 匯出 |
| rtsp-record | github.com/SweatierKey/rtsp-record | `segment` muxer 區段錄影、`-c copy`、崩潰善後 |
| Sdcb.FFmpeg | github.com/sdcb/Sdcb.FFmpeg | C# FFmpeg API 綁定、QSV/CUDA 硬解範例 |
| FFMediaToolkit | github.com/radek-k/FFMediaToolkit | WPF WriteableBitmap 直接渲染解碼幀 |
| Mpv.NET-lib- | github.com/hudec117/Mpv.NET-lib- | WPF 內嵌 mpv 播放（低延遲監看/回放）|
| FFmpegView.Wpf | NuGet: FFmpegView.Wpf | FFmpeg→WPF 顯示控制項參考 |
| GoWVP (owl) | github.com/gowvp/owl | ONVIF 設備接入、多協定、雲端播放模型 |

**授權提醒**：FFmpeg 為 LGPL/GPL（依編譯組態），自有代碼採 LGPL 相容授權（或使用 LGPL build）
並將 UI/Core 與 FFmpeg 介面分目錄拆分，確保授權乾淨；libmpv/Mpv.NET 為 MIT。

---

## 13. 立即行動（建議下一步）

1. `dotnet new` 建立解決方案與三個核心專案（App/Core/Media）。
2. 安裝 Sdcb.FFmpeg 並驗證本機 FFmpeg 二進位載入（含 NVDEC/QSV 支援）。
3. 完成 M1：單路 RTSP「監看 + 錄影」閉環並跑通。
4. 依里程碑逐步推進，每步有可運行版本。

## 14. 功能缺口分析與後續路線圖

> 審視前文 13 章藍圖後的完整性對照：核心（監看/錄影/回放）與專業化（設備/UI/AI/法遵）已覆蓋，
> 下方列出**遺漏或僅觸及未深化的部分**，並依策略給優先級。

### 14.1 缺口總表

| # | 功能缺口 | 現況 | 影響 | 建議 |
|---|---|---|---|---|
| 1 | **遠程存取（Web / 行動端）** | 完全沒有（僅本機桌面） | NVR 基本構成，缺=半殘 | **P0** |
| 2 | **匯出證據工作流** | 有匯出精靈，缺批量/驗證/證據包 | 司法效力與營運效率 | **P0** |
| 3 | **地圖/平面圖檢視（Map View）** | 只有基礎概念 | 專業賣點與空間直覺 | P0′（§16.1 完整設計） |
| 4 | 備份與異地備援 | 沒有（僅本地錄影 + ROI） | 資料安全（災後） | P1 |
| 5 | 數位簽章與證據包 | 僅 SHA-256 | 證據鏈完整性 | P1 |
| 6 | 事件回應工作流 | 僅 `acknowledged` 位元 | 營運（確認/指派/留言） | P1（**已實作 M38**） |
| 7 | 多語言 i18n | 僅繁體中文 | 市場（出口與外文通路） | P1 |
| 8 | 智慧搜尋（物體/區域/色） | 僅時間與事件篩選 | 現代 VMS 賣點 | P2 |
| 9 | 統計報表 | 僅提及（§11.7）未深化 | 管理與驗收 | P2 |
| 10 | 中央/多機集群管理 | 沒有（單機 NVR） | 大型案/連鎖場域 | P2 |
| 11 | 雙向對講 | 僅預留 `ITalkProvider` | 門禁/收費場景 | P2 |
| 12 | 雲整合 / 訂閱 | 沒有 | 商業模式（訂閱營收） | P3 |
| 13 | Legal Hold（保存鎖定/沖銷） | 沒有 | 個資法合規用的「暫停汰除」 | P3 |
| 14 | GPS 時鐘/高精度時間源 | 僅 NTP | 證據時間可信度（法庭） | P3 |
| 15 | **遮蔽偵測（Tamper）** | L0 已實作（M39） | 專業 NVR 基本警報（鏡頭被遮/被移/被噴漆） | P1（**已實作 M39**） |
| 16 | **感測器/乾接點 IO（DI/DO）** | 沒有 | 門磁/煙霧/紅外警報主機整合 | P1（§16.2 完整設計） |
| 17 | **斷線補錄（Gap 補抓）** | 沒有 | 斷網期間設備 SD 自錄，重連後補抓 | P3 |

### 14.2 優先級定義

- **P0**：第一版核心閉環應包含（不做會明顯輸給競品）
- **P1**：第二版（M7–M8）
- **P2**：差異化 / 擴展市場
- **P3**：法遵強化 / 商業模式，視市場回饋啟動

### 14.3 P0 建議（併入核心藍圖）

**(1) 遠程存取 Web + 行動端**
- 架構：`HeliVms.WebApi`（ASP.NET Core）暴露 REST + WebSocket；`HeliVms.Web`（SPA）與行動 App 消費
- 串流：實時監看 = **WebRTC**（低延遲，瀏覽器免外掛）；回放 = HLS/MPEG-DASH（依索引即時生成片段）——借鏡 GoWVP（§12）與 WHEP/WARP 模式
- 安全：TLS + JWT；與桌面端共用資料庫與稽核（誰遠端看了什麼）——呼應 §11.5
- 權限：遠程預設更嚴（檢閱者級別起跳）
- 影響：新增 2 專案入目錄；里程碑 M4 之後並行

**(2) 匯出證據工作流（強化現有匯出精靈）**
- 批量匯出（多通道/多時段一次）、進度與續傳、匯出中心（歷史清單與狀態）
- **證據包**：影片 + `manifest.json`（通道/時間/AI 結果/操作者）+ SHA-256 清單 + 數位簽章（P1）
- 匯出即驗證：`ffprobe` 完整性檢查，產出《完整性報告》（可供司法敘用）

**(3) 地圖/平面圖檢視（Map View）**
- 上傳場域平面圖（DXF/PNG/背景圖）→ 拖放攝影機圖釘（含視角扇形）
- 點圖釘 → 即時小窗 / 回放；離線紅色警報閃動
- 廠房/店鋪/校園場景是標案常用亮點

### 14.4 P1 建議（第二版）

| 功能 | 設計要點 |
|---|---|
| 備份與異地備援 | 可排程批次複製錄影區段至第二磁碟/NAS/雲端；事件級錄影可雙寫 |
| 數位簽章 | 匯出影片以私鑰簽署，`HeliVmsVerify` 工具可驗證（司法效力） |
| 事件回應工作流 | 確認/未決/誤報/已處理四態 + 指派 + 附註 + 時間戳軌跡（**已實作 M38**：`event_dispositions`/`event_disposition_trail`、`AlarmEventRepository.SetDisposition`/`ListDispositionTrail`、事件中心處置列） |
| 遮蔽偵測（Tamper） | L0 即可實作：幀亮度突變 / 邊緣能量急降 / 全黑全白偵測 → 「鏡頭被遮、被移、被噴漆」警報（附快照）（**已實作 M39**：`TamperDetector`（16×16 灰階網格／亮暗閾值／邊緣能量基準 EMA）＋`TamperEventEngine`（連續 N 幀開窗、冷卻收尾、寫入 `alarm_events` `event_type='tamper'` 附 BMP 快照）；設定鍵 `detect.tamper.enabled`，設定中心「功能」頁開關） |
| 多語言 i18n | RESX 資源庫 + 語言切換（繁中/簡中/EN/日本語）；字型與格式全面參數化 |

### 14.5 P2 建議（差異化 / 擴展）

- **智慧搜尋**：於 AI 事件上延伸——依人/車/時間區間/指定 ROI 篩出縮圖牆→單擊直達該段錄影
- **統計報表**：錄影時數、斷線次數、容量趨勢、AI 事件統計（VIP）— 每週排程郵寄
- **中央管理（上部 NVR 集群）**：一台上層 VMS 看多台 NVR 的狀態與跨機回放（XML/XMPP 或自訂協議）
- **雙向對講**：完成 `ITalkProvider`（海康/大華對講），搭配門禁呼叫燈
- **感測器/乾接點 IO**：隊列通訊（MODBUS/TCP 或 IO 模組）讀門磁/煙霧/紅外主機乾接點 → 寫入 `alarm_events` 並可觸發錄影策略

### 14.6 P3 建議（法遵 / 商業）

- **Legal Hold**：單通道指定時段「暫時封存」不被配額汰除；沖銷前需高權限操作並留稽核
- **GPS 時鐘**：選配接收器校正 NTP 偏移，提供「時間可信度」等級標示於回放 OSD
- **雲整合**：雲訂閱（備份/遠程/通知）— 商業化階段
- **斷線補錄**：重連後透過 Profile T 或 SDK 讀設備 SD 卡在斷網期間的自錄段，補抓到本地索引並標記 `source='sdcard'`

### 14.7 現今 VMS 市場對標：HeliVms 缺口補遺

> 以 Milestone XProtect 2026 R1、Genetec Security Center、QNAP QVR、Synology Surveillance Station、
> Frigate 0.16+、Blue Iris 為對照基準，盤點我方尚未涵蓋或需深化的能力。

| # | 類別 | 欠缺功能 | 市場代表 | HeliVms 現況 | 建議 |
|---|---|---|---|---|---|
| 1 | 身份整合 | **企業帳戶：LDAP / AD / OIDC SSO** | Milestone（Active Directory、OIDC SSO）、Synology | **本機帳號＋RBAC（PBKDF2＋失敗鎖定，M42 已實作）**＋OIDC 驗證/LDAP 設定（M50 `EnterpriseAuthService`）＋LDAP 連線層（M85 `LdapClient`）＋企業登入（M99 `LdapLoginBroker`＋`login_sessions` v36）＋**OIDC 授權碼＋PKCE 登入（M100 `OidcLoginFlow`）已落地** | **P1**（企業標案基本門檻） |
| 2 | 影像 | **魚眼攝影機矯正（Dewarping）** | QNAP Qdewarp、Genetec、Synology | **魚眼矯正服務＋ffmpeg v360 filter（`DewarpService`/`DewarpFilter`/`DewarpWindow`）已落地** | P1（魚眼/全景漸普及） |
| 3 | 事件營運 | **警報管理器（Alarm Manager）**：分診/指派/傳遞/進度狀態大面板 | Milestone Alarm Manager | 事件中心＋四態(§14.4)＋**分診面板（M47 alarm_triage）＋分診工作流 L1（M102 `alarm_notes`＋`alarm_escalations`＋`AlarmEscalationPolicy` SLA 升階，v38）已落地**；智慧牆大面板 UI 待續 | P1（營運效率亮點） |
| 5 | 影像 | **遮蔽回放/匯出（Redaction）** | Genetec Digital Evidence、Synology 遮蔽格式 | **遮蔽資料層＋BCL 處理引擎 L0（M101 `RedactionRepository`＋`RedactionProcessor`，v37）已落地**；**快照一鍵遮蔽已落地（M116 `SnapshotRedactor`＋`SnapshotRedactionService`）**；回放/匯出影像遮罩待續 | P1（個資法合規優先） |
| 5 | 隱私 | **錄影遮蔽/模糊化（Redaction）** | Genetec Digital Evidence、Synology 快照模糊 | **遮蔽區域倉儲＋純 BCL 處理引擎 L0（M101 `RedactionRepository`＋`RedactionProcessor`，v37）已落地**；回放/匯出串接待續 | P1（個資法交付加分） |
| 6 | AI | **模組化分析情境套件**：周界/靜止車輛/尾隨/區域防護/方向控制 | Genetec KiwiVision | 核心已規劃（§5.6 分析情境＋§5.7 追蹤＋§5.10 規則） | P2（能力開放，§19 銷售位元） |
| 7 | 搜尋 | **法證語意搜尋**（NLP / CLIP 語意）、影片摘要 | Genetec Forensic Search、Frigate CLIP | **門禁/POS/Edge AI/警報多源統一檢索已落地（M91 `EventSearchRepository`＋M97 `UnifiedEventSearch`，FTS5 4 表）＋規則式中文 NLP 查詢解析（M107 `NlEventQueryParser`）已落地**；CLIP/AI 向量語意待續 | P2（差異化賣點） |
| 8 | 整合 | **統一安全平台**：門禁/入侵感測/POS(Metadata 配對) | Genetec 平台、Synology Transactions、QNAP Metadata Vault | DI/DO＋綁定鏡頭已規劃(§16.2)；**門禁事件 L0（M92）＋POS 交易存錄/時間窗配對 L0（M93）已落地**；**POS 接口擴展 L1（M104 去重匯入＋`QueryByRegister`＋`PosReconciliation` 對帳統計）已落地**；**POS→門禁自動核對（M113 `PosEventLinker` 同頻窗關聯）已落地**；異常視覺化待續 | P2 |
| 9 | 可靠 | **Failover 容錯**（第二記錄伺服器） | Milestone Failover、QNAP QVR Guard | 僅本機看門狗＋**租約仲裁 L0（M87）＋監控視窗/harness（M88）＋實體接管協調 L1（M98 `FailoverCoordinator.Reconcile`＋`failover_events` v35 軌跡）已落地** | P2 |
| 10 | 儲存 | **Edge Storage 雙保險**（設備 SD 側錄 + NVR） | Milestone、Genetec、Synology | **L0 補抓規劃器（M89）＋L1 執行器（M94）＋真實 ffmpeg runner（M96 `EdgeFfmpegBackfillRunner`）已閉合** | P2 |
| 11 | 地圖 | **智慧地圖深化**：視角扇形 FOV/深度 | Milestone Smart Map | **電子地圖（M41 `MapWindow`：樓層切換、圖釘含 camera 扇形視角 FOV、事件閃爍、雙向定位）已落地**；深度/設備自動布局待續 | P2 |
| 12 | 錄影 | **Adaptive Streaming / SVR 品質自適應** | Milestone | **`BitrateGovernor`（錄影品質/幀率自適應控流）已落地**；SVR 跨設備協調待續 | P3 |
| 13 | 邊緣 | **消費邊緣 AI 相機 metadata**（D2C/方向） | Genetec、Frigate | **L0 消費/軌跡/方向分類（M90）＋L1 持久化/查詢/分向摘要（M95 `EdgeSmartEventRepository` v33）已落地** | P3 |
| 14 | 顯示 | **智慧牆（Smart Wall）多螢幕拼接控制** | Milestone Smart Wall | 僅雙螢幕(§11.3)；**智慧牆版面資料模型＋幾何校驗＋看板時間常數（M105 v39 `SmartwallLayoutRepository`＋`LayoutGrid`）＋警報看板引擎 L0（M106 `SmartwallAlertBoard`）已落地**；視訊牆 UI/派送掛載待續 | P3 |
| 16 | 影像 | **快照一鍵遮蔽（快照隱私）** | Synology | **已落地（M116 `SnapshotRedactionService` 按 snapshot 區套用＋`RedactionRepository` 區位資料層）** | **P3** |
| 16 | 分割 | **即時快照「一鍵模糊/遮蔽」** | Synology | 沒有 | P3 |
| 17 | 偵測 | **VMD 靈敏度自動調校** | Milestone Auto-VMD | **`SensitivityAutoTuner`（事件率統計→升/降靈敏度建議，Alarms）已落地**；逐場景自動套用（M114 `SensitivityAutoApplier` clamp 寫回＋稽核）已落地 | P3 |

**已覆蓋確認（對標後確認不需補）**：巡視 Tour、多分屏/輪播、PTZ/預設點/巡航、日排程、主/次流、motion/AI 事件、事件前預錄、浮水印、匯出+hash、多通道同步回放、快速回放、雙向對講、車牌 OCR (L2)、人臉 (L2)、雲備份(方向)、Web/行動(§14.3 P0)。

> 補充說明：#1 身份整合建議提至 P1 —— 大型案與政府標案常要求「沿用企業 AD」，屬准入門檻；
> #7 語意搜尋是 2025–2026 開源/商業共同趨勢（Frigate 0.16 CLIP、Genetec NLP），可作為 v2 差異化賣點。
> 此表為對標補遺，不改變核心閉環優先序。

### 14.8 對里程碑的調整建議

| 里程碑 | 調整 |
|---|---|
| M1–M2 | 不變（核心管線優先） |
| M3 | ✅ 匯出工作流基礎（單段匯出+驗證報告）納入 |
| M4 | ✅ 新增 WebApi 骨架（REST + 稽核），為遠程準備 |
| M5–M6 | 不變（AI/設備管理） |
| M7 | 遠程存取（WebRTC 監看 + HLS 回放） |
| M8 | 地圖模式 / 證據包 / 備份 / 事件工作流（P1 批次） |

> 原則：P0 不改變「監看→錄影→回放」核心閉環的優先順序；確保核心成熟後再疊加，避免範圍蔓延。
> 本節為動態路線圖，將隨市場/使用者反饋迭代。

---

## 15. 錄影規劃總覽（依類型分列的完整方案）

> 將文內 §3（管線）、§4（資料庫）、§6（排程）、§10（專業需求）、§14（路線圖）
> 之錄影相關設計依「類型」彙整，作為單一查閱入口。

### 15.1 依「觸發模式」類型

| 類型 | 行為 | 設計要點 | 資料表欄位 |
|---|---|---|---|
| **全時錄影** | 24/7 不間斷 | 預設 `always`；配額汰除保底 | `channels.recording_mode` |
| **排程錄影** | 依週計劃起停 | 7×24 矩陣編輯（§6）；30s 檢查 | `schedules`（day_mask/start/end） |
| **事件錄影** | 觸發才錄（含前後預錄） | L0/L1 或 IO 觸發；預錄 I 幀掃描 | `recording_mode=motion` + `alarm_events` |
| **混合/Smart** | 平時次流，事件切主流（含往後 N 秒） | 事件級最高畫質保證，省空間主力（§11.2） | 事件設定檔 + `segments.stream` |
| **手動錄影** | 使用者臨時開關 | 頂欄/格面工具列即時啟停 | `recording_mode=manual` 暫存 |

### 15.2 依「影像品質 / 碼流」類型

| 類型 | 組合 | 適用 |
|---|---|---|
| 純主碼流 | 主流 `-c copy` 全時 | 畫質優先（預設） |
| 純次碼流 | 次流全時 | 容量/頻寬受限（§10.2 估算） |
| 雙碼流同時 | main + sub 兩系列檔 | 證據 + 檢索（§3.2） |
| Smart 切流 | 次流常態，事件瞬間切主流 | 商場/廠房大範圍（推薦） |
| SVR/自適應品質 | 依頻寬/負載調整 | 未來（§14.7 #12） |

### 15.3 依「儲存位置」類型

| 類型 | 說明 | 狀態 |
|---|---|---|
| NVR 本機主存 | HDD 主錄影區（OS+索引走 SSD） | ✅ 核心 |
| 設備 Edge SD 側錄 | 斷網期間自錄，重連補抓 | P2 強化（§14.7 #10） |
| 異地/雲備份 | 排程批次複製區段至 NAS/雲端 | P1（§14.4） |
| 快照/縮圖庫 | 事件快照集中目錄 | ✅ 核心（snapshots/） |

### 15.4 依「錄製內容」類型

| 內容 | 設計 | 狀態 |
|---|---|---|
| 視訊軌 | H.264/H.265 `-c copy` | ✅ 核心 |
| 音訊軌 | 開關可設；G.711/PCM 轉 AAC-LC 128k（§3.2） | ✅ 核心 |
| Metadata／事件 | AI 結果、時間戳、置信度入 `alarm_events` | ✅ 核心 / L1 後 |
| 浮水印 OSD | 通道名/牆鐘時間/主次流標示（顯示層，不燒錄） | ✅ 核心（§8.4） |

### 15.5 依「格式與容器」類型

| 格式 | 用途 | 狀態 |
|---|---|---|
| **fMP4** | 正式錄影（GOP 自含、斷電容忍、seek 精準） | ✅ 主格式（§3.2） |
| 一般 MP4 | 匯出交付 | ✅（§8.5 匯出精靈） |
| MKV | 原始碼流備援 / 臨時暫存 | 備用 |
| JPEG 快照 | 警報/AI 事件與單幀快照 | ✅ 核心 |

### 15.6 依「生命週期管理」類型

| 機制 | 設計 | 狀態 |
|---|---|---|
| 區段化 | 5–10 分鐘/段，`reset_timestamps` | ✅ 核心 |
| 保留策略 | 天數 / 容量 / 事件優先三模式 | ✅（§4 配額） |
| 配額汰除 | 預警>優先刪事件段>停新錄保全（§8.1） | ✅ 核心 |
| Legal Hold | 指定時段封存不被汰除 | P3（§14.6） |
| 索引與復原 | `segments`+`segment_keyframes`；`.tmp` 修復 | ✅ 核心 |

### 15.7 依「可靠性策略」類型

| 策略 | 設計 | 狀態 |
|---|---|---|
| 落盤保證 | 邊寫邊 `fsync`、先 `.tmp` 後改名 | ✅ 核心 |
| 斷電容忍 | fMP4 fragment 自含，最多損 1 GOP | ✅ 核心 |
| 完整性 | 寫入紀錄 SHA-256 + 定期 `ffprobe` 抽查 | ✅ 核心（§9.1） |
| 斷線處理 | 指數退避重連、接續新段不中斷 | ✅ 核心 |
| Failover | 第二錄影伺服器即時接管 | P2（§14.7 #9） |
| AI 不中斷錄影 | 推理獨立進程，崩潰僅失偵測 | ✅ 核心（§5） |

### 15.8 錄影設定模型（每通道完整參數，對應 §4 資料庫）

| 參數 | 欄位 | 預設 |
|---|---|---|
| 錄影模式 | `recording_mode` | always |
| 主/次流來源 | `main_rtsp` / `sub_rtsp` / 流用途 `use_role` | - |
| 編碼 | `codec` | h264 |
| 音訊 | `audio_enabled` / `audio_encoder` | 1 / aac |
| 區段長度 | 系統設定 | 600s |
| 保留與品質 | 保留天數 / 質量級 | §9 設定 |
| 事件設定 | `motion_enabled` / `motion_sensitivity` | 0 / 0.5 |
| 排程規則 | `schedules` | - |
| 事件切流 | Smart 事件設定 | 次流→主流 |

> 本表為設計意圖總彙整；實際欄位以 §4 建表腳本與授權實作時為準。

---

## 16. 電子地圖與警報 IO

> 兩者高度連動：電子地圖是「空間視覺化」入口，警報 IO 是「外部物理世界的數位神經」。
> 將 §14 之 Map View（#3）與感測器 IO（#16）從概念升級為完整設計。

### 16.1 電子地圖（E-Map / Map View）

**目標：場域空間感一覽，所有裝置狀態與事件視覺化。**

- **地圖基底類型**
  | 類型 | 作法 | 適用 |
  |---|---|---|
  | 自訂平面圖 | 上傳 PNG/DXF/背景圖，支援縮放/平移/比例尺標定 | 店鋪/廠房/校園 |
  | 線上磚圖 | OSM（免費）/ Google（需金鑰）可選 | 廣域/戶外 |
  | 多樓層 | 樓層頁籤切換，圖釘各樓層獨立 | 大樓/賣場 |
- **圖釘與圖層**
  - **攝影機圖釘**：含視角扇形（FOV/深度，承 §14.7 #11）、PTZ 方向、離線紅點 / 錄影綠點 / 警報閃動
  - **感測器/IO 圖釘**：DI/DO 狀態即時呈現（門開啟／煙霧／緊急按鈕）
  - 事件圖釘：最近警報位置臨時標記，點擊跳轉回放
  - 警戒區繪製：poly 區域（進入偵測）、圖例、命名
- **互動**
  - 單擊圖釘 → 即時影像浮窗；雙擊 → 進入該鏡頭監看/回放
  - 事件發生 → 對應圖釘閃爍 + 可選「視窗自動跳轉」
  - 警報列表點擊 → 地圖定位（雙向聯動）
- **資料模型（M41 已實作，schema v10）**
  ```sql
  maps(id PK, name, type DEFAULT 'plan', image_path, width, height,   -- 原圖尺寸（0..1 座標比對）
       enabled DEFAULT 1, sort_order DEFAULT 0, created_at);
  map_devices(id PK, map_id REFERENCES maps(id) ON DELETE CASCADE,
              device_type CHECK('camera'/'io'), channel_id,           -- camera→channels.id；io→io_channels.id
              x REAL, y REAL,             -- 0..1 比例座標（圖面縮放/換圖不跑位）
              angle REAL DEFAULT 0, fov_deg REAL DEFAULT 90, fov_depth REAL DEFAULT 3,
              enabled DEFAULT 1,
              UNIQUE(map_id, device_type, channel_id));
  ```
- **M41 實作摘要**：`MapRepository`（Storage）＋主視窗「地圖」`MapWindow`（樓層頁籤、Image 背景＋Canvas
  圖釘、滾輪縮放/拖曳平移、camera 視角扇形、io 狀態點、事件觸發橙色閃爍、雙擊→回放）；設定中心「地圖」
  頁（nav 第 9 項）管理地圖與圖釘；事件中心「在地圖定位」雙向聯動；事件閃爍依 `alarm_events` 對應 channel
- 佈設：圖釘拖放、批次上版、可複製背景圖層（承接未來 §14.7 #11 智慧地圖）

### 16.2 警報 IO（Alarm Input / Output）

**目標：外部物理感測與執行器融入事件引擎與地圖。**

- **DI（數位輸入）常見來源**：門磁、紅外/微波周界、煙霧、緊急按鈕、斷電偵測
- **DO（數位輸出）常見用途**：聲光警報器、電擊門鎖、補光燈、繼電器控制

- **接入方式（依優先）**
  | 方式 | 說明 | 適用 |
  |---|---|---|
  | ONVIF I/O | Profile S 標準 IO（綁定通道） | 相容品牌 |
  | 品牌 SDK | 海康/大華警報輸入輸出 | 該品牌主場 |
  | 網路 IO 模組 | Modbus/TCP 或自有 TCP（低價量產） | 通用部署 |
  | RTSP/RTP alarm | 部分設備串流內嵌事件 | 特殊 |
- **事件引擎整合（擴充 §5 / 對應 §14.7 #8 統一平台）**
  - DI 觸發 → 寫入 `alarm_events`（`event_type='io_input'`、`io_channel_id`、狀態、時間）
  - **鏡頭綁定**：IO 觸發 → 綁定鏡頭 Smart 錄影 ＋ 快照（`io_event_map`）
  - **動作鏈（Actions）**：DO 自動回應（煙霧→聲光＋鎖門）、通知（Webhook/郵件/聲音，推播 P3）
  - **去抖動**：`debounce_ms` 防誤觸（電磁/雷擊抖動）
  - 警報分級：緊急（煙霧/緊急鈕）vs 一般（門磁）→ 地圖顏色分級
- **DO 操作**：測試面板一鍵開/關（含狀態回饋）、DO 排程、DO 與事件/時間聯動
- **資料模型（M40 已實作，schema v9）**
  ```sql
  io_devices(id, name, protocol,    -- 目前 modbus_tcp
             host, port DEFAULT 502, unit_id DEFAULT 1,
             enabled DEFAULT 1, poll_ms DEFAULT 500);
  io_channels(id, device_id → io_devices CASCADE,
              direction,           -- DI / DO
              io_index, name, enabled DEFAULT 1,
              debounce_ms DEFAULT 200, polarity DEFAULT 0,   -- DI 去抖＋常閉反相
              camera_id NULL → channels（DI 綁定，io_input 事件需綁定）
              alarm_priority DEFAULT 'normal',
              UNIQUE(device_id, direction, io_index));
  ```
  - 未來擴充：`io_event_map`（鏡頭/動作鏈）、`pulse_ms/default_state`（DO 脈衝）、ONVIF/品牌 SDK 接入（protocol 欄預留）
- **M40 實作摘要**：`IoRepository`（Storage）＋`ModbusTcpClient`/`IoDeviceMonitor`（HeliVMS.Alarms，
  FC02 Read Discrete Inputs／FC01 Read Coils／FC05 Write Single Coil，MBAP echo 驗證）；監視器以 `poll_ms` 輪詢
  啟用 DI 通道 → debounce 狀態機 → 上升沿寫 `io_input` 事件（`EventInserted`）→ 下降沿 `UpdateEnd`；
  `IoMonitorHost`（HeliVMS.App）按 io_devices 管理監視器；設定中心「IO」頁（nav 第 8 項）管理模組/通道並提供
  DO 測試；事件中心 `io_input` 以橙色呈現。
- **測試/Tool**：掃描 IO 模組、單點 TTL（`io_probe`）、DI 事件回放檢視

### 16.3 地圖 × IO × AI × 錄影 整合案例

```
門磁開啟 (DI) ─► 警報分級=一般
   ├─► 地圖：該感測器圖釘閃爍＋綁定鏡頭角標
   ├─► 綁定鏡頭 Smart 錄影（次流→主流＋預錄）
   ├─► AI（§5）判斷「有無人員」→ 有人：緊急通知
   └─► 動作鏈：DO 開補光燈／發 Webhook
```
- 統一事件源：AI 事件、IO 事件、設備離線、Tamper 皆彙入同事件引擎與地圖圖層

### 16.4 對應更新

- §14.1 #3 Map View：升為 **P0′（本節完整設計）**
- §14.1 #16 感測器 IO：升為 **P1**
- §10 里程碑：**M8** 內含「電子地圖＋警報 IO」交付
- 模組：新增 `HeliVms.Io`、`HeliVms.Maps` 專案（見 §2 目錄結構）

### 16.5 驗收標準

- [ ] 上傳平面圖 → 佈攝影機/IO 圖釘 < 5 分鐘完成
- [ ] DI 觸發後：地圖閃爍 + 綁定鏡頭自動 Smart 錄影 + 動作鏈生效
- [ ] DO 由測試面板遠程開關且有狀態回饋
- [ ] 多樓層切換與「警報列表↔地圖」雙向定位流暢

---

## 17. 即時監看完整功能

> 收斂並展開 §3.3（管道）與 §8.4（互動）之即時監看，涵蓋多分割、數位放大、PTZ、拍照。

### 17.1 多分割布局

- **預設布局集**：1 / 4 / 6 / 8 / 9 / 13 / 16 / 25 / 32（含 12／20 自選）；頂欄布局切換器 ＋ 快捷鍵
- **自訂布局**：任意格位組合（拖格調整）、儲存多組命名布局、一鍵套用；布局含每格影音設定（音訊靜音/跟隨）
- **拖放交換**：格位拖放換鏡頭；空格先填已連線通道（智慧填充）
- **頁簽式布局**：多分頁（如「大門區」「廠房區」），各記憶布局與通道
- **雙螢幕**：拖動格面浮窗至副螢幕當獨立分割牆（§11.3）
- **全螢幕**：F11 全螢幕、隱藏 OSD、滑鼠移動才顯示工具列
- **輪播巡視（Sequential Rotation）**：整牆自動切頁、間隔可設（5–120s）、暫停即停
- **每格狀態角標**：● 錄影紅點 / 連線綠 / 離線灰 / 警報閃爍 / 遮蔽標示；右上角 FPS

### 17.2 單路放大

- 點兩下 → 單路放大（浮窗置頂，可拖至副螢幕）；再點兩下或 Esc 還原
- 放大窗保留完整功能：PTZ、快照、語音、錄影開關、立即回放
- 放大與布局間切換狀態互不遺失（多分割中的格子不死，回歸原位）

### 17.3 數位放大（Digital Zoom / ePTZ）

- **操作方式**：滾輪縮放（以鼠標為中心）、拖曳框選局部放大、＋/－鍵步進
- **技術**：於解碼幀上裁切＋`swscale` 縮放（ePTZ），**不重開串流**（頻寬零增加，承 §14.7）；放大 1–8×、顯示倍率指示
- **解析度策略**：放大時靜態讀取通道最高解析度幀（暫占硬解通道），鬆開自動釋放；全屏中縮放時優先滿解析
- **與其他功能互斥**：影像處理濾鏡（§8.4 美化）啟用時禁縮放；縮放中不觸發 PTZ 動作
- **回放互補**：即時監看不支援的 zoom 由回放 seek 取幀補齊

### 17.4 PTZ 控制（Live PTZ 控制台）

- **方向**：8 向 D-Pad（含斜向）、Home 回中、連續移動 ContinuousMove
- **速度**：速度滑桿（0–100%）即時反應；按住方向持續移動、放開停止
- **Zoom/Focus/IR**：電動變焦、對焦微調、AutoFocus、IR 開關（如設備支援）
- **預設點**：儲存 / 呼叫 / 命名 / 刪除（與 §7.3 管理共用同一資料）
- **巡視（Tour）**：啟動 / 停止、進度顯示
- **點擊跟蹤**：滑鼠於畫面點擊 → 該點置中（ONVIF 相對移動或 L2 SDK，§7.9.6 轉譯）
- **顯示**：格面 hover 浮動 PTZ 棒、單路放大/全螢幕常駐；連續動畫指示
- **焦點鎖定**：回放或縮放中自動禁用 PTZ（防誤觸），可設定切回

### 17.5 拍照（Snapshot）

- **單路**：快門鍵 / 右鍵 / 鍵盤 `S` — 立即抓當前顯示幀；對話框預覽、選格式（JPEG/PNG）、畫質、儲存路徑
- **多路批量**：「整牆快照」一次抓取目前布局內所有通道（齊時）
- **來源**：監看框抓即可（響應 <200ms）；錄影中單幀提取無需開流（mpv screenshot，§3.3）
- **浮水印**：每張含通道名＋牆鐘時間（可選燒錄），證據力與個資法一致
- **檔名規則**：`CAM{channel}_{yyyyMMdd_HHmmss_fff}.jpg`，集中 `snapshots/`
- **快照瀏覽器**：按通道／時間段檢索、縮圖牆、勾選批次下載／刪除、可一鍵送匯出精靈封包
- **與事件整合**：警報/AI 觸發的快照（§5）與手動快照同源管理

### 17.6 其他即時監看增強

- **即時語音**：揚聲器按鍵即時監聽（§3.4）
- **立即回放**：格面點「立即回放」→ 該通道過去 30/60s 浮窗回放，不退出監看
- **手動錄影**：格面上錄影開關即時啟停（對應 §15.1 manual）
- **診斷模式**：每格顯示 FPS / 延遲 / 解碼負載（on-demand，效能無感）
- **OSD 浮水印**：通道名＋時間＋錄影紅點（顯示層，隨佈局開關）

### 17.7 驗收標準

- [ ] 32 路布局切換 < 1 秒無明顯卡頓
- [ ] 數位放大至 8× 順滑（≥15fps）、框選縮放命中正確
- [ ] PTZ 方向/速度/預設點/巡視在兩品牌（例：海康 L2＋Axis/ONVIF L1）驗證通過
- [ ] 快照 1 鍵 < 200ms 完成且含浮水印；批量快照齊時
- [ ] 放大/回放中 PTZ 鎖定生效，無誤觸

---

## 18. 網路與通訊總覽

> 彙整設備連接（北向）、本地 IPC、遠程存取、通知外送、安全與診斷為統一視圖。
> 關聯：§3.1（串流）、§14.3（遠程）、§5（通知）。

### 18.1 三平面架構與埠規劃

| 平面 | 用途 | 走線 | 埠（可改） |
|---|---|---|---|
| **控制平面** | 設定/查詢/PTZ/管理 | REST API（本地 IPC 或遠程）＋ WebSocket 事件 | 8443 (TLS) |
| **媒體平面** | 即時監看/回放/對講 | RTSP(內) / WebRTC(遠程) / HLS(相容) | 555 內（RTSP）、UDP 19022+（RTC） |
| **通知平面** | 警報外送 | Webhook（出站）／SMTP／SNMP trap | 出站 443/587/162 |
| 本機 IPC | UI ↔ 錄影服務 | 具名管道 `\\.\pipe\HeliVms\svc`（本機預設）；TCP 8443（遠程管理，選配） | - |

- 所有埠管理集中一份 `ports` 設定群；衝突偵測與佔用顯示
- 遠程存取建議經反向代理終止 TLS（Caddy/nginx），HeliVms 內部可混用 HTTP，降低面風險

### 18.2 設備連接（北向：HeliVms → 攝影機）

- **傳輸模式**：RTSP 預設 **TCP**（穩定、穿透易）；可選 UDP（低延遲）、組播 multicast（UV 場合）
- **認證**：Digest 優先、Basic 兜底、RTSP over TLS（若設備支援）
- **頻寬預算**：32 路全主流 4M ≈ 128 Mbps → 抽幀/次流自動降載（§3.3）；預算超過時優先保錄影路
- **健康與重連**：心跳 + 指數退避（1s→2s→4s…≤60s），斷線不中斷已緩存幀；SPS/PPS 變更後重新協商（`h264` paramset 更新、VPS/SPS 節流）
- **網路種類標記**：即時連線（監看）可短暫離線；錄影連線永不因監看讓出
- **多網卡**：可指定來源網卡（管理網/錄影網分離）
- **WS-Discovery／ONVIF 探索**：§7.2 皆走此介面

### 18.3 本機 IPC（UI ↔ 錄影核心）

- 預設**具名管道**（本機零 TCB）；同一主句可以 TCP 8443 遠程接管（P1 多機）
- 協定：**訊息（binary, MessagePack）**優先於 JSON（低開銷），RPC + 事件訂閱兩種通道
- 事件訂閱：`subscribe` 過濾器（依通道/事件型別），心跳保活、逾時自動重連
- 版本相容：請求帶 `protocol_version`，服務端向後相容、舊版客戶端提示升級
- 渲染極限考量：媒體永不經 IPC — 影像以共享記憶體/句柄直接呈現（次級回路）

### 18.4 遠程存取（P0，承 §14.3）

- **API**：`HeliVms.WebApi`（ASP.NET Core）REST + JWT（短效 access＋refresh）；WS 事件推送
- **實時監看**：**WebRTC**（WHIP 入/ WHEP 出），STUN/TURN 可配（NAT 穿透），延遲目標 < 1.5s
- **回放**：HLS（依關鍵幀索引生成片段，§3.2）／MPEG-DASH 支援
- **下載**：遠程喚試取錄影片段（限制時長與速率，防止拖垮頻寬）
- **TTL／稽核**：遠程連線記錄、檢閱者權限起跳（§14.3）
- **反向代理整合**：X-Forwarded-* 取真實 IP、IP 白名單、失敗登入鎖定

### 18.5 通知外送（通知平面）

| 通道 | 用途 | 狀態 |
|---|---|---|
| Webhook | JSON payload `{type, channel, ts, data}` 推送至第三方系統 | ✅ 核心（§5 INotifier） |
| SMTP | 事件郵件（含快照附件） | P1 |
| SNMP trap | 整合既有網管中心（NOC） | P1 |
| 行動推播 | 遠程 App 警報推送 | P3 |
| MQTT | 自動化平台（HA 等，§14.7 #15） | P3 |
- 通知內容仲裁：同事件 N 分鐘合併、靜默時段（免擾）、可驗證重試（指數退避＋queue）

### 18.6 安全與認證

- **本地**：本機帳號 **PBKDF2**（BCL `Rfc2898DeriveBytes`，SHA-256、100k iter）＋失敗鎖定、RBAC admin/viewer，**M42 已實作**；**企業**：LDAP/AD/OIDC SSO（P1，§14.7 #1）
- **TLS**：TLS 1.2+；憑證可自簽（將導引申請）或 ACME 自動簽發；私鑰保護（DPAPI）
- **埠偵測**：預設埠綁定 127.0.0.1（本機 IPC），僅遠程選配才對外開放
- **速率限制**：API 每 IP 限速、登入滑動驗證、CSRF 保護（Cookie 場景）、CSP 標頭（Web）
- 稽核：所有網絡存取寫 `audit_logs`（§11.5），含 IP／來源方

### 18.7 網路診斷與工具

- **介面健康**：每通道網路統計（封包遺失/抖動/重傳/RTT）
- **網路工具**：ping／traceroute／埠測試（含「錄影核心→攝影機」往返）、測速（上/下行）
- **抓包輔助**：FFmpeg `-trace`/轉 dump 待選目錄，支援客服診斷（§11.6）
- **QoS**：媒體資料可打 DSCP/AF（企業網）、流量塑造限制遠程觀察帶寬
- **多 NIC 監控**：哪個網卡貢獻高頻寬／故障切換顯示

### 18.8 穩定性設計

- **jitter buffer**：監看可容忍 50–150ms 抖動；錄影走穩定管道不理會短期抖動
- **重協商**：編碼參數變更（SPS/PPS/GOP 變動）自動重新初始化解析器，不重連
- **緩衝與背壓**：解碼器佇列有限，滿則抽幀（面板 FPS 顯示實際值）
- **網路風暴保護**：單通道重連頻率限流、全站連線數上限、發現風暴限流（WS-Discovery 間隔）
- 遠程斷線後：WebRTC 自動 ICE restart、HLS 播放器續播銜接

### 18.9 驗收標準

- [ ] RTSP over TCP 32 路連續 7 天零中斷（含斷電重啟）
- [ ] 斷線重連 < 5s 且錄影不造成空洞
- [ ] 遠程 WebRTC 監看延遲 < 1.5s、HLS 回放可 seek
- [ ] Webhook 觸發至第三方送達 < 2s（含重試保證）
- [ ] 遠程 API 失敗鎖定與稽核軌跡完整可追

---

## 19. 授權與許可管理（整合 Tools/ 授權系統）

> 整合既有 `Tools/LicenseKeyGen`（HMAC 對稱版）與 `Tools/LicenseKeyGenUI`（RSA-2048 版）兩套工具，
> 正式定案為一套 **非對稱簽章授權** 並接入 HeliVms 全產品。

### 19.1 方案定案：RSA-2048 非對稱簽章

| 項目 | HeliVms 採行 | 說明 |
|---|---|---|
| 簽章 | **RSA-2048 / SHA-256 (PKCS#1)** | 驗證只需公鑰；私鑰僅存廠商側，出貨產品無法偽造金鑰 |
| 授權碼格式 | `HELVMS-v2.{Base64URL(payload)}.{Base64URL(signature)}` | 承 LicenseKeyGenUI；payload 為 UTF-8 JSON |
| 公鑰嵌入 | 產品端 `HeliVms.Licensing` 編譯期內嵌 `EmbeddedPublicKey` | 授權碼獨立字串，可傳真/Email 交付，免隨身碟 |
| 設備碼 | **WMI（CPU ProcessorId + MainBoard SerialNumber）→ SHA-256 → 前 32 碼 HEX** | 兩工具同邏輯；**綁定全 32 碼，不接受前 4 碼子集** |
| 到期授權 | payload 含到期日；`永久` 以「無 exp 欄位」表示 | 勿再用「年份偏移+月日」的低精度編碼 |

### 19.2 授權碼 Payload 定義（v2）

```json
{
  "mid":  "設備碼32碼HEX",          // 必填
  "max":  64,                       // 最大通道數 1~1024
  "tip":  "企業版",                 // 等級：基本/標準/專業/進階/企業/客製
  "exp":  "2027-12-31",             // 選填；缺省=永久
  "iss":  "2026-09-13",             // 簽發日（用於回流時鐘防護）
  "lic":  "禾秝公司"                // 授權對象（公司/案場名）
}
```

### 19.3 等級 → 功能矩陣

| 等級 | max 建議 | 即時監看 | 錄影/回放 | 事件+AI(L0) | L1 AI 人/車 | L2 車牌/人臉 | 地圖/IO | 遠程存取 | 企業 AD/SSO |
|---|---|---|---|---|---|---|---|---|---|
| 基本版 | 4 | ✅ 4 路 | ✅ 全時 | ✅ motion | ❌ | ❌ | ❌ | ❌ | ❌ |
| 標準版 | 8 | ✅ | ✅ 含排程 | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ |
| 專業版 | 16 | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ❌ |
| 進階版 | 32 | ✅ | ✅ | ✅ | ✅ | ❌ | ✅ | ❌ | ❌ |
| 企業版 | 64 | ✅ | ✅ | ✅ | ✅ | ✅(選購) | ✅ | ✅ | ✅ |
| 客製版 | ≤1024 | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |

> 載入授權後，UI 依等級**隱藏未授權功能**（非僅停用按鈕），避免誤導。

### 19.4 產品端整合點（HeliVms.Licensing）

- **驗證時機**
  1. 錄影核心（`HeliVms.Recording`）啟動：失敗 → 拒絕錄影並登入稽核（`audit_logs`，`event='license'`）
  2. 管理介面：顯示授權摘要（等級/到期/剩餘通道）與「匯入授權」精靈
  3. 遠程 API（WebApi）：合並檢查，避免遠程繞過（§18.4）
- **導入流程**：貼上授權碼 → 收集本機設備碼（UI 顯示給使用者複製）→ 會員/客服工具核發 → 驗證簽章＋綁定設備碼一致 → 生效
- **超限行為**：新增第 N+1 通道時軟性阻擋（可瀏覽設備但不可錄影），並於事件中心提示「已達授權上限」
- **到期行為**：到期前 14 天 UI 浮條提醒；到期後停止**新增**錄影，既有錄影檔仍可回放（不可勒索客戶）
- **回流時鐘防護**：本機記 `license_max_seen_dt`（首次啟用與每次成功驗證），若 `exp < 該時間-7 天` 判定時間被改回 → 停用並提示重新校時
- **離線優先**：驗證全在本機（無需網路）；線上啟用/綁定帳號列 P3 雲端強化

### 19.5 既有工具處置

| 工具 | 現況 | 處置 |
|---|---|---|
| `Tools/LicenseKeyGenUI` | RSA 金鑰對＋簽章，功能正確 | **升級為正式 `Tools/LicenseProducer`** |
| `Tools/LicenseKeyGen`（HMAC） | secret 同時存在兩端、HMAC 僅 1 byte、device 綁定前 4 碼、預設密碼明文 | **標記停用**，移動至 `Tools/_deprecated/LicenseKeyGen/`（或刪除）；註解註明「請改以 LicenseProducer 簽發」 |
| `Password.dat` 登入 | SHA-256 無鹽、預設密碼 `hr22619219` 明文於程式碼 | 升級：bcrypt/PBKDF2＋首次登入強制改密碼；密碼不進程式碼（環境/由授權表 DB 管理） |

### 19.6 LicenseProducer（廠商側）功能

- 金鑰對產生（2048/3072/4096 可選）→ 產出 `public.key`（嵌入用程式碼片段）與受保護私鑰
- **批次簽發**：匯入 CSV（設備碼/等級/max/到期）一次簽發多張
- 登入與稽核：管理員操作全部留痕（誰簽了哪張）
- 密碼與金鑰安全：私鑰可選加密靜置（DPAPI/口令）；與程式碼分開存放
- 與 §19.4 對應：產生公鑰張貼於產品版的 `EmbeddedPublicKey`

### 19.7 資料表（追加至 §4）

```sql
license(id INTEGER PK,
        key_text TEXT NOT NULL,            -- 完整授權碼
        device_code TEXT NOT NULL,         -- 綁定設備碼
        tier TEXT, max_cameras INT, expired_at TEXT,  -- 快取欄（提升查詢）
        first_seen TEXT, last_verified TEXT,
        status TEXT,                       -- active / expired / time_rollback / revoked
        created_by TEXT, UNIQUE(device_code));
```

### 19.8 驗收標準

- [ ] 產生授權 → 於另一台機導入，綁定不相符時明確拒絕並提示查詢正確設備碼
- [ ] 未授權之等級功能於 UI 隱藏；超限通道無法啟動錄影
- [ ] 改回系統時鐘 → 7 天內偵測並停用，提示校時
- [ ] 授權啟用/到期/改版事件完整寫入稽核日誌

---

## 20. 系統架構總覽圖

> 圖形化總覽：進程部署、資料流、模組依賴。各細節見對應章節。

### 20.1 進程與部署拓樸

```
                        ┌──────────────────────────────────────────────┐
                        │          用戶端層（多進程可並行）              │
                        │  HeliVms.App (WPF 桌面)                       │
                        │   ├ 監看工作台*  ├ 回放工作台*  ├ 管理控制台   │
                        │  HeliVms.Web (SPA, P0) ／ 行動 App (P0)       │
                        │  LicenseProducer (廠商側 Tool, 不在客戶機)     │
                        └───────┬──────────────────┬───────────────────┘
                                │ 具名管道 MessagePack        │ TLS: REST+WS+WebRTC/HLS
                                │ (本機)                      │ (遠程, 經反向代理)
┌───────────────────────────────▼──────────────────┬────────▼────────────────────┐
│          錄影核心 (HeliVms.Recording Service)     │      遠程服務層 (P0)         │
│  Devices   Media    Recording   Alarms   Licensing│  HeliVms.WebApi: REST + JWT  │
│  ONVIF/RTSP FFmpeg  排程/配額   事件+AI   驗證    │  + WS 推送 + WebRTC/轉封裝    │
│  IO§16    H/W解碼   fMP4區段    Scheduler         │                             │
└───┬──────┬───────┬──────────┬───┬───────┬─────┬──┴────────────┬─────────────┬──┘
    │      │       │          │   │       │     │               │             │
    │RTSP/ONVIF│   │ fMP4    │SQLite│ 通知外送│ 回放解碼 IPC     │ 事件/快照     │ Webhook/
    │+品牌SDK │   │ 區段檔   │index│(SMTP/ │ (命名管道)         │             │ SNMP/SMTP
    ▼        │   ▼         │ .db  │  Webhook│                 ▼             │
┌──────────┐ │ ┌─────────┐ │      │  SNMP)  │        ┌───────────────────┐    │
│ IP 攝影機 │ │ │recordings│ │ ┌────┴──┐ MQTT │        │ HeliVms.Decoder  │    │
│ ×32 (雙流)│ │ │+snapshots│ │ │ 稽核/ │ (P3) │        │ 回放解碼子進程     │    │
└──────────┘ │ └─────────┘ │ │ license│      │        └───────────────────┘    │
             │ 裝置SD卡(Edge)│ └───────┘                                 ┌─────┴──┐
             └──────────────┘                                            │ 第三方系統│
                                                                        └────────┘
```

### 20.2 核心資料流（錄影 / 監看 / 事件）

```
錄影：Camera RTSP ─► Media.Pull ─┬─► Muxer(-c copy) ─► fragments\seg-*.mp4 ─► 落盤(fsync)
                                 └─► Media.Parse ─► 關鍵幀/區段中繼 ─► SQLite index.db

監看：Camera RTSP ─► Pull ─► 解碼(NVDEC/QSV) ─► YUV ─► 抽幀佇列 ─► 共享記憶體 ─► WPF 格面渲染
        └ 16ms/幀擇一、鎖送「錄影優先」硬性規則（§3）

回放：UI(時間T) ─► Storage 查關鍵幀索引 ─► ≤T 最近 I 幀 ─► Decoder.Open(file,seekUs)
      ─► YUV 幀 ─► 命名管道 ─► 主進程渲染（0.5×~16× 倍速、逐幀）

事件：L0/L1 偵測 ─► 事件引擎(Session) ──► alarm_events + 快照 + 時間軸標記
      IO(DI) ─► 同事件引擎 ──► 綁定鏡頭 Smart 錄影 / 動作鏈（DO/通知）
      通知 ─► INotifier ─► Webhook(JSON)/SMTP/SNMP
```

### 20.3 模組依賴

```
HeliVms.App ─► ViewModels/Views ─► Services(共享管道客戶端)
   │
   ├─► HeliVms.Licensing ◄=== 內嵌公鑰 → 驗證授權碼
   ├─► HeliVms.Core ────────────────► HeliVms.Storage ─► SQLite
   ├─► HeliVms.Devices (ONVIF/RTSP)
   ├─► HeliVms.Shared (Serilog/聚合)
HeliVms.Recording.Service ─► Media + Devices + Recording + Alarms + Licensing
HeliVms.Decoder  ⇄ 主進程（命名管道，JSON 協定，獨立崩潰域）
```

### 20.4 資料/事件流總結（對應章節速查）

| 流向 | 路徑 | 章節 |
|---|---|---|
| 拉流→錄影 | Media.Pull → Muxer → recordings\ | §3.2 |
| 監看低延遲 | 解碼→抽幀→共享記憶體→格面 | §3.3, §17 |
| 時間軸回放 | Storage 索引 → Decoder → 渲染 | §3.4, §8.5 |
| 事件/警報 | L0/L1/IO → 引擎 → 資料庫+通知 | §5, §16, §18.5 |
| 遠程存取 | WebApi → WebRTC/HLS | §18.4 |
| 授權 | LicenseProducer → 授權碼 → Licensing | §19 |
| 設備離線 | 網路監控 → 事件 → 地圖紅點+通知 | §16.1, §18.8 |

---

## 21. 總檢討：欠缺與可避免之錯誤

> 對整份藍圖的事後檢討（post-mortem），聚焦**「會實際踩到的坑」**，不以功能多寡為焦點。

### 21.1 工程流程治理缺口（現況完全沒有，最優先補）

| 缺口 | 現況 | 立即動作 |
|---|---|---|
| 版本控管 | `D:\HeliVms` 非 git repo | `git init` + `.gitignore`（排除 `bin/` `obj/` `*.log`） |
| CI | 無 | GitHub Actions：每次 push 跑 build + 單元測試 |
| 自動化測試 | 全部只有手動驗收清單 | 三層：單元（授權/排程/配額/`Storage` repository）＋整合（Media 管道、SQLite、WAL）＋ E2E（真實 RTSP 分流） |
| 性能基準 | 有計算無工具 | benchmark 儀表：32 路 CPU/記憶體/磁碟 IOPS 基線；每里程碑量測一次 |
| NuGet 固定 | 未鎖 | `packages.lock.json`＋版本固定；**Sdcb.FFmpeg / Mpv.NET 綁定版本與本機 FFmpeg 8.0.1 對照表** |
| 發布流程 | 無 | semver＋CHANGELOG＋簽章安裝包＋升級路徑測試 |

### 21.2 現在就要避免的工程錯誤（技術陷阱）

| # | 陷阱 | 典型失敗 | 預防（定案） |
|---|---|---|---|
| 1 | 硬體解碼超過 GPU 同時通道上限 | 32 路同解 → NVDEC session 不足、掉幀崩潰 | 溢位自動降級到軟解；**錄影本就走 `-c copy` 不需解碼**，保障錄影優先於監看 |
| 2 | 32 路同時隨機寫磁碟 | 碎片化、IOPS 飽和 | 順序寫（區段連續）、分組提交、僅索引上 SSD、資料目錄與系統碟分離 |
| 3 | SQLite 多執行緒寫鎖 | `locked` 錯誤、阻塞拉流執行緒 | **WAL 模式**＋單一寫入佇列＋ `busy_timeout`；讀寫分離 | 
| 4 | 牆鐘跳動／夏令時 | 區段檔名日期衝突、時間軸錯位 | **全程 UTC 基準**，顯示層才轉本地；NTP 偏差 >5s 記重大事件，不回溯覆寫，下區段續接 |
| 5 | 時區變更 | 資料路徑錯位、歷史搜尋失敗 | 存檔路徑一律 UTC-date（或序號），與顯示時區完全解耦 |
| 6 | 服務 session 0 無法開窗 | 無登入時錄影掛掉無人知 | 已進程分離；驗收：登出狀態下服務照常錄影＋看門狗拉起 |
| 7 | Program Files 防寫 | 更新/錄影寫入失敗 | 資料一律 `C:\HeliVmsData\`（§2.1），設定檔也放資料夾，安裝路徑唯讀 |
| 8 | credential 洩漏 | Tools 預設密碼明文、HMAC secret 硬編碼 | §19 已處置；全面盤點剩餘 credential（DB、SDK、SMTP）集中 `.secrets` 加密保存 |
| 9 | 索引無限成長 | 億級 rows 查詢退化 | retention 配額一併壓縮；定期 `VACUUM`；`EXPLAIN QUERY PLAN` 驗證熱查詢走索引 |
| 10 | `-c copy` 與設備 mux 差異 | 部分品牌 timebase/extradata 異常 → 壞檔 | 品牌驗證矩陣：每品牌 `ffprobe` 檢驗輸出；異常設備走**轉碼 fallback**（§11.2） |
| 11 | fMP4 跨段 seek 撕裂 | 回放邊界頓挫 | 關鍵幀索引＋邊界前補 1 GOP overlay（§3.4 已設計，實作必驗） |
| 12 | native 綁定版本漂移 | FFmpeg 庫 ABI 不符 → 啟動即 crash | 鎖定綁定版本＋啟動自檢庫版本＋此版本納入 CI 矩陣 |

### 21.3 營運與產品缺口

- **監控自身的監控者**：錄影服務死亡的通知來自信任鏈最底層 → 心跳檔（看門狗每 30s 寫）+ 服務異常 SMTP/Webhook
- **日誌衛生**：Serilog RollingFile＋保留天數；禁止影像路徑/密碼/授權碼入 log（隱私＋安全）
- **備份還原演練**：設定＋授權＋索引之備份要求「可還原」；定義 RPO/RTO（例：索引 24h、設定即時），每版手動演練一次
- **真實硬體驗證**：M2 驗收需 ≥1 台 ONVIF（Axis/相容）＋ 1 台品牌（海康／大華），避免「只對模擬流」過關
- **測試訊號來源**：本機 FFmpeg 8.0.1 自造 RTSP 測試流（testsrc/sinelive）做無設備 CI 與基準
- **公鑰輪替**：授權公鑰需可於升級時替換（新版內嵌新公鑰），避免舊版外洩後無法補救
- **多顯示器 DPI 差異**：佈局座標/浮窗在 mixed-DPI 環境驗證（§8.3）
- **介質/檔案損壞自我檢查**：定期抽查錄影片段 `ffprobe`（已在 §15.7，落地為排程任務）

### 21.4 現有資產衛生（Repo 檢討，開工即做）

- `D:\HeliVms` 尚未 git init → 先初始化，再做首次 commit（含 docs/＋Tools/）
- `.gitignore`：`**/bin/` `**/obj/` `*.log` `password.dat` `private.key` `*.tmp`
- 清理工具殘留：`LicenseKeyGen` 的 `error*.log` `output*.log`；標記停用（§19.5）
- 命名統一：Tools 使用 `HeliVMS.*`、藍圖使用 `HeliVms.*` → 新專案一律 `HeliVms.*`；舊工具遷移時改名
- 加入 `.editorconfig`＋.NET 內建 analyzers，CI 將警告視為錯誤，範式（nullable、ImplicitUsings）在 `Directory.Build.props` 全 repo 統一

### 21.5 開工前檢查清單（對應 §13）

- [ ] `git init` ＋ `.gitignore` ＋ `Directory.Build.props`（統一 TF/net10.0-windows、nullable、Analyzer）
- [ ] 解決方案結構（§2：App/Core/Media/Decoder/Recording/Alarms/Devices/Storage/Licensing/Shared）
- [ ] CI YAML：build＋單元測試跑通（GitHub Actions）
- [ ] 測試串流：本機 FFmpeg 建虛擬 RTSP（testsrc＋音訊）雙流
- [ ] 授權骨架先行（§19），鎖定公鑰嵌入機制
- [ ] 硬體借用清單：1 台 ONVIF ＋ 1 台海康/大華（M2 驗收前備妥）

> 本節所列不新增功能，皆為「把錯誤在發生前擋掉」的工程治理，併入 M1–M4 執行。