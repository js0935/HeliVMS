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

HeliVMS 這條相容層的音訊跟著影像的 ffmpeg 走（同一個行程的第二個 `-map 0:a:0`
輸出到 loopback UDP，**不另開 RTSP**——便宜攝影機只有 2–4 條連線，多一條會把畫面
擠掉）。只有「頻道設定的收音已開啟」且 ffprobe 探測到白名單內可轉碼音軌時才掛：
對沒有音軌的來源下 `-map 0:a:0` 會讓 ffmpeg 以「沒有任何輸出串流」直接結束，
整個頻道連畫面都沒有（加 `?` 也一樣）。有的頻道每 125ms 送一包 8 kHz 單聲道
µ-law（1000 bytes，與真機同節奏），沒有麥克風的頻道仍只送開流那一包近靜音，
讓「有音軌的裝置」對客戶端成立。`channels.listen_enabled` 預設 0，所以預設行為
與加入音訊前完全相同。

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
| 音訊 | 每 tick 1–2 包 µ-law | 收音開啟且有音軌：每 125ms 一包 1000-byte µ-law；否則只有開流一包近靜音 |
| 無影格時 | 從未發生（5 fps 持續有幀） | 每秒一個 8-byte 狀態幀（客戶端轉 type 7，`Put` 只收 type 1，等於忽略） |
| 時戳 | 寫出時刻 | 寫出時刻以牆鐘蓋章 |
| 指標槽 | 自己的緩衝位址 | 0 |

### 8.8 §5b 作廢

§5b 的「授權後關線＝根因」不成立：那條連線在真機上是**長時間成功串流**的
（stream 48 持續 54.6 秒／7.37 MB，stream 0 持續 114 秒／15.3 MB）。當時我們
的伺服器在授權後把連線交給別的元件（等待視窗），從客戶端看才是「被關線」。
現在連線建立後由同一個 `QctekLiveSession` 持有到對端關閉或逾時。

## 9. QCC 端的即時影像槽位：為什麼「連得上卻沒畫面」只能重啟 QCC（2026-11-14）

§8 證明我方在 NEMO 線路上的位元組與真機同構。但 11-14 全天，QCC 對我方只做了探測
（`GET_LIVE_START` 五次、每次 20 ms 內結束、從未送 keepalive），而它自己的 `.err`
在 06:33 之後以約 33 次/秒空轉。本節把 QCC 端的狀態機寫清楚，讓「是 QCC 卡住」
這句話有逐位元組依據，而不是推測。

### 9.1 三個 slot：一個探測、兩個真 live

QCC 每個裝置有三個獨立的 live 槽位，`FUN_180012d10(slotBase, slotIndex)` 每 30 ms
tick 一次：

| slot | 用途 | `FUN_180012d10` 的 cfg |
|---|---|---|
| 2 | 探測（連得上嗎／版本／頻道數） | 1 |
| 0 / 1 | 真的即時影像 | 0 |

`slotBase = *(this + 0x58) + slotIndex * 0x46278`；每個 slot 內：

| 位移（slot 相對） | 欄位 |
|---|---|
| `+0x26110` | `conn`（連線狀態） |
| `+0x26128` | `busy`（`LiveAutuor` 的互斥旗標） |
| `+0x26138` | `peer`（對端） |

`StartLive!` 只印在 `flagA(+0x461ba) == 1 && flagB(+0x461bc) >= 1` 的分支，也就是
「裝置已註冊且至少有一個可用頻道」；它代表 QCC 認為「該開畫面了」，**不代表任何
TCP 連線真的發生**。

### 9.2 `LiveAutuor` 唯一與「已註冊」相容的失敗點是 busy

`0x1800060f0`（`LiveAutuor`）先看 `flagA`；`flagA == 1` 時只剩一個地方會失敗：
`this+0x28`（＝slot 的 `busy`）為 1 → 直接返回，而且在 `0x1800060f0.c:26-32`
**完全靜默**（不寫 log、不改旗標）。其餘失敗點（裝置不存在、版本不支援、頻道 0）
都會推翻「裝置清單看得到」這個事實，與現場不符。

### 9.3 busy 誰會洩漏、誰才會清

- `FUN_180013430`（送 connect 的那個）在 `-1 < iVar4`（連線成功）時**直接 `return 1`**，
  沒有清 busy。
- 只有 `InitialProxy`（`0x180002240`）的兩條返回路徑會清 busy；`conn`／`peer` 也只在
  `InitialProxy` 裡被設定。

所以：**一次「connect 送出去了、但後續初始化沒走完」的失敗，會讓該 slot 的 busy 永久
停在 1**，之後 `LiveAutuor` 每次都靜默返回，`StartLive!` 卻照樣每 30 ms 印一次。
重開畫面、重新選頻道都不會清 busy；只有重建 slot（重啟 QCC 或移除裝置重新加入）才會。

### 9.4 11-14 的楔住時序（`C:\QCTek\QCC\log\20261114.err`，時間為 UTC）

| 時間 | 事件 |
|---|---|
| 06:20:12–06:21:16 | 對 slot 0 共 22 次 `Connect! 0, 1, 1, 1, 0, -10`；當時 `:80` 由 Kestrel 回 HTTP 400（相容服務尚未接管該埠） |
| 06:21:32 / 06:21:56 | `CQC_Device::QueryDeviceInformation fail` |
| 06:22:32 | `{Dbg} StartStream88 fail! 3, 0` |
| 06:33:14–06:33:42 | `StartLive! 0` 在 28 秒內 937 次 |
| 之後 | busy 卡在 1，約 33 次/秒空轉到當日結束 |

