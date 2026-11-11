# 儲存方式對照：QCV8001（`D:\SDK\REA`）vs HeliVMS

> 目的：使用者指示「HeliVMS 的儲存方式 參考 `d:\SDK\REA`」，範圍已定為
> **語意對齊＋對照文件，不搬目錄結構**。這份文件是那張對照表：逐項寫出參考實作
> 的做法、HeliVMS 現況、以及「等價／更強／缺口」，讓後面每一項改動都有依據。
>
> 參考側的每個事實都標了出處（模組＋函式或原始檔名），可以逐項回去查；
> 沒有標出處的敘述不要當事實用。反編譯輸出的樣板警語（REA 的 `limitations:` 那一段）
> 一律省略。

---

## 1. QCV8001 的儲存機制（參考側）

### 1.1 目錄與檔名

```
<RecStorage>\YYYY_MM_DD\HH_mm\ChannelNNNN\
    ├─ YYYYMMDD-HH.idx     每小時索引
    └─ 媒體檔（與 idx 同目錄）
```

出處：`QC_Record\README.md` §2；路徑格式字串由 `QC_Record` 的
`FUN_180006560`（`0x180006560`）重建：`"%04d_%02d_%02d\\%02d_%02d\\Channel%04d\\"`。

`<RecStorage>` 來自 `qc_setting.xls!RecStorage`（實機 `D:\QC_Storage\`，36 槽＝36 通道）。

### 1.2 每小時索引 `.idx`（精確版型）

`FUN_180006560` 是本機判斷「某頻道某時刻有沒有錄影」的函式，它把整個索引讀法寫在同一段：

```c
read(idxFile, buf, 0xe10);                    // 一次讀 3600 bytes
p = buf + tm_min * 0x3c;                      // 以「分」定位，每分鐘 0x3C = 60 bytes
for (i = 0; i < 0x3c; i++, p++)               // 60 個 slot＝每分鐘 60 秒
    if ((*p & 0xf) != 0 && (*p & 0xf) != 3)   // 低 4 bit＝錄影型態；0 與 3 不算有錄
        return 有錄影;
