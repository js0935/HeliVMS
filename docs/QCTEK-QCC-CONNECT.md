# QCTek QCC ↔ HeliVMS：裝置相容進度交接（2026-11-08）

> **新 session 接手用**：本文件＋`git` 紀錄＋磁碟上的抓包/日誌/反編譯即為完整上下文，
> 不必重讀舊對話。
> 目標：讓 **QCTek QCC（CMS）能連上並管理 HeliVMS**（把 HeliVMS 當成 QCV8001 裝置）。

---

## 一句話現況

**NEMO 控制協定已完整解出並實作**（`src/HeliVMS.Qctek/Nemo`），以 QCC ↔ 真機 QCV8001 的
黃金抓包逐位元組驗證（94 個測試）。
**剩下的唯一關卡是「80 埠」**：QCC 連的是裝置的 Web 埠，而真機把 HTTP 與 NEMO 多工在同一個
埠；HeliVMS 的 80 是純 Kestrel，所以 QCC 只會看到「連線失敗」。
下一步＝**80 埠前置多工器**（見文末設計）。

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

- 挑戰＝16 bytes 隨機字元＋空白＋10 位 Unix 時間（共 27 bytes），raw 送出、**沒有 NUL**。
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
| `src/HeliVMS.Qctek/Nemo/QctekReply.cs` | 四種具名框法：`Raw`／`RawExact`／`Framed`／`FramedWithNul`／`FramedBinary` |
| `src/HeliVMS.Qctek/Nemo/QctekPayloads.cs` | 三個二進位 payload 產生器（位移與長度照黃金） |
| `src/HeliVMS.Qctek/Nemo/QctekChallenge.cs` | 27 bytes 挑戰字串產生器 |
| `src/HeliVMS.Qctek/Nemo/NemoServer.cs` | TCP 迴圈；HTTP 探測與 NEMO 同一埠多工（5170 用） |
| `src/HeliVMS.Qctek/Nemo/QctekDiagnostics.cs` | 日誌摘要（密碼遮蔽、未知命令留痕） |
| `src/HeliVMS.App/Services/QctekCompatHost.cs` | 正式 `IQctekDeviceContext`（頻道名、使用者政策） |
| `src/HeliVMS.Qctek/Nemo/QctekPassword.cs` | 密碼欄位編解碼；**解碼容忍省略的 Base64 填補**（黃金證據：`YWRtaW4` 被真機接受） |

測試（`tests/HeliVMS.Qctek.Tests`，94 綠）：

- `GoldenCapture.cs`：每條連線的原始 hex＋payload
- `QctekPayloadsTests.cs`：與黃金 payload **整段位元組相等**、位移、超長名稱截斷
- `QctekSessionTests.cs`：重播 stream 0/2/4/6/16/18/24/25；未知／畸形 digest、半個訊框、
  開頭孤立 NUL、授權續用、過期待辦、不同步關線
- `NemoServerTests.cs`：真實 loopback 連線跑完整黃金對話（含宣告長度外的 NUL）
- `QctekCommandHandlerTests.cs`、`QctekPasswordTests.cs`、`QctekDiagnosticsTests.cs` 等

**改壞驗證（已做並還原）**：`ConfigNameOffset 0x1C→0x18` → 4 個測試紅；
`GET_IOCONFIG` 加入 `RequiresAuthorization` → 2 個紅；framed 警報成功改成 `FramedWithNul`
→ 2 個紅。順帶把 payload 測試的位移改成**黃金絕對值**（不再引用 `ConfigNameOffset`）——
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

## 下一步：80 埠多工（唯一的關卡）

真機的 `QC_Live.exe` 在 80 上分辨 NEMO／HTTP（`HttpServer::PreParseRequest`）；QCC 連的就是
裝置 Web 埠。`getparam.cgi` 的 `RemoteConsolePort` 導向 **已證實不可行**（真機也不靠它），
所以要在 80 埠做前置多工。

設計（未實作）：

1. `qctek.compat.port == web.port` 時才啟用前置器（不同埠則維持現狀：5170 直接服務）。
2. 前置 `TcpListener` 綁 **loopback**（保留 `WebHost` 刻意只聽本機的姿態；遠端 CMS 用
   5170 的 `NemoServer`，它已同時服務探測 HTTP）。
3. 連線進來先 `peek` 前幾個位元組：`NEMO ` 或可列印 ASCII 命令 → 本行程式內 `QctekSession`；
   HTTP 方法（`GET`／`POST`／…）→ 轉發到內部 Kestrel 的臨時埠。
4. `WebHost` 需支援「改聽內部埠」，並由 `MainWindow`／設定頁按順序啟動／停止（改埠要重啟
   前置器；設定變更立即生效）。

---

## 環境狀態與路徑

### HeliVMS
- App：`D:\HeliVMS\src\HeliVMS.App\bin\Release\net10.0-windows\HeliVMS.App.exe`
  - `127.0.0.1:80`（web）＋`0.0.0.0:5170`（NEMO＋探測 HTTP）
  - **真機 QCV8001 也用 80**——兩者不能同時開（要測真機就先關 HeliVMS 的 Web 服務）
- 診斷日誌：`%TEMP%\helivms-qctek.log`（NEMO 命令＋`[WEB]` 探測，密碼遮蔽）
- 關機記錄：`%TEMP%\helivms-shutdown.log`（「收尾完成」＝正常關閉）
- 現況設定（實機 `index.db`）：`web.port=80`、`qctek.compat.port=5170`

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
