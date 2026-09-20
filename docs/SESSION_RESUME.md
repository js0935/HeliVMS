# HeliVMS 合作恢復卡（RESUME CARD）

> 用途：對話上下文過長、影響回覆速度時，開新 session 用這份檔案接手，**不依賴舊對話記憶**。
> 權威來源：`git` 與 GitHub Actions 的現況（不是聊天記錄）。
> 所有里程碑均已 commit＋push、CI 全綠、工作目錄乾淨（下方數據皆為驗證過的真實值）。

## 一句話總結
HeliVMS 為一套**網路影像監控系統**（WPF 桌面應用：即時監看／回放／AI 事件中心／錄影排程）。
里程碑 **M1 至 M52 已全數 commit＋push、CI 綠燈**。最終 commit＝HEAD（M52 模組化分析情境）。
Release build 0 error、測試 **466/466 全過**、`git status --porcelain` **空白（工作目錄清乾淨）**、
本地與遠端完全同步（`git diff origin/HEAD` 為空）。

## 開新 session 的接手方法
```powershell
# 在 D:\HeliVMS 開新 session 時，先對新的 agent 講：
git status                    # 預期為 empty（乾淨）
git log --oneline -20         # 預期見到 M1..M52，HEAD＝M52
git diff origin/HEAD          # 預期為空（同步）
gh run list -L 3              # 預期全部 success
```
新 session 的 prompt 只需這一句：
「接續 HeliVMS（下一里程碑依 SESSION_RESUME 規劃自主選定並先寫定義），請先讀 `docs\SESSION_RESUME.md`，照指示以自主模式繼續。」
新 session 會以 git 現況接手，不會靠猜測。

