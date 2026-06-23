# HeliVMS Roadmap — 媲美 Nx Witness 的智慧影像管理系統

## 現狀摘要

- **程式語言 / 框架：** .NET 10 WPF, x64
- **核心引擎：** FlyleafLib + FFmpeg.AutoGen + 自製子行程解碼器
- **DI / 持久化：** Microsoft.Extensions.DependencyInjection / JSON + SQLite (EF Core)
- **日誌：** Serilog
- **已建置模組：** 26 服務、13 控制項、11 檢視、5 對話框、完整深色主題

---

## Phase 1 — 即時監看體驗強化（預計 2–4 週）

### P1.1 時間軸錄影區間色條 ✅ 進行中
- 每台攝影機獨立色相（HSV 色輪分配）
- 30 秒自動刷新，Canvas 寬度對齊 Slider
- 正在錄影的區段以閃爍動畫標示
- 滑鼠懸停顯示「攝影機名稱 / 起訖時間」

### P1.2 事件規則引擎
- 條件（Condition）：移動偵測、排程時段、ONVIF 警報、斷線
- 動作（Action）：開始錄影、發送 Email/HTTP Webhook、PTZ 跳預設點、OSD 警示
- UI：規則列表 + 條件/動作設定面板（SettingsView / 獨立對話框）
- 持久化為 JSON 規則檔

### P1.3 PTZ 預設點 / 巡航管理 UI
- 設定、呼叫、刪除預設點
- 巡航路線編輯器（多點順序 + 停留秒數）
- PTZ 面板整合巡航啟／停按鈕

### P1.4 版面配置儲存／載入
- 目前格位布局（Camera → Slot）可命名儲存
- LayoutService：建立、讀取、套用、刪除配置
- LiveView 工具列加入配置選擇下拉

### P1.5 匯出片段
- 時間軸選取時間區段
- 選擇目標攝影機（單一／全部）
- 轉檔為 MP4／AVI，含進度條
- 儲存路徑選擇

---

## Phase 2 — 系統完備度（預計 4–6 週）

### P2.1 告警通知系統
- Email（SMTP 設定 UI + 發送執行緒）
- Push Notification（Windows 原生 Toast）
- HTTP Webhook（JSON Payload 可自訂）
- 通知佇列 + 重試機制

### P2.2 移動偵測設定 UI
- 靈敏度滑桿、區域遮罩繪製（Canvas overlay）
- 每台攝影機獨立偵測參數
- 儲存至 Camera 模型

### P2.3 稽核日誌檢視器
- 操作記錄（登入、設定變更、使用者管理）
- 時間／使用者／動作類型過濾
- 匯出 CSV

### P2.4 雙向語音
- Audio Talk 按鍵通話（ONVIF 雙向音訊）
- 對講按鈕整合至 ControlBar

### P2.5 故障轉移錄影
- N+1 備援機制（主機異常 → 備援自動接續）
- 斷線緩衝 （Buffer on disconnect）

### P2.6 儀表板強化
- 即時頻寬圖、錄影成功率、儲存使用率
- 健康分數（連線狀態 × 錄影狀態 × 儲存狀態）
- 最近事件時間軸

---

## Phase 3 — 進階與延伸（預計 6–8 週）

### P3.1 多樓層電子地圖
- 樓層切換 Tab
- 設備狀態色彩疊加（綠 = 正常、紅 = 斷線、黃 = 錄影異常）
- 攝影機預覽 Popup

### P3.2 Web / Mobile API
- RESTful API（攝影機列表、即時串流 URL、事件查詢）
- gRPC streaming 雙向通訊
- Swagger 文件

### P3.3 第三方整合 SDK
- Plugin 載入機制（Assembly.Load）
- HTTP 事件轉發範本

### P3.4 智慧分析整合
- LPR 車牌辨識（第三方引擎串接）
- POS / ATM 交易資料疊加

### P3.5 效能調校
- 子碼流模式（Grid 多分割時自動切換子串流）
- 硬體加速編碼錄影（NVENC / QSV）
- 記憶體池調校、解碼器並行控制

---

---

## Post‑Phase 3 擴充功能（全自主持續建置）

### ✅ 錄影排程 (RecordingScheduleService)
- CameraSchedule / ScheduleRule 模型，JSON 持久化
- SettingsView 分頁 10：攝影機下拉 + 規則編輯器（週幾／起訖時間／錄影啟用／偵測啟用）

### ✅ 系統托盤圖示 (TrayIconService)
- Hardcodet.NotifyIcon.Wpf TaskbarIcon
- 關閉時最小化至系統匣，雙擊還原，內容功能表 (Show/Hide/Exit)

### ✅ 鍵盤快捷鍵
- F / F11 全螢幕，Esc 退出全螢幕
- Ctrl+R 切換選取攝影機錄影，F1 顯示快捷鍵說明疊層

### ✅ 快捷鍵說明疊層 (ShortcutHelpView)
- 半透明彈出視窗列出所有快捷鍵

### ✅ 通知歷史 (NotificationHistoryService)
- 記憶體內佇列（最多 500 筆），未讀計數
- 導航列鈴鐺按鈕 + 彈出飛出視窗（全部標為已讀／清除）

### ✅ 攝影機分組 (CameraGroupService)
- CameraGroup 模型 + JSON 持久化，明確的攝影機 ID 列表
- DeviceManagementView 分頁 4：群組列表 + 每群組攝影機勾選清單

### ✅ 移動觸發錄影 (MotionTriggeredRecordingService)
- 對啟用移動偵測但未啟用連續錄影的攝影機，偵測到移動時自動開始錄影
- 30 秒冷卻、5 秒預錄 ＋ 15 秒後錄緩衝

### ✅ 備份還原 (BackupService)
- ZIP 壓縮備份所有 Data/*.json 設定檔
- 還原、列出備份、自動清理舊備份（最多保留 10 份）
- SettingsView 分頁 11

### ✅ 攝影機搜尋篩選
- LiveView 右上角搜尋框，依名稱或 ID 過濾攝影機並重新載入網格

### ✅ 儀表板警示統計
- DashboardView 顯示今日 ERROR／WARN／INFO 計數卡

### ✅ 自動重連設定
- Camera 模型新增 AutoReconnectEnabled／MaxReconnectAttempts／ReconnectIntervalSeconds
- CameraEditDialog 新增自動重連設定區塊

### ✅ 系統操作控制
- Settings 除錯頁面新增「重新啟動應用程式」與「關閉應用程式」按鈕

### ✅ PTZ 巡航路線持久化 (TourService)
- 巡航路線儲存至 Data/ptz_tours.json
- PTZControlPanel 整合刪除按鈕，載入攝影機時自動載入對應路線

---

## 開發原則

1. **單一架構師：** 全由主代理（我）決策，不使用平行子代理以確保一致性
2. **每步 Build 驗證：** 每次修改後 `dotnet build` 確保 0 錯誤 0 警告
3. **原子 Commit：** 每項功能完成即 Commit + Push，訊息繁體中文
4. **不改現有介面（除非必要）：** 優先擴充而非重構
5. **無註解原則：** 生產程式碼不寫註解，只留區域分割線
