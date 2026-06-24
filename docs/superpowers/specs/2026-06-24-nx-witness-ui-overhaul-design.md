# Nx Witness UI Overhaul — HeliVMS 介面深度改造

- **日期:** 2026-06-24
- **作者:** HeliVMS (全自主架構師)
- **狀態:** 設計核定，待實作

---

## 策略

**漸進置換（B）** — 分 4 個獨立 Phase，每 Phase 完成後系統仍可正常運作，逐步逼近 Nx Witness 的 UI 品質。

---

## Phase 1 — Timeline 全面翻新

### 目標
將目前 LiveView 內嵌的固定 24h 時間軸（`RecordingBars` Canvas + `GlobalTimeline` Slider）取代為獨立的 `NxTimeline` 控制項，支援縮放、錄影類型色條、選區放大等 Nx 等級功能。

### 新元件

**`Controls/NxTimeline.xaml`** + **`Controls/NxTimeline.xaml.cs`**

```
NxTimeline (UserControl)
├── TimeScaleBar (刻度尺) — Canvas 自訂繪製
│   └── 動態刻度間距（依縮放層級自動調整：2h/1h/30min/15min/5min）
├── CameraRows (攝影機錄影條) — 多層 Canvas
│   ├── 每台目前格線攝影機獨立一列
│   └── 色條顏色依 RecordType 映射（見下表）
├── AllCamerasRow (合併列) — 底部合併條，顯示所有攝影機錄影總和
├── SelectionOverlay (選區疊層) — 右鍵拖曳標記選取範圍（藍色半透明）
│   └── 右鍵選單 → 「放大至選區」 / 「清除選取」
├── PositionSlider (位置指示器) — 垂直線 + 頂端拖曳手柄
└── ZoomControls (縮放控制)
    ├── ＋ (縮小範圍)、－ (放大範圍) 按鈕
    └── 滑鼠滾輪處理 (PreviewMouseWheel)
```

### 色條顏色映射

| RecordType | 顏色 | 用途 |
|-----------|------|------|
| 0 (連續) | `#2196F3` 藍 | 連續錄影 |
| 1 (位移) | `#FF5722` 橙紅 | 位移偵觸發錄影 |
| 2 (警報) | `#F44336` 紅 | 警報/IO 觸發錄影 |
| 3 (AI) | `#FFC107` 黃 | AI 分析事件錄影 |
| 無資料 | 透明/底黑 | 無錄影片段 |

### 縮放層級

| 層級 | 時間範圍 | 刻度間距 | 像素/小時 |
|------|----------|----------|----------|
| 0 | 24h | 2h | ~40px/h |
| 1 | 12h | 1h | ~80px/h |
| 2 | 6h | 30min | ~160px/h |
| 3 | 3h | 15min | ~320px/h |
| 4 | 1h | 5min | ~960px/h |

### 互動行為

| 操作 | 行為 |
|------|------|
| 滑鼠滾輪 | 以滑鼠位置為中心縮放時間範圍 |
| 右鍵水平拖曳 | 標記選取區段（半透明藍色疊層） |
| 右鍵選單 → 放大至選區 | 將視野切換至選取範圍 |
| 雙擊刻度尺 | 回到 24h 全覽（層級 0） |
| 拖曳 PositionSlider | 更新播放位置（觸發 `PositionChanged` event） |
| 懸停色條 | ToolTip: `攝影機名稱 / HH:mm–HH:mm / 錄影類型` |

### 資料流

```
LiveView.RefreshRecordingBars()
  → VideoIndex.QuerySegmentsByCamerasAsync(cameraIds, dayStart, dayEnd)
  → List<VideoSegment> (含 RecordType / CameraId / StartTime / EndTime)
  → NxTimeline.LoadSegments(cameraIds, segments)
  → 內部分組依 CameraId → 繪製各列色條
```

### 與現有系統整合

| 現有元件 | 取代為 |
|----------|--------|
| `RecordingBars` (Canvas) | 移除，由 NxTimeline 內部 CameraRows Canvas 取代 |
| `GlobalTimeline` (Slider) | 移除，由 NxTimeline.PositionSlider 取代 |
| `GlobalTimeline_ValueChanged` | 改為訂閱 `NxTimeline.PositionChanged` event |
| `TimelineTimeLabel` / `TimelineDateLabel` | 整合進 NxTimeline 內部 |
| `RefreshRecordingBars()` | 改為呼叫 `NxTimeline.LoadSegments()` |
| `PreviewMouseDown/Up` on GlobalTimeline | 由 NxTimeline 內部處理 `_isDraggingTimeline` |

