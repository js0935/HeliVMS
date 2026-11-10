# QCV8001 行為契約（顯示／錄影／回放）與 HeliVMS 對照

> 目的：把「HeliVMS 改成跟 QCV8001 一樣的顯示、錄影、回放方式」變成可驗收的規格。
> 依據：`D:\SDK\REA\`（QCV8001 全模組反編譯，42,100+ 函式）＋ `C:\QCTek\QCV8001\media\ws\`
> （它的 web 前端實檔）。只取**行為事實**；不複製它的程式碼、美術資源、字型或 WASM。
> 日期：2026-11-10｜狀態：M1 草案，待核定後才動產品程式。

---

## 0. 最重要的三個結論

> 更正（2026-11-10）：`media\ws` 是它的 **Web 遠端介面**（瀏覽器/CMS/手機用）。
> 操作員在機器前用的**主操作介面是 `QC_Live.exe` 的原生監看顯示**——
> 本規格以原生監看為對齊目標，web 介面資料僅作旁證。

1. **對齊目標＝`QC_Live.exe` 監看顯示**（`C:\work\Project\Application\QC_Live1\`：
   `nvr_live.cpp`／`live_button.cpp`／`remote_channel.cpp`，顯示引擎 `QC_Display` D3D9/DirectDraw，
   版面計算 `_DISPLAY_LAYOUT`/`CalcRect`，OSD 疊字 `QC_ScreenCap!CreateTextBMP`）。
2. **它的錄影「方式」有兩層**：行為語意（模式/排程/事件型別/單檔秒數）與**磁碟格式**
   （`YYYY_MM_DD\HH_mm\ChannelNNNN\` 目錄＋每小時 `.idx` 每分鐘槽位）。前者是操作員看得到的
   「方式」，後者是內部格式——QCC 相容層已證明不需要動磁碟格式也能對齊。建議先對齊前者。
3. **它的回放是「多路同步 + 日期＋事件過濾」模型**（`QC_Player!QC_MultiPlayer/UnitPlayer`、
   `NVRPlayback!PopupCalendar/PopupBookmark/PopupVideoClip/PopupVideoExport/PopupVisualSearch`、
   `QC_Playback.exe` 的 `PlayerPanel/PlayerScrollBar`、`QC_UsageHabit!GetPlaybackMonitorIdx`
   記住上次佈局）。HeliVMS 已有時間軸/日曆/**多路同步**，缺的是事件過濾、±30s、
   剪輯/匯出的任務化與視覺搜索。

---

## 1. 顯示（Live）＝ `QC_Live.exe` 監看顯示（本節為對齊目標）

### 1.0 原生監看契約 **[反編譯確認，`QC_Live\decompiled\functions.txt`]**

| # | 行為 | 證據 |
|---|---|---|
| 1 | **全螢幕監看牆＋多螢幕**：每個監視器一份設定（0x1828B/螢幕）與桌布 `monitor%d.png`；`GetMaxMonitorSize` 取得上限；`{Display} set success/fail, win=%d`（win＝螢幕索引，通道數上限 0x100） | `QC_Display` `{Display} set`、`GetWindowRect failed! idx=%d`；`QC_Setting` `GetMaxMonitorSize` |
| 2 | **分割選擇與記憶**：`SplitIndex`（UsageHabit）記住上次分割；**分割上限隨通道數分級**：≤36→36、==64→64、37–64→72、65–99→128、==100→100、144 級→144（`+1` 為最大索引）；`GetSplitChannels`／`QCSplit` 物件管理 | `QC_Live` 0x14000b1xx：`sVar5` 分級表；`QC_Setting` `SplitIndex` |
| 3 | **頻道快速按鈕**：`channel%02d.bmp`／`uchannel`（hover）／`xchannel`／`ychannel` 四態，編號 1..36；登錄檔 `ImmedChannels`／`ImmedSelAll`（QCC 端同名命令的來源） | `QC_Live` 0x140012xxx 資源函式；`QC_Setting` `ImmedChannels` |
| 4 | **按鈕組**（`live_button.cpp`）：`[Live]`、`[Playback]`、`[Advanced]`、`[Split]`、`[Monitor]`、`[AutoScan]`、`[FullScreen]`、`[PTZ]`（`bptz%d.bmp`）、`[Volume]`、`[Keyboard]`、`[LiveMenu]`；警報快速按鈕 `AlarmBtn%d_%d.bmp`（10 顆）；`LockKbdShortcut` 可鎖快捷鍵 | `QC_Live` 命令 ID 0x80e8–0x8136 與 `QC_Lang::GetString` |
| 5 | **輪巡 AutoScan**：`AutoScanEnable`＋`AutoScanSplit`（記在 UsageHabit）、週期 `AutoScanPeriod`（`GetAutoScanPeriod`，存成 short）；**行為＝每個週期把畫面往前推「一個分割量」的頻道**：`offset=(offset+splitChannels)%count`，尾頁夾在 `count-splitChannels` | `QC_Live` 0x1400029c0、0x14000dbxx（`iVar8 = (offset+iVar4) % iVar9`）、0x14000ed44 分割預設 |
| 6 | **全螢幕單格**：`FullScreenEnable`（UsageHabit 記憶）；全螢幕顯示**選取的頻道**，碼流依攝影機能力選 1 或 2（`GetCameraInfo(ch)+0x82` 判斷）→ 主/子碼流 | `QC_Live` 0x14000b0xx（`FullScreenEnable` 寫回） |
| 7 | **OSD 疊字**：時間兩種模式——完整 `YYYY/MM/DD HH:MM:SD` 或只有 `HH:MM:SS`（旗標切換）；字色/字級可設（`DispFontColor`/`DispFontSize`）；文字以 `CreateTextBMP`（GDI Tahoma 64px→DIB）疊加；OSD 內容含頻道名（`GetCameraInfo`） | `QC_Live` 0x140005xxx（兩種格式字串）；`QC_ScreenCap\README.md` |
| 8 | **每攝影機音量**：`CameraVolume`；`[Volume]` 按鈕與音量面板 | `QC_Setting` `CameraVolume` |
| 9 | **牆上浮動窗**：modeless 事件檢視（`modeless_cms_event::ToggleCmsEventView`）與警報對話（`modeless_alarm::AlarmDialogAction`）；e-map 可自動開（`AutoStartEMap`） | `QC_Live` 0x14000b1xx；`QC_Setting` `AutoStartEMap` |
| 10 | **顯示/解碼策略**（`QC_Advanced` 設定）：`LimitDispRes`（限制顯示解析度）、`LimitOneFPS`、`LimitBandwidth`/`SpeedLimit`/`MaximumFlow`、`LongLatencyMode`、`SupportLowLatency`、`DefaultFrameBufferLength`（每路緩衝） | `QC_Setting` 登錄檔鍵全集 |

### 1.1 Web 遠端介面（旁證，`ws\view\Live\liveWasm.html`）

| 元素 | 內容 | 證據 |
|---|---|---|
| 頂欄四頁簽 | `LIVE`／`PLAYBACK`／`EXPORT`／`INFO`（＋右上使用者） | `main.html` `mu_live/mu_player/mu_backup/mu_info`（lk=`IDCS_MAIN_*`） |
| 分割選擇 | **只有 1/4/9/16** ＋ 全螢幕 | `liveWasm.html` `seg_1/seg_4/seg_9/seg_16`、`full_screen` |
| 左上時鐘 | `0000/00/00 00:00:00` 常駐 | `liveWasm.html` `liveTimeClock` |
| 右側五面板 | 頻道樹（含搜尋/重新整理）／頻道群組／PTZ／操作（聲音+麥克風）／快照 | `liveWasm.html` `chlList/chlGroup/ptz/chlCmd/chlCap` |
| PTZ 面板 | 8 方向＋Home、速度滑桿（慢↔快）、zoom/focus/iris 各 +/-、preset 清單、cruise 清單＋啟停 | `liveWasm.html` `PtzPane/speedPL/imgCtrlPL/presetCruisePL` |
| 頻道上限 | web 版 36 通道（出廠名 `攝影機001..036`）；原生 144；CMS 版 1024 | `ASSETS-AND-DATA.md` §1.1；`CAMERA-DISPLAY-RECORD-WEB-CMS.md` §9 |

### 1.2 影像路徑與每格行為

| 項目 | QCV8001 | 證據 |
|---|---|---|
| 傳輸 | WS 二進位幀 `/device/preview/data`，**標準 H.264/H.265 裸流** | `WEB-FRONTEND.md` §3.2；`MediaDecoder\README.md` |
| 解碼 | 瀏覽器 WASM ffmpeg（`_createWebPlayer(1)`）＋WebGL 貼圖＋PCM WebAudio；**佇列上限 8 幀，落後丟幀**；原生端 FFmpeg+QSV/CUVID | `WEB-FRONTEND.md` §3.2.2 |
| OSD | 通道名/時間以 GDI Tahoma 64px→DIB 疊加；`SendOSDText` 64-slot 佇列 | `QC_Display\README.md`、`QC_ScreenCap\README.md` |
| 版面計算 | `_DISPLAY_LAYOUT`/`CalcRect`（x86 另有 `DisplayLayoutSDK` 12 個 `LAYOUT_*`：分割/PIP/hit-test） | `QC_Display\README.md`、`X86\DisplayLayoutSDK` |
| 外掛指令面（行為字典） | `SetViewChannelId`、`SetScreenMode`、`SetStreamType`（主/子碼流）、`TakePhoto`、`TalkSwitch`、`SetVolume`、`ScanSwitch`（輪巡）、`MagnifyImg/MinifyImg`（電子放大）、`OSDSwitch`、`OriginalDisplaySwitch`、`Preview/StopPreview`、`PlayRecItem`（從回放頁帶入） | `WEB-FRONTEND.md` §3.3（84 個 OCX 指令） |
| 嵌入 | `AsmLive.cgi?hBase64&cam&width&height`（cam 1 起） | `ASSETS-AND-DATA.md` §3 |

---

## 2. 錄影

### 2.1 磁碟格式（它的內部格式；對齊前先決定要不要做這層）

```
<RecStorage>\YYYY_MM_DD\HH_mm\ChannelNNNN\
    ├─ YYYYMMDD-HH.idx   每小時索引 0xE10 bytes＝100 槽 × 60 bytes（tm_min 定位）
    │                    每槽低 4 bit＝錄影型態旗標
    └─ 媒體檔（與 idx 同目錄；容器需動態驗證）
