# QCTek QCC ↔ HeliVMS：裝置相容進度交接（2026-11-08）

> **新 session 接手用**：本文件＋`git` 紀錄＋磁碟上的抓包/日誌/反編譯即為完整上下文，
> 不必重讀舊對話。
> 目標：讓 **QCTek QCC（CMS）能連上並管理 HeliVMS**（把 HeliVMS 當成 QCV8001 裝置）。

---

## 一句話現況

**NEMO 控制協定已完整解出並實作**（`src/HeliVMS.Qctek/Nemo`），以 QCC ↔ 真機 QCV8001 的
黃金抓包逐位元組驗證。
**80 埠多工也已實作**：相容服務一啟用，公開的 Web 埠就由前置多工器（`QctekPortMux`）持有，
NEMO 在本行程應答、HTTP 原封不動轉給內部的 Kestrel 臨時埠（與真機
`HttpServer::PreParseRequest` 同一個模型）。
實機踩到的坑（已修）：`web.port=80` 配出廠預設 `qctek.compat.port=5170` 時，舊規則
「兩埠相同才共用」讓 80 上只有 Kestrel，QCC 加得進裝置卻永遠「連線中斷」（NEMO 命令換到
一句 `400 Bad Request`，日誌乾淨）。QCC **沒有地方可以填 NEMO 埠**，所以現在只看相容服務
有沒有啟用，與相容埠填什麼無關；5170 那類號碼只給「在 CMS 端明確指定埠」的遠端站台。
**即時影像（live view）已實作**（`src/HeliVMS.Qctek/Live`）：`GET_LIVE_START` 之後同一條
連線交棒給 `QctekLiveSession`——讀 QCC 的 0x90-byte keepalive、依頻道位圖開關每頻道的
ffmpeg 來源、以 33ms tick 打 FRAM 回給 QCC（見下方「即時影像」章節）。
下一步＝**在實機上讓 QCC 連 `127.0.0.1:80` 走完上線／頻道／警報／即時畫面**（見文末驗收清單）。

---

## 協定（已解出，全部有抓包證據）

證據：`%TEMP%\opencode\qcc-capture\golden.pcapng`（tshark 抓 loopback，QCC ↔ 真機 QCV8001，
32 條短連線）。逐條 hex 存於 `streams.txt`，二進位 payload 存成 `config.bin`（312 B）／
`authuser.bin`（152 B）／`ioconfig.bin`（280 B）。測試常數集中在
`tests/HeliVMS.Qctek.Tests/GoldenCapture.cs`，是**證據**，不是「看起來合理」的期望值。

### 連線模型

- **每個命令一條短 TCP 連線**（QCC 實測行為）。協定本身允許續用，實作也支援。
- **QCC 不會送 `CONNECT`**。工作上線後的控制通道認證完全由 `QCV8001_AUTH` 挑戰交換完成。
  （`CONNECT` 仍實作，供其他 CMS／工具使用。）
- 同一條連線上，命令可能以 raw（ASCII＋NUL）或 NEMO 框送出。

### 分幀（請求端）

| 型式 | 線路 | 例 |
|---|---|---|
| raw | ASCII 命令＋結尾 `NUL` | `CHECK_SERVER_VERSION CLIENT_AUTH QCTEK \0`（40 B） |
| NEMO | 16 B 標頭（`NEMO <十進位長度>`＋空白補滿）＋payload | `NEMO 35         GET_CONFIG_QCTEK …`（16+35=51 B） |

`NEMO` 標頭長度＝payload 位元組數（伺服端驗證「長度＝總長−16」，不符即丟棄）。
`ALARM_ISALIVE` 是特例：標頭宣告 34，payload 內最後一個位元組就是命令的 `NUL`（總長 35）。

### 回應分幀（**逐命令不同，不要統一**）

| 命令 | 真機回應 | 備註 |
|---|---|---|
| `CHECK_SERVER_VERSION` | raw `OK QCV8001 \0`（12 B） | 未 framed 時 |
| `CHECK_SERVER_VERSION`（framed） | `NEMO 11` ＋ `OK QCV8001 ` ＋ `NUL` | 標頭**少宣告 1**（實際 28 B） |
| `CONNECT` 成功 | raw `OK -1 -1 -1 \0`；framed `NEMO 12`＋12 B | framed 無 NUL |
| `CONNECT` 失敗 | raw `Login Failed \0`；framed `NEMO 13`＋13 B | 同上 |
| `GET_NCHANNEL` | `OK <n> `（framed 無 NUL） | raw 有 NUL |
| `GET_MAX_STREAM_NUM` | `OK <n> `（framed 無 NUL） | |
| `ALARM_REQUEST` 成功 | raw `OK 0 1 \0`（8 B）；framed `NEMO 7`＋7 B | framed **無** NUL |
| `ALARM_REQUEST` 失敗 | raw `FAIL \0`；framed `NEMO 5`＋`FAIL `＋`NUL` | 兩種框法不同，照抄 |
| `ALARM_ISALIVE` | `NEMO 6`＋`ALIVE `＋`NUL`（NUL 在宣告長度外） | |
| `ALARM_CANCEL` | `NEMO 3`＋`OK `＋`NUL` | |
| `GET_CONFIG_QCTEK` | `NEMO 312`＋312 B 二進位，無 NUL | 與請求框法無關 |
| `GET_AUTH_USER` | `NEMO 152`＋152 B 二進位，無 NUL | |
| `GET_IOCONFIG` | `NEMO 280`＋280 B 二進位，無 NUL | **不經挑戰** |

