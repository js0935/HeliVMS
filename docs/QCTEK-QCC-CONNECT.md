# QCTek QCC ↔ HeliVMS：裝置相容進度交接（2026-11-08）

> **新 session 接手用**：本文件＋`git`（commit `5b71370`）＋磁碟上的日誌/反編譯即為完整上下文，
> 不必重讀舊對話。
> 目標：讓 **QCTek QCC（CMS）能連上並管理 HeliVMS**（把 HeliVMS 當成 QCV8001 裝置）。

---

## 一句話現況

**型號識別已完成**（QCC 能把 HeliVMS 加入裝置清單）；
**按「連線」仍失敗**——QCC 連上 80 埠後要講 **NEMO 控制協定**（真機 QCV8001 是 HTTP＋NEMO **同一個埠多工**），
而 HeliVMS 的 80 是純 HTTP（Kestrel），不回 NEMO → 下一步＝**在 80 埠提供 NEMO 應答**。

---

## 證據鏈（全部為實測）

### 1. QCC 的裝置探測（已抓到）
80 埠上：`GET / HTTP/1.1` ＋ `Authorization: Basic YWRtaW46YWRtaW4=` ＋ **`User-Agent: QCHTMLGET 1.0`**
（來源：`%TEMP%\helivms-qctek.log` 的 `[WEB]` 行；QCC 以 `QCHTMLGET` 為請求代號）

### 2. 真機 QCV8001 對它的回應（逐位元組複製完成）
`HTTP/1.1 200` ＋ **`Server: QCV8001`** ＋ **`Connection: close`** ＋ `Content-Type: text/html` ＋ **`Content-Length: 445`**
body＝`media/root.html`（**UTF-8 BOM＋LF＋無尾換行**，445 bytes，`<title>QCV8001</title>`）
- 真機 `GET /DVR_Info.htm` → 有 `<ProductName>` 標籤的資訊頁
- 複製位置：`src/HeliVMS.Qctek/Nemo/QctekHttpResponder.cs`（`RootPage()`）；80 埠標頭在
  `src/HeliVMS.WebApi/QctekProbeMiddleware.cs`
- **實測結果：QCC 識別過關、能把 HeliVMS 加入清單**（commit `5b71370` 前後皆有效）

### 3. 「連線失敗」的鐵證（QCC 自己的日誌）
`C:\QCTek\QCC\log\20261108.err`（UTF-8 文字）：
```
{QCV} QC_DVR_Aver::Connect OK! 127.0.0.1, 80     ← TCP 連上 80 就報 OK
{QCV} ReadDVR::ControlRequest -13                ← 送出控制請求（QCC 內部狀態機）
CQC_Device::QueryDeviceInformation fail          ← 等不到能解析的 NEMO 回應
（每 4–12 秒重試一輪；QCC 畫面＝「連線失敗」）
```
- 承載者：**`QC_IPCAM_Aver.dll`** 的 `QC_DVR_Aver` 類（家族還有 `QC_Device.dll` 的 `CQC_Device` 基類）
- **真機行為**：HTTP 與 NEMO 在 **80 埠**多工（`HttpServer::PreParseRequest` 分辨兩者）——
  QCC 對真機也是這樣成功的。HeliVMS 目前 80 只有 HTTP。

---

## 已完成的修改（已 commit＋push：`5b71370`）

| 檔案 | 修改 |
|---|---|
| `src/HeliVMS.Qctek/Nemo/QctekHttpResponder.cs` | `RootPage()` 逐位元組對齊真機（BOM＋LF＋無尾換行＝445 bytes）；`Response()` 帶 `Server: QCV8001`＋`Connection: close` |
| `src/HeliVMS.WebApi/QctekProbeMiddleware.cs` | 80 埠回應補 `Server`／`Connection: close` 標頭；`[WEB]` 診斷改 bytes |
| `src/HeliVMS.Qctek/Nemo/NemoServer.cs` | HTTP 回應後**關閉連線**（真機 close 語意；404 同）；移除 keep-alive 推測 |
| `tests/…QctekHttpResponderTests.cs` | 新增「首頁與真機 root_html 逐位元組一致」（445/BOM/LF/尾端）＋Server/close 斷言 |
| `tests/…NemoServerTests.cs` | 兩個 HTTP 測試改為 close 語意；NEMO 改由**新連線**驗證 |
| `tests/…ApiTests.cs` | `Get_Root_WithBasicAuth` 加 445/BOM/`Server` 斷言 |

