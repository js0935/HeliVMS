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

## 5a. 重新掛鉤（QCC 重啟後）

Frida hook 隨目標進程退出而失效。QCC 重新啟動後重掛：
把 REA MCP 的 `start_frida_session`（attach QC_Live.exe）＋
`load_frida_script`（本檔 §5 的腳本）再跑一次；或用操作員側的
`hook_qcc_live.py`（frida CLI 注入，輸出 `%TEMP%\qcc-live-trace.txt`）。

**進程優先序**：先找 `QC_Live.exe`（live 客戶端在這裡），
沒有才退回 `QCC.exe`（只有輪詢，會看到「QC_IPCAM_Aver.dll not loaded」）。

## 5b. 執行時鐵證：根因鏈完整定位（Frida，2026-11-12 晚）

操作員點擊 live 後（StartLive 被呼叫兩次），Frida 捕獲完整序列：

```
ProcessCallback state=3 type=0
StartLive_QCV8001 type=0
>> GET_LIVE_START admin QCV8001_AUTH\0        (raw 34B)
ProcessCallback state=3 type=2                  ← 同一事件又跑一次（type 殘值）
StartLive_QCV8001 type=2
<< 挑戰 27B
>> QCV8001_AUTH_SENDCODE <32hex>\0
ProcessCallback state=4 type=2
<< "OK \0" (4B)
AfterAuthorization resp="OK "                   ← 激活函數被調了！
C 127.0.0.1:80 fd=4140                          ← 新連線（但閒置、無流量）
```

實例字段轉儲（關鍵）：

```
dvrProxy_live this=0x1779f606408
  active(+0x78) = 1    ← 激活旗標已設（AfterAuthorization 成功）
  +0x60(NVR 旗標) = 1  ← LiveAutuor(param_2 = (NET_TYPE == 2)) = true
  fd(+0x4c)     = -1   ← 授權後立即被關閉！
  state(+0x2c)  = 0    ← 回到空閒
```

**根因鏈（100% 實鏈）**：dvrProxy_live::AfterAuthorization 的 "OK" 分支：
`this[0x60] != 0 → Close(fd)、fd = -1`。之後 ReadDVR 計時器的
`ReceiveFrame` 因 `fd = -1` 永遠失敗（條件 `-1 < fd`），
GetLiveSend（keepalive）永不執行，伺服器 305 tick 逾時斷線→黑屏。

**機制揭露**：QCC 的 `dvrProxy::ClientCallback(fd, obj, type)` 對每條 fd
**成對呼叫（type=0 與 type=2 各一次**，實錄反覆出現）——
所以 ProcessCallback 的 type 參數是殘值，StartLive_QCV8001 被調兩次
（type=0 發 GET_LIVE_START；type=2 讀挑戰送 SENDCODE）。
`this[0x60]` 由 LiveAutuor 的 param_2（交替的 0/2）決定，
實測值 = 1（type 2 語意：授權完成後關控制連線，
影像改走其他通道——真機上那條通道是什麼我們沒有抓包無法確定）。

**伺服器側無法單方修復**：不回挑戰→ QCC 把 "OK " 當挑戰解析，
摘要錯誤；回挑戰→ 授權成功但 +0x60=1 關線。
兩條路都死。**需要真機 QCV8001 的 live 抓包**（現場有真機）：
看真機對 type 2 連線授權後的下一步——伺服器在哪條連線、
以何格式開始送影格。拿到抓包即可補齊最後一塊。

> **（2026-11-14 更正）本節結論已作廢，見 §8。** 黃金抓包證明真機在同一條
> NEMO 連線上就完成握手與推流（授權後沒有任何「換通道」），當時的「關線」
> 是我們伺服器把連線交棒出去的行為。§8 另有完整 FRAM 規格、`type=2` 是音訊
> 而非參數幀、`+0x20` 是指標槽而非 FILETIME。

## 6. 不要做的事

- **不要把 GET_LIVE_START 改成 framed**：QCC 的 StartLive_QCV8001 type 0
  送的是 raw（無 NEMO 框）；黃金抓包的 framed 是 config 查詢，不是 live。
- **不要在 OK 之後主動送 128 bytes**：keepalive 的 128B 位圖是 QCC
  自己的 +0x461ec 緩衝（UI 勾選頻道），不是伺服器資料的回顯。
- **不要修「挑戰長度」**：27 bytes（16字元+空格+10位Unix秒）符合
  QCC 的 `< 0x21` 檢查；執行時實錄 QCC 照常完成 SENDCODE。