**宣告長度外多出來的 `NUL` 必須被吸收**（`CHECK_SERVER_VERSION`、`ALARM_ISALIVE`、
`ALARM_CANCEL`）。只讀「標頭宣告的長度」會讓下一輪從一個孤立 NUL 開始——實作在
`QctekSession.AdvanceFramed` 把同一條命令後的 NUL 一起吃掉，並容忍連線開頭的孤立 NUL。

### 認證（`QCV8001_AUTH` 挑戰交換）

```
QCC → GET_CONFIG_QCTEK admin QCV8001_AUTH            （framed）
真機 → <27 bytes 挑戰，無 NUL>                       例：lf6AdoSqOMFtt3ln 1773482693
QCC → QCV8001_AUTH_SENDCODE <32 hex> ＋ NUL           （raw）
真機 → NEMO 312 ＋ 312 bytes
```

- 挑戰＝16 bytes 隨機字元＋空白＋**10 位 Unix 秒**（共 27 bytes），raw 送出、**沒有 NUL**。
  整數必須是十位數：挑戰既沒有長度欄位也沒有結尾 NUL，長度就是協定的一部分；用九位亂數
  會變成 26 bytes，讀固定 27 bytes 的用戶端會把下一個回應的第一個位元組吞進挑戰裡，整條
  連線從此錯位、兩邊日誌都乾淨（`QctekChallenge.TotalLength = 27`，測試釘住）。
- 需要挑戰的命令：`GET_CONFIG_QCTEK`、`GET_AUTH_USER`、`GET_LIVE_START`。
  **`GET_IOCONFIG` 不需要**（黃金 stream 18：直接回二進位）；把它一併擋下，QCC 會卡在等挑戰。
- 失敗回 `NEMO 12`＋`Login Failed`（12 B，無 NUL、無尾空白）。
- 授權狀態在同一條連線上持續有效；挑戰途中來了別的命令會**丟掉過期的待辦**（否則之後的
  sendcode 會拿去執行舊命令，回應與請求錯位：QCC 收到 312 B 卻以為那是頻道數）。

### 二進位 payload 版型

三個 payload 共用開頭 `"QCTEK_RM"`＋4 bytes 版型碼：

| payload | 長度 | 版型碼（位移 8） | 其餘 |
|---|---|---|---|
| `GET_CONFIG_QCTEK` | `0x18 + n * 0x120`（單頻道 312） | `1C 3B 34 01` | 見下 |
| `GET_AUTH_USER` | 152（固定） | `1C 3B 34 01` | 位移 20＝頻道數（真機實測 36）；位移 24 起 128 B 全 `0xFF`（權限位圖） |
| `GET_IOCONFIG` | 280（固定） | `6D 3D 34 01` | 位移 12 起全零（無 DI／DO） |

設定 payload 標頭：位移 12＝`u32 0`、位移 16＝`u32 0xFFFFFFFF`（常數，語意未知）、
位移 20＝`u32 n`。每筆記錄 0x120 B：記錄開頭 `01 00 01 01 01 01 00 01`（能力／狀態位元組，
**逐位元組照抄真機**，語意在未反編譯的 `QC_Device.dll` 裡），名稱在記錄位移 `0x1C`
（檔位移 `0x34`），純 UTF-16LE、容量 0x41 wchar（64 字＋NUL），超過截斷在欄位內。
黃金樣本＝`攝影機001`（`1d 65 71 5f 5f 6a 30 00 30 00 31 00`）。

### `ALARM_REQUEST`／`ALARM_ISALIVE`

- `ALARM_REQUEST 5122 version7.7 user: admin YWRtaW4 =QCV0000000000`：密碼是 **Base64 無填補**
  （`YWRtaW4`＝`admin`）。憑證錯誤回 `FAIL`。成功回 `OK 0 1 `（真機值，QCC 原樣存下）。
- `ALARM_ISALIVE`：HeliVMS **一律回 `ALIVE`**，刻意不重現真機對第二個序號的 `NOT ALIVE`——
  那會讓 QCC 認為訂閱掉了而重送 `ALARM_REQUEST`，形成無意義的重連迴圈。

---

## 即時影像（live view，已實作，待 QCC 實機驗證）

證據：伺服器 live 迴圈 `QC_Server` 的 `FUN_18001eb80`（逾時／狀態碼）、逐頻道寫出的
`FUN_18001f0d0`、FRAM writer `FUN_18001cb50`；客戶端 `dvrProxy_live::GetLiveSend`
（keepalive 逐欄位）、`readQCV8001frame`（解析與指標重算）、`QC_StreamDec::Put`／
`DecProc`（餵解碼器與牆鐘調速）、`SplitIFrame_H264`（block1/block2/video 的切出形狀）、
`DummySink::GetSpsPps`（SPS/PPS block 帶 4-byte start code）。

### 流程

