# HeliVMS 合作恢復卡（RESUME CARD）

> 用途：對話上下文過長、影響回覆速度時，開新 session 用這份檔案接手，**不依賴舊對話記憶**。
> 權威來源：`git` 與 GitHub Actions 的現況（不是聊天記錄）。
> 所有里程碑均已 commit＋push、CI 全綠、工作目錄乾淨（下方數據皆為驗證過的真實值）。

## 一句話總結
HeliVMS 為一套**網路影像監控系統**（WPF 桌面應用：即時監看／回放／AI 事件中心／錄影排程）。
里程碑 **M1 至 M25 已全數 commit＋push、CI 綠燈**。最終 commit＝HEAD（M25 為最新）。
Release build 0 error、測試 **109/109 全過**、`git status --porcelain` **空白（工作目錄清乾淨）**、
本地與遠端完全同步（`git diff origin/HEAD` 為空）。

## 開新 session 的接手方法
```powershell
# 在 D:\HeliVMS 開新 session 時，先對新的 agent 講：
git status                    # 預期為 empty（乾淨）
git log --oneline -20         # 預期見到 M1..M23，HEAD＝（M23）
git diff origin/HEAD          # 預期為空（同步）
gh run list -L 3              # 預期全部 success
```
新 session 的 prompt 只需這一句：
「接續 HeliVMS M16 收尾，請先讀 `docs\SESSION_RESUME.md`，照指示以自主模式繼續。」
新 session 會以 git 現況接手，不會靠猜測。

## 現況快照（權威來源＝git，非聊天記憶）
- 最後 commit：`HEAD`＝**M25（PTZ 控制＋ONVIF 自動探索強化）**——`OnvifDeviceService`
  新增 `Tptz` namespace（`ver10/ptz/wsdl`）＋**獨立** `GetCapabilities Category="All"` 解析
  `<Capabilities><PTZ><XAddr>`（不覆用 Media 快取）：`PtzXAddr`/`HasPtz`/
  `EnsurePtzCapabilityAsync`＋`GetPtzStatusAsync`/`ContinuousMoveAsync`(Velocity)/
  `StopPtzAsync`(PanTilt+Zoom=true)/`GetPtzPresetsAsync`/`GotoPtzPresetAsync`/`SetPtzPresetAsync`
  （`TptzAction` helper，`ParseAxis` 保險防 NaN）；`OnvifModels.cs` 加 `PtzStatus`/`PtzPreset`；
  資料面**不加 DB 欄位、schema 維持 v6**：`DeviceRepository.Get(id)`（含帳密）＋`DeviceRecord`
  `Username`/`PasswordEncrypted`；**`PtzWindow`**（AutomationId `PtzWindow`/`PtzStatusText`、
  八向＋變焦＋`PtzStopButton`、`PtzPresetList`＋儲存/移轉/重新整理；按一次＝ContinuousMove 400ms
  後自動 Stop）＋MainWindow `PtzButton`（新行按鈕列）＋格子右鍵「PTZ 控制」`OnCtxPtz`
  （`_cellChannel[cell]`→`OpenPtz`；未綁定→訊息盒）；Wizard 顯示「PTZ：支援/不支援/未知」。
  單元測試 **109/109**（Storage 48＋Alarms 45＋Licensing 8＋Devices 8；Devices ＋4＝PTZ 解析/
  封包、Storage ＋1＝Get 帳密）＋ E2E 新增 **`ptzcheck` PTZCHECK_OK**（迷你 ONVIF 假設備
  Socket 級三端點＋channel 1 綁定 device→App `PtzButton`→`PtzWindow` 狀態「PTZ 支援」→
  `PtzUpButton`→假設備收到 ContinuousMove）＋ notifcheck/logcheck/smtpcheck/setcheck/snapcheck/
  expcheck 全數回歸綠
- 前一個 M24 交付＝`1a36f81`（SMTP 快照附件＋通知合併：SmtpNotifier 多筆多載、純 SMTP 週期
  合併寄一封、Attachment 依快照路徑；單元測試 104/104、E2E smtpcheck SMTPCHECK_OK）
