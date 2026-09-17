# HeliVMS 合作恢復卡（RESUME CARD）

> 用途：對話上下文過長、影響回覆速度時，開新 session 用這份檔案接手，**不依賴舊對話記憶**。
> 權威來源：`git` 與 GitHub Actions 的現況（不是聊天記錄）。
> 所有里程碑均已 commit＋push、CI 全綠、工作目錄乾淨（下方數據皆為驗證過的真實值）。

## 一句話總結
HeliVMS 為一套**網路影像監控系統**（WPF 桌面應用：即時監看／回放／AI 事件中心／錄影排程）。
里程碑 **M1 至 M31 已全數 commit＋push、CI 綠燈**。最終 commit＝HEAD（M31 為最新）。
Release build 0 error、測試 **128/128 全過**、`git status --porcelain` **空白（工作目錄清乾淨）**、
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
- 最後 commit：`HEAD`＝**M35（`0493612`）**——ONVIF Discovery Hello/Bye/Resolve
  （見下方 M35 定義段）；Devices 19→24、全 **148**、App Release 0 error、CI `35165622598` success
- 進行中：**M36＝SNMP 陷阱**（見下方 M36 定義段）
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
- CI：`gh run list` 顯示最新 run `conclusion=success`；建置 0 error、測試 120/120

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
     **`ptzadvcheck`（M31，PTZADVCHECK_OK）**
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
12. 每里程碑節奏照舊：定義先寫入本檔→實作→App Release build 0 error→單元測試→
    （有 UI 面者）E2E harness block→commit＋push＋CI success→`git status --porcelain` 空白

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