```
QCC → GET_LIVE_START <user> QCV8001_AUTH （raw＋NUL；命令本身不帶頻道）
     ← 27 bytes 挑戰 →（認證碼）→ "OK "＋NUL（raw 4 bytes；黃金 4f4b2000）
之後同一條連線不再是文字命令：
QCC → 0x90-byte keepalive（每 250ms 一個）
     ← FRAM（33ms tick，每頻道每 tick 至多一個）／8-byte 狀態幀（每秒）
```

- **`GET_LIVE_START` 的成功回覆必須剛好 4 bytes**（`"OK "`＋NUL）。用戶端只看前兩字元
  `OK`，但之後立刻改讀 keepalive——長度錯 1 byte 讓 keepalive 解析錯位，「即時影像永遠
  連不上」且兩邊日誌乾淨。framed 請求用 `QctekReply.FramedWithNul`（標頭宣告 3、NUL 在外）。
- 頻道選擇**只**透過 keepalive 的 1024-bit 位圖（bit i＝頻道 i，LSB-first，位於 `0x10–0x8f`）；
  stream type 在 `+0x0c`（0＝主碼流、1＝子碼流，signed，負值視為錯位、關線）。
- 逾時：超過 `0x130`（304）個 33ms tick（≈10 秒）沒有 keepalive 就斷線（真機 break3）。

### FRAM 封包（資料幀）

8-byte 標頭 `"FRAM"`＋u16 channel（0-based）＋u16 status（0＝資料）＋0x38-byte media header
＋payload（**順序固定 block1→block2→video**）：

| 位移 | 內容 |
|---|---|
| `+0x00` | u32 type＝1（視訊；客戶端 `Put` 只收 1）、`+0x04` u32 codec＝1（H.264） |
| `+0x08` | u32 sizeVideo、`+0x0c` u32 sizeBlock1、`+0x10` u32 sizeBlock2；幀總長＝8＋0x38＋三者和 |
| `+0x14` | u32 微秒、`+0x18` u64 Unix 秒（`qc_timeval` 的線上順序；**寫出前以牆鐘蓋章**——客戶端 `DecProc` 以封包時戳對牆鐘調速，超出容差的非關鍵幀整包丟棄） |
| `+0x20`/`+0x28`/`+0x30` | video/block1/block2 指標欄位——客戶端依 size **重算**，線上值一律填 0 |

- **block1＝SPS、block2＝PPS，都帶 4-byte start code**（`SplitIFrame_H264` 的切出形狀）；
  video 保持 Annex-B。**block1 size > 0 是客戶端「關鍵影格」的唯一判準**：SPS 只放關鍵影格
  的 block1，非關鍵影格 block1/block2 必須為 0。
- 狀態幀：status 非 0 時**只有 8 bytes**。`0x8001`＝來源已開尚無影格、`0x8002`＝沒有來源
  （頻道不存在／無法建立）。
- AU 分組（`QctekH264AccessUnitGrouper`）：ffmpeg `-f h264` 的 packet 邊界在流裡丟失，
  每個 AU 的第一個 slice 以「slice header 首位元組最高位元＝1」判（`first_mb_in_slice` 的
  ue(v) 值 0 編碼成「1」）；多 slice 的續片屬同一 AU；SEI／AUD 屬**下一個** AU；
  尾端半個 start code 留待下一包，串流結束（`Flush`）時殘留就是完整的最後一個 NAL。

### 實作

| 檔案 | 內容 |
|---|---|
| `src/HeliVMS.Qctek/Live/QctekLiveSession.cs` | live 迴圈：keepalive 解析與逾時、位圖啟停頻道、33ms tick、有界佇列（滿了丟最舊）、狀態幀每秒一個、來源重試 5 秒 |
| `src/HeliVMS.Qctek/Live/QctekLivePackets.cs` | FRAM 位元組工廠（上表逐欄位） |
| `src/HeliVMS.Qctek/Live/QctekH264AccessUnits.cs` | Annex-B 切分（跨包、半 start code）＋ AU 分組＋ SPS/PPS 抽到 block |
| `src/HeliVMS.Qctek/Live/QctekLiveSource.cs` | 來源介面（Qctek 專案不引用 Media/Storage） |
| `src/HeliVMS.Qctek/Nemo/QctekSession.cs`／`NemoSessionLoop.cs` | `QctekSessionResult.Live` 旗標與交棒（`"OK "`＋NUL 寫完後把殘餘位元組帶給 live 迴圈） |
| `src/HeliVMS.App/Services/QctekLiveStreamSource.cs` | ffmpeg 來源：`-rtsp_transport tcp`（僅 rtsp）`-an -c:v copy -bsf:v dump_extra -f h264 -`；stderr 持續讀掉（pipe 滿會卡死 ffmpeg） |
| `src/HeliVMS.App/Services/QctekCompatHost.cs` | `CreateLiveSource`：位圖 bit i＝`ChannelRepository.List()` 的第 i 個頻道；停用／不存在回 null（0x8002）；**子碼流為空退回主碼流** |