保留不變：
- `_timelineDay`（目前觀看日期）
- `_playbackMode`（Live / CustomSeek）
- `SwitchToPlayback()` / `PerformSeek()` / `SwitchToLive()`
- `_fwdSpeedIndex` / `_playbackSpeed`

### NxTimeline API

```csharp
public class NxTimeline : UserControl {
    // 屬性
    public DateTime TimelineDay { get; set; }
    public double ZoomLevel { get; set; } // 0-4
    public double PositionSeconds { get; set; } // 目前播放位置 (0-86400)

    // 事件
    public event EventHandler<double>? PositionChanged; // 位置變更
    public event EventHandler<(DateTime start, DateTime end)?>? SelectionChanged; // 選區變更

    // 方法
    public void LoadSegments(IEnumerable<string> cameraIds, List<VideoSegment> segments);
    public void SetPosition(DateTime time);
    public void Refresh();
    public void ZoomIn(); // 程式化縮小範圍
    public void ZoomOut(); // 程式化放大範圍
    public (DateTime start, DateTime end)? GetSelection(); // 取得選取區段
    public void ClearSelection();
    public void ZoomToSelection(); // 放大至選取區段
}
```

### 實作注意事項

- 所有繪製使用 WPF `Canvas` + `System.Windows.Shapes.Rectangle`，**不使用 WriteableBitmap** 以保持硬體加速
- CameraRows 使用 `VirtualizingPanel` 概念—僅繪製可見範圍內的攝影機列
- 每個 Rectangle 的寬度 = `(totalSecondsInView / pixelsPerSecond)`，最小 1px
- 效能：1000+ 片段/24h 時維持 60fps；3000+ 片段時可接受 30fps
- 滑鼠滾輪使用 `PreviewMouseWheel` 事件保持響應

---

## Phase 2 — Layout 多分頁系統

### 目標
將單一 DynamicCameraGrid 改為多分頁 Layout 系統，每個分頁獨立儲存攝影機排列、格線設定。用戶可建立/切換/重新命名/關閉佈局分頁。

### 新元件

**`Controls/LayoutTabBar.xaml`** + **`Controls/LayoutTabBar.xaml.cs`**

```
LayoutTabBar (UserControl)
├── TabItem 列表
│   ├── 圖示 + 名稱（可雙擊內嵌重新命名）
│   ├── * 尾碼表示未儲存變更
│   └── × 關閉按鈕（最後一個分頁不可關閉）
├── ＋ 新增分頁按鈕
└── 支援拖曳重新排序分頁
```

### 資料模型

```csharp
public class LayoutTab {
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "新佈局";
    public List<string?> CameraIds { get; set; } = new(); // null = 空格
    public int SlotCount { get; set; } = 4;
    public CellAspectRatio AspectRatio { get; set; } = CellAspectRatio.Auto;
    public CellSpacing Spacing { get; set; } = CellSpacing.Small;
    public ResolutionMode Resolution { get; set; } = ResolutionMode.Auto;
}

public enum CellAspectRatio { Auto, Ratio16x9, Ratio4x3, Ratio1x1 }
public enum CellSpacing { None, Small, Medium, Large }
public enum ResolutionMode { Auto, High, Low }
```

### LayoutTabBar API

```csharp
public class LayoutTabBar : UserControl {
    public LayoutTab? CurrentTab { get; }
    public event EventHandler<LayoutTab>? TabChanged;

    public LayoutTab AddTab(string name = "新佈局");
    public void CloseTab(string id);
    public void RenameTab(string id, string name);
    public void SelectTab(string id);
    public void MarkDirty(string id); // 標記 * 未儲存
    public void MarkClean(string id); // 已儲存消除 *
    public void Reorder(int fromIndex, int toIndex);
}
```

### DynamicCameraGrid 改造

現有 grid 保留以下擴充：

| 功能 | 實作 |
|------|------|
| 格線設定 | `SetAspectRatio(CellAspectRatio)`, `SetSpacing(CellSpacing)` |
| 拖放交換 | 允許在同一 Grid 內拖曳攝影機交換 Slot |
| 解析度切換 | `SetResolution(ResolutionMode)` 影響載入時 useSubStream 參數 |

### 與現有系統整合

- `LayoutService` 擴充為管理 `List<LayoutTab>`，儲存 `Data/layouts.json`
- `LiveView` 新增 `LayoutTabBar` 於頂端，取代固定標題
- 分頁切換時保存當前 grid 狀態，載入目標分頁 camera order
- CameraTreePanel 拖放目標改為當前 active tab 的 grid

