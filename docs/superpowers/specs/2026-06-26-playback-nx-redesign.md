# PlaybackView Nx Witness 風格改造

## 動機

PlaybackView 功能完整（4293 行 code-behind，1330 行 XAML），但佈局偏傳統：
- 搜尋面板在右側 overlay，與頻道選擇分離
- TimelineControl 為單行時間軸（雖有 NxTimeline 但未整合至 PlaybackView）
- Transport bar 過長（25 個按鈕/控制項）
- 缺少 Nx Witness 風格的左側整合面板 + 全寬多列時間軸

## 目標

將 PlaybackView 改造為 Nx Witness 風格回放頁面，保留所有現有功能。

## 新佈局

```
┌─ Toolbar ────────────────────────────────────┐
├──┬───────────────────────────────────────────┤
│  │                                           │
│ S    中央影片網格 (多路同步回放)              │
│ e                                           │
│ a  +  EmptyState (未選取時)                  │
│ r                                           │
│ c  +  LoadingOverlay                         │
│ h                                           │
│  +  Channel List                             │
│                                             │
├──┴───────────────────────────────────────────┤
│  NxTimeline（全寬，多列彩色錄影條）            │
│  1H 6H 12H 24H  GoLive                       │
├──────────────────────────────────────────────┤
│  Transport Bar（精簡單列）                    │
│  ⏮⏪▶⏸⏩⏭ | 速度[1x▼] | 時間顯示 | AB🔁 | 書籤📷導出│
└──────────────────────────────────────────────┘
Overlays:
  - 全螢幕模式 (FsTopBar + FsBottomBar + video grid)
  - 快捷鍵一覽 (ShortcutsOverlay)
  - 效能監控面板 (PerfPanel) — 右上角
```

## 組件詳情

### 1. 頂部工具列 (ToolbarRow)
**保留現有功能**，佈局微調：
- 左側：標題「錄影回放」+ 日期選擇（DatePicker + ◀▶ 按鈕 + 時段 ComboBox）
- 右側：即時時鐘 + SyncIndicator + PlayingCountBadge + 拍照/版面/備份/重新整理
- 移除：搜尋按鈕（移到左側面板）、SnapshotBtn2（合併到 transport）

### 2. 左側面板 (Search + Channel, 280px)
**搜尋區段（上半）：**
- 快速時間預設：本日 / 1H / 6H / 24H / 本週（保留現有按鈕）
- 時間範圍輸入：從/至 日期 + 時/分/秒 TextBox（保留現有）
- 類型 ComboBox：全部/連續/動態/警報
- 頻道 TextBox + 僅動態 CheckBox
- 搜尋 / 清除按鈕 + 關閉（X）按鈕
- 搜尋狀態文字

**頻道區段（下半）：**
- 標題「頻道選擇」+ 計數 badge + 隱藏按鈕（▶/◀）
- 全選 / 清除按鈕
- 頻道搜尋框（TextChanged→過濾）
- 頻道列表：ScrollViewer + StackPanel（保留現有 ChannelListPanel）
- 底部狀態列：頻道數 + 重新整理按鈕

**兩區段間**以分隔線 Border 區隔。

### 3. 中央影片網格
**維持現有實作不變：**
- PlaybackGrid（Grid 動態佈局，播放器嵌入）
- EmptyPrompt 空狀態
- VideoLoadingOverlay
- ExpandChannelBtn（左側面板折疊時顯示 ▶ 按鈕）

