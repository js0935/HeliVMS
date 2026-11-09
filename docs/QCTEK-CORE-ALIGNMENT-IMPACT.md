# 對齊 QCV8001 的影響清單（給「重寫核心」這個選項）

> 目的：在你決定要不要真的把 HeliVMS 的核心改成跟 QCV8001 一致之前，先把
> **會被改到的檔案、會被弄壞的測試、以及 QCC 到底看不看得到** 列清楚。
> 本文件不含任何變更，只是決策依據。

---

## 1. 先看實測：QCC 對外只用了 6 個管理命令

`%TEMP%\helivms-qctek.log`（512 KB 滾動，涵蓋 11/08–11/09 所有 QCC 工作階段）
裡，QCC 送出過的 NEMO 命令去重後只有下表這些。**但這份普查一開始被污染了**：
其中三條（`ALARM_HELLO`／`ALARM_ISALIVE`／`ALARM_REQUEST`）不是 CMS 在管理
HeliVMS，而是 QCC 自己的「回報警報到 CMS」設定指到 `127.0.0.1:5170`（也就是
我們自己）之後的**外送**流量。把它們算進「QCC 需要的命令」會導致兩個錯誤結論：
以為要補心跳的回應（其實真機連一個位元組都不回），以及把命令數量高估 50%。

真正屬於「CMS → 裝置」的管理命令只有 6 條：

| 命令 | 次數 | 我方目前 | 備註 |
|---|---:|---|---|
| `GET_AUTH_USER` | 904 | 支援 | 回 152 bytes，位移 20 = 36 |
| `GET_CONFIG_QCTEK` | 903 | 支援 | 回 0x18 + n×0x120 |
| `GET_IOCONFIG` | 409 | 支援 | 回 280 bytes 全零 |
| `GET_LIVE_START` | 36 | 支援 | 全部是控制通道（QCC 端 `cfg=1`） |
| `CHECK_SERVER_VERSION` | 15 | 支援 | 回 `QCV8001` |
| `SEARCH_EVENT` | 2 | **未支援** | 二進位框（與 `GET_AUTH_USER` 同族） |

另外三條是 QCC 自己那一側的流量。線路上確實會到我們這裡，但**不需要（也不該）
補回應**：

| 命令 | 次數 | 性質 |
|---|---:|---|
| `ALARM_HELLO 1 =QCV8001` | 1341 | 每 5 秒一次的心跳；真機回 **0 bytes**（把 0x100 回應緩衝區首位寫 0），QCC 送出後只累加計數、從不讀回應 |
| `ALARM_ISALIVE` | 386 | 同族的存活回報（我方回 `ALIVE`，無害） |
| `ALARM_REQUEST` | 16 | 同族的警報回報（我方回 `OK 0 1`，無害） |

也就是說，**QCC 實際會碰的管理介面只有 6 個命令**，其中 5 個已經可用。
真機 QCV8001 安裝目錄有 289 個 exe/dll、110 個 `GET_/SET_/ALARM_/PTZ_/IO_` 類命令名，
但 QCC 只用得到上面那 6 個。

這個數字直接決定了「重寫核心」的成本效益：**要對齊的是 1 個命令 + 少數協定區塊，
不是一整套產品。**

---

## 2. 三層分工：哪一層該「跟 QCV8001 一致」

| 層 | 內容 | 要不要一致 | 現在的狀態 |
|---|---|---|---|
| A. 線路契約 | NEMO 命令、payload 版型、框法、狀態碼、時序、FRAM、回放 proxy、alarm、HTTP 端點 | **要** | 6 個管理命令中 5 個完成（`SEARCH_EVENT` 未做）；QCC 自己的心跳／警報回報 3 條**不需回應** |
| B. 裝置模型 | 36 通道視圖、能力位元、名稱、IO 端子、`GET_AUTH_USER` 位移 20 = 36、設定檔版型 | **要，但只在「回應內容」層** | 部分（36 已寫死、通道數仍回 7） |
| C. 產品核心 | RTSP 拉流、分段錄影、索引、保留策略、桌面 UI、資料庫 | **不要** | QCC 看不到 |