全天 `.err` **沒有任何** `Connect fail1/2/3/4`，所以 06:20–06:33 的 connect 路徑本身
是健康的（失敗發生在 connect 之後的初始化）；而同一個 06:33 區間，本地 pcap
（`qcc-live6.pcapng`，14:33 本地＝06:33 UTC）**沒有任何新增 SYN**——`StartLive!`
937 次、零 TCP 連線。兩者合起來就是 §9.3 的 busy 洩漏。

### 9.5 07:48–09:10 的成功不屬於我方

那段時間 `cfg=0` 的 live 成功，是**真機 QCV8001** 在回應，不是相容層。

### 9.12 用 Frida 直接驅動 `QC_Live.exe` slot 狀態：觸發順序與影像路徑真相（2026-11-21）

本輪第一次在不靠 GUI 拖曳的情況下，讓真實 QCC 對 `127.0.0.1:80` 送出 `cfg=0`、
HeliVMS 來源啟動並 **送了 8147 幀 FRAM、546 秒**。方法與結論都是現場反編譯 +
Frida 17（REA 內建）實測，記錄在此避免下次走回頭路。

**環境**：QCC live 客戶端在 `QC_Live.exe`（不是 `QCC.exe`）；QCC DLL 的 PE
preferred ImageBase 全是 `0x180000000`，所以 Ghidra 絕對址要換 RVA（RVA =
絕對址 − `0x180000000`），`modbus.dll` 恰在 `0x180000000` 不能用絕對址判模組。
Frida 17 移除 `Memory.readByteArray`／`Memory.readU32`，改用
`NativePointer.readByteArray()`／`.readU16()`；`Module.findExportByName`／
`Module.getExportByName` 都不存在，只剩 `Module.getGlobalExportByName('recv')`
（WS2_32 的 recv/send 是全域匯出）。

**slot 陣列與欄位**（device A 含 `127.0.0.1`）：slot0 base `0x204fc4e4478`、
stride `0x46278`，slot2（探測槽）＝base + `2*0x46278`。每個 slot：
`+0x26110` conn、`+0x26128` busy、`+0x26138` peer；`+0x461b8` state（S16）、
`+0x461ba` flagA（S16）、`+0x461bc` flagB（S16）、`+0x461be` flagC（byte）、
`+0x461e0` counter0、`+0x461e4` timer、`+0x461ec` 128-byte 頻道位圖（LSB-first）。
`FUN_180012d10(param_1=slot base, param_2=index, param_3=slot2 base)` 是 30ms tick；
`param_1[0x8c37]`（＝`+0x461b8`）就是 state。

**關鍵：觸發順序不能颠倒**（完整反編譯 `FUN_180012d10` 才看得出來）：
1. 先只寫 slot0 位圖 bit0=1（`+0x461ec`），**保持 flagA=0**、flagB=-10、flagC=0。
   位圖非空才不會被 `IsMemoryEmpty(+0x461ec,0x80)` 提前 return；`flagA != 1` 才走
   `else` 分支呼叫 `FUN_180013430` 建立 conn/peer。該分支的 `bVar7` 讀的是
   **slot2（探測槽）** 的 `flagA==1 && flagB>=1 && flagC==0`，所以要先把探測槽備好。
   同時寫位圖 **且** flagA=1 會直接跳到 `StartLive!`→`LiveAutuor`，但 conn/peer
   還沒建立（`LiveAutuor` 失敗會把 state 設回 0，造成 `StartLive!` 狂印）——這是
   一開始失敗的原因。
2. 等 `FUN_180013430` 命中、ret=1、conn/peer 從 0 變成非 0（實測
   `conn=2220140762456`、`peer=2218612408336`）之後，再讓 flagA=1 觸發
   `StartLive!`→`LiveAutuor`；QCC 自己會把 flagA/flagB 帶到 1。
3. `flagB` 是 **S16**：`-10=0xfff6`（U8 讀成 246），不能寫 U8；寫 U8 `1` 會讓
   short 變成 `0xff01=-255`。探測槽的 `flagC(+0x461be)` 必須是 0 才滿足 Connect
   的 `bVar7`。

成功證據（兩端時間差 8 小時）：QCC `17:36:53 Connect! 0, 1, 1, 1, 0, -10` →
`ConnectReq OK!` → `StartLive! 0` → **`GET_LIVE_START fd=0, cfg=0`** →
`StartLiveReq 0, 0, 1`；HeliVMS `01:36:54` 挑战→SENDCODE→`OK`→來源啟動→
`LIVE：頻道 0（主碼流）開始輸出`；ffmpeg
`-i rtsp://.../live1s1.sdp -an -c:v copy -bsf:v dump_extra -f h264 -` 存活。

