# 回放架構對照：QCV8001（`D:\SDK\REA`）vs HeliVMS

> 使用者指示「將 `d:\SDK` 的錄影回放架構核心重新編寫到 HeliVMS」。
> 依 `AGENTS.md` 的界線：**不可複製 QCV8001 的程式碼或資源**，只能依反編譯出的
> **事實**重寫。所以這份文件先當施工圖：REA 的架構長什麼樣、我們已經有什麼、還缺什麼、
> 依序做哪一項。每一項都獨立提交、獨立守門。

## 1. REA 的回放架構（事實，含出處）

### 1.1 四層分工

| 層 | 模組 | 事實 | 出處 |
|---|---|---|---|
| UI 外殼 | `QC_Playback.exe` | MFC 外殼；`PlayerPanel`／`PlayerButton`／`PlayerScrollBar`；**引擎全部外包**；用授權決定本機或遠端模式（`isLocalPlayer`）；**記住上次佈局**（`GetPlaybackMonitorIdx`） | `QC_Playback\README.md` |
| 回放引擎 | `QC_Player.dll` | `CQC_MultiPlayer`／`QC_MultiPlayer`（多通道同步）、`CQC_Player`／`CQC_UnitPlayer`（單通道）、`CQC_PlayerStep`（逐格）；**速度控制含「倒」**（「快/慢/倒/逐格」並列）；匯出（`CQC_PlayerExport`／`QC_VideoClip`／`QC_UnitBackup`）；視覺/區域搜索引擎；**檔案存取與播放快取樣板**（`Access_Template`／`PlayerCache_Template`） | `QC_Player\README.md` §1–2 |
| 檢索 UI | `NVRPlayback.dll` | 16 個匯出即功能面：日曆、書籤、**本機/遠端來源選擇**、剪輯、匯出、視覺搜索、框選（`SetRegion`＋相對座標）、顯示控制 | `NVRPlayback\README.md` §1 |
| 遠端引擎 | `QC_RM_Player.dll` | `RemoteMultiPlayer`／`AccessRemote`／`RemoteCache`：`OpenFile`→`Read`→EOF 的遠端檔案協議＋快取，**接上與本機相同的播放引擎**；遠端備份、AHD 主機回放 | `QC_RM_Player\README.md` §1–2 |

### 1.2 值得學的三個架構決策

1. **引擎與來源解耦**：本機檔與遠端流共用同一顆播放引擎，差別只在存取層（`Access_Template`）
   與快取（`PlayerCache_Template`）。UI 因此不需要知道畫面是從哪裡來的。
2. **播放能力是一組**：快、慢、**倒**、逐格、跳段並列——倒轉不是附加功能，而是同一組控制。
3. **檢索與播放分離**：日曆、書籤、剪輯、搜索掛在 UI 層（`IntellSearch` 以 `SetClass` 接到播放器），
   引擎只負責播出影格。

## 2. HeliVMS 現況對照

| # | 面向 | REA | HeliVMS | 判定 |
|---|---|---|---|---|
| 1 | 多通道同步 | `QC_MultiPlayer` | `PlaybackWindow`＋`PlaybackSyncPlanner`（母鐘、0.4s 容差、啟動延遲補償、64 路總覽 30s 容差） | **更強**（有量測與否證紀錄） |
| 2 | 單通道 | `CQC_UnitPlayer` | `PlaybackSession` | 等價 |
| 3 | 逐格 | `CQC_PlayerStep` | `StepFrame`＋`PlaybackFrameTiming`（取檔案相鄰兩格，不是名目 fps） | **更強** |
| 4 | 速度（快/慢） | 快/慢/倒/逐格 | 0.5×–16× | 等價 |
| 5 | **速度（倒）** | **有** | **原本沒有** → 2026-11-11 補上（見 §3） | 已補 |
| 6 | 日曆 | `PopupCalendar` | 回放日曆（月曆藍字＋被汰除說明） | 等價 |
| 7 | 書籤 | `PopupBookmark`（timeline 上有 bookmark 標記） | 書籤（新增/改名/刪除/跳轉）＋**2026-11-11 起時間軸上有黃色刻度**（事件是白色 1px，書籤是黃色 2px） | 等價 |
| 8 | 剪輯/匯出 | `QC_VideoClip`／`CQC_PlayerExport` | `ExportService`（`-ss`/`-t` 裁切）＋匯出中心（批次、取消、收據） | **更強** |
| 9 | 視覺/區域搜索 | `CQC_VisualSearch`／`SetRegion` | 視覺搜索（感知雜湊）＋框選 ROI（`VisualHash`） | 等價 |
| 10 | 來源抽象 | 本機/遠端同一引擎＋快取 | 本機（索引＋`RecordingDiskImporter`）／遠端（HLS VOD、QCTek NEMO、中央代理）**各自成路** | 缺口（架構級，見 §4 第 3 項） |
| 11 | 播放快取 | `PlayerCache_Template` | `FrameTimestamps`（依路徑＋長度＋mtime，跨窗共用） | 等價 |
| 12 | 每頻道一列時間軸 | canvas、每頻道 28px、事件分色 | `ChannelBandsCanvas`（每格一列覆蓋＋焦點台事件刻度） | 等價（§8.5 已做） |
| 13 | 記住上次佈局 | `GetPlaybackMonitorIdx` | `playback.split`／`speed`／`auto_advance`／`loop`／`channel_id` | 等價 |