- **回放（dvrProxy_pb）的讀循環**用同一個 readQCV8001frame
  （ReadFileResponseQCV8001），FRAM 格式與 live 共用——live 通了回放同享。

## 7. 附錄：模擬客戶端腳本（端到端驗證用）

對運行中的 HeliVMS 重跑（PORT 改 80 即驗 MUX 路徑）：

\\python
# -*- coding: utf-8 -*-
"""模擬 QCC 的 live 客戶端（依 qc_ipcam_aver 反編譯的狀態機），對運行中的
HeliVMS 做端到端驗證：GET_LIVE_START → 挑戰 → SENDCODE → OK → keepalive → FRAM。"""
import io
import socket
import sys
import time

HOST = '127.0.0.1'
PORT = 5170          # 獨立 NEMO 埠（MUX 80 也可以）

out = io.StringIO()
def w(s):
    out.write(s + '\n')

def recv_until_nul_or_bytes(s, want=None, timeout=3.0):
    s.settimeout(timeout)
    buf = b''
    try:
        while True:
            chunk = s.recv(4096)
            if not chunk:
                break
            buf += chunk
            if want is not None and len(buf) >= want:
                break
            if b'\x00' in chunk:
                break
    except socket.timeout:
        pass
    return buf

t0 = time.time()
s = socket.create_connection((HOST, PORT), timeout=3)
w(f'[{time.time()-t0:.3f}] connected')

# 1. raw GET_LIVE_START（QCC type 0：無 NEMO 框、結尾 NUL）
cmd = b'GET_LIVE_START admin QCV8001_AUTH\x00'
s.sendall(cmd)
w(f'[{time.time()-t0:.3f}] -> {cmd!r}')

# 2. 讀挑戰（< 33 bytes；真機 27 bytes：16字符+空格+10位Unix秒）
challenge = recv_until_nul_or_bytes(s, timeout=2.0)
w(f'[{time.time()-t0:.3f}] <- challenge ({len(challenge)} bytes): {challenge!r}')

if len(challenge) == 0 or len(challenge) >= 0x21:
    w('!! 挑戰長度不合法（QCC 要求 < 0x21）——依反編譯 QCC 會靜默重試或斷線')
    s.close()
    sys.stdout.buffer.write(out.getvalue().encode('utf-8', errors='replace'))
    sys.exit(1)

# 3. 送認證碼（QCV8001_AUTH_SENDCODE <32hex>；摘要演算法不驗，形狀要對）
code = b'a' * 32
sendcode = b'QCV8001_AUTH_SENDCODE ' + code + b'\x00'
s.sendall(sendcode)
w(f'[{time.time()-t0:.3f}] -> {sendcode!r}')

# 4. 讀 OK（QCC 的 AfterAuthorization：回應開頭 = "OK" → 激活）
reply = recv_until_nul_or_bytes(s, timeout=2.0)
w(f'[{time.time()-t0:.3f}] <- reply ({len(reply)} bytes): {reply!r}')
ok = reply[:2] == b'OK'
w(f'[{time.time()-t0:.3f}] authorized = {ok}')

if not ok:
    s.close()
    sys.stdout.buffer.write(out.getvalue().encode('utf-8', errors='replace'))
    sys.exit(1)

# 5. 每 250ms 送 0x90 keepalive（QCTEK_RM + 01 00 00 01 + stream type + 128-byte 位圖）
#    依 GetLiveSend：+0x00 "QCTEK_RM"、+0x08 01 00 00 01、+0x0c u32 stream type(0=主碼流)、
#    +0x10 起 128 bytes 頻道位圖（bit i = 頻道 i，LSB-first）
ka = bytearray(0x90)
ka[0:8] = b'QCTEK_RM'
ka[8:12] = bytes([0x01, 0x00, 0x00, 0x01])
ka[12:16] = (0).to_bytes(4, 'little')      # stream type 0（主碼流）
ka[16] = 0x01                               # bit 0 = 頻道 0
# QCC 端 ks+0xaa334 初始 -250 → 第一次立即送；之後每 250ms
s.sendall(bytes(ka))
w(f'[{time.time()-t0:.3f}] -> keepalive #1 (0x90 bytes)')