**影像路徑真相（本輪最重要的糾正）**：先前以為「QCC 收到 FRAM 但影像路徑沒啟動、
`GetFrame*` 全 0 = 沒收到影格」是 **hook 錯函式**。完整狀態機（§2）：
`peer+0xaa328==0 → this+0x2c=0x10（GetLive）→ readQCV8001frame 讀 FRAM`；
`!=0 → this+0x2c=5（GetFrame）→ GET_FRAME 文字協定（給 Aver IPC 用）`。
`GetFrameSend/GetFrame/GetFrameH264/GetFrameQCTekAudio` 全是 **Aver 模式**的函式，
不是 QCV8001。實測 hook `readQCV8001frame(0x180007920)`：回傳 `134/136/137/138`
（完整 FRAM 影格長度，對應 golden 的 137B P-frame）、`GetLive(0x180007b60)` 呼叫
550 次、`f328=0` 全程保持——**QCC 其實一直在成功讀取 HeliVMS 的 FRAM**。
`GetLive` 讀到正數走 default case，呼叫 `this+0x68` 的 frame callback 送影格到 UI。

**不要強制翻 `0xaa328=1`**：本輪做過實驗性翻轉，`GetFrameSend`/`GetFrame` 立刻從
0 升到 420/210，但 QCC 改送 **165-byte `GET_FRAME 0 0 0 MP w=2592 h=1944`**
（HeliVMS log：「非 keepalive 資料 165 bytes... h=1944」），HeliVMS 不認識這個
文字協定、當成非 keepalive resync，305 ticks 無 keepalive 後關線，QCC 則瘋狂
`dvr_proxy_live.cpp(L:291) program fail! 134/138`。也就是說 f328=1 是**錯誤模式**，
黃金抓包裡 f328 應維持 0，靠 keepalive→FRAM 推送鏈顯示影像。

**socket 層證據**：hook `ws2_32.recv` 觀察到 fd=4052（live socket，同時送 144B
keepalive）收到 `10715/10689/10671 bytes` 的關鍵影格（golden 第一包 10279B）。
所以 HeliVMS 推流、QCC 接收、QCC FRAM 解析三段都正常；剩下的未知數只在
`this+0x68` frame callback 之後的 UI 顯示層（QCC 自繪牆的 cell 指派），本輪因
Frida session 累積 21 個高頻 hook 後 `QC_Live.exe` 進程結束而未能走到那一步。
下次應：重啟 QCC → 只用最少 hook → 重做 slot 兩階段觸發 → hook `this+0x68`
的 callback 與 UI cell，確認影格真的被畫出來；不要把 f328 往上翻。

**同日稍後續做（這一步已走到，答案確定）**：上一輪的未知數已收斂。重新 attach
`QC_Live.exe`（本輪 PID 5772，`QC_IPCAM_Aver.dll` base `0x7ffe3da00000`，
slot A base `0x23d89d2b498`——注意這些是當次行程的堆積位址，重啟會變，必須重新
scan，不是硬編碼常數），把上面提到的 `this+0x68` 精確定為
`FUN_180013330`（RVA `0x13330`），x64 `__fastcall(uint channel, undefined8,
uint* frameType, longlong slotBase)`。它的守衛是
`frameType < 9 && ((0x126 >> frameType) & 1)`，有效型別恰為 `{1,2,5,8}`；
`frameType==1` 時寫時間戳 `slot+ch*0x80+0x261f0` 與 present byte `+0x26200`，
然後對 3 個註冊的 consumer callback 派發；frame callback 自己在
`FUN_1800136d0` 的 `StartLiveReq` case 被裝到 `slot+0x26168`（函式指標）與
`slot+0x26170`（slotBase）。先前誤以為第四個參數是 slot、第三個參數裡的
`0x89a5d440` 是 frame type，都是參數索引錯位：x64 下 `a[2]` 才是 `frameType*`。

精確 hook 量到：`frame_callback` 呼叫 225 次、`frameType` 分布 **100% = 1**（有效）、
`channel=0`、`a[2]=0x23d982b5358`（heap frame 物件，不是 slot）、
`slot=0x23d89d2b498`（正確）；**但 3 個 consumer callback 指標
`slot+ch*0x80+0x261c0/0x261c8/0x261d0` 全是 `0x0`**，派發計數 a/b/c 全 0。
也就是：影格到了、時間戳和 present 旗標也寫了，卻沒有任何顯示 consumer 被通知。

註冊點是 `QC_DVR_Aver::StartStream(this, device, callback, context, stream)`
（RVA `0x11b10`）：它在 `slot + stream*0x80 + 0x261c0` 寫 callback、
`+0x261d8` 寫 context、`+0x261b8` 寫 count、`+0x461ec` 設頻道位元；
`StopStream`（`0x11d10`）註銷。`StartStream` 經 vtable（RVA `0x1ac20`，
`StartStream` 槽 `0x1ac78`）間接呼叫，所以靜態 xref 只到 vtable 資料槽、找不到
真正的 caller。hook `StartStream`/`StopStream` 6 秒：**starts/stops 全空**——
顯示層 consumer 從未註冊。原因很明確：Frida 兩階段觸發只走了推流端
（`Connect`/`StartLive`/`GET_LIVE_START`），而 consumer 註冊是 UI cell 指派
（把攝影機拖到畫面格）才會呼叫 `StartStream` 的另一條路徑，這條路徑沒被走。

**決定性驗證**：在進程內 scan 出 `QC_DVR_Aver` 實例
`this=0x23d899f0a80`（其 `+0x58 = slot base`，vtable RVA `0x1ac20`，
與 `StartStream` 槽吻合），主動呼叫
`StartStream(this, 0, myCallback, 0x12345678, 0)`：`ok=1`、
`slot+0x261c0=0x23d97d40010`（非 0）、context `+0x261d8=0x12345678`、
count=1、bitmap=1。緊接著 FRAM **立即以約 15fps 派發到 myCallback**
（3 秒計數 45→89→136→181→226→271），`data` 是同一個 frame heap 物件、
`ctx=0x12345678` 正確。

