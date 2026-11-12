# QCC live/playback 诊断（2026-11-12）

> 本輪由「QCC 連得上、設定查詢全通、但 live/playback 黑屏」出發，把 QCC 的
> live 客戶端**整個狀態機**從反編譯與**執行時觀察**兩端還原完畢。
> 伺服器端已被模擬客戶端端到端證明正常；等待操作員在 QCC 點擊 live view
> 觸發 live 通道，Frida hook 會記錄每一步。

## 1. QCC 的 live 客戶端在哪裡

**`QC_IPCAM_Aver.dll` 的 `dvrProxy_live` 類，運行在 `QC_Live.exe` 子進程**
（不是 QCC.exe 主進程——Frida 實測主進程沒載入這個 DLL）。

掃描 `C:\QCTek\QCC\bin_release`（ASCII 字串）：

| 檔案 | 命中字串 |
|---|---|
| QC_IPCAM_Aver.dll | QCTEK_RM、GET_LIVE_START、dvrProxy |
| QC_Server.dll | QCTEK_RM、GET_LIVE_START（伺服器端參照） |
| QC_Device_ID.dll | dvrProxy（基類） |
| QC_Network.dll | QCHTMLGET（HTTP 探測 UA） |

## 2. dvrProxy_live 的完整狀態機（反編譯還原）

```
dvrProxy_live 欄位：
  +0x10 網路層物件      +0x2c 授權狀態      +0x30 保存的授權狀態
  +0x34 讀法（0=raw、>0=NEMO框+長度）       +0x38 KEEP_CONNECT_SETTING(ks)
  +0x4c socket fd      +0x60 NVR旗標(type==2)  +0x78 激活旗標（keepalive 開關）

ProcessCallback(fd, type)——資料到達事件（QC_Client select → ClientCallback → 虛表[0]）：
  基類狀態 1   → GetVersion（CHECK_SERVER_VERSION）
  基類狀態 2   → GetConfig_QCV8001（QCTEK_RM 設定回應，ks+0x480d0 寫 "QCV8001"）
  基類狀態 4   → Authorization(fd, type)：
                   type!=2 → 關線報錯（QCV8001 一切走 type 2）
                   type==2 → this+0x34<1 用 raw Recv，否則 ReadNemoData；
                   讀到回應後調虛表[1] = AfterAuthorization(this, 回應)
  基類狀態 0x11 → GetPermitted_QCV（使用者權限；設 this+0x34 = 0x98）
  派生狀態 3   → StartLive（版本 "QCV8" → StartLive_QCV8001）
  派生狀態 0x10 → GetLive（readQCV8001frame 讀 FRAM 影格/狀態幀）
  派生狀態 5   → GetFrame（Aver IPC 的 GET_FRAME 文字協定）

StartLive_QCV8001(fd, type)：
  type 0：送 raw "GET_LIVE_START <user> QCV8001_AUTH"、ks+0xaa334=-250、this+0x34=0
  type 2：Recv 挑戰（<0x21=33B 才合法）→ BeforeQCV8001Authorization → 送
          raw "QCV8001_AUTH_SENDCODE <32hex>" → 保存狀態(this+0x30=3)、狀態 4

BeforeQCV8001Authorization：
  digest = MD5(CreatePassword(ks+0xa4) ++ ":" ++ user ++ …)，輸出 32 字元
  自訂字母表（0→a…5→f、6→0…15→9，與 QctekAuthDigest.Alphabet 一致——
  執行時實錄的認證碼 a127aec542cefd5cbeedd36fc82962c0 全落在該字母表）

dvrProxy_live::AfterAuthorization(回應)：
  this+0x30 != 3 → 轉基類（狀態 2 處理 QCTEK_RM config／0x11 處理使用者清單）
  this+0x30 == 3 → 跳過前導空白、取第一個 token；token == "OK" →
                   this[0x78] = 1（激活！）→ callback(0)

ReadDVR 33ms 計時器（QC_Live 內部）：
  this[0x78] != 0 → ReceiveFrame(128B) → ks[0xaa328]==0 → GetLiveSend(ks)

GetLiveSend（keepalive，每 clock()>249ms 送一次）：
  0x90 bytes = "QCTEK_RM" + 01 00 00 01 + u32 stream type(0=主/1=子)
             + 128 bytes 頻道位圖（bit i = 頻道 i，LSB-first；
               位圖是 QCC 自己維護的 +0x461ec 緩衝，UI 勾選頻道時 SetBit）
  送出後狀態 → 0x10（GetLive 讀 FRAM）

readQCV8001frame（live 影格讀取，type 7 狀態幀 = 8B "FRAM"+ch+status≠0；
  影格 = "FRAM"+status=0 + 0x38 media header + block1/2/video payload；
  "NO DATA" → -200 空閒；其他頭 → -100 斷線）
```