- `-bsf:v dump_extra`：部分攝影機只在 SDP 帶 sprop-parameter-sets、流內不重複 SPS/PPS；
  而 QCC 以 block1 > 0 判關鍵影格——SPS 缺席的關鍵影格會被它當非關鍵丟棄，那一路
  **永遠黑屏且沒有任何錯誤**。
- 每頻道一個短生命週期 ffmpeg 行程：CMS 要哪幾路才開哪幾路、斷線即收——與排程截圖
  同一個理由（便宜攝影機的 RTSP 工作階段有限）。輸出是 `-c:v copy` 的純 remux，伺服器
  不花解碼 CPU。

---

## 已完成的實作

| 檔案 | 內容 |
|---|---|
| `src/HeliVMS.Qctek/Nemo/QctekSession.cs` | 單一連線狀態機：raw／NEMO 分幀、孤立 NUL 容忍、挑戰→sendcode→重播待辦命令、過期待辦丟棄、不同步即關線 |
| `src/HeliVMS.Qctek/Nemo/QctekCommandHandler.cs` | 命令→線路位元組派送器；`RequiresAuthorization` 只列黃金證明需要的命令 |
| `src/HeliVMS.Qctek/Nemo/QctekReply.cs` | 五種具名框法：`Raw`／`RawExact`／`Framed`／`FramedWithNul`／`FramedBinary` |
| `src/HeliVMS.Qctek/Nemo/QctekPayloads.cs` | 三個二進位 payload 產生器（位移與長度照黃金） |
| `src/HeliVMS.Qctek/Nemo/QctekChallenge.cs` | 27 bytes 挑戰字串產生器（16 亂數字元＋空白＋10 位 Unix 秒） |
| `src/HeliVMS.Qctek/Nemo/NemoSessionLoop.cs` | 單一連線的讀寫迴圈；獨立埠與多工器**共用同一套狀態機**（否則只會修好其中一份） |
| `src/HeliVMS.Qctek/Nemo/NemoServer.cs` | 獨立埠（預設 5170）的 TCP 迴圈：NEMO＋探測 HTTP，綁 `Any`（給遠端 CMS） |
| `src/HeliVMS.Qctek/Nemo/QctekPortMux.cs` | Web 埠前置多工器：分類 NEMO／HTTP，HTTP 雙向半關閉轉發到內部 Kestrel 臨時埠 |
| `src/HeliVMS.Qctek/Nemo/QctekPortPlanner.cs` | listener 組態的純函式決策：Web 埠給多工器（不看相容埠填什麼）、相容埠不同時另開獨立 listener |
| `src/HeliVMS.Qctek/Nemo/QctekDiagnostics.cs` | 日誌摘要（密碼遮蔽、未知命令留痕） |
| `src/HeliVMS.App/Services/QctekCompatHost.cs` | 正式 `IQctekDeviceContext`（頻道名、使用者政策）；多工器／獨立伺服器兩種 `ApplyLocal` |
| `src/HeliVMS.App/Services/NetworkPortCoordinator.cs` | 兩個服務的啟停順序、失敗還原、兩條狀態文字（Web 與相容服務共用它） |
| `src/HeliVMS.App/Services/WebHost.cs` | 共用埠模式改聽 `WebListenUrl.DynamicLoopback`（`http://127.0.0.1:0`），實際埠由 `IServerAddressesFeature` 讀回；啟動失敗寫 `%TEMP%\helivms-web.log` |
| `src/HeliVMS.Qctek/Nemo/QctekPassword.cs` | 密碼欄位編解碼；**解碼容忍省略的 Base64 填補**（黃金證據：`YWRtaW4` 被真機接受） |

測試（`tests/HeliVMS.Qctek.Tests`，129 綠；`tests/HeliVMS.App.Tests` 769 綠，本機排除
`SolutionCoverageTests`——未追蹤的 `Tools/授權/LicenseKeyGen` 不在 slnx，CI 無此檔不影響）：

- `GoldenCapture.cs`：每條連線的原始 hex＋payload
- `QctekPayloadsTests.cs`：與黃金 payload **整段位元組相等**、位移、超長名稱截斷
- `QctekSessionTests.cs`：重播 stream 0/2/4/6/16/18/24/25；未知／畸形 digest、半個訊框、
  開頭孤立 NUL、授權續用、過期待辦、不同步關線
- `NemoServerTests.cs`：真實 loopback 連線跑完整黃金對話（含宣告長度外的 NUL）
- `QctekPortMuxTests.cs`：分類表（NEMO／HTTP／CONNECT／半個動詞／垃圾）、多工器上的完整黃金
  對話（挑戰＋312／280 payload）、**真實雙向 HTTP 轉發**（含分類時先讀掉的位元組要補送）
- `QctekPortPlannerTests.cs`、`QctekChallengeTests.cs`
- `NetworkPortContractTests.cs`（App）：共用埠的兩個先後順序、多工器失敗要還原 Web、
  `RemoteConsolePort` 回報公開埠、設定頁一次刷新兩條狀態列
- `QctekCommandHandlerTests.cs`、`QctekPasswordTests.cs`、`QctekDiagnosticsTests.cs` 等
- `QctekLivePacketsTests.cs`：FRAM 逐欄位（標頭／media header／時戳順序／指標全 0／payload 順序）
- `QctekH264AccessUnitTests.cs`：SPS/PPS 抽 block、非關鍵 block 空、多 slice、半 start code、
  分批＝整包、IDR 無 SPS 不附空 block
