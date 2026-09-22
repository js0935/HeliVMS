# HeliVMS 合作恢復卡（RESUME CARD）

> 用途：對話上下文過長、影響回覆速度時，開新 session 用這份檔案接手，**不依賴舊對話記憶**。
> 權威來源：`git` 與 GitHub Actions 的現況（不是聊天記錄）。
> 所有里程碑均已 commit＋push、CI 全綠、工作目錄乾淨（下方數據皆為驗證過的真實值）。

## 一句話總結
HeliVMS 為一套**網路影像監控系統**（WPF 桌面應用：即時監看／回放／AI 事件中心／錄影排程）。
里程碑 **M1 至 M106 已全數 commit＋push、CI 綠燈**。最終 commit＝HEAD（M106 智慧牆警報看板引擎 L0）。
Release build 0 error、測試 **994/994 全過**、`git status --porcelain` **空白（工作目錄清乾淨）**、
本地與遠端完全同步（`git diff origin/HEAD` 為空）。

## 開新 session 的接手方法
```powershell
# 在 D:\HeliVMS 開新 session 時，先對新的 agent 講：
git status                    # 預期為 empty（乾淨）
git log --oneline -20         # 預期見到 M1..M88，HEAD＝M88
git diff origin/HEAD          # 預期為空（同步）
gh run list -L 3              # 預期全部 success
```
新 session 的 prompt 只需這一句：
「接續 HeliVMS（下一里程碑依 SESSION_RESUME 規劃自主選定並先寫定義），請先讀 `docs\SESSION_RESUME.md`，照指示以自主模式繼續。」
新 session 會以 git 現況接手，不會靠猜測。

## 現況快照（權威來源＝git，非聊天記憶）
- 最後 commit：`HEAD`＝**M106**——§14.7 #14 智慧牆警報看板引擎 L0（純 BCL）：
  `SmartwallAlertBoard`：`Snapshot`＝保留窗（300s）內事件→每頻道最新一則→RuleOrder 升序→優先序
  （low&lt;normal&lt;high&lt;critical）降序→發生時間新→舊→至多 maxCells 格，`BoardCell
  (ChannelId,EventType,Priority,Rank,Highlight,Age)`（Highlight＝發生 ≤5s＝AlarmHighlight）；
  `LatestForChannel`（頻道保留窗內最新）；`RankOf`。12 新測試→全 **994**、Release build 0 error、
  樹淨
- 前一個 M105 交付＝`8766691`（§14.7 #14 智慧牆版面資料模型＋幾何校驗＋看板時間常數 v39）＋`91a4306`（docs）；
  全 **982**、CI `35701637588` success
- 前一個 M85 交付＝`54cad94`（LDAPv3 連線層 L1：`LdapClient : ILdapBinder`
  裸 BER wire（simple bind＋memberOf SearchGroups）；`LdapBer` minimal BER＋`LdapFilterEncoder`
  RFC4515→BER；24 新測試（FakeLdapServer loopback）；全 **799**、CI `35621317643` success；
  seedtriage `--ldap-bind` 第 28 支 harness→`LDAP_BIND_OK`）
- 前一個 M84 交付＝`434e275`（回放視窗強化② 時間軸模型驅動：band 覆蓋率 `COV:...`，
  UIA 第 27 支 pbcheck harness）、全 **775**、CI `35614308528` success
- 前一個 M83 交付＝`f9a2ba1`（回放視窗強化① 時間軸 L0 `PlaybackTimelineBuilder`）、全 **775**、
  CI `35613154435` success
- 前一個 M80 交付＝`4de75be`（LDAP/AD 登入 L0：`LdapDn`＋`LdapSettingsValidator`）、全 **746**、
  CI `35608457139` success；ldapcheck 第 25 支 harness
- 前一個 M79 交付＝`c661f9c`（音訊感測測試視窗 `AudioWindow`）、全 **735**、CI `35605392969` success
- 前一個 M78 交付＝`7c937e8`（音訊感測 L1 協調器 `AudioSensorCoordinator`）、全 **735**、
  CI `35604408658` success
- 前一個 M77 交付＝`06b4517`（雙碼流切流視窗）、全 **724**、CI `35603258006` success
- 前一個 M76 交付＝`d993122`（雙碼流 Smart 切流 `StreamSwitcher` §15.2 L0）、
  全 **724**、CI `35602052735` success（隨附 NTP 測試寬鬆 fix `828acc5`）
- 前一個 M75 交付＝`3f12ab4`（感測器 IO 測試視窗）、全 **713**、CI `35601164639` success
- 前一個 M74 交付＝`5cec078`（感測器 IO 引擎 schema v27）、全 **713**、CI `35600379623` success
- 前一個 M72 交付＝`77ee434`（巡航視窗＋持久化）、全 **701**、CI `35599188803` success
- 前一個 M71 交付＝`e63459f`（OnvifPtzExecutor ONVIF 實作）、全 **693**、CI `35587474678` success
- 前一個 M70 交付＝`ec03350`（PatrolRunner PTZ 巡航執行器）、全 **686**、CI `35586810793` success
- 前一個 M69 交付＝`36cbb60`（PTZ 巡航排程器 PatrolController L0）、全 **676**、CI `35586031203` success
- 前一個 M68 交付＝`a5015a7`（RFC 5905 SNTP 校時用戶端）、全 **663**、CI `35585248329` success
- 前一個 M64 交付＝`3418fe5`（音訊事件偵測原語 §5.8）、全 **621**、CI `35547445399` success
- 前一個 M61 交付＝`d4303b0`（單鏡目標追蹤原語，見下方 §37 定義段）、全 **570**、CI `35544108591` success
- 前一個 M60 交付＝`ef567a7`（統圖報表／管理報表，見下方 §36 定義段）、全 **559**、CI `35543378978` success
- 前一個 M58 交付＝`3e28bcd`（智慧分析模組二，見下方 §34 定義段）、全 **543**、CI `35541102070` success
- 前一個 M57 交付＝`5225a3a`（多語言介面 i18n，見下方 §33 定義段）、全 **525**、CI `35537573067` success
- 前一個 M56 交付＝`1309112`（事件中心搜尋／篩選／CSV 匯出，見下方 §32 定義段）、全 **518**、CI `35533344119` success
- 前一個 M55 交付＝`9b1b37a`（異地備援自動複製 Off-Site Replication，見下方 §31 定義段）、全 **512**、CI `35526440981` success
- 前一個 M54 交付＝M54 智慧警報（`d893554`）、全 **505**、CI `35525399049` success
- 前一個 M52 交付＝`005649c`（模組化分析情境套件 Analytics Modules，見下方 §27 定義段）、全 **466**、CI `35522898383` success
- 前一個 M51 交付＝`2dea00f`（外部安全共享 Share Link／無帳號分享，見下方 §26 M51 定義段）、全 **425**、CI `35294863720` success
- 前一個 M50 交付＝`d43c058`（企業身份整合 OIDC SSO 核心＋LDAP 設定面，見下方 §25 M50 定義段）、全 **400**、CI `35293174402` success
- 前一個 M48 交付＝`2f6508a`（魚眼矯正 Dewarping，見下方 §23 M48 定義段）、全 **308**、CI `35287346481` success
- 前一個 M47 交付＝`f3c01e1`（警報管理器 Alarm Manager，見下方 §22 M47 定義段）、全 **299**、CI `35286083642` success
- 前一個 M46 交付＝`c7f6e8f`（錄影遮蔽 Redaction，見下方 §21 M46 定義段）、全 **293**、CI `35284550851` success
- 前一個 M45 交付＝`ef3a8bc`（備份與異地備援，見下方 §20 M45 定義段）、全 **289**、CI `35263453271` success
- 已驗收：**M65＝運動感度自動調校（處置回饋誤報率統計與感度建議）**（§14.7 #17 Auto-VMD，見下方 §41 定義段）；**M64＝音訊事件偵測原語（RMS dBFS 分塊分類、爆音/持續音/斷路事件引擎）**（§5.8 Audio L0，見下方 §40 定義段）；**M63＝影片摘要（偵測動態幀拼貼預覽＋manifest 摘要）**（§5.9，見下方 §39 定義段）；**M62＝複合事件規則引擎（match/all/any/not/within/count/timeBetween、ai_rules 設定層、規則編輯器）**（§5.10，見下方 §38 定義段）；**M61＝單鏡目標追蹤原語（匈牙利 IoU 指派／track_id／EMA 平滑／生命周期）**（§5.7，見下方 §37 定義段）；**M60＝統圖報表／管理報表（錄影時數／斷線／容量趨勢／AI 事件統計）**（§14.7 #9、§11.7，見下方 §36 定義段）；**M59＝智慧分析模組三（尾隨／逆行 `ai_tailgating`）**（§5.6，見下方 §35 定義段）；**M58＝智慧分析模組二（長時間徘徊／靜止物／車流統計／熱區圖）**（§5.6，見下方 §34 定義段）；**M57＝多語言介面 i18n（繁中／簡中／English）**（§14.7 P1，見下方 §33 定義段）
- 進行中：（下一里程碑依規劃自主選定並先寫定義；M67＝§43 已完成）
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
- CI：`gh run list` 顯示最新 run `conclusion=success`；建置 0 error、測試 633/633

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
29. **M53 ＝數位證據完整性與數位簽章（Evidence Integrity ＆ Signing）（§14.7 #5 P1，確立 §14.3 證物線）**：
    - 背景：M45 備份已產 manifest 清單；本里程碑補足「**SHA-256 清單＋數位簽章＋驗證**」P1
      （合規／鑑識要求：證明匯出證據集未被改動，丟給司法單位可驗證）
    - Storage schema **v19**：`evidence_manifests`（id, directory_path UNIQUE, manifest_json, created_at, last_verified_at）；
      `EvidenceManifestRepository`（Upsert／Get／List／Delete）
    - Storage 新檔：
      - `EvidenceSigner`：RSA-2048（金鑰落點 `app_settings` key `evidence.signing_key_pem`，首次自動生成）；
        `EnsureKey`／`SignDocument(manifestJson)→(signature, fingerprint)`（SHA-256 digest 簽屬）／`VerifySignature`
      - `EvidenceManifestService`：`Build(directory)`（遞迴掃描各檔 rel_path→sha256+size、排除 manifest.json、總檔數／位元組）→`ManifestDocument`；
        `Save`（寫目錄內 `manifest.json`＋數位簽章＋DB upsert）；`Verify(directory)`（讀回逐檔重算：
        OK／TAMPERED／MISSING／EXTRA，附 Overall）；`Sign`／`VerifySigned`
    - App：`EvidenceWindow`（`--evidence`；admin）：選擇目錄→「建立 manifest」清單顯示各檔 SHA-256／大小→
      「驗證完整性」重新掃描比對＋數位簽章，狀態列「完整性：N 檔 OK／M 篡改」；`--evidence <dir>` 初始化目錄
    - 測試：`EvidenceManifestTests`／`EvidenceSignerTests`（建 manifest 遞迴與排除自身、round-trip、
      篡改偵測 TAMPERED、缺檔 MISSING、新增檔 EXTRA、簽章產生／驗證、私鑰更換→簽章驗證失敗；約 +20；總 466→約 **486**）
    - harness `evidencecheck`→**EVIDENCE_OK**（seed 建探針證據目錄 2 檔→開 `--evidence`→建立 manifest 全 OK→
      篡改一檔→驗證出 TAMPERED→還原再驗證 OK）；回歸 **ALARMMANAGER／DEWARP／MAPFOV／SHARE／ANALYTICS**
    - 驗收：App Release 0 error、全約 **486**、EVIDENCE_OK、回歸綠、CI 綠
    - **已驗收（M53 snapshot）**：App Release 0 error；測試 **486/486**（Storage 304→**324**＝
      `EvidenceManifestTests` 13＋`EvidenceSignerTests` 7；總 466→486）；harness `evidencecheck`→**EVIDENCE_OK:create=2;tampered=1;restored 驗證通過**
      （seed `--evidence` 建 `evidence\probe` 2 檔＋簽屬 manifest→開 `--evidence` 視窗→建立→篡改 clip.bin→
      驗證出「1 篡改」＋TAMPERED→還原→「2 檔 OK … 驗證通過」）；回歸 **ALARMMANAGER／DEWARP／MAPFOV／SHARE／SHAREWIN／ANALYTICS 全綠**；
      HEAD＝`6f4cccf`、CI `35524266378` success；定義段（`2175b55`）已 commit＋push

30. **M54 ＝智慧警報（Smart Alerts）（§14.7 #8 P2：「物體感知的事件篩選」，搭配 §5.5 警報規則）**：
    - 背景：M37 已建 `alert_rules`＋NotificationService 誤發門檻，M38/M47 回報審核；本里程碑補「**智慧化防抖＋類別篩選**」：
      處理警報洪泛（靠偵測產生的 `ai_line_cross`／`ai_intrusion` 事件短時間大量進 alarm_events）與誤警
    - Storage schema **v20**：`alert_rules` 增列
      `match_event_types`（TEXT NULL；JSON 陣列，空/NULL＝全部事件）、`frame_minutes`（INTEGER DEFAULT 0；
      大於 0 時「同頻道＋同條件」N 分鐘聚合窗）、`min_events_in_window`（INTEGER DEFAULT 1）；
      `AlertRuleRepository` 存取擴充（Get/Add/Update 含三欄；既有 migration 補 `ALTER TABLE` 冪等）
    - Alarms 新檔 `SmartAlertEvaluator`：`Evaluate(evalMinUtc, windowMinutes, rules, openAlarmEvents)` →
      對窗內 `alarm_events`（按 channel_id＋event_type）計數，跨過 `min_events_in_window` 才觸發
      `AlertTrigger`（rule 摘要＋計數＋首末時間）；`NotificationService` 改為：`NotifyEvents` 先過
      SmartAlert 聚閤（`frame_minutes>0` 的規則只在窗滿時發送一次摘要，避免逐筆洪泛）
    - App：SettingsWindow 警報規則頁增列「事件類型篩選」「聚合窗(分)」「窗內最少事件」三欄編輯；
      AlarmManager 摘要列顯示「窗內 N 事件聚合」註記
    - 測試：`SmartAlertTests`（v20 遷移冪等、兩欄 round-trip、聚合窗計數與 min 門檻、跨窗不觸發、
      match_event_types 篩選、NotificationService 聚合發送一次；Storage＋Alarms 約 +22；總 486→約 **508**）
    - harness `smartalertcheck`→**SMARTALERT_OK**（seed 產 2 頻道各 5 筆 `ai_intrusion` 事件＋規則
      frame_window 5min／min 3→開警報規則 UI 確認三欄→評估器輸出窗內 count；回歸 6 支 6 枝全綠）
    - 驗收：App Release 0 error、全約 **508**、SMARTALERT_OK、回歸綠、CI 綠
    - **已驗收（M54 snapshot）**：App Release 0 error；測試 **505/505**（Storage 324→**329**＝
      `SmartAlertColumnsTests` 5；Alarms 130→**144**＝`SmartAlertEvaluatorTests` 14；總 486→505）；
      harness `smartalertcheck`→**SMARTALERT_OK:seed-rows=1;add-rows=2;report=已新增「harness ui」（2 分聚合窗≥2 筆）;eval=SMARTALERT_EVAL_OK:count=5;reached=true;rule-threshold=3**
      （seed `--smart-alert` 清 harness* 規則＋插 5 筆窗內 ai_intrusion＋規則 frame5/min3→`--smart-alert-eval` 窗內 count=5 達標→開 `--settings` 規則頁選取 harness smart→UI 三欄新增 harness ui）；回歸 **ALARMMANAGER／DEWARP／MAPFOV／SHARE／SHAREWIN／ANALYTICS 全綠**；
      HEAD＝`d893554`、CI `35525399049` success
    - 實作差異：NotificationService 未改動——聚合抑制由 **MainWindow 通知橋接** `ShouldSuppressSmartAlert` 執行
      （命中 frame_minutes&gt;0 規則且窗內含本筆計數不足 `min_events_in_window` 時丟棄）；`SmartAlertEvaluator.Matches`
      含 Enabled 檢查

31. **M55 ＝異地備援自動複製（Off-Site Replication）（§4.4 P1「異地」＋§14.4 備份線）**：
    - 背景：M45 已做本機備份（zip＋SHA-256 manifest）；本里程碑補「**定時自動複製備份檔案至 NAS／雲端掛載路徑**」P1 承諾
    - Storage schema **v21**：`offsite_jobs`（id, source_path, destination_path, interval_minutes, enabled,
      last_run_utc, last_result, last_error, consecutive_failures, created_at）；
      `OffsiteReplicationRepository`（Upsert／SetRunResult／ListEnabled／List）
    - Storage 新檔 `OffsiteReplicationService`：`RunOnce(job)`（列舉 source 下全部檔案，遞迴複製至 destination
      保留相對結構，僅複製「mtime ≥ last_run_utc」的新檔；完成後寫 last_run_utc；失敗記錄 last_error 且
      consecutive_failures++，成功歸零）＋`AutoSync()`（依各 job interval_minutes 定時觸發）
    - App：SettingsWindow 備份頁增「異地複製」群（啟用／來源備份目錄／目標路徑（UNC 或本機資料夾）／間隔(分)）＋
      「立即複製」按鈕＋狀態（last_run／last_result／連續失敗數）；MainWindow 啟動 `OffsiteReplicationService.AutoSync`
    - 測試：`OffsiteReplicationTests`（v21 冪等、複製新檔並跳過已存在未變更、失敗記錄與復試、consecutive_failures
      計數與歸零、interval 未到不重跑；約 +20；總 505→約 **525**）
    - harness `offsitecheck`→**OFFSITE_OK**（seed `--offsite <src> <dst>` 建來源樣本檔→服務複製→斷言相對結構
      檔案存在＋job last_run 更新→刪目標→再複製成功）；回歸 **SMARTALERT＋6 支＋EVIDENCE**
    - 驗收：App Release 0 error、全約 **525**、OFFSITE_OK、回歸綠、CI 綠
    - **已驗收（M55 snapshot）**：App Release 0 error；測試 **512/512**（Storage 329→**336**＝
      `OffsiteReplicationTests` 7；總 505→512）；harness `offsitecheck`→
      **OFFSITE_OK:prepare=OFFSEED_PREPARE_OK:job=2;result=OK;lastRun=…;fails=0;rerun=OFFSITE_RERUN_OK:result=OK;consecutive=0;files=3**
      （seed `--offsite-prepare` 建來源 3 樣本檔→Upsert＋RunOnce→斷言相對結構＋last_run→刪目標→`--offsite-rerun` 復原，檔名 harness 內含）；回歸 **EVIDENCE／SMARTALERT／ALARMMANAGER／DEWARP／MAPFOV／SHARE／SHAREWIN／ANALYTICS 全綠**；
      HEAD＝`9b1b37a`、CI `35526440981` success
    - 實作行為：主視窗每分鐘 `RunOffsiteDueJobs`（DispatcherTimer）；SettingsWindow 備份頁含
      OffsiteSourceBox／OffsiteDestBox／OffsiteIntervalBox／OffsiteRunNowButton，狀態列顯示
      last_result／連續失敗數（DB 權威來源為 offsite_jobs＋app_settings `offsite.*`）