**結論（寫在這裡避免下次重查）**：HeliVMS 推流 → QCC 接收 → FRAM 解析 →
frame callback → consumer 派發入口，**整條資料鏈完全通暢、端到端已證明**。
真機「連得上、收到 FRAM、卻沒畫面」的唯一原因是 **QCC 顯示層的 display
consumer callback 從未註冊**（`slot+0x261c0/8/d0` 全 0），而它只能由 QCC 自己的
UI cell 指派（`StartStream`）建立——這一步在禁止 GUI 操作的前提下無法安全完成，
且純屬 QCC 客戶端顯示層機制，**不是 HeliVMS 相容層的問題，不要改 HeliVMS 推流鏈**。
若要讓 QCC UI 真的畫出 HeliVMS 畫面，唯一途徑是讓操作員在 QCC 上把該攝影機
拖到一個畫面格（觸發 `StartStream` 註冊真實顯示回呼），而不是繼續改 slot 狀態或
翻 `f328`。技術陷阱：scan `*(this+0x58)==slotBase` 時，`0x23d89d2b498` 的正確
小端位元組是 `98 b4 d2 89 3d 02 00 00`，寫錯位移（`...2b...d8...`）會 0 match。

同一個陷阱會以另一種形式重複：QCC 的裝置 `127.0.0.1:80` 到底是誰在聽，取決於哪個
行程先綁到 80。這台機器同時安裝了 QCV8001（`C:\QCTek\QCV8001\bin_release`），它的
`QC_Live.exe` 會佔用 `0.0.0.0:80`；HeliVMS 沒在執行時（或晚一步啟動時），
`127.0.0.1:80` 就是真機，QCC 看到畫面完全正常。實際量到的一次（2026-11-14 18:0x
本地）：`Get-NetTCPConnection -LocalPort 80` 的擁有者是 `QC_Live.exe`（PID 16332，
路徑 `C:\QCTek\QCV8001\bin_release`），而 HeliVMS 沒有任何行程。

**判別方法**：`Get-NetTCPConnection -State Listen | ? LocalPort -eq 80` 看擁有者行程
路徑，或 `curl -sI http://127.0.0.1/DVR_Info.htm` 看 `Server:` 標頭——真機回
`QCV8001`，相容層回 HeliVMS 的 613-byte 頁面。

要在同一台機器上驗我們自己，`:80` 必須讓給相容層：先停止 QCV8001，或把 HeliVMS 的
Web 埠改成別的（例如 8080），並在 QCC 用 `127.0.0.1:8080` 新增裝置（QCC 的裝置埠
欄位就是 web 埠，相容服務會在同一個埠上多工）。

### 9.15 重啟後兩台裝置的 display consumer 都是空的（2026-11-21，Frida 唯讀掃描）

承接 §9.12 最後一個未解問題：既然 36002（真機）曾經能顯示畫面，它的
`QC_DVR_Aver` slot 裡應該留有真實的 display consumer callback，也許能讀出來複製到
36001。本輪用 REA 內建 Frida 17 attach 乾淨重啟後的 `QC_Live.exe`（pid 3092，
`QC_IPCAM_Aver.dll` base `0x7ffe42ad0000`），以 vtable RVA `0x1ac20` 掃出 **2 個**
`QC_DVR_Aver` 實例（對應 36001 與 36002 兩台裝置），逐 slot 唯讀取值：

| 實例 `this` | slot base | stream 0 callback (c0/c8/d0) | stream 1 callback | context | count | 128-byte 位圖 |
|---|---|---|---|---|---|---|
| `0x22030d1cfe0` | `0x22030ee2bd8` | 0 / 0 / 0 | 0 / 0 / 0 | `0` | 0 | 全 0 |
| `0x22030d1d280` | `0x22030fb5358` | 0 / 0 / 0 | 0 / 0 / 0 | `0` | 0 | 全 0 |

兩台裝置的 stream 0 都是 `flagA=0, flagB=-10, flagC=0`（閒置），沒有任何通道被指派。
重啟後連 36002 的 display consumer 都為 0，不是被覆蓋而是**根本不存在**。

**結論**：display consumer 不是隨裝置設定或 registry 持久化的狀態，而是操作員在
QCC live view 把攝影機拖到一個 cell 時，由 UI 當場呼叫 `StartStream` 建立的執行期
狀態；QCC 一重啟就全部消失。這同時解釋了為什麼多次重啟後 36002 也從 `cfg=0`
掉回只有 `cfg=1` 探測：沒有 cell 指派 → `+0x461ec` 位圖全空 → `FUN_180012d10`
在 `StartLive!` 之前就 return（§9.10 的守門）。因此「從 36002 複製真實 display
callback 到 36001」這條路**不存在**（沒有東西可複製）。§9.12 的端到端證明仍然成立：
HeliVMS 推流 → QCC 接收 → FRAM 解析 → frame callback → consumer 派發入口整條資料鏈
完全通暢，缺的 100% 是 QCC 客戶端顯示層的 cell 指派，純屬 QCC 機制，不是相容層缺陷。
在禁止 GUI 操作的前提下，HeliVMS 端沒有任何改動能讓 QCC 註冊這個 callback。

### 9.8 對照實驗：60.251.180.241 是一台真機（2026-11-14）