## 3. 第一項落地：倒轉播放（2026-11-11）

**為什麼先做這一項**：它是唯一「REA 明列為核心能力、我們完全沒有」的一項，而且不需要新解碼路徑。

**設計（三個決定）**：

1. **倒轉不是負速度**，是「往回跳著看」：解碼器只能從關鍵影格往後解，負速度在檔案層做不到
   （`-vf reverse` 要整段緩衝進記憶體，一段 60 秒 1080p 就是 GB 級）。所以實作是
   **每 250ms 往後退一步、顯示那一格**。
2. **落點一律走 `PlaybackSeekPlanner.Resolve(forward: false)`**（`PlaybackReversePump.Next`）：
   段內倒退、跨空隙回前一段最後一格、「退到第一段之前停在開頭」這三件事方向鍵早就定義好了；
   倒轉自己再寫一份，遲早會在空隙或接縫上給出不同答案（時間軸上明明有那段錄影，倒轉卻跳過它）。
3. **步長＝速度 × 0.25 秒**（夾在 0.25–4 秒）：1× ＝每秒倒退 1 秒影片；16× ＝每秒 16 秒。
   下限是為了不落回同一格，上限是為了不要一次跳掉整個 GOP 以上。

**顯示**：走既有的「起播＋首幀後暫停」路徑（與逐幀同一個接縫），不新增解碼路徑。

**停止條件與中止**：
- 到當日第一段開頭 → **停住**，不繞回最後一段（否則看起來像倒轉跳到晚上）。
- 任何會搶播放頭的操作（播放、停止、拖曳時間軸、點時間軸、換台/換日/重新載入、關窗）
  都呼叫 `StopReverse()`——否則節拍器會在操作員做別的事時繼續往回拉畫面，看起來像「回放自己亂跳」。

**出處**：`src/HeliVMS.Shared/PlaybackReversePump.cs`（純函式）、
`PlaybackWindow.OnReverseClicked`／`OnReverseTick`／`StopReverse`。
守門：`PlaybackReversePumpTests`（6 條，含「重複倒轉單調往回且收斂」）、
`PlaybackReverseContractTests`（11 條，含「不自己算落點」「到開頭會停」「六個使用者操作都會中止」）。

## 4. 缺口與後續順序

| # | 項目 | 為什麼 | 形狀 | 風險 | 狀態 |
|---|---|---|---|---|---|
| 1 | **倒轉播放** | REA 引擎明列為核心能力 | `PlaybackReversePump`＋`PlaybackWindow` | 低 | **已做（`62b1bb7`，CI success）** |
| 2 | 倒轉的節拍保護 | seek 比 250ms 慢時連發 `PlayFrom` 互相打斷 | `ShouldSkipBeat`／`ShouldGiveUp`（連續跳過 2 秒收工） | 低 | **已做（`f5012c3`）** |
| 3 | **書籤的時間軸刻度** | REA 的 timeline 有 bookmark 標記；我們只有清單 | `RenderChannelBands` 焦點台那排加黃色刻度（事件白 1px／書籤黃 2px） | 低 | **已做（本輪）** |
| 4 | **來源抽象**（本機/遠端同一引擎） | REA 的核心架構決策；我們現在遠端（HLS／NEMO／中央代理）與本機各自成路，新功能要接三處 | 抽 `IPlaybackSource`（列段、開流、seal），本機走索引、遠端走既有代理 | 中高（動播放核心） | 未做 |
| 5 | 遠端回放的快取（`RemoteCache` 等價物） | 遠端 HLS 每次 seek 都重抓 manifest／片段 | 既有 HLS 路徑上加 LRU 快取 | 低中 | 未做（**先量**：目前沒有現場證據說它是瓶頸） |
| 6 | 倒轉在多路同步下的行為 | 現在只有焦點台倒轉；同步牆仍以母鐘正向 | 由母鐘統一倒退（`SyncPanes` 支援負向，每步 N 條行程重啟） | 中 | 未做（**先問現場**：多路回放時倒轉的使用頻率；直接動已量測的同步核心不划算） |

> 每一項都要獨立提交、獨立守門；施工前先在本文件補「事實 → 設計 → 守門」三塊。