- 分支／遠端：`git diff origin/HEAD` 為空（完全同步）；HEAD＝origin
- CI：`gh run list` 顯示最新 run `conclusion=success`；建置 0 error、測試 109/109

## 架構關鍵（確切、無漂移）
- 解決方案：`D:\HeliVMS\HeliVMS.App\HeliVMS.App.csproj`（WPF）＋ `HeliVMS.slnx`
- 儲存層真名：`SqliteStore`、`AlarmEventRepository`、`ChannelRepository`（無漂移、無臆測）
- 回放視窗：`PlaybackWindow`，AutomationId `PlaybackEventList` 的事件明細列表，
  code-behind 的 `OnEventSelected` handler（SelectionChanged→向該事件所在 band 跳播至事件時刻）、
  `ChannelRepository(_store).List()` 建立 camNames 字典；事件單元在 UIA 樹是 **`ControlType.DataItem`**
- 回放鍵盤快捷鍵實作（M17）：`OnPreviewKeyDown`（Window PreviewKeyDown）、`SeekBy`（±秒，跨段定位
  用 `_bandSegments` 找含 targetUtc 的段並 `PlayFrom`；越界 clamp 到首段起點／末段終點）、
  `StepFrame`（暫停→`PlayFrom(_posInside+1/15)`→`_pendingFramePause` 於首幀後 `StopPlayback(keepSelection:true)`）
