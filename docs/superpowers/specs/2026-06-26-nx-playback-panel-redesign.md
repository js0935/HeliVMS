# Nx Witness 風格播放面板改造

> **目標：** 將 HeliVMS 播放面板（PlaybackView Row 2+3）設計更接近 Nx Witness 的簡潔傳輸控制 + 連續速度滑桿 + 新按鈕（LIVE/SYNC/CLND/THMB）

---

## 1. Transport Controls Row 重新佈局

### 現狀（18 欄，將移除）
```
JumpStart | StepBack | [Play][Pause] | StepFwd | JumpEnd | PrevSeg | NextSeg | Stop | || 速度: Combo | LoopA/LoopB/⤾/✕ | 時間顯示 | StopAll | 書籤+列表 | Export | FPS
```

### 目標（12 欄，簡潔分組）

```
Group1      Group2      Group3    | Group4              | Group5  || Group6                     | Group7
[PlayPause] [Step◀][▶] [Seg◄][►] | [Speed ▬▬●▬▬ 1x ▬▬] | [HH:mm] || [LIVE] [SYNC] [CLND] [THMB] | [Bookmark] [StopAll] [FPS]
```

### 移除項目與替代

| 移除 | 替代 |
|---|---|
| JumpStartBtn | `Ctrl+←` 快捷鍵 |
| JumpEndBtn | `Ctrl+→` 快捷鍵 |
| StopBtn | `StopAllBtn` 保留（縮小），個別停止透過 Player 右鍵選單 |
| LoopPanel (A/B/⤾/✕) | `I` / `O` / `L` 快捷鍵，可從 Player 右鍵選單存取 |
| "速度" 文字標籤 | Slider 本身即暗示 |
| BookmarkListBtn | 保留 BookmarkBtn，列表透過右鍵或 SYNC 旁選單 |
| ExportClipBtn | 保留但移至 "..." 更多選單 |

---

## 2. Speed Slider — 連續對數滑桿

取代 `ComboBox`（固定 0.25x/0.5x/1x/2x/4x/8x/16x/32x）。

### 設計

```
Minimum: 0 (對應 0.25x)
Maximum: 100 (對應 32x)
Default: 50 (對應 1x)
```

- **刻度映射（對數）：** `speed = 0.25 * 2^(value / 25)`
- **顯示：** 在 Slider 上方或 ToolTip 顯示目前速度，如 `1x`
- **點擊 Slider：** 跳至該速度並播放
- **拖曳 Slider：** 連續改變速度
- **鍵盤：** `↑` 加快 / `↓` 減慢（Nx 相容）

### SpeedRates 欄位更新

```csharp
// 不再需要固定陣列，改為從 Slider.Value 即時計算
private double SliderValueToSpeed(double val) =>
    Math.Round(0.25 * Math.Pow(2, val / 25.0), 2);
```

### 全螢幕模式的速度顯示

全螢幕的 `FsSpeedBtn` 改為點擊循環（1x→2x→4x→8x→16x→1x）。

---

## 3. 新按鈕行為

### LIVE 按鈕

- **欄位：** `_liveMode` (`bool`, 預設 `false`)
- **點擊：** 設定 `_liveMode = true`，跳至目前時間（`DateTime.Now`），顯示綠色 LIVE 徽章
- **退出條件：** 使用者拖曳時間軸或點擊時間軸 → `_liveMode = false`
- **再次 LIVE：** 回到目前時間
- **UI：** 按鈕背景變綠色（`SuccessBrush`）、文字 `LIVE`、左側 3px 綠條
- **快捷鍵：** `L`

### SYNC 按鈕

- **欄位：** `_syncEnabled` (`bool`, 預設 `true`)
- **點擊：** 切換 `_syncEnabled`
- **ON：** 所有頻道跟隨主頻道（現有行為，`_coordinator` 正常運作）
- **OFF：** 分離非主頻道的同步（每個 Player 獨立 seek/play/pause）
- **UI：** ON 時背景藍色（`RecordingContinuousBrush`）＋文字 `SYNC`；OFF 時變灰
- **快捷鍵：** `Ctrl+S`

### CLND 按鈕

- **行為：** 點擊彈出 `Popup` + `Calendar` 控制項
- **選取日期：** 關閉 Popup，更新 `FilterDatePicker.SelectedDate`，觸發 `FilterDatePicker_SelectedDateChanged`
- **UI：** 日曆背景 `SurfaceBrush`，選取日期高亮
- **快捷鍵：** `Ctrl+C`

### THMB 按鈕

- **欄位：** `Timeline._showThumbnails` (`bool`, 預設 `false`)
- **點擊：** 切換 NxTimeline 的縮圖列顯示
- **縮圖列：** 在 ActivityCanvas 上方新增 48px 高的 `ThumbnailCanvas`（Grid.Row 移到 Row 0，原 ActivityCanvas→Row 1，CameraRows→Row 2，TimeScale→Row 3）
- **初始實作：** 先畫佔位矩形＋時間標籤；縮圖生成為後續增量
- **UI：** ON 時按鈕高亮
- **快捷鍵：** `T`

---

## 4. NxTimeline 內部調整（因應 THMB）

### Grid 結構變更（從 3 列→4 列）

```xml
<Grid.RowDefinitions>
    <RowDefinition Height="Auto"/>   <!-- NEW: ThumbnailStrip (48px if visible, 0 otherwise) -->
    <RowDefinition Height="24"/>     <!-- ActivityCanvas -->
    <RowDefinition Height="*"/>      <!-- CameraRowsCanvas -->
    <RowDefinition Height="16"/>     <!-- TimeScaleCanvas -->
</Grid.RowDefinitions>
```

