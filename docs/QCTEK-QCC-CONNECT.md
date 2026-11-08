# QCTek QCC ↔ HeliVMS：裝置相容進度交接（2026-11-08）

> **新 session 接手用**：本文件＋`git` 紀錄＋磁碟上的抓包/日誌/反編譯即為完整上下文，
> 不必重讀舊對話。
> 目標：讓 **QCTek QCC（CMS）能連上並管理 HeliVMS**（把 HeliVMS 當成 QCV8001 裝置）。

---

## 一句話現況

**NEMO 控制協定已完整解出並實作**（`src/HeliVMS.Qctek/Nemo`），以 QCC ↔ 真機 QCV8001 的
黃金抓包逐位元組驗證。
**80 埠多工也已實作**：`qctek.compat.port == web.port` 時，公開埠由前置多工器
（`QctekPortMux`）持有，NEMO 在本行程應答、HTTP 原封不動轉給內部的 Kestrel 臨時埠
（與真機 `HttpServer::PreParseRequest` 同一個模型）。
下一步＝**在實機上讓 QCC 連 `127.0.0.1:80` 走完上線／頻道／警報**（見文末驗收清單）。

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
| `src/HeliVMS.Qctek/Nemo/QctekPortPlanner.cs` | 「要不要共用同一個埠」的純函式決策（兩邊都啟用且埠相同且埠合法） |
| `src/HeliVMS.Qctek/Nemo/QctekDiagnostics.cs` | 日誌摘要（密碼遮蔽、未知命令留痕） |
| `src/HeliVMS.App/Services/QctekCompatHost.cs` | 正式 `IQctekDeviceContext`（頻道名、使用者政策）；多工器／獨立伺服器兩種 `ApplyLocal` |
| `src/HeliVMS.App/Services/NetworkPortCoordinator.cs` | 兩個服務的啟停順序、失敗還原、兩條狀態文字（Web 與相容服務共用它） |
| `src/HeliVMS.App/Services/WebHost.cs` | 共用埠模式改聽 `http://localhost:0`，實際埠由 `IServerAddressesFeature` 讀回 |
| `src/HeliVMS.Qctek/Nemo/QctekPassword.cs` | 密碼欄位編解碼；**解碼容忍省略的 Base64 填補**（黃金證據：`YWRtaW4` 被真機接受） |

測試（`tests/HeliVMS.Qctek.Tests`，110 綠；`tests/HeliVMS.App.Tests` 768 綠）：

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

**改壞驗證（已做並還原）**：`ConfigNameOffset 0x1C→0x18` → 4 個測試紅；
`GET_IOCONFIG` 加入 `RequiresAuthorization` → 2 個紅；framed 警報成功改成 `FramedWithNul`
→ 2 個紅；多工器分類的大寫動詞改成回 HTTP → 2 個紅；協調者的兩個順序各自顛倒／拿掉
`StopMux()`／拿掉多工器失敗後的 Web 還原 → 對應契約測試各紅；`ResolveConsolePort` 忽略
公開埠設定 → 紅；挑戰尾端改回九位亂數 → 2 個紅（其中一個是「用戶端讀固定 27 bytes」卡住）。
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

1. **共用條件**：`QctekPortPlanner.ShouldSharePort`（純函式）＝兩邊都啟用、`web.port ==
   qctek.compat.port`、且埠在 1–65535。不同埠則維持原狀：5170 直接由 `NemoServer` 服務。
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

### 還沒做的驗收（實機）

1. 設定頁把 `qctek.compat.port` 改成與 `web.port` 相同 → 兩條狀態列都顯示執行中，
   瀏覽器仍打得開 `http://127.0.0.1:<web.port>/`。
2. QCC 對 `127.0.0.1:80` 按「連線」→ 上線、頻道數與名稱正確、警報訂閱不再 5 秒重連。
3. 若 QCC 仍失敗，先看 `%TEMP%\helivms-qctek.log` 有沒有 `MUX：NEMO 連線來自 …`
   （沒有＝QCC 連到別的地方，或公開埠不是多工器在聽）。

---

## 環境狀態與路徑

### HeliVMS
- App：`D:\HeliVMS\src\HeliVMS.App\bin\Release\net10.0-windows\HeliVMS.App.exe`
  - `127.0.0.1:80`（web）＋`0.0.0.0:5170`（NEMO＋探測 HTTP）
  - **真機 QCV8001 也用 80**——兩者不能同時開（要測真機就先關 HeliVMS 的 Web 服務）
- 診斷日誌：`%TEMP%\helivms-qctek.log`（NEMO 命令＋`[WEB]` 探測，密碼遮蔽）
- 關機記錄：`%TEMP%\helivms-shutdown.log`（「收尾完成」＝正常關閉）
- 現況設定（實機 `index.db`）：`web.port=80`、`qctek.compat.port=5170`
  → 目前**走不到多工器**（兩個埠不同）；要在 80 埠上服務 QCC，把 `qctek.compat.port`
  改成 80（或把 `web.port` 改成 5170，兩者相等即可）。

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
- 工作目錄：`Tools/授權.rar`、`Tools/授權/` 是使用者的未追蹤檔——**不可動、不可提交**
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