驗證：Qctek.Tests 62 綠、Api.Tests 相關 3 綠；**改壞驗證**（BOM 拿掉→紅；`Server` 改值→紅）已做並還原。
CI：`5b71370` 已 push（若 CI 未綠，先修 CI 再動新功能）。

---

## 反編譯情報（下一步的彈藥）

- 已反編譯：`D:\SDK\QCC\qc_ipcam_aver_decompiled`（**QC_IPCAM_Aver.dll**，561 函式，
  `decompile-index.json`＋`functions/*.c`）
- **框格式＝NEMO**（與 `src/HeliVMS.Qctek/Nemo/NemoMessage.cs` 已實作一致）：
  - `dvrProxy::AddNemoToHead`（0x180004d80）＝`sprintf("NEMO %d", len)`＋空白補到 16 bytes ←**證實**
  - `dvrProxy::ReadNemoData`（0x180005030）＝讀回應（Ghidra 反編譯失敗：Overlapping varnodes）
- **函式地圖（本輪新挖）**：
  - `QC_DVR_Aver::Connect`（0x180011070）→ 基類 `CQC_Device::Connect`＋開 timer
  - **`FUN_180012d10`（0x180012d10）**＝ReadDVR **timer 狀態機**（log「ReadDVR::ControlRequest %d」在 `case 2`；
    其中 `0x461ba`＝等待旗標、`0x461bc`＝重試倒數（負數慢慢 +1 回 0））
  - **`FUN_180013430`（0x180013430）＝`ReadDVR::ConnectReq`**（log「ReadDVR::Connect fail1–4」）——
    **真正的 NEMO 連線請求送出點，下一批優先讀**
  - `FUN_1800135e0`（0x1800135e0）＝接收/回應處理（log「ReadDVR::ReceiveFrame fail2」）
  - **認證流程群**：`dvrProxy::BeforeAuthorization`（0x180003d70）／`Authorization`（0x180003ed0）／
    `AfterAuthorization`（0x180003fe0）／`BeforeQCV8001Authorization`（0x180004820）／`ConvertAuthNV`（0x1800052d0）
  - `QC_DVR_Aver::QueryDeviceInformation`（0x180012070）→ 基類在 **QC_Device.dll（尚未反編譯）**
- **未做的反編譯**：`QC_Device.dll`（`CQC_Device::Connect/QueryDeviceInformation` 基類實作）

---

## 下一步（建議順序）

1. **抓包拿「ControlRequest → 後續對話」的黃金標準**（最快、最準；工具已裝好）：
   - 先啟動 App（下方），再執行 `C:\Users\JS\AppData\Local\Temp\opencode\qcc-capture\capture80.bat`
     （tshark 抓 loopback 80＋5170 埠 900 秒 → `golden.pcapng`）
   - 請使用者按 QCC 的「連線」→ 分析：`& 'C:\Program Files\Wireshark\tshark.exe' -r golden.pcapng -V`
   - 目標：**「ControlRequest」在線路上到底送什麼 bytes、QCC 期待什麼回應**
2. **讀 `0x180013430`（ReadDVR::ConnectReq）＋ 認證流程**（對照抓包）
3. **實作 80 埠的 NEMO 應答**——架構難點：80 目前是 Kestrel。
   選項：**(a) 連接層前置器**（自製 TcpListener 接管 80，peek 首位元組：
   `NEMO `/二進位框 → 自處理；HTTP（`G`/`P`/…）→ 轉發內部 Kestrel 埠）← **最貼近真機**；
   (b) 確認 QCC 是否能收 `getparam.cgi` 的 `RemoteConsolePort` 導向 5170（未證實，別先做）