32. **M56 ＝事件中心搜尋／篩選／CSV 匯出（Event Center Query ＆ Export）（§5.2 分析中心）**：
    - 背景：AlarmManager 目前僅全量列示；補「條件查詢＋匯出」讓調查人員快速收斂
    - Storage `AlarmEventRepository` 新 `Query(channelId?, eventType?, keyword?, fromUtc?, toUtc?, limit)`：
      WHERE 以 AND 合併、event_type 相等、keyword 對 detail LIKE（大小寫不敏感 UTF-8）、時間閉區間、
      `ORDER BY start_utc DESC LIMIT`；原 ListAll/ListByRange 不變
    - App AlarmManager：上方新增篩列（頻道／事件類別／關鍵字／起始-結束時間）＋「套用」「清除」＋
      「匯出 CSV」（依目前查詢導出 UTF-8 BOM 檔，含時間序(UUID)與全欄位標題）
    - 測試：`AlarmEventQueryTests`（頻道過濾、事件類別、關鍵字 LIKE、時間範圍、合併條件、limit＋排序，
      約 +7；總 512→約 **519**）
    - harness `eventquerycheck`→**EVENTQUERY_OK**（seed `--event-probe` 插 5 筆不同事件類別/頻道/時間
      harness* 探針→`--event-query` 驗證 keyword/類別/時間篩選命中數→App AlarmManager 篩列套用後 rows 對應、
      匯出 CSV 檔存在且行數＝命中＋1 標題）＋回歸 **OFFSITE＋8 支**
    - 驗收：App Release 0 error、全約 **519**、EVENTQUERY_OK、回歸綠、CI 綠
    - **已驗收（M56 snapshot）**：App Release 0 error；測試 **518/518**（Storage 336→**342**＝
      `AlarmEventQueryTests` 6；總 512→518）；harness `eventquerycheck`→
      **EVENTQUERY_OK:alpha3;beta2;gamma1;combined1;miss0;csvRows=8;bom=True;uiBeta=2;uiMiss=0;uiAll=8**
      （seed `--event-probe` 插 6 筆 eq-* 探針→`--event-query <kw> <type|NONE> <ch|NONE>` 驗證 keyword／類別
      命中數→App `--events-export <path>` 產 UTF-8 BOM CSV（標題＋探針行存在）→App `--events` UI 篩列
      KeywordBox＋ApplyQueryButton 應用後 rows 與 0 命中與全量對應）；回歸
      **OFFSITE／SMARTALERT／EVIDENCE（連同上述三支均連跑 2 次）＋ALARMMANAGER／DEWARP／MAPFOV／SHARE／SHAREWIN／ANALYTICS 全綠**；
      HEAD＝`1309112`、CI `35533344119` success
    - 實作位置更正：篩列＋匯出實作於 **`EventCenterWindow`（事件中心）**（§5.2 分析中心正是事件中心；
      AlarmManager 為分診板不列全量）。`QueryArgs.Keyword` 對 `detail` 做 `LIKE %kw%`（BuildWhere＋BindWhere，
      大小寫不敏感 ASCII）；匯出經另存對話框（OnExportCsvClicked，共用 `EventCenterWindow.WriteCsv` 靜態
      UTF-8 BOM 寫檔，欄位＝時間(本地)/頻道/類型/持續(秒)/詳情/快照/狀態/指派/備註）＋ CLI `--events-export <path>`
      供 E2E（該路徑關窗前設 `_exiting=true` 以避免 M26 收進系統匣而不退出）

33. **M57 ＝多語言介面 i18n（繁中／簡中／English）（§14.7 P1「多國語言」承諾）——已驗收**：
    - 已交付 commit＋push＋CI 綠：Storage `I18n` 靜態字串表（`zhHant`／`zhHans`／`en` 三表，鍵集合一致）
      ＋ `IsSupported/Normalize/Load/SettingsRepository.Get/ForLang/Get`（`Get(lang,key)` 缺 key fallback 繁中再回 key）；
      app_settings 鍵＝`ui.lang`（`I18n.SettingKey`，預設 `zh-Hant`）；**實作以靜態字串表取代原 XAML 資源字典設計**
      （避免 .xaml 資源讀取／編譯期漂移，Storage 可測、不依賴 WPF）；`Localizer`（App：`Init/T/SetLang`）
    - 套用範圍：MainWindow 標題＝`Brand.Title`（`OnLoaded` 於 `Localizer.Init` 之後設定；`RefreshTitle()` 供即時切換）；
      EventCenterWindow 標題＋過濾列（頻道/範圍/類型/關鍵字 label、`ApplyQueryButton`→套用、`RefreshButton`→重新整理、
      `ExportCsvButton`→匯出 CSV、`RangeCombo` 四項、全部頻道/全部類型）於 `OnLoaded` `ApplyI18n()` 套用；
      AlarmManagerWindow 標題＝`AlarmManager.Title`；SettingsWindow 標題＝`Settings.Title`＋PageGeneral「介面語言」群
      （`LangCombo`：繁體中文 zh-Hant／简体中文 zh-Hans／English en；切換→`Localizer.SetLang` 寫回 `ui.lang`＋
      刷新自身標題＋`MainWindow.RefreshTitle()`；`LangHintText` 提示）
  - 測試：`I18nTests` **7/7**（Storage 342→**349**；缺 key fallback、三語言命中、負載預設等）；全 **518→525**
    （Storage 349＋Alarms 144＋Devices 24＋Licensing 8）
    - harness `i18ncheck`→**I18N_OK**（seed `--i18n <lang>` 寫 `ui.lang`（seedtriage `--i18n`／`--i18n-clean`）→
      App `--events --settings` 啟動→比對三語言下 MainWindow 標題（HeliVMS 監控中心／监控中心／Surveillance Center）、
      SettingsWindow 標題（設定中心／设置中心／Settings）、`LangCombo` 選項、`ExportCsvButton` 文字（匯出 CSV／导出 CSV／Export CSV））
    - 驗收：App Release 0 error、全 **525**、`I18N_OK`、回歸 **13 支全綠**、CI 綠；工作目錄空白
    - 雷區附註：**BIG5/ACP=950 環境下 harness 讀視窗標題必須 `EntryPoint="GetWindowTextW"`**（預設 `GetWindowText` 綁 ANSI 版，
      簡體「监」U+76D1 不在 Big5 → 讀回「?」假象，實為讀取端編碼問題，App 本身無誤）；`--settings` 開窗時
      SettingsWindow ctor 設 `LangCombo.SelectedIndex`→觸發 `OnLangChanged`→會寫回 `ui.lang`（同值，無害）

34. **M58 ＝智慧分析模組二（長時間徘徊 Loitering／靜止物 Stationary／車流統計 Traffic／熱區圖 Heatmap）——已驗收**：
    - 選定理由：M52 定義段明示「跌倒/尾隨/車輪/徘徊（跨線追蹤）待交付」；ARCHITECTURE §5.6 表
      剩下需遞交付之模組＝`ai_loitering`（P2）、`ai_stationary`（P3）、`ai_traffic`／`heatmap`（P2），
      前置條件「需 §5.7 追蹤」；本里程碑以 **zone dwell＋cell 熱區近似**落地（不需完整目標追蹤、不改 Detection 模型）
    - 模組語意（事件型別）：`loitering`→**ai_loitering**（目標在多邊形內連續停留≥`DwellSeconds` 觸發一次，
      離開重新進入才再觸發）；`stationary`→**ai_stationary**（同停留判定＋移動超過 `StationaryMoveTolerance=0.02`
      即重計停留）；`traffic`→**ai_traffic**（2 點線段跨線，依 `Direction` 篩選，每跨加計並輸出累計數）；
      `heatmap`→**無事件**（5×5 格子依 `(row,col)=(floor(y·5),floor(x·5))` 累計，`HeatmapCells/HeatmapTotal` 呈現）
    - `AnalyticsModuleKinds` 增 `Traffic`／`Heatmap`（All 含 7 模組）；`IsLineModule`（line_cross／traffic 允許 2 點幾何）；
      catalog `ModuleInfo.EventType` 改可為 null（heatmap）；`AnalyticsZoneEvaluator` 增 dwell/fired/lastPos/trafficCount/heat
      狀態＋`Reset` 全清
    - **schema v21→v22**：`analytics_zones` 之 `module` CHECK 擴充 `traffic`／`heatmap`（SQLite 無法改 CHECK，
      重建資料表並複製資料＋重建索引；`ExpandAnalyticsModulesV22` 於 `version<22` 執行）；真 DB 已實證遷移（`PRAGMA user_version`＝22）
    - 測試：Alarms `AnalyticsModulesTests` 144→**158**（loitering 觸發/重觸發/0 停留、stationary 靜止觸發/移動重計、
      traffic 累計/方向篩選、heatmap 累計/reset/格 clamp/遠離不計、Engine 寫 ai_loitering + heatmap 無事件、catalog 對映）；
      Storage `AnalyticsZoneTests` 349→**353**（traffic 2 點、heatmap 多邊形、loitering dwell 寫回、v22 重初始化冪等）；全 **525→543**
      （Storage 353＋Alarms 158＋Devices 24＋Licensing 8）
    - UI：`AnalyticsWindow` 模組 Combo 現含 7 模組；「以範例偵測評估」支援新模組（traffic 用雙側 probe 計跨線、
      loitering/stationary 依 `DwellSeconds` 餵跨秒幀、heatmap 餵 25 幀後顯示「熱區：(col,row)xN」前 3 格）
    - harness `analytics2check`→**ANALYTICS2_OK**（seed `--analytics`（7 模組）、開窗 seed≥7、UI 依序新增並評估
      車流統計→ai_traffic／長時間徘徊→ai_loitering／靜止物→ai_stationary／熱區圖→「熱區：」、
      停用熱區再評估→無熱區、刪除→行數還原；雷區：UIA 選 ComboBox 項需對 `ListItem` 之 child Text 比對 DisplayName）；
      seedtriage `--analytics` 增 4 新模組 seed；UI 新增區名採「harness ui …」以便 `--analytics-clean` 淨空
    - 驗收：App Release 0 error、全 **543**、`ANALYTICS2_OK`、回歸 **15 支全綠**（13 既有支＋i18ncheck 保持綠＋新 analytics2check）、CI 綠（`35541102070`）；
      工作目錄空白、真 DB `user_version`＝22

35. **M59 已完成**（commit `386faa5`，CI `35541998741` success）：智慧分析模組三——**尾隨／逆行**（`ai_tailgating`，§5.6
    P3、§14.7 #6 KiwiVision 方向控制對標），M52 定義段「尾隨 …（跨線追蹤）待交付」之收尾：
    - 模組語意（沿用既有欄位）：`tailgating`＝雙點線段管制流向模組；`Direction`＝管制流向（`a_to_b`／`b_to_a`，
      `both`＝無管制）；`DwellSeconds`＝**尾隨跟隨窗（秒）**。**逆行 counter-flow**＝`Direction != both` 且跨線方向
      違反（`方向 != Direction`）；**尾隨 following**＝另一 track 於 `DwellSeconds` 窗內同方向跨線（detail 含
      `尾隨 <方向>（<elapsed>s 窗）`）。以 per-key 跨線狀態近似，不需 §5.7 完整目標追蹤（track_id/Hungarian/ReID
      留作獨立 infra 里程碑）；`both` 時無逆行、`DwellSeconds=0` 時無尾隨窗
    - Storage：`AnalyticsModuleKinds`＋`Tailgating="tailgating"`（All 8 模組）、`IsLineModule` 納入；schema
      **v22→v23** `ExpandAnalyticsModulesV23`（重建 analytics_zones，module CHECK 再擴充含 tailgating，保留資料＋重建索引）
    - Alarms：catalog ＋`EventTailgating="ai_tailgating"`（授權位元 `analytics.tailgating`）；`AnalyticsZoneEvaluator`
      ＋`_followRecent`（每分流向最近跨線清單，跨線時先 prune `DwellSeconds` 窗外再查其他 key＝尾隨、方向違反＝逆行）；
      `Reset()` 一併清空
    - App `AnalyticsWindow.xaml.cs`：評估分支優先處理 Tailgating（`ProbePoints(zone)` 取線段法向量兩側點，
      `probeA` 於 t0→t+1s 跨線、`probeB` 於 t+1s→t+2s 同向跨線，`DwellSeconds>=1` 才送第二軌）＋既有 IsLineModule
      分支改用 `ProbePoints` 重構
    - 測試：Alarms 158→**166**（尾隨窗內命中/窗外不中/雙向無逆行有尾隨/逆行違反管制流向/順向非逆行/同軌不自我尾隨/
      Reset 清史/catalog 對映/engine 逆行落地）、Storage 353→**355**（tailgating 雙點收受、IsLineModule、重入冪等）；
      全 **553**（Storage 355＋Alarms 166＋Devices 24＋Licensing 8）
    - seedtriage `--analytics` ＋「harness tailgating」（雙點、dwell=2）；harness `tailgatecheck.ps1`→**TAILGATE_OK**
      （seed=8；DB 直查 tailgating 種子；UI 新增「尾隨/逆行」→ 評估首筆 `ai_tailgating`；
      停用再評估→未命中；刪除→列數還原）；回歸 **16 支全綠**（15 既有＋tailgatecheck，含 analytics2check seed>=7 不受影響）
    - 真 DB `C:\HeliVMSData\index.db` 實證 **v22→v23**（`PRAGMA user_version`=23）、analytics_zones 已淨空
      （harness 前綴名、`--analytics-clean` 回收）

36. **M60 已完成**（commit `ef567a7`，CI `35543378978` success）：統圖報表／管理報表（§14.7 #9 P2、§11.7）——
    錄影時數／斷線次數／容量趨勢／AI 事件統計（VIP），唯讀既有 `segments`／`alarm_events`／`channels` 不需新表：
    - Storage 新 `ReportRepository`（ISO 文字時間字串 `>= from AND < to` 比對）：`ListRecordingSummary`
      （各頻道 `status='final'` LEFT JOIN channels SUM duration_sec/3600 與 size_bytes，無錄影頻道列 0）；
      `ListCapacityTrend`（`substr(start_time,1,10)` 逐日位元組＋時數分組）；`GetDisconnectCount`
      （`event_type='offline'`）；`ListAiEventSummary`（按 `event_type` COUNT 之後依筆數降冪）
    - App 新 `ReportsWindow`（統圖報表）：期間 Combo（近24小時／近7日／近30日／全部）＋「重新整理」
      （四區文字：錄影時數總覽＋逐頻道、斷線次數、容量趨勢、AI 事件計數）＋「匯出 CSV」寫
      `<dataRoot>\reports\report-<期間>-<時間戳>.csv`（UTF-8 BOM，`類別,項目,值1,值2` 列）
    - MainWindow 第二列工具列＋「報表」按鈕（admin 限定，同分析情境）；命令列 `--reports` 直開；
      匯出成功狀態列顯示檔案路徑
    - 測試：Storage 355→**361**（ReportRepositoryTests 6：逐頻道聚總/窗外排除/無區段零值/逐日分組/
      斷線只算窗內 offline/AI 事件計數排序）；全 **559**（Storage 361＋Alarms 166＋Devices 24＋Licensing 8）
    - seedtriage ＋`--report-seed`（隔離頻道名 `harness reports`＋3 段 final＋offline×2/motion×3/ai_intrusion×1）
      ／`--report-clean`（刪該頻道，CASCADE 清區段與事件，不影響其他 harness）；harness
      `reportcheck.ps1`→**REPORT_OK**（seed→視窗四區相應文字→匯出→CSV 內容含類別與計數、隔離頻道
      時數 3.00、斷線≥2、趨勢含日期）；回歸 **17 支全綠**（16 既有＋reportcheck，交叉驗證既有 harness
      不受隔離頻道影響）
    - §11.7「每週排程郵寄」列為延伸（需 NotificationService 支援任意內容負載，納入後續里程碑評估）