判斷依據：QCC 只能透過 A 看見 B，**永遠看不到 C**。C 的任何改動對 QCC 的
「有沒有畫面」是 0 收益，但對 HeliVMS 自己的 3,691 個測試方法是直接風險。

---

## 3. 若真的動核心：逐模組影響清單

測試方法數為 `[Fact]/[Theory]/[SkippableFact]/[WpfFact]` 實測計數。

| 模組 | 現況 | 「核心對齊」要改什麼 | 受影響測試 | QCC 看得到嗎 | 判斷 |
|---|---|---|---|---|---|
| `HeliVMS.Core`（6 檔） | 共用抽象 | 幾乎不用動 | – | 否 | 不需改 |
| `HeliVMS.Media`（12 檔） | RTSP → ffmpeg → H.264 | 只有「QCC 要聽聲音」時要加音訊框（G.711/AAC） | 98 | 部分（音訊） | 小幅新增，不必重寫 |
| `HeliVMS.Recording`（46 檔） | `seg-HHmmssfff.mp4` 分段、事件/排程/全時/手動 | QCV8001 的分段命名與目錄結構不同，但 **QCC 不看檔名**，只透過 `CATALOG`/`OPENFILE` 問 | 339 | 否（經相容層） | **不要改** |
| `HeliVMS.Storage`（154 檔） | `segments` 表、保留策略（保留 N GB）、磁碟匯入、小時合併 | 改成 QCV8001 的配額/槽位模型 | **1317** | 只有容量數字（`GET_HDD_*` 類） | **不要改** |
| `HeliVMS.Rtc`（17 檔） | WebRTC/WHEP | 無關 | 131 | 否 | 不需改 |
| `HeliVMS.Alarms`（53 檔） | 事件、規則、IO 聯動 | QCC 的 `ALARM_MATRIX`/`ALARM_MSG`/`ALARM_RESET` 映射 | 331 | 是（事件頁） | 映射，不重寫 |
| `HeliVMS.Devices`（24 檔） | ONVIF、RTSP、IO 端子 | 無關（QCC 不碰 ONVIF） | 245 | 否 | 不需改 |
| `HeliVMS.App`（231 檔） | WPF 桌面、頻道管理、回放 | 36 通道 UI、QCV8001 設定頁 | **561** | 否 | **不要改** |
| `HeliVMS.WebApi`（21 檔） | HeliVMS 自家 Web/API | QCC 的 `QCBackupSystem.bin`/`QCAmList.bin`/`QCUserList.bin` 端點 | 241 | 是（備份/清單） | 小幅新增 |
| `HeliVMS.Qctek`（29 檔） | NEMO/live/alarm/playback 相容層 | **主要工作都在這裡** | 134 | 全部 | 這才是要動的地方 |

**合計受「核心重寫」直接影響的測試：約 2,600 個（App 561 + Storage 1317 +
Recording 339 + Alarms 331 + Media 98 ≈ 2,646）**，而其中對 QCC 有可見收益的
只有 Alarms 的事件映射與 Media 的音訊。

---

## 4. 真正還缺的東西（依 QCC 實際會碰的順序）

