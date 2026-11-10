# 多螢幕牆與視覺搜索——設計（下一輪施工圖）

> 使用者已解禁 UI（2026-11-10「UI 可以變動」），這兩項是 QC_Live 對齊的最後兩個大工程。
> 本文把設計決策先釘住，施工時不再重新發明；依據與前份文件相同（`QCV8001-BEHAVIOUR-SPEC.md`）。

---

## 1. 多螢幕牆（QC_Live：`monitor%d.png`、`{Display} set win=%d`、`GetMaxMonitorSize`）

### 1.1 現況約束（反編譯＋程式碼事實）

- `ChannelSession`／`ChannelManager` 的工作階段是「一頻道 ↔ 一格」（`session.Cell` 單值）；
  `ReconcileAsync(wall, startIndex, cellCount)` 一次只服務一面牆。
- 錄影擁有權（`RecordingOwnership`）與 `RecordingScheduler` 以頻道為單位，與格位無關——
  第二面牆顯示同一台**不會**重複錄影（錄影走主流解碼行程，與顯示格位脫鉤）。
- QC_Live 的多螢幕：每個 win 一份 0x1828B 設定＋桌布； win 內各自有分割與頻道派置。

### 1.2 設計

1. **每螢幕一個 Window**（`MonitorWallWindow`），主視窗保留現有牆（不動既有行為與測試）。
2. **格位位址改成 (wallId, cell)**：`ChannelSession.Cell` 維持不變，但牆 window 的
   `ReconcileAsync` 呼叫帶 `wallId`；同頻道出現在兩面牆＝**同一個解碼行程、兩個顯示訂閱**
   （幀事件 multicast 到兩個視窗的格位，不解碼兩次——QC_Live 的 D3D9 疊層同構）。
3. **佈局記憶**：`MonitorLayoutPrefs` 擴充為每螢幕一份（`ui.json` 加 `"screens": { "1": {...} }`，
   舊檔相容：沒有 screens 時全部沿用現有單牆值）。
4. **沉浸模式**只在焦點所在的螢幕生效；快速列／事件面板每窗一份（事件面板共用同一份清單）。
5. **錄影模式圓點／徽章**：每窗各自的 `UpdateBadges`（同一條 1 秒計時器 multicast）。

### 1.3 驗收

- 同頻道兩面牆都顯示、錄影只有一份（`RecordingScheduler` 不重複開）。
- 第二螢幕拔掉：視窗關閉、格位改派回主牆、串流不重連。
- 既有 561 條 App 測試不改（新視窗是新增，不改主視窗行為）。

### 1.4 落地現況（2026-11-11）

**已完成**：`WallMirrorWindow` 鏡像窗（第一輪 `88d32a3`）＋第二輪——右上角分割鈕
（1／2／4／9／16，`MirrorWallBudget` 純函式、上限 16 因為鏡像不花解碼只花每格一張
`WriteableBitmap` 的貼圖成本）、格數記在 `wall.mirror.split`（改動當下寫回，不等關窗）、
螢幕拔插（`SystemEvents.DisplaySettingsChanged`：所在螢幕還在就只讓 Windows 重排、消失就搬到
另一個非主螢幕、一個都不剩就關窗）、關窗同步取消主視窗勾選（✕／Esc／拔插三條路都不走 toggle
分支）、同一頻道佔兩格時兩格都會更新（原本提早 `return` 只會讓後面那格停在舊畫面）。

徽章與每格的錄影方式指示燈由**主牆推**（`MainWindow.PushMirrorBadges`，掛在 `UpdateBadges` 的
尾巴＝同一個一秒節奏）：鏡像端不碰錄影方式、不碰阻塞原因、不碰色票，只把推來的字串經
`WallMirrorLabel.Compose` 接上時刻。理由與「兩份查詢遲早漂移」同一個：兩面牆對同一台給出
不同答案時，操作員唯一能做的就是兩邊都不信。

第三輪——**獨立頻道派置**（QC_Live 的每個 win 各自派置）：右上角來源鈕循環「跟隨主牆頁簽 →
監看群組 1 → 群組 2 → …」，記在 `wall.mirror.group`。兩個必須保持的設計：
(1) **影格過濾比對頻道、不是比對格位**，轉發點因此搬到 `OnCellFrame` 且在格位過濾**之前**
——指定群組的頻道可能不在主牆格位上（`Cell = -1`），先過濾就永遠收不到影格（畫面全黑、
日誌乾淨）；(2) 因為轉發現在發生在**解碼執行緒**上，鏡像端只放「待畫影格」＋把繪製排回
UI 執行緒（`PostFrame` → `RenderCell`，每格最多一張在途），`WriteableBitmap` 只在
`RenderCell` 裡寫。格位與徽章都走同一個 `MirrorChannels()`，群組改名／刪除後由
`ReloadGroupTabs` 重送來源清單。

**尚未做**：三螢幕以上的逐窗佈局記憶（現在只支援一個鏡像窗）、每窗一份的沉浸模式
（鏡像窗沒有工具列可收，本來就不需要）。

---

## 2. 視覺/框選搜索（QC_Live：`PopupVisualSearch`、`SetRegion`、`QC_VisualSearch`）