- E2E harness：`C:\Users\JS\AppData\Local\Temp\opencode\e2e\x\Program.cs`
  （**temp harness，不入 git、不影響 CI、不影響 repo 狀態**）
  - 已有 block：`playcheck`、`playbackcheck`、`playmark`、`plist`（M16）、`kbcheck`（M17，KB_OK）、
    `evcard`（M18，EV_OK）、`setcheck`（M19，SET_OK）、**`snapcheck`（M20，SNAP_OK）**、
    **`expcheck`（M21，EXP_OK）、**`notifcheck`（M22，NOTIF_OK）**、
     **`logcheck`（M23，LOG_OK）**、**`smtpcheck`（M24，SMTPCHECK_OK）**、
     **`ptzcheck`（M25，PTZCHECK_OK）**
  - `kbcheck` 驗證：`Space` 暫停（t1）→ `→` 前跳 10 秒（t2−t1＝11s）→ `F` 逐幀仍暫停 →
    `Esc` 停止（StopButton disabled）；輸出 `KB_OK`
  - `evcard` 驗證：主視窗→右鍵 cell→「開啟事件中心」；選頻道後 grid DataItems≥2、
    「卡片」切換後 grid DataItems=0 且 card ListItems≥2、卡片含縮圖 Image、
    選中 `evcard-person` 卡片後 `SnapshotDetailText`＝「明細：evcard-person」且 `SnapshotPathText` 指向快照檔、
    切回清單 grid DataItems 回復；輸出 `EV_OK`
  - `setcheck` 驗證：`--settings` 旗標直開設定中心；nav 5 項；初始「一般」頁可見（dataRoot
    offscreen=false、儲存頁元素**不存在**）；切「儲存」後配額 Box 出現且值=種子「4」、SetValue「5」→
    套用 → 重開 SqliteStore 讀 `app_settings["recording.quota_gb"]=="5"`；切「授權」狀態非空；切「功能」
    入口按鈕存在；輸出 `SET_OK`
  - `snapcheck` 驗證：前置造 120/100 天前舊快照＋新快照、設 `snapshots.retention_days="30"`；
    `--settings` 直開設定中心（nav 現 5 項）；儲存頁 `SnapshotDaysTextBox` 值＝「30」→「立即清理」
    → 舊快照檔刪除、新快照保留；切「頻道」頁 ChannelList DataItems≥1；輸出 `SNAP_OK`
  - `expcheck` 驗證：前置造 6s 段入 C:\HeliVMSData 庫（第一頻道）、清空 exports/；`--export`
    直開匯出精靈；驗 `ChannelCombo/OutputFolderBox/HashCheckBox/ExportButton` 存在；點匯出後
    **輪詢 ResultText 含「匯出成功」且 `.sha256` 產出**（勿只等 mp4 出現）；驗 hash 檔內容與
    輸出 MP4 的 SHA-256 一致、ResultText 含 SHA-256；輸出 `EXP_OK`
  - `notifcheck` 驗證（M22）：(A) 本機 `TcpListener` 假 webhook（前 2 次 500→第 3 次 200）＋
    `NotificationService`（temp DB 設 `notify.enabled`/`notify.webhook.url`）enqueue motion 事件，
    驗 `DeliveredCount=1、FailedCount=0、Hits=3` 且 payload JSON 含 `type/channel/data`；
    (B) `--settings` 直開設定中心→切「通知」頁→填 webhook URL/埠/收件者＋開啟總啟用→套用→
    DB 讀回 `notify.*` 值；(C)（M23）Phase A 後重開 temp store 驗 `notification_log`
    `Count=1、Ok=true、Route="webhook"`；輸出 `NOTIF_OK`
  - `logcheck` 驗證（M23）：前置清空 C:\HeliVMSData 的 `notification_log` 並插 2 筆（ok=1／ok=0 各一）→
    啟動主視窗→`NotificationButton` Invoke→開「HeliVMS 通知紀錄」窗→`NotificationRefreshButton`
    Invoke→`NotificationLogList` DataItem≥2、`NotificationCountText` 含「共 2 筆」；輸出 `LOG_OK`
  - **`logcheck`/`smtpcheck` 前置污染**：與 C:\HeliVMSData 同庫的 block 建議先 `DELETE FROM
    notification_log` 再插資料（M24 曾因 smtpcheck 殘留 log 造成 logcheck rows=6 誤判 BAD）
  - `smtpcheck` 驗證（M24）：熱拷 mini SMTP 假伺服器（`E2eSmtpServer`，Socket 級）＋ temp/App DB
    純 SMTP 設定（`notify.smtp.*`、webhook.url 空）＋ NotificationService interval 60ms→enqueue
    2 事件（一筆帶存在快照 PNG）→`DeliveredCount==2`→恰收 1 封、payload 含 `image/png`＋快照檔名＋
    `Content-Disposition: attachment`、`notification_log` Count==2；輸出 `SMTPCHECK_OK`
  - `ptzcheck` 驗證（M25）：迷你 ONVIF 假設備（`E2ePtzDevice`，Socket 級 HTTP/1.1：通
    `/onvif/device_service` 回 GetCapabilities→Media+PTZ XAddr＝本機 `/onvif/Media` 與
    `/onvif/ptz_service`；`/Media` 回 GetProfiles；`/ptz_service` 依動作回 Presets/空信封並記錄
    action）＋清空 devices、綁定 channel 1 DeviceId→**服務層直測**（Ensure→profiles=1→presets=2→
    ContinuousMove 使 MoveCount+1→Stop 收到）→清空後 App `PtzButton` Invoke→`PtzWindow` 開→
    輪詢 `PtzStatusText` 名稱含「PTZ 支援」→`PtzUpButton` Invoke→假設備 MoveCount 遞增→
    `PTZCHECK_OK`；`SqliteStore.Execute` 回 void，affected rows 要用 `SELECT changes()` 查
  - `evcard`/`kbcheck` 自 M18/M17 後**未列入後續里程碑回歸組**（環境 flaky：右鍵 Popup 選單 UIA
    與播放秒表對 CPU 敏感；M19-M25 均未跑）。回歸組＝notif/log/smtp/set/snap/exp（＋ptz 於 M25），
    看到 evcard/kbcheck BAD 不迴溯 M25

## 尚未完成／下一步（依 git 與 repo 判斷）
1. 下一里程碑候選（M26 起）：系統匣常駐、通知靜默時段延時補送、紀錄頁篩選/分頁 UI、
   離線事件源（斷線補送）、SNMP/MQTT/推播通道、PTZ 增強（長按連續移動、AbsoluteMove/Home、
   預設點管理 UI、多 Profile 選擇 UI）、ONVIF Discovery Hello/Bye/Resolve
   - 每里程碑節奏照舊：定義先寫入本檔→實作→App Release build 0 error→單元測試→E2E harness
     block→commit＋push＋CI success→`git status --porcelain` 空白