現場在 QCC 新增了一台外部裝置 `60.251.180.241:80`，「有畫面」。它**不是**我們的
相容層：HTTP 回應標頭是 `Server: QCV8001`、內容是本機 `DVR_Info.htm` 那一頁
（`Smart Q Information`），而本機對外 IP 是 `49.216.90.186`（`curl https://api.ipify.org`）。

它之所以有用，是因為它是一組現成的成功對照：

```
10:02:21 QC_DVR_Aver::Connect OK! 60.251.180.241, 80
10:02:22 ReadDVR::TimerNotify StartLive! 2   + GET_LIVE_START fd=0, cfg=1   ← 探測
10:02:24 ReadDVR::TimerNotify StartLive! 1   + GET_LIVE_START fd=0, cfg=0   ← 真 live
10:04:02 ReadDVR::TimerNotify StartLive! 0   + GET_LIVE_START fd=1, cfg=0
10:04:21 read fail fd=1, ret=-2                                            ← 19 秒後結束
```

由此可確定：QCC 的 live 管線（探測→真 live→FRAM）本身沒有問題，slot 是**每個裝置
各自一套**，一台卡住不會影響另一台。所以「對我方沒有畫面」永遠是那台裝置自己的
連線／slot 狀態，而不是 QCC 全域故障；反之，看到別的裝置有畫面也不能當成我方
相容層可用的證據。


### 9.6 現場處置

1. 確認 QCC 連的是哪一個埠（預設是 Web 埠；相容服務啟用且同埠時由 `QctekPortMux` 接手）。
2. **啟用或變更埠之後，重啟 QCC，並把裝置移除後重新加入**——這是唯一能清掉 busy 的方法。
3. 只重開畫面、只重新選頻道、只重新整理裝置清單都**不會**恢復。
4. 設定中心「QCTek CMS 相容」的說明文字已寫入同一句處置（三語）。

### 9.7 `%TEMP%\helivms-qctek.log` 的結論行怎麼讀

每次 LIVE 連線結束都會寫一行：

```
LIVE：即時影像通道結束（<對端>）：已訂閱/未訂閱、影像 N 幀、狀態 N 幀、音訊 N 幀、X.X 秒
```

- `未訂閱`／秒級長度：QCC 的**探測**連線（cfg=1）。下一行會寫「判定為探測連線…」，
  直接點名要重啟 QCC 或移除裝置重新加入。
- `已訂閱`但 `影像 0 幀`：問題在我們這一側（頻道位圖或即時影像來源），log 會指出來。
- `已訂閱`且影像幀數持續增加：這條連線是正常的；QCC 若仍黑屏，回到 §9.1–9.3 檢查
  它的 slot 狀態，不要改我們的推流。

音訊另外兩行（只在收音開啟的頻道出現）：

- `QCTEK live：頻道 N 音訊探測：<codec>，會送出聲音` 或
  `…已開啟收音但沒有可轉譯的音軌（<codec>），不送聲音`（每次真正探測一行，
  10 分鐘快取）。後者代表麥克風沒有音軌、編碼不在白名單，或探測失敗（上一行有
  原因）；前者卻仍聽不到，才輪到查 QCC 端的音訊播放。`音訊 N 幀` 為 1 且之後
  不再增加，就是「沒有麥克風」的正常形狀。
- `LIVE：頻道 N 的音訊積壓超過 4 幀，開始丟棄最舊的整幀`：tick 被拖住或對端太慢，
  聲音會斷斷續續（只記第一次，不洗版）。

### 9.9 `Out of Memory!` 重啟迴圈會讓一切觀測失效——先清出記憶體（2026-11-14 深夜）

QCC.exe 的記憶體檢查（`FUN_140001ef0`，`GlobalMemoryStatusEx`）：**可用 page file
低於總量的 10% 就重啟整個 NVR**（`Out of Memory! (N) xxxMB, yyyyMB` → `Stop NVR 3`）。
11-14 白天 commit 停在 12.3–12.6 GB / 13.9 GB（可用 8–11.8%），QCC 每 40–66 秒死一次：
每條生命只有「cfg=1 探測 → +15 秒 cfg=0 嘗試 → +13 秒 read fail → OOM」，任何比這長的
流程（授權後的串流、解碼器激活）**根本沒有機會發生**。本節前半的「cfg=0 永遠連不到」
就是在這個迴圈裡量出來的。

處置：把分析用後端（Ghidra/REA 的 java，commit ~800 MB）關掉後 commit 降到 10.7 GB
（可用 23%），迴圈立即停止。**下一次觀測前先看 `%TEMP%\helivms-qctek.log`／QCC `.err`
有沒有新的 `Out of Memory!`**——有就先清記憶體，否則結論一律無效。

### 9.10 cfg=0 只為「有頻道被指派到監看牆」的裝置發動——「cfg=0 連不到」結案（2026-11-14 深夜）

記憶體健康後重啟 QCC 的乾淨週期（`.err` 14:12，本機 22:12）：

```
14:12:06  QC_DVR_Aver::Connect OK! 127.0.0.1, 80        ← 我們
14:12:06  QC_DVR_Aver::Connect OK! 60.251.180.241, 80   ← 真機
14:12:07  StartLive! 2 ×2 → GET_LIVE_START cfg=1 ×2      ← 兩台都只做探測
14:12:28  StartLive! 1 → GET_LIVE_START fd=0, cfg=0      ← 只有一台發動串流
（之後 3 分鐘以上：零 read fail、零 OOM、零 Proxy Shutdown——串流鏈存活）
```