- `ThumbnailCanvas` 新增在 Row 0，初始 `Visibility="Collapsed"`
- THMB 按鈕切換 `ThumbnailCanvas.Visibility`
- 所有 `Grid.Row="N"` 與 `Grid.RowSpan` 更新（+1 offset）
- SelectionCanvas/PositionCanvas/BookmarksCanvas → `Grid.RowSpan="4"`

---

## 5. 全螢幕模式同步改造

### Row 2 全螢幕控制列調整

```
Existing:  JumpStart | StepBack | Play | Pause | StepFwd | JumpEnd | Speed(1x) | Clock
New:       StepBack | Play/Pause | StepFwd | SegPrev | SegNext | Speed(cycle) | Clock
```

- **Play/Pause 合併**：單一按鈕（同 transport bar）
- **移除 JumpStart/JumpEnd**：快捷鍵保留
- **新增 Segment Prev/Next**：全螢幕也可跳錄影區段
- **Speed 循環**：點擊循環 1x→2x→4x→8x→16x→1x

---

## 6. 資料流

```
PlaybackView (View)
  │
  ├── Transport Controls Row (Row 3)
  │     ├── PlayPauseBtn_Click()  → _coordinator.Play()/Pause()
  │     ├── StepBackBtn_Click()   → _coordinator.StepFrame(-1)
  │     ├── StepFwdBtn_Click()    → _coordinator.StepFrame(+1)
  │     ├── SegPrevBtn_Click()    → JumpToSegment(-1)
  │     ├── SegNextBtn_Click()    → JumpToSegment(+1)
  │     ├── SpeedSlider           → _coordinator.SetPlaybackRate(speed)
  │     ├── LiveBtn_Click()       → ToggleLiveMode()
  │     ├── SyncBtn_Click()       → ToggleSync()
  │     ├── ClndBtn_Click()       → ShowCalendarPopup()
  │     └── ThmbBtn_Click()       → Timeline.ToggleThumbnails()
  │
  ├── NxTimeline (Row 2)
  │     ├── ThumbnailCanvas (NEW)
  │     ├── ActivityCanvas
  │     ├── CameraRowsCanvas
  │     ├── TimeScaleCanvas
  │     ├── SelectionCanvas
  │     ├── PositionCanvas
  │     └── BookmarksCanvas
  │
  └── FullScreenOverlay (Row 0-3)
        └── Transport buttons (simplified matching Row 3)
```

---

## 7. 需要新增/修改的檔案

| 檔案 | 變更 |
|---|---|
| `Views/PlaybackView.xaml` | Row 3 傳輸列全面重構；Row 0-3 FullScreenOverlay 調整；時鐘/狀態樣式微調 |
| `Views/PlaybackView.xaml.cs` | 新增 `_liveMode`, `_syncEnabled`；LiveBtn_Click, SyncBtn_Click, ClndBtn_Click, ThmbBtn_Click；SpeedSlider 事件；移除/reduce 已刪按鈕的程式碼；ToggleLiveMode, ToggleSync 方法 |
| `Controls/NxTimeline.xaml` | 新增 Row 0 ThumbnailCanvas；調整所有 RowIndex 與 RowSpan |
| `Controls/NxTimeline.xaml.cs` | 新增 `_showThumbnails`；`ToggleThumbnails()`；`DrawThumbnails()` 骨架 |
| `Controls/NxTimeline.xaml` + `.cs` | 移除舊的 GoLiveBtn（移至 Row 3 LIVE 按鈕） |

---

## 8. 非目標（明確不包含）

- **Rewind（倒轉）**：Flyleaf 不支援負值速度，此版本不實現；Slider 只處理正向速度
- **實際縮圖生成**：THMB 首版只繪製佔位矩形，縮圖生成排入後續
- **音量控制**：Nx 有 Volume Control，但 HeliVMS 為 VMS 系統，多路同時播放時無意義
- **LayoutBtn 移除**：保留在 Toolbar 中

---

## 9. 邊界情況

| 情況 | 行為 |
|---|---|
| 點擊 LIVE 時無任何頻道播放 | 顯示短暫 Toast 或無作用 |
| SYNC OFF + 點擊 Play | 只播放主頻道（若有設定主頻道） |
| SYNC OFF + 點擊 Pause | 只暫停主頻道 |
| CLND 選取無錄影的日期 | 正常載入，時間軸顯示空白（NoData） |
| THMB 切換時無錄影資料 | 縮圖列顯示空白 |
| Speed Slider 快速拖曳 | 每個 Slider.ValueChanged 觸發 `SetPlaybackRate`，Flyleaf 內部會節流 |

---

## 10. 現有功能保留檢查清單

- [x] 日期選取與載入（FilterDatePicker / CLND）
- [x] 搜尋面板（Row 1 左側）
- [x] 頻道選擇與勾選
- [x] 錄影類型濾波（連續/動態/警報/AI Toggle）
- [x] 時間軸縮放（ZoomIn/ZoomOut/Reset）
- [x] 時間軸活動概覽列（ActivityCanvas）
- [x] 時間軸選取（右鍵拖曳 + ZoomToSel）
- [x] 書籤（BookmarkBtn + BookmarkList）
- [x] 匯出（ExportClip + BatchExport）
- [x] 效能監控面板（PerfPanel）
- [x] 快捷鍵一覽（ShortcutsOverlay）