- `QctekLiveSessionTests.cs`：keepalive 解析（位圖／碼流／負值拒收）、`GET_LIVE_START` 回
  4 bytes＋交棒旗標（raw／framed／已授權重開）、**loopback 端到端**（keepalive → FRAM 輸出、
  無來源 0x8002、位圖撤頻道收掉來源）

**改壞驗證（已做並還原）**：`ConfigNameOffset 0x1C→0x18` → 4 個測試紅；
`GET_IOCONFIG` 加入 `RequiresAuthorization` → 2 個紅；framed 警報成功改成 `FramedWithNul`
→ 2 個紅；多工器分類的大寫動詞改成回 HTTP → 2 個紅；協調者的兩個順序各自顛倒／拿掉
`StopMux()`／拿掉多工器失敗後的 Web 還原 → 對應契約測試各紅；`ResolveConsolePort` 忽略
公開埠設定 → 紅；挑戰尾端改回九位亂數 → 2 個紅（其中一個是「用戶端讀固定 27 bytes」卡住）。
即時影像輪：`GET_LIVE_START` 的 `withNul` 改回 false → 1 個紅；first_mb 判準反向 → 5 個紅；
FRAM 時戳兩欄位對調 → 2 個紅；`NemoSessionLoop` 停用交棒分支 → 3 個端到端紅；
`Flush` 不處理殘留 → 4 個紅。
順帶把 payload 測試的位移改成**黃金絕對值**（不再引用 `ConfigNameOffset`）——
引用同一個常數的斷言會跟著錯誤的常數移動，永遠是綠的。

---

## 已知安全限制（刻意，需寫進設定頁說明）

1. **digest 無法驗證。** QCTek 的預期值是
   `MD5(QCTek 專有 32 bytes 密碼轉換 ++ ":" ++ user ++ ":" ++ deviceInfo)`；那份專有轉換值
   不存在於 HeliVMS 的帳號資料，所以無法比對。實作照實完成交換、只驗證「32 hex 形狀」與
   「使用者存在且可用」，並保留 `CONNECT`／`ALARM_REQUEST` 的密碼驗證。
   因此相容層是**選用、限受信任網段**（LAN）的功能，文件與設定頁都必須這樣寫。
2. **密碼等同明文。** `CONNECT`／`ALARM_REQUEST` 的密碼欄位只是 Base64 編碼，不是加密。
3. 診斷日誌一律遮蔽密碼段（`QctekDiagnostics`），測試釘住這條規則。

---

## 80 埠多工（已實作）

真機的 `QC_Live.exe` 在 80 上分辨 NEMO／HTTP（`HttpServer::PreParseRequest`）；QCC 連的就是
裝置 Web 埠。`getparam.cgi` 的 `RemoteConsolePort` 導向 **已證實不可行**（真機也不靠它），
所以公開埠改由前置多工器持有。

### 接線

1. **共用條件**：`QctekPortPlanner.Plan`（純函式）在相容服務與 Web 都啟用、且 `web.port`
   合法時，一律讓多工器接手公開埠——**不要求兩個埠相同**（QCC 沒有地方填 NEMO 埠）。
   `qctek.compat.port` 與 `web.port` 不同時，另外再開一個獨立 `NemoServer`（綁 `Any`），
   給在 CMS 端明確填了那個埠的遠端站台；相同時只留多工器，不在同一個埠開第二個 listener。
   Web 沒啟用時只有獨立埠；相容服務沒啟用時兩個都不開。
2. **順序不可顛倒**（`NetworkPortCoordinator.Apply`）：
   - 共用時：Web 先改聽臨時埠 → 多工器才綁公開埠。
   - 不共用時：`StopMux()` 先放掉公開埠 → Web 才綁得回去。
   - 多工器綁不上（公開埠被別的程式占用）→ 把 Web 還原到公開埠，管理介面至少還在。
   兩個順序反了的症狀都一樣：設定頁一句「啟動失敗」，日誌乾淨。
3. **多工器只綁 loopback**（`QctekPortMux`，保留 `WebHost` 刻意只聽本機的姿態）；
   遠端 CMS 走 5170 的 `NemoServer`（綁 `Any`，同一套 `NemoSessionLoop`）。
4. **分類規則**（`QctekPortMux.Classify`，純函式、容忍半個動詞）：第一個空白前的 token 是
   HTTP 方法（`GET`／`HEAD`／`POST`／`PUT`／`DELETE`／`OPTIONS`／`PATCH`／`TRACE`／`PRI`）→
   HTTP；`CONNECT` 只有帶 ` HTTP/` 才算 HTTP（NEMO 的 `CONNECT <user> <base64> ` 以 NUL 結尾）；
   其餘大寫動詞（含 framed 的 `NEMO`）→ NEMO；出現控制字元／非 ASCII 或動詞超過 32 B → HTTP
   （Kestrel 回 400 並關線，至少不留半開連線）。