## 已知雷區（勿再犯）
- **勿以 bash 對 repo 源碼做 byte 級重寫**（曾造成 UTF-8 漂移／mojibake 污染，已 `git restore` 還原）；
  改源碼一律用 read/edit tool
- grep tool 對 harness 檔案可能出現「零結果」（權威＝`git show HEAD` 對 repo 檔；
  harness 在 temp 路徑，請用**完整絕對路徑** grep）
- **勿臆測 repo 內的識別名稱**；以 `git show`／`git diff` 為準
- **回放窗 ComboBox 的 UIA**：下拉 item 的 `ListBoxItem.Current.Name` 是物件的 ToString（型別名），
  要以 item 內層 `ControlType.Text` 的 Name 來配「頻道 N」再對 `ListItem` 下
  `SelectionItemPattern.Select()`（對 Text 下 Select 會靜默失敗）
- **WPF GridView 事件列是 `ControlType.DataItem` 不是 `ListItem`**；`FindAll(ListItem)` 會拿到 0 筆
- **驗證跳播後 `PlaybackTime` 要輪詢**（ffmpeg 解碼有啟動延遲，且內建倍速預設 0.5×）；不要只等固定 1.5s
- **`EventCenterWindow` 開窗必 NRE（M18 排雷）**：XAML 的 `RangeCombo`/`ChannelCombo` `SelectedIndex` 在
  `InitializeComponent()` 期間即觸發 `OnChannelSelectionChanged → DoRefresh()`，此時 ctor 尚未執行到
  `_events = new AlarmEventRepository(store)`（`_events` 為 null）→ NRE 崩 app。
  修法：`OnChannelSelectionChanged` / `OnRefreshClicked` 開頭 `if (_events is null) return;`
  （此 bug 一直存在——事件中心在 M18 前從未有成功開啟的 E2E，首次觸發才發現）
- **`AutomationElement.RootElement.FindAll(Children)` 對本 app 的視窗可能無 provider（拿不到）**；
  找 HeliVMS 視窗一律用 `Native.EnumWindows` ＋ `AutomationElement.FromHandle(handle)`
- **主視窗按鈕的 UIA `InvokePattern.Invoke()` 不穩定**（有機會「返回成功但 handler 未執行」）；
  改用**右鍵 cell →「開啟事件中心」選單 item Invoke** 較可靠（WPF ContextMenu 是獨立 Popup，
  要在全部 process 的頂層視窗 Descendants 找 Name＝「開啟事件中心」）——`OnCtxOpenEvents` 與按鈕共用 `OnEventClicked`
- **WPF ListBox 卡片在 Visibility=Collapsed 期間只會 materialize 可視項目（virtualization）**；
  切到卡片視圖時 UIA `ListItem` 數量＝畫面上可見張數（非資料總數），驗證要綁「≥ 目標筆」而非精確等於
- **修改過 XAML/code-behind 後務必重新 Release build**（harness 吃 `bin\Release\…\HeliVMS.App.exe`；
  忘 rebuild 會測到舊版且行為不解）
- **回放窗快捷鍵 E2E（kbcheck）前置**：要先 `ChannelRepository.Get(特定 id)` 確認頻道存在、
  用 ffmpeg lavfi 產生當日段並 `BeginSegment/CompleteSegment` 入庫（否則「當日 0 段」無資料可播）
- **`Space` 焦點衝突**：Tab 焦點落在 PlayPauseButton/StopButton 時按空白鍵會雙重觸發（Button 自身也處理空白鍵）；
  故 `OnPreviewKeyDown` 對按鈕聚焦需 `e.Handled=true` 後自行分派播放/暫停，避免 Button Click 與快捷鍵各觸發一次