## 現況快照（權威來源＝git，非聊天記憶）
- 最後 commit：`HEAD`＝**M52（`005649c`）**——模組化分析情境套件（Analytics Modules §14.7 #6 P2）（見下方 §27 M52 定義段）；全 **466**、CI `35522898383` success
- 前一個 M51 交付＝`2dea00f`（外部安全共享 Share Link／無帳號分享，見下方 §26 M51 定義段）、全 **425**、CI `35294863720` success
- 前一個 M50 交付＝`d43c058`（企業身份整合 OIDC SSO 核心＋LDAP 設定面，見下方 §25 M50 定義段）、全 **400**、CI `35293174402` success
- 前一個 M48 交付＝`2f6508a`（魚眼矯正 Dewarping，見下方 §23 M48 定義段）、全 **308**、CI `35287346481` success
- 前一個 M47 交付＝`f3c01e1`（警報管理器 Alarm Manager，見下方 §22 M47 定義段）、全 **299**、CI `35286083642` success
- 前一個 M46 交付＝`c7f6e8f`（錄影遮蔽 Redaction，見下方 §21 M46 定義段）、全 **293**、CI `35284550851` success
- 前一個 M45 交付＝`ef3a8bc`（備份與異地備援，見下方 §20 M45 定義段）、全 **289**、CI `35263453271` success
- 已驗收：**M52＝模組化分析情境套件（Analytics Modules）**（§14.7 #6 P2，見下方 §27 定義段）
- 前一個 M38 交付＝`d09b045`（事件回應工作流，見下方 M38 定義段）、全 **172**、CI `35185639491` success
- 前一個 M30 交付＝`24ad4c7`（MQTT 通知通道）：`NotificationSettings`＋
  `MqttEnabled/MqttHost/MqttPort(1883)/MqttTopic/MqttUser/MqttPassword`（鍵 `notify.mqtt.*`、
  env `HELIVMS_MQTT_*`、密碼 SecretProtector）；（快照 docs commit `dfdd551` 已含 M30 定義段）
  `{"channel_id",..,"event_type","start_utc","detail"}`）；`NotificationService` 第三通道
  （`HasMqttRoute` guard＋route "mqtt"＋log Route "webhook+smtp+mqtt"）；SettingsWindow 通知頁
  MQTT 群（啟用/主機/埠/主題/使用者/密碼，密碼不預填＋僅非空才寫）。單元 **125/125**
  （Storage 53＋Alarms 56＋Licensing 8＋Devices 8；Alarms ＋5＝mqtt settings load、
  publish ok＋payload JSON、CONNACK 拒絕→false、連不上→false、webhook+mqtt 複合 route）＋
  E2E 新增 **`mqttcheck` MQTTCHECK_OK**（Program 內 FakeMqttBroker）。回歸 12 全綠
  （notif/log/smtp/set/snap/exp/ptz/tra/quiet/evfilter/off/**mqtt**）。
- 前一個 M29 交付＝`6e95ba8`（離線事件源：`OfflineEventTracker` MarkOffline/MarkOnline/
  CloseOpenAtStartup；`AlarmEventRepository.FindOpenOffline+CloseOpenEvents`；ChannelManager
  Reconnecting→開窗、Streaming→補 online、健康逾時開窗＋RestartAsync 回 bool；Storage 53、
  Alarms 51、全 120、offcheck OFFCHECK_OK）
- 前一個 M28 交付＝`ba95d0f`（事件中心篩選＋分頁：QueryArgs/ListByQuery/CountByQuery/
  ListEventTypes＋UI 類型 Combo＋Prev/Next 50/頁；114、evfiltercheck EVFILTERCHECK_OK）
- 分支／遠端：`git diff origin/HEAD` 為空（完全同步）；HEAD＝origin
- CI：`gh run list` 顯示最新 run `conclusion=success`；建置 0 error、測試 293/293

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
     **`ptzcheck`（M25，PTZCHECK_OK）**、**`tracheck`（M26，TRAYCHECK_OK）**、
     **`ptzadvcheck`（M31，PTZADVCHECK_OK）**、**`mqttcheck`（M30，MQTTCHECK_OK）**、
     **`pushcheck`（M34，PUSHCHECK_OK）**、**`quietcheck`（M27，QUIETCHECK_OK）**、
     **`evfiltercheck`（M28，EVFILTERCHECK_OK）**、**`offcheck`（M29，OFFCHECK_OK）**、
     **`snmpcheck`（M36，SNMPCHECK_OK）**、**`rulescheck`（M37，RULECHECK_OK）**、
     **`dispcheck`（M38，DISPCHECK_OK）**、**`tampercheck`（M39，TAMPERCHECK_OK）**、
     **`mapcheck`（M41，MAPCHECK_OK）**
   - `setcheck`/`snapcheck` 的設定中心 nav 斷言＝**10**（M42 增「身份」頁後）；
     `authcheck`（M42，AUTHCHECK_OK）以 Win32 keybd_event 鍵入密碼（PasswordBox 無 ValuePattern）
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
1. **M26 已完成**（系統匣常駐，見「現況快照」）
2. **M27 已完成**（commit `c198884`，CI success）：通知靜默時段延時補送
   `notify.quiet.retransmit`（off＝維持原跳過）；`NotificationSettings` 加
   `QuietRetransmit`＋`QuietEndUtc(now)`（跨午夜兩型）；`NotificationService` 延後佇列
   （`DelayedForQuiet` 旗標、`NextDueUtc=QuietEndUtc+5s`、retry loop 於「非靜默時
   DelayedForQuiet 且已非靜默」立即視為到期→提早結束靜默立即補送）；成功 log
   `note="延後補送"`（保留供 E2E/單元斷言）；SettingsWindow 通知頁 `NotifyQuietRetransmitBox`；
   Alarms ＋2 測試（webhook 延後、SMTP batch 延後）、Storage/Alarms 全 111；
   harness `quietcheck`→**QUIETCHECK_OK**；回歸 8 全綠
3. **M28 已完成**（commit `ba95d0f`，CI success）：事件中心篩選＋分頁——`AlarmEventRepository`
   加 `QueryArgs/ListByQuery/CountByQuery/ListEventTypes`；EventCenterWindow 頂加「類型」Combo、
   底部 `PrevPageButton/PageText/NextPageButton`（PageSize=50、筛选變動歸零、Count 顯示第 x/y 頁）；
   Storage ＋3 測試；harness `evfiltercheck`→**EVFILTERCHECK_OK**；回歸全綠；全 114/114
4. **M29 已完成**（commit `6e95ba8`，CI success）：離線事件源（斷線補送）——見「現況快照」；
   Storage ＋2（FindOpenOffline、CloseOpenEvents）＋Alarms ＋4（OfflineEventTracker 開窗防重複/
   復連補 online＋duration detail/無開窗 noop/startup close）＝全 **120**；
   harness `offcheck`→**OFFCHECK_OK**；回歸 11 全綠
5. **M30 已完成**（commit `dfdd551`，CI success）：MQTT 通知通道——見「現況快照」＋Alarms
   `MqttNotifier`（裸 MQTT 3.1.1、無第三方依賴）、`NotificationService` 第三 route
   （複合 log Route "webhook+smtp+mqtt"）、SettingsWindow 通知頁 MQTT 群（密碼不預填）；
   Alarms ＋5＝**56**、全 **125**（Storage 53＋Alarms 56＋Licensing 8＋Devices 8）；
   E2E `mqttcheck`→**MQTTCHECK_OK**（Program 內 FakeMqttBroker：CONNECT→CONNACK rc0→收 PUBLISH
   驗 topic＋JSON）；回歸 12 全綠含 off/evfilter/quiet
   - 現況：通知平面（M22）已有 Webhook＋SMTP 兩通道，無 MQTT。M30 補 **MQTT 3.1.1 通道**：
   - **NotificationSettings**：＋`MqttEnabled/MqttHost/MqttPort(=1883)/MqttTopic/MqttUser/
     MqttPassword`（鍵 `notify.mqtt.enabled/host/port/topic/user/password`、env
     `HELIVMS_MQTT_*`，password 以 DPAPI 加密保存）；`AnyChannelConfigured` 納入
     （`MqttEnabled && host && topic`）
   - **Alarms 新 `MqttNotifier`**（**裸 MQTT 3.1.1**，不引 NuGet——自寫 TCP socket 組
     CONNECT/PUBLISH frame，CI 無第三方網路）：`SendAsync(cfg, record)`＝TCP 連線→CONNECT
     （clientId `helivms-<rand>`、clean session 1、選填 user/pass）→讀 CONNACK（回碼 0 才算）
     →PUBLISH QoS0（topic＋JSON payload：
     `{"channel_id":N,"event_type":"motion","start_utc":"<ISO>","detail":"..."}`）→true/false
   - **NotificationService**：`ProcessItemAsync` 第三route：`cfg.MqttEnabled && host && topic`
     → `routes.Add("mqtt")`＋`_mqtt.SendAsync`；route join "webhook+mqtt+smtp" 照既有
   - **SettingsWindow 通知頁**：加 `NotifyMqttEnabledBox`＋Host/Port/Topic/User/Password 盒群
     （load/apply；密碼留空＝不變更，與 SMTP 同原樣）
   - 驗收：Alarms ＋4（Settings load mqtt、MqttNotifier publish ok＋payload JSON、
     CONNACK 拒絕→false、Service 多路 route 含 "mqtt+webhook"）＝全 **124**；App Release
     build 0；E2E `mqttcheck`→**MQTTCHECK_OK**（Program.cs TCP listener 假 broker：
     CONNECT→CONNACK code 0→收 PUBLISH 驗 topic＋payload `channel_id`）；回歸全組含 off
   - 雷區預告：MQTT fixed header 2 bytes（type<<4＋remaining len）；varint remaining 長度
     encoding（<128 單 byte）；CONNECT 需 `ProtocolName "MQTT"/Level 4`；假 broker 必須回
     CONNACK（0x20 0x02 0x00 <rc>）否則 notifier 判定失敗；不要混淆 `record` namespace
     （AlarmEventRecord 有 Record）；密碼 key 用 SecretProtector（比照 smtp.password）
6. **M31 已完成**（commit `20e7c94`，CI success）：PTZ 增強——
   服務端（23813fd）`OnvifDeviceService` ＋3：`AbsoluteMoveAsync`（AbsoluteMove body `/tptz:Action`）、
   `HomeAsync`（tptz `<Home>`）、`RemovePtzPresetAsync`（tptz `RemovePreset`
   ProfileToken＋PresetToken）；App `PtzWindow` 以 **M25 原結構為基底**（`5b2b2c3`）補回
   M31 UI：方向/變焦 10 鈕加長按
   `PreviewMouseLeftButtonDown="OnMoveHeldDown" PreviewMouseLeftButtonUp="OnMoveHeldUp"
   MouseLeave="OnMoveMouseLeave"`（鬆開/滑離→`StopMoveAsync`；`OnMoveHeldDown` 為真正長按
   迴圈：按住期間每 300ms 持續 `ContinuousMoveAsync`，`_ptzHeld=false` 即停）、
   ＋`PtzHomeButton`→`HomeAsync`、`PtzDeletePresetButton`→`RemovePtzPresetAsync`（選中項）；
   Devices ＋3 單元＝**11**、全 **128**（Storage 53＋Alarms 56＋Licensing 8＋Devices 11）；
   App Release 0 error；harness `ptzadvcheck`→**PTZADVCHECK_OK**（服務層
   AbsoluteMove/HomePosition/RemovePreset 三 action＋UI `Native.MouseDown` 長按 PtzUpButton：
   按住期間 ContinuousMove≥2 且鬆開收到 StopPtz──假設備 action 用 element name 匹配
   `Contains("Home")/("AbsoluteMove")/("RemovePreset")`，勿用 `<tptz:` prefix（XElement
   送 default namespace））；回歸含 ptzcheck
   - **排雷（20e7c94 收斂）**：M31 前三次 commit（23813fd/16d8dd2/a1a1d84）的 XAML 巢狀
     MC3089 從未編譯過；修法＝**不以 bash 重寫整檔**，`git checkout 5b2b2c3`（M25 綠版）
     拿乾淨基底→行號精準插 2 行自閉按鈕→.cs 補 handler；`OnMoveHeldUp` 用
     `MouseButtonEventArgs` 無法同時綁 `MouseLeave`（CS0123，需 `MouseEventHandler`）——
     拆 `OnMoveHeldUp(object,MouseButtonEventArgs)`（PreviewMouseLeftButtonUp）與
     `OnMoveMouseLeave(object,MouseEventArgs)`（MouseLeave）共用 `StopMoveAsync`
7. **M32 進行中＝ONVIF Discovery 測試補完**（refactor 已備、8 測試已跑綠，尚未 commit）：
   - 背景：`DiscoveryClient`（`src\HeliVMS.Devices\Onvif\DiscoveryClient.cs`）自 M20 已有完整
     WS-Discovery 實作（單一 multicast `239.255.255.250:3702` Probe、收 ProbeMatch、依
     `HttpXAddr` 去重、`<types>`/`<scopes>` 解析），`OnvifWizardWindow` 已用
     `DiscoverAsync(5s)`＋IP fallback；但 **tests 完全零覆蓋**，且硬編碼 multicast 無法在 CI
     測
   - refactor（public 簽名不變）：將真正監聽邏輯抽為
     `internal ProbeAsync(UdpClient udp, IPEndPoint endpoint, TimeSpan timeout, ct)`——端點與
     socket 皆可注入；multicast （TTL=1）＋`EnableBroadcast` 留在 `DiscoverAsync`；`ParseMatch`
     由 private→**internal**（供解析層直接驗證）
   - 新 `DiscoveryClientTests`（+8）：ParseMatch 6 例（欄位擷取含 `HttpXAddr`/`NameHint`、
     壞 XML 忽略、無 ProbeMatch 忽略、少 EndpointAddress 跳過、無 http(s) XAddr 跳過、同
     HttpXAddr 去重後到覆蓋）＋ProbeAsync 2 例（loopback 假 peer 回 ProbeMatch→解析回傳；
     假 peer 不回→逾時回空）
   - 驗收：Devices 11→**19**、全 **136**（Storage 53＋Alarms 56＋Licensing 8＋Devices 19）；
     App Release 0 error；其餘 tests 不破
   - 雷區預告：**「MulticastTimeToLive 設在非 multicast socket 於 CI（Windows）拋
     SocketException」**——所以 multicast 設定只在 `DiscoverAsync`、測試一律注入自備
     UdpClient（loopback unicast，不設 TTL）
8. **M33 進行中＝多 Profile 選擇 UI**（實作已完、harness PROFCHECK_OK，尚未 commit）：
   - 背景：`PtzWindow` 自 M25 起 `_profileToken = profiles.FirstOrDefault()`（硬取第一個）；
     多 Profile 設備（主/子碼流）只能操控第一個
   - App `PtzWindow`：Row0 狀態列下加 `PtzProfileCombo`（AutomationId `PtzProfileCombo`，
     ItemTemplate 顯示「{Name}（{Token}）」`SelectionChanged="OnProfileChanged"`）——
     切換後 `_profileToken`＝所選、`RefreshPresetsAsync()`（預設點 per-profile）、狀態列示
     「已切換 Profile：{Token}」；`_profiles` 欄位、`SetControlsEnabled` 納入 ComboBox
   - harness `profcheck`→**PROFCHECK_OK**：`E2ePtzDevice` 加 `ProfilesXml`（預設單 profile
     保 ptzcheck 之 `profiles.Count==1`）＋`MultiProfilesXml`（Main/Sub）＋`LastProfileToken`
     （regex 解析 ptz body 內 ProfileToken）＋`ResetCounts()`；direct 驗 GetProfiles=2 ＋
     ContinuousMove 帶 SubProfile；UI 開 PtzWindow→combo Expand→找 Text「子碼流（SubProfile）」
     →TreeWalker 取 ListBoxItem→`SelectionItemPattern.Select()`→Collapse（防 popup 蓋住按鈕）
     →長按 PtzUpButton→`LastProfileToken=="SubProfile"` 且 MoveCount≥2
   - 排雷：UIA 下 ComboBox item 的 Name 是 item ToString（`HeliVMS.Devices.Onvif.OnvifProfile`）
     非顯示文字——需抓 ItemTemplate 內 Text block；選完**必須 Collapse** popup，否則蓋住
     按鈕、MouseDown 打不到（holdDelta=0）
   - 附帶修 harness 脆弱點：ptzcheck/ptzadvcheck/profcheck 在 `channels` 空表時
     `ChannelRepository.Add` 自建測試頻道（取代直接 BAD「no channels」——主 DB 可能被清空）
   - 驗收：App Release 0 error；ptzcheck/ptzadvcheck/profcheck 三健；單元全 **136** 不破
9. **M34 進行中＝Web Push 推播（RFC 8030/8292）**（實作＋測試＋pushcheck 已完成，尚未 commit）：
   - 背景：候選「推播通道」在「無第三方＋CI 可驗」約束下落地為 Web Push 提交端
     （RFC 8030）；VAPID JWT（RFC 8292）用 .NET BCL `ECDsa` P-256 自簽，不需 NuGet；
     endpoint 可為任何自選訂閱端點（無外部商業服務依賴），CI 用本機假端點完整驗證
   - `PushNotifier`（Alarms）：`GenerateKeyPair()`（公鑰＝65B uncompressed point 之
     base64url，RFC 8292 格式；私鑰＝PKCS#8 DER base64）、`CreateVapidJwt()`（ES256：
     header+claims（aud=endpoint origin、exp=+12h、sub=mailto:）+raw R||S 64B 簽名）、
     `SendAsync` POST endpoint＋headers `TTL:60`/`Urgency:normal`/`Authorization: vapid t=,k=`
     /`Content-Encoding: identity`；body 同 MQTT 事件 JSON（channel_id/event_type/start_utc/detail）
   - 關鍵發現：本環境 **.NET 10 的 `ECDsa.SignData(byte[], HashAlgorithmName)` 直接回傳
     raw IEEE P1363（64B）而非 DER**（先前實測確認）——恰好就是 JWS ES256 所需的格式，
     免去 DER→raw 轉換
   - Settings：`notify.push.enabled/endpoint/public_key/private_key`（私鑰 DPAPI 保護）、
     `HasPushRoute`（enabled＋endpoint＋私鑰）；SettingsWindow 通知頁加推播群，
     「套用」時 endpoint 非空且無現有私鑰 → 自動 `GenerateKeyPair()` 落庫
   - NotificationService：`ProcessItemAsync` 加 push route（複合 route 會併 `<x>+push`）
   - 測試（NotificationTests +7）：Load 解析＋DPAPI 解私鑰、HasPushRoute 三要件、
     VAPID JWT ES256 簽章以公鑰驗證（aud/sub claim）、PushNotifier POST 帶
     `vapid t=.., k=..`＋TTL＋body（FakeHttpServer 擴充記錄 headers）、410→false、
     缺私鑰→false、Service webhook+push 複合 route；Alarms **56→63**、全 **143**
   - harness `pushcheck`→**PUSHCHECK_OK**（比照 mqttcheck）：本機 TcpListener 假 push
     端點收 POST＋驗 Authorization `vapid t=`+`k=`＋body channel_id/event_type＋delivered=1
   - 附帶：Signature 驗證測試因應 .NET 10 raw 簽章，改用直接驗 raw；不引入 DER helper
   - 驗收：App Release 0 error、Alarms 63/63、全 **143**、mqttcheck/pushcheck 雙健、
     CI 綠
10. **M35 已驗收＝ONVIF Discovery Hello/Bye/Resolve**（commit `0493612`，CI `35165622598` success）：
   - M32 只補 Probe 的測試；本里程碑補 WS-Discovery 其餘三訊息
     （對 HeliVMS 之價值：Hello/Bye 可作設備上線/離線公告之送出；Resolve 可精確查詢
     特定 InstanceId 設備在位與取得 XAddrs）
   - `DiscoveryClient` 新增：`SendHelloAsync(instanceId, xAddrs, types?, scopes?)`、
     `SendByeAsync(instanceId)`（皆多播 TTL=1）與 `ResolveAsync(instanceId, timeout)`
     （發 Resolve、收集 ResolveMatch、比對 EndpointReference==urn:uuid:{id}）
   - internal 注入面（測試可用 loopback 假設備/自備 UdpClient）：
     `SendHelloAsync(UdpClient, IPEndPoint, …)`、`SendByeAsync(UdpClient, IPEndPoint, …)`、
     `ResolveAsync(UdpClient, IPEndPoint, instanceId, timeout, ct)`；
     `BuildHello/BuildBye/BuildResolve`、`ParseResolveMatch`；抽共通
     `SendDiscoveryMessageAsync`（SendAsync 送包，套 try/catch）
   - 測試（DiscoveryClientTests +5，沿用 Probe 之 loopback round-trip 模式）：
     SendHello 驗對端 XML（EndpointReference/Address=urn:uuid:、XAddrs、MetadataVersion=1）、
     SendBye 驗對端 XML、Resolve 發送（對端驗 Action=…/Resolve＋含 instanceId）
     ＋回 ResolveMatch 之解析（含 NameHint、HttpXAddr）、Resolve 收到非本機 instanceId
     之 ResolveMatch 忽略→null、Resolve 無回應逾時→null
   - 排雷：測試對 XNamespace 之用法 hack（`"…" + "ElementName"` 字串拼接）在 C# 是
     string concat 非 XNamespace→改用 `Wsa＋"…"/Wsd＋"…"` 靜態欄位；public API XML
     註解需完整 `<param>`（CS1573 視為 error）
   - 快照：Devices 19→**24**、全 **148**（Storage 53＋Alarms 63＋Licensing 8＋Devices 24）
   - 沿用 M32 先例：Discovery 純 UDP client 無 UI 面，不加 harness/UiCheck，
     以 loopback 單元 round-trip 為 CI 驗證主體
   - 驗收：App Release 0 error、Devices 24/24、全 148、CI 綠
11. **M36 已驗收＝SNMP 陷阱**（commit 待補）：
    - 無第三方下以裸 UDP 手編 BER 送 SNMPv2c trap：message＝
      `0x30{ version=1(0x02)＋community(0x04)＋SNMPv2-Trap PDU(0xA7) }`
    - `SnmpTrapSender`（Alarms）：`SendAsync(cfg, record)`（UDP 送出即成功）；internal
      `BuildTrap(community, record)`（可直接驗 wire format）；varbind 五筆：
      sysUpTime.0（TimeTicks `0x43`，值 0）、snmpTrapOID.0（事件 OID
      `1.3.6.1.4.1.99999.0.1`）、`…2.1`=channel_id(Int32)、`…2.2`=event_type(String)、
      `…2.3`=detail(String)；`ParseOid` internal
    - `NotificationSettings`：`SnmpEnabled/SnmpHost/SnmpPort(162)/SnmpCommunity(public)`，
      鍵 `notify.snmp.enabled/host/port/community`、env `HELIVMS_SNMP_*`；
      `HasSnmpRoute = enabled && host 非空`；`AnyChannelConfigured` 併入
    - `NotificationService` 第四/五 route 化（webhook/smtp/mqtt/push/snmp，複合 route
      會併 `<x>+snmp`）；`SettingsWindow` 通知頁加 SNMP 群（啟用/主機/埠/Community，
      AutomationId `NotifySnmpEnabledBox/HostBox/PortBox/CommunityBox`）
    - 測試（NotificationTests +6＋helper ReadTlv/DecodeInteger/DecodeOid/ParseTrap，
      對 Fake UDP receiver 收包做 mini BER decode、缺 host false、複合 webhook+snmp route、
      Load 解析、HasSnmpRoute 二要件）；Alarms **63→68**、全 **153**
      （Storage 53＋Alarms 68＋Licensing 8＋Devices 24）
    - harness `snmpcheck`→**SNMPCHECK_OK**（本機 UDP 收包驗 community＋`…99999.2.1` OID
      位元組序列＋delivered=1；OID 99999 之 base-128 末位是 `0x1F` 非 `0x9F` 易算錯）
    - 回歸：snmpcheck/pushcheck/mqttcheck/ptzcheck 全 OK
    - 驗收：App Release 0 error、Alarms 68/68、全 **153**、CI 綠
12. **M37 已驗收＝告警規則層**（commit `ba1636b`，CI `35175424681` success）：
    - 背景：多通道建好後需「規則白名單」——命中規則事件僅走指定通道，多規則第一命中優先
    - `AlertRule`（Storage）：`Id/Name/EventType/ChannelId/Keyword/Channels/Enabled`；
      `alert_rules` 表為 schema **v7**（`CurrentSchemaVersion` 6→7、
      `CreateAlertRulesTableV7` 遷移接線）
    - `AlertRuleRepository`（Storage）：`Add/ListAll/ListEnabled/SetEnabled/Delete`
    - `AlertRuleMatcher`（Alarms）：`Match(record)`＝先 ListEnabled 後比對（規則條件全 AND：
      EventType/ChannelId/Keyword，Keyword 比對型別或 Detail）、`Matches`、
      `ParseChannels`（逗號分隔 route 白名單）、`ParseOid`（分號複合）
    - `NotificationService` ctor 加 `AlertRuleRepository? ruleRepo`（MainWindow 以
      `ruleRepo: new AlertRuleRepository(_store)` 接線）；`Allow(rule, route)`——命中規則且
      Channels 非 null ⇒ 僅白名單通道（webhook/smtp/mqtt/push/snmp 五 route 各加 `&& Allow`），
      未命中或無規則維持全通道；`RecordAllows` 空白時視為「規則命中但未限制」仍全通道
    - SettingsWindow 規則頁（nav 第 7 項「規則」）：`PageRules`＋`RuleNameBox/
      RuleEventTypeBox/RuleChannelCombo/RuleKeywordBox`＋五個 `RuleChannel*Box` CheckBox
      （Webhook/SMTP/MQTT/推播/SNMP）＋`AddRuleButton/ToggleRuleButton/DeleteRuleButton/
      RuleList`；code-behind `ReloadRules/ReloadRuleChannels/OnAddRuleClicked/
      OnToggleRuleClicked/OnDeleteRuleClicked`
    - 測試：Storage `AlertRuleRepositoryTests` **53→58**（Add/ListAll/ListEnabled/SetEnabled/Delete）；
      Alarms `AlertRuleMatcherTests`＋`NotificationTests` 新 2（Service_RuleWhitelist_WebhookOnly、
      Service_RuleNotMatching_SendsAllRoutes）**68→78**；全 **168**
    - harness `rulescheck`→**RULECHECK_OK**：(A) Service 層——temp DB 設
      `notify.webhook.url`＋`notify.snmp.*`、`AlertRuleRepository.Add("motion 只走 webhook",
      "motion", null, null, "webhook")`、`new NotificationService(store, ruleRepo:…)` enqueue
      motion 事件→驗 `DeliveredCount==1` 且收 webhook body 含 `"channel":7`/`"type":"motion"`
      （否 `snmpSilent==true`）(B) UI 層——`--settings` 開設定中心→nav 規則項 Select→
      `RuleNameBox`/`RuleEventTypeBox` SetValue＋`RuleChannelWebhookBox` Toggle→
      `AddRuleButton` Invoke→重開 SqliteStore 讀 `ListAll()` 有 `peak-only`＝
      `person/webhook/True`
    - 排雷：規則頁 `PageRules` 是 StackPanel 無 AutomationPeer——`RuFind("SettingsPageRules")`
      得 null（UIA 找不到 page）但子元素都在，conditions 不應以 page 為必要（見雷區）；且
      規則 harness 開 App 用「設定中心」窗標籤＋enum `GetWindowTextW` 含「設定中心」判斷 focus
    - 回歸影響：設定中心 nav 由 6→**7**（新增「規則」頁）——`setcheck`/`snapcheck` 之
      `navCount==6` 硬期望須同步改 **7**（已更新 harness）；回歸組全綠
      （set/snap/snmp/push/mqtt/ptz/notif/log/smtp/exp/off/evfilter/quiet）
    - 驗收：App Release 0 error、Storage 58/58、Alarms 78/78、全 **168**、CI 綠
13. **M38 已驗收＝事件回應工作流**（commit `d09b045`，CI `35185639491` success，§14.4）：
    - 背景：事件中心原本僅 `acknowledged` 位元（已確認/未確認），缺四態、指派、備註與軌跡
    - Storage schema **v8**（`CurrentSchemaVersion` 7→8、`CreateEventDispositionsTableV8` 遷移接線）：
      - `event_dispositions(event_id PK REFERENCES alarm_events ON DELETE CASCADE, status TEXT
        DEFAULT 'pending', assigned_to TEXT, note TEXT, updated_at TEXT)`
      - `event_disposition_trail(id PK AUTOINCREMENT, event_id, status, assigned_to, note,
        changed_at)`＋`idx_disp_trail_event`
      - **設計決策**：用新表而非 `ALTER TABLE alarm_events`——`Initialize()` 對新庫會依序跑所有
        `version<N` 遷移，ALTER 加欄會 duplicate column
    - `AlarmEventStatus`（Storage）：四態代碼 `pending/acknowledged/actioned/false_alarm`、
      `All`、`Label()`（待處理/已確認/已處理/誤報）、`IsValid()`
    - `EventDispositionEntry`（Storage）軌跡列；`AlarmEventRecord` 加 `Status`（預設 pending）/
      `AssignedTo`/`Note`
    - `AlarmEventRepository`：`SetDisposition(id,status,assignedTo,note,changedAtUtc)`（UPSERT
      dispositions ＋寫 trail ＋同步 legacy `acknowledged`＝status≠pending）、
      `ListDispositionTrail(eventId)`（舊→新）、`QueryArgs.Status` 篩選
      （`COALESCE(d.status,'pending')`）；`ListByQuery/ListByRange/CountByQuery` SELECT 改
      LEFT JOIN `event_dispositions`；`Acknowledge` 保留舊簽章但同步 status＋trail
    - `EventCenterWindow`：底部處置列 `DispositionCombo`（四態）＋`AssignBox`/`NoteBox`＋
      `ApplyDispositionButton`；`TrailText` 顯示「共 N 筆｜最近 …」；清單「確認」欄改「狀態」＋
      新增「指派」欄；`EventRow` 加 `StatusLabel/StatusBrush/AssignedTo/Note`；選取後帶入編輯器
      （`_selectedId` 保留、刷新後重選）；`SelectedRow`（卡片/網格兩模式通用）
    - App：`--events` 啟動參數（供 harness 自動開事件中心，比照 `--settings`）
    - 測試：Storage ＋4（`SetDisposition_DefaultsPending_ThenPersistsAndTrails`、
      `SetDisposition_AppendsTrail_AndQueryFiltersByStatus`、
      `SetDisposition_PendingClearsAcknowledged_AndRejectsInvalidStatus`、
      `Acknowledge_SyncsStatusAndTrail_AndKeepsAssignment`）**58→62**；全 **172**
    - harness `dispcheck`→**DISPCHECK_OK**：(A) Service——temp DB 預設 pending、SetDisposition
      四態＋指派＋備註、trail 2 筆、`QueryArgs.Status` 篩選（actioned/acknowledged/false_alarm）
      (B) UI——App DB seed 一筆 event、`--events` 開「事件中心」、`DispositionCombo/AssignBox/
      NoteBox/ApplyDispositionButton/TrailText/EventList` 存在、選取列後按鈕 enabled、SetValue
      指派/備註、Invoke 套用→重讀 DB 驗 trail>=1（combo 展開選項 UIA 不易操作，狀態切換以
      service 層驗證；UI 套用路徑以「指派 alice＋trail」證實）
    - 排雷：WPF `ListView` 的列 UIA peer 為 **`ControlType.DataItem`**（非 ListItem）——
      `FindAll(ControlType.ListItem)` 得 0，須用 `OrCondition(ListItem, DataItem)` 或
      `ItemContainerPattern`；且該列 peer 的 `Name` 是型別名
      （`HeliVMS.App.EventCenterWindow+EventRow`）而非顯示文字
    - 回歸：全 172（Storage 62＋Alarms 78＋Licensing 8＋Devices 24）；set/snap/snmp/push/mqtt/
      ptz/notif/log/smtp/exp/off/evfilter/quiet 全綠
    - 驗收：App Release 0 error、Storage 62/62、Alarms 78/78、全 **172**、CI `35185639491` success
14. **M39 已驗收＝遮蔽偵測（Tamper）**（commit `0bc2ec1`，CI `35220667530` success）：
    - 背景：專業 NVR 基本警報「鏡頭被遮／被移／被噴漆」；L0 以純 CPU 幀分析即可落地（ARCHITECTURE §14.4 功能表 P1）
    - Alarms 新 `TamperDetector`（`src\HeliVMS.Alarms\TamperDetector.cs`）：16×16 灰階網格
      （`FrameDifferenceMotionDetector.DownsampleToGrid`）算**平均亮度**與**邊緣能量**
      （`EdgeEnergy`＝水平＋垂直平均梯度）；閾值黑 12／白 243 直接判定 Blackout/Whiteout；
      「被遮（Covered）」與**暖機 8 幀建立的邊緣基準**比較（`edge <= baseline*0.2`，
      `MinBaselineEdge=6` 防止低紋理場景誤判）；基準只用「正常」幀累積（暖機後以
      `EMA 0.98/0.02` 慢速跟隨場景變化，避免遮蔽狀態污染基準）；`Reset()` 清基準
    - Alarms 新 `TamperEventEngine`（`src\HeliVMS.Alarms\TamperEventEngine.cs`）：
      連續 **6 幀同類遮蔽**才開窗（防瞬時誤報）；遮蔽中止且過 **1500ms 冷卻**後結算
      （收到正常幀才觸發結算，`MinEventMs=300` 以下捨棄）；結算寫一筆 `alarm_events`
      `event_type='tamper'`（`Insert`＋`UpdateEnd`，與 Motion/Ai 引擎同款），detail=
      `kind={blackout|whiteout|covered};brightness=..;edge=..;duration=..ms`，並把**首幀**
      存 BMP 快照 `tamper-{channelId}-{armedUtc:HHmmss}.bmp`（`BmpSnapshotWriter.Save`，
      IO Exception 吞掉僅不打快照）；事件 `EventInserted`（寫庫後）、`TamperSignal`（開窗
      rising edge，UI 用）；`Flush()`（停止前立即結算未關窗口）、`Reset()`（斷線重連/切頻道
      直接捨棄進行中窗口）、`Dispose()=Flush()`
    - App 接線：
      - `SettingsWindow` 功能頁（第 3 頁）加 `TamperEnabledBox`（AutomationId
        `TamperEnabledBox`，Toggle 即寫 `app_settings["detect.tamper.enabled"]`；
        `ReloadTamper` ctor 載入）——**不新增 nav 項目**（setcheck/snapcheck nav 維持 7）
      - `ChannelManager` 開新 session 時 `IsTamperEnabled()` 讀該鍵（重新連線即生效）
      - `ChannelSession` ctor 加 `bool tamperEnabled = false`；`OnClientFrame` 對
        tamper 引擎**每 ~250ms 抽一幀**（`_motionClock` 節流）；`DisconnectAsync/Reset/Dispose`
        補 `_tamper?.Flush()/Reset()/Dispose()`
      - `EventCenterWindow` 事件顏色加 `"tamper" => Brushes.MediumPurple`
    - 測試：**Alarms 78→91**（`TamperDetectionTests` ×7：Blackout/Whiteout 瞬時、正常幀建
      基準、Covered 急降、低紋理不誤判、Reset 清基準、EdgeEnergy 均勻 0／棋盤正；
      `TamperEventEngineTests` ×6：持續黑→事件＋快照檔存在、5 幀中斷→無事件、過冷卻
      Checker 收尾→單事件、Reset 捨棄窗口、Whiteout kind、KindText 對映）；全 **185**
      （Storage 62＋Alarms 91＋Licensing 8＋Devices 24）
    - harness `tampercheck`→**TAMPERCHECK_OK**：(A) Service——temp DB＋temp 快照目錄：
      belowThreshold（5 幀黑＋Checker→0 事件）＋blackout（6 幀黑→恰 1 筆 tamper、
      `kind=blackout`、快照檔存在）(B) UI——App DB `detect.tamper.enabled="false"`
      →`--settings` 開設定中心→nav「功能」→`TamperEnabledBox` Toggle→重開 SqliteStore 讀
      `detect.tamper.enabled=="true"`（測完復原 false）
    - 排雷／設計決策：抽幀由呼叫端（ChannelSession 250ms）負責，引擎不限頻率；開窗後首幀
      像素才 Clone（長窗節省記憶體）；基準更新限定「正常」幀防污染
    - 回歸 13 全綠：set/snap/snmp/push/mqtt/ptz/notif/log/smtp/exp/off/evfilter/quiet
    - 驗收：App Release 0 error、全 **185**、CI `35220667530` success
15. **M40 已驗收＝警報 IO（DI/DO 乾接點，Modbus/TCP 網路 IO 模組）**（§16.2 完整設計落地「核心」，全 **210**，CI success）：
    - 背景：外部物理感測（門磁/煙霧/紅外主機乾接點）與執行器（聲光/鎖門）融入事件引擎；
      第一落地協議＝**Modbus/TCP**（無第三方，手編 MBAP+PDU，CI 用本機假模組 loopback 驗）
    - Storage schema v9（`CurrentSchemaVersion` 8→9、`CreateIoTablesV9`）：`io_devices(id PK, name,
      protocol DEFAULT 'modbus_tcp', host, port 502, unit_id 1, enabled 1, poll_ms 500)`＋`io_channels(id PK,
      device_id REFERENCES io_devices ON DELETE CASCADE, direction CHECK('DI'/'DO'), io_index, name, enabled 1,
      debounce_ms 200, polarity 0, camera_id, alarm_priority 'normal', UNIQUE(device_id,direction,io_index))`
      （DI／DO 的 index 是不同位址空間，故 UNIQUE 含 direction）
      **啟用 `PRAGMA foreign_keys=ON`**（ctor 每連線一次，CASCADE 才生效；因此 io_input 事件必須綁定相機，
      camera_id NULL 時不寫事件僅送 StateChanged——FK 防護）
    - Storage 新 `IoRepository`：device/channel 完整 CRUD＋`ListChannels(deviceId,direction,enabledOnly)`；
      `IoDevice/IoChannel` records（camera_id 可 null）
    - Alarms 新 `ModbusTcpClient`（手寫 binary）：FC02 ReadDiscreteInputs／FC01 ReadCoils／FC05 WriteSingleCoil、
      MBAP（TransactionId 隨機/ProtocolId=0/Length/UnitId）＋PDU、回應 echo 比對、byteCount 驗證；
      例外 frame `fc|0x80` throw；靜態 Build/Parse 供測試、`OpenAsync`→`StreamHolder`
    - Alarms 新 `IoDeviceMonitor`（IDisposable，Timer 週期 poll `poll_ms`）：`RefreshChannels()` 載入啟用 DI；
      去抖狀態機（polarity 反相、raw==候選且維持≥debounce_ms 才切換、首次採樣即基準）；
      **升沿** `Insert(channelId=cameraId,"io_input")` detail=`device=..;io=..;index=..;priority=..;state=on`
      ＋`EventInserted`→可接通知平面；**降沿** `UpdateEnd` 關窗；`StateChanged`；`WriteOutputByChannelAsync`
      （DO→FC05）；`Flush()`/`Dispose()`；`PollOnceAsync`（public，測試與進階客製用）；
      Alarms.csproj 加 `InternalsVisibleTo HeliVMS.Alarms.Tests`
    - App：
      - `SettingsWindow` nav 第 8 項「IO」＋PageIo：模組群（IoDeviceList＋IoNameBox/IoHostBox/IoPortBox/
        IoUnitBox/IoPollBox＋啟停/刪除）、通道群（方向/Index/名稱/去抖/NC 勾選/相機必綁（DI）＋啟停/刪除）、
        DO 測試（IoDoDeviceCombo/IoDoChannelCombo/DoOnButton/DoOffButton/IoDoStatusText）
      - `IoMonitorHost`（App Services）：依 io_devices 建立/同步各 `IoDeviceMonitor`（啟用者輪詢；
        停用/刪除即釋放）；`EventInserted` 對外；`WriteOutputAsync(deviceId,channelId,on)`；`RefreshAndStart()`
      - MainWindow：`_ioHost = new IoMonitorHost(_store)`＋`RefreshAndStart()`＋
        `EventInserted → _notify?.Enqueue`；關閉時 `_ioHost?.Dispose()`
      - `EventCenterWindow`「io_input」→ `Brushes.Orange`
    - 測試：Storage `IoRepositoryTests` 62→**70**（含 foreign_keys CASCADE、UNIQUE 衝突 throw）；Alarms
      `ModbusTcpClientTests`（frame 編碼/bits 解析/byteCount 例外/echo 比對/loopback FC02·FC05·例外）
      ＋`IoDeviceMonitorTests`（升沿 detail、降沿 UpdateEnd、NC 反相、debounce 濾抖、停用通道忽略、
      方向不符、DO 寫出、未綁相機僅 StateChanged）91→**108**；全 **70＋108＋8＋24＝210**
    - harness `iocheck`→**IOCHECK_OK**：本機假 Modbus 伺服器（TcpListener Loopback：FC02 回可控位元、
      FC05 echo、例外 fc|0x80）；(A) Service——temp DB seed 頻道（綁相機）＋模組＋DI/DO→真實 TCP 輪詢
      （r1 基準 off、r2/r3 on 升沿開事件、r4/r5 off 降沿結算）＋DO 寫入回饋；(B) UI——`--settings`→nav「IO」
      （**nav 7→8，setcheck/snapcheck 硬斷言已同步 8**）→IoNameBox/IoHostBox 填寫＋IoAddDeviceButton→
      io_devices 持久化讀回（測完清理）
    - 設計決策：io_input 事件 channel 為綁定相機（foreign_keys=ON 強制存在）；debounce=0 仍須連續兩次
      同 raw 才觸發（candidate→trigger）；Modbus 請求 count=maxIndex+1、大端位元組序；Unbound DI 事件不寫
      僅 StateChanged（FK 防護）；EventCenter 頻道名 fallback `#id`
    - 回歸 13 全綠：set/snap（nav 8）＋snmp/push/mqtt/ptz/notif/log/smtp/exp/off/evfilter/quiet
    - 驗收：App Release 0 error、全 210、IOCHECK_OK、CI success
16. **M41 已驗收＝電子地圖／平面圖（E-Map）**（§16.1 完整設計落地「P0′」，commit `2d89ea7`，全 **222**，CI `35232094120` success）：
    - 目標：場域空間一覽＋所有裝置（鏡頭/IO）狀態與事件視覺化；承接 M40 事件源
      （motion/offline/ai/tamper/io_input）與 M38 事件回覆工作流
    - **Storage schema v10**（`CurrentSchemaVersion` 9→10、`CreateMapTablesV10`）：
      ```sql
      maps(id PK, name, type DEFAULT 'plan', image_path, width, height,  -- 原圖尺寸（0..1 座標比對）
           enabled 1, sort_order 0, created_at);
      map_devices(id PK, map_id REFERENCES maps ON DELETE CASCADE,
                  device_type CHECK('camera'/'io'), channel_id,          -- camera→channels.id；io→io_channels.id
                  x REAL, y REAL,        -- 0..1 比例座標（圖面縮放/換圖不跑位）
                  angle 0, fov_deg 90, fov_depth 3, enabled 1,
                  UNIQUE(map_id, device_type, channel_id));
      ```
    - Storage `MapRepository`：map CRUD＋`ListMaps`/`SetMapEnabled/DeleteMap`；device overlay
      `AddDevice/ListDevices(map)/SetDevicePosition(map,id,x,y)/SetDeviceEnabled/DeleteDevice/FindMapByChannel`
      （事件定位）；positions 全部 0..1 比例
    - App：
      - **`MapWindow`**（主視窗按鈕列「地圖」，AutomationId `MapButton`）：樓層 ComboBox（依 sort_order）、
        Image＋Canvas（Stretch=None、水平/垂直 ScrollViewer、中心錨點 RenderTransform 縮放 0.2–8×、拖曳平移）；
        圖釘：camera 扇形 Path（fov/angle，半徑 70px）＋綠色圓點（14px；橙＝30 分內有事件）；
        io 方形（10px，橙/灰）；每 15s 依 alarm_events 重繪事件色；單擊→狀態文字 tooltip、雙擊
        camera→`PlaybackWindow` 該頻道；`LocateChannel(id,type)`：切樓層＋12 tick 橙/白閃爍
        （camera pin → fallback io pin whose cameraId matches）
      - `SettingsWindow` nav 第 9「地圖」＋`PageMap`：地圖 CRUD（名稱/圖檔→複製至 dataRoot/maps、
        自動讀寬高、刪除）；圖釘編輯（camera/io＋頻道→點畫布放置 0..1、拖放微調位置即存）；
        AutomationIds：`SettingsPageMap`、`MapNameBox`、`MapImageBox`、`MapBrowseButton`、`MapAddButton`、
        `MapRefreshButton`、`MapList`、`MapToggleButton`、`MapDeleteButton`、`MapPinKindCombo`、
        `MapPinChannelCombo`、`MapPinCanvas`、`MapPinList`、`MapDeletePinButton`、`MapReportText`
      - `EventCenterWindow` 加 `MapLocateButton`「在地圖定位」（io_input→camera 定位）
      - MainWindow 加 `MapButton` 開 `MapWindow`
    - 設計決策：map_devices 無 FK→channels/io_channels（多對多抽象，查 pin 由 DeviceType 分派）；
      圖檔於 dataRoot/maps 持久化，支援換圖不改路徑；定位優先 camera→fallback io pin with same cameraId
    - 測試：Storage `MapRepositoryTests` 12 測（map CRUD+sort/enable/delete cascade、overlay CRUD+pos+
      enable+delete cascade、FindMapByChannel fallback+disabled filter、duplicate unique throw）70→**82**；
      全 **222**（Storage 82+Alarms 108+Licensing 8+Devices 24）
    - harness `mapcheck`→**MAPCHECK_OK**：(A) Service——temp DB＋MapRepository 增圖/圖釘/定位/位置更新；
      (B) UI——`--settings`→nav「地圖」（**nav 8→9，setcheck/snapcheck 斷言已同步 9**）→新增 1×1
      圖、驗 DB ＋主視窗 `MapButton`→電子地圖視窗開啟；setcheck/snapcheck 全綠；回歸 **14 blocks** 全綠
      （set/snap nav 9＋snmp/push/mqtt/ptz/notif/log/smtp/exp/off/evfilter/quiet＋***mapcheck***）
    - 驗收：App Release 0 error、全 222、MAPCHECK_OK、14 回歸全綠、CI success
17. **M42 已驗收＝本機身份驗證＋RBAC**（§14.7 #1、§18.6「本機帳號＋雜湊＋失敗鎖定」P1；`auth.enabled=0` 預設無感、不破既有 harness；commit `6322fae`，全 **253**，CI 進行中）：
    - 背景：企業授權門檻＝「本機登入＋角色」；無第三方約束下以 **PBKDF2（BCL `Rfc2898DeriveBytes`，SHA-256、100k iter、16B salt、32B hash）** 取代 bcrypt（.NET 無內建 bcrypt）；鎖定採「連錯達 threshold 鎖 N 分鐘」
    - **Storage schema v11**（`CurrentSchemaVersion` 10→11、`CreateUsersTableV11`）：
      ```sql
      users(id PK AUTOINCREMENT, username TEXT NOT NULL UNIQUE COLLATE NOCASE,
            password_hash TEXT NOT NULL,                    -- v1$iter$salt$hash（base64）
            role TEXT NOT NULL CHECK(role IN ('admin','viewer')) DEFAULT 'viewer',
            display_name TEXT, enabled INTEGER NOT NULL DEFAULT 1,
            failed_logins INTEGER NOT NULL DEFAULT 0,
            locked_until TEXT,        -- ISO8601 UTC；null＝未鎖定
            last_login TEXT, created_at TEXT);
      ```
    - App 設定鍵（app_settings）：`auth.enabled`（0/1，預設 0）、`auth.lockout.threshold`（預設 5）、`auth.lockout.minutes`（預設 5）
    - Storage 新檔案：`PasswordHasher`（static `Hash/Verify`）、`UserRecord`（Id/Username/PasswordHash/Role/DisplayName/Enabled/FailedLogins/LockedUntil/LastLogin）、`UserRepository`（`CreateUser`/`GetByUsername`/`GetById`/`ListUsers`/`SetEnabled`/`SetRole`/`SetDisplayName`/`DeleteUser`/`RecordFailedLogin`[逾 threshold 自動設 locked_until]`/`RecordLoginSuccess`[failed=0＋last_login]）、`AuthService`（`IsAuthEnabled`、`Authenticate(username,password)`→`AuthResult{Success(role,displayName)|InvalidCredentials|Locked|Disabled|UserNotFound}`，流程＝GetByUsername→enabled→鎖定檢查→Verify→錯則 RecordFailedLogin＋連錯達標鎖定→成功 RecordLoginSuccess）
    - App：
      - `SessionContext`（App，static）：`CurrentUser`（Username/Role/DisplayName）、`IsSignedIn`、`IsAdmin`；`auth.enabled=0` 時一律視為 admin（維持現況不限權限）
      - `LoginWindow`（新窗，Title「登入」）：`UsernameBox`/`PasswordBox`/`LoginButton`/`LoginCancelButton`/`LoginMessage`（錯誤文字）；Enter 鍵登入；取消＝Shutdown
      - `App.OnStartup`：顯示 Splash 前以暫用 `SqliteStore` 讀 `auth.enabled`；`1`→`LoginWindow.ShowDialog()`（成功設 SessionContext、失敗/取消→`Shutdown()`）；`0`→維持現流程（所有 harness 不受影響）
      - RCAC guard：MainWindow `OnLoaded` 依 `SessionContext.IsAdmin` 設 `SettingsButton`/`ExportButton` `IsEnabled`；`OpenSettingsWindow`/`OpenExportWindow` 開頭再 guard（viewer→return）
      - `SettingsWindow` nav 第 10「身份」＋`PageUsers`：`AuthEnabledBox`（Toggle 即寫）＋`AuthThresholdBox`/`AuthMinutesBox`＋使用者群（`UserAddNameBox`/`UserAddPasswordBox`/`UserAddRoleCombo`[admin/viewer]/`UserAddButton`＋`UserList`[selection 顯示]＋`UserToggleButton`/`UserDeleteButton`＋`UserReportText`）；新增時 `PasswordHasher.Hash` 落庫、密碼不顯示
    - 測試：Storage **82→113**（`PasswordHasherTests` 5：Hash 非明文＋Verify ok/bad＋malformed 拒＋隨機 salt＋自訂 iterations；`UserRepositoryTests` 15：CRUD＋NOCASE unique 衝突 throw＋啟停/角色/display_name＋failed counter＋達 threshold 鎖定有效期限＋成功清除＋ClearLock；`AuthServiceTests` 11：預設關＋設定變更＋四態結果＋連錯達標鎖定＋鎖定期內拒＋過期重試＋停用拒＋成功重置）全 **222→253**（Storage 113＋Alarms 108＋Licensing 8＋Devices 24）
    - harness `authcheck`→**AUTHCHECK_OK**：(A) Service——temp DB 驗整套雜湊/NOCASE unique/登入/鎖定（threshold 3）；(B) UI——App DB `auth.enabled="1"`＋建 admin/viewer 兩帳→開 App→**登入窗出現**→錯密碼→`LoginMessage` 含「密碼錯誤」→正確→主窗出現且 admin `SettingsButton` enabled；改用 viewer 登入→`SettingsButton` **disabled**（RBAC）；密碼以 Win32 keybd_event 鍵入（PasswordBox 無 ValuePattern）；測完復原 `auth.enabled=0`＋刪帳
    - 回歸影響：設定中心 nav 由 9→**10**（新增「身份」頁）——`setcheck`/`snapcheck` 之 nav 斷言已同步改 **10**；回歸 **15 blocks 全綠**（set/snap 10＋snmp/push/mqtt/ptz/notif/log/smtp/exp/off/evfilter/quiet＋mapcheck＋***authcheck***）
    - 設計決策：登入窗在 `MainWindow` 建立前以暫用 `SqliteStore` 檢查（`AuthService.IsAuthEnabled`）→ 失敗/取消 `Shutdown(1)`；viewer 僅監看（設定/匯出按鈕 disabled＋handler guard）；`LoginWindow` 密碼錯誤即清空重新輸入（harness 友善）
    - 驗收：App Release 0 error、Storage 113/113、全 253、AUTHCHECK_OK、15 回歸全綠、CI 綠
18. **M43 已驗收＝數位證據安全包（Evidence Bundle）**（§14.3「證據包：影片＋快照＋SHA-256 清單」、§14.7 #4「安全共享」，P1；以 `EvidencePackager` ＋ `.evp` 格式落地「完整性＋密碼保護＋到期」，`Verify` 驗證含篡改偵測；commit `3406f77`，全 **266**，CI `35243549066` success）：
    - 背景：匯出後「證據力」＝檔本身＋SHA-256 清單（§14.3 已列）＋可選密碼保護（前述 `PasswordHasher` 之 PBKDF2 可重用 KDF）；不做 RSA 公鑰簽署（金鑰管理複雜、需另配發驗證工具），先以 **AES-256-GCM**（tag 即篡改偵測）＋完整 SHA-256 清單達成同等完整性
    - **包格式 `.evp`**（Storage 新檔 `EvidencePackager.cs`）：
      ```
      無密碼：直接 ZipArchive ─ manifest.json + items（相對路徑原樣）
      有密碼：ZipArchive → PBKDF2-SHA256(password, salt16, 200000, 32B)
              → AES-256-GCM(nonce12) 加密整包
              → HELIVMS-EVP(11B) | ver(1B=1) | salt(16B) | nonce(12B) | tag(16B) | ciphertext
      ```
    - API：`BundleManifest(BundleId, CreatedUtc, ExpiresUtc?, BundleName, Items[RelativePath/Sha256/SizeBytes/Kind])`；
      `BuildManifest(bundleName, files)`──每檔 SHA-256＋kind 依副檔（mp4=video、bmp/jpg/png=snapshot、json=manifest、txt=note）＋
      定位用 `SourcePath`（不入 manifest，僅供 `Create` 開檔）；
      `Create(outputPath, manifest, password?)` 回傳包檔 SHA-256；`Verify(path, password?)`→`EvidenceVerifyResult{Valid, Expired, BundleId, CreatedUtc, Items, Failures}`（解包後逐 item 重算 sha256＋size 比對，GCM tag 驗證失敗／hash 不符→`Valid=false`）
    - UI（ExportWindow，原「匯出後產生 SHA-256」下方）：`BundleCheckBox`（AutomationId "BundleCheckBox"，Content「匯出後打包數位證據包（.evp）」）
      ＋`BundlePasswordBox`（PasswordBox，選填「包密碼（留空＝開放包）」）＋`BundleExpiryBox`（TextBox，選填「到期日 yyyy-MM-dd」）；
      匯出成功後自動建 `<輸出>.evp`，ResultText 追加「證據包：…evp（SHA-256：…）」；失敗仍回報原匯出成功
    - 測試：Storage **113→126**（`EvidencePackageTests` 13：無密碼 roundtrip Valid＋hash/size 比對＋Expired=false、
      篡改 1 byte→sha256 不符（ZipArchive mode Update 直接改 entry）、密碼包對密碼 Valid／錯密碼 fail、
      到期 expired、nonce 唯一、密碼包無密碼 open fail、kind 分類、空輸入拒、
      非 EVP 檔拒）全 **253→266**
    - harness `evidcheck`→**EVIDCHECK_OK**：(A) Service——temp 檔產 sample.mp4/sample.bmp→BuildManifest→
      無密碼 Create→Verify Valid＋items 序對＋Expired=false→改 bytes→Verify 失敗；密碼包錯/對密碼；到期包 Expired；
      (B) UI——`--export` 開窗→勾 `BundleCheckBox`＋`BundlePasswordBox` 鍵入密碼（keybd_event）＋`BundleExpiryBox` 填明日→
      匯出→輪詢 ResultText 含「證據包」→`exports` 下 `.evp` 存在→以 Service `Verify(密碼)` Valid→cleanup 刪
    - 回歸影響：無（原 15 blocks 不觸及 ExportWindow 新控制項；`expcheck` 未勾包照舊）
    - 驗收：App Release 0 error、Storage 126、全 266、EVIDCHECK_OK、回歸全綠（evidcheck 連續 2 次綠）、CI 綠
19. **M44 已驗收＝匯出中心（Export Center）：批量匯出＋歷史工作＋完整性驗證（§14.3(2) P0 剩餘）**（commit `99556f6`，全 **283**，CI `35260052774` success；修復 `1112251`＝CI 無 ffmpeg→`ExportJobService` 注入 executor、測試改用 fake executor）：
    - 背景：§14.3(2)「匯出證據工作流」已有單段匯出精靈＋證據包（M43），缺「批量匯出＋進度與續傳＋匯出中心（歷史清單與狀態）＋匯出即驗證（ffprobe 完整性報告）」
    - Storage schema **v12**：新表 `export_jobs`（
      `id`、`channel_id`、`stream`、`start_time`、`end_time`（ISO UTC）、`status`
      `('queued'|'running'|'done'|'failed')`、`output_path`、`file_size_bytes`、`sha256`、`error`、
      `created_at/started_at/finished_at`）；`ExportJobRepository`（
      Enqueue＋List(全部,倒序)＋Get＋UpdateStatus＋SetResult(輸出/SHA/size)＋SetError＋Delete＋ListQueued）
    - Storage 新 `ExportVerifier`：`Verify(outputPath, expectedSha?, useFfprobe?)`→
      `VerificationReport{OutputPath, Sha256, SizeBytes, HashMatches, FfprobeSummary?, Valid}`
      （重算 SHA-256 比對→HashMatches；`ffprobe -v error -show_entries format=duration -of csv` 非缺失時出 summary；
      報告文字供 UI「完整性報告」）
    - Recording 新 `ExportJobService`：`ProcessQueued(store, jobsRepo?, temp/settings)`——依序取 queued（最舊優先）
      各 job 以既有 `ExportService.ExportAsync` 執行（channel/range→concatenate→write output_path collate job 名），
      成功→status=done＋sha256＋size；例外→failed＋error；單一反任一 job 失敗不卡佇列（續下一）；
      `PurgeFinished(olderThan)` 供「清完成紀錄」
    - App：`ExportCenterWindow`（`--exportcenter` 開啟；MainWindow 工具列「匯出」旁加「匯出中心」按鈕）：
      「新增匯出」群（多通道 Checklist、起訖時段、目的資料夾）→批次 Enqueue；關閉列狀態與刷新 Timer；
      DataGrid 歷史清單（id/channel/range/status/大小/SHA/時間）＋「開始處理」（循序跑 queued，處理中
      ExportButton disable）＋「驗證」（done job→ExportVerifier.Verify→報告文字含完整性 ok/fail）＋
      「刪除」＋「清完成紀錄」
    - 測試：Storage **126→143**（`ExportJobRepositoryTests` 7：Enqueue 回 id＋queued／List 新→舊／
      ListQueued 僅 queued 且舊→新／MarkRunning→SetResult done＋輸出路徑＋sha＋size＋時間／SetError→failed＋error／
      Delete／PurgeFinished 僅舊 done；`ExportVerifierTests` 6：hash 符→Valid／不符→fail／無預期 hash 存在即 Valid／
      缺檔 invalid／sha 小寫 hex 64／ffprobe summary 可選；`ExportJobServiceTests` 4：空佇列 noop／兩 job 循序 done＋
      輸出存在＋Verify 對 sha Valid＋ffprobe 非空／一 job 失敗 failed 續下一（「無錄影段落」）／輸出檔名含 export-job{id}）＝全 **266→283**
    - harness `exportcentercheck`→**EXPORTCENTER_OK**：(A) Service——temp store＋假 segments（兩通道各
      一段 6s）→Enqueue 2 job→`ProcessQueued`→兩 done＋output 存在＋`ExportVerifier.Verify` Valid；
      一 job 指向不存在段→failed＋佇列續作；(B) UI——`--exportcenter` 開窗→列表顯示 jobs→點「驗證」
      →報告含「完整性」；(C) 安全：真實 DB 只讀不改 jobs／不執行「開始處理」
    - 回歸影響：`expcheck`/`evidcheck` 不觸新窗；新增 EXPORTCENTER_CHECK（回歸 17 blocks）
    - 驗收：App Release 0 error、Storage 143、全 283、EXPORTCENTER_OK（連續 2 次綠）、回歸全綠、CI 綠
20. **M45 已驗收＝備份與異地備援（Backup & Off-Site Redundancy）（§14.4 P1；commit `ef3a8bc`，全 **289**，CI `35263453271` success）**：
    - 背景：§14.1 既有 Retention 只做本機配額清除，單機故障即失資料；需「錄影區段增量複製至第二磁碟／異地目標＋可追溯備份紀錄＋自動推進檢查點」
    - Storage schema **v13**：新表 `backup_log`（`id`、`run_at`（ISO UTC）、`source_root`、`target_root`、
      `checkpoint_utc`（成功 run 才寫，此時間前之 final segment 已備妥）、`copied_count`、`copied_bytes`、
      `failed_count`、`detail`）；`BackupRepository`（RecordRun；LastCheckpoint(source,target)=MAX(checkpoint_utc)；
      ListRuns(targetRoot?, take)→新→舊）
    - Storage 新 `BackupService`：`Run(sourceRoot, targetRoot, checkpointUtc?)`——列全部 final segments
      （start_time > lastCheckpoint）→逐一 `File.Copy` 至 targetRoot 之相對 mirror 路徑→複製後以 db `sha256`
      重算比對（不符→failed 不中斷）；缺來源檔→failed＋detail；結束
      `RecordRun(checkpointUtc = failed==0 ? now : null)`（**有失敗不推進**，下次重跑再試同批）；
      回傳 `BackupRunResult{Scanned, Copied, CopiedBytes, Failed, Advanced}`
    - App：SettingsWindow 左導航新增「備份」頁（`PageBackup`，nav 第 11 項）：啟用 CheckBox＋目標目錄（輸入＋
      瀏覽）＋間隔時數＋「立即備份」＋最近紀錄 ListView（時間/目標/複製/失敗/推進）；MainWindow
      `RunRetentionLoopAsync` 每圈併跑 `RunScheduledBackup`（讀 backup.enabled/target/interval_hours，距
      backup.last_run 達間隔→背景執行→寫 last_run 防熱循環）
    - 設定鍵：`backup.enabled`（1/0）、`backup.target`、`backup.interval_hours`（預設 24）、`backup.last_run`（ISO）
    - 測試：Storage **143→149**（`BackupServiceTests` 6：空段 noop＋逐一複製且 target SHA==db SHA＋增量依
      checkpoint 只複製新段＋來源檔遺失→failed 不推進、補檔重試即成功＋目標=來源 throw＋ListRuns 新→舊）；
      假位元組檔即可（不需 ffmpeg，CI 友好）
    - harness `backupcheck`→BACKUP_OK：(A) Service——temp store＋兩通道假段→run1 copied=2＋target hash 符合→
      追加第三段→run2 只複製新增→run3 無變更 copied=0→刪來源→run4 failed=1 且不推進；(B) UI——`--settings`
      開窗→左導航選「備份」→`BackupTargetBox` 填 temp 目標→「立即備份」→狀態含「備份完成」→log 清單有列
    - 回歸影響：Retention loop 每小時多一次 backup 檢查（無 target 設定時跳過）；Settings nav 第 11 項
    - 驗收：App Release 0 error、Storage 149、全 289、BACKUP_OK（連續 2 次綠：UI 立即備份複製 1 段＋推進、第二次 0 段）、回歸全綠、CI 綠
21. **M46 已驗收＝錄影遮蔽（Redaction／隱私遮罩）（§14.7 #5 P1；commit `c7f6e8f`，全 **293**，CI `35284550851` success）**：
    - 背景：§14.7 影像處理候選中，「錄影遮蔽」為隱私合規面（遮蔽敏感區/人物）且可獨立交付；ffmpeg 既有（M3/M21）
    - Recording 新 `RedactionService`：`RedactAsync(RedactionRequest)`——`segments=ListByRange(main,from,to)`
      → concat demuxer → `-filter_complex <BuildFilter(rois)> -map "[vout]"` → libx264 重新編碼
      → 回傳 `RedactionResult{OutputPath, Sha256, FileSizeBytes, DurationSeconds}`（範圍無段 throw「無錄影段落」）
    - `RedactionFilter.Build(rois)`（**純字串建構、CI 可測不需 ffmpeg**）：`[0:v]split={n+1}[s0..sn]`＋
      每 ROI `[s{k}]crop={w}:{h}:{x}:{y},boxblur={r}:2:{r}:2,scale={w}:{h}[r{k}]`＋依序
      `[s0][r1]overlay={x}:{y}[m1]…[vout]`；`r=clamp(min(w,h)/4,1,12)`（避開 boxblur chroma radius
      上限 12 與 radius≤min/2 限制）；空 ROIs→throw、負座標/零寬高→throw
    - `RedactionRoi(int X,int Y,int Width,int Height)`（絕對像素，多 ROI 同解析度假設）
    - App：`RedactionWindow`（`--redaction`；MainWindow 工具列「遮蔽」按鈕）：頻道＋起訖時段＋
      ROI 編輯（X/Y/W/H 輸入＋加入清單＋移除所選，RoiList）＋輸出預設 `dataRoot\redacted`＋「開始遮蔽」→
      進度→結果（路徑/大小/時長/SHA-256）；頻道預設選第一個有 final 段者、時段預設對準該頻道 final 段範圍
    - 測試：全 **289→293**（Storage/Recording 混合慣例：`RedactionFilterTests` 4——單 ROI 字串含
      crop/boxblur/scale/overlay＋split=2、三 ROI 依序且 split=4、空 throw、負座標/零寬高 throw）
    - harness `redactioncheck`→**REDACTION_OK**（真 ffmpeg 端到端，走 UI 真 DB）：`--redaction` 開窗→
      `RedactRoiW/H` 設 64/48→加入遮罩（RoiList 1 列）→「開始遮蔽」→輪詢狀態含「遮蔽完成」且含 64-hex
      SHA-256→`redacted` 下輸出存在＋`ffprobe` duration>0 且 codec=h264
    - 回歸影響：MainWindow 新增工具列按鈕（不影響既有 10 鈕）；無 New DB 表
    - 驗收：App Release 0 error、全 293、REDACTION_OK（連續 2 次綠：既有段 ch27 遮蔽產出 6s h264＋SHA-256）、回歸全綠、CI `35284550851` success
22. **M47 已驗收＝警報管理器（Alarm Manager／分診面板）（§14.7 #3 P1：事件營運「分診/指派/傳遞/進度狀態大面板」；M38 四態為前置；commit `f3c01e1`，全 **299**，CI `35286083642` success；docs 定義＝`d927295`）**：
    - 背景：M38 已有四態（pending/acknowledged/actioned/false_alarm）＋指派＋備註＋軌跡，但缺「優先序／處理時限（SLA 到期）／分診總覽面板」；本里程碑補事件營運面
    - Storage schema **v14**：新表 `alarm_triage`（`event_id` PK→alarm_events ON DELETE CASCADE、`priority`（預設 normal）、`due_utc`、`owner`、`updated_at`）＋索引 `idx_triage_due(due_utc)`
    - Storage 新 `AlarmTriageRepository`：
      - `AlarmPriority`（`low/normal/high/critical`＋`All/Label/IsValid`，比照 AlarmEventStatus）
      - `SetTriage(eventId, priority, dueUtc?, owner?, updatedAtUtc)`（無效 priority throw；UPSERT）
      - `Get(eventId)`→`AlarmTriageRecord?`
      - `ListBoard(nowUtc, take=100)`→`AlarmBoardRow{EventId,ChannelId,EventType,StartUtc,Status,Priority,DueUtc?,Owner?,AssignedTo?,IsOverdue}`：LEFT JOIN `alarm_events`＋`event_dispositions`＋`alarm_triage`，僅活躍（`COALESCE(status,'pending') != 'false_alarm'`）；排序＝逾期優先→優先序權重（critical>high>normal>low，無 triage 視 normal）→`start_time` 新→舊；`IsOverdue`＝`due_utc < now` 且狀態∈(pending, acknowledged)（C# 計算）
      - `Summarize(nowUtc)`→`AlarmBoardSummary{Pending,Acknowledged,Actioned,FalseAlarm,Overdue}`（各狀態計數＋逾期數）
    - App 新 `AlarmManagerWindow`（`--alarmmanager`；MainWindow 工具列「警報」鈕）：頂摘要列（待處理/已確認/已處理/誤報/逾期計數）＋`BoardList`（時間/頻道/類型/狀態/優先序/負責人/到期/逾期）＋操作區（`DispositionCombo` 四態、`PriorityCombo` 四級、`OwnerBox`、到期 `DueDate`/`DueTime`、`NoteBox`、`ApplyButton`、`RefreshButton`）＋`ManagerStatusText`；選列載入欄位、套用＝`SetDisposition`＋`SetTriage`；開窗列 board（預設選第一列）
    - 測試：Storage **153→159**（`AlarmTriageRepositoryTests` 6：SetTriage round-trip＋UPSERT 更新／無效 priority throw／ListBoard 排除 false_alarm／排序＝逾期→優先序→新→舊／IsOverdue 判定（pending/acknowledged 且到期；actioned 或無 due 不計）／Summarize 計數）；不需 ffmpeg（CI 友好）
    - harness `alarmmanagercheck`→**ALARMMANAGER_OK**：先以臨時 seed 工具（temp console 引用 HeliVMS.Storage；`detail='harness triage seed'` 可重跑覆蓋，含一筆逾期 critical）插入事件→`--alarmmanager` 開窗→`SummaryText` 含「逾期」→`BoardList` ≥1 列→選首列→`PriorityCombo` 選「高」＋`OwnerBox`/`NoteBox` 填值→套用→輪詢 `ManagerStatusText` 含「已更新」→清單出現優先序「高」儲存格
    - 回歸影響：MainWindow 新增工具列按鈕；新表 v14（舊庫自動升版）
    - 驗收：App Release 0 error、全 **293→299**、ALARMMANAGER_OK（連續 2 次綠）、回歸全綠、CI `35286083642` success
23. **M48 已驗收＝魚眼矯正（Dewarping／全景校正）（§14.7 #2 P1：魚眼/全景攝影機漸普及，QNAP Qdewarp、Genetec、Synology 皆有；本機原本完全沒有；commit `2f6508a`，全 **308**，CI `35287346481` success；docs 定義＝`18f81e5`；另含 CI flaky 修正 `79b47f4`）**：
    - 背景：回放/匯出目前僅原樣呈現魚眼畫面；ffmpeg 內建 `v360`（本機 `ffmpeg -filters` 已確認 `V->V`）可離線矯正、不需 GPU；與 M46 遮蔽同屬「錄影段後處理」模式（`ListByRange`→concat→重編碼）
    - Recording 新 `DewarpFilter.Build(DewarpSettings)`（**純字串建構、CI 可測不需 ffmpeg**）：
      - 白名單投影：`input`∈{`fisheye`,`dfisheye`,`equirect`}、`output`∈{`flat`,`equirect`,`c3x2`}（其他 throw）
      - 參數：`ih_fov`/`iv_fov`（輸入視角 0–360）、`h_fov`/`v_fov`（輸出視角 0–360；`flat` 時需 >0）、`yaw`/`pitch`/`roll`（−180–180）、`w`/`h`（輸出尺寸，0＝沿用輸入）
      - 產出：`v360=input={in}:output={out}:ih_fov={ihfv}:iv_fov={ivfv}:h_fov={hfv}:v_fov={vfv}:yaw={yaw}:pitch={pitch}:roll={roll}`＋（w/h>0 時）`:w={w}:h={h}`
      - 驗證：視角超界（<0 或 >360）、角度超界（<−180 或 >180）、`flat` 且 `h_fov`/`v_fov` ≤0、`w`/`h` <0 → throw
    - `DewarpSettings`（record：`Input`/`Output` 投影列舉＋`InputHFov`/`InputVFov`/`HFov`/`VFov`＋`Yaw`/`Pitch`/`Roll`＋`Width`/`Height`；`Default`＝fisheye→flat、輸入 180×180、輸出 90×90、1280×720）
    - Recording 新 `DewarpService.DewarpAsync(DewarpRequest, IProgress<ExportProgress>?, ct)`：`ListByRange(ch,"main",from,to)` 依 `StartUtc` 排序→無段 throw「所選範圍無錄影段落可供矯正」→concat demuxer→`-vf <DewarpFilter.Build>`→libx264 ultrafast crf23＋aac＋`+faststart`＋可選 SHA-256→`DewarpResult{OutputPath,Sha256,FileSizeBytes,DurationSeconds=Σ DurationSec}`（temp concat 清理；骨架比照 `RedactionService`，不共用避免動 M46）
    - App 新 `DewarpWindow`（`--dewarp`；MainWindow 工具列「矯正」鈕）：頻道＋起訖時段（預設選第一個有 final 段者、時段對準該頻道 final 段，比照 `RedactionWindow`）＋`InputCombo`/`OutputCombo`＋輸入/輸出視角四框＋`YawBox`/`PitchBox`/`RollBox`＋`WidthBox`/`HeightBox`＋「預覽」（對首段抽 1 幀套 `v360` 產生 PNG 並於 `PreviewImage` 顯示）＋「開始矯正」→進度→結果（路徑/大小/時長/SHA-256）；輸出預設 `dataRoot\dewarped`
    - 測試：`DewarpFilterTests` 9（Storage/Recording 混合慣例）——fisheye→flat 精確字串、dfisheye→equirect 角度格式、w=h=0 省略尺寸、視角超界 throw、角度超界 throw、flat 零視角 throw、負尺寸 throw、投影白名單 throw、`ToToken` 對映；Storage **159→168**、全 **299→308**
    - harness `dewarpcheck`→**DEWARP_OK**（真 ffmpeg 端到端，走 UI 真 DB）：`--dewarp` 開窗→設定 fisheye→flat、h_fov/v_fov=90、1280×720→「開始矯正」→輪詢狀態含「矯正完成」且含 64-hex SHA-256→輸出存在＋`ffprobe` 解析度＝1280x720 且 `codec=h264` 且 duration>0（實測 ch27 6s→6581 bytes）
    - 回歸影響：MainWindow 新增工具列按鈕；**無 New DB 表**（不涉 schema，維持 v14）
    - 驗收：App Release 0 error、全 **308**（Storage 168＋Alarms 108＋Licensing 8＋Devices 24）、DEWARP_OK（連續 2 次綠）、`redactioncheck` REDACTION_OK＋`alarmmanagercheck` ALARMMANAGER_OK 回歸綠、CI `35287346481` success
    - 附帶修正（`79b47f4`）：`MotionEventEngine` 支援可注入時鐘（ctor 選用 `utcNow`，預設 `DateTime.UtcNow` 行為不變）——`MotionEventEngineTests.BriefBlip_BelowMinDuration_Discarded` 原依賴真實 `Task.Delay(150)`，CI 執行緒延遲使其實際達 1015ms > `MinEventMs`(400) 而誤判為事件（CI `35287015216` 失敗）；改為固定時鐘僅推進 150ms
24. **M49 已驗收＝智慧地圖深化（視角扇形 FOV/深度＋比例尺）（§14.7 #11 P2「視角扇形 FOV/深度」承 §16.1；M41 已落地地圖/圖釘/扇形，但半徑固定 70px、`fov_depth` 未使用、UI 無法編輯角度/FOV/深度且無比例尺）**：
    - **交付**：功能＋測試 `d201bc3`（CI `35289916444` success）；docs 定義 `78e6ce2`；本 snapshot 段。
    - **結果**：App Release build **0 error**、測試 **346/346**（Storage **206**＋Alarms 108＋Devices 24＋Licensing 8）、
      harness `mapfovcheck`→**MAPFOV_OK 連續 2 次**（`db=DUMP_OK:scale=0.05;angle=45;fov=120;depth=5`、`map-scale=比例：0.05 m/px｜扇形半徑：100 px`）；
      回歸 **ALARMMANAGER_OK**（`rows=2`）／**DEWARP_OK**；`git status --porcelain` 空白。
    - 背景：M41 `MapWindow.CreateCameraPin` 以固定半徑 70px 畫扇形（`MapDeviceRecord.FovDepth` 被忽略），且設定頁地圖圖釘只有種類/通道/位置，無 angle/fov/depth 編輯 → 覆蓋範圍無法反映真實尺度；本里程碑補「深度→像素半徑」與比例尺標定
    - Storage schema **v15**：`maps` 加 `scale_m_per_px REAL NOT NULL DEFAULT 0`（0＝未標定；每像素代表公尺）
    - `MapRepository`：`MapRecord` 追加 `ScaleMPerPx`；`SetMapScale(mapId, mPerPx)`（負值 throw）；`ListMaps`/`GetMap` SELECT 帶入；`UpdateDeviceGeometry(id, angle, fovDeg, fovDepth)`（超界 throw）
    - 新 `MapGeometry`（Storage，**純函式、CI 可測**）：
      - `NormalizeAngle(double)`→[0,360)、`ClampFov(double)`→[5,360]、`ClampDepth(double)`→[0,1000]
      - `Bearing(double angleDeg)`→八方位字串（北/東北/東/東南/南/西南/西/西北，45° 為單位、四捨五入）
      - `SectorRadiusPixels(double fovDepthMeters, double scaleMPerPx, double fallbackPixels = 70)`：`scale>0 && depth>0` → `clamp(depth/scale, 8, 100000)`；否則 `fallback`
    - `MapWindow`：`ShowMap` 記錄 `_scaleMPerPx`；camera 扇形半徑改用 `MapGeometry.SectorRadiusPixels(d.FovDepth, _scaleMPerPx)`；新增 `MapScaleText`（AutomationId）顯示「比例：{scale} m/px｜扇形半徑：N px」；圖釘 ToolTip 追加角度/FOV/深度/方位
    - `SettingsWindow` 地圖頁：地圖比例 `MapScaleBox`＋「套用比例」鈕（`OnMapApplyScaleClicked`）；圖釘幾何 `MapPinAngleBox`/`MapPinFovBox`/`MapPinDepthBox`＋「套用幾何」鈕（套用至 `MapPinList` 選取項）；新增圖釘時採用欄位值；`MapPinRow` 追加角度/FOV/深度/方位；`MapReportText` 回報（**nav 數不變**，不破 setcheck/snapcheck）
    - 測試：新 `MapGeometryTests`（Storage.Tests：角度正規化/FOV 與深度 clamp/八方位/半徑＝depth÷scale、無比例回退、極小 scale 上限）；`MapRepositoryTests` 擴充（`SetMapScale` round-trip＋負值 throw、`UpdateDeviceGeometry` round-trip＋超界 throw、`MapRecord.ScaleMPerPx`）；全 **308→約 320**
    - harness `mapfovcheck`→**MAPFOV_OK**（走 UI 真 DB）：seed 地圖（temp console 插 `maps` 列＋產 PNG）→`--settings` 地圖頁→選地圖→`MapScaleBox=0.05`→套用比例→`MapPinKindCombo=camera`→選通道→`MapPinAngleBox=45`/`Fov=120`/`Depth=5`→於 `MapPinCanvas` 放置→`MapPinList` 選列→套用幾何→`MapReportText` 含「已更新幾何」→DB 驗 `maps.scale_m_per_px=0.05` 與 `map_devices` angle/fov/depth→開 `MapWindow`（`--map`）→`MapScaleText` 含「0.05」→MAPFOV_OK 連續 2 次
    - 回歸影響：schema v15（舊庫自動升版）；設定頁新增欄位；`MapWindow` 需新增 `--map` 命令列旗標（目前僅主視窗「地圖」鈕）
    - 驗收：App Release 0 error、全 **346**（≈320）、MAPFOV_OK（連續 2 次綠）、回歸綠、CI 綠
25. **M50 已驗收＝企業身份整合（OIDC SSO 核心＋LDAP 設定面）（§14.7 #1 P1：企業標案基本門檻；M42 已有本機帳號＋RBAC）**：
    - **交付**：功能＋測試 `d43c058`（CI `35293174402` success）；docs 定義 `84ecd72`；本 snapshot 段。
    - **結果**：App Release build **0 error**、測試 **400/400**（Storage **260**＋Alarms 108＋Devices 24＋Licensing 8）、
      harness `ssoidcheck`→**SSOID_OK 連續 2 次**（`provider=harness-idp;expired=rejected;valid=accepted`）；
      回歸 **ALARMMANAGER_OK**／**DEWARP_OK**／**MAPFOV_OK**；`git status --porcelain` 空白。
    - 背景：市場對標（Milestone／Genetec／Synology）唯一尚未覆蓋的 P1——大型案要求「沿用企業 AD／OIDC SSO」
    - Storage schema **v16**：`auth_providers`（id, name UNIQUE, kind CHECK(oidc|ldap), enabled, config_json, created_at）
    - Storage 新檔（純函式／BCL，**無新套件依賴**）：
      - `AuthProviderRepository`：`AuthProviderRecord`＋CRUD（`Add`／`List`／`ListEnabled(kind)`／`Get`／`SetEnabled`／`Delete`）
      - `OidcOptions`：Issuer／Audience／ClientId／UsernameClaim／RoleClaim／AdminGroups／DefaultRole／ClockSkewSeconds／JwksJson／JwksUri；`FromJson`／`ToJson`
      - `OidcToken`：三段解析（base64url）→ `OidcHeader{alg,kid,typ}`／claims（`System.Text.Json`）
      - `RsaJwk`：JWKS JSON → `Dictionary<kid,RSA>`（n/e base64url；`FromModulusExponent`）
      - `OidcValidator.Validate(token, options, keys, utcNow)`：alg=RS256＋`RSA.VerifyData(SHA256,Pkcs1)`＋iss／aud／exp／nbf（clock skew）→ `OidcResult{Ok,Error,Subject,Username,DisplayName,Role}`
      - `LdapFilter`：RFC4515 escape（`\00`／`(`／`)`／`*`／`\`）＋`{user}`／`{0}` 樣板組合；`LdapSettings`（Host／Port／BaseDn／BindDn／BindPassword／UserFilter／AdminGroup／DefaultRole）＋`FromJson`
      - `EnterpriseAuthService`：列 providers、`AuthenticateOidc(provider,token,utcNow)`（JWKS 來源 `JwksJson`→`JwksUri`）、角色對映（groups∩AdminGroups→admin 否則 default）、`AuthenticateLdap(settings,username,password,ILdapBinder)`（目錄連線以介面抽象）
    - App：
      - SettingsWindow「身份」頁新增「企業身份」區（`EnterpriseProviderList`／`EntNameBox`／`EntKindCombo`／`EntIssuerBox`／`EntAudienceBox`／`EntRoleClaimBox`／`EntAdminGroupsBox`／`EntDefaultRoleCombo`／`EntJwksBox`／`EntAddButton`／`EntToggleButton`／`EntDeleteButton`／`EntReportText`）
      - LoginWindow 新增「企業 SSO」區（`OidcProviderCombo`／`OidcTokenBox`／`OidcLoginButton`／`OidcMessage`）：僅當有啟用 OIDC provider 時顯示；成功→`SessionContext.CurrentUser`（角色由對映決定）
      - `App.PerformLogin` 傳入 `SqliteStore` 供 `LoginWindow` 建 `EnterpriseAuthService`
    - 測試：`OidcValidatorTests`（自簽 RSA：有效、錯誤簽章、alg=none、過期、nbf、iss／aud 不符、未知 kid、clock skew、角色對映）、`RsaJwkTests`、`LdapFilterTests`、`AuthProviderRepositoryTests`、`EnterpriseAuthServiceTests`；全 **346→400**（Storage **206→260**）
    - harness `ssoidcheck`→**SSOID_OK**（seed `--sso` 產 RSA＋JWKS＋有效/過期 token＋provider→設定頁驗清單→`auth.enabled=1` 重啟→LoginWindow 貼有效 token 登入成功→過期 token 被拒→還原 `auth.enabled=0`）連續 2 次
    - 回歸影響：schema v16；設定頁新增區塊（nav 數不變）；`auth.enabled` 預設 0，既有流程與 harness 不受影響；LDAP 目錄連線以 `ILdapBinder` 抽象（本里程碑完成設定／filter／角色對映，實際 AD 綁定由部署端提供）
    - 驗收：App Release 0 error、全 **400**、SSOID_OK ×2、回歸綠、CI **35293174402** success
26. **M51 ＝外部安全共享（Share Link／無帳號分享）（§14.7 #4 P1：對標 Synology Share Link／Genetec Secure Share／Milestone；企業與法務常需把片段／證據以「連結＋到期＋密碼」分享給無帳號第三方）**：
    - 背景：現有輸出僅本機檔案（匯出中心）與 `.evp` 證據包；無「產生一次性／限時連結供外部檢視下載」能力
    - Storage schema **v17**：`share_links`（id, token UNIQUE, kind CHECK(segment|snapshot|evidence), resource_path, label, password_hash, created_at, created_by, expires_at, max_uses, use_count, revoked, last_used_at）
    - Storage 新檔：
      - `ShareLinkRepository`：`Add`／`GetByToken`／`List`／`ListActive(nowUtc)`／`IncrementUse`／`SetRevoked`／`Delete`／`PurgeExpired(nowUtc)`
      - `ShareLinkService`：`ShareToken.Create()`（`RandomNumberGenerator` 32 bytes→base64url）；`Create(kind, path, label, createdBy, expiresAt, maxUses, password, allowedRoots)`（驗證 kind、路徑在允許根內且存在、可選 `PasswordHasher.Hash`）；`Evaluate(record, nowUtc, password)`→`ShareAccessResult{Ok,Error}`（revoked／過期／超次數／密碼）；`Revoke`／`PurgeExpired`
      - 純函式 `SharePath.IsWithinRoot(root, path)`（`Path.GetFullPath` 正規化後前綴比對，拒 `..` 逃逸；大小寫不敏感）
    - App：
      - `ShareHost`（`System.Net.Sockets.TcpListener`，綁 loopback（避免 HttpListener 的 URL ACL 需求），位址 `http://localhost:{port}/`）：`GET /share/{token}` 下載、`GET /share/{token}/info` 摘要 JSON、`POST /share/{token}` 表單帶密碼；狀態碼 404（查無／撤銷／資源消失）／410（過期）／401（密碼錯）／403（超次數）／405（方法不允許）
      - 設定頁「一般」新增分享服務區（`ShareEnabledBox`／`SharePortBox`／`ShareBaseUrlText`／套用鈕／狀態文字；預設 `share.enabled=0`）
      - 匯出中心新增「建立分享連結」與「分享連結管理」（清單／複製連結／撤銷）；`--share` 命令列可開管理視窗
    - 測試：`ShareLinkTests`（token 唯一且長度足夠、路徑逃逸拒絕、kind 驗證、過期／撤銷／超次數／密碼、`IncrementUse`、`PurgeExpired`、`ShareAccessResult`）；Storage 260→**285**、全 400→**425**
    - harness `sharecheck`→**SHARE_OK**（seed 建分享指向 temp 檔→`share.enabled=1` 啟主機→HTTP 下載 200＋內容相符→錯 token 404→撤銷後 404→過期 410→`info` JSON 正確）連續 2 次
    - 回歸影響：schema v17；`TcpListener` 預設關閉，不影響既有流程與 harness；CI 僅跑單元（無網路）；`PasswordHasher` 重用
    - **已驗收**：App Release 0 error、測試 **425/425**（Storage 285、Alarms 108、Devices 24、Licensing 8）；harness `sharecheck`→**SHARE_OK ×2**、`sharewindowcheck`→**SHAREWIN_OK**；回歸 **ALARMMANAGER_OK／DEWARP_OK／MAPFOV_OK**；功能 `2dea00f`、CI `35294863720` success
27. **M52 ＝模組化分析情境套件（Analytics Modules）（§14.7 #6 P2；§5.6 落地）**：
    - 背景：對標 Genetec KiwiVision 等以「場景情境模組」販售，全部以幾何規則引擎組合、不寫死。本里程碑落地 §5.6 的 P2 三模組：**周界跨線（line_cross）／區域侵入（intrusion）／人群聚集（crowd）**；靜止車/尾隨/車流/徘徊（需跨幀追蹤）留待後續
    - Storage schema **v18**：`analytics_zones`（id, name, channel_id, module CHECK IN('line_cross','intrusion','crowd','loitering','stationary'), enabled, polygon_json, direction CHECK IN('both','a_to_b','b_to_a'), min_count, dwell_seconds, created_at）；`AnalyticsZoneRepository`（Add／List／ListByChannel／Get／Update／SetEnabled／Delete；module／direction 驗證；polygon 以 `"x,y;x,y;…"` 正規化 0..1 存取）
    - **HeliVMS.Alarms 新檔**：
      - `AnalyticsGeometry`：`NormalizedPoint(X,Y)`；`ParsePoints`／`FormatPoints`；`IsValidPolygon`（≥3 點且座標於 [0,1]）；`PointInPolygon`（射線法，含邊界）；`SegmentIntersects`；`SignedSide`（點在線段左／右）；`Area`（鞋帶公式）
      - `AnalyticsZone`（Id／Name／ChannelId／Module／Enabled／Polygon／Direction／MinCount／DwellSeconds）＋`AnalyticsModuleCatalog`（模組 id／顯示名／事件類型／授權 feature：`analytics.line_cross`／`analytics.intrusion`／`analytics.crowd`）
      - `AnalyticsDetection(Class, X, Y, Confidence, TrackId?)`（**中心點**正規化）＋`AnalyticsResult(Module, EventType, ZoneId, ZoneName, ChannelId, TrackId?, Count, Detail)`
      - `AnalyticsZoneEvaluator`（有狀態逐幀）：intrusion（以 track／class+格位追蹤 inside 集合，產生進入／離開）、line_cross（polygon 前 2 點為線段，依前一幀側向判方向並支援 direction；track_id 去重）、crowd（zone 內數量 ≥ min_count，連續 N 幀去抖動）；`Reset()`
      - `AnalyticsEventEngine`（ctor(store, snapshotDir)；`LoadZones()`；`OnDetections(channelId, DetectionsFrame)`：以 `Detection` 中心 `(X+W/2, Y+H/2)` 評估，並以 `AlarmEventRepository.Insert` 寫 `ai_line_cross`／`ai_intrusion`／`ai_crowd`，detail 含 zone 名）
    - **App**：`AnalyticsWindow`（`--analytics`；admin 限制）：zone 清單（通道／模組／點數／啟用）、新增區（名稱／通道 Combo／模組 Combo／多邊形點文字／方向／min_count／dwell）、簡易 `AnalyticsCanvas` 點擊加點顯示、啟用停用／刪除、「測試評估」按鈕（以假偵測跑 evaluator 並輸出 `AnalyticsReportText`）；MainWindow 建立 `AnalyticsEventEngine` 並於 `OnManagerAiDetections` 轉呼叫（真實偵測到位時觸發）
    - 測試：`AnalyticsGeometryTests`、`AnalyticsZoneEvaluatorTests`、`AnalyticsZoneRepositoryTests`、`AnalyticsEventEngineTests`（約 +45；Storage／Alarms 增加；總 425→約 **470**）
    - **已驗收**：App Release 0 error；測試 **466/466**（Storage 304＋Alarms 130＋Devices 24＋Licensing 8；Storage ＋19＝`AnalyticsZoneTests`、Alarms ＋22＝`AnalyticsModulesTests`）；harness `analyticscheck`→**ANALYTICS_OK:seed=4;add=5;eval=評估：1 筆事件（周界/跨線）。 首筆：ai_line_cross**（seed 建 3 區→開 `--analytics`→列出現→UI 新增 line_cross 區→範例偵測評估命中→停用後評估 0→刪除還原）；回歸 **ALARMMANAGER_OK／DEWARP_OK／MAPFOV_OK／SHARE_OK／SHAREWIN_OK**；HEAD＝`005649c`、CI `35522898383` success
    - 實作與定義細微差異（已更新原文）：多邊形以**點字串文字輸入**（未做 Canvas 點擊加點）；`AnalyticsPolygon.TryParse` 支援 `minPoints`（跨線 2 點、區域 3 點），`AnalyticsZoneRepository.Validate` 依 module 決定下限（Update 仍查原 module）
    - **附加修復**：`SqliteStore.AddMapScaleV15` 冪等化（`ALTER` 前檢查 `scale_m_per_px` 欄位是否存在）。起因：真 DB `user_version` 偵測為 4 但表結構已達 v18 → 每次啟動重跑遷移，`ALTER COLUMN` 重複即崩潰（ExitCode -532462766）；修復後升級遷移可自癒（跑過一次即 SET 18）
28. 每里程碑節奏照舊：定義先寫入本檔→實作→App Release build 0 error→單元測試→
    （有 UI 面者）E2E harness block→commit＋push＋CI success→`git status --porcelain` 空白

## 已知雷區（勿再犯）
- **harness `.ps1` 必須存成 UTF-8 with BOM**：寫檔工具產出的是 UTF-8 無 BOM，含中文的 `.ps1` 會被 PowerShell 5.1 以 ANSI/Big5 誤讀而**靜默破壞解析**（症狀：`Start-Process` 看似無效、App 根本沒啟動、`Get-Process HeliVMS.App` 找不到）。修法：`[System.IO.File]::WriteAllText($p,[System.IO.File]::ReadAllText($p,[System.Text.Encoding]::UTF8),(New-Object System.Text.UTF8Encoding($true)))`（temp/opencode 有 `fix-encoding.ps1`）
- **PowerShell 雙引號內 `$var?xxx` 的 `?` 是合法變數字元**：`"$url/$token?password=x"` 會把 `$token?password` 當成變數名（不存在→空字串），導致 token 遺失。改用 `${token}`（M51 `sharecheck` 實證）
- **勿以 bash 對 repo 源碼做 byte 級重寫**（曾造成 UTF-8 漂移／mojibake 污染，已 `git restore` 還原）；
  改源碼一律用 read/edit tool
- grep tool 對 harness 檔案可能出現「零結果」（權威＝`git show HEAD` 對 repo 檔；
  harness 在 temp 路徑，請用**完整絕對路徑** grep）
- **勿臆測 repo 內的識別名稱**；以 `git show`／`git diff` 為準
- **回放窗 ComboBox 的 UIA**：下拉 item 的 `ListBoxItem.Current.Name` 是物件的 ToString（型別名），
  要以 item 內層 `ControlType.Text` 的 Name 來配「頻道 N」再對 `ListItem` 下
  `SelectionItemPattern.Select()`（對 Text 下 Select 會靜默失敗）
- **ComboBox 展開後的 popup item 不在 combo 子樹**（popup 是獨立視窗，`$combo.FindAll(Descendants, ListItem)` 為 0）；
  且 `RootElement.FindFirst(Name=「高」)` 會先命中 item 內層 `ControlType.Text`，對 Text 下
  `SelectionItemPattern.Select()` 會丟 `InvalidOperationException`（不支援此模式）；需以
  `TreeWalker.ControlViewWalker.GetParent` 由該 Text 上溯至 `ControlType.ListItem` 再 `Select()`（M47 alarmmanagercheck 實證）
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
- **M47 警報管理器 E2E 前置**：本機真 DB `C:\HeliVMSData\index.db` 未必有 `alarm_events`
  （`Summarize` 全 0、`BoardList` 0 列）；`alarmmanagercheck` 先跑臨時 console（temp `seedtriage`，
  ProjectReference `HeliVMS.Storage`）插入 `detail='harness triage seed'` 事件（先 DELETE 同名再 INSERT，
  可重跑覆蓋），並對一筆設逾期 critical（`SetTriage(..., now.AddMinutes(-5), ...)`）以驗「逾期」計數
- **M48 魚眼矯正 E2E 前置**：`dewarpcheck` 不 seed DB，直接吃既有 final 段（本機 ch27 有 6s 段）；
  ffmpeg 內建 `v360`（`-vf v360=input=…:output=flat:…:w=…:h=…`，**用 `-vf`、無 `-filter_complex`/`-map`**）；
  CI 不跑 harness，故 runner 無 ffmpeg 亦不影響
- **時間敏感單元測試勿依賴真實 `Task.Delay` 上界**：`MotionEventEngineTests.BriefBlip`（門檻
  `MinEventMs`=400ms）在 CI 慢機因 `Task.Delay(150)` 實際達 ~1s 而 flaky（CI `35287015216`）；
  `MotionEventEngine` 已支援可注入時鐘（ctor 選用 `utcNow`），同類測試請注入假時鐘而非真實延遲
- **`Space` 焦點衝突**：Tab 焦點落在 PlayPauseButton/StopButton 時按空白鍵會雙重觸發（Button 自身也處理空白鍵）；
  故 `OnPreviewKeyDown` 對按鈕聚焦需 `e.Handled=true` 後自行分派播放/暫停，避免 Button Click 與快捷鍵各觸發一次
- **WPF Panel（StackPanel/Grid）沒有 AutomationPeer**：其 `AutomationId`/`x:Name` 無法用
  `FindFirst(Descendants, AutomationIdProperty)` 找到；但 **`Visibility=Collapsed` 的子項目
  （TextBlock/TextBox/Button 等具 peer 元素）在 UIA 樹中直接「不存在」**（FindFirst 回 null），
  切頁測試用「目標頁元素存在／消失」判斷即可（`setcheck` 以此驗證翻頁）
  - M37 再證：即使 nav 已切到規則頁（子元素 `RuleNameBox` 等都找得到＝頁面 Visible），
    page 本身 `SettingsPageRules` 仍回 null（Panel 無 peer）——故 rules-ui 的成立條件
    應以子控件為準，page 存在與否僅供診斷（切勿把 `ruPage is not null` 當 gate）
  - M37 回歸：設定中心 nav 由 6→7，凡 harness 硬斷言 `navCount==6` 者（setcheck/snapcheck）
    都必須同步更新為 7
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
- **M26 WinForms 並容**：啟用 `<UseWindowsForms>true</UseWindowsForms>` 後 implicit usings 會
  注入 `System.Drawing/System.Windows.Forms`，與 WPF 型別衝突（`Application`/`Image`/`Brush`/
  `Size`/`ContextMenu` 等 CS0104）——必須在 csproj `<Using Remove>` 三者；NotifyIcon 的 tooltip
  屬性名是 **`Text`**（不是 `ToolTipText`）
- **M26 Process 環境變數**：`ProcessStartInfo.Environment` 設定時
  `UseShellExecute` 必須 `false`（true 會 InvalidOperationException）——GUI exe 直接
  CreateProcess 啟動 WPF app 無礙
- **ptzcheck 綁定頻道別硬編 id=1**：開發機 C:\HeliVMSData\index.db 的 channels id 已不含 1
  （由使用者實際使用所致）；綁「最小存在頻道 `SELECT MIN(id)`」即可（App ChannelCombo index0
  恰為最小 id）