5. **`RemoteConsolePort` 必須回報公開埠**，不是內部臨時埠：`WebHost` 以設定鍵
   `helivms.public_port`（`QctekHttpResponder.PublicPortConfigKey`）把公開埠傳給內嵌
   WebApi，`QctekProbeMiddleware` 用 `ResolveConsolePort(實際埠, 設定值)` 決定。
   回報臨時埠的症狀是「裝置加入成功、連線失敗」——那個號碼只有本行程內部知道。
6. HTTP 轉發是**雙向半關閉**：分類時先讀掉的位元組要補送到後端，否則 Kestrel 收到半個
   請求行（400、空白頁）；每個方向讀完只 `Shutdown(Send)` 目標，不能用 `Close` 截斷另一個方向。

### 驗收（實機，2026-11-08 18:24 起）

1. ✅ 相容服務與 Web 都啟用（`web.port=80`、`qctek.compat.port=5170`）→
   `%TEMP%\helivms-qctek.log` 出現
   `MUX listening on 127.0.0.1:80（HTTP → 127.0.0.1:54131）`，
   `%TEMP%\helivms-web.log` 出現 `Web 已啟動：公開埠 80，實際 54131，共用=True`。
   `netstat`：`127.0.0.1:80`（多工器）、`127.0.0.1:<臨時>`（Kestrel）、`0.0.0.0:5170`（獨立 listener）。
2. ✅ 對 `127.0.0.1:80` 直接跑黃金協定（腳本 `%TEMP%\opencode\golden80.py`）：
   raw `CHECK_SERVER_VERSION` → `OK QCV8001 \0`（12 B）；framed `GET_CONFIG_QCTEK` →
   27 B 挑戰；同連線 `QCV8001_AUTH_SENDCODE` → `NEMO 2040`＋2040 B
   （本機 7 頻道：`24 + 7×288`，不是真機的 312）；`GET_IOCONFIG` → `NEMO 280`＋280 B；
   同一個埠 `GET /` → `HTTP/1.1 200 OK`（HTTP 經多工器轉發）。
3. ✅ QCC 端重試「連線」→ 上線、頻道數與名稱正確（2026-11-08 23:14 自動重連：
   日誌見 `MUX：NEMO 連線來自`＋`GET_AUTH_USER` 挑戰→152 B）。

### 驗收：即時影像（代碼層完成、CI 綠，待 QCC 實機操作）

4. ✅ QCC 開即時影像 → 通道完整運行：`LIVE：即時影像通道開始`、
   `LIVE：頻道 0（主碼流）開始輸出`、ffmpeg 來源啟動，keepalive 與 FRAM 輸出
   持續到操作員關閉（10.4 秒全程無斷線）。
   - **根因（已修）**：一條 NEMO 命令寫三次同步日誌（開檔/寫/關，過防毒），回應被
     拖到 20–90ms；QCC 的狀態機以 33ms 為週期，回應跨過一個週期它就在授權成功後
     0.6ms 發 FIN、從不送 keepalive（真機 <1ms、每次成功）。修法：NEMO 處理改
     **專用執行緒＋同步 I/O**（真機的核心模型：每連線一條同步迴圈）、診斷日誌改
     **有界佇列背景泵**、執行緒池下限一次抬足（SetMinThreads 96）。
5. ⏳ 畫面驗證（操作員遠端遙控暫時看不到 QCC 畫面，恢復後確認）。
6. ⏳ 關掉 QCC 的即時影像 → 日誌出現 `LIVE：即時影像通道結束`，
   ffmpeg 行程數回到基準（`Get-Process ffmpeg`）。
7. ✅ 狀態碼對齊真機四態判定樹（FUN_18001eb80）：0x8003 無權限→0x8002 攝影機
   資訊無效→0x8004 proxy 未建立→0x8001 等幀。相容層不做使用者級頻道權限
   （GET_AUTH_USER 回全權限），0x8003 不會發生；腳本實測 0x8001→0x8004 序列
   （來源建立後死亡、重試中）。另回歸真機值：GET_AUTH_USER 位移 20 恆為 36
   （機種容量，不是實際頻道數——回實際頻道數會讓 QCC 授權後關線）。
8. ⏳ `/QCCamList.bin`（RMCL，284B/頻道）與 `/QCUserList.bin`（RMUL，552B/使用者）
   的格式與驗證邏輯已從 QCC 端（QC_rsClient::CheckCameraList）還原，但**記錄欄位
   佈局需要真機 HTTP 樣本**（QCC 連真機時抓包）——操作員能操作 QCC 後補。
   現況：HeliVMS 對這兩個路徑回 SPA（QCC 的驗證會失敗，但 live 不依賴它們——
   live 走 NEMO）。

### 下一階段藍圖：QCC 的遠端回放協定（dvrProxy_pb）

QCC 的「遠端回放」也走同一條 NEMO 通道（`dvrProxy_pb`，與 live 的 `dvrProxy_live`
同構：挑戰授權→命令→幀流）。命令集已從反編譯還原（`QC_ipcam_aver` 的
`dvrProxy_pb::*`），**線路格式待真機抓包驗證後實作**：