- **WPF Panel（StackPanel/Grid）沒有 AutomationPeer**：其 `AutomationId`/`x:Name` 無法用
  `FindFirst(Descendants, AutomationIdProperty)` 找到；但 **`Visibility=Collapsed` 的子項目
  （TextBlock/TextBox/Button 等具 peer 元素）在 UIA 樹中直接「不存在」**（FindFirst 回 null），
  切頁測試用「目標頁元素存在／消失」判斷即可（`setcheck` 以此驗證翻頁）
- **設定中心儲存庫權威**：錄影配額已從 DB 讀（`recording.quota_gb`），若改動 `MainWindow.ReadQuotaBytes()`
  需同時顧及 `SettingsWindow.QuotaGbFromStore()`（同優先序：DB→`HELIVMS_QUOTA_GB`→10GB）
- **本機存在有效授權檔**（`%LOCALAPPDATA%\HeliVMS\license.lic`：32 路、2027-09-30 到期），
  授權頁 UIA 驗收以「狀態文字非空」為準，不可假定「未授權」
- **快照檔沒有 DB 索引**：清理只能以 `File.GetLastWriteTimeUtc` 判斷（`PurgeSnapshots`）；E2E 造檔後要
  手動 `File.SetLastWriteTimeUtc` 模擬「舊檔案」
- **SettingsWindow 與 MainWindow 各有配額/天數讀取方法**（`SettingsWindow.QuotaGbFromStore`/
  `SnapDaysFromStore` vs `MainWindow.ReadQuotaBytes`/`ReadSnapshotDays`），改設定來源時兩處要同步
- **SettingsNav 增刪分類會破 setcheck/snapcheck 的 nav 數斷言**（現為 6）：改 nav 項目時務必同步 harness
  `seNavCount/snNavCount` 預期值
- **M21 匯出浮水印雷區**：Windows 無 fontconfig 會讓 `drawtext` 「exit=0 但文字不渲染」——務必加
  `:fontfile='C\:/Windows/Fonts/arial.ttf'`；浮水印內 `:` 要跳脫為 `\:`（用 `\\:` 的 C# 字串）
- **M21 harness 產物路徑**：E2E csproj 目標含 `net10.0-windows`，Release 實跑要用
  `bin\Release\net10.0-windows\HeliVmsE2E.exe`（`bin\Release\net10.0\…exe` 是**舊產物**，會測到舊 code）
- **M21 匯出完成判定**：`.sha256` 與 ResultText 更新比 MP4 落盤晚；harness 輪詢結果以
  「ResultText 含匯出成功＋.sha256 存在」為完成條件，勿以「mp4 已出現」判斷
- **通知鍵名**：網鉤鍵是 `notify.webhook.url`（**不是** `notify.webhook_url`）；其餘同族為
  `notify.enabled`／`notify.smtp.{enabled,host,port,from,to,user,password}`——E2E／單元測試寫
  錯鍵名會「靜默無通知」（service 找無設定直接 return）
- **`SecretProtector` DPAPI CA1416**（Storage 是 net10.0 非 -windows）：呼叫處需**內聯**
  `if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException(...)`；
  包成 helper 方法／try/catch 包圍都不會被分析器認可，會直接 build error
- **`SmtpClient` obsolete**：`HeliVMS.Alarms.csproj` 已 `<NoWarn>$(NoWarn);CS0618</NoWarn>`；
  勿引 MailKit（會破壞既有 lock 檔）
- **SMTP DATA payload 是 MIME**：中文主旨/內文會 base64＋encoded-word 且可**跨行折疊**
  （`?==?\r\n =?`）；斷言不可直比中文原文，要解 base64＋併回折疊 token
  （作法見 `tests\HeliVMS.Alarms.Tests\NotificationTests.cs` 的 `DecodeMail`）
- **假 webhook 端點用 `TcpListener`**，不用 `HttpListener`（http.sys URLACL 非 admin 會檔）；
  驗重試＝前 2 次回 500、第 3 次回 200，`Hits==3` 且 `DeliveredCount==1`（Alarms 單元與 harness
  notifcheck 同款）