37. **M61 已完成**（commit `d4303b0`，CI `35544108591` success）：目標追蹤原語單鏡（§5.7「v2 核心差異」）——
    以**純演算法庫**交付（無 UI、無新表、無模型），全數以單元測試驗證：
    - `Detection`（Alarms）尾加選填 `TrackId`（positional record 第 7 元，既有 6 元呼叫不變）
    - 新 `Tracker`（Alarms）：`Update(detections)` 逐幀以**匈牙利最小成本指派**（cost＝1‒IoU，僅同
      Class 且 IoU ≥ 門檻允許配對；雙器材質：無效配對 INF／dummy track 列 0／dummy det 欄 1.0）
      匹配既有 track；匹配成功→EMA 平滑 bbox＋`Misses=0`；未匹配既有 track→`Misses++`、逾
      `maxMisses` 即汰除（本幀遞增後立即淘汰）；未匹配新偵測→建新 track（id 由 1 起不再重用）
    - 輸出 `TrackState(TrackId, Class, X, Y, W, H, Misses)` 快照；`Reset()` 清空並重編號；可設定
      `iouThreshold`（預設 0.3）／`maxMisses`（預設 5）／`smoothAlpha`（預設 0.3）
    - 測試：Alarms 166→**177**（TrackerTests 11：跨幀同 id 穩定／多目標各自不互換／超出門檻發新 id 且
      原 track 記失蹤／連續失蹤逾 maxMisses 汰除且 id 不重用／EMA 收斂位移 0.3·Δ／跨類不匹配（同位置
      不同類→發新 id 原 track 失蹤）／Teleport 大跳→新 id＋舊適存／**無視偵測順序**（反序輸入依 IoU
      指派回原 track）／Reset 重編號／Misses 遞增與匹配歸零）；全 **570**（Storage 361＋Alarms 177＋
      Devices 24＋Licensing 8）
    - 定位：為 §5.6 靜止/尾隨/徘徊與 §5.9 圖搜提供穩定追蹤地基（§5.7「事件以 track_id 去重」）；
      UI／harness 待追蹤接入即時分析管線後之里程碑一併驗證

38. **M62 已完成**（commit `ea512cf`，CI `35545119891` success）：複合事件規則引擎（§5.10 Compound Rules /
     Rule Builder）——以**設定層＋評估引擎＋編輯器 UI**交付（無模型外接），全數單元測試＋`rulescheck` harness 驗證：
    - Storage **v23→v24**：`ai_rules(id, name, expression_json, actions_json, enabled, created_at)`
      （`CreateAiRulesTableV24`）；新 `RuleRepository`（Add 驗空名／空表達式、List／ListEnabled／Get／SetEnabled／
      Delete）——Storage 361→**368**（RuleRepositoryTests 7：v24、CRUD 全欄位 round-trip、ListEnabled 排除停用等）
    - Alarms 新 `RuleEngine`：表達式 JSON（camelCase、PropertyNameCaseInsensitive、null 省略）解析成
      `RuleExpressionNode` 樹——`match`＝`RuleEventPredicate`{eventType, channelId}（`within`/`count` 的 match
      是**葉節點**，不可巢狀全節點）／`all`／`any`／`not`／`within`{seconds}／`count`{atLeast, seconds}／
      `timeBetween`{start, end HHMM}；`RuleEvaluator` 僅取 **enabled** 規則、逐規則吞 `JsonException` 跳過損壞規則、
      緩衝窗預設 2h（Feed 前 Prune）、`timeBetween` 用本機時區（Unspecified 視牆鐘，測試跨機器確定）且跨午夜直翌日；
      `RuleActions`（severity/tag/notify）隨命中浮出——Alarms 177→**191**（RuleEngineTests 14：match/強制全子/
      任一子/反向/within 窗內外/count 下限/緩衝淘汰/時窗內外/跨午夜含整日/actions 浮出/停用＋損壞略過/Reset/
      表達式 JSON 往返）；全 **591**（Storage 368＋Alarms 191＋Devices 24＋Licensing 8）
    - App 規則編輯器：`RulesWindow`（AutomationId：RulesWindow／RulesAddToggleButton／RulesRefreshButton／
      RulesNameBox／RulesEnabledBox／RulesExprBox／RulesActionsBox／RulesConfirmButton／RulesCancelButton／
      RulesList／RulesEvalText／RulesStatusText）；主視窗工具列「規則」按鈕（報表旁）＋`--rules` 直開（admin 限定）；
      開啟即內建樣本評估（t0 motion→t+5 ai_intrusion→t+10 offline→t+20/t+25 motion，輸出 `RULE_MATCH:<name>`）
    - harness `rulescheck.ps1`（回歸第 18 支）：seedtriage `--rules-seed` 種 3 條（啟用「harness intrud-after-motion」
      ＝all[match ai_intrusion, within 60s motion]＋actions severity=critical/tag=escalated；停用
      「harness disabled-rule」同表達式 enabled=0；啟用「harness 3-motions-60s」＝count atLeast 3／60s motion）→
      `--rules` 開窗→RulesList `DataItem`＝3 且三名稱齊（用內層 Text 名稱）→`RulesEvalText` 含
      `RULE_MATCH:harness intrud-after-motion @ t+5s (severity=critical)` 與 `RULE_MATCH:harness 3-motions-60s @ t+25s`
      →`RulesStatusText` 共 3 條（啟用 2）→`--rules-clean` 回收 harness 規則；`RULES_OK`（回歸 18 支全綠）
    - 定位：規則（match/all/any/not/within/count/timeBetween）＋動作（severity/tag/notify）以 JSON 存 `ai_rules`，
      為擴充複合事件規則（§5.10）落地；事件引擎銜接（§5.4）留待 v2 即時分析管線里程碑

39. **M63 已完成**（commit `a6dceeb`，CI `35546979051` success）：影片摘要（Video Synopsis，§5.9
     「事件檢索→縮圖/動態幀預覽」落地）——把一段時間內的偵測動態幀收斂成**單張拼貼預覽＋manifest 摘要**；
     引擎**純 C# 位圖處理**（零外部依賴、零第三方，CI 可直接單元驗證）；**無 schema 變更**（v24 維持）：
    - Alarms 新 `BmpFile`（讀／寫 24-bit 無壓縮 BMP；讀側解析檔案＋DIB 標頭、只收 24-bit BI_RGB、
      正向/負向高度、含 row padding；寫側與 `BmpSnapshotWriter` 同位元組格式）、`ImageOps`
      （`ResizeNearest` 最近鄰縮放）、`SynopsisBuilder`（`Build(request, events, outputDir)`→
      取範圍內**有快照**事件（可選 `EventTypes` 白名單）→依 `StartUtc` 升序→每張快照解 BMP→
      縮至 320×180→row-major N 欄（預設 4）網格（空白格填黑）→輸出
      `synopsis-ch{n}-{from}-{to}.bmp`＋同名 `.json`（manifest：channel/from/to/thumb/columns/rows/
      first/last/frameCount/skipped/truncated/cells[{index,utc,event_type,source_snapshot}]）；
      回 `SynopsisResult`（路徑＋`SummaryText`＝「摘要：N 幀（RxC 網格）；HH:mm–HH:mm」＋截斷/略過註記）；
      `MaxFrames=200` 上限；無輸入→null**不產檔**；單幀損壞→黑格＋Skipped 計入）
    - App `SynopsisWindow`（AutomationId：SynopsisWindow／SynopsisChannelCombo／SynopsisFromBox／
      SynopsisToBox／SynopsisBuildButton／SynopsisImage／SynopsisSummaryText／SynopsisStatusText）：
      `AlarmEventRepository.ListByQuery`（Limit 2000）→引擎產出→Image 顯示拼貼（OnLoad）＋
      manifest 路徑於狀態列；From/To 預填近 24 時、`yyyy-MM-dd HH:mm`（local→UTC 查）；
      產出目錄 `dataRoot\synopsis`；主視窗工具列「報表／規則」旁加「摘要」按鈕＋`--synopsis` 直開
      （admin 限定）
    - 測試：Alarms `SynopsisTests` +15（BmpFile round-trip 像素等值、row padding 任意尺寸、拒非 24-bit、
      ResizeNearest 尺寸與取樣＋放大、依時排序 row-major(ai_person/tamper/motion)、無快照跳過、
      5 幀 4 欄→2 列數學＋sheet 維度 1280×360、空白格填黑、MaxFrames 截斷含「截斷」註記、
      EventTypes 白名單、損壞快照略過計入、空輸入→null 不產檔、manifest JSON 往返、SummaryText）；
      Alarms 191→**206**；全 **606**（Storage 368＋Alarms 206＋Devices 24＋Licensing 8）
    - seedtriage：`--synopsis-seed {db} {dataRoot}`（造 4 張 640×360 均色 BMP→`snapshots\synopsis-harness-{type}.bmp`
      ＋插 4 筆事件 motion/ai_person/tamper/ai_vehicle 於近 24 時、SnapshotPath 指向；先清舊 harness 事件、
      `INSERT OR IGNORE` 頻道 1「harness synopsis」）／`--synopsis-clean`（刪 harness 事件＋快照＋
      `synopsis\synopsis-*` 產物＋頻道）
    - harness `synopsischeck.ps1`（回歸第 19 支）：seed→`--synopsis` 開窗→預設選頻道 1→
      `SynopsisBuildButton` Invoke→輪詢 `SynopsisStatusText` 含 `synopsis-ch1-`→`SynopsisSummaryText`
      含「4」→由狀態列 regex 抽出 `.bmp`/`.json`→讀 BMP 標頭驗維度=`1280x180`（4 幀→1×4 網格）→
      manifest `frameCount=4`＋`cells.Count=4`＋`columns=4`→`--synopsis-clean` 回收→`SYNOPSIS_OK`
      （回歸 19 支全綠）
    - 排雷：BMP 讀取須自行處理 row 4-byte padding 與 bottom-up 行序（寫側底部行優先）；
      **清理階段 App 對 `synopsis*.bmp` 仍是開檔鎖**——harness finally 在 Stop-Process 後須
      `Start-Sleep 2` 等檔案釋放再 `--synopsis-clean`；harness 內不得有中文 literal（Big5）
    - 定位：§5.9「以事件縮圖→縮圖牆/預覽」第 0 版落地；CLIP 語意搜尋／ReID embedding／連續動畫
      短片留待 L1/v2（需模型或 ffmpeg 合成）里程碑

40. **M64 已完成**（commit `3418fe5`，CI `35547445399` success）：音訊事件偵測原語（Audio Sensing L0，
    §5.8）——**純 C# PCM 引擎**，輸入 16k mono PCM（`short[]`）：
    - Alarms 新 `AudioTriggerEngine`：`Feed(pcm, utc)` 跨批次累積樣本、滿 `BlockSamples=512`（約 32ms）
      切塊；每塊 RMS dBFS（`20·log10(rms/32768)`、0→−120 下限）；依絕對門檻分類
      `Quiet/Burst/Sustained/Silent`（`BurstDb=-12`/`SustainDb=-32`/`SilenceDb=-66`）→ 爆音連續
      `BlocksToConfirm=2` 塊開窗、持續音連續達 `SustainSeconds=2.0`s 開窗、斷路連續達 `SilenceSeconds=30`s
      開窗；冷卻 `CooldownMs=1500` 收尾（≥`MinEventMs=120`ms 才寫）、斷路事件在音訊恢復當下立即結算；
      寫 `alarm_events` `audio_burst`／`audio_sustained`／`audio_break`（無快照、無新表、schema v24 維持）、
      detail=`kind={burst|sustained|silence};peak_db=..;mean_db=..;duration=..ms`（`UpdateEnd` 收尾）；
      Feed 回傳逐塊 `(Kind, RmsDb, Utc)`；`AudioSignal`（進入作用中）＋`EventInserted`（鎖內 raise）；
      `Flush()`（停止前結算）／`Reset()`（捨棄開窗）／`Dispose()`；跨 Feed 收不完的樣本留待下批
    - 測試：Alarms `AudioTriggerEngineTests` +15（方波 20000→Burst 且 dB≈−4.29、方波 2000→Sustained
      且 ≈−24.29、全零→Silent 且 −120、Burst 開窗＋冷卻寫測 events（duration=1500ms）、未足確認數→無、
      Sustained 持續 320ms（閾 0.1s）寫測、不足閾值→無、斷路 160ms 音訊回歸→audio_break 立即結算、
      跨 Feed 300+212 湊塊、Reset 捨棄、Flush 結算、冷卻區隔兩爆音、detail 正規式、EventInserted 帶
      record 與庫內相符、非法參數 throw）；Alarms 206→**221**；全 **621**（Storage 368＋Alarms 221＋
      Devices 24＋Licensing 8）
    - 排雷（已驗）：方波 rms＝|幅度|（20000→−4.29dB 觸發 Burst、2000→−24.29dB 觸發 Sustained、
      5→−76.3dB 觸發 Silent、200→−44.3dB 為 Quiet）；dB 常數直接寫死避免 const 無法用 Math；`Flush()` 內
      `TryFinalize(true)` 的位置引數修復命名引數殘留；`Finalize` 於 Burst/Sustained/Silent 三路徑皆須
      處理「斷路中音訊回歸立即結算」，避免 silence 窗口洩漏
    41. **M65 已完成**（commit `caa92b6`，CI `35547924953` success）：運動感度自動調校＋信頼回饋統計
    （§14.7 #17 Auto-VMD，承 §5.11 信頼回饋方向）——把營運者的「誤報/確認」處置回饋
    （M38 `event_dispositions`）化為運動感度調校建議：
    - Alarms 新 `SensitivityAutoTuner`：`Summarize(events)` 統計最近窗內某頻道 `motion` 事件處置
      回饋（誤報 `false_alarm`→FP、已確認/已處理 `acknowledged`/`actioned`→TP、pending/無處置不計）；
      `Suggest(channelId, current, nowUtc, windowDays=14)`＝`ListByRange` 取窗→篩 `motion`→決策：
      1）已處置樣本 < `MinDecisionSamples=5`→不建議（Reason「樣本不足」）；2）誤報率 >
      `MaxFpRate=20%`→調高感度門檻（`+Step=0.05`，減少誤報）；3）誤報=0 且 TP ≥ `DownshiftSamples=20`
      →調低（`−Step`，避免過度遲鈍）；4）其餘→維持已達標；建議收斂 `[0.10, 0.90]`；回
      `SensitivitySuggestion(現值/建議值/是否變更/誤報率/TP/FP/Reason 簡中)`；呼叫端（App 後續接線）以
      建議套回 `channels.motion_sensitivity`；語意=「比值門檻」（誤報多→調高）
    - 測試：Alarms `SensitivityAutoTunerTests` +12（無處置→不建議、樣本<5→不建議、誤報率 62.5%→
      0.50→0.55、恰等 20%→不調、達標 16.7%→維持、FP=0 且 TP=25→0.60→0.55、調低收斂 0.10、調高收斂
      0.90、已到 0.90 clamp 不再變更、Summarize 分狀態計數（pending 不計）、窗外(-15d) 事件忽略、
      非 motion（ai_person）不計）；Alarms 221→**233**；全 **633**（Storage 368＋Alarms 233＋Devices 24＋Licensing 8）
    - 排雷（已驗）：`ListByRange` 之 `COALESCE(status,'pending')` 無法區分無處置與明確 pending——兩者
      皆不計為回饋樣本；**測試樣本須全數落在 14 天窗內**（用 `.AddHours(-i)` 而非跨 25 天）；浮點
      `0.6−0.05` 非精確 0.55→斷言用 `Assert.Equal(..., 3)` 精度
    - 定位：§5.11「標記誤報/正確→統計 FP/FN→建議調感度」落地（motion 線）；「自動套用」+事件中心
      「建議感度」按鈕與 AI 模型線 FP/FN 統計留待接線/模型里程碑