# 6. 讀 FRAM（期待 SPS/PPS/IDR 或狀態幀）
s.settimeout(8.0)
total = b''
try:
    for _ in range(10):
        chunk = s.recv(65536)
        if not chunk:
            w(f'[{time.time()-t0:.3f}] connection closed by server')
            break
        total += chunk
        w(f'[{time.time()-t0:.3f}] <- {len(chunk)} bytes (head={chunk[:8].hex()})')
        if len(total) > 200000:
            break
except socket.timeout:
    w(f'[{time.time()-t0:.3f}] recv timeout')

w(f'total received: {len(total)} bytes')
if total[:4] == b'FRAM':
    w('FRAM received — LIVE 通道端到端成功')
else:
    w(f'no FRAM; head={total[:32].hex()}')
s.close()
sys.stdout.buffer.write(out.getvalue().encode('utf-8', errors='replace'))

## 8. 真機 QCV8001 如何把資料送到 QCC（2026-11-14 逐位元組解出）

> 本節取代 §5b。§5b 由 Frida 觀察到「授權成功後 fd 被關」而推論「真機把影像改走
> 另一條沒抓到的通道」；黃金抓包（`docs/qcc-qcv8001-golden.pcapng`）證明**同一條
> NEMO 連線**上就完成了握手與推流，關線是我們當時伺服器「等待視窗交棒」的行為，
> 不是真機的行為。

### 8.1 握手時序（stream 48／port 54079，逐包時戳）

| 相對時間 | 方向 | 內容 |
|---|---|---|
| 125.474972 | QCC → device | `GET_LIVE_START`（raw，無 NEMO 框） |
| 125.475108 | device → QCC | 27 bytes 挑戰（16 字元＋空白＋10 位 Unix 秒） |
| 125.475485 | QCC → device | `QCV8001_AUTH_SENDCODE <32hex>` |
| 125.475591 | device → QCC | `"OK \0"`（4 bytes） |
| 125.490129 | QCC → device | **第一個** 144-byte keepalive（`QCTEK_RM`，bit0＝頻道 0） |
| 125.520158 | device → QCC | **第一個** FRAM |

**因果方向是「QCC 先送 keepalive、裝置才推流」**，不是舊版假設的相反。授權完成到
第一個 keepalive 之間裝置一個位元組都不送；`QctekLiveSession` 現在就是這樣（第一個
keepalive 前零輸出）。反過來做過（舊版「連線即推流」）：QCC 收到 1064 bytes 後
3.5 ms 就以 RST 拒絕，連自己的 keepalive 都還沒送出。

keepalive 實測間隔 250–280 ms；內容 0x90 bytes＝`QCTEK_RM`＋`01 00 00 01`＋
u32 碼流（0＝主）＋128 bytes 頻道位圖（bit i＝頻道 i，LSB-first）。裝置只回位圖上
的頻道。

### 8.2 FRAM 幀格式

```
8 bytes  : "FRAM" | u16 channel(0-based) | u16 status
status≠0 : 只有這 8 bytes（狀態幀）
status=0 : 8 bytes ＋ 56 bytes media header ＋ payload
```

56-byte media header＝14 × u32 LE（與客戶端 `readQCV8001frame` 的讀取量算式一致）：

| offset | 欄位 | 實測值 |
|---|---|---|
| +0x00 | type | 1＝視訊、2＝音訊 |
| +0x04 | codec | 1＝H.264、200＝G.711 µ-law |
| +0x08 | 主體長度（video/audio） | 38647–54820（IDR）／13777–29788（P）／1000（音訊） |
| +0x0c | block1 長度 | 關鍵影格 118（SPS），其餘 0 |
| +0x10 | block2 長度 | 關鍵影格 8（PPS），其餘 0 |
| +0x14 | 微秒 | 當秒內微秒 |
| +0x18 | Unix 秒（u64） | 寫出時刻 |
| +0x1c | 0 | 秒的高 32 位 |
| +0x20/+0x28/+0x30 | block 指標槽 | 見 8.5 |

幀總長＝`8 + 0x38 + block1 + block2 + 主體`；payload 順序固定
**block1 → block2 → 主體**。關鍵影格的 block1 是 SPS
（`00 00 00 01 67 64 00 28 …`）、block2 是 PPS（`00 00 00 01 68 ee 3c b0`）、
主體是 IDR；非關鍵影格 block1＝block2＝0。

實測（stream 0：1475 幀／stream 48：709 幀，0 desync、0 leftover）：
影像 5 fps（200 ms/格）、GOP≈1 秒（stream 0 的 569 個影像幀中 114 個關鍵影格，
間隔固定 5 幀）、音訊約 8 包/秒、每 tick 1 影像＋1–2 音訊。stream 48 的前置
31 bytes 是「27 bytes 挑戰＋`OK \0`」，屬握手不屬 FRAM。