| 命令 | 格式（反編譯還原） | 用途 |
|---|---|---|
| `GET_MONTH_DATE %u %u %u %u %u %u` | 月曆查詢 | 哪些天有錄影 |
| `CATALOG %u %u %u %d`（年 = tm_year+1900） | 目錄查詢 | 某天的錄影段目錄 |
| `OPENFILE %u %u %u %u %u %s %s QCV8001_AUTH` | 開檔 | 頻道＋時間＋帳密＋挑戰 |
| `OPENSECOND …`（同命令變體，旗標 0x94 bit24） | 按秒開檔 | |
| `SEEKFILE %d` | 跳轉 | |
| `CLOSEFILE` | 結束 | |

**回應格式（QCC 端解析器還原）**：

| 命令 | 回應 |
|---|---|
| `GET_MONTH_DATE` | **NEMO 框**，payload 0x40：`"OK"`＋位移 0x10 起 48 bytes 月曆條件；非 OK 回全 0 |
| `CATALOG` | **NEMO 框**，payload 0x314（788）：標頭約 20B＋32 天×24 小時矩陣（每 byte 一小時）；位移 0x14 起逐 byte 欄位 |
| `OPENFILE` | **raw**：`"OK <d1> <d2> <d3> <d4> \0"`——四個數字由 `parserFileInfo` 解析進 `__PB_FILE_INFO` |
| 幀流 | **與 live 完全相同的 FRAM**（回放直接重用 `dvrProxy_live::readQCV8001frame`） |

狀態機（`dvrProxy_pb::ProcessCallback`）：6＝月曆、7＝目錄、8＝開檔、9＝幀流；
每個查詢（月曆/目錄）都是「關舊檔→開新連線→發送→解析→關線」的短連線。
實作前必須抓「QCC ↔ 真機的回放抓包」雙重驗證——
live 的經驗：反編譯還原的格式，抓包會抓出反推不出的細節（挑戰長度、時戳順序、
回應延遲敏感度）。

### QCC 對裝置的完整命令清單與對齊完成度（從 QCC 端字串盤點）

| 命令 | HeliVMS | 備註 |
|---|---|---|
| CHECK_SERVER_VERSION、GET_CONFIG_QCTEK、GET_AUTH_USER、GET_NCHANNEL、GET_MAX_STREAM_NUM、GET_IOCONFIG、CONNECT、ALARM_*、CHECK_VNC_VERSION | ✅ | 黃金抓包對齊 |
| GET_LIVE_START | ✅ | FRAM 幀流（本文件上半） |
| GET_CATALOG／CATALOG、GET_MONTH_DATE、OPENFILE、OPENSECOND、SEEKFILE、CLOSEFILE | ⏳ | 回放協定（上表藍圖） |
| **GET_REC_DAYS** | ⏳ | **第三種協定**：`HR_MonthDays::SendRequest` 用「QC32」魔數框（+0x00 `"QC32"`、+0x04 總長 0xec、+0x08 `"GET_REC_DAYS"`、+0xdc 起月範圍；236B 固定結構、MonthDateCB 回呼）——與 NEMO/RMCL 並列，需另外還原回應 |

**GET_REC_DAYS 的回應格式（MonthDateCB 還原）**：分兩段到達——先讀滿 **0xec（236B）**
（結構原樣：QC32＋總長＋命令＋參數，結果欄位由裝置填），再讀 **0x28（40B）** 到結構的
+0x114；累積滿 0x28 後取 **+0x124 起 6 個 u32（24 bytes）** 回呼給上層（48 bytes 容器、
後半補零）——語意是**連續月份（月範圍查詢，跨度 ≤6）的「每天有無錄影」位圖**
（u32 的 bit d＝當月第 d 天）。資料開頭是 `"FAIL    "`（8 bytes）表示查詢失敗。
回應讀取中斷（不足 0x28）且非 FAIL 時靜默等待；完成或失敗都會關閉連線。
| GET_SDK_RECINFO | ⏳ | 錄影資訊 |
| GET_FACE_FINDER_CHANNEL、GET_OBJECT_COUNTING_CHANNEL | ⏳ | AI 分析頻道 |
| ALARM_MATRIX、ALARM_MSG、ALARM_RESET | ⏳ | 警報矩陣/訊息/復位 |
| OPEN_IO、CLOSE_IO、OPEN_POS、CLOSE_POS | ⏳ | IO/位置開關 |
| GET_PARAMETER、GET_IP_NUMBER、GET_PREV | ⏳ | 參數查詢 |

未實作命令的現況：`NemoSessionLoop` 不回應（留痕於日誌「未知命令」）——QCC 端會
逾時或重試，不會崩潰。每實作一個命令前，先挖 QCC 端的**回應解析器**（格式的權威）
並以真機抓包雙重驗證。

> **這一輪的根因（已修）**：`WebHost` 共用埠時送 `--urls=http://localhost:0`，
> Kestrel 直接擲 `InvalidOperationException: Dynamic port binding is not supported when
> binding to localhost`。那句話被啟動失敗處理吞掉，於是「Web 沒起來 → 公開埠沒人讓 →
> 多工器沒綁 → 只剩 5170」，QCC 連 Web 埠什麼都拿不到。現在網址由
> `HeliVMS.Shared.WebListenUrl` 產生（明確的 `127.0.0.1:0`），失敗會寫
> `%TEMP%\helivms-web.log`，協調者也把「Web 起不來」寫進 `helivms-qctek.log`。