42. **M66 已完成＝Legal Hold 保存鎖定（§14.1 #13）**：
    - 背景：§14.1 #13「單通道指定時段暫時封存不被配額汰除；沖銷前需高權限操作並留稽核」。錄影配額
      汰除（`RetentionService.Apply` §9）依最舊開始刪 final 區段；M66 加「保存鎖定」讓指定時段在鎖定期間
      **豁免汰除**，沖銷留稽核（誰/何時/原因）。潛在使用：個資法案件、爭議錄影保存（Legal Hold）
    - **schema v24→v25**：新表 `legal_holds(id, channel_id, from_utc, to_utc, reason, created_by,
      created_at, revoked_at, revoked_by, revoked_reason)`；rim 權限（沖銷）由 App 層 `SessionContext.IsAdmin`
      把關
    - Storage 新 `LegalHoldRecord`＋`LegalHoldRepository`（Storage）：`Add(ch, from, to, reason, by, at)`
      （from<to 否則 throw）／`ListActive()`（未沖銷，按 from 升序）／`ListAll()`（含沖銷欄位，按 from 降序）
      ／`Revoke(id, by, reason, at)`（軟刪：填 revoked_*；回傳是否影響行＝不存在或已沖銷→false）
      ／`IsLocked(channelId, startUtc, endUtc)`＝存在未沖銷且 `from<=end && to>=start`（重疊）
    - Recording `RetentionService`：ctor 增可選 `LegalHoldRepository? legalHolds=null`；`Apply` 由
      `ListOldestFinal(1)` 改批次 `ListOldestFinal(64)`：由最舊掃描，鎖定區段跳過、刪第一個未鎖定；
      全批鎖定→break（保守、不漏刪）
    - App `LegalHoldWindow`（AutomationId：LegalHoldWindow／LegalHoldChannelCombo／LegalHoldFromBox／
      LegalHoldToBox／LegalHoldReasonBox／LegalHoldAddButton／LegalHoldList／LegalHoldStatusText）：
      頻道 combo＋起/訖（`yyyy-MM-dd HH:mm` local→UTC 檢查 to≥from）＋原因→加鎖；清單顯示
      頻道/起/迄/原因/建立人/狀態（作用中/已沖銷）；選列→沖銷按鈕（需 admin）；主視窗工具列
      「摘要」旁加「保存鎖定」按鈕＋`--hold` 直開（admin 限定）；保留迴圈 `RunRetentionLoopAsync`
      以 `new RetentionService(_segRepo, recordings, new LegalHoldRepository(_store))` 注入
    - seedtriage：`--hold-seed {db} {dataRoot}`（清 harness 舊資料→建 3 段真實檔案
      recordings\hold-harness-N.mp4 於 now-4h/-3h/-2h（各 60 秒、50B）→於「首頻道（DB 中 id 最小的）
      」加鎖 from=now-4h-30s to=now-4h+30s（覆蓋最舊段））／`--retention-run {db} {dataRoot} {quotaBytes}`
      （`RetentionService`＋`LegalHoldRepository`．Apply→HOLD_RUN_OK:deleted=..;survivors=..）
      ／`--hold-lock-check {ch} {from} {to}`（IsLocked→LOCK_CHECK:locked=true/false；db 迴圈 i+=3）
      ／`--hold-clean {dataRoot}`；另 `--rec-seed {db} {dataRoot}` 以外部 ffmpeg 產真實 h264 片段並登錄
      final 段（供 dewarp/redaction harness 環境依賴）
      ／`--hold-clean`
    - harness `holdcheck.ps1`（回歸第 20 支）：`--hold-seed`→`--retention-run quota=0` 驗證（最舊被鎖
      保留、較新 hold-harness 段被汰除；deleted≥2 動態斷言，檔案存在/消失為準）→`--hold-lock-check`
      （鎖內 true／鎖外 false，頻道取 seed 回報值）→`--hold` 開窗 UI 冒煙（AutomationId 存在、
      填時段＋原因、Add→StatusText 含「已加鎖」）→`--hold-clean`→`HOLD_OK`
    - 測試：Storage +8（LegalHoldRepositoryTests×6：Add→ListActive、Add 非法範圍 throw、IsLocked 重疊
      矩陣（內/外/左/右/恰等/他頻道/沖銷後 false）、Revoke→ListActive 消失＋ListAll 含沖銷、Revoke
      不存在/已沖銷→false、ListAll 排序與欄位；RetentionServiceTests×2：鎖定最舊→汰除其餘 2 段留 1
      、全鎖定→不刪）；Storage 368→**376**；全 **641**（Storage 376＋Alarms 233＋Devices 24＋Licensing 8）
    - 排雷（已驗）：`ListOldestFinal(1)` 無法跳過鎖定段（會無限刪同一段）→已批次 ListOldestFinal(64) 逐段掃
      並 break；hold 重疊判定含端點（`from<=end && to>=start`）——harness 測試曾因 hold 訖點恰等 seg2
      起點（含端點重疊）而多鎖一段；ISO 時間儲存僅毫秒精度（測試基準須截斷 ms）；seedtriage db 迴圈會
      吃走未知 flag 的位置參數（`--hold-lock-check` 曾把幾可 db 誤判，需在 db 迴圈 i+=3/i+=2）；
      `--rec-seed` 的 ffmpeg 不可 RedirectStandard*（pipe 死鎖）→改為繼承 console；
      holdcheck 的 retention 斷言 `deleted=2` 會受共享 DB 其他 final 段干擾（如 rec-harness）→改
      `deleted>=2`＋檔案存在/消失斷言；harness 一律自 seed（rec-seed）避免依賴測試流在跑
    - 完成狀態：全 **641/641**（Storage 376＋Alarms 233＋Devices 24＋Licensing 8）；回歸 **20 支**
      harness 全綠（含新 holdcheck）；feat commit＝**`e183239`**；CI＝**`35550100603`** success；
      排雷已驗（均列於上方 排雷（已驗））

43. **M67 已完成＝Bitrate Governor（依頻寬負載動態調整，§14.7 #12 SVR/自適應品質，L0 純演算法）**:
    - 背景：§14.7 #12「依頻寬/負載調整」為錄影模式表中的「SVR/自適應品質」列（未來項）。落點：
      SVR 或多鏡高清場景下，總可用頻寬跌破總碼率（擁塞/上行受限）時，逐通道動態降碼率，優先保住
      **事件中（motion/AI）通道**與**高優先權**通道；M67 交付「分配演算法」L0（純 C# 無新依賴，
      對齊 M64/M65 先例），App/編碼器接線留待後續接線里程碑
    - `HeliVMS.Recording` 新 `BitrateGovernor`：
      - `Adapt(budgetBps, IReadOnlyList<GoChannel>)`→`ReallocateResult`
        （`GoChannel(ChannelId, CurrentBps, Priority, InEvent)`；`ReallocateResult(Targets,
        AllocatedBps, ThrottleRatio)`）
      - 權重：`w_i = clamp(Priority,1..5) * (InEvent ? 4 : 1)`；配額：各通道
        `Budget * w_i / Σw`（整數）；餘額以最大餘數法分配至最高權重通道，保證 `ΣTarget == Budget`
        （Budget>0 且有通道時）
      - 邊界：Budget<=0→全 0＋ratio 0；Σw==0（空清單）→空 Targets；CurrentBps<0→視 0；
        ratio = ΣCurrent>0 ? Allocated/ΣCurrent : 0
      - 語意：事件通道相較同優先權非事件通道得 4 倍頻寬；高優先權 5 得 5 倍於優先權 1；
        結果保證不超項預算（可用於 SVR 總頻寬閘控）
    - 測試：Storage.Tests 新增 `BitrateGovernorTests`（+12：預算 0 全停、相同通道均分且 Σ==預算、
      事件 4 倍、優先權倍率、隨機多通道 Σ==預算 不超、同優先權事件勝出、全 Current 0 → ratio 0、
      負 Current 視 0、Priority 越界收斂、空清單、餘數精確分配 Σ==預算、ratio 數值）；Storage
      376→**388**；全 **653**（Storage 388＋Alarms 233＋Devices 24＋Licensing 8）
    - 排雷（已驗）：整數除法先 floor 再 largest remainder 補餘（不能四捨五入）——餘數以
      「目標/權重」比值最小者補 1（迭代 ≤通道數 次即收斂）；Priority 須 clamp(1..5) 才進權重
      （測試曾誤判 clamp 後仍均分——實為 5:1）；Σw==0／空清單／budget≤0 三極端各自回空或全 0；
      ratio 以 3 位有效比較；remu 迴圈以 `targets[i]>=budget` 防超配
    - 完成狀態：全 **653/653**（Storage 388＋Alarms 233＋Devices 24＋Licensing 8）；Build Release
      0 error；feat commit＝**`32cf833`**；CI＝**`35550716766`** success；排雷已驗
      （BitrateGovernorTests×12 見上；無 harness——純演算法里程碑）

