# HeliVMS 合作恢復卡（RESUME CARD）

> 用途：對話上下文過長、影響回覆速度時，開新 session 用這份檔案接手，**不依賴舊對話記憶**。
> 權威來源：`git` 與 GitHub Actions 的現況（不是聊天記錄）。
> 所有里程碑均已 commit＋push、CI 全綠、工作目錄乾淨（下方數據皆為驗證過的真實值）。

## 一句話總結
HeliVMS 為一套**網路影像監控系統**（WPF 桌面應用：即時監看／回放／AI 事件中心／錄影排程）。
里程碑 **M1 至 M16 已全數 commit＋push、CI 綠燈（BIG 尾巴**。最終 commit＝`456d20a`（M16 修正）、
`docs` 道路圖（roadmap）**沒有 M17+ 的定義**（M16 是行動計畫最後一篇）。
Release build 0 error、測試 **69/69 全過**、`git status --porcelain` **空白（工作目錄清乾淨）**、
本地與遠端完全同步（`git diff origin/HEAD` 為空）。

## 開新 session 的接手方法
```powershell
# 在 D:\HeliVMS 開新 session 時，先對新的 agent 講：
git status                    # 預期為 empty（乾淨）
git log --oneline -20         # 預期見到 M1..M16，HEAD＝456d20a
git diff origin/HEAD          # 預期為空（同步）
gh run list -L 3              # 預期全部 success
```
新 session 的 prompt 只需這一句：
「接續 HeliVMS M16 收尾，請先讀 `docs\SESSION_RESUME.md`，照指示以自主模式繼續。」
新 session 會以 git 現況接手，不會靠猜測。

## 現況快照（權威來源＝git，非聊天記憶）
- 最後 commit：`456d20a`＝**M16 修正**（事件明細欄寬 `Width="Auto"`）
  ——M16 原本 `Width="*"` 對 `GridViewColumn` 不合法，`--playback` 一開回放窗就
  XamlParseException 崩潰（69 個單元測試沒開視窗所以沒攔到）；E2E `plist` 實測抓到並已修。
- 前一個 M16 主交付＝`0056ec5`（回放事件列表點擊即跳播至事件時刻、AutomationId `PlaybackEventList`）
- 全部里程碑（M1..M16）依 `docs\ARCHITECTURE.md` 的里程碑定序交付；**roadmap 文件沒有 M17+ 定義**
- 分支／遠端：`git diff origin/HEAD` 為空（完全同步）；HEAD＝origin
- CI：`gh run list` 顯示 M16 run `conclusion=success`；建置 0 error、測試 69/69

## 架構關鍵（確切、無漂移）
- 解決方案：`D:\HeliVMS\HeliVMS.App\HeliVMS.App.csproj`（WPF）＋ `HeliVMS.slnx`
- 儲存層真名：`SqliteStore`、`AlarmEventRepository`、`ChannelRepository`（無漂移、無臆測）
- 回放視窗：`PlaybackWindow`，AutomationId `PlaybackEventList` 的事件明細列表，
  code-behind 的 `OnEventSelected` handler（SelectionChanged→向該事件所在 band 跳播至事件時刻）、
  `ChannelRepository(_store).List()` 建立 camNames 字典；事件單元在 UIA 樹是 **`ControlType.DataItem`**
- E2E harness：`C:\Users\JS\AppData\Local\Temp\opencode\e2e\x\Program.cs`
  （**temp harness，不入 git、不影響 CI、不影響 repo 狀態**）
  - 已有 block：`playcheck`（L73）、`playbackcheck`（L522）、`playmark`（L963）、**`plist`（M16，已驗證 PLIST_OK）**
  - 驗證結果：事件明細載入（DataItem count>0）→ 選首列 → `PlaybackTime` 顯示段內偏移 `00:00:02/00:00:08`＝跳播至事件時刻

## 尚未完成／下一步（依 git 與 repo 判斷）
1. **若要開 M17**：roadmap 目前無定義，需先在 `docs` 制定 M17 內容
   （候選：事件中心卡片式檢視／回放鍵盤快捷鍵⋯⋯），才可照「先定義、後實作」的節奏進行

## 已知雷區（勿再犯）
- **勿以 bash 對 repo 源碼做 byte 級重寫**（曾造成 UTF-8 漂移／mojibake 污染，已 `git restore` 還原）；
  改源碼一律用 read/edit tool
- grep tool 對 harness 檔案可能出現「零結果」（權威＝`git show HEAD` 對 repo 檔；
  harness 在 temp 路徑，請用**完整絕對路徑** grep）
- **勿臆測 repo 內的識別名稱**；以 `git show`／`git diff` 為準
- **回放窗 ComboBox 的 UIA**：下拉 item 的 `ListBoxItem.Current.Name` 是物件的 ToString（型別名），
  要以 item 內層 `ControlType.Text` 的 Name 來配「頻道 N」再對 `ListItem` 下
  `SelectionItemPattern.Select()`（對 Text 下 Select 會靜默失敗）
- **WPF GridView 事件列是 `ControlType.DataItem` 不是 `ListItem`**；`FindAll(ListItem)` 會拿到 0 筆
- **驗證跳播後 `PlaybackTime` 要輪詢**（ffmpeg 解碼有啟動延遲，且內建倍速預設 0.5×）；不要只等固定 1.5s