---

## 環境狀態與路徑

### HeliVMS
- App：`D:\HeliVMS\src\HeliVMS.App\bin\Release\net10.0-windows\HeliVMS.App.exe`
  - `127.0.0.1:80`（web）＋`0.0.0.0:5170`（NEMO＋探測 HTTP）
  - **真機 QCV8001 也用 80**——兩者不能同時開（要測真機就先關 HeliVMS 的 Web 服務）
- 診斷日誌：`%TEMP%\helivms-qctek.log`（NEMO 命令＋`[WEB]` 探測，密碼遮蔽）
- 關機記錄：`%TEMP%\helivms-shutdown.log`（「收尾完成」＝正常關閉）
- 現況設定（實機 `index.db`）：`web.port=80`、`qctek.compat.port=5170`
  → 兩者不同沒關係：80 由多工器服務 QCC，5170 同時留給明確指定該埠的遠端 CMS。
  （修正前的版本要求兩個埠相同，那時的症狀是 QCC「裝置加入成功、連線中斷」。）

### QCC（使用者的）
- 日誌：`C:\QCTek\QCC\log\20261108.err`（**連線失敗原因看這裡**）
- 設定/裝置：`C:\QCTek\QCC\setting\`
- 從真機匯出的完整設定：`C:\QCTek\QCC\resource\CfgExport\127_0_0_1(80)\2026_11_08.cfg`
  （434 KB，後續功能的範本）

### 真機
- `C:\QCTek\QCV8001\bin_release\QCV8001.exe`（子行程 `QC_Live.exe` 擁有 80 埠）
- 首頁 `C:\QCTek\QCV8001\media\root.html`（445 bytes 黃金樣本）

### 工具
- tshark：`C:\Program Files\Wireshark\tshark.exe`；loopback 介面 `\Device\NPF_Loopback`
- 抓包：`%TEMP%\opencode\qcc-capture\`（`golden.pcapng`、`streams.txt`、`*.bin`）
- 反編譯：
  ```powershell
  $env:JAVA_HOME='C:\Tools\jdk-21.0.13+9'; $env:GHIDRA_INSTALL_DIR='C:\Tools\ghidra_12.1.4_PUBLIC';
  $env:REA_ANALYSIS_PROVIDER='ghidra';
  & 'C:\Tools\node-v24.22.2-win-x64\node.exe' 'C:\Tools\node-v24.22.2-win-x64\node_modules\rea-agents\scripts\rea.mjs' decompile-all '<dll>' --output '<out>' --format json
  ```
- 已反編譯：`D:\SDK\rea\QC_Server\decompiled`（伺服端派送器／回應函式）、
  `D:\SDK\QCC\qc_ipcam_aver_decompiled`（用戶端狀態機與 digest）

### Git
- 工作目錄：`Tools/` 底下剩的全是**未追蹤**的本地檔（含 `授權.rar`、`授權/`）——
  **不可動、不可提交**。repo 裡的 6 個 Tools 專案（BrandGen、HeliVMS.Decoder、
  HeliVMS.LicenseProducer、HeliVmsVerify、LicenseKeyGen、LicenseKeyGenUI）已由
  「Delete Tools directory」移除，連同依賴它們的 `HeliVMS.LicenseProducer.Tests`／
  `HeliVMS.ExportVerify.Tests` 一併下架（slnx 與 CI 的測試清單同步清理）；
  磁碟上殘留的 Tools 檔案讓本機建置一直綠、CI 卻紅（MSB3202）——「刪除類 commit
  的本地綠」不作數，要先看 `git status` 有沒有未追蹤殘留。
- 相容層本體在 `src/HeliVMS.Qctek`，已列入 `HeliVMS.slnx`（`SolutionCoverageTests` 會擋）

---

## 坑（不要重複）

- **測試逐專案跑**（全 solution 不可靠）；測試名用繁中句子；新守門測試要做「改壞→紅→還原」。
- **不要用 PowerShell 改原始碼**：`Set-Content -NoNewline` 會把 CJK 編碼弄壞（本輪就因此重建過
  兩個檔案）。用編輯工具改。
- **80 埠會被 HeliVMS App 佔用**；「連不上 127.0.0.1:80」先查 App 是否在跑。
- 使用者的「回報警報到 CMS」設定指到 `127.0.0.1:5170`（自己），每 5 秒一條 `ALARM_HELLO`
  是**預期噪音**（已建議關）。
- QCC 加入過的裝置（127.0.0.1:80）會留在它清單；重試「連線」即可，不必重新加入。
- **`NEMO` 標頭長度少宣告 1 是真機行為**，不是 bug；「修正」它會讓對端讀取器錯位。
- **多工器只綁 loopback**：在 80 上服務的是本機的 QCC。別台機器連 80 送 NEMO 會落到
  Kestrel（400 關線）——遠端 CMS 一律走 5170 的 `NemoServer`。
- **挑戰長度就是協定**：十位 Unix 秒換成九位亂數會讓「讀固定 27 bytes」的用戶端整條錯位
  （本輪真的重現過：測試卡在等挑戰直到逾時）。