44. **M68 已完成＝NTP 校時用戶端（RFC 5905 SNTP，診斷面板「NTP 即時校時」需求，L0 純 BCL）**：
    - 背景：NVR 錄影時間戳為證據完整性基礎（M53 已做檔案 hash）；診斷面板（Ctrl+K「輸入
      NTP 即時校時」）早有需求。M68 交付 SNTP 客戶端引擎 L0（`System.Net.Sockets` 純 BCL、
      零外部套件）：向 NTP 伺服器取 4 時間戳求「時鐘偏移/往返延遲/stratum」，供後續校時接線
      （M69 起接診斷/自動校時）
    - `HeliVMS.Core`（原為空專案容器）新增 `Ntp\`：
      - `NtpClient.QueryAsync(server, port=123, timeout=3s, ct)`→`NtpQuery?`（null＝逾時/網路錯/
        伺服器未同步/樣本無效）：「48 位元組 MODE3 VN4」T1=發送 UTC；收 4 時間戳
        T2(recv)/T3(xmit)/T4(本地收)；`offset=((T2-T1)+(T3-T4))/2`、`delay=(T4-T1)-(T3-T2)`；
        delay<0→null；stratum=0（kiss-o'-death）→null
      - `NtpQuery(Offset, RoundTrip, Stratum, LeapIndicator, Version)`
      - `NtpFrames.BuildRequestUtc()`／`TryParseResponse(48B, t1, t4, out …)`（靜態可測）
      - NTP epoch＝1900-01-01（秒 32bit＋分數 32bit），轉換以 AddTicks 保精度
    - 測試：Storage.Tests `NtpClientTests`（+10）：request 頭位元組 0x23 且 xmit 欄=本地 T1；
      TryParse 已知 T1..T4→offset=150ms/delay=700ms 精確值；不足 48B→null；stratum0→null；
      delay<0→null；負偏移樣本符號保留；epoch 轉換 roundtrip（含毫秒）；E2E 本機 fake server
      （+100ms 偏移，best-of-3 最小 RTT 樣本）→offset>90ms 且 stratum=2；E2E 不回覆→300ms
      逾時回 null；ms 精度轉換驗證
    - 完成狀態：全 **663/663**（Storage 398＋Alarms 233＋Devices 24＋Licensing 8）；Build Release
      0 error；feat commit＝**`a5015a7`**；CI＝**`35585248329`** success；排雷已驗
      （見下）
    - 排雷（已驗）：E2E 假 server 若用 async（await ReceiveAsync）會受 xUnit 平行 threadpool
      飽和影響——deep 負載下回應延遲可達 ~190ms，offset=100−rtt/2 跌到 <90ms 而 flaky；
      改用**專屬 background thread 同步收發**後 RTT=純 loopback、四連跑全綠；另 NtpClient
      `SendAsync(byte[],int,ct)` 無 ct 重載（會誤綁 IPEndPoint）→用 `SendAsync(ReadOnlyMemory)`；
      negative-delay 樣本測試向量需 T3−T2>T4−T1 才會觸發 delay<0（先前向量算成 delay=0）

45. **M69 已完成＝PTZ 巡航排程器（`PatrolController`，L0 純演算法）**：
    - 背景：PTZ 預設點 API 已備（`GetPtzPresetsAsync/GotoPtzPresetAsync`）；「巡航」＝依時間
      排程依序巡迴預設點。M69 交付**排程器 L0**（純 C#＋注入時鐘，不碰真裝置），決定「現在該做
      什麼」（MoveTo 預設點／Wait 停留／Idle 停擺）——M70 再接 ONVIF `GotoPreset` 與巡航視窗
      ＋harness
    - `HeliVMS.Devices` 新增 `Ptz\PatrolController.cs`：
      - `PatrolStep(PresetName, Dwell)`、`PatrolPlan(Name, Enabled, WindowStart, WindowEnd,
        Steps)`（每日時段，Start&gt;End 視為跨午夜）
      - `PatrolController(plan, Func<DateTime>? clock=null)`；`Tick()`→`PatrolAction
        (Kind: MoveTo/Wait/Idle, PresetName?, Remaining?)`；`HoldFor(span)`／`HoldUntil`／
        `ReleaseHold()`（操作員/事件介入：停留凍結，release 後從剩餘續走）
      - 語意：停用或時段外→Idle；時段起始→MoveTo 第 1 點；停留期滿→依序下一點（末點→第 1 點
        循環）；同點停留中重複 Tick→Wait(剩餘)（不重複 MoveTo）；H標 凍結以 anchor 平移實作
        （hold 期間停留不消耗）；Step 為空→Idle
    - 測試：Devices.Tests `PatrolControllerTests`（+13，可變時鐘）：停用→Idle、時段未開始→Idle、
      時段開始→MoveTo 首點、停留期滿→下點、末點→首點循環、停留中重複 Tick→Wait 不重複 MoveTo、
      時段結束→Idle、隔日時段重入→由首點重來、Hold 凍結（anchor 平移）釋放後剩餘不變、空
      Steps→Idle、Dwell=0 每 Tick 即進、長時鐘累積跨點、跨午夜時段；Devices 24→**37**；全
      **676**（Storage 398＋Alarms 233＋Devices 37＋Licensing 8）
    - 完成狀態：全 **676/676**；Build Release 0 error；feat commit＝**`36cbb60`**；CI＝
      **`35586031203`** success；排雷已驗（見下）
    - 排雷（已驗）：單次 Tick 只推進一個步驟（Ledger 語意）——「長跳躍」測試須多次 Tick 累積，
      無法一跳跨多點；Hold 若只跳過不快進 anchor，dwell 會悄悄在 hold 期間流逝——需以
      「anchor += hold 期間」實作凍結；`static () =>` lambda 在該專案編譯失敗（CS1003）→改
      `(() => ...)`；WPF/Devices 專案 TreatWarningsAsErrors 啟用——XML doc 缺 `param` 標籤即
      CS1573 失敗

46. **M70 已完成＝PatrolRunner（PTZ 巡航執行器 L1：`PatrolController`→`IPtzExecutor` 動作接線）**：
    - 背景：M69 排程器產出「MoveTo/Wait/Idle」動作；M70 把動作化為實際執行——抽象
      `IPtzExecutor.GotoPresetAsync(presetName, ct)`，真實實作＝ONVIF `GotoPreset`（M71 接真
      裝置與巡航視窗＋harness）；M70 以注入 fake executor 全測試驗證序列
    - `HeliVMS.Devices\Ptz\` 新增：
      - `IPtzExecutor { Task GotoPresetAsync(string presetName, CancellationToken ct = default) }`
      - `PatrolRunner(PatrolController, IPtzExecutor)`；`Task<PatrolAction> TickOnceAsync(ct)`：
        取 `controller.Tick()`；MoveTo→await `executor.GotoPresetAsync(name)` 再回 action；
        Wait/Idle→直接回（不呼叫 executor）；executor 例外原樣傳出（App 迴圈可記錄）
    - 測試：Devices.Tests `PatrolRunnerTests`（+10）：首動作→executor 收到 "A"；時鐘推進
      A→B→C 依序收到；Wait 動作不呼叫 executor；停用/時段外 Idle 不呼叫；Hold 期間不呼叫、
      Release 後 anchor 平移續走剩餘、期滿才呼叫下一點；末點→首點再呼叫 "A"；executor 拋例外
      →由 TickOnce 傳出；同點停留中重複 TickOnce→executor 僅呼叫一次；Dwell=0→連續收到 B、C；
      時段結束後 Idle 不再呼叫、隔日重入由首點重新呼叫；Devices 37→**47**；全 **686**
      （Storage 398＋Alarms 233＋Devices 47＋Licensing 8）
    - 完成狀態：全 **686/686**；Build Release 0 error；feat commit＝**`ec03350`**；CI＝
      **`35586810793`** success；排雷已驗（見下）
    - 排雷（已驗）：Hold 釋放後依「anchor+hold 期間」續走——測試須先算準錨點（release 當刻
      anchor 即平移），否則等到較晚時刻才斷言會誤變 MoveTo；「乾淨樣本」與「被排程延遲樣本」
      的斷言要分級——NtpClient E2E 受並行負載影響 rtt 可達 100ms+，改多樣本取 min-RTT、
      RTT≤30ms 才做精確 >90ms 檢查（方向為正永遠驗）

47. **M71 已完成＝`OnvifPtzExecutor`（M70 `IPtzExecutor` 之 ONVIF 實作）**：
    - 背景：M69/M70 巡航引擎已把「MoveTo 預設點」抽象成 `IPtzExecutor.GotoPresetAsync`；M71
      提供真機實作：把「預設點名稱」解析成 ONVIF `PresetToken`（GetPresets）再 `GotoPreset`
      （GetCapabilities Category=All→PTZ XAddr→GetPresets→GotoPreset）。巡航名稱與播放清單
      層（App 視窗與 harness）留 M72
    - `HeliVMS.Devices\Ptz\OnvifPtzExecutor.cs`：
      - `OnvifPtzExecutor(OnvifDeviceService device, string profileToken)`
      - `GotoPresetAsync(presetName, ct)`：先 `EnsurePtzCapabilityAsync` 解析 PTZ XAddr；首呼
        `GetPtzPresetsAsync` 建 name→token 快取（惰性）；找不到名稱→throw
        `InvalidOperationException`（App 可顯示設定缺點）；命中→`GotoPtzPresetAsync`
      - 名→token 快取（不變假設，巡航常見；二次呼叫不再 GetPresets）
    - 測試：Devices.Tests `OnvifPtzExecutorTests`（+7，RecordingHandler SOAP mock）：
      name→token 解析後 GotoPreset 收到正確 token；無 PTZ 能力→throw；名稱不存在→throw；
      快取：二次呼叫不同名稱只 GetPresets 一次；GetPresets 空→throw；GotoPreset 傳對
      profileToken；多組 preset 選對 token；Devices 47→**54**；全 **693**
      （Storage 398＋Alarms 233＋Devices 54＋Licensing 8）
    - 完成狀態：全 **693/693**；Build Release 0 error；feat commit＝**`e63459f`**；CI＝
      **`35587474678`** success；排雷已驗（見下）
    - 排雷（已驗）：async helper 不可有 `out` 參數（CS1988）→改回傳 tuple `(Executor, Device)`
      並解構；SOAP mock 的 GetCapabilities 回應必須帶 `Capabilities/PTZ/XAddr` 且其路徑名稱
      空間任意（Descendants 找 LocalName）；GetPresets 回應 Preset 的 Name 在 `tt:Name`
      （`ElementAnyNs("Name")`）——mock 命名空間須齊備
48. **M72 已完成＝巡航管理視窗＋持久化（§47 計畫「巡航視窗與 harness」）**：
    - 背景：M69–M71 已有引擎（PatrolController/PatrolRunner/OnvifPtzExecutor/IPtzExecutor）
      ，但巡航設定無從落地。M72 補上「每通道一套巡航 Plan」的持久化與 UI 管理，收 §47 尾
    - 儲存層 schema **v26**（`SqliteStore`）：`patrols(id,name,channel_id,enabled,
      window_start,window_end,created_at,updated_at)`＋`patrol_steps(id,patrol_id,seq,
      preset_name,dwell_seconds)`＋`idx_patrol_steps_patrol_seq`
    - `HeliVMS.Storage\PatrolRepository.cs`：`Save(id?)`（id null→查同頻道既有並沿用其 Id，
      保「每通道單套」語意；否則覆寫＋重建步驟 seq 0..n-1）；`GetByChannel/ListAll/Delete`；
      驗證：名稱非空、通道>0、預設點名非空、dwell≥0、空白時窗回退 00:00-23:59
    - App `PatrolWindow.xaml(.cs)`（`--patrol` 直開＋主視窗工具列「巡航」）：頻道下拉、
      名稱/啟用/每日時窗（HH:mm）、預設點步驟 ListView（新增/移除）、整存→狀態列回報儲存
      成功含步驟數供 harness 斷言；AutomationId 群 `Patrol*`
    - 測試：Storage.Tests `PatrolRepositoryTests`（+8）：Save→GetByChannel 全欄位＋步驟序回圈；
      null id 同頻道覆寫不重複（Id 不變、ListAll 仍 1 筆）；顯式 id 覆寫欄位＋重建步驟；空步驟
      可存；Delete 移步驟＋連帶；ListAll 多頻道排序；輸入驗證 4 例；空白時窗回退全天；
      Storage 398→**406**；順帶 `RuleRepositoryTests.SchemaVersion_IsV26`（v24→26）；全 **701**
      （Storage 406＋Alarms 233＋Devices 54＋Licensing 8）
    - harness（已完成，第 21 支回歸 patrolcheck）：seedtriage `--patrol-seed/--patrol-verify`
      （`PATROL_SEED_OK`/`PATROL_VERIFY_OK`，名稱=enabled=時窗＋3 步驟）；patrolcheck.ps1 以
      `HELIVMS_DATA` temp db 開 `--patrol` UI：驗 `PatrolNameBox=harness patrol`、時窗 08:00、
      `PatrolPresetList` 3 列、按 `PatrolSaveButton` 後狀態列「已儲存」、再 `--patrol-verify`
      讀回仍在；終輸出 `PATROL_OK:id=1;steps=3;window=08:00-18:00;ui=true;save=true;
      verify-after-save=true`
    - 完成狀態：全 **701/701**；Build Release 0 error；feat commit＝**`77ee434`**；CI＝
      **`35599188803`** success；排雷已驗（見下）
    - 排雷（已驗）：`SqliteStore.Execute` 回 void（無 affected rows）→Delete 先查 EXISTS；
      schema 升版必須同步改 `RuleRepositoryTests.SchemaVersion_IsV*`（一時漏改造成全量 1 紅）；
      AiConcurrencyTests 併發時序 flake（fixed Sleep 350ms 在 xUnit 平行載入下偶發 0 µg）
      →`WaitUntil` 輪詢雙計數器（Runs 與 FramesInferred/FramesFed 須齊到位再斷言）；App env
      data root 用 `HELIVMS_DATA`（harness 以 temp db 隔離）
50. **M74 已完成＝感測器 IO 引擎（§14.1 #16，L0 純 BCL）**：
    - 背景：roadmap P1 大項「感測器乾接點 DI/DO」拆 L0 引擎（先）＋視窗/harness（M75）。
      以常開/常閉極性轉「邏輯值」，僅在邏輯上昇沿（false→true）依該埠規則觸發
    - 儲存層 schema **v27**（`SqliteStore`）：`io_ports(id,channel_id,kind 'DI'/'DO',
      number,name,polarity,debounce_ms,enabled)`＋`UNIQUE(channel_id,kind,number)`；
      `io_rules(id,input_port_id,action_kind 'alarm'/'toggle_do',output_port_id,event_type,
      retrigger_sec,enabled)`
    - `HeliVMS.Storage\IoPortRepository.cs`：`Add/Exists/List/ListByChannel/Get/Delete`；
      Delete 連帶刪該埠規則；Add 對 (channel,kind,number) 重複→`ArgumentException`
    - `HeliVMS.Storage\IoRuleRepository.cs`：`Add/List/ListByInput/Delete`；Alarm 動作須有
      event_type、ToggleDo 動作須有 output_port_id、retrigger_sec≥0
    - `HeliVMS.Storage\IoRuleEngine.cs`（純 BCL、時鐘注入）：
      - `OnInput(portId, physicalOn, utcNow)`：極性轉邏輯 → 邏輯上昇沿才觸發；
        每規則冷卻窗（retrigger_sec）內不重複觸發；找不到埠→`ArgumentOutOfRangeException`
      - Alarm → `IoAction(Alarm, ruleId, channel, port, null, eventType)`；ToggleDo → 該 DO
        邏輯狀態反相並回傳動作；`GetInputState/GetOutputState/Reset`
    - 測試：Storage.Tests `SensorIoTests`（+12）：埠盤 Add/Get/List 回圈、重複(z同)＋異 kind
      共存、Delete 連帶規則、規則 Add/List/驗證 3 例、Engine 上昇沿觸發 alarm、同值不重觸、
      冷卻阻擋＋逾時復原、停用規則跳過、常閉極性反相（實體 false→邏輯 true）、ToggleDo 反相
      兩次、未知埠 throw、無規則埠只記狀態；
      順帶 `SchemaVersion_IsV26→IsV27`；Storage 406→**418**；全 **713**
      （Storage 418＋Alarms 233＋Devices 54＋Licensing 8）
    - 完成狀態：全 **713/713**；Build Release 0 error；feat commit＝**`5cec078`**；CI＝
      **`35600379623`** success；排雷已驗（見下）
    - 排雷（已驗）：極性是「實體→邏輯」映射——常閉埠實體 false＝邏輯 true，測試初稿誤反（
      cooldown 序列須先下昇沿落底再上昇沿重觸發，且 GetInputState 要反過來斷言）；「重複埠」
      為三元組 (channel,kind,number) 唯一，DI#1 與 DO#1 可共存（測試初稿誤撞）；schema 升版
      照例要同步 `SchemaVersion_Is*` 斷言值
51. **M75 已完成＝感測器 IO 測試視窗＋harness（§14.1 #16 收尾）**：
    - 背景：M74 完成 L0 引擎後，M75 補「可視/可模擬」：App `IoWindow` 列埠與規則、
      以「閉合＋餵入」呼叫引擎模擬接點變化；實體採樣（GPIO/模組）留真機層
    - App `IoWindow.xaml(.cs)`（`--io` 直開＋主視窗工具列「感測IO」）：
      - DI 下拉＋「閉合」CheckBox＋`IoFireButton`→`IoRuleEngine.OnInput`（M74）
      - `IoPortList`（端別/#/名稱/極性/邏輯值）、`IoRuleList`（規則/輸入埠/動作/冷卻/啟用）
      - 狀態列回報「觸發 #n：警報[io_trigger]；DO→開」；AutomationId 群 `Io*`
    - harness（第 22 支回歸 iocheck）：seedtriage `--io-seed`（DI#1 門磁＋DO#1 警報燈＋
      alarm 規則＋ToggleDo 規則）/`--io-verify`（`IO_VERIFY_OK:di=1;do=1;rules=2`）；
      iocheck.ps1 以 `HELIVMS_DATA` temp db 開 `--io`：驗 2 埠＋2 規則列、勾閉合＋餵入、
      狀態列含「觸發 #1…警報[io_trigger]…DO→開」；終輸出
      `IO_OK:ports=2;rules=2;trigger=1;ui=true`
    - 測試：全 **713/713**（Storage 418＋Alarms 233＋Devices 54＋Licensing 8，無新增源碼測試；
      App 由 harness 覆蓋）；Build Release 0 error；feat commit＝**`3f12ab4`**；CI＝
      **`35601164639`** success
    - 排雷（已驗）：ListView 行數用 ControlType.DataItem 列舉（GridView 列皆為 DataItem）；
      CheckBox 用 TogglePattern.Toggle 而非 InvokePattern（WPF CheckBox 對 UIA 是 Toggle）；
      狀態列斷言須對應引擎語意（HC 閉合＋邏輯上昇沿才觸發）
52. **M76 已完成＝雙碼流 Smart 切流決策引擎（§15.2，L0 純 BCL）**：
    - 背景：即時監看主流/次流之「智慧切流」需先有決策引擎（第 15 章 §15.2）；純函數＋時鐘注入
      可測，調度層（App/串流管道）採納建議後回報 ApplySwitch
    - `HeliVMS.Storage\StreamSwitcher.cs`：`StreamKind{Main,Sub}`、`StreamSwitchDecision(Target,
      Reason?)`（Reason null＝維持）；`Evaluate(channelId, currentMainBytesPerSec, 近窗事件帶
      時間戳, activeViewers, utcNow)` 決策序：①honeymoon（切後 minHoldSec 內不動）②事件熱度
      （窗內加權分，預設權重表 ai_intrusion=100/tamper=90/ai_line_cross=60/motion=40）且為
      Sub→Main（"event-burst"）③已是 Main 且（主流總碼率≥上限"bandwidth-peak" 或 0 名收看
      "no-viewers"）→Sub ④否則維持；`ApplySwitch` 更新現行＋切換時間
    - 測試：Storage.Tests `StreamSwitcherTests`（+11）：honeymoon 內爆量不切；事件爆量 Sub→
      Main；頻寬高峰 Main→Sub；0 收看→Sub；Sub 無觸發維持；已是 Main 爆量維持；ApplySwitch
      更新現行＋時間；窗外交事件忽略；權重可客製（motion 不加權但 custom_critical 升）；未知
      頻道預設 Main；三項建構驗證；Storage 418→**429**；全 **724**
      （Storage 429＋Alarms 233＋Devices 54＋Licensing 8）
    - 完成狀態：全 **724/724**；Build Release 0 error；feat commit＝**`d993122`**；CI 首跑因
      NTP no-reply 測試 flake 紅（`NtpClientTests` 上界 2s 在 CI 負載下 300ms 計時器延遲、
      elapsed≥2s 誤判定時）→隨附修 `828acc5`（上界改 5s hang-guard＋下限 200ms 實際等待驗證）
      ；CI＝**`35602052735`** success；排雷已驗（見下）
    - 排雷（已驗）：NTP no-reply timeout 測試對「絕對上界」敏感——計時器在超載 runner 會延遲，
      上界須留超大餘裕（5s）並加下限（200ms 確保真的等了逾時），保留「返回 null」語義
53. **M77 已完成＝雙碼流切流調整視窗＋harness（§15.2 #19 收尾，串接 M76 引擎）**：
    - 背景：M76 完成決策引擎後，M77 補「可視/可操作」：App `StreamSwitchWindow` 以單一持久
      引擎例項做頻道級評估與套用，honeymoon/現行串流狀態跨操作連續（評估不重建引擎）
    - App `StreamSwitchWindow.xaml(.cs)`（`--stream` 直開＋主視窗工具列「切流」）：
      - 頻道下拉＋主碼率/收視/維持期輸入＋`事件爆量`/`評估`/`套用`按鈕
      - `StreamStateList`（頻道/現行 Main|Sub/上次切換/最近評估）；AutomationId 群 `Stream*`
      - 指令串流（honeymoon 5s）：無收看→Sub、套用→現行列 Sub、6s 後爆量→Main、套用→Main
    - harness（第 23 支回歸 streamcheck）：seedtriage `--schema` 建預設頻道（seedtriage 開機即
      建當缺頻道時）；streamcheck.ps1 以 `HELIVMS_DATA` temp db 開 `--stream`：斷言初始列
      Main、viewers=0+碼率 60MB 評估→`no-viewers`→套用 Sub、6s 後爆量→`event-burst`→套用
      Main；終輸出 `STREAM_OK:seed=1;rows=1;demote=no-viewers;promote=event-burst;apply=2`
    - 測試：全 **724/724**（Storage 429＋Alarms 233＋Devices 54＋Licensing 8，無新增源碼測試；
      App 由 harness 覆蓋）；Build Release 0 error；feat commit＝**`06b4517`**；CI＝
      **`35603258006`** success
    - 排雷（已驗）：PS5.1 console 對含中文行→cell 文字在工具捕捉時 mojibake（cp950 渲染）——
      UI 狀態/現行 token 一律用 ASCII（Main/Sub/->/hold/applied），harness 斷言純 ASCII（
      `no-viewers`/`event-burst`）；窗體中文標題仍可經 Win32Enum 找窗（FindWindowText Unicode）
54. **M78 已完成＝音訊感測 L1 多頻道協調器（§5.8 服務層）**：
    - 背景：M64 完成單頻道 `AudioTriggerEngine`（L0）後，L1 把「多頻道生命週期＋啟用狀態＋
      每頻道門檻」收成一個服務層；M64 引擎原生支援 `AudioTriggerConfig` 覆寫 → 無需改引擎
    - Alarms 新 `AudioSensorCoordinator`：`ctor(store, repo, configOverrides?)`；啟用集合來自
      `channels.audio_enabled`（`Refresh()` 重載）；`Feed(channelId, pcm, utc)` 以鎖路由至
      對應引擎（惰性 `GetOrCreate`），停用頻道不建引擎、不寫事件、回傳空清單；`Reset(channelId)`
      ／`ResetAll()`／`Flush()`（停止前結算各引擎未關窗）／`Dispose()` 冪等；`EventInserted`
      轉傳引擎事件；`Channels`／`IsEnabled` 查詢
    - 測試：Alarms `AudioSensorCoordinatorTests` +11（停用頻道 Noop/無引擎無事件；啟用惰性建引擎
      且分類 Burst；雙頻道隔離；每頻道 BurstDb=-1 覆寫（20000→-4.29＜-1 落至 Sustained、預設頻道
      Burst）；Refresh 偵測新啟用；Refresh 偵測停用移除；Flush 收尾 burst─duration 768ms≥120ms
      寫 audio_burst；ResetAll 捨棄開窗；Reset 單一頻道不影響他人；EventInserted 轉傳（audio_burst、
      channelId=1）；Dispose 冪等＋後續 Feed 拋 ObjectDisposedException）；Alarms 233→**244**；
      全 **735**（Storage 429＋Alarms 244＋Devices 54＋Licensing 8）
    - 完成狀態：全 **735/735**；Build Release 0 error；feat commit＝**`7c937e8`**；CI＝
      **`35604408658`** success
    - 排雷（已驗）：協調器建構時 `Refresh()` 讀 channels——測試須「先建頻道、後建協調器」，否則
      enabled 為空、Feed 全 Noop；引擎 `Feed` 同一批塊共用單一 utc → 同批 duration＝0 會撞
      `MinEventMs=120` 驗收陷阱——測試以「每 8 塊一批、每批 256ms」進時餵入，開窗 duration 才合法；
      分類階梯：20000（-4.29）在 BurstDb=-1 覆寫下不落 Quiet 而落 **Sustained**（SustainDb=-32 仍過）
55. **M79 已完成＝音訊感測測試視窗＋harness（§5.8 L1 收尾，可視化 M78 協調器）**：
    - 背景：M78 完成多頻道協調器後，M79 補「可視/可操作」：App `AudioWindow` 以合成方波
      （20000）每 8 塊一批、每批 256ms 進時餵入協調器，驗證判讀與事件寫入
    - App `AudioWindow.xaml(.cs)`（`--audio` 直開＋主視窗工具列「音訊」）：頻道下拉（僅
      audio_enabled）、`餵爆音`（24 塊進時多批→`fed:{id};blocks=24;last=Burst`）、`捨棄`
      （`reset:{id}`）、`結算`（`flushed:{n}ch;events=m`）；`AudioStateList`（頻道/啟用 Y|N/
      最後判讀/本回事件）；AutomationId 群 `Audio*`
    - harness（第 24 支回歸 audiocheck）：seedtriage `--map`（走過建頻道路徑，`SEED_MAP_OK`；
      `--schema` 在頻道建立前即 return 不可用）；audiocheck.ps1 以 `HELIVMS_DATA` temp db 開
      `--audio`：餵爆音→`last=Burst`、結算→`events=1`、捨棄→`reset:`；終輸出
      `AUDIO_OK:channels=1;last=Burst;events=1;reset=true`
    - 測試：全 **735/735**（Storage 429＋Alarms 244＋Devices 54＋Licensing 8，無新增源碼測試；
      App 由 harness 覆蓋）；Build Release 0 error；feat commit＝**`c661f9c`**；CI＝
      **`35605392969`** success
    - 排雷（已驗）：WPF ComboBox 未展開時 UIA 不列 ListItem——harness 不得以 combo 行數斷言
      （會誤報 no-channel），改以點「餵爆音」後讀狀態列（`fed:...last=Burst`）間接證明有頻道；
      seedtriage 建頻道須用 `--map`（建於 schemaMode return 之後），`--schema` 不會建
    - 另補：seedtriage 新增診斷模式 `--channels`（列出 id/name/audio_enabled，`CHANNELS:...`）
56. **M80 已完成＝LDAP/AD 登入 L0（§14.7 #1：DN 對映＋設定驗證）**：
    - 前置釐清：§14.7 #1 的 LDAP 提供者設定 L0 於 **M50** 已落地（`LdapSettings` record，存於
      `auth_providers.config_json`，含 Host/Port/BaseDn/BindDn/BindPassword/UserFilter/AdminGroups/
      DefaultRole；`LdapFilter` RFC4515 escape＋樣板代入）；本里程碑補「使用者 DN 對映」與
      「設定驗證」兩塊純規則，且**不升 schema**（避免與既有 record 重名衝突＋免遷移）
    - Storage 新 `LdapSettingsRepository.cs`（檔名沿用誤命名，實為兩個靜態類別）：
      - `LdapDn`：RFC 2253 屬性值跳脫（`,`/`=`/`"`/`+`/`<`/`>`/`;`/`\`/`#`、前導/後導空白）＋
        `BuildUserDn(settings, username)`＝`sAMAccountName=<escaped>,BaseDn`（BaseDn 空白僅回
        屬性值；供 L1 bind）
      - `LdapSettingsValidator`：純規則（Host 非空白、Port 1–65535、BaseDn 非空白、UserFilter 非
        空白且含 {user} 或 {0} 佔位）；回傳問題清單（空＝通過）
    - 測試：Storage `LdapDnValidatorTests` +11（用戶 DN 組裝、特殊字元跳脫（`,`/`=`/`#`/前導空白）、
      BaseDn 空白、null 使用者、後導空白、`a+b`/`a<b`、Escape null/空、Host/BaseDn 必填、埠範圍
      （0/65536 與 389）、UserFilter 缺失與缺佔位、完全有效無問題）；Storage 429→**440**；
      全 **746**（Storage 440＋Alarms 244＋Devices 54＋Licensing 8）
    - 完成狀態：全 **746/746**；Build Release 0 error；feat commit＝**`4de75be`**；CI＝
      **`35608457139`** success；harness（第 25 支 ldapcheck）＝seedtriage `--ldap-check`（既有
      `LdapSettings` 實例走 Validate＋BuildUserDn）→
      `LDAP_CHECK_OK:dn=sAMAccountName=alice,DC=example,DC=com;host=idc.example.com;port=636;
      problems=0`；`LDAP_OK:dn=alice;validate=0;json-via-store=true`
    - 排雷（已驗）：**§14.7 #1 的 LDAP L0 早已存在（M50）**——新增同名 `LdapSettings` 撞 CS0101；
      正確做法是補既有型別缺口而非另起 schema（經驗：新里程碑先 grep 同名型別）；RFC2253 對 `#`
      也跳脫（`\#`），測試期望值寫成未跳脫版會失敗；意外編輯到 `AddSmartAlertColumnsV20` 文件註解
      與方法區塊——一律 `git checkout --` 還原避免噪音 diff