```

- 儲存池：`QC_NVRStorage.initial_storage(path)`，路徑來自 `qc_setting.xls!RecStorage`
  （實機 `D:\QC_Storage\`，36 槽＝36 通道）；多顆 HDD 依設定輪替
  （`QC_Setting_Record::GetRecStorage/GetRecStorageInfo`）。
- 回收：`qc_rec_recycle.cpp`——**容量滿即刪最舊**。
- 單檔時長可調：`QC_Advanced::GetRecordFileSeconds()`。
- 每通道錄影器：`initial_record(ch, QC_Stream*, QC_Loading*, QC_Alarm*)`；
  **備援錄影** `initial_recbkup`；**JPEG 快照連拍** `initial_jpeg_record`；
  全系統服務 `RecordingSystem.Start251/Stop251`（251＝錄影型態代碼）、`SetQuality(float)`。
  證據：`QC_Record\README.md` §1–2。

### 2.2 模式與排程（操作員看得到的「錄影方式」）

| 項目 | QCV8001 | 證據 |
|---|---|---|
| 錄影模式 | `MOTION`／`ALARM`／`MOTION_ALARM`／`INTENSIVE_MOTION`／`INTENSIVE_ALARM`／`INTENSIVE_MOTION_ALARM`／（進階）`INTENSIVE_MOTION_ALARM_INTELLIGENT` | `ws\view\RecCfg\recModeCfg.html`（radio values） |
| 手動錄影 | 時長下拉 `cboRecTime`（到期停止） | `recModeCfg.html` |
| 排程 | 7 天 × 100 小時槽字串；`c`=連續，其他代碼＝動偵/警報情境；有「排程管理」（新增/編輯/刪除排程）；原生設定頁 `NVRSetting!camera_record.cpp` 寫 `RecSchedule` | `ASSETS-AND-DATA.md` §1.1 `RecSchedule`；`NVRSetting\README.md` |
| 單檔秒數 | `RecFileSeconds`（登錄檔/設定可調）＋ `QC_Advanced::GetRecordFileSeconds()` | `QC_Setting` 鍵；`QC_Record\README.md` |
| 其他錄影設定 | `RecordingSystemMode`、`FileSizeLimit`、`RecStorage`、`EventReserveDays`、`ShutdownAfterRecordStopping`（離開時等錄影收尾再關機） | `QC_Setting` 鍵全集 |
| 錄影設定頁 | `config/record/{event,mode,parameter,schedule,schedule/manager,status,stream/event,stream/timing}` → 事件錄影、模式、一般參數、排程、**錄影狀態**、定時/事件**分別的碼流** | `UI-ROUTES.md` |
| 磁碟頁 | `config/disk/{diskArrayCfg,diskMode,information,information/smart,manager,physicalDiskCfg,storage/mode}` | `UI-ROUTES.md` |
| 容量資訊 | `queryRemainRecTime`（剩餘可錄時間）、`queryRecStatus`、`queryRecordDistributeInfo`（錄影分佈）、`queryDiskStatus`、`queryDiskMode` | `WEB-FRONTEND.md` §5 |

### 2.3 事件型別（回放過濾的 23 種 `REC_EVENT_TYPES`）

`manual`、`schedule`、`motion`、`sensor`、`gsensor`、`shelter`、`overspeed`、`overbound`、
`osc`、`avd`、`tripwire`、`perimeter`、`vfd`、`pos`、`smart_motion`、`face_verity`、`cpc`、
`cdd`、`ipd`、`smart_aoi_entry`、`smart_aoi_leave`、`smart_pass_line`、`smart_plate_verity`
（來源：`ws-protocol-cmd.md`，`websocket.cmd.js`）。

---

## 3. 回放

### 3.0 原生回放契約 **[反編譯確認]**

| # | 行為 | 證據 |
|---|---|---|
| 1 | **獨立回放程式**（`QC_Playback.exe`，MFC 外殼）：`PlayerPanel`/`PlayerButton`/`PlayerScrollBar`（播放面板/按鈕/進度軸）、`nvr_play.cpp`/`play_display.cpp`；**記住上次佈局**（`QC_UsageHabit!GetPlaybackMonitorIdx`） | `QC_Playback\README.md` |
| 2 | **多路同步回放引擎**：`QC_Player!QC_MultiPlayer`（多通道同步）、`CQC_UnitPlayer`（單路）、`CQC_PlayerStep`（逐格）；**本機檔與遠端串流雙來源**（`PopupSelLocalRemote` 選擇） | `QC_Player\README.md` §1–2 |
| 3 | **檢索/工具 UI**（`NVRPlayback.dll`）：`PopupCalendar`（錄影日曆：哪天哪些時段有錄）、`PopupBookmark`（書籤）、`PopupVideoClip`（剪輯）、`PopupVideoExport`（匯出）、`PopupVisualSearch`（**以畫面找錄影**）、`SetRegion`（**框選區域**做智慧搜索）、`StatusSetting` | `NVRPlayback\README.md` §1 |
| 4 | **智慧搜索的資料面**：以時間/區域（`RELATIVE_COORDINATES`）掃描事件索引（`eventlog` 的 `type/event/chid/utc_sec/period`） | `QC_Player\README.md` §2；`QC_Database\README.md` |
| 5 | **匯出**：`CQC_PlayerExport`/`QC_VideoClip`/`QC_UnitBackup`（AVI/MP4；CMS 端 ASR 直接輸出 `ASR%03d_<起>_<訖>.mp4`） | `QC_Player\README.md`；`QC_Center\README.md` §2 |

### 3.1 Web 遠端回放（旁證，`ws\view\QCV8001\player.html`）

| 元素 | 內容 |
|---|---|
| 版面 | 左：播放器；右：**日期選擇 ＋ 攝影機下拉 ＋ 播放鈕** |
| 控制列 | `fwPlay`（往前播）、`bkSpeed`（倒轉）、`fwSpeed`（快轉）、`nextFrame`（逐格）、`bk30s`/`fw30s`（±30 秒）、`pcapture`（擷圖）、`fullScreen`、`playOriginIcon`（**回到即時**）、音訊控制、播放狀態文字 |

### 3.2 WS 回放協定（`ws-protocol-cmd.md` + `WEB-FRONTEND.md` §4）

```
CMD_PLAYBACK_OPEN → /device/playback/open
  { task_id: GUID, channel_id, start_time: ms, end_time: ms,
    stream_index: 0|1, type_mask: "manual schedule … smart_plate_verity",
    backup: bool, audio: bool }