`Get-NetTCPConnection` 40ms 輪詢（`tcpwatch5.txt`）：22:12:27.639 SynSent →
22:12:28.136 Established **到 `60.251.180.241:80`**——cfg=0 連的是「頻道有被放到牆上」
的那台。`127.0.0.1`（HeliVMS）**連 cfg=0 都沒發動**：`FUN_180012d10` 的非 2 路徑在
`StartLive!` 之前有閘門——

```c
// FUN_180012d10（param_2 != 2 的排程 tick 路徑）
if (IsMemoryEmpty((void *)((longlong)param_1 + 0x461ec), 0x80)) {
    return;   // 該裝置的 1024-bit 頻道位圖全空 → 連 StartLive! 都不印
}
```

`+0x461ec` 是 **QCC 自己 UI 的「這台裝置要推哪幾路」位圖**（keepalive 的 128-byte
位圖同源，見 §7 第 5 步）。QCC 的監看牆上只有真機的頻道，HeliVMS 的頻道一次都沒被
指派過——所以「cfg=0 永遠到不了 80 埠」**從來不是網路或伺服器的問題**，是 QCC 根本
沒有對我們發動串流。§9.4 的 busy 楔住是另一層（同樣發生在探測槽）。

**結案**：cfg=0 缺口的唯一剩餘動作是**操作員在 QCC 的 live view 把 HeliVMS 的頻道
拖上牆**。之後 QCC 會對 `127.0.0.1:80` 發動同一條 cfg=0 鏈（connect → GET_LIVE_START
→ 挑戰 → SENDCODE → OK → keepalive → FRAM），而這條鏈的伺服器端已在線上完整驗證
（§9.11）。

### 9.11 HeliVMS 部署版的完整 live 鏈線上驗證（2026-11-14 深夜）

對**正在執行的** HeliVMS（`127.0.0.1:80`，MUX 路徑）跑 §7 腳本的完整流程：

```
22:07:51.461  MUX：接受連線（127.0.0.1:55427）→ 分類 34 bytes
22:07:51.461  NEMO ← GET_LIVE_START admin QCV8001_AUTH → 27B 挑戰
22:07:53.466  NEMO ← QCV8001_AUTH_SENDCODE（32hex）→ "OK \0"
22:07:53.467  LIVE：即時影像通道開始（127.0.0.1:55427）
22:07:53.501  QCTEK live：來源已啟動（rtsp://…/live1s1.sdp，pid 16220）
22:07:53.507  LIVE：頻道 0（主碼流）開始輸出
22:08:01.548  LIVE：即時影像通道結束：已訂閱、影像 75 幀、狀態 3 幀、音訊 1 幀、8.1 秒
```

客戶端送出 0x90 keepalive（bit0=1）後 6 秒收到 19,325 bytes 的連續影格資料。
**伺服器端沒有剩餘工作**：HeliVMS ↔ QCC 之間未驗證的只剩「QCC 解碼器激活」
（§9.1–9.3 的 QCC 端變數）與操作員把頻道放上牆（§9.10）。

### 9.13 乾淨重啟後右鍵「監控設備 → 36001」仍不發動 live：再次確認是 QCC 上牆指派（2026-11-21）

先前對 `36001`（QCV8001 (127.0.0.1)）的測試都在 QCC 忙碌／slot 殘留狀態下做，無法排除
「busy 洩漏」（§9.4）的干擾。本輪先把 QCC/QC_Live 全部 kill，再依序啟動 HeliVMS.App
（PID 20500，聽 `52660,8612,5170,80`）與 QCC（PID 2312，`QC_CMS_Class` hwnd 3932646），
等 40 秒穩定後，用真實 Win32 選單手勢（右鍵 → `#32768` 選單 → 方向鍵 → Enter，
不是 `PostMessage(WM_COMMAND)`）點 `36001`。結果：

```
08:16:22  qcc-nav3 -TargetId 36001 → enter sent（選單 highlight 已停在 36001）
          QCC .err size 1147 → 1147（零新增）
          HeliVMS 只有週期性 ALARM_ISALIVE / GET_IOCONFIG(280B) /
          GET_CONFIG_QCTEK(2904B) / GET_AUTH_USER(152B)，無新 GET_LIVE_START
```

**但同一次乾淨啟動的開機探測確實打到了 HeliVMS**（`%TEMP%\helivms-qctek.log` 08:15:00）：

```
08:15:00.411 NEMO ← GET_LIVE_START admin QCV8001_AUTH
08:15:00.413 LIVE：即時影像通道開始（127.0.0.1:64196）
08:15:00.426 LIVE：對端關閉連線（127.0.0.1:64196）
08:15:00.427 LIVE：即時影像通道結束：未訂閱、影像 0 幀、狀態 0 幀、音訊 0 幀、0.0 秒
08:15:00.427 LIVE：判定為探測連線（未送出 keepalive，QCC 的 cfg=1）…
```

也就是說 `127.0.0.1` 這台裝置 **QCC 開機時會做 cfg=1 探測**（HeliVMS 正確辨識並關線），
但右鍵點它不會像 `36002`（真機 60.251.180.241）那樣再補一發 cfg=0 真 live。這與
§9.10 的閘門一致：`FUN_180012d10` 非 2 路徑在 `IsMemoryEmpty(slot+0x461ec, 0x80)`
為真時直接 return，該位圖就是「這台裝置要推哪幾路」的 UI 指派；`127.0.0.1` 沒有任何
頻道被拖上 QCC 監看牆，因此 `StartLive! 1` / cfg=0 永遠不發生。**不是 busy、不是
HeliVMS 回應錯誤，也無法靠改 HeliVMS 設定修正。**