57. **M81 已完成＝電子地圖強化② 事件熱點聚合器（§16 延伸，純 BCL L0）**：
    - 定位：M41 E-Map 已含事件色圖釘/定位/閃爍；本里程碑補「熱點聚合」純計算層，供 M82 地圖視窗
      渲染脈動與 tooltip 計數；不碰 UI
    - Storage 新 `MapHotspot.cs`：`MapEventSample(ChannelId, Kind, Utc)`、`MapHotspot(DeviceId,
      ChannelId, X, Y, Count, DistinctKinds, LastUtc, Hot, Urgency)`、`MapEventAggregator.Compute
      (pins, samples, now, window=30分, minHotThreshold=3)`（靜態純函式）
    - 規則（已測試）：窗含兩端〔from ≤ utc ≤ now〕；未來事件未鉚定頻道忽略；僅啟用圖釘；同頻道
      camera/io 圖釘並存以 **camera 優先**；Hot＝Count ≥ minHotThreshold（<1 throw）；Urgency＝
      0（不熱）或 min(100, Count×20＋60 秒內 +15)；DistinctKinds 依序排序；排序 Count 降序→
      LastUtc 降序→DeviceId 升序（平手穩定）；LastUtc 取窗內最晚事件
    - 測試：Storage `MapHotspotTests` +13（空輸入／空樣本零計數／依圖釘聚合計數／熱門門檻與
      urgency 60／近期事件 +15→75／caps 100／未來＋窗緣（含兩端）過濾／停用圖釘／未鉚定頻道／
      camera 優先／排序（count 降→lastUtc 降）／門檻 0 throw／LastUtc 追蹤）
    - 修 1 雷：`HashSet` out var 於 false 分支未指派 → CS8602；改 `set = new...` 再註冊
    - 全量：Storage 440→**453**；全 **757**（453＋244＋54＋8）；Build Release 0 error（NTP
      `OffsetMatches` 已知 flake：本里程碑全量首跑 1/453 紅、rebuild 復跑 453/453 綠——與 M81 無關）
    - 完成狀態：feat commit＝**`e6948bf`**；CI＝**`35609914309`** success；樹淨；harness 維持 25 支
      （純 L0 不加 harness；M82 UI 再上 maphotcheck 第 26 支）
58. **M82 已完成＝電子地圖強化③ 事件熱點渲染（§16 延伸，M81 聚合器上 UI）**：
    - App `MapWindow.xaml.cs`：`RefreshHotspots(mapId)`——依 `MapEventAggregator.Compute` 對啟用圖釘
      的近 30 分事件聚合，於每顆有事件圖釘加：**計數徽章**（Border＋TextBlock 數字，熱點紅/冷點紫，
      位置圖釘右上方）＋**脈動圓環**（Ellipse 28px，`_pulseTimer` 380ms 切換 Opacity 0.85/0.2）
    - 徽章 UIA：`AutomationId=MapHotspotBadge_{channel}`、`Name=HOT:{ch}:{count}:{kinds}:{hot}`、
      ToolTip「近 30 分 N 起事件（kinds）· 熱點/冷點」；供 harness 直接斷言（純 ASCII token）
    - 掛點：`LoadPins` 末尾（地圖切換重建）、15s `_eventTimer` 併同 `RefreshEventColors` 重新計算
      （DB 讀取在 UI 執行緒、L0 規模可接受）；`_currentMapId` 記錄目前樓層
    - seedtriage 新增 `--map-hotspot`（bypass）：建「harness 熱點」地圖＋2 相機圖釘（0.3,0.4/0.7,0.6）
      ＋第二頻道 harness hotspot-2；餵事件 hot ch＝motion×2＋tamper×1、cold ch＝offline×1；直接以
      `MapEventAggregator` 自餵驗算（hot.3/motion,tamper、cold.1）→ `MAP_HOTSPOT_SEED_OK:map=…;
      hot=1:3:motion/tamper;cold=2:1`
    - harness（第 26 支）`maphotcheck`：fresh dataRoot＋seed → 開 App `--map`→「電子地圖」視窗 →
      UIA 斷言 `MapHotspotBadge_1` Name＝`HOT:1:3:motion/tamper:True`、`MapHotspotBadge_2` Name＝
      `HOT:2:1:offline:False` → `MAPHOT_OK:hot=1:3:True;cold=2:1:False;ui=badges=2`
    - 排雷（已驗）：**PowerShell 雙引號內 `$var:...` 會被解析成變數限定名**——`"^HOT:$hotCh:3:…"` 中
      `$hotCh:3:motion` 被當成 `$hotCh:3:motion` 變數而吃掉字串（pattern 前 1:3:motion 消失）→
      以 `${hotCh}` 包夾修正；使用 `$var` 後接 `:` 的正則一律用 `${var}`；偵測到 window handle 後 badge
      需等 layout（加 3s）避免 Name 未就緒
    - 全量：Storage 453（App-only，未動 Storage 測試）→ 全 **757/757**；Build Release 0 error；
      feat commit＝**`d1013a0`**；CI＝**`35611891145`** success；樹淨
59. **M83 已完成＝回放視窗強化① 回放時間軸 L0（§回放延伸，純 BCL）**：
    - 定位：PlaybackWindow（M3/M8）已有時間軸帶（segment 色塊＋遊標＋事件列表點擊跳轉）；本里程碑
      補「當日時間軸模型」純計算層，供 M84 帶上事件標記渲染與以比例遊標；不碰 UI
    - Storage 新 `PlaybackTimeline.cs`：`TimelineBar(SegmentId, LeftFraction, WidthFraction)`、
      `TimelineMarker(EventId, ChannelId, Kind, Utc, XFraction)`、`PlaybackTimeline(Bars, Markers,
      DayUtc, GapFraction, GapCount)`、`PlaybackTimelineBuilder.Build(segments, events, dayStartUtc)`
      （靜態純函式）
    - 規則（已測試）：當日窗固定 `[dayStart, dayStart+24h)` **含左不含右**（`dayStart ≤ t < dayEnd`）；
      區段裁切至窗內、零長度略過；`EndUtc` null → `StartUtc + DurationSec`（null→回退 10s）；
      Bar/Marker 依時間排序；重疊區段以**聯集**計算間隙；GapFraction＝全日 24h 未被涵蓋比例（0..1）、
      GapCount＝未涵蓋區間數（空輸入＝1/1.0）；`ToFraction(utc,day)` 換算 0..1（夾 0..1）
    - 測試：Storage `PlaybackTimelineTests` +16（空輸入全日 gap；全日段 w=1；日中段 fraction＋2 gap；
      跨午夜裁至日末；日前段略過；跨日首裁到 0；相鄰段 gap=0；重疊裁切段合併 gap——23:00–26h 與
      23.5–27h 裁至 [23,24] 後 GapCount=1/fraction=23/24〔0–23h 未涵蓋〕；窗內標記 fraction 6:30→
      6.5/24；日首含、日末不含；窗外部標記濾除；EndUtc null→duration；null→10s 回退；雜亂輸入依時
      排序；ToFraction 外夾）
    - 修 1 測試誤判：重疊裁切段合併後「0–23h 仍為一個未涵蓋區間」，GapCount 應為 1 非 0
    - 全量：Storage 453→**469**；全 **775**（469＋244＋54＋8）；Build Release 0 error；
      feat commit＝**`f9a2ba1`**；CI＝**`35613154435`** success；樹淨；harness 維持 26 支
      （純 L0；M84 UI 再上 pbcheck 第 27 支）
60. **M84 已完成＝回放視窗強化② 時間軸模型驅動＋覆蓋率標籤（§回放延伸，M83 L0 上 UI）**：
    - RealApp 直接改 band：RenderBlocks 改以 `PlaybackTimelineBuilder.Build(segments, events,
      DayStartUtc())` 產出 `_timeline`，藍色區塊改用 `bar.LeftFraction/WidthFraction` 定位與
      縮放（裁切/聯集語意交回 L0，移除舊的 `DurationSec/86400` 宽度數學）；重繪即更新
    - 新增 **CoverageText**（取代舊固定「進度」Label）：`覆蓋 {pct}% · 未涵蓋 {n} 區間`；
      UIA `AutomationId=PlaybackCoverageText`、`Name=COV:{pct}:{gapCount}:{barCount}`（純 ASCII
      token 供 harness 斷言）
    - 事件標記 RenderMarkers 改以 `_timeline.Markers` 的 `XFraction` 定位（10px 圓點，tooltip 回查事件
      Detail）；DayStartUtc/FracOfDay 保留（OnBandClicked/UpdateCursor 共用同一當日窗基準，語意不變）
    - seedtriage 新增 `--pb-seed`（bypass）：今日 00:00 起 dayStart；8:00–10:00、14:00–16:00 兩段
      （間隙＝0–8／10–14／16–24）；9:00 motion、15:00 offline 兩事件；直接以 `PlaybackTimelineBuilder`
      自餵驗算 → `PB_SEED_OK:day=…;segs=2;events=2;coverage=17;gaps=3;bars=2`
    - harness（第 27 支）`pbcheck`：fresh dataRoot＋`--pb-seed` → 開 App `--playback`（「回放」視窗）→
      UIA 斷言 `PlaybackCoverageText` Name＝`COV:17:3:2` → `PB_OK:coverage=17:3:bar=2;ui=true`
    - 排雷（已驗）：PlaybackWindow 原有 `System.Windows.Automation` 未 import（MapWindow 才有）→
      `AutomationProperties.SetName` 報 CS0103；以往無 UIA 需求故未暴露；需補 using
    - 全量：Storage 469（App-only）→ 全 **775/775**；Build Release 0 error；
      feat commit＝**`434e275`**；CI＝**`35614308528`** success；樹淨

61. **M85 已完成＝LDAPv3 連線層 L1（§14.7 #1 LDAP，M80 L0 之上實作 wire）**：
    - Storage 新增 `LdapClient : ILdapBinder`（純 BCL，零第三方依賴）：TCP＋BER 講 LDAPv3 wire——
      `LDAPMessage = SEQUENCE(INTEGER msgId, op)`；simple bind（app0 0x60）、search（app3 0x63）、
      entry（app4 0x64）、done（app5 0x65）；`LDAP_BIND_OK` harness 原則與 `LdapClientTests` 以
      loopback `FakeLdapServer`（TcpListener）對測
    - bind 流程：有 `BindDn`（service account）→ 先 svc bind → subtree search 用
      `LdapFilter.Build(UserFilter)` 找用戶 DN（sizeLimit 1，**done 未附 entry ⇒ null**）→ 用戶 bind；
      無 BindDn → 直接 `LdapDn.BuildUserDn` bind；接著 baseObject search（用戶 DN，`(objectClass=*)`，
      attrs `['*','+']`）蒐集 memberOf SET → 群組清單；任何 Socket/IO/Arg/Format 例外 ⇒ null
    - `LdapBer`：minimal BER 編解碼（`Integer/Enumerated/Octet/Boolean/Tlv/Seq/Set/Concat/
      TryReadTlv/Parse`）；**`Parse(span)` 語意＝輸入為 TLV content（無表頭）回傳直接子節點**，
      測試必須 `Parse(x.Body())`；解出物 `BerTlv(byte Tag, byte[] Body)`
    - `LdapFilterEncoder`：RFC4515→BER（`(attr=*)`→0x87 presence、`(attr=val)`→0xA3 equality、
      `&`/`|`/`!`→0xA0/0xA1/0xA2、`\XX` hex 跳脫）；排雷（已驗）：`params byte[]` 無法收多個
      byte[]（expanded form 要求 element），要組多 TLV 得用 `params byte[][]`＋`Concat`
    - 測試：**24 新測試**（LdapBerTests 5、LdapFilterEncoderTests 6、LdapClientTests 12）；排雷：
      responder 收到的是**請求** op tag（bind=**0x60**，search=0x63）不是回應 tag（0x61/0x65）；
      `(objectClass=user)` 是 equality（0xA3）非 presence；`(&(a=1)b` 單子 AND＋殘字不會 throw
      （改以 `(!(a=b)))` 測結尾殘字）；"Jo\6eh" 跳脫為 "Jonh" 非 "Joh"
    - seedtriage `--ldap-bind`（第 28 支 harness）：in-process `FakeLdapHarness`（TcpListener）+真正的
      `EnterpriseAuthService.AuthenticateLdap(provider, user, pw, new LdapClient())` → admin 對映＋
      WRONG 密碼 reject → `LDAP_BIND_OK:provider=23;user=alice;role=admin;reject=true;port=…`
    - 全量：Storage 469→493 → 全 **799/799**；Build Release 0 error；feat commit＝**`54cad94`**；
      CI＝**`35621317643`** success；樹淨
- **M86 下一步（§14.7 #1 下一段落）＝App `LoginWindow` LDAP 面板**（見下方 §62 已完成）

62. **M86 已完成＝App `LoginWindow` LDAP 面板＋UI harness（§14.7 #1 LDAP 收尾）**：
    - `LoginWindow.xaml`：新增 `LdapPanel`（AutomationId）／`LdapProviderCombo`／`LdapLoginButton`
      （「以 LDAP 登入」，Click=`OnLdapLoginClicked`）／`LdapMessage`；`LoginWindow.xaml.cs`
      `LoadEnterpriseProviders`＝oidc＋ldap 皆載（高度 Base 300＋各面板高）；`OnLdapLoginClicked`：
      `_enterprise.AuthenticateLdap(provider, user, pw, new LdapClient())` → 成功設
      `SessionContext.CurrentUser`＋`DialogResult=true`；失敗 `LdapMessage`＋`PasswordInput.Clear()`
    - seedtriage 擴充：`--ldap-seed <port>`（建「LDAP harness」provider）、`--ldap-fake-server <portFile>`
      （`FakeLdapHarness.Start(multiple:true)` 寫 port 檔後 `Sleep(Infinite)`）、`--ldap-clean`、
      `--ldap-probe <port>`／`--ldap-probe-db`（cross-process 診斷）；`FakeLdapHarness` 改 instance
      `RunAsync` accept 迴圈（static Task lambda 引用 instance 欄位會 CS0120）
    - harness（第 29 支）`ldaplogincheck.ps1`（UIA）：`--auth-off` → fake server 進程 →
      `--ldap-seed` → `--auth-on` → Start-Process App → UIA 斷言 LdapProviderCombo/LdapLoginButton/
      LdapMessage 存在 → 錯密碼 reject 停留＋有訊息 → 正確密碼開主窗 → `LDAPLOGIN_OK`；finally 清 App/
      fake 進程/`--auth-off`/`--ldap-clean`
    - 排雷（已驗）：**WPF `StackPanel` 無 automation peer**——`Get-El "LdapPanel"` 抓不到，改斷言
      子控件；**PasswordBox 不支援 UIA TextPattern/SetValue**，只能 `SetFocus()+SendKeys`；
      **此環境 SendKeys 大小寫不穩定**（`p@ss` 到達 server 變 `P@SS`、`WRONG` 變 `wrong`，Caps/Shift
      狀態逐次翻轉）→ fake 改對「x」case-insensitive 接受、harness 用單字元 `y`(reject)/`x`(accept)
      唯 shift-free 才穩；**seedtriage db-scan 會把首個非 `--` 參數當 db 路徑**——`--ldap-seed`/
      `--ldap-fake-server` 的值誤當 db 會在該路徑初始化 SQLite（fake 把 SQLite header 寫進 portFile →
      harness 讀到 `'SQLite format 3'` 丟 FormatException）→ db-scan skip 清單加兩者；另外早期錯誤曾於
      repo 產生 `SQLite format 3*` 垃圾檔需 `Remove-Item`；**`Select-Object -Last 1` 會遮住 build 錯誤**
      （CS0120 連環產生舊 dll，診斷全歪）——看 build 結果要抓 `error|錯誤` 列
    - App Release build 0 error；Commit＋CI 綠；樹淨

