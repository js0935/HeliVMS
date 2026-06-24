# HeliVMS 開發模式

This project uses **全自主模式** — the agent should:

1. **直接執行**所有實作、修正、重構，不需請求許可或確認
2. 每完成一個功能後，自動繼續下一個最有價值的改進
3. 只在建置錯誤或無法解決的問題時才提問
4. 每次變更後執行 `dotnet build` 確保 0 錯誤 0 警告
5. 使用繁體中文提交訊息（git commit）
6. 持續推送至 GitHub

---

## 目前目標
持續將 HeliVMS 功能選單設計得更接近 Nx Witness 風格，無須中斷請求許可。

## 專案狀態
### 已完成 (Done)
- **側欄全面改造為 Nx Witness 風格**：48px 窄側欄 + SidebarButton 樣式（左側 3px accent 指示條）+ SubMenuDrawer（Settings 11 項 / Device 5 項）
- **生產代碼 emoji 全數清零**：XAML（SettingsView ✓✗✕、ChannelManagementPage ⚡、PlaybackView 14 顆傳輸控制）與 C# 全面清除
- **Icons.xaml 擴充至 40 個 PathGeometry 圖示**：IconStop/IconRewind/IconFastForward/IconPause/IconSkipBack/IconSkipForward/IconSearch/IconLogout 等
- **全部 ContextMenu 加入 PathGeometry 圖示 + 鍵盤快捷鍵提示**：CameraTreePanel（8 項）、VideoPlayer（6 項）、PlaybackPlayer（4 項）、MainWindow 側欄（14 項）、LayoutTabBar（4 項）、EMapView（6 項）、NxTimeline（4 項）— 全部 MenuItem.Icon + InputGestureText
- **LiveView/VideoPlayer/PlaybackPlayer/PlaybackView 全部硬編碼色碼 → StaticResource**（約 100+ 行清理）
- **DynamicCameraGrid 全部硬編碼色碼 → StaticResource** + 容器追蹤修復 MaximizeSlot bug
- **NotificationsPanel/LayoutTabBar/NxTimeline/TimelineControl/SettingsView 等全面硬編碼色碼清理**（新增 60+ 行 StaticResource 替換）
- **Colors.xaml 新增錄影類型 Brushes** + 應用至 RecordingSettingsPage（RecordingContinuousBrush/MotionBrush/AlarmBrush/AiBrush）
- **CameraTreeItem 狀態燈即時更新**：IsConnected PropertyChanged、圓點 6px→8px、ConnectionColor StaticResource 快取
- **DynamicCameraGrid.MaximizeSlot bug 修復**：_containers[] 陣列追蹤容器層級
- **Camera tile 全面升級 Nx 風格**：NameTag 改為全寬底部資訊列（攝影機名稱 + HealthBadge + 錄影指示燈 + 錄影經過時間），移除 DynamicCameraGrid 重複名稱標籤與頂端 RecDot，PtzOverlay ZIndex 修正
- **PlaybackView 殘留色碼清理**：4 處 #FFD700/#33000000/#22000000 → StaticResource
- **PlaybackView Unicode 符號全面置換為 PathGeometry 圖示**：◀▶ PrevDayBtn/NextDayBtn → IconChevronLeft/Right、StepBackBtn/StepFwdBtn → IconRewind/IconFastForward、PlayBtn/FsPlayBtn → IconPlay、RefreshChannelBtn → IconRefresh、LoopToggleBtn → IconLoop、LayoutBtn → IconLayout、FsStepBack/FsStepFwd → IconRewind/IconFastForward
- **LiveView 濾鏡按鈕殘留色碼清理**：4 處 #332196F3/#33FF5722/#33F44336/#33FFC107 → FilterContBrush/MotionBrush/AlarmBrush/AiBrush（Opacity=0.2 的 Recording 色系資源）
- **LiveView 內聯 Path → StaticResource**：下拉箭頭改為 IconChevronDown
- **DashboardView 日曆導覽 Unicode → IconChevronLeft/IconChevronRight**
- **Icons.xaml 新增 3 個 PathGeometry**：IconChevronLeft、IconRefresh、IconLoop
- **LiveView 版面配置改為 Nx 風格拖曳 Grid 選擇器**：8×8 可拖曳選取，即時高亮 + layout 標籤
- **登出改為 NavigateToLogin**：而非 Application.Current.Shutdown()
- **MainWindow_Loaded 加入 try-catch 診斷日誌**
- **登入畫面強制顯示**：清除殘留 session 不再跳過登入
- **Flyleaf 硬體加速連結設定**：VideoAcceleration 與 FFmpeg HwDeviceType 跟隨 AppSettings.EnableHardwareAcceleration
- **NotificationsPanel/LayoutTabBar/NxTimeline/TimelineControl/SettingsView/ChannelManagementPage/UserEditDialog 等全面硬編碼色碼清理**
- **NxTimeline 錄影類型色碼改從 Colors.xaml 資源載入（含 fallback）**
- **PlaybackPlayer 殘留色碼置換 + Session 還原加入完整診斷日誌**

### 待完成 (~0%)
- 版面預覽 Popup 拖放選擇器（Nx 風格 grid 預覽升級）— 基本版已完成，進階拖放可後續優化
- NxTimeline 錄影類型區塊著色（顏色值已與 Colors.xaml 一致，繪圖邏輯維持原狀）

### 阻塞中 (Blocked)
- 使用者回報 **設備管理（DeviceManagementView）無法管理攝影機** — 需檢視執行時期行為，目前無法透過靜態分析重現

### 阻塞中 (Blocked)
- 使用者回報 **設備管理（DeviceManagementView）無法管理攝影機** — 需檢視執行時期行為，目前無法透過靜態分析重現

## 關鍵決策 (Key Decisions)
- **Nx Witness 側欄 vs 頂部分頁列**：保留 48px 窄側欄設計，搭配 ContextMenu 快速操作捷徑
- **SubMenuDrawer 雙模式**：Settings/Device 共用同一 Border 但內部兩個 ScrollViewer
- **Sidebar ContextMenu 整合**：右鍵選單透過 MainWindow code-behind 判斷 MainWorkArea.Content 型別呼叫對應方法
- **錄影類型 Brushes 統一**：RecordingContinuous/Motion/Alarm/Ai 4 組作為全域色彩語言

## 下一步
- 等待使用者確認登入問題（提供 log）
- 持續監看 ROADMAP.md 優先順序
- 待使用者指示再繼續

## 重要上下文
- 所有 `dotnet build` 輸出「建置成功。0 個警告 0 個錯誤」
- 側欄：6 顆導覽按鈕 + 3 顆工具按鈕，分隔線區隔
- 專案 GitHub：`https://github.com/js0935/HeliVMS`，版本 1.0.0
- emoji 全數清除僅餘 1 筆開發註解（D3DImageRenderer.cs:125 🔴）