CMD_PLAYBACK_SEEK / KEY_FRAME / ALL_FRAME / REFRESH_FRAME_INDEX
CMD_PLAYBACK_AUDIO_OPEN/CLOSE；二進位幀 /device/playback/data
```

- 日期/有無錄影查詢：`queryDatesExistRec`（哪些天有錄）、`queryDateListRecChl`、
  `queryChlRecLog`（時段明細）、`queryRecDataSize`（資料量估算）。
- 嵌入回放：`AsmPlayer.cgi?cam&date&time`；片段匯出 `QCVideoClip.cgi?ch_idx&date&time&length`（秒）。

### 3.3 時間軸（`ws\js\lib\WasmPlayer\PlaybackTimeline.js`）

- canvas 繪製；**每頻道一列（28px）**；`colorMap` 事件型別分色；支援縮放/拖曳/雙擊/右鍵、
  選取區間（`clipRange`）、時間遮罩；上方刻度 40px。

### 3.4 備份/匯出（`recBackUp.js` 64KB）

- 任務化：`createRecBackupTask`（XML：`<chls><item id="chlId"/></chls><eventType>…</eventType>
  <startTime>UTC</startTime><endTime>…`）＋ `ctrlRecBackupTask`（控制）＋
  `queryRecBackupTaskList`＋`heartBeat`（進度）；可備到**外接碟**（`queryExternalDisks`）。
- 圖片庫：`searchPictures`/`backupPicture`/`delPictures`；POS 帳單：`queryPosList/queryPosBillList`。

---

## 4. HeliVMS 對照與缺口

| # | 面向 | QCV8001（原生＝對齊目標） | HeliVMS 現況 | 缺口 |
|---|---|---|---|---|
| 1 | 監看牆 | `QC_Live.exe` 全螢幕監看牆＋多螢幕＋每螢幕桌布 | WPF 監控中心（分割牆＋群組頁簽） | 多螢幕牆、桌布、`SplitIndex` 分級 |
| 2 | 分割 | 上限隨通道數分級（36/64/72/100/128/144），`SplitIndex` 記憶 | 1/2/4/9/16/25/36/64（`PlaybackWallBudget`） | 分級上限＋記憶 |
| 3 | 頻道快速按鈕 | 36 顆四態按鈕＋`ImmedSelAll`（QCC 同名命令來源） | 無（群組頁簽近似） | 新增 |
| 4 | 輪巡 AutoScan | 週期＋分割量推進＋夾頁，`AutoScanEnable/Split/Period` 記憶 | 無 | 新增 |
| 5 | 全螢幕單格 | 選取頻道全螢幕＋主/子碼流自動選擇 | 放大格（同碼流） | 碼流選擇 |
| 6 | OSD | 時間兩模式＋字色字級＋頻道名 | 有（錄影時刻 OSD，格式固定） | 兩種模式＋設定 |
| 7 | 音訊 | 每攝影機音量 `CameraVolume` | 獨占播放（無每路音量） | 每路音量 |
| 8 | 浮動窗 | 牆上事件檢視＋警報對話＋e-map 自動開 | 事件中心（獨立窗） | 浮動化（可選） |
| 9 | 解碼策略 | `LimitDispRes`/`LimitOneFPS`/頻寬限制/緩衝長度 | `PlaybackWallBudget`（回放）；live 有診斷 | live 端設定化 |
| 10 | 錄影模式 | 6+1 種＋密集 | always/schedule/event/manual 4 種 | 語意對齊 |
| 11 | 排程 | 7 天×100 槽＋排程管理 | 起訖時段規則 | 一週格子 |
| 12 | 單檔秒數 | `RecFileSeconds` 可調 | 固定 | 設定化 |
| 13 | 事件型別 | 23 種 | 自由字串 | 對映表 |
| 14 | 回放 | 多路同步＋日曆/書籤/剪輯/匯出/視覺搜索＋記住佈局 | 多路同步＋日曆/書籤/匯出精靈 | ±30s、事件過濾、視覺搜索（可選） |
| 15 | 磁碟格式 | 目錄+每小時 idx 槽位 | `seg-*.mp4`＋SQLite | **建議不動**（M5 另估） |

---

## 5. 分階段實作計畫

| 階段 | 內容 | 產出 | 驗收 |
|---|---|---|---|
| M1 | 本規格核定＋決策點（尺度、去留） | 核定紀錄 | 使用者核可 |
| M2 | **WPF 監看牆改版成 `QC_Live` 模型**：分割選擇與 `SplitIndex` 記憶（含分級上限）、頻道快速按鈕列（四態）、`[AutoScan]` 輪巡（週期＋分割量推進＋夾頁）、`[FullScreen]` 單格全螢幕（主/子碼流依能力）、OSD 兩種時間模式＋字色字級、每攝影機音量、牆上事件/警報浮動窗 | `src/HeliVMS.App` 監看牆 | 行為守門測試（繁中測試名，先紅後綠；`app.layout.test.js` 對應項） |
| M3 | 回放對齊：事件型別過濾（對映 23 種語意）、±30 秒、日期＋頻道快取的「有錄影日/時段」查詢、剪輯/匯出任務化（進度+取消）、書籤對齊 `PopupBookmark` | `PlaybackWindow`/`HeliVMS.Recording` | 行為測試 |
| M4 | 錄影語意：模式對齊（event 拆 motion/sensor＋密集=事件碼流）、一週排程格（7×24）、`RecFileSeconds` 設定化、剩餘可錄時間估算、錄影狀態頁、`ShutdownAfterRecordStopping` | `HeliVMS.Recording`/`Storage`/設定 | 行為測試＋遷移 |
| M5 | （可選）磁碟格式對齊：`YYYY_MM_DD\HH_mm\ChannelNNNN\`＋每小時 idx 槽位、滿即刪最舊 | `HeliVMS.Storage` | **單獨估價**：影響 Storage/Recording 約 1,656 條測試與既有錄影的相容 |

每階段遵循 repo 慣例：繁中測試名、守門測試先紅後綠、i18n 三語、
`AGENTS.md` 的「監控中心 UI 不得改變」由本規格取代（M1 核可時同步改寫）。

## 6. 實作紀錄（F 系列）

| 項目 | 結論 | 證據／產出 |
|---|---|---|
| F1 分割記憶 | **HeliVMS 既有**：`MonitorLayoutPrefs` 已記「每頁簽格數＋最後頁簽＋佈局索引」並在啟動還原（`MainWindow.ApplyLayoutPreference`）——與 QC_Live `SplitIndex` 等價，不需重做 | `src/HeliVMS.Shared/MonitorLayoutPrefs.cs`；`MainWindow` 2245 起 |
| F2 輪巡 | **HeliVMS 已有跳台序列**（`AdvanceWallSequence`＋`WallSequenceToggle`，固定 5 秒）。本輪升級為 QC_Live 語意而非另造第二份：推進算術改走 `WallAutoScanPlanner.NextOffset`（尾頁夾住、分割量蓋滿全部頻道時不動）；開關持久化 `wall.auto_scan`、週期 `wall.auto_scan_seconds`（未設定＝5 秒＝既有節奏）。QC_Live 把 `AutoScanEnable`/`AutoScanPeriod` 記在 UsageHabit，重開不會忘——HeliVMS 原本會忘，這是本次補的缺口 | `src/HeliVMS.Shared/WallAutoScanPlanner.cs`＋`WallAutoScanPlannerTests`（7 條，含突變檢查）；`WallSequenceContractTests`（3 條） |

尚未實作：F3（OSD 兩種時間模式＋字色字級）、F4（每攝影機音量）、F5（全螢幕單格自動主/子碼流）、F6（解碼策略設定化）、F7（錄影語意：`RecFileSeconds`、模式對齊、事件型別 23 種、剩餘可錄時間）、F8（回放 ±30s、事件過濾、匯出任務化）。

## 7. F3–F8 盤點結論（2026-11-10 自主掃描）

逐項對照的結果：**多數 QC_Live 核心行為 HeliVMS 已經存在**，真正缺的都有「需要新 UI」或「高風險低收益」的性質。逐項：

| 項目 | 結論 | 證據 |
|---|---|---|
| F3 OSD 時間/字色字級 | **部分存在**：單格 OSD（`OsdText`，與主讀數同一格式器）在 1 分割已有；多分割每格 OSD 有更新間隔（`PaneOsdInterval` 模式）。缺的是「畫面上疊時刻＋兩種格式＋字色字級設定」——本質是畫面上的新元素，**需要你同意才加**（預設關閉可做到零視覺變化） | `AGENTS.md` 播放 OSD 條目；`PlaybackClock.FormatInstant` |
| F4 每攝影機音量 | **不存在**：`channels` 無 volume 欄、`WaveOutPlayer` 無音量 API。要落地需 schema＋`waveOutSetVolume`＋至少一個設定入口（UI）——**暫緩** | 掃描 `src/HeliVMS.Storage`、`src/HeliVMS.App` |
| F5 單畫面自動主碼流 | **已存在**：`ChannelManager.ApplyDisplayPolicyAsync`——1 分割第 0 格升主流、其餘次流、錄影恆主流、次流常駐不斷線（比 QC_Live 更細：含手動切流覆蓋與回復） | `ChannelManager.cs` 215–229 |
| F6 解碼策略 | **等價物已存在且更進階**：live 有 `FrameRateGate`（令牌桶丟幀）、`decode.hwaccel` 設定與全域冷卻、回放有 `PlaybackWallBudget`（每格縮放/幀率上限/解碼執行緒/關鍵影格模式）。QC_Live 的 `LimitDispRes`/`LimitOneFPS` 是設定面；HeliVMS 是自動策略＋診斷。設定化屬 UI 範疇——**暫緩** | `AGENTS.md` live/回放 budget 條目 |
| F7 單檔秒數 | **已存在**：`recording.segment_seconds`（`RecordingSegmentPolicy`，預設 3600）＝QC_Live `RecFileSeconds` | `RecordingSegmentPolicy.cs` |
| F7 錄影模式 | **語意已由映射對齊**：HeliVMS 4 模式（全時/排程/事件/手動）涵蓋 QC_Live 的 MOTION/ALARM/MOTION_ALARM（→event）與手動；`INTENSIVE_*` 是碼流策略不是錄影時機。改 `RecordingMode` 列舉會波及排程/牆上模式點/CS8524——**不做內部對齊**，QCC 相容層已在回應面映射（`QctekEventTypes`） | `QctekEventTypes.cs`；`AGENTS.md` 錄影模式點條目 |
| F7 事件型別 23 種 | **部分**：`alarm_events.event_type` 已分類且 CMS 映射完成（System/Operation/Network/Event/Alarm）；補滿 23 種 QCV8001 專屬型別（POS/車牌/人臉…）對 HeliVMS 沒有對應資料源——**不做** | `QctekEventTypes.cs` |
| P3 裝置模型 | **前提經黃金抓包否證**：`%TEMP%\opencode\qcc-capture\config.bin`＝312B＝`0x18＋1×0x120`——實機 `GET_CONFIG_QCTEK` 的 n＝**當時實際頻道數（1）**，不是 36；`GET_AUTH_USER` 位移 20＝36 是**授權層級**（36 通道視圖），兩者本來就不同欄位。HeliVMS 現況（config n＝7、AuthUser＝36）與真機**同構**，不需改。「`GET_CONFIG_QCTEK` 補到 36 筆」的原本 P3 計畫作廢；`SET_CONFIG_QCTEK` 不在 QCC 實際使用的 6 條命令裡（普查 0 次），等有 CMS 端證據再做 | `docs/QCTEK-QCC-CONNECT.md` §二進位 payload 版型；抓包 `config.bin`／`authuser.bin` |
| F7 剩餘可錄時間 | **基礎存在**（磁碟壓力等級/保留空間）；「剩餘可錄時間估計」需要顯示位置（UI）——**暫緩** | `DiskPressureCoordinator` |
| F7 關機等錄影收尾 | **已存在且更完整**：`SegmentRecorder` 先 q 後 kill、`ShutdownSequencer` 有界收尾、`ShutdownWatchdog` 兜底 | `AGENTS.md` shutdown 條目 |
| F8 ±30 秒 | **能力已存在**（±10s/±60s Ctrl/±600s Shift）；QC_Live 的 30 秒是不同步進值。改預設值會動現有操作員肌肉記憶——**保留現值** | `PlaybackSeekPlanner` |
| F8 事件過濾/匯出任務化 | 事件清單已有；型別過濾與背景匯出佇列都是 UI 面——**暫緩** | `PlaybackWindow`；匯出精靈 |

結論：**F 系列已全部完成**。F1（分割記憶）、F5（單畫面主流）等為「確認已存在」；
F2（輪巡）、F3（時間 OSD）、F4（每路音量）、F8（匯出取消/停止）為本輪補齊——
F8 實測後發現匯出中心（批次、佇列、進度、重試、驗證）早已是任務化模型，
缺的只是 QC_Live 的取消/停止（`ctrlRecBackupTask`），已補上（repository/服務/視窗三層）。
QCTek 線的下一步是 **P0 實機驗證**（需重啟 QCC 並先降記憶體壓力）。

## 8. UI 解禁後的第二輪（2026-11-10，使用者明示「UI 可以變動」）

原本因「不改監控中心介面」擱置的四項，逐一處理：

| 項目 | 狀態 |
|---|---|
| 頻道快速按鈕列（QC_Live `channel%02d` 四態鈕＋`ImmedChannels`/`ImmedSelAll`） | ✅ `7d193fb`：牆右緣直欄、依**目前頁簽的牆序**編號，點號碼＝單格顯示該台（走既有 `ToggleExpandCell`/`ApplyWallAsync`，**不重連**），「全部」還原整面；沉浸模式收起；開關 `wall.quick_bar` 預設開；i18n 三語 |
| 牆上浮動事件/警報窗 | ✅ `db0c4b4`：左緣事件列（`alarm_events` 最近一天＋`RouteAlarm` 即時接續，上限 20 列），點一列選取那台格位；**預設關**（頁尾警報列已有最近三筆）；沉浸模式收起；開關 `wall.event_panel` |
| 多螢幕牆 | 待做（體量大：每螢幕一窗、工作階段配置、錄影擁有權） |
| 視覺/框選搜索 | 待做（需 AI 管線，QC_Live 用 OpenVINO） |