### LayoutService 擴充

```csharp
public interface ILayoutService {
    List<LayoutTab> GetAllTabs();
    LayoutTab? GetTab(string id);
    void SaveTab(LayoutTab tab);
    void DeleteTab(string id);
    LayoutTab CreateTab(string name);
    LayoutTab? CurrentTab { get; }
    void ActivateTab(string id);
}
```

---

## Phase 3 — MainWindow 架構改造

### 目標
將 MainWindow 從「左側導覽列 + Drawer + MainContent」改造為 Nx 風格的「頂端 Tab Navigator + 左側 Resource Tree + 右側 Notifications Panel + 底部 Timeline」。

### 新佈局

```
┌──────────────────────────────────────────────────────────────────┐
│ TabNavigator (LayoutTabBar + 主選單 + 通知鈴鐺 + 使用者 + ╳)     │
├──────────┬────────────────────────────────┬──────────────────────┤
│Resource  │  Scene (DynamicCameraGrid)      │ NotificationsPanel  │
│Tree      │  / Settings / Device etc.       │ (可收合)             │
│(可調寬)  │                                │                     │
│Alt+1     │                                │ Alt+2               │
├──────────┴────────────────────────────────┴──────────────────────┤
│ NxTimeline + Transport Controls (Play/Pause/Skip/Live/Now)       │
│ Alt+3                                                           │
└──────────────────────────────────────────────────────────────────┘
```

### MainWindow Grid 定義

```xml
<Grid>
    <Grid.RowDefinitions>
        <RowDefinition Height="Auto"/>     <!-- TabNavigator -->
        <RowDefinition Height="*"/>         <!-- 主體 -->
        <RowDefinition Height="Auto"/>     <!-- Timeline -->
    </Grid.RowDefinitions>

    <!-- Row 0: TabNavigator -->
    <Border Grid.Row="0">
        <Grid>
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="Auto"/> <!-- ☰ menu -->
                <ColumnDefinition Width="*"/>    <!-- LayoutTabBar -->
                <ColumnDefinition Width="Auto"/> <!-- 🔔 👤 ╳ -->
            </Grid.ColumnDefinitions>
            ...
        </Grid>
    </Border>

    <!-- Row 1: 主體區（三欄） -->
    <Grid Grid.Row="1">
        <Grid.ColumnDefinitions>
            <ColumnDefinition x:Name="ResourceTreeColumn" Width="200"/>
            <ColumnDefinition Width="*"/>
            <ColumnDefinition x:Name="NotifColumn" Width="0"/>
        </Grid.ColumnDefinitions>
        <controls:ResourceTree Grid.Column="0" x:Name="ResourceTreePanel"/>
        <ContentControl Grid.Column="1" x:Name="MainWorkArea"/>
        <controls:NotificationsPanel Grid.Column="2" x:Name="NotifPanel"/>
    </Grid>

    <!-- Row 2: Timeline -->
    <Grid Grid.Row="2" Height="48">
        <controls:NxTimeline x:Name="GlobalTimelineControl"/>
        <controls:TransportControls/>
    </Grid>
</Grid>
```

### TabNavigator 設計

| 元件 | 說明 |
|------|------|
| ☰ Menu | 下拉：系統設定 / 設備管理 / 匯出 / 快捷鍵說明 / 關於 |
| LayoutTabBar | 佈局分頁（僅 LiveView 模式顯示） |
| 🔔 | 通知鈴鐺，未讀計數徽章，點擊開啟 NotificationsPanel |
| 👤 | 目前使用者，下拉：設定 / 登出 |
| ╳ / □ / — | 自訂視窗控制按鈕 (使用 WindowChrome) |

### 面板快捷鍵

| 快捷鍵 | 面板 | 行為 |
|--------|------|------|
| `Alt+1` | Resource Tree | 切換顯示/隱藏 |
| `Alt+2` | Notifications Panel | 切換顯示/隱藏 |
| `Alt+3` | Timeline | 切換顯示/隱藏 |
| `Ctrl+Tab` | Layout 分頁 | 切換至下一分頁 |
| `Ctrl+Shift+Tab` | Layout 分頁 | 切換至上一分頁 |
| `F` / `F11` | 全螢幕 | 隱藏所有面板（保留 Timeline 可選） |

### Resource Tree 強化

現有 `CameraTreePanel` 改名/改造為 `ResourceTree`：