### 4. NxTimeline（全寬底部）
**取代 TimelineControl，NxTimeline 已具備：**
- 多列彩色錄影條（每台選取攝影機一列）
  - Continuous = 藍 (#2196F3)
  - Motion = 橙 (#FF5722)
  - Alarm = 紅 (#F44336)
  - AI = 黃 (#FFC107)
- TimeScale 時間尺規（底部 16px）
- 播放位置線（PositionCanvas）
- 選取範圍括號（SelectionCanvas）
- 書籤標記（BookmarksCanvas，可拖曳）
- 右鍵選單：放大至選區 / 清除選取 / 匯出 / 加入書籤
- 縮放：1H / 6H / 12H / 24H（ZoomLevels 按鈕）
- GoLive 按鈕
- ZoomLabel 顯示目前範圍

**整合至 PlaybackView：**
- Loaded 時：從 IRecordingService 取得錄影區段 → NxTimeline.LoadSegments()
- 頻道選取變更時：更新 NxTimeline cameraIds + 重新載入 segments
- 播放位置更新：PositionSeconds 同步
- SeekRequested / SelectionChanged → 觸發現有 PlaybackView 事件
- 移除 BookmarkPanel（右側彈出），NxTimeline 內建書籤渲染
- 移除 FpsBadge（移到工具列右側或 PerfPanel）

### 5. Transport Bar（精簡單列）
**從 25 個欄位縮減為：**

| 位置 | 按鈕 | 功能 |
|------|------|------|
| 0 | ⏮ JumpStartBtn | 跳到開頭 (Ctrl+←) |
| 1 | ⏪ StepBackBtn | 上一幀 (←) |
| 2-3 | ▶⏸ PlayBtn/PauseBtn | 播放/暫停 (Space) |
| 4 | ⏩ StepFwdBtn | 下一幀 (→) |
| 5 | ⏭ JumpEndBtn | 跳到結尾 (Ctrl+→) |
| 6-7 | ⏪區段 ⏩區段 | 前/後錄影區段跳躍 |
| 8 | ⏹ StopBtn | 停止 |
| 9-10 | 速度 [1x ▼] | SpeedCombo |
| 11-14 | A B 🔁 ✕ | LoopSetA/B / Toggle / Clear |
| 15 | 時間顯示 | PlaybackTimeText (Consolas) |
| 16 | 🔖 BookmarkBtn | 書籤 (B) |
| 17 | 📷 SnapshotBtn | 拍照 |
| 18 | 導出 ExportClipBtn | 匯出選取範圍 |

**移除：** 全部停止、SnapshotBtn2（重複）、BookmarkListBtn（NxTimeline 處理）、FpsBadge

### 6. 全螢幕模式
**保留現有實作：**
- FullScreenOverlay（RowSpan=6）
- FsTopBar：時間顯示 + 書籤/拍照/離開
- FsBottomBar：迷你 transport（⏮⏪▶⏩⏭ + 速度 + 時鐘）

### 7. 其他 Overlays
- **ShortcutsOverlay**：保留不變
- **PerfPanel**：保留右上角（現有位置和功能）
- **SearchBackdrop**：移除（搜尋已整合至左側面板，不再需要 overlay）

## 資料流

### PlaybackView ↔ NxTimeline

| 方向 | 方法/事件 | 說明 |
|------|-----------|------|
| → | LoadSegments(cameraIds, segments) | 初始載入 |
| → | SetPosition(time) | 播放位置同步 |
| ← | PositionChanged(sender, seconds) | 時間軸點擊→seek |
| ← | SelectionChanged((start,end)?) | 匯出範圍選取 |
| ← | ExportRangeRequested(start,end) | 右鍵匯出 |
| ← | GoLiveRequested | 跳回目前時間 |

### PlaybackView ↔ IRecordingService
- GetRecordings(cameraIds, date) → segments
- SearchRecordings(...) → search results

## 移除項目
- SearchPanel (右側 overlay) → 功能移至左側面板
- SearchBackdrop → 不再需要
- BookmarkPanel (書籤列表) → NxTimeline 內建
- TimelineControl → NxTimeline 取代
- 部分重複按鈕：SnapshotBtn2, BookmarkListBtn, FpsBadge, StopAllBtn

## 保留現有
- 所有 code-behind 業務邏輯（播放器管理、錄影查詢、書籤、匯出、鍵盤快捷鍵）
- FullScreenOverlay
- ShortcutsOverlay
- PerfPanel
- Button Click event handlers 名稱（例如 PlayAllBtn_Click、LoadTimeRangeBtn_Click 等）

## 實作步驟

1. **重寫 PlaybackView.xaml**：新 Grid 佈局（left sidebar + center + bottom timeline + transport）
2. **更新 PlaybackView.xaml.cs**：更新 UI 元素參照（移除舊的、加入新的）
3. **移除/清理**：被取代的 UI 元素（SearchPanel, BookmarkPanel, TimelineControl）
4. **整合 NxTimeline**：在 PlaybackView Loaded 時初始化、載入 segments
5. **測試編譯**：dotnet build 確保 0 errors 0 warnings
6. **驗證功能**：日期選擇、頻道選取、搜尋、播放、seek、export、bookmark

## 注意事項

- NxTimeline 已存在於 Controls/ 中，不需修改其核心邏輯
- 新 XAML 必須使用 DynamicResource 而非硬編碼色碼
- 圖示使用現有 PathGeometry（IconPlay/Pause/SkipBack/SkipForward 等）
- 保留 ContextMenu，不修改 NxTimeline 的右鍵選單功能