4. 識別過關後續完整 NEMO 對話（login→QueryDeviceInformation→頻道→串流），
   5170 已有部分 NemoServer 實作可參考（`QCC完整對話整合測試`）

---

## 環境狀態與路徑

### HeliVMS
- App：`D:\HeliVMS\src\HeliVMS.App\bin\Release\net10.0-windows\HeliVMS.App.exe`
  - 啟動後 `127.0.0.1:80`（web）＋`0.0.0.0:5170`（NEMO）LISTENING；心跳 `C:\HeliVMSData\recording.heartbeat`
  - **真機 QCV8001 也用 80**——兩者不能同時開（要測真機就把 HeliVMS 的 Web 服務暫時關掉）
- 診斷日誌：`%TEMP%\helivms-qctek.log`（NEMO 命令＋`[WEB]` 探測，密碼遮蔽；可直接給工單）
- 關機記錄：`%TEMP%\helivms-shutdown.log`（「收尾完成」＝正常關閉；CrashDumps 空＝沒崩潰）

### QCC（使用者的）
- 日誌：`C:\QCTek\QCC\log\20261108.err`（**連線失敗原因看這裡**）
- 設定/裝置：`C:\QCTek\QCC\setting\`（`qc_setting.xls`、`user.bin`）
- **QCC 從裝置匯出的設定**：`C:\QCTek\QCC\resource\CfgExport\127_0_0_1(80)\2026_11_08.cfg`
  （434KB，11:01 從**真機**匯出成功——QCC 連上後拉的完整設定結構，實作後續功能時的範本）

### 真機
- `C:\QCTek\QCV8001\bin_release\QCV8001.exe`；首頁檔案 `C:\QCTek\QCV8001\media\root.html`（445 bytes 黃金樣本）

### 工具
- tshark：`C:\Program Files\Wireshark\tshark.exe`；loopback 介面 `\Device\NPF_Loopback`
- 抓包腳本：`C:\Users\JS\AppData\Local\Temp\opencode\qcc-capture\capture80.bat`（900 秒）
- 模擬 QCC 探測（Python）：`C:\Users\JS\AppData\Local\Temp\opencode\verify_qcc.py`
- 反編譯（REA＋Ghidra）：
  ```powershell
  $env:JAVA_HOME='C:\Tools\jdk-21.0.13+9'; $env:GHIDRA_INSTALL_DIR='C:\Tools\ghidra_12.1.4_PUBLIC';
  $env:REA_ANALYSIS_PROVIDER='ghidra';
  & 'C:\Tools\node-v24.22.2-win-x64\node.exe' 'C:\Tools\node-v24.22.2-win-x64\node_modules\rea-agents\scripts\rea.mjs' decompile-all '<dll>' --output '<out>' --format json
  ```
- 已反編譯的其他模組：`D:\SDK\QCC\{qc_network,qc_dvr,qc_center,nvrdialog}_decompiled`

### Git
- 最新：`5b71370`「QCTek 相容：裝置頁逐位元組對齊真機（445/BOM/Server/close）與 NEMO close 語意」
- 工作目錄：乾淨；**`Tools/授權.rar`、`Tools/授權/` 是使用者的未追蹤檔——不可動、不可提交**

---

## 坑（不要重複）

- **測試逐專案跑**（全 solution 不可靠）；測試名用繁中句子；新守門測試要做「改壞→紅→還原」。
- **80 埠會被 HeliVMS App 佔用**（dev 機常駐）；「連不上 127.0.0.1:80」先查 App 是否在跑。
- App 被「正常關閉」時 shutdown log 有「收尾完成」——不是崩潰；別被「處理程序不見了」誤導。
- 使用者的「回報警報到 CMS」設定指到 `127.0.0.1:5170`（自己），每 5 秒一條 `ALARM_HELLO` 是**預期噪音**
  （已建議關，未關不影響判讀 NEMO 以外的日誌行）。
- QCC 加入過的裝置（127.0.0.1:80）會留在它清單；重試「連線」即可，不必每次重新加入。
