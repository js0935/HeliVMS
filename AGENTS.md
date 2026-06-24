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
- **側欄全面改造為 Nx Witness 風格**
- **所有 XAML emoji 全數取代為 PathGeometry 向量圖示**（曾使用 📹🔖📷🎥🔍📤📥💾🔄🗑📋📊☀🌙 等全部清除，僅餘 D3DImageRenderer 開發註解 🔴）
- **所有 C# code-behind emoji 清除**：MainWindow 狀態列（📷💾🧠）、PlaybackView 同步指示器（🔄）、NxTimeline ToolTip（📌）、UserEditDialog 複製按鈕（✅📋）
- **CameraTreePanel 重構**：📁/⭐/📷 emoji 改為 PathGeometry（IconGeometry 屬性）、全部 #FFFFFFFF/#FF000000 等 6 種硬編碼色碼改為 StaticResource
- **SidebarButton 統一樣式**（48x42px + 左側 3px accent 指示條）
- **SettingsView/ExportDialog/EventRuleEditDialog 硬編碼色碼全面修正**
- **CameraPickerDialog/ShortcutHelpView/DashboardView 硬編碼色碼全面修正**
- **LiveView 控制列 emoji 全數取代 + TimelineContainer/ControlBar 硬編碼色碼**
- **PlaybackView 14 處 emoji 取代 + 書籤動畫重構（Grid 包裝 Path/TextBlock 切換）**
- **MainWindow drawer 📹→IconCamera + 主題切鈕改用 ToolTip**
- **VideoPlayer/PlaybackPlayer/CameraGrid/ChannelManagementPage 全面修正**
- **Icons.xaml 擴充至 37 個 PathGeometry 圖示**（新增 IconFolder/IconStar 等）
- 三次 commit + push，所有建置 0 錯誤 0 警告

### 進行中 (In Progress)
- (無)

### 阻塞中 (Blocked)
- 使用者回報 **設備管理（DeviceManagementView）無法管理攝影機** — 需檢視執行時期行為，目前無法透過靜態分析重現

## 關鍵決策 (Key Decisions)
- **Nx Witness 側欄風格**：保留 48px 窄側欄設計（非 Nx 頂部分頁列），因 HeliVMS 以 View 切換為核心；向量圖示 + 左側 accent 指示條 + 正確暗色主題色系
- **SidebarButton 而非 RadioButton**：因需容納非導覽按鈕（主題/通知/使用者），改為 Button 基底 + 手動 Opacity 管理
- **白底灰字修復策略**：TextBox/PasswordBox/ComboBox 三個屬性皆硬編碼 → Style="{StaticResource Modern...}"；僅部分屬性 → 逐一改為 StaticResource
- **Emoji 取代策略**：Button 內 Path 元素取代純 emoji；text+emoji 保留文字移除 emoji；C# code-behind 使用 TextBlock/Path Visibility 切換取代 Content= 寫法；樹狀節點 IconGeometry 改用 Geometry.Parse 繫結

## 下一步 (Next Steps)
- 等待使用者確認 Nx Witness 風格改造結果，或指出更多需改良之處
- 確認 DeviceManagementView 執行時期異常原因
- 持續監看 ROADMAP.md 優先順序

## 重要上下文 (Critical Context)
- 所有 `dotnet build` 輸出「建置成功。0 個警告 0 個錯誤」
- 側欄：6 顆導覽按鈕（Live/Device/License/Dashboard/EMap/Settings）+ 3 顆工具按鈕（Theme/Notification/User），分隔線區隔
- 專案 GitHub：`https://github.com/js0935/HeliVMS`，版本 1.0.0
- 使用者提示「持續優化不要停止除非我中斷」
- emoji 全數清除僅餘 1 筆開發註解（D3DImageRenderer.cs:125 🔴）