```

- **索引 = 3600 bytes/hour ＝ 60 分鐘 × 60 bytes，一個 byte 對一秒**；型別在低 4 bit。
- 值 `0`＝無錄影、`3`＝被查詢排除（語意未定，不猜），其餘＝該秒的錄影型態。
- 這更正了 `QCV8001-BEHAVIOUR-SPEC.md` §2.1 原本的「100 槽 × 60 bytes」（100×60≠0xE10）。

> 對 HeliVMS 的意義：`PLAYBACK`／`CATALOG`／`GET_MONTH_DATE` 這幾條 NEMO 查詢在參考實作
> 就是讀這個索引。我們是讀 SQLite，**線路上等價**——前提是查詢結果一致（見 §3 缺口 5）。

### 1.3 容量需求與儲存池

`GetRecordReservedBytes(int channels, int frameBufferLen)`（`0x18000ab30`）：

```
raw = channels × 64 × frameBufferLen
reserve = max(raw, 1 GiB)                     // 0x40000000 下限
```

`FUN_18000ab50`（`0x18000ab50`）用同一組數字算出「這顆儲存池需要多大」：

```
需要 = max(raw + 1 GiB, 1.25 × raw)           // 大容量時 1.25 倍勝出，小容量時 +1 GiB 勝出
另一模式（flag ≠ 0）：需要 = max(channels × frameBufferLen, 128 MiB)
```

- 也就是說參考實作的**磁碟大小需求是估出來的**（每通道 × 64 × 一個緩衝長度），不是量出來的。
- 儲存池本體是 `QC_NVRStorage`／`NVRStorageSystem`（`initial_storage`、`0x1800026c0`；
  物件 +0x62e8 固定值 `0xb4`＝180），多槽由設定決定
  （`QC_Setting_Record::GetRecStorage`／`GetRecStorageInfo`，`FUN_180006560` 逐槽掃描）。
- 同一顆物件裡有 `CQC_Recovery`（`FUN_180002730` 的 vftable）＝**開機時的儲存回收/修復**。

### 1.4 回收（空間滿）

- 錄影側：`qc_rec_recycle.cpp`——**容量滿即刪最舊**（`QC_Record\README.md` §2；
  錯誤字串 `Error(...,0x60/0x64/0x68,"Recycle failed!")` 在 `FUN_18000adc0`）。
- 日誌/資料庫側：`QC_Database` 的 `RecycleLog`＝`delete ... where utc_sec < <cutoff>`，
  依保管期限整批刪除（`QC_Database\README.md` §2 保養策略）。

### 1.5 單檔時長、備援錄影、JPEG 連拍

| 機制 | 出處 |
|---|---|
| 單檔秒數可調 `RecFileSeconds`／`QC_Advanced::GetRecordFileSeconds()` | `QC_Record\README.md` §2；`FUN_...`（`GetRecordFileSeconds` 在 `functions.txt:6589` 被呼叫） |
| 每通道錄影器 `initial_record(ch, QC_Stream*, QC_Loading*, QC_Alarm*)` | `QC_Record\README.md` §1 |
| **備援錄影** `initial_recbkup(ch, stream, alarm)` | 同上 |
| **JPEG 快照連拍** `initial_jpeg_record(stream)` | 同上 |
| 全系統服務 `RecordingSystem.Start251/Stop251`（251＝錄影型態代碼）、`SetQuality(float)` | 同上 |
| 錄影碟上會被複製進播放器（`HDPlayerInstall::Install`） | 同上 §4（安全觀察：錄影碟因此含可執行檔） |

### 1.6 CMS 端的事件錄影與快照

| 機制 | 出處 |
|---|---|
| 進階事件錄影（ASR）**直接輸出 MP4**：`"%s%04d_%02d_%02d\\"` ＋ **`"%sASR%03d_%lld_%lld.mp4"`**（事件號_起_訖） | `QC_Center\README.md` §2 |
| 事件快照檔名 `"Img_%d_%d_%02d%02d%02d.jpg"` | 同上 |
| 設定備份 `setting/backup.bin`、`backup.bak`；工作站端 `/QCBackupSystem.bin` | `QC_Center\README.md` §2；`BackupSystem\README.md` |

---

## 2. HeliVMS 的儲存機制（現況）

### 2.1 目錄與檔名

```
<錄影根>\<頻道資料夾>\
    ├─ seg-HHmmssfff.mp4            每段一檔（正式檔）
    ├─ seg-HHmmssfff.mp4.tmp        錄影中；收尾成功才改名
    └─ merged-HHmmssfff.mp4         選用的小時合併檔（remux，不重編碼）
