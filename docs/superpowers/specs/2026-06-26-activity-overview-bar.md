# Activity Overview Bar — Nx 風格活動熱度概覽列

## 目標
在 NxTimeline 上方新增一條 24px 活動熱度概覽列，以分色混合方式直觀顯示各時段的錄影活動密度，使 HeliVMS 回放體驗更接近 Nx Witness。

## 設計

### 位置
NxTimeline 內部，CameraRowsCanvas 正上方。新增一列 RowDefinition 放置 ActivityCanvas。

### 視覺行為
- 高度 24px，橫跨完整時間範圍
- 將可見時間範圍分割為像素寬度的 bucket（約 `ActualWidth` 個 bucket）
- 每個 bucket 掃描 `_cameraRecordings` 中與該時間區間重疊的區段：
  - 依 `RecordType` 分別計數（Continuous / Motion / Alarm / AI）
  - 顏色優先級（取最高優先級且 count > 0 者）：Alarm → Motion → AI → Continuous
  - 透明度 = min(0.15 + count * 0.20, 0.85)（1 段約 35%、2 段約 55%、3 段以上約 75-85%）
  - 無活動 = 完全透明
- 底部 1px 分隔線（`BorderBrush` Opacity 0.3）與 CameraRows 區隔

### 互動
| 操作 | 行為 |
|------|------|
| 左鍵點擊 | Seek 至該時間點 |
| 右鍵拖曳 | 選取時間範圍（共享 CameraRowsCanvas 的選取邏輯） |
| 懸停 | Tooltip：`"HH:mm:ss — X segments (Continuous Y / Motion Z / Alarm W)"` |

### 資料來源
`_cameraRecordings`（`List<CameraRecordings>`）— NxTimeline 既有欄位，無需新增後端查詢。

### 重新繪製時機
- `LoadSegments()` 完成時
- `DrawCameraRows()` / `DrawTimeScale()` 等渲染管線執行時
- 縮放或平移變更時

## 實作範圍

### NxTimeline.xaml
- 將 `CameraRowsCanvas` 所在的 Border 移入新的 Grid.Row="1"
- 在 Grid.Row="0" 新增 `Canvas x:Name="ActivityCanvas"`（Height="24"，ClipToBounds="True"）
- 加入 ActivityCanvas 的滑鼠事件處理（LeftButtonDown、RightButtonDown/Up、MouseMove）

### NxTimeline.xaml.cs
- 新增 `DrawActivityOverview()` private method
- 在 `DrawCameraRows()` 完成後或 `InvalidateVisual()` 路徑中調用
- 演算法：
  1. 計算 `_visibleStart` 到 `_visibleEnd` 的總秒數
  2. `double pixelsPerSecond = ActivityCanvas.ActualWidth / totalSeconds`
  3. 迴圈每個像素 column：
     - `bucketStart = visibleStart + (x / actualWidth) * duration`
     - `bucketEnd = visibleStart + ((x+1) / actualWidth) * duration`
     - 遍歷 `_cameraRecordings` 計算各類型重疊數
     - 決定顏色 → 建立 Rectangle 加入 ActivityCanvas.Children
  4. 清除舊的 Children（ActivityCanvas.Children.Clear()）再重新繪製
- 掛載滑鼠事件：`ActivityCanvas_MouseLeftButtonDown`（seek）、`ActivityCanvas_MouseMove`（tooltip）

### PlaybackView.xaml
- NxTimeline Row 2 的 Height 從 "Auto" 或固定值調整，配合新增的 24px（若 NxTimeline 自身 Height 已有彈性則無需更動）

## 不納入範圍
- Thumbnail 預覽懸停 — 留待後續階段
- 速度滑桿取代 ComboBox — 留待後續階段
- 傳輸控制列 redesign — 留待後續階段