## 3. 伺服器端已被端到端證明（模擬客戶端）

`opencode` 暫存目錄的 `simqcc.py`（依反編譯行為寫的 Python 模擬）：
**5170（獨立 NEMO）與 80（QctekPortMux）兩條路徑全通**：

```
GET_LIVE_START → 挑戰(27B) → SENDCODE → "OK \0"(4B)
→ keepalive(0x90, bit0=頻道0) → FRAM 狀態幀(8B, status 0x0180)
→ FRAM 影格（10117B IDR + 137B P-frames 連續到來）
```

**結論：live/playback 黑屏的原因 100% 在 QCC 端（live 通道未被啟動），
不在 HeliVMS 的 NEMO/MUX/FRAM 伺服器。**

## 4. 08:37 會話的完整解讀（日誌陷阱）

`helivms-qctek.log` 每個命令出現**兩次相同文字**不是重發：
`QctekSession.CompleteAuthorization` 成功後記錄的是 **pending 命令名**
（原始命令），不是 `QCV8001_AUTH_SENDCODE` 幀本身。所以：

```
27.437 NEMO ← GET_LIVE_START …     ← 命令（無碼）
27.437 NEMO → 挑戰
27.438 NEMO ← GET_LIVE_START …     ← 其實是 SENDCODE 幀（記成 pending 命令名）
27.438 NEMO → OK                    ← 授權成功、Live 交棒
27.441 LIVE：即時影像通道開始
（之後 QCC 零字節 → 305 tick 逾時我們斷線）
```

**QCC 已完成授權但 10 秒內不送 keepalive**＝它的激活鏈
（OK → AfterAuthorization → this[0x78]=1 → ReadDVR 計時器 → GetLiveSend）
中間斷了。08:37 之後 QCC 也**沒有**重發 GET_LIVE_START——
說明它的 ReadDVR 重試循環（每 ~330ms）根本沒跑，不是「挑戰格式不對」。

## 5. 執行時觀察（Frida，已掛鉤待觸發）

- **QCC.exe 主進程**：沒有 QC_IPCAM_Aver.dll（live 不在這裡）。
- **QC_Live.exe（PID 6880）**：hook 已掛——AfterAuthorization、GetLiveSend、
  ReceiveFrame、StartLive_QCV8001、ProcessCallback、ws2_32 send/recv/connect。
- 已觀察到的輪詢循環（每 30-60 秒）：state 0xe → 0x12(IO) → 2(config) →
  4(SENCODE) → AfterAuthorization("QCTEK_RM…") → 0x11(permitted) → 4 → …
  **StartLive_QCV8001 從未被呼叫**＝live 通道等待 UI 觸發。
- dvrProxy_live 實例快照：`state=14、saved=17、+0x34=152(0x98)、fd=0、
  active(0x78)=1、ver="QCV8001"`。

**操作員點擊 live view 後**，從 `frida_session_status`（session
`864d06ed-d6ad-438f-88b2-035d5517f829`，QC_Live）讀 messages：
`[NET] >>/<<` 是與 HeliVMS 之間的原始字節，`[LIVE]` 是狀態機事件——
對照第 2 節即可定位斷點。

## 6. 不要做的事

- **不要把 GET_LIVE_START 改成 framed**：QCC 的 StartLive_QCV8001 type 0
  送的是 raw（無 NEMO 框）；黃金抓包的 framed 是 config 查詢，不是 live。
- **不要在 OK 之後主動送 128 bytes**：keepalive 的 128B 位圖是 QCC
  自己的 +0x461ec 緩衝（UI 勾選頻道），不是伺服器資料的回顯。
- **不要修「挑戰長度」**：27 bytes（16字元+空格+10位Unix秒）符合
  QCC 的 `< 0x21` 檢查；執行時實錄 QCC 照常完成 SENDCODE。
- **回放（dvrProxy_pb）的讀循環**用同一個 readQCV8001frame
  （ReadFileResponseQCV8001），FRAM 格式與 live 共用——live 通了回放同享。