```

**頻道資料夾是頻道名稱**（M311，`RecordingFolderNaming`）：`ch{id:000}`（`ch001`）只是
**舊名／退回名**——名稱不可用（全空白、非法字元清完為空）時才用它。原因是磁碟搬到別台時
`ch007` 沒有任何辨識價值，而名稱讓資料夾自己說得出是哪一台；代價是名稱要清成檔名安全、
超過 80 字截斷、同名以 `_ch{id}` 消歧，而**錄影端與搬移端必須用同一個函式**
（否則已經搬好的目錄名稱會和寫入端對不上）。

出處：`src/HeliVMS.Storage/RecordingStorage.cs`（`RecordingsDirName`、`FolderForDrive`）、
`src/HeliVMS.Storage/RecordingFolderNaming.cs`（`Sanitize`／`LegacyName`／`ResolveFor`）、
`src/HeliVMS.Recording/SegmentRecorder.cs`。

### 2.2 索引

SQLite `index.db` 的 `segments` 表是**唯一可查詢的事實來源**（`channel_id, stream, start_time,
duration_sec, size_bytes, status, tier, origin_path`…），外加 `SegmentShardRegistry` 分片。
`user_version` 之外還有 `SqliteStore.EnsureAdditiveColumns` 無條件補欄（schema 漂移的教訓）。

### 2.3 保留與汰除（語意：保留空間，不是配額）

| 機制 | 實作 | 關鍵數字 |
|---|---|---|
| 寫入門檻 | `recording.min_free_bytes`（`RecordingStorage.MinFreeSettingKey`） | 預設 **2 GiB** |
| 壓力汰除 | `RetentionService.ApplyPressure`：低於門檻才刪最舊 final 段，刪到「門檻＋最近一小時實際寫入量」 | 補充水位下限 `PressureRefillBytes`＝**1 GiB** |
| 總量配額 | `RetentionService.Apply(quotaBytes)`；**產品路徑一律傳 0＝停用** | `if (quotaBytes > 0)` 是防「刪到一滴不剩」的閘 |
| 保存鎖定 | `LegalHoldRepository.IsLocked` 豁免汰除 | 證據優先於空間 |
| 汰除優先序（配額路徑） | `ListReclaimCandidates`：全時／排程／手動段先，**事件段最後**，同層由舊至新（2026-11-11 修正，見 §5.2） | 壓力路徑嚴格由舊至新 |
| 暫存殘檔 | `PurgeStaleTmp`：`*.tmp` 超過 **10 分鐘**刪除（先救援再清除） | `RetentionCoordinator.Run` 順序 |
| 快照 | `PurgeSnapshots`：依檔案時間刪 | `snapshots.retention_days` 預設 **30 天** |
| 警報/稽核 | `MetadataRetentionPruner`：`alarm.retention.days`／`audit.retention.days` | 預設 **365 天** |

### 2.4 對帳與救援（參考側的 `CQC_Recovery` 等價物，且更細）

| 情境 | 機制 |
|---|---|
| 索引有、磁碟沒有 | `SegmentIndexReconciler`（只認 final 資料夾，不逐檔 stat） |
| 磁碟有、索引沒有 | `RecordingDiskImporter`（`seg-`／`merged-` 都認；**不猜頻道**，資料夾對不上時由操作員指定） |
| 中斷收尾的 `.tmp` | `TemporarySegmentRecovery`（在 `PurgeStaleTmp` 之前跑） |
| 收尾途中失敗 | `SegmentRecorder.TryCompleteMovedSegment`：改名成功後才失敗的段用磁碟事實收尾；`.tmp` 還在時不碰同名正式檔 |
| 人工重掃 | `AdoptOrReviveFinal`（把 `missing`／`tmp`／`corrupt` 救回） |

### 2.5 其他

| 機制 | 實作 |
|---|---|
| 單檔秒數 | `recording.segment_seconds`；預設 **300 秒**、範圍 5–600（`RecordingSegmentPolicy`） |
| 小時合併 | `recording.hourly_merge`（選用）：remux → ffprobe 驗時長 → 改名 → 索引交易 → **最後才刪原檔** |
| 多碟 | 錄影一律避開系統碟；`recording.root`＋`RecordingStorage.KnownRoots`（切換磁碟後舊根仍可汰除） |
| 磁碟壓力 | `DiskPressurePolicy`：已用 ≥ **85%** 預警；可用 ≤ 門檻＝危急；**全部**磁碟都危急才停新錄 |
| 事件快照 | 資料根 `snapshots/`；`alarm_events.snapshot_path`＋`snapshot_hash`（`VisualHash` 唯一管線） |
| 設定備份 | `SettingsBackupService`：22 張設定表 → 單一 JSON（有 `version`）；**不含憑證/資料表**；匯入原子交易；admin-only＋稽核；DPAPI 綁機器 |
| 匯出 | `ExportService`：`-ss`／`-t` 依輸入端裁切；`inpoint` 實測無效（不要改回去） |

---

## 3. 逐項對照表

| # | 面向 | QCV8001（參考） | HeliVMS | 判定 |
|---|---|---|---|---|
| 1 | 目錄結構 | `YYYY_MM_DD\HH_mm\ChannelNNNN\` | `<頻道名稱>\`（舊名 `chNNN`）＋檔名帶時刻 | **刻意不同**（見 §4） |
| 2 | 錄影索引 | 每小時 `.idx`（3600 B，1 byte/秒，低 4 bit 型別） | SQLite `segments`（＋分片） | 等價（線路查詢一致即可） |
| 3 | 單檔時長 | `RecFileSeconds` 可調（預設未定） | `recording.segment_seconds` 300s（5–600） | 等價 |
| 4 | 空間不足時 | 滿即刪最舊（`qc_rec_recycle`） | 低於保留空間才刪最舊；刪到「門檻＋最近一小時寫入量」 | **更強**（可預期、可解釋、不刪到門檻以下） |
| 5 | 保留語意 | 容量導向（滿了才動） | 保留空間導向（`min_free_bytes`）＋法律保存豁免 | **更強**（證據不被空間逼走） |
| 6 | 事件/稽核保管 | `RecycleLog`：`utc_sec < cutoff` 整批刪 | `MetadataRetentionPruner` 三類各自天數 | 等價 |
| 7 | 快照 | `Img_<ch>_<id>_<時間>.jpg` | `snapshots/`＋DB 指標＋感知雜湊 | **更強**（可搜尋、可去識別） |
| 8 | 儲存池/多碟 | 設定檔列出的多槽輪替 | `CreateRootSelector`＋`ChooseRoot`：依「主要碟排首位」的順序挑第一個還有保留空間的碟，全都不夠時挑空間最多的；錄影一律避開系統碟 | 等價 |
| 9 | 開機回收/修復 | `CQC_Recovery`／`NVRStorageSystem` | 四個獨立機制（§2.4），順序被測試釘住 | **更強** |
| 10 | 容量需求估算 | `GetRecordReservedBytes`＋1.25 倍／+1 GiB | 只有保留空間門檻 | **缺口 1** |
| 11 | 剩餘可錄時間 | Web／原生 `queryRemainRecTime` | **沒有** | **缺口 1**（同一件事的兩個面） |
| 12 | 備援錄影 | `initial_recbkup` | `RecordingOwnership`＋兩條錄影路徑（牆／排程），語意是「不重複錄」 | 語意不同，非缺口 |
| 13 | JPEG 連拍 | `initial_jpeg_record` | 只有事件單張快照（`SnapshotGrabber`） | 缺口 3（價值未證） |
| 14 | 事件錄影直出 MP4 | ASR：`ASR<事件>_<起>_<訖>.mp4` | 分段檔＋匯出工具（`ExportService` 裁切） | 缺口 4（我們有匯出，沒有自動成品） |
| 15 | 設定備份 | `backup.bin`／`/QCBackupSystem.bin` | 22 表 JSON、版本、原子匯入、稽核 | **更強**（但格式不相容，見 §4） |
| 16 | 播放器複製進錄影碟 | `HDPlayerInstall`（錄影碟含 exe） | 不做 | **刻意不做**（安全） |

---

## 4. 為什麼不搬目錄/索引格式（決策與理由）

1. **線路上不需要。** QCC 的月曆、`CATALOG`、`OPENFILE`／`SEEKFILE` 都是**問我們**，
   我們從 SQLite 回答；沒有任何 QCC 端證據顯示它會直接讀磁碟上的 `.idx`。
2. **索引是我們救援能力的根。** `status`（tmp/final/missing/corrupt）、`origin_path`、
   分片、感知雜湊都掛在 `segments` 上；搬成 `YYYYMMDD-HH.idx` 等於重寫
   `SegmentIndexReconciler`／`TemporarySegmentRecovery`／`RecordingDiskImporter`／`RetentionService`
   與 2,600+ 測試，換到的是零個新功能。
3. **檔名即時刻是我們事故經驗的產物。** `seg-HHmmssfff.mp4` 讓「檔名」與「索引」互相可驗
   （`RecordingDiskImporter` 就是靠它重建時間軸）；參考實作的檔名不帶時刻，靠 `.idx` 再查一次。
4. **對方也會改。** 參考實作的索引版型是它的內部格式（容器格式至今「需動態驗證」），
   綁上去等於把我們的救援邏輯綁在沒有對外承諾的格式上。

**例外**：如果哪天要「把舊 QCTek NVR 的錄影碟接進 HeliVMS 回放」，
那是一個**讀取器**（新的實作路徑），不是改我們的儲存格式——屆時用 §1.2 的版型實作。

---

## 5. 缺口與下一步（依價值排序）

| # | 缺口 | 為什麼值得 | 形狀 | 驗收 |
|---|---|---|---|---|
| 1 | ~~沒有容量需求／剩餘可錄時間估算~~ → **已實作（2026-11-11）** | 現場問「這顆碟還能錄多久」「幾路要配多大的碟」時，目前只能靠猜；參考實作兩者都有（`GetRecordReservedBytes`、`queryRemainRecTime`） | 純函式 `RecordingCapacity` ＋量測入口 `RecordingCapacityService`：以**實測寫入量**推算，不用名目碼率；沒有寫入樣本時回「未知」而不是 0 或無限大；低於保留水位回 0 | 設定中心「儲存」頁與 `GET /api/recording/capacity` 用同一份計算；見 §5.1 |
| ~~2~~ | ~~多碟輪替策略未明~~ → **已釐清：實作已存在** | 「新段寫哪一顆」由 `CreateRootSelector`→`ChooseRoot` 決定（見 §3 第 8 列），設定中心的多磁碟清單順序就是優先序 | 不用做 | — |
| 3 | JPEG 連拍（`initial_jpeg_record`） | 參考實作有；但我們的快照已是事件驅動＋可搜索，連拍要新 UI 與大量磁碟 | 暫緩（記錄理由即可） | — |
| 4 | 事件錄影自動輸出 MP4（ASR） | 我們的匯出工具已能裁切；自動成品是便利性，不是能力缺口 | 暫緩 | — |
| 5 | 索引與 NEMO 查詢的一致性 | 若哪天索引補欄或改語意，`CATALOG`／月曆會跟著錯；目前由測試釘住 | 保持現有守門（`QctekEventPayloadsTests` 等） | — |

> 缺口 1 是唯一「有參考依據、有現場價值、且不動儲存格式」的一項，優先開工；
> 其餘三項在文件裡留原因，避免日後重做同一批評估。

### 5.1 剩餘可錄時間：實作口徑（2026-11-11）

| 決定 | 理由 |
|---|---|
| 輸入是**實測寫入量**，不是名目碼率 | 攝影機送的是變動位元率（`-c:v copy` 複製的是攝影機自己的時間戳與位元率），名目碼率算出來的「還能錄 30 天」會在 12 天後變成真的沒空間 |
| 速率分母是**實際涵蓋範圍**（最早段起點→現在），夾在 10 分鐘與一小時之間 | 半小時前才開始錄的機器只涵蓋半小時，除以一小時會低估速率、把剩餘時間報成兩倍（799→「還能錄 8 小時」在 4 小時後爆掉）；一段 5 秒的事件錄影若直接當分母會算出天文數字，所以有 10 分鐘的下限 |
| 三態：`known`／`at_reserve`／`unknown` | 「沒有樣本」要去看錄影有沒有在跑；「已低於保留空間」要去清磁碟或調保留空間。兩者都印成 0 會讓第一個問題永遠不會被問出來 |
| 只有 `free − reserve` 拿來估算 | 保留空間是壓力汰除的停止線，越過它錄影就會被汰除；拿 `free` 全額估算等於保證不會發生的事 |
| 時長一律**向下取整** | 向上取整會把「23 小時 40 分」顯示成「2 天」，而操作員會據此決定今天要不要擴充磁碟 |
| 量測只有一份實作（`RecordingVolume.WrittenBytes`） | 壓力汰除與剩餘時間問的是同一個問題；兩份實作會讓同一顆碟在兩個畫面上出現兩個數字 |

出處：`src/HeliVMS.Recording/RecordingCapacity.cs`（純函式）、
`RecordingCapacityService.cs`（量測入口）、`RecordingVolume.cs`（共用寫入量）、
桌面 `SettingsWindow.ReloadRemainingRecordTime`、API `GET /api/recording/capacity`。
守門：`RecordingCapacityTests`（20）、`RecordingCapacityServiceTests`（6）、
`RecordingCapacityApiTests`（2）、`RecordingCapacityContractTests`（6）。

### 5.2 第二輪：設定鍵逐項對照，以及一個被對照揪出來的語意錯誤

**（a）參考側的錄影相關設定鍵，逐一歸類**（先前只列了名字，這裡把語意釘住）：

| QC 鍵（`Advanced`／`qc_setting.xls`） | 實際語意（出處） | HeliVMS 對應 | 判定 |
|---|---|---|---|
| `EventReserveDays`（實機值 **184**） | **事件／警報／車牌「日誌」的保管天數**，不是錄影影片：`QC_Schedule::RecycleEventLog` 拿它去呼叫 `QC_AlarmLog::RecycleLog(days)`／`QC_EventLog::RecycleLog(days)`／`QC_VtmsLog`／`QC_CMSAlarmLog`（`QC_Center` 0x13d70、`NVRSetting` 把天數映射成 6 段下拉） | `alarm.retention.days`（警報事件＋事件錄音）＋`audit.retention.days`（稽核）＋`MetadataRetentionPruner` | **等價**（命名會騙人：看到 "EventReserveDays" 別以為是影片保留空間） |
| `RecFileSeconds` | 單檔秒數 | `recording.segment_seconds`（300 秒、5–600） | 等價 |
| `RecStorage`／`StoragePath`（實機 `QC_Storage`） | 錄影根目錄（可多槽） | `recording.root`＋`recording.disks`＋`ChooseRoot` 輪替 | 等價 |
| `FileSizeLimit` | 單檔**大小**上限（`QC_Reg::ReadNumber`） | 我們用**時間**上限（`recording.segment_seconds`） | 等價（更強的槓桿：檔大小隨碼率浮動，時間才是可預期的那個） |
| `ShutdownAfterRecordStopping` | 離開時等錄影收尾再關機 | `SegmentRecorder.StopAsync`（先 `q` 後 kill）＋`ShutdownSequencer`＋`ShutdownWatchdog` | **更強**（有界收尾＋兜底，且行程若死掉不會留半截檔） |
| `BackupPlayer`（實機 1） | 把播放器複製進錄影碟（`HDPlayerInstall`） | 不做 | **刻意不做**（錄影碟含可執行檔＝擴大執行面） |
| `RecordingSystemMode` | 執行期模式旗標（QC_Live 用它決定顯示／行為分支），非儲存策略 | — | 非儲存語意，不比對 |

**（b）被對照揪出來的語意錯誤：配額汰除原本會先刪事件段（2026-11-11 修正）**

`ListReclaimCandidates` 的原本排序是「事件模式（`recording_mode = 2`）**先**、其他後」，理由是
「事件段是全時段的補充，連續錄影才是主證」。這個前提在 HeliVMS 不成立：**錄影模式是互斥的**
——事件模式的頻道只在事件窗口內錄影（`recording.event_seconds`），那些片段是**它唯一的畫面**，
不是任何東西的補充。舊排序的實際後果：混用模式的站台在配額壓力下先失去事件模式頻道的全部證據，
而全時頻道好幾天的量原封不動，log 乾淨（連續錄影是會再生的，事件片段不會再發生一次）。

修正：`ORDER BY CASE WHEN c.recording_mode = 2 THEN 1 ELSE 0 END`（事件段最後），並補上
「只剩事件段而仍超額時仍會由舊至新汰除」的測試——「留到最後」不等於「永遠不刪」，
少了那一條，「事件段永不刪」會讓配額永遠降不下來、磁碟無聲寫爆。
「絕不刪」仍然只有保存鎖定（`LegalHoldRepository`）一個機制。

> 這一項與參考實作的關係是**方向一致**：QC 用 `EventReserveDays` 保護事件**日誌**、
> 我們用排序層保護事件**影片**；兩邊都不會讓事件材料比連續材料先被淘汰。

**（c）被「當錄影滿了會如何」這個問題揪出來的第三個缺口：磁碟滿閘門只蓋住排程路徑**

參考實作是「容量滿即刪最舊、繼續錄」；我們是「低於保留水位先汰除，全滿才停開新錄」。
逐一追問「誰會停、誰不會停」時發現：`RecordingScheduler`（全時／排程／事件）有 `diskFull` 閘門，
但**監看牆上的手動錄影按鈕沒有**——`ChannelSession.SetRecordingAsync` 只問授權閘門。於是滿碟上
手動開錄會建出一台每段都失敗的錄影機，而 `SegmentRecorder.IsRecording` 只看迴圈有沒有結束
（`_loop is { IsCompleted: false }`），所以 REC 徽章亮著、畫面看不出異常，只有一筆
`recording.stalled` 稽核——正是「看起來在錄、其實沒錄」那一類。

修正：手動路徑也問 `DiskPressureCoordinator.ShouldStopNewRecording`（與排程同一道閘門、同一個
判準：**每一顆**錄影磁碟都低於門檻才擋，多碟站台不因某一顆滿了就全面停錄）；擋下時回
`RecordingGateResult` 並帶 `DiskFull = true`（`Decision` 刻意維持 `Valid`），
`LicenseLimitNotifier` 才不會把它記成「已達授權上限」——把操作員送去查一張沒問題的授權是錯的調查方向。

> 守門測試的教訓值得記下來：第一版契約用**裸字串**比對 `ShouldStopNewRecording`，把整個 `if`
> 區塊刪掉之後它仍然全綠——因為上面那段說明註解裡有同一個字串。改成比對 **`if (…)` 的呼叫形狀**
> （排程端則要求 `_diskFull?.Invoke(`）之後，突變才真的會紅。原始碼掃描的價值全在「真的有人接線」，
> 而註解會把字串比對餵飽。

守門：`ListReclaimCandidates事件模式區段排在最後且同層由舊至新`、
`配額汰除先刪全時段而事件段留到最後`、`只剩事件段而仍超額時仍會由舊至新汰除`。

### 5.3 第三輪：磁碟健康（S.M.A.R.T.）——參考側有頁面、沒有取樣證據

**參考側的事實（含證據等級）**：

| 觀察 | 證據 | 等級 |
|---|---|---|
| 有磁碟 SMART 頁 | `QCV8001\UI-ROUTES.md`：`config/disk/information/smart`（另有 `diskArrayCfg`／`diskMode`／`physicalDiskCfg`／`storage/mode`） | 已觀察（UI 路由） |
| 取樣機制 | **查無證據**：反編譯語料裡沒有 WMI 類別（`MSStorageDriver_*`）、沒有 ATA pass-through IOCTL、`PROTOCOL-WS.md` 也沒有磁碟健康命令 | 未知（不可推論） |
| 頁面欄位 | **查無證據**：語料只有反編譯函式與 .md 摘要，沒有 web 資產（`ws\view\...`） | 未知 |

換句話說：**操作員會期待這台 NVR 有磁碟健康頁**（參考實作有），但「它到底量了什麼」我們不知道。
所以這一項不是協議對齊，是照 Windows 上有文件的做法自建；要對齊欄位得先拿到那頁的資產或實機。

**HeliVMS 的實作（2026-11-11）**：

| 層 | 內容 |
|---|---|
| 純函式 `DiskHealth` | ATA 屬性區解析（12 bytes × 30 項；屬性 5 重新配置、197 待重新配置、194 溫度、9 通電時數）＋等級（`Failing`／`Warning`／`Healthy`／`Unknown`）＋`Worst`（未知不掩蓋已知） |
| 讀取器 `WmiDiskHealthSource` | `Win32_LogicalDisk`→分割區→`Win32_DiskDrive` 對應實體碟，再以 `PNPDeviceID` 對上 `root\wmi` 的 `FailurePredictStatus`／`ATAPISmartData`；每一段各自 fail-open |
| 協調 `DiskHealthCoordinator` | 只回報**錄影磁碟**；等級改變才寫 `disk.health` 稽核（執行期狀態存 `app_settings`，不記 `settings.set`）；讀不到一律未知 |
| 顯示 | 設定中心「儲存」頁逐顆一行（等級＋型號＋溫度）、每小時迴圈提示列、Headless Agent 分級 log、`GET /api/system/disk-health` |

三個刻意的決定：

1. **`Unknown` 與 `Healthy` 分開**（`未提供` vs `正常`）：不是管理員、控制器不外露、RAID/USB 橋接後面
   看不到實體碟——這些都會讓畫面一片綠，而硬碟已經在重新配置磁區。未知印成健康是最貴的一種靜默失效。
2. **`Failing` 的判準只看兩件事**：控制器預測故障、以及**待重新配置磁區 ≥ 1**（讀取失敗、等重映射，
   資料已在流失）。已重新配置磁區與高溫（≥55°C）算 `Warning`——還能用，但要準備換。
3. **不做「健康時也寫稽核」**：一顆預測故障的硬碟會連續好幾天維持同等級，每小時一筆會把稽核表灌滿
   （與磁碟壓力同一條去重規則）。

與既有機制的分工：`DiskPressurePolicy`＝碟滿了（清檔／擴充）、`MediaIntegrityChecker`＝已封檔區段的
**解碼**抽查（事後才知道某一格讀不出來）、`DiskHealth`＝硬碟自己講的早期訊號（換碟）。
三者互補，任何一個都不能取代另一個。

守門：`DiskHealthTests`（12）、`DiskHealthCoordinatorTests`（5）、`DiskHealthContractTests`（4）。

---

## 6. 這份文件不包含什麼

- 不重述 `docs/ARCHITECTURE.md` 的模組圖與 `docs/LIVEVIEW.md` 的顯示成本。
- 不把反編譯的樣板警語抄進來；要追細節就回 `D:\SDK\REA\<模組>\README.md` 與
  `<模組>\decompiled\functions.txt`（以 `// name:` 或 `address` 定位）。
- 參考側的容器格式仍未動態驗證，本文件不主張任何容器級事實。