63. **M87 已完成＝§14.7 #9 Failover 容錯 L0（純 BCL leader 租約仲裁引擎）**：
    - 背景：§14.7 #9「第二錄影伺服器即時接管」為僅剩的大型 P2 infra（現僅本機看門狗）；
      先以「共用儲存作為仲裁者」的 lease 模型落地 L0 引擎（Frigate/Milestone 同 take-over 語意），
      後續里程碑再接 UI／實體接管（§14.7 #9 收尾）
    - 落點：Storage `failover_state`（v28，單列 id=1：`server_id`／`lease_expires_utc`）；
      `IFailoverLeaseStore` 介面＋`FailoverRepository(SqliteStore)` 實作（ISO 往返）；
      `FailoverCoordinator` 純 BCL 引擎（注入 store＋utcNow）：`AcquireOrRenew(lease, utcNow)`
      （空庫→Leader、他人有效租約→Standby、過期→接管、同 server→續約）、`GetStatus(utcNow)`
      （Role/LeaderId/剩餘/到期）、`Release(utcNow)`（僅現任 Leader 可清除）；驗證：lease 正數、
      serverId 非空；`now == ExpiresUtc` 視為過期可接管
    - 測試：引擎（InMemory store 假時鐘）x11＋倉儲整合（temp DB v28 表）x6＝＋17 → 全 **816**
      （Storage 493→510）；`RuleRepositoryTests.SchemaVersion_IsV27`→v28（v bump 隨附）
    - Commit＋CI＋樹淨；§14.7 #9 狀態改「L0 引擎已落地（UI/接管為後續里程碑）」

64. **M88 已完成＝Failover 監控/測試視窗＋harness（§14.7 #9 上 M87 收尾）**：
    - `FailoverWindow.xaml(.cs)`：`FailoverServerText`（可改名，coordinator 依輸入 serverId 重建）、
      `LeaseSecText`、`FailoverAcquireButton`（取得/續約）、`FailoverReleaseButton`（讓出）、
      `FailoverRefreshButton`、`FailoverStatusText`（角色/Leader/本機/剩餘/到期）、`FailoverResultText`；
      1s `DispatcherTimer` 重刷狀態＝心跳監看；`MainWindow` 加「容錯」按鈕＋`--failover` CLI flag
    - seedtriage 擴充：`--failover-seed <serverId> <leaseSec>`（寫他人/過期租約）、`--failover-clean`
      （清單列）；皆加入 db-scan skip 清單（`--failover-seed` 需 i+=2）
    - harness（第 30 支）`failovercheck.ps1`：Phase A 空庫 None→Acquire=Leader→Release=None；
      Phase B seed nodeZ 300s→Acquire=Standby、Release 不覆寫（release skipped）；Phase C seed
      nodeZ leaseSec=0（起點即過期）→Acquire=Leader（過期接管）→ `FAILOVER_OK`
    - 排雷（已驗）：**harness `.ps1` 用 `Get-Content -Raw`（無 BOM 時依 ANSI 讀取）會把 UTF-8
      中文解成 Big5 亂碼且吞掉換行**——曾把註解與下一個 `$seedOut = …` 語句併成同一註解行，
      seed 靜默不執行（診斷=seedZ 空輸出）。修法：統一用 write tool 重寫全檔＋
      `WriteAllText(ReadAllText(…, UTF8), UTF8(BOM))`，**之後不再用 edit tool 改 harness**；
      seed 的 `2>&1` 合併 stdout+stderr 若仍為空即代表 dotnet 程序在 print 前出事
    - App Release build 0 error、全 **816** 不變（純 App/工具變更）；Commit＋CI＋樹淨

65. **M89 已完成＝§14.7 #10 Edge Storage 雙保險 L0（純 BCL `EdgeRecoveryPlanner`）**：
    - 背景：§14.7 #10「設備 SD 側錄＋NVR 雙保險，斷網期間設備自錄、重連補抓」列 P2、現況僅
      事件層補送（M56/M29）無段層實作；L0 先以「設備 SD manifest＋NVR 既有段」做缺失區間
      補抓排程（與 Milestone/Synology「SD 回灌」同語意），後續里程碑接 UI/調度器
    - 落點：Storage `EdgeRecoveryPlanner`（純 BCL、時鐘注入）：輸入 `EdgeSegment`
      （SourceId/StartUtc/EndUtc：設備 SD manifest）＋「NVR 本機已涵蓋段」，輸出
      `EdgeRecoveryPlan`（`EdgeRecoveryTask(FetchStart/End)` 依時序、跨段 gap ≤ 閾值併為單一
      補抓、單任務上限 maxFetchWindow 分割）；過期（manifest 末端早於 now−staleTtl）段略過
      （SD 已被覆寫權威）；參數驗證（段長正、閾值非負、ttl/max 正）；重疊段正規化後求差
    - 測試：純引擎假時鐘 x13 → 全 **829**（Storage 510→523）；排序時序、併段計數、StaleFloor
      以「末端」判斷（`EndUtc < now−ttl`）
    - Commit＋CI＋樹淨；§14.7 #10 狀態改「L0 規劃器已落地（UI/段層回灌為後續里程碑）」

66. **M90 已完成＝§14.7 #13 消費邊緣 AI 相機 metadata（D2C/方向）L0（純 BCL）**：
    - 背景：§14.7 #13「消費邊緣 AI 相機 metadata」列 P3、現況「以 NVR 端推理為準」；L0 先做
      「消費」層——吃相機 AI 送出的 metadata 串（ONVIF Profile M 語意：Appear/Move/Present/
      Disappear＋bounding box），聚合成每條物件軌跡並判運動方向，後續里程碑接欄位持久化/查詢 UI
    - 落點：Storage `EdgeAIClassifier`（`TrajectoryDirection`：Stationary/LtR/RtL/TtoB/BtoT/Other，
      以物件箱款當「畫面尺度」做位移閾值＝箱款平均×(W+H)/2×2%：早期 1/3 中位數 vs 晚期 1/3
      中位數之淨位移判向，抵抗零星雜訊；樣本<3 回 Stationary）；`EdgeAITracker`（依
      DeviceId+Class+TrackId in-memory 滑動窗：Disappear 或與 utcNow 的間隔逾 trackTimeout
      收尾；逾 maxTracks 逐「最近活躍最舊」；亂序容忍＝收尾時依時間戳排序）
    - 測試：純假時鐘 x12（四向判別／靜止／鋸齒淨位移小→Stationary／樣本不足→Stationary／
      Disappear 收尾／timeout 分兩條／跨 TrackId 隔離／亂序排序後判向／maxTracks 逐最舊）
      ＝＋12 → 全 **841**（Storage 523→535）
    - 排雷（情境編寫）：timeout 以「utcNow − 最後樣本時間」判斷，測試的 utcNow 若遠晚於樣本
      時間會在每次 Push 即收尾（首推非空、Disappear 推回 2 條等假失敗）——近即時串流語意
      應讓 `utcNow = 最新樣本時間`；「方向」以時間序為準，送達逆序僅排序、不反轉運動方向
    - Commit＋CI＋樹淨；§14.7 #13 狀態改「L0 消費 metadata 軌跡/方向分類已落地（持久化/查詢為後續）」

67. **M91 已完成＝§14.7 #7 法證語意搜尋 L0（事件 FTS5 全文檢索）**：
    - 背景：§14.7 #7「法證語意搜尋」列 P2；現況僅 M56 對 detail 做 LIKE 關鍵字（大小寫不敏感、
      單 token 粗查、無排名）；L0 引入 SQLite **FTS5** 全文檢索（word token、bm25 排名、範圍過濾）
    - 前置驗證：`Microsoft.Data.Sqlite 10.0.12`（專案現用版）bundle 的 e_sqlite3 **含 FTS5**
      （temp `ftspoke` 實測 `CREATE VIRTUAL TABLE ... fts5`＋MATCH＋rank 正常）
    - 落點：SqliteStore v28→**v29**；`alarm_events_fts` FTS5 **外部內容表**（content=alarm_events、
      content_rowid=id，索引 event_type/detail＋id UNINDEXED）＋ AI/AD/AU 三 trigger 同步；
      `EventSearchRepository`（建構時**無條件 `RebuildIndex()`**＝正規 `INSERT INTO
      alarm_events_fts(fts) VALUES('rebuild')`，自癒且確定；`Search`＝MATCH（SQLite 分詞）＋
      bm25 排名（較小較相關）＋start_time 左閉右開範圍＋limit；空白查詢抛 ArgumentException）
    - 排雷（外部內容 FTS5）：①`DELETE FROM alarm_events_fts` **不縮 count 卻使 match 全失效**
      （索引毀損），`Search` 靠建構時 rebuild 自癒；②per-row `'delete'` 特例以空串補值會
      SQLITE_CORRUPT；③升版模擬（DROP fts＋user_version=28 重開）受 WAL 時序干擾、不可靠——
      最終以「delete 致損→建構自動修復」為測試語意；④以 `PRAGMA triggers=OFF` 停 trigger **無效**
      （非官方 pragma 語意）；⑤測期望 bm25：`"alpha beta"`＝AND 語意只中兩 token 都有的列，
      OR 才取優先行
    - 測試：整合 x10（建構自癒索引／trigger 新增可搜／大小寫不敏感分詞／無命中空／空白查詢
      抛錯／from 左閉＋to 右開範圍／刪除不再命中／更新反映新內容／rank 最佳命中在先／limit）
      ＝＋10 → 全 **851**（Storage 535→545）；`SchemaVersion_IsV29` 改名
    - Commit＋CI；樹淨；§14.7 #7 狀態改「FTS5 全文檢索 L0 已落地（語意/AI 向量為後續）」

68. **M92 已完成＝§14.7 #8 統一安全平台之門禁事件 L0**：
    - 背景：§14.7 #8「統一安全平台（門禁/入侵/DO）」列 P2；現況 DO 已落地（M85）、入侵＝tamper/
      motion（M39/M38）；門禁（卡片進出事件）仍無；L0 先落「卡片刷卡事件」記錄＋查詢
    - 落點：SqliteStore v29→**v30**；`door_events`（device_id/door_id/card_id/direction
      In|Out/granted/reason/occurred_at_utc＋時序索引 ×3＝device・card・door）；
      `DoorEvent` record＋`DoorEventRepository`（Insert 回 id；Query 過 device/door/card/
      granted/from-to/limit；CountByCard 區間計數）
    - 測試：整合 x7（Insert 全欄位 roundtrip／依 card＋區間或右開／依 device＋granted／door 過濾／
      CountByCard 區間／limit／未知 card 空）；`SchemaVersion_IsV30` 改名
      ＝＋7 → 全 **858**（Storage 545→552）；CI 綠；樹淨；§14.7 #8 現況改「DO＋門禁事件
      已落地」

69. **M93 已完成＝§14.7 #8 統一安全平台之 POS 交易 Metadata 配對 L0**：
    - 背景：#8 欄「POS 接口待擴」；L0 落「POS 交易存錄」＋與同設備鏡頭/DIO/門禁事件
      「時間窗配對」（Metadata 配對＝把交易帶回影片脈絡）
    - 落點：SqliteStore v30→**v31**；`pos_events`（device_id/register_id/transaction_no/
      amount_cents/occurred_at_utc＋設備時序索引＋交易號索引）；`POSEvent` record＋
      `POSEventRepository`（Insert 回 id／Query 依 device＋左閉右開區間＋limit）；
      `POSEventMatcher`（純函式：同設備事件集按 |Δt|≤window 配對、依 |Δt| 排序）
    - 測試：整合 x7（Insert roundtrip／Query device＋區間／limit／未知 device 空／配對命中
      窗內／窗外排除／無候選空）；`SchemaVersion_IsV31` 改名
      ＝＋7 → 全 **865**（Storage 552→559）；CI 綠；樹淨；§14.7 #8 現況補「POS 交易
      存錄＋配對 L0 已落地」

70. **M94 已完成＝§14.7 #10 Edge Storage 雙保險之補抓回灌執行器 L1**：
    - 背景：#10 欄「實體回灌待續」；M89 `EdgeRecoveryPlanner` 只產補抓計畫，本里程碑接上
      「執行＋狀態」：設備重連後把 SD 側錄漏段 pull 回主庫、失敗退避重試、進度可查
    - 落點：SqliteStore v31→**v32**；`edge_backfill_jobs`（device/channel/start/end/status
      Pending|Downloading|Done|Failed/attempts/next_attempt/last_error/completed_utc＋設備時序
      索引＋狀態索引）；`EdgeBackfillJobRepository`（Create **重疊去重**（Pending/Downloading/
      Done 任一重疊即跳過）／QueryDue＝`status IN (Pending,Failed) AND (next_attempt≤now)`
      ——**重點：Failed 且達退避時刻必須回取，否則退避永不啟動重試**／MarkDownloading・
      MarkDone・MarkFailed（attempts+1＋退避時刻＋last_error））；`EdgeBackfillExecutor`
      （一次 ExecuteOnce 取到期 jobs、SemaphoreSlim 有限並行、成功 Done／失敗退避）；
      `EdgeBackfillCommandFactory`（純函式：`ffmpeg -y -nostdin -ss <start> -t <dur> -i <src>
      -c copy <dest>`，-ss 前置快速 seek、-t 定長、免轉碼）
    - 測試：整合 x9（CmdFactory 指令形狀／Create→QueryDue／重疊去重／MarkDone 設 completed＋
      Done 不再取／MarkFailed attempts+1＋next_attempt、到期才回取／Executor 全部完成並記
      completed／失敗退避（failed→now 空→now+退避可取）／失敗一次後重試成功 Done＋attempts
      維持／maxConcurrent=2 時 ActiveMax=2 併行全跑完）；`SchemaVersion_IsV32` 改名
      ＝＋9 → 全 **874**（Storage 559→568）；CI 綠；樹淨；§14.7 #10 現況改「L0 規劃器
      ＋L1 回灌執行器均已落地；實際 ffmpeg 拉流為部署整合（`IEdgeBackfillRunner`）」

71. **M95 已完成＝§14.7 #13 消費邊緣 AI metadata 持久化/查詢 L1**：
    - 背景：#13 欄「持久化/查詢待續」；M90 只有分類/追蹤即時邏輯，收尾定案事件無處落
    - 落點：SqliteStore v32→**v33**；`edge_smart_events`（device/class/track/direction/
      型別框 x1y1x2y2/occurred＋時序索引 ×3＝device・class・direction）；`EdgeSmartEvent`
      ＋`EdgeSmartEventRepository`（Append 回 id／Query 過 device/class/direction/左閉右開
      區間/limit／**AggregateByDirection** 區間分向計數＝消費分析原語）
    - 測試：整合 x6（Append roundtrip／direction 過濾／class＋device 過濾／範圍左閉右開／
      limit／AggregateByDirection 分向計數＋設備/區間維度）；`SchemaVersion_IsV33` 改名
      ＝＋6 → 全 **880**（Storage 568→574）；CI 綠；樹淨；§14.7 #13 現況改「L0 消費/軌跡/
      方向分類＋L1 持久化/查詢/分向摘要均已落地」

72. **M96 已完成＝§14.7 #10 補抓回灌執行器之真實 ffmpeg runner（實體整合）**：
    - 背景：#10 欄「實際 ffmpeg 拉流為部署整合」；本里程碑把 `IEdgeBackfillRunner` 落地為
      真實進程 runner，閉合 Edge 補抓管線
    - 落點：`EdgePullTarget（src URI＋落盤路徑）`；`EdgeFfmpegBackfillRunner`（注入 resolver：
      job→拉流位址；`EdgeProcessExecutor` 委派抽象外部進程——測試注入 fake、無需真實 ffmpeg；
      預設實作＝Process + ArgumentList、限量 30min 逾時、逾時 Kill、stderr 截尾 500 字元；
      `IsToolAvailable()` 以 `ffmpeg -version` 探測；工具缺失/例外一律轉失敗結果）
    - 測試：整合 x5（exit0 成功＋驗證 args 沿用 Factory（-ss/-i/-c copy/dest）／非零 exit→
      stderr 尾＋exit 碼入錯誤訊／例外→失敗訊息／IsToolAvailable exit0 為真／不可用→false＋
      執行失敗）；`ffmpeg -version` 探測分流 fake（args=["-version"] 才回工具版本）
      ＝＋5 → 全 **885**（Storage 574→579）；順帶放寬 NTP loopback 乾淨樣本斷言（100ms 注入
      的 90ms 嚴限在 CI 排程抖動下太緊→60ms）
    - CI 綠；樹淨；§14.7 #10 現況改「L0 規劃器＋L1 執行器→真實 ffmpeg runner 已閉合」

73. **M97 已完成＝§14.7 #7 法證語意搜尋多源化（Unified Forensic FTS5）**：
    - 背景：#7 欄 M91 只有 alarm 單源 FTS5 L0；門禁/POS/Edge AI metadata 各自查詢無統一
      法證檢索入口
    - 落點：SqliteStore v33→**v34**（`CreateUnifiedSearchTablesV34`：door_events_fts/
      pos_events_fts/edge_smart_events_fts 三 FTS5 外部內容表＋ai/ad/au trigger，仿 v29）；！
      `UnifiedEventSearch`＝`[Flags] ForensicSource { Alarm=1, Door=2, Pos=4, EdgeSmart=8, All }`
      ＋`ForensicSearchHit(Source,SourceId,Text,OccurredAtUtc,Rank)`；ctor **RebuildAll()**
      對 4 表（含 alarm）無條件自癒；`Search(query,from,to,sourceMask,limit)` 各源 MATCH＋
      `($from IS NULL OR time>=$from) AND ($to IS NULL OR time<$to)`＋`bm25` rank，跨源合併
      OrderByDescending(OccurredAtUtc).ThenBy(Rank).Take(limit)；空查詢抛 ArgumentException
    - 引擎排雷：FTS5 對 `TXN-999`（無空格連字符）把 `-999` 當負項→若為純數字 token 會被視為
      欄位名→SQLite Error「no such column: 999」；`"CARD-77" OR "approved"` 若把 OR 也包進引號
      會被當成含字面 OR 的詞→0 命中。→新增 **`FtsQuery.Normalize`**：逐空白 token 引號包覆
      （除法 `"` 剝除）；`OR/AND/NOT` 保留為運算子；空輸入抛錯。並回填套用到 M91
      `EventSearchRepository.Search`（消彌同規格潛在 bug）
    - 測試：整合 x10（door/pos/edge/alarm 四源 trigger 可搜、跨源依時間 DESC、SourceMask 過濾、
      Range 左閉右開、空白抛錯、RebuildAll 自癒、**運算子 OR/AND 存活正規化**）；
      `SchemaVersion_IsV34` 改名＝＋10 → 全 **895**（Storage 579→589）；CI 綠；樹淨；
      §14.7 #7 現況改「FTS5 L0（alarm）＋多源統一檢索 L1 已併入」