**QCC 本地設定的交叉證據**（`C:\QCTek\QCC\setting\qc_setting.xls`，自建 OLE2+BIFF8
解析器讀出，SST 每筆是 `cch(u16)+grbit(u8)`、BOUNDSHEET 名稱是 `cch(u8)+grbit(u8)`）：

| Device list | B(IP) | C(port) | H(model) | I | J |
|---|---|---|---|---|---|
| 127.0.0.1 | 127.0.0.1 | 80 | QCV8001 | 5 | 10 |
| 60.251.180.241 | 60.251.180.241 | 80 | QCV8001 | 4 | 23 |

`J` 與裝置回報的頻道數吻合：HeliVMS 的 `GET_CONFIG_QCTEK` 回 2904 bytes
＝ `0x18 + 10*0x120`（10 頻道），真機設定匯出則是 23 路真實攝影機（名稱／MAC／IP）。
兩台裝置的 `I` 不同（5 vs 4），但都是同一型號 QCV8001；`Camera list` 工作表在
`qc_setting.xls` 中是空的——QCC 的牆面指派不在這份表，而在各裝置的設定與 UI 狀態。
HeliVMS 的 cfg 匯出（`resource\CfgExport\127_0_0_1(80)\2026_11_14.cfg`）裡 32 個
`eq__j0` 佔位名稱，正是「沒有操作員指派過攝影機」的形狀。

**結論（不要重查）**：乾淨重啟＋真實右鍵手勢仍無法讓 `36001` 對 HeliVMS 發動 live，
再次確認缺口在 QCC 客戶端的「攝影機拖上監看牆」指派，不是 HeliVMS 相容層；唯一的
下一步仍然是操作員在 QCC live view 把 HeliVMS 頻道拖上牆（§9.10、§9.12）。

### 9.14 程式化自動化「拖上牆」不可行：Picker 是空的（2026-11-21）

§9.13 留下的問題是：自主模式下能否用程式完成這個指派，而不要求操作員碰 GUI？
本輪逐層驗證，結論是**做不到**：

**(1) 影像牆沒有任何可程式化控制項。** UI Automation（ControlView）枚舉
`QC_CMS_Class`（hwnd 1770660）只得到右側控制panel按鈕（`即時監控` 33010、
`1分割`～`144分割` 33055–33064、`電子地圖` 33006 等）與一條捲軸；`EnumChildWindows`
同樣只在右側（邏輯座標 x 941–1129）看到 `Button`/`ScrollBar`。左側影像牆（x 0–940）
與攝影機樹完全沒有子視窗、沒有 UIA 元素——全是 `QC_CMS_Class` 自繪。因此沒有
「來源項目」與「目的格位」可供 SendInput 精確拖曳，只能靠螢幕座標盲拖；而本機
RDP 螢幕合成全黑、`PrintWindow` 只抓得到 GDI chrome、模型亦不能讀圖，座標無從取得。

**(2) 唯一的標準控制項是「選擇攝影機」對話框，但它是空的。** Win32 枚舉發現 QCC
預先建立一批隱藏的 `#32770` 對話框，其中 `選擇攝影機`（hwnd 7276042）內含標準
`SysTreeView32`（hwnd 8455692）。以 `ShowWindow(SW_SHOW)` 直接顯示後，UIA 的
TreeItem 數為 0、`TVM_GETCOUNT` 也回 0 —— 沒有任何可選的攝影機。這與
`qc_setting.xls` 的 `Camera list` 工作表空白（§9.13）互不矛盾：QCC 的牆面指派
不在這份表，但 picker 本身也沒有內容可供選取。

**(2a) 右側裝置面板的 6 項樹也是空的。** 即時監看右側（rect 999,88–1119,369）另有
一棵固定的 6 項 `SysTreeView32`（屬於 QC_Live，hwnd 每次重啟會變）。UIA 對它回報
TreeItem=0；改以 `VirtualAllocEx` 在 QC_Live 行程內配置 `TVITEM` 與文字緩衝，再用
`TVM_GETITEMW` 跨行程讀取，六個 root 項目的訊息皆成功（回傳非 0）但文字全是空字串
——這些是沒有內容的 place holder。因此即使改從右側裝置樹下手，也沒有 HeliVMS 頻道
可供程式化選取。這與 (2) 一起排除了「也許某棵標準控制項其實有頻道」的最後一個可能。

**(3) 真實選單手勢選 `36001` 是完全的 no-op。** 用已驗證的 Win32 選單導航
（右鍵 → `#32768` → 方向鍵 → Enter，不是 `PostMessage(WM_COMMAND)`）點
`監控設備 → QCV8001 (127.0.0.1)`（id 36001，state 0x0 = enabled）：highlight
正確停在 36001、Enter 已送出，但 picker 沒有變成可見、tree 仍是 0 項、QCC `.err`
零新增、HeliVMS 只有例行的 `GET_CONFIG_QCTEK`／`GET_AUTH_USER` 輪詢，**沒有任何
`GET_LIVE_START`**。對照 QCC 開機自己對 127.0.0.1 做的 cfg=1 探測仍正常
（§9.13），差別只在於沒有任何頻道被指派到牆。

