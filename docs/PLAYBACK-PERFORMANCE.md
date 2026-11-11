# 多路回放的效能：業界做法、我們的基線、以及可量測的候選

> 觸發：現場回報「多路播放會不同步、會卡頓」，並問「改用 `D:\SDK\REA`（QCV8001）的播放方式
> 會不會更好」。這份文件先給事實與診斷順序，再列候選；**沒有量測數字支撐的改動不做**。

## 1. 業界的三條路線

| 路線 | 做法 | 代表 | 我們 |
|---|---|---|---|
| A. 每格獨立解碼＋獨立顯示 | 每一格一條解碼工作、各自貼到 UI | 多數桌面 VMS 客戶端（含我們） | **現在的做法** |
| B. 單一來源、多消費者 | 對來源只拉一次，重封裝（`-c copy`）給多個消費者；沒有客戶端就斷來源 | `go2rtc`（Frigate 的 restream 引擎）、MediaMTX | 同頻道跨窗已共享（`multicast` 幀）；跨節點走 HLS/NEMO |
| C. GPU 管線 | 硬解到 GPU 紋理（NV12）→ GPU 縮放＋色轉 → **GPU 合成整面牆** → 一次顯示 | NVR 一體機、專業 VMS（Hikvision iVMS 的技術支援甚至直接要使用者把解碼切成 D3D11/D3D9） | 未做（見 §3 風險） |

來源（2026-11 查）：

- go2rtc：<https://github.com/AlexxIT/go2rtc>（零依賴、多協定、單一來源多消費者；「沒有客戶端時斷開來源」）
- Frigate restream／MSE：<https://docs.frigate.video/configuration/restream/>
- Hikvision iVMS-4200 顯示問題的官方處置：<https://supportusa.hikvision.com/support/solutions/articles/17000129265>（要求切 D3D11/D3D9 解碼）
- D3D11 Video Processor 用於播放（縮放／色轉在 GPU 上）：MS Learn *D3D12/D3D11 video*、NVDEC Programming Guide
- **共享紋理的警訊**：Chromium issue 40397157（D3D9↔D3D11 共享紋理可能造成 CPU–GPU 往返，反而更慢）——
  WPF 的 `D3DImage` 正是 D3D9 介面，走它之前必須先量共享成本。

## 2. 我們的基線（都已量測，不是估的）

| 項目 | 數字 | 出處 |
|---|---|---|
| 1080p 解碼本身 | 17.8 ms/幀 | `docs/LIVEVIEW.md` §3.4 |
| 1080p ＋色轉 BGR24 | 28.7 ms/幀（**色轉佔 38%**） | 同上 |
| 640×360 解碼＋色轉 | 3.5 ms/幀 | 同上 |
| WPF 硬體繪製（Tier 2）6 格 640×360 @13fps | 25% 單核 | 同上 |
| WPF **軟體繪製** 同情境 | **98% 單核**（16 格 → 116%） | 同上 |
| 每格預算（16／36／64 格） | 480@10fps／320@6fps＋只解關鍵影格／256@4fps＋只解關鍵影格 | `PlaybackWallBudget` |
| 多路同步 | 容差 0.4s＋啟動延遲補償；36 格以上容差 30s（總覽） | `PlaybackSyncPlanner` |
| 丟幀 | 回放與即時都用**令牌桶**（`FrameRateGate`，burst 2） | `PlaybackSession`／`RtspClient` |

## 3. 為什麼不直接「改用 QCV8001 的播放方式」

1. **解碼層是同一套 FFmpeg**：REA 的 `MediaDecoder`／`QC_Codec` 用 avcodec／avformat 的 libav API
   （SBOM：`avcodec-58=Lavc58.91.100`＝4.3.1，含 CUDA/cuvid/nvdec 與 QSV 字串）；我們用同樣的
   FFmpeg，差別只在「in-process libav 還是子行程」。換過去換不到解碼時間。
2. **它真正的優勢是 GPU 管線＋DirectX 疊層**（`QC_Display` 走 DirectShow/DirectX，x86 另有
   `DVRDisplay` 的 DirectDraw）——**那是路線 C，不是「另一個 FFmpeg 用法」**。要拿到同樣好處，
   該做的是 C，而不是把播放器換成 MFC + in-process libav。
3. **代價**：in-process 解碼失去行程隔離（一條解碼崩潰＝整個 UI 掛掉；我們現在靠行程治理與
   `OrphanChildNames` 兜底）；DirectX 疊層要放棄 WPF 控件樹（多螢幕、無框、置頂、21 個視窗的
   契約測試全部重寫）；而且本機／現場存在**沒有可用 GPU** 的情境（`helivms-live.log` 的 `wpf=0`），
   純 GPU 路線在那些環境會直接失效，必須保留 CPU 後備。

## 4. 診斷先行：卡頓與不同步各有四種成因

`%TEMP%\helivms-playback.log` 每 5 秒一行（`PlaybackPaneDiagnosticLine`）已分開記錄：

| 症狀 | 日誌特徵 | 真正的原因 | 處置 |
|---|---|---|---|
| 每幾秒才換一張、格數 ≥36 | `kf` 標記、`tol=30` | **總覽模式的設計取捨**（只解關鍵影格） | 不是故障；要順就減格數 |
| 有些格一直落後 | `drift` 單調增大、`restarts` 少量 | 解碼跟不上（CPU 飽和） | 減格數、開硬解、或縮小每格解碼寬度 |
| 畫面一直閃／重來 | `restarts` 每 5 秒 > 2 | 同步反覆重啟（seek 慢、磁碟忙） | 見 §5 候選 1 |
| 全體一起鈍、CPU 沒滿 | `shown` 遠低於上限、`wpf=0`（即時那邊） | **WPF 在軟體繪製** | 換顯示方式（本機繪製／關遠端桌面省電），不是改解碼 |

> 先看日誌再動手：這四種的處置沒有一個是「換播放引擎」。

## 5. 可量測的候選（依風險排序）

| # | 候選 | 為什麼可能有效 | 怎麼量 | 風險 |
|---|---|---|---|---|
| 1 | 同步重啟的**滯後**（連續 N 次超容差才重啟） | `restarts` 高時每次重啟都是一次黑畫面，觀感就是卡頓 | 同一份日誌比 `restarts` 次數與 `drift` 分佈 | 低（純函式＋日誌） |
| 2 | UI 顯示目前**每格預算**（寬度／fps／是否總覽） | 「卡頓」有一半是誤判（36 格以上本來就慢） | 契約測試＋操作員回報 | 低 | **已做（2026-11-11，`PlaybackWallBudgetUiContractTests`；數字全取自 `PlaybackWallBudget`）** |
| 3 | GPU 管線（路線 C，**僅在偵測到可用 GPU 時啟用**） | 16 格以上時把縮放／色轉／合成搬到 GPU，CPU 可大幅下降 | 先在**有** GPU 的機器上用同一套協定量（6/16/36 格） | 高（新顯示層＋後備路徑） |
| 4 | 跨節點回放的片段快取（`RemoteCache` 等價物） | 遠端 seek 重抓 manifest／片段 | 量 seek 延遲與重抓次數 | 低中 |
| 5 | `D3DImage`（D3D9）貼圖替換 `WriteableBitmap` | 表面上是「零拷貝」 | **先量共享成本**（Chromium issue 的警訊） | 中（本機無 GPU 時不能驗） |

**不建議**：改成 in-process libav（失去隔離、換不到時間）、在沒有 GPU 的機器上強制 GPU 路徑、
把多路合成放進 WPF 的軟體繪製路徑（成本 ∝ 輸出像素×更新率，已量測）。