### 2.1 現況資產

- `HeliVMS.Shared` 已有**感知雜湊**（`PerceptualHash`，含測試）。
- 事件已帶快照：`alarm_events.snapshot_path`（RouteAlarm 時寫入），快照集中於資料根 `snapshots/`。
- 框選區域：智慧分析的 ROI 基礎建設已在（`RedactionRoi`、`DetectionWriter`、AI 政策）。

### 2.2 設計（以既有資產拼裝，不引 AI 依賴）

1. **索引**：事件快照寫入時計算 64-bit 感知雜湊並存進 `alarm_events` 新欄
   `snapshot_hash INTEGER`（`EnsureAdditiveColumns` 補欄；既有列懶計算——搜尋時現算現補）。
   **雜湊的唯一管線是 `VisualHash`**（Bgra → 灰階 Rec.709 → 可選 ROI 裁切 → `PerceptualHash`），
   取樣尺寸是 `VisualHash.IndexWidth`（72）×`VisualHash.IndexHeight`（64），即 9×8 區塊每塊
   8×8 像素。這個尺寸**就是雜湊的定義**：改它等於換一套指紋，資料庫裡既有的 `snapshot_hash`
   會全部與新樣本不可比，而症狀只是「以圖搜索找出來的都不像」，沒有任何錯誤訊息——要改就得
   同時重建索引。樣本側（`VisualSearchService.TryHashImage`）與快照側（`BackfillAsync`）都走
   同一個入口，服務內不得再寫第二份灰階公式。
2. **以圖搜圖**：輸入樣本圖 → 同雜湊演算法 → 漢明距離 ≤ 門檻（預設 10/64）的事件列表，
   按距離排序；點結果 → `PlaybackSeekPlanner.ResolveAt` 跳到該事件時間（**與事件跳轉同一落點
   規則**，不寫第二份）。
3. **框選過濾**：樣本圖先讓操作員框出關注區域 → 只對框內區域算雜湊（ROI 裁切後重算），
   事件側存「全圖雜湊」＋比對時同樣裁切事件快照——兩側同一個裁切函式。
   框選的相對座標（0–1）讓不同解析度的樣本與快照框在同一個位置；候選重算要用
   `VisualHash.FromBgra(..., roi)`，不可另寫一份裁切＋縮放。
4. **UI**：事件中心新增「以圖搜索」頁（選圖／框選／結果清單），與 QC_Live 的
   `PopupVisualSearch` 掛在回放 UI 同位。

### 2.3 驗收

- 同一攝影機、同一場景的快照互搜命中（漢明距離小）；不同場景不命中。
- 框選只比對框內：同一張圖框不同區域→結果不同。
- 結果跳轉落在事件所在的錄影段（時間軸上看得見那個色塊）。

### 2.4 框選搜索的落地細節

- **框只對「框的那張圖」有意義**。事件中心記住框架時的樣本圖完整路徑（`_visualRoiSamplePath`），
  換圖或重新選圖就把框清掉；否則會拿舊圖的區域去搜新圖，看起來像「框錯了地方」。
- **顯示座標 → 影像像素**：`VisualRoiWindow` 的影像是 `Stretch="Uniform"`（有黑邊），
  所以拖曳點要先扣掉黑邊並除以縮放比才是影像像素，再交給 `VisualHash.FromPixels`（含反向拖曳、
  邊界夾取、最小邊長 `MinSelectionPixels = 8`）。框選太小回 `null` 而不是夾成 1×1——1×1 的雜湊恆為 0，
  與任何樣本的漢明距離都等於自己的位元數，於是「每次都回全部事件」。
- **候選必須重算**：索引的 `snapshot_hash` 是整張圖的指紋，與框內樣本是兩種東西。
  `VisualSearchService.SearchInRegionAsync` 走 `AlarmEventRepository.ListSnapshotCandidates`
  （時間窗＋`TRIM(snapshot_path) != ''`），把每張快照解到 `VisualHash.ScaleWidth`（640）寬，
  以**同一個相對 ROI** 呼叫 `VisualHash.FromBgra(..., roi)`；樣本側也走同一尺寸，兩側才框到同一個區域。
  候選上限 `RoiCandidateLimit = 400`、平行上限 4 條：每張都要解碼，七天內超過 400 筆時縮小時間或頻道
  範圍比讓視窗看起來像當機合理。
- **規則來源**：`VisualHash` 是唯一管線（行為測試 `VisualHashTests`）；接線由
  `VisualSearchContractTests` 守住（事件中心把 roi 傳進 `SearchAsync`、服務用候選查詢＋同一 ROI 重算）。

---

## 3. 施工順序與體量

| 項目 | 預估 | 前置 |
|---|---|---|
| 視覺搜索（2） | 1 輪（雜湊索引＋搜尋頁，無新依賴） | 無——資產齊 |
| 多螢幕牆（1） | 2 輪（先雙螢幕 MVP：第二窗＋multicast 幀；再佈局記憶與拔插） | `ChannelSession` 幀事件多播改造 |
| P0 QCC 顯示閉環 | 操作員在 QCC 視窗雙擊頻道 1，看兩邊 log | 無 |