| 功能 | 說明 |
|------|------|
| 分組節點 | 保留現有群組 + 我的最愛 |
| 拖放至 Grid | 保留現有 DragSource 行為 |
| 雙擊快速選取 | 雙擊攝影機 → 加入目前 Grid |
| 右鍵選單 | 保留播放/錄影/PTZ/最愛 |
| 寬度調整 | 右邊緣拖曳（GridSplitter） |
| 搜尋過濾 | 保留搜尋框 |

### Content Switcher 改造

- `MainWorkArea` ContentControl 保留
- LiveView 模式：顯示 Scene（含 LayoutTabBar + DynamicCameraGrid）
- 其他模式（Settings/Device/Map/Maintenance）：直接取代 ContentControl，隱藏 LayoutTabBar

### 面板狀態持久化

擴充 `window_state.json`：

```json
{
    "DrawerOpen": true,
    "ResourceTreeWidth": 200,
    "NotifPanelOpen": false,
    "TimelineVisible": true
}
```

---

## Phase 4 — 視覺細節打磨

### 字體系統

| 用途 | 字型 | 大小/字重 |
|------|------|-----------|
| 版面標題 | Segoe UI Variable / Inter | 14px SemiBold |
| 內容文字 | Segoe UI Variable / Inter | 12px Regular |
| 輔助文字 | Segoe UI Variable / Inter | 11px Regular |
| 時間軸刻度 | Segoe UI Variable / Inter | 10px Regular |
| 按鈕文字 | Segoe UI Variable / Inter | 12px SemiBold |

### 圖示系統

- 以 **Fluent UI System Icons** 取代 Emoji
- 向量路徑定義於 `Styles/Icons.xaml` 作為資源
- 使用 `Path` + `Geometry` 而非 Bitmap，支援主題色繼承
- 逐步替換優先級：導覽列 → 按鈕 → 樹節點 → 通知 → 狀態列

### 間距系統

統一間距尺規：4 → 8 → 12 → 16 → 24 → 32 (px)

| 場景 | 間距 |
|------|------|
| 面板內邊距 | 16px |
| 卡片內距 | 20px |
| 元件間距 | 8px / 12px |
| 列表項間距 | 4px |
| 區段間距 | 24px |
| 視窗邊距 | 0px (邊到邊) |

### 按鈕樣式

| 類型 | 風格 |
|------|------|
| Primary | 填滿背景（強調色 `#2196F3`），白色文字，8px 圓角 |
| Secondary | 邊框 1px（`#444`），透明背景，懸停填滿 |
| Flat | 無邊框，懸停微亮背景 |
| Icon | 正方形 32x32，僅圖示，懸停圓形高亮 |

### 動畫系統

| 動畫 | 持續時間 | 類型 |
|------|----------|------|
| 面板滑入/滑出 | 200ms | CubicEaseInOut |
| 分頁切換淡入 | 150ms | Linear |
| 通知進出 | 300ms | BackEase |
| 按鈕 Hover | 100ms | Linear |
| Toast 顯示 | 400ms | CubicEaseOut |

使用 WPF `Storyboard` + `DoubleAnimation` / `ColorAnimation`，避免第三方動畫庫。

### 主題資源架構

```
Styles/
├── Colors.xaml (深色)      # 現有，僅調整數值
├── ColorsLight.xaml (亮色)  # 現有，僅調整數值
├── Typography.xaml (字型)   # 新增—所有字型資源
├── Spacing.xaml (間距)      # 新增—Thickness 資源
├── Animations.xaml (動畫)   # 新增—Storyboard 資源
└── Icons.xaml (圖示)        # 新增—PathGeometry 資源
```

所有新控制項直接引用 `{StaticResource}`，不寫死 px 值。

---

## 實作順序

1. **Phase 1** (NxTimeline) — 最高優先，獨立元件不破壞現有架構
2. **Phase 2** (Layout 多分頁) — 改造 DynamicCameraGrid，新增 LayoutTabBar
3. **Phase 3** (MainWindow) — 重新佈局 MainWindow，搬遷 Timeline 至此
4. **Phase 4** (視覺打磨) — 主題、圖示、動畫，跨所有元件的視覺統一

---

## 非目標

- 不改動後端服務（RecordingService / VideoIndexService / CameraService 等）
- 不改動 VideoPlayer 核心播放邏輯（仍在 Flyleaf 解碼之上）
- 不引入第三方 UI 框架（保持純 WPF）
- 不重新設計 Login / License 頁面