74. **M98 已完成＝§14.7 #9 Failover 實體接管協調器 L1**：
    - 背景：#9 欄「實體接管待續」；M87 只有租約仲裁 L0，M88 監控視窗/harness——協調器仍停在
      角色判定，無「接管/交還實體上下文＋軌跡」
    - 落點：SqliteStore v34→**v35**（`failover_events`：server_id/mode/detail/at_utc＋時序索引）；
      `FailoverEventMode{Leader,Takeover,Relinquish}`＋`FailoverEvent` record；
      `IFailoverRoleController`（`TakeOver／Relinquish`——錄影/串流上下文啟停抽象，測試注入 fake、
      正式為 WPF 整合點）＋`NoopFailoverRoleController`；`IFailoverEventLog`＋
      `FailoverEventRepository`（Append 回 id／ListAfter 時間 DESC＋from 過濾＝稽核/監控窗原料）
    - 引擎：`FailoverCoordinator.Reconcile(leaseDuration, missingWindow, utcNow, controller, events)`
      心跳驅動狀態機：空庫/自身租約→當選(Leader event)或續約（不重複動作）；他人有效→Standby 零動作；
      他人逾時且逾窗 ≥missingWindow→Upsert 競選＋`TakeOver`＋Takeover event；未逾窗→寬限等待；
      本機上輪 Leader 而租約易主/逾窗→`Relinquish`＋Relinquish event（交還）；僅轉變才動作/寫軌跡
      （Steady 續約不濫寫）。行為同 SQLite/WAL 單列原子競選（租約 Upsert=競選點，與 M87 一致）。
      L0 三方法同步維護 `_lastRole` 供 Reconcile 連續觀察
    - 測試：Reconcile 純邏輯 x8（空庫當選＋TakeOver／續約不重複／他人有效被動／寬限窗內 Standby／
      逾窗接管+Takeover 事件／旁落 Relinquish／Release 重選 Leader×2／負窗抛錯）＋v35 軌跡 x2
      （Append roundtrip＋DESC/from 過濾、mode 字串落庫）；`SchemaVersion_IsV35` 改名
      ＝＋10 → 全 **905**（Storage 589→599）；CI 綠；樹淨；
      §14.7 #9 現況改「租約仲裁 L0＋監控視窗（M87/M88）＋實體接管協調 L1（M98）已落地」

75. **M99 已完成＝§14.7 #1 企業登入落地（LDAP 登入＋登入 session L1）**：
    - 背景：#1 欄「LDAP/SSO 待接」；M50 已做協調（AuthenticateLdap/RoleMapper/設定面）、M85 已做
      連線層（LdapClient wire bind）——但「登入」本身未落地：無 session、無鏡像同步、無鎖定
    - 落點：SqliteStore v35→**v36**（`login_sessions`：session_id UNIQUE/user_id/username/role/
      provider/issued/expires/revoked＋session/expiry 索引）；`LoginSessionRepository`（Create＝
      32 位元組加密亂數 hex；IsActive＝未撤銷且未過期；Revoke；PurgeExpired 清過期）
    - 登入：`LdapLoginBroker.Login(provider, username, password, ILdapBinder, ttl, utcNow)`——
      LDAP simple bind 驗證 → groups+adminGroups→`RoleMapper.Map` 得 admin/viewer；
      成功＝users 鏡像列同步（`NoLocalPasswordHash="v1$0$!"` 不可驗證記號，PasswordHasher.Verify
      格式不符必 false→鏡像無法以本機密碼登入；存在則更新 role/display）、RecordLoginSuccess
      清失敗計數，簽發 session；失敗＝有鏡像列才計 failed_logins 並在 ≥threshold 鎖定
      （沿用 auth.lockout.threshold/minutes 與 M42 同鍵）；停用/鎖定鏡像先於綁定檢查
      （停用＝連綁定都不做，管理端即時 kill-switch）
    - 測試：x10（成功創鏡像＋session active／admin 群晉升／複用鏡像更新 role＋重置鎖／綁定失敗
      無鏡像不鎖／綁定失敗累計至鎖定／停用鏡像綁定前拒絕（BindCalls=0）／非 LDAP provider 拒絶／
      session 到期失活／Revoke 失效／PurgeExpired 只刪過期）；`SchemaVersion_IsV36` 改名
      ＝＋10 → 全 **915**（Storage 599→609）；CI 綠；樹淨；
§14.7 #1 現況改「本機帳號 RBAC（M42）＋OIDC 驗證/LDAP 設定（M50）＋LDAP 連線層（M85）＋
       企業（LDAP）登入與 session（M99）已落地；OIDC 授權碼登入流程待續」
76. **M100 已完成＝§14.7 #1 尾段 OIDC 授權碼＋PKCE 登入流程**：
    - 背景：#1 欄「OIDC 授權碼登入流程待續」；M50 只有 `OidcValidator.Validate`＋`AuthenticateOidc`
      （id_token 驗證＋RBAC 對映），沒有授權碼流程（Begin/Complete）與登入 session
    - 落點：`OidcOptions` 新增 `AuthorizeUri`/`TokenUri`/`RedirectUri`/`ClientSecret`（ClientSecret 選填）；
      `OidcLoginFlow`（`Begin`＝state（32 hex）＋PKCE verifier（32B→Base64Url）＋S256 code_challenge，
      組授權 URL（response_type=code&client_id&redirect_uri&scope=openid profile email&state&
      code_challenge&code_challenge_method=S256）並暫存 pending（單次消費、10 min TTL、
      Begin/Complete 前置 PurgeExpired）；`Complete(code,state)`＝TryRemove pending（一次性，
      重複 state 拒絶）→ `ITokenEndpointClient.Exchange`（`HttpTokenEndpointClient` RFC6749 §4.1.3
      form POST，含 code_verifier／選填 client_secret）→ `EnterpriseAuthService.AuthenticateOidc`
      驗證 id_token→鏡像 users（M99 同款 `NoLocalPasswordHash`；存在則 SetRole/SetDisplayName）→
      `LoginSessionRepository.Create` provider `"oidc"`）；state 一次性＋PKCE 防 CSRF／授權碼挾持
    - 測試：x9（Begin URL 含 state＋PKCE／成功簽 session＋鏡像／admin 群晉升／複用鏡像更新 role＋name／
      錯誤 state 拒絶／state 單次消費第二回拒絶／pending 逾時拒絶／token 端點錯誤／id_token 無效）
      ＝＋9 → 全 **924**（Storage 609→618）；CI 綠；樹淨；
      §14.7 #1 現況改「OIDC 授權碼＋PKCE 登入已落地（M100 `OidcLoginFlow`）」 
77. **M101 已完成＝§14.7 #5 隱私‧錄影遮蔽（Redaction）L0**：（見頂部快照）
    - 背景：#5「錄影遮蔽/模糊化」完全沒有（個資法交付加分 P1）；Snapshot→Redaction 最小可落地切
    - 落點：SqliteStore v36→**v37**（`redaction_regions`：source_type/ref_id/channel_id/
      occurred_at_utc/x/y/width/height/filled/created_at_utc＋source/時間索引）；
      `RedactionSources`（clip/snapshot）；`RedactionRepository`（Add 寬高≤0 抛錯／QueryBySource／
      QueryByTime 左閉右開／Remove 回實際刪除行數）；`RedactionProcessor`（純 BCL：BGRA 32bpp
      in-place，filled=false 以 blurRadius 盒狀模糊（ArrayPool 暫存、逐像素均值）、filled=true
      實心→像素歸零；區域自動裁剪至影像界內、緩衝區長度校驗抛錯；提供影片全遮蔽與
      快照一鍵模糊（§14.7 #16）共用引擎）
    - 測試：x10（Add→QueryBySource roundtrip／跨來源與跨 ref_id 過濾／QueryByTime 左閉右開／
      零寬高抛錯／Remove true→Empty→false／filled 區域內歸零區域外不動／blur 改變區域內像素／
      越界 clamp 全遮且界外不動／完全在影像外無效果／緩衝區過短抛錯）＝＋10 → 全 **934**
      （Storage 618→628）；`SchemaVersion_IsV37` 改名；CI 綠；樹淨；
      §14.7 #5 現況改「錄影遮蔽 L0（M101 `RedactionRepository`＋`RedactionProcessor`）已落地；
      回放/匯出串接待續」
78. **M102 已完成＝§14.7 #3 警報管理器分診工作流 L1**：（見頂部快照）
    - 背景：#3 欄 M47 只有優先序/SLA 截止/負責人/面板（alarm_triage）；「指派→進度→升階」
      工作流缺進度註記串與逾時升階軌跡
    - 落點：SqliteStore v37→**v38**（`alarm_notes`（event_id/author/note/created_at＋event 索引）＋
      `alarm_escalations`（event_id/level/from_priority/to_priority/due_utc/created_at＋event 索引））；
      `AlarmWorkflowRepository`（AddNote（空白拒絶）／NotesByEvent 舊→新／NoteCountByEvent／
      RemoveNote／RecordEscalation（level＝既有 max+1）／EscalationsByEvent）；
      `AlarmSla.ResponseSeconds`（critical 300／high 900／normal 3600／low 7200（秒））
      ＋`AlarmEscalationPolicy`（純 BCL：優先序高一階 normal→high、high→critical、critical→critical
      停頂階；升階後新 SLA 截止＝now＋新優先序回應時限；僅「未關案（pending/acknowledged）
      且已逾 due」才升階，closed/未逾期一律 None）＝大面板「指派→進度→升階」資料閉合
    - 測試：x11（註記增列順序／空白註記抛錯／計數＋移除 true→false／跨事件隔離／升階 level
      遞增＋Due 正確／跨事件隔離／SLA 秒數表／未逾期不升階／逾期升 decision 正確＋NewDue／
      critical 逾期停頂階／closed 事件不升階）＝＋11 → 全 **945**（Storage 628→639）；
      `SchemaVersion_IsV38` 改名；CI 綠；樹淨；
      §14.7 #3 現況改「M47 分診面板＋M102 分診工作流 L1（alarm_notes＋alarm_escalations＋SLA 升階）
      已落地；智慧牆大面板 UI 待續」
79. **M103 已完成＝§14.7 #15 MQTT／自動化平台輸出 L0**：（見頂部快照）
    - 背景：#15「MQTT / 自動化平台輸出」完全沒有（P3，Frigate/HA MQTT 生態對照）
    - 落點：`MqttClient : IMqttPublisher`（最小 MQTT 3.1.1 QoS0 發送器：CONNECT（protocol name
      +level4+clean session+keepalive）→CONNACK 檢查（rc≠0 Fail）→PUBLISH（固定頭 0x30＋剩餘長度
      多字節編碼 §2.2.3）→DISCONNECT（0xE0 0x00）；TcpClient/NetworkStream 同步走 wire、timeout 注入；
      未連線/空 topic/連線拒絕/斷線皆 Fail；僅發送不訂閱，HA/Frigate 相容；`IDisposable`）
      ＋`MqttEventRouter`（topic＝`prefix/ch{channelId}/{eventType}`（eventType 空白→`_`＋小寫）＋
      JSON payload {channel_id,event_type,ts ISO}）；測試 loopback `FakeMqttBroker`
      （解析 CONNECT/PUBLISH/DISCONNECT、可注入 CONNACK rc）
    - 測試：x9（連線→發布→斷線 roundtrip（ClientId/clean/topic/payload 全核對）／CONNACK rc=5 拒絶
      Fail／未連線發布 Fail／埠關閉 Fail／長 topic 多字節剩餘長度 roundtrip／剩餘長度 128 邊界
      （0x80 0x01）／空 topic Fail／router topic 正規化／payload JSON 欄位）＝＋9 → 全 **954**
      （Storage 639→648）；CI 綠；樹淨；
      §14.7 #15 現況改「MQTT 輸出 L0（M103 `MqttClient`＋`MqttEventRouter`）已落地；事件
      驅動掛載/訂閱待續」
80. **M104 已完成＝§14.7 #8 POS 接口擴展 L1**：（見頂部快照）
    - 背景：#8「POS 接口待擴」「沒有」；M93 僅做 v34 pos_events＋時間窗配對，無對帳/去重/收銀機查詢
    - 落點：`POSEventRepository.InsertDedupe`（去重匯入：同 device+register+txn+amount＋|Δt|≤keyWindow
      視重複→列回既有 id 不新增；julianday 差≤window 天數）＋`QueryByRegister`（左閉右開）；純 BCL
      `PosReconciliation`：`PosReconSummary(Total,Matched,Unmatched,Duplicates)`、`Compute`＝逐筆交易
      對最近事件候選（沿用 M93 `POSEventMatcher` |Δt|≤window 語意）、duplicates＝同 register+txn+
      amount 之重複筆數
    - 測試：x10（首插/同窗內回插拒重複（同 id）、超窗重插、異 register/異金額不重複、QueryByRegister
      濾 device+register+時間、全配對、部分未配對、重複計入不算二次 matched、無候選全未配、空清單
      None）＝＋10 → 全 **964**（Storage 648→658）；CI 綠；樹淨；
      §14.7 #8 現況改「POS 接口擴展 L1（M104 去重匯入＋收銀機區間查詢＋對帳統計）已落地；
      POS→門禁自動核對/異常視覺化待續」

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

81. **M105 已完成＝§14.7 #14 智慧牆 L0（資料模型＋版面 API，v39）**：（見頂部快照）
    - 背景：#14「智慧牆」完全沒有（P3）；智慧牆＝警報牆＋視訊牆（馬賽克/告警格），本里程碑先落
      「版面資料模型＋幾何校驗＋看板時間常數」，不碰 UI
    - 落點：`SqliteStore` v38→**v39** `smartwall_layouts`（name UNIQUE、rows/cols 1..16、
      created/updated_at）＋`smartwall_tiles`（layout_id、row/col 0 起算、rowspan/colspan、
      channel_id 可空、view 0 單鏡頭/1 馬賽克、position）＋索引；
      `SmartwallLayoutRepository`：CreateLayout（名稱重複/空白/尺寸越界→ArgumentException）、
      RenameLayout、ListLayouts、AddTile（越界與重疊（矩形交集）→ArgumentException）、GetTiles
      （position 排序）、RemoveTile；純 BCL `LayoutGrid`（IsTileInBounds／FindOverlap）＋
      `SmartwallTimings`（§14.7 #14 常數 AlarmHighlight=5s、MosaicKeepLast=300s）
    - 雷：`_store.Query<T>` 是「single-row」非集合——要 List 回傳須用非泛型
      `Query(sql, reader=>List<T>, bind)` 形態（誤用泛型→CS1503 集合形態不符）；UNIQUE 衝突在
      SQLite 是 `SqliteException(19)` 非 ArgumentException——CreateLayout 需先 EXISTS 預查再插
    - 測試：x18（SchemaVersion_IsV39、Create/Rename/List、名稱重複與空白與尺寸越界 Throws、
      AddTile 讀回、越界/重疊/貼邊允許、RemoveTile、IsTileInBounds 邊界、FindOverlap 邊角與無重疊、
      Timings）＋RuleRepositoryTests.SchemaVersion_IsV39 改名＝＋18 → 全 **982**（Storage 658→
      676）；CI 綠；樹淨；
      §14.7 #14 現況改「智慧牆版面資料模型＋幾何校驗＋看板時間常數（M105 v39）已落地；警報牆
      派送引擎/視訊牆 UI 待續」
82. **M106 已完成＝§14.7 #14 智慧牆警報看板引擎 L0（純 BCL）**：（見頂部快照）
    - 背景：#14 續作：M105 做完資料模型，本里程碑補「警報牆派送引擎」（收到的告警依 Rule 排序、
      每頻道保留窗內最新、≤5s 高亮、>300s 移出）——全程不碰 UI，可單測
    - 落點：`SmartwallAlertBoard.Snapshot(events, nowUtc, maxCells)`：`SmartwallBoardEvent
      (ChannelId,EventType,Priority,OccurredAtUtc,RuleOrder)` → 過濾保留窗（MosaicKeepLast=300s）→
      GroupBy ChannelId 取最新 → OrderBy RuleOrder → ThenByDescending 優先序（low≥0…critical=3）
      → ThenByDescending 發生時間 → Take(maxCells) → `BoardCell(...,Rank 1 起,Highlight
      =now−occ≤5s,Age)`；`LatestForChannel`（保留窗內該頻道最新事件）；`RankOf`（未知優先序=
      int.MaxValue 排後）；排序語意＝同一 Rule 內優先序高者先進
    - 測試：x12（窗外剔除、每頻道去重新者勝、規則→優先序→新舊排序、≤5s 高亮、>5s 不高亮、
      Age 反映差、maxCells 上限、空清單、maxCells=0 Throws、LatestForChannel 最新、無資料 null、
      RankOf 對映）＝＋12 → 全 **994**（Storage 676→688）；CI 綠；樹淨；
      §14.7 #14 現況改「智慧牆版面資料模型＋幾何校驗＋看板時間常數（M105 v39）＋警報看板引擎
      L0（M106 `SmartwallAlertBoard`）已落地；視訊牆 UI/MQTT 派送掛載待續」
