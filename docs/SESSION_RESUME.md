# HeliVMS 合作 Resume Card

> 用途：對話 context 過長時，開新 session 用呢份檔接手（唔靠舊對話記憶）。
> 權威：git／GitHub Actions（唔係 chat log）。所有里程碑已 commit＋push，CI 有據。

## 一句總結
HeliVMS 網絡影像監控系統（WPF App：即時監看／回放／AI 事件中心／錄影排程）。
里程碑 M1–M16 已全部 commit＋push、CI green（HEAD `0056ec5`）、Release build 0 error、
測試 69/69、**working tree CLEAN**（`git status --porcelain` 空白）。
M16＝回放窗當日事件明細列表（點擊跳播至事件時刻，AutomationId `PlaybackEventList`）。

## 開新 session 接手方法
```powershell
# 喺 D:\HeliVMS 開新 session 先講呢句：
git status                    # 預期 clean
git log --oneline -20         # 預期 M1..M16（HEAD=0056ec5）
```
新 session prompt 只需一句：「接 HeliVMS M16 尾，參考 docs\SESSION_RESUME.md，
繼續自主模式」。我（新 session）會照 git 現況接手，唔會估。

## 現況速覽（git 權威，非 chat 記憶）
- 最後 commit：`0056ec5`＝M16「回放窗當日事件明細（回放事件列表點擊跳播＋事件跳播時刻＋AutomationId PlaybackEventList 俾 harness）」
- 所有路線（M1..M16）依 docs\ARCHITECTURE.md 里程碑定序交付，docs roadmap 冇 M17+ 定義
- 分支/遠端：`git diff origin/HEAD` 為空（同步）
- CI：`gh run list -L 15` 全部 success；M16 run `conclusion=success`

## 架構關鍵（確切、權威）
- 解決方案：`D:\HeliVMS\HeliVMS.App\HeliVMS.App.csproj`（WPF）＋`HeliVMS.slnx`
- 儲存層：`SqliteStore`、`AlarmEventRepository`、`ChannelRepository`（真名，無漂移）
- PlaybackWindow：`PlaybackEventList`（AutomationId）、`OnEventSelected` handler、`ChannelRepository(_store).List()` camNames dictionary
- E2E harness：`C:\Users\JS\AppData\Local\Temp\opencode\e2e\x\Program.cs`（temp harness，**唔入 git**）
  - 已有 block：`playcheck/playmark/playbackcheck/playmark/plist?`——**plist 未落**（M16 E2E block）
  - 未來工作＝喺 harness 加 `args.Contains("plist")` block：開 `--playback` 回放窗→讀 `PlaybackEventList` item count>0→點擊事件列→驗證跳播至該事件時刻

## 未完成／下一步（依 git＋repo 判斷）
1. **M16 E2E harness `plist` block**（temp harness 唔入 repo，純驗證用）
2. 若要開 M17：先喺 docs roadmap 定義（現無定義），可議（例如事件中心卡片式檢視／回放鍵盤快捷）

## 會踩嘅坑（唔好再犯）
- 唔好用 bash byte-rewrite 喺 repo 源碼（會造成 UTF-8 漂移/mojibake）；改源碼用 edit/read tool
- grep tool 對 harness file 會出現「零結果」方晚期（authoritative 權威＝git show HEAD 對 repo 檔；harness 喺 temp 路徑，用完整絕對路徑 grep）
- 唔好估 repo identifier；以 git show/git diff 為準