**結論（不要再試自動化）**：自主模式下無法可靠完成 QCC 的「攝影機拖上牆」——沒有
UIA 控制項可操作、攝影機挑選器是空的、右鍵選裝置也不會開啟挑選器。這不是 HeliVMS
相容層的缺陷，改 HeliVMS 程式碼不會有任何幫助。唯一路徑仍是操作員在 QCC 先建立／
確認攝影機清單，再於 live view 把 HeliVMS 頻道拖到監看牆格位
（§9.10、§9.12、§9.13）。

### 9.16 `GET_CONFIG_QCTEK` 每頻道 record `+0x18` 的位元組保真（2026-11-21）

**新的現場事實**：操作員確認 QCC 連真實 `QCV8001`（`36002`）能正常顯示，連
HeliVMS（`36001`）不能。這推翻了 §9.13／§9.14 原先「只是 QCC 端 cell 指派、不是
HeliVMS 問題」的結案；以下只記錄有線路證據的差異，不把未證實的推測寫成結論。

**黃金抓包的獨立驗證**：用自建 pcapng parser（LINKTYPE_NULL／BSD loopback，不是
Ethernet）解析 `docs/qcc-qcv8001-golden.pcapng`，在單獨的 TCP payload 內找到 15 份
312-byte `QCTEK_RM` config（`0x18` header + 1 筆 `0x120` record），15 份**完全一致**：

```
 hdr: 51 43 54 45 4b 5f 52 4d 1c 3b 34 01 00 00 00 00 ff ff ff ff 01 00 00 00
 rec +0x18（絕對 0x30）: 02 00 00 00        ← 真機所有 15 份都是 2
 rec flags +0x00       : 01 00 01 01 01 01 00 01
```

目前仍連得上的真機（23 頻道）在同一欄也是 2；金抓包與目前真機在這一欄一致。
`tests/HeliVMS.Qctek.Tests/GoldenCapture.cs` 的 `ConfigPayloadBase64` 原本是
`…0x30 = 0`，也就是**測試常數遷就了 `QctekPayloads.Config()` 沒有寫這一欄**（回 0），
而不是照抄真機——已直接以程式從 pcap 抽取位元組寫回，round-trip 驗證
`written len 312 byte0x30 2 EQUAL pcap True`，不再手抄 base64。

**修正**：`QctekPayloads` 新增 `ConfigRecordStreamCount = 2` 與
`ConfigRecordStreamCountOffset = 0x18`，每筆 record 都寫入 u32（不是只寫第一筆）。
該欄語意未反編譯出來，不要當成頻道編號或旗標去「算」；照抄真機是唯一有證據的
選擇。新增護欄 `每筆頻道記錄的串流數欄都照抄真機為二()` 用絕對位移 `0x30 + i*0x120`
逐筆檢查（引用常數會讓錯的常數自己也通過；單頻道 golden 抓不到「只寫第一筆」）。
突變驗證：把常數改回 0 時，新護欄與既有 golden 逐位元組測試都在位移 48 變紅；
還原後 311 測試全綠、全方案 build 成功。

**線路驗證**：完整重啟 HeliVMS（PID 18508，聽 `54237,8612,5170,80`）與 QCC
（PID 148／QC_Live 2284）後，直接向 `127.0.0.1:80` 抓 `GET_CONFIG_QCTEK` 回傳
2904 bytes＝`0x18 + 10*0x120`，逐筆 `+0x18 u32=2`、`flags=01 00 01 01 01 01 00 01`
——與真機同欄一致。

**REA 只證明「欄位被讀取」，沒有證明 live 因果**：`QC_IPCAM_Aver.dll` 的
`dvrProxy::GetConfig_QCV8001`（`0x1800030d0`）呼叫 `GetConfig_QCV8001_Old`
（`0x180002f50`），後者把解析後 config 結構的每頻道 u32（`lVar2+0x28`，stride
`0x20`）抄進 QCC 內部 record（`puVar5 = lVar3+0xd2`，stride `0x120`）的 `+0x16`
（`*(puVar5 + 0x16) = *puVar6`），接著把名稱 `qc_wcscpy` 到同一 record 的 `+0x1a`
（`lVar3+0xec`）。注意內部 record 的位移與線路 record 差 2 bytes，不要照抄線路的
`+0x18`／`+0x1C`。所以該欄的確會被 QCC 保存，不是無用填充。但乾淨重啟後，不論
修正前後，QCC 對**兩台**裝置都只印 `cfg=1` 探測（log delta 1103 bytes，`StartLiveReq
0, 1, 1`），自動化情境沒有出現 `cfg=0`／`StartStream`——因此**不能宣稱這個修正已讓
live 畫面出現**，也不能排除黑畫面另有原因。真正的 live 閘門仍在 `FUN_180012d10` 的
`IsMemoryEmpty(+0x461ec, 0x80)`（§9.10、§9.15）；若要繼續查因果，需要能可靠觸發
QCC 對 HeliVMS 發出 `cfg=0`，或重新 attach Frida 追該 128-byte 位圖是誰填入的。

**不要改回去的事**：①不要把 golden 常數改回 `0x30 = 0` 去配合沒有寫入的實作——那是
把證據改成實作；②不要把 `+0x18` 猜成別的值（真機 15 份抓包與目前 23 頻道真機都為
2）；③本節只是位元組保真，未授權據此改編碼參數、推流鏈、WHEP 或保留策略。