- **`Interlocked.Increment(ref prop)` 不合法（CS0206）**：屬性無法當 ref 用，需 backing field
  ＋ `Volatile.Read`（harness 假端點曾踩）
- **SettingsNav 現為 6 項**：setcheck（`seNavCount`）與 snapcheck（`snNavCount`）斷言已改 6；
   日後改 nav 項目務必同步 harness
- **靜默時段鍵名**：`notify.quiet.start`／`notify.quiet.end`（"HH:mm" 24h、可跨午夜、起訖相同＝停用、
   空值＝停用）；service 判定**讀 DB 裡的這兩個鍵**（不是測試自理 cfg）——單元測試要
   `Settings.Set("notify.quiet.start", …)` 寫入 DB 才會生效；env 對映 `HELIVMS_NOTIFY_QUIET_START/END`
- **`record with { QuietStart = null, … }.Method()` 曾踩 CS1003**（與 `with` 接續 method call、值含
   `null` 時 compiler 誤報「必須是 ,」）——寫法改「先存 base 變數，`(b with { … }).Method()` 或以
   變數承接」即可
- **SQLite schema 現為 v6**（`CurrentSchemaVersion=6`）：M23 加 `notification_log` 表＋
   `idx_notification_log_ts`（`CREATE TABLE IF NOT EXISTS`，v<6 才建，**勿改既有表**；
   ChannelRepository List 合併後可用來做頻道名對應）；`notification_log.ts` 用 `SqliteStore.Iso`（UTC）
- **playcheck 的 `PLAY_1X_BAD` 是環境 flaky**（ffprobe `readframes=91` vs 期望 120，1x 播放器解碼幀數
   受 CPU 滿載影響；與 schema/通知變更零交集）——playcheck 非本次改動的 gate，看到此結果不迴溯
- **純 SMTP 合併路徑**（M24）：`ProcessBatchAsync` 判定「`WebhookUrl` 空白＋`SmtpEnabled`＋
   `SmtpHost` 非空」才走 `ProcessSmtpBatchAsync` 週期合併；有 webhook（含混用）仍逐筆。batch 失敗＝
   一封失敗，逐筆按 `Attempts` 重試／落 failed（與 webhook 重試語意一致）。**排雷**：單元測試漏設
   `notify.smtp.host` 會讓 `AnyChannelConfigured=false`（host 空）→ service 開頭直接 return、事件
   永不被處理（靜默測試一度因漏設 host 逾時）
- **`SmtpNotifier` 保留單筆多載並委派 batch**：`SendAsync(cfg, AlarmEventRecord)` 呼叫
   `SendAsync(cfg, new[]{record})`——既有呼叫（M22 測試、notification check）不破；MIME 附件
   `image/png` 依副檔名，`MediaTypeNames.Application.Octet` 兜底；附件隨 `MailMessage` Dispose
- **`AlarmEventRecord` 是 class 非 record**：測試/程式**不能用 `with`** 複製（需改 helper 參數或逐欄
   構造）；「通知合併」測試先前誤用 `Event("offline") with {...}` 觸 CS8858/CS0201
- **FakeSmtpServer 的 `CountMessages` 會被 `WaitForSmtpAsync` 消費後為 0**：驗「恰一封」要
   `WaitForSmtpAsync` 取走後 `Assert.False(smtp.TryDequeueMessage(out _))`，或 harness 用
   `HasAnother`
- **M25 ptzcheck 假設備（`E2ePtzDevice`）**：Socket 級 HTTP/1.1 回 `Connection: close`＋對
  `NetworkStream` 用**同步 `stream.Read`**（`ReadAsync(byte[],int,int)` 在 net10 解析為 void 會
  CS0815）；HttpClient 會為各請求開新連線；`ReadRequestAsync` 讀到 `\r\n\r\n` 即止再補 body
- **M25 驗收錨點**：`PtzStatusText` 成功態含固定字串「PTZ 支援」；`PtzButton` AutomationId
  在主視窗按鈕列（勿於往後按鈕列重整時誤刪）；`SqliteStore.Execute` 回 void，要影響列數須用
  `SELECT changes()` 查詢