| # | 缺口 | 影響 | 改哪裡 | 驗收（可觀測） |
|---|---|---|---|---|
| P0 | **QCC 端沒有把頻道放進格位** → 顯示通道 `Connect! 1` 從未建立 | **就是「連線但沒畫面」** | 不需要改程式；QCC 操作 + 確認 | `20261109.err` 出現 `Connect! 1`、`GET_LIVE_START cfg=0`；我方送出可解碼 Annex-B。**注意：11/09 之後的觀測被 QCC 每 66 秒自我重啟汙染（機器記憶體壓力，見 §4 註）** |
| ~~P1~~ | ~~`ALARM_HELLO` 未回應~~ → **已釐清：本來就不該回應，不是缺口** | 無（把心跳當缺口去補，反而會讓客戶端錯位一格） | `QctekCommandHandler.IsSilentHeartbeat`＋`NemoSessionLoop` 只記第一次 | 心跳不再被記成「未知命令」，也不再擠掉 512 KB 診斷日誌（2026-11-10 完成，134 綠） |
| P2 | `SEARCH_EVENT`（二進位框）未回應 | QCC 事件搜尋頁空白 | `QctekCommandHandler` + 新 payload | QCC 事件頁列出既有事件 |
| P3 | 裝置模型：通道數回 7（真機 36）、`SET_CONFIG_QCTEK` 未實作 | QCC 設定頁可能顯示不完整；從 QCC 改設定會遺失 | `QctekPayloads` + `DeviceContext` | QCC 設定頁可讀可存且不報錯 |
| P4 | 回放真機驗證：`OPENFILE` 參數語意、`SEEKFILE` 秒數、ffmpeg 少了 `-re` | 回放可能全速灌幀或跳錯位置 | `QctekPlayback*` | QCC 回放 1× 正常、拖動時間軸可跳 |
| P5 | 選配：音訊、PTZ、`QCBackupSystem.bin`、`QCUserList.bin`、`QCAmList.bin` | 對應功能在 QCC 不可用 | `HeliVMS.Qctek` + `WebApi` | 逐項手動驗收 |

> P0 的證據：QC_Live 主視窗停在 `(-64000,-64000)`、所有頁面視窗
> `IsWindowVisible = False`、日誌每 30–40 秒一行 `Hide Monitor 16`。
> 11/08 同一份伺服器程式（核心未動）就出現過 8 次 `Connect! 1`，所以
> 協定路徑本身是通的。
>
> **11/09 之後的觀測不可直接採信**：QCC 外殼（`FUN_140001ef0`）每輪用
> `GlobalMemoryStatusEx` 檢查「可用分頁檔是否低於總量 10%」，在 Ghidra／OpenCode
> 等高 commit 程式同時執行時成立，於是 `PostMessage 0x801b`／`Stop NVR 3:
> ShellStartPage` 把整個 NVR 子行程重建——週期 58 秒＋8 秒拆除＝**66 秒**。
> 那不是我們的程式，也不是協定問題；做 P0 驗證前必須先確認機器 commit 壓力已解除，
> 判準是 `20261109.err` 不再出現 `Out of Memory!`／`Stop NVR 3: ShellStartPage`。
> 這也解釋了為何登錄檔實驗（`ImmedChannels`／`ImmedSelAll`）沒有訊號：實驗環境
> 每 66 秒被重置一次。

---

## 5. 成本對比

| 方案 | 工作量（估） | 風險 | 能解決「沒畫面」嗎 |
|---|---|---|---|
| P0 + P2（相容層收尾） | 2–3 人日 | 低（只動 `HeliVMS.Qctek`，134 個測試護著） | **能** |
| P3–P4（裝置模型 + 回放驗證） | 3–5 人日 | 中（payload 版型要靠抓包比對） | 能（回放、設定頁） |
| 核心重寫（Recording/Storage/App 對齊 QCV8001） | 20–40 人日 | **高**：2,600+ 測試、現場錄影/回放行為、`AGENTS.md` 的整串 landmine | **不能**（QCC 看不到） |

---

## 6. 不可跨越的界線

- 不可複製 QCV8001 的程式碼或資源（C++/MFC、第三方）；只能依反編譯出的
  **協定事實**重寫（`AGENTS.md`）。
- 相容層維持「選用、僅限 LAN」，文件中要說明認證限制。
- 每新增一個守門測試，必須先改壞程式碼確認會紅，再還原。

---

## 7. 建議

把「跟 QCV8001 一致」的範圍定義成 **A 層（線路契約）+ B 層（回應內容）**，
C 層（核心）不動：

1. 先做 P0（打通顯示通道）——這是唯一擋住「有畫面」的事，且與核心無關。
2. 再做 P2（`SEARCH_EVENT`），把 QCC 的事件搜尋頁補起來。原 P1（`ALARM_HELLO`）
   已釐清為「真機也不回應的心跳」，程式已改成一條都不回、日誌只記第一次。
3. 最後做 P3/P4，用黃金抓包逐欄位對齊裝置模型與回放語意。
4. 只有在某項功能「QCC 真的看得到、而我們的資料模型表達不了」時，才動核心；
   目前清單裡沒有這種項目。