### 8.3 狀態幀與 `NO DATA`

真機在兩條成功串流的 2,184 個幀裡**一個狀態幀都沒送過**，也從未送 8-byte
`NO DATA`。客戶端兩種都認得：狀態幀會轉成 media type 7 並把狀態碼寫到
`media+0x28`；`NO DATA` 回 −200（清讀取緩衝、不算斷線）。所以這兩種都是
「客戶端容忍、真機在正常串流時不用」的東西。

### 8.4 type=2 是**音訊**，不是參數幀

客戶端 `dvrProxy_live::GetFrameQCTekAudio`：

```c
*(u32*)media     = 2;                        // type
*(u32*)(media+4) = *(u32*)(frame+0x20);      // codec（實測 200）
*(u32*)(media+8) = *(u32*)(frame+8);         // 長度
*(void**)(media+0x20) = frame+0x74;          // payload
```

真機 906 個（stream 0）／436 個（stream 48）type=2 幀的 payload 長度**全部**是
1000 bytes，整段 180 秒只出現 `0x7e`／`0xff`／`0xfe` 三種位元組——G.711 µ-law
的 −8／0／+8，也就是相機麥克風的近靜音。每個 tick 1–2 包、約 8 包/秒。
舊版「type=2 是叫醒預覽解析器的參數幀」**沒有任何證據**，已作廢。

HeliVMS 這條相容層沒有音訊來源（即時監看給的是 Annex-B H.264），所以每個頻道
開流時只送**一包**近靜音音訊，讓「有音軌的裝置」對客戶端成立；之後不再送。
真機每 tick 都送，但客戶端對音訊缺漏是容忍的（UDP 音訊本來就會丟包）。

### 8.5 `+0x20/+0x28/+0x30` 是指標槽，不是 FILETIME

| 證據 | 數值 |
|---|---|
| 關鍵影格 `+0x30 − +0x28` | 114/114 恰等於 block1 長度 118 |
| 關鍵影格三個高位 u32（`+0x24`/`+0x2c`/`+0x34`） | 114/114 全相同（stream 0＝370、stream 48＝627） |
| 非關鍵影格三個槽 | 全 0（455／217 幀） |
| 音訊幀 `+0x28` | 固定 1416（906/906、436/436），`+0x30`/`+0x34` 為 0 |

那是**真機自己行程裡的緩衝位址**（同一次執行內三個槽共用同一個高位），跨機器
沒有意義；客戶端 `readQCV8001frame` 也不讀它，而是用三個長度重算
（`0x40 ＋ block1 ＋ block2`）。所以我們一律寫 0。

> 舊 commit `3f7d028` 把關鍵影格指標的高位 `0x172` 誤讀成 FILETIME 的日期欄位，
> 於是每一幀都多寫了一份真機沒有的時間。已移除。

### 8.6 TCP 寫出形狀

真機每幀分多次小寫出（8＋56＋block1＋block2＋主體）；段長直方圖集中在
8／56／118／1000／14K／20K。這對接收端不是協議（TCP 是位元組流），只是提醒
「不要假設一次 `recv` 就是一個幀」——客戶端以長度累積湊幀。

### 8.7 HeliVMS 的實作差異（都是刻意）

| 項目 | 真機 | HeliVMS |
|---|---|---|
| 第一個 keepalive 前 | 零輸出 | 零輸出（已對齊） |
| 音訊 | 每 tick 1–2 包 µ-law | 開流時一包近靜音 |
| 無影格時 | 從未發生（5 fps 持續有幀） | 每秒一個 8-byte 狀態幀（客戶端轉 type 7，`Put` 只收 type 1，等於忽略） |
| 時戳 | 寫出時刻 | 寫出時刻以牆鐘蓋章 |
| 指標槽 | 自己的緩衝位址 | 0 |

### 8.8 §5b 作廢

§5b 的「授權後關線＝根因」不成立：那條連線在真機上是**長時間成功串流**的
（stream 48 持續 54.6 秒／7.37 MB，stream 0 持續 114 秒／15.3 MB）。當時我們
的伺服器在授權後把連線交給別的元件（等待視窗），從客戶端看才是「被關線」。
現在連線建立後由同一個 `QctekLiveSession` 持有到對端關閉或逾時。
\\n