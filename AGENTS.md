# AGENTS.md

Repo conventions for AI agents and new contributors. Read this before making changes.

## What this is

HeliVMS is a Windows NVR/VMS. Camera ingest is RTSP → ffmpeg → H.264 RTP → WebRTC
(WHEP) to browsers. Recordings go to segmented MP4.

- .NET 10 (`HeliVMS.slnx`), Node 24 for the web SPA only.
- The web SPA (`src/HeliVMS.Web`) has **zero runtime npm dependencies**. It is plain
  ES modules + vanilla JS. Do not introduce a framework or a CDN script — the page
  ships a strict `Content-Security-Policy` with `default-src 'self'`.
- SIPSorcery 10.0.16 is the WebRTC stack. `WhepPeer` is the only place SIPSorcery is
  constructed, deliberately, so negotiation failures have one origin.

## Build and test

Run from the repo root.

```
dotnet build HeliVMS.slnx -c Release --nologo
dotnet test  HeliVMS.slnx -c Release --no-build --nologo
cd src/HeliVMS.Web; npm ci; npm test
```

Notes that will save you time:

- **`dotnet test` on the whole solution is not reliable here.** `HeliVMS.Media` and
  `HeliVMS.Storage` tests create real files, and `HeliVMS.Rtc` spawns real ffmpeg
  processes. Under load they contend and intermittently fail. CI runs the test
  projects **one at a time**; do the same when a full-suite run fails, before
  concluding anything is broken.
- **`HeliVMS.App.Tests` does not compile `HeliVMS.App`.** Its contracts read the
  source as text, so a WPF-only error (`CS0104` on `Path` between `System.IO` and
  `Shapes`, a broken XAML binding) passes every App test and only shows up in
  `dotnet build HeliVMS.slnx`. Build the whole solution before calling desktop work
  done.
- **`MSB3027`/`MSB3021` file-in-use during build is the dev machine's running app,
  not your code.** A live `HeliVMS.App` process started from
  `src/HeliVMS.App/bin/Release/net10.0-windows` locks the copied DLLs, and the
  full-solution build fails only on the copy step. The operator has approved closing
  that process when it blocks a build (ask once, then just close it and rebuild);
  to verify compilation without touching it meanwhile, build with a redirected
  output path (`dotnet build src/HeliVMS.App -c Release -p:OutputPath=obj\verify-bin\`)
  and delete the folder afterwards.
- ffmpeg must be on `PATH`. Tests that need it are `[SkippableFact]` and self-skip
  via `PublishPipelineProbe`, so a green run without ffmpeg means less than it looks.
- Commits go through `.github/workflows/ci.yml`. Check the run before calling work done.

## Frontend conventions

- User-visible strings go through `t()` / `tx()` and live in the i18n catalogue.
  Never hardcode a user-facing string in markup or JS.
- Anything interpolated into an `innerHTML` template must be escaped with `esc()`
  (text/attribute context) or `escPct()` (chart geometry). `app.security.test.js`
  enforces this by scanning every `innerHTML` assignment.
- All network calls go through `apiRaw` in `app.js` — exactly one `fetch` call site,
  which is what keeps the bearer header consistent. `app.security.test.js` pins that count.
- Layout behavior lives in `styles.css` and is pinned by `app.layout.test.js`.
  If you change CSS, add or update an assertion there; a silent layout regression
  is exactly what the operators reported and nothing else would catch it.
- Line endings: `.js`/`.css`/`.html` in `src/HeliVMS.Web` are CRLF except `*.test.js`,
  which are LF. Match the file you are editing.

## Test conventions

- Test names are Traditional Chinese sentences describing the guarantee, not the
  method: `第一個RTP封包到達前不會回應`, not `TestNoResponseBeforeFirstPacket`.
  The name shows up in CI output, so it should tell an operator what broke.
- Comments explain **why**, especially why a non-obvious approach was chosen and what
  the failure looks like in production. A test that guards a real incident should say
  what the symptom was.
- Prefer behavioral assertions. Source-text scanning (grepping a `.cs` for a string)
  is brittle and usually a sign the real seam is missing.
- Verify a new guard actually guards: mutate the code it protects, confirm the test
  fails, then revert. A test that passes against broken code is worse than no test.
  **When you restore the mutated file by copying a backup, the copy carries the backup's
  timestamp** — so the restored file is *older* than the DLL built from the broken code,
  `dotnet build` skips it as up to date, and the "reverted" run tests the mutation (this
  already happened here: two budget assertions stayed red after a clean-looking revert).
  Touch the restored files, or build once more after checking the timestamp.
- Check coverage before claiming a module is under-tested. Raw test counts are not
  evidence — `HeliVMS.Media` has 52 tests to `HeliVMS.Storage`'s 997, and measured
  with coverlet it is 92% versus 95%. The module that actually had no coverage was
  `HeliVMS.Recording` at 26%, and the reason was that it had no test project at all.

## Known landmines

- **UDP port selection in tests.** Never pick a port by binding a probe socket and
  disposing it — xunit runs test classes in parallel, so another class can take that
  port. Bind the real socket first and hold it. This caused
  `第一個RTP封包到達前不會回應` to fail only on CI. Holding your own socket does not
  stop someone else's packets from arriving on a recycled port, so every test class
  that binds a loopback RTP port or starts real ffmpeg is now in the `rtc-io`
  collection (`RtcIoCollection`), which does not run in parallel with anything else.
  Adding a socket- or process-touching test class means adding it to that collection. When you
  forget, the symptom is not in your new test: an *unrelated* timing test in the same project goes
  red under load and passes when run alone (measured: a new real-ffmpeg class started in parallel
  with `高倍速會追上排程而不是被Delay粒度綁住` and made it fail at 331 ms against a 250 ms budget).
- **ffmpeg RTP output must stay `-f rtp`.** `-f rte` (RTCP only) produces no media.
  `PublisherIntegrationTests` fails if this regresses.
- **`OpenAsync` must not complete before the first RTP packet.** Returning 201
  immediately gives the browser a connection that looks successful and never shows
  video, with nothing in the logs. Guarded by `第一個RTP封包到達前不會回應`.
  The wait deliberately happens *outside* `_startGate` so a 30s start does not block
  other viewers, which means it must wait on the `TaskCompletionSource` it captured
  inside the gate — never on the nullable `_firstPacket` field, which `TeardownAsync`
  nulls. Reading the field let a concurrent teardown pass for "media has arrived".
  Guarded by `等待首包期間被拆除不會回報成功`, which calls `TeardownAsync` directly
  (hence `internal`) instead of racing timing.
- **`X_DisableExtendedMasterSecretKey = true`** is deliberate: pure-managed SharpSRTP
  uses EMS by default (RFC 7627) but some older Safari only accepts RFC 5764.
- **Do not add trickle ICE.** There is no `PATCH` endpoint; the answer must carry
  complete candidates or the browser has no path to the server.
- **Encoding parameters are load-bearing.** payload type 96, H.264/90000,
  `packetization-mode=1`, `profile-level-id=42e01f`. Any mismatch means "connects,
  no picture" with a clean log. `WhepLoopbackTests` asserts the negotiated SDP and
  that ICE + DTLS really complete against it.
- **A `.csproj` outside `HeliVMS.slnx` is never compiled by anything.** No build,
  no CI, no error — the source can rot indefinitely. This is not hypothetical:
  `Tools/HeliVMS.Decoder` sat outside the solution from the initial commit with a
  CS1587 in it, while `src/HeliVMS.Decoder` (a project with zero source files) was
  the one the solution actually built. `SolutionCoverageTests` now fails the build if
  a project is undeclared; add new projects to the slnx, or move them under
  `Tools/_deprecated/` to state that you meant to drop them.
- **Deleting a directory must remove its slnx entry, its CI test list entry, and every
  test project that referenced it — all in the same commit.** `Delete Tools directory`
  removed six tracked projects but left `HeliVMS.slnx` referencing them and
  `HeliVMS.LicenseProducer.Tests`/`HeliVMS.ExportVerify.Tests` referencing Tools
  projects directly, so CI failed with MSB3202 while every local build stayed green —
  the deleted files still existed on the dev machine's disk as untracked leftovers,
  and `dotnet build` resolves references from disk. A green local run therefore
  proves nothing about a commit that deletes files; check `git status` for untracked
  leftovers before concluding a deletion is complete.
- **`packages.lock.json` must be committed.** CI restores with `--locked-mode`, so a
  missing lock file is green locally and red on CI (NU1004) — which reads like a
  runner or network problem and isn't. `dotnet restore HeliVMS.slnx` then commit the
  generated file.  Also asserted by `SolutionCoverageTests`. **Self-contained publishing poisons the
  lock files:** `scripts/publish-desktop.ps1` restores with `-r win-x64`, which rewrites
  every `packages.lock.json` with a `net10.0/win-x64` RID graph; committing that state
  (e.g. via `git add -A` right after publishing) makes CI locked-mode restore fail with
  NU1004 on every project. The script reverts the lock files itself after publishing;
  if you publish by hand, check `git status` for lock files before committing.
- **Enum switch expressions must keep the discard arm (`_ =>`).** With
  `TreatWarningsAsErrors`, CS8524 (an unnamed enum value not handled) is an error, and it
  fires the moment a new enum member is added while some switch over that enum lost its
  discard arm - the arm most often displaced when an edit replaces `_ => fallback` with
  the new member's case. Keep both: the named cases AND the discard (mapping unknown
  values to the safest fallback). Recently bit the maintenance-task kind combo.

- **`ComboBox.SelectedItem` compares by reference.** Setting it to an object that is
  equal-but-not-the-same instance as one in `ItemsSource` is silently discarded
  (`SelectedItem` becomes `null`, `SelectedIndex` `-1`) — no exception, no log. This
  already shipped once: `PlaybackWindow.LoadChannels` called `_channels.List()` twice,
  so every "open playback for this channel" entry point (live view, map pin, event
  centre, drag-and-drop) opened a window with no channel selected and a black player.
  Query once and select the instance from the collection you assigned. Guarded by
  `回放選台用的是ItemsSource裡同一份實例`.
- **Enabling local auth must not lock you out.** `auth.enabled=1` with no enabled
  admin parks the desktop app on `LoginWindow` with no account that can pass, and
  the only recovery is editing `index.db` by hand. `SettingsWindow` refuses to enable
  without an enabled admin and refuses to disable/delete the last one; `App.PerformLogin`
  fail-opens (logging `%TEMP%\helivms-auth-bootstrap.log`) if that state is reached
  another way. Policy is `AuthService.HasEnabledAdmin` / `WouldRemoveLastEnabledAdmin`,
  behavior-tested by `AuthBootstrapLockoutTests` and wired by `AuthBootstrapContractTests`.
- **Recording shutdown must ask ffmpeg to quit, never kill it first.** `ffmpeg` keeps
  the segment open until it reads `q` on stdin; killing it leaves
  `seg-HHmmssfff.mp4.tmp` while the index says `seg-HHmmssfff.mp4`, so the reconciler
  marks the segment `missing` and `RetentionService.PurgeStaleTmp` deletes the file
  10 minutes later. Every abnormal exit therefore eats the last recording with no
  visible error. `SegmentRecorder.StopAsync` now cancels, sends `q`
  (`IProcess.RequestQuit`), waits up to `DefaultGracefulStopTimeout`, and only then
  kills — a killed segment is marked `corrupt` and never renamed to a final file.
  Guarded by `停止時會請ffmpeg收尾並留下可回放的最後一段` and
  `收尾請求被忽略時會強制結束ffmpeg且不留下半截正式檔`.
- **Recover `.tmp` segments before purging them.** `TemporarySegmentRecovery` runs as
  the first step of `RetentionCoordinator.Run`; with the order flipped, the stale-tmp
  purge deletes the very files the recovery was about to rescue. Guarded by
  `清理會先救回中斷的暫存段再清除逾時暫存檔`.
- **A failed reindex must not condemn a finished segment.** Recording finishes in three
  steps: `File.Move(tmp → final)`, hash, `CompleteSegment`. Anything that throws in the
  last two used to land in one `catch` that deleted the `.tmp` (already moved, so a no-op)
  and wrote `status='corrupt'` — leaving a complete, playable MP4 on disk that no query
  returns (playback reads only `final`) and that `RecordingDiskImporter` skipped as
  "already indexed". Nothing logged it. `SegmentRecorder.TryCompleteMovedSegment` now
  completes the row from the file's own size/mtime/hash, but **only when the `.tmp` is
  gone**: if the move failed, the file at `finalPath` belongs to somebody else and copying
  it into this row would point evidence at the wrong camera. Both ends of the same gap:
  `CompleteSegment` also accepts `corrupt` rows, `TemporarySegmentRecovery` rescues rows
  whose final file exists, and `AdoptOrReviveFinal` revives `missing`/`tmp`/`corrupt` so a
  manual rescan fixes what the automatic one missed. Guarded by
  `收尾途中失敗但正式檔已在時不會被記成corrupt`,
  `改名成功後才失敗的段會用磁碟上的事實收尾`,
  `暫存檔還在時不會把撞名的正式檔收進索引`,
  `被標成tmp的段在正式檔還在時會復活成final` and
  `被標成corrupt而檔案不是MP4時不會被復活`.
- **Playback must finalize the segment that is still being recorded.** Recordings are one file
  per segment, and the segment currently being written only enters `segments` when ffmpeg exits.
  So the item an operator actually wants — "what just happened" — is always the one missing from
  the timeline, with nothing in the log to say so. `PlaybackWindow` therefore awaits
  `IPlaybackRecordingFinalizer.FinalizeAsync` **before** `LoadSegments`, on open, on channel
  change, on date change and on "load"; reversing that order silently makes the cut useless
  (the new segment only shows up on the next reload). Desktop has two independent recording
  paths — `ChannelSession` on the monitor wall and `RecordingScheduler` — and the scheduler
  deliberately skips channels the wall already records, so both must be asked or the same
  button shows different footage depending on whether the camera happens to be on screen.
  Two gates keep this from shredding the archive: `SegmentRecorder.MinCutAge = 10s` (a segment
  started moments ago has nothing new in it, and cutting it leaves a length-≈0 row nobody can
  click) and `CutCooldown = 30s` per channel, counted from the moment `q` was actually sent.
  Each cut is a segment boundary, i.e. an RTSP reconnect and 0.5–2s of lost picture, so repeated
  clicks must not keep restarting the recorder. Only "today" is cut: paging through history
  must not truncate the live segment. Guarded by `PlaybackFinalizeContractTests`,
  `PlaybackRecordingFinalizerTests`, `按回放要求收檔會把正在錄的那一段收成正式檔`,
  `同一台錄影機在冷卻時間內只會收檔一次`, `錄不到五秒的段不會為了回放被截斷` and
  `收檔逾時會回報失敗而不是卡住回放`.
- **Changing channel or day is an async funnel, not a synchronous event.** `SelectedItem`/
  `SelectedDate` fire `SelectionChanged` synchronously, and that handler awaits the cut before
  `LoadSegments` — whose first act is `StopPlayback`. A caller that switches channel and then
  immediately calls `PlayFrom` gets its playback killed ~2s later: dropped channels play for a
  moment and go black, a double-clicked pane freezes, log clean. All entry points go through
  `SwitchChannelAsync` → `await ReloadForPlaybackAsync()` and only then play/resume, and every
  programmatic selection sets `_suppressSelectionReload` so the event does not queue a second
  load. Concurrent loads are ordered by `_reloadGeneration`; an older one returns `false` and
  must not write its rows over the newer selection. `ReloadForPlaybackAsync` also swallows its
  own exceptions (logs `%TEMP%\helivms-playback.log`) because it is awaited from `async void`
  handlers. Guarded by `PlaybackReloadContractTests`, `換台會等新頻道的索引載入完成才開播`
  and `程式自己改選取時不會再排一次載入`.
- **Clearing the list must clear the playback head and the last frame.** `StopPlayback` keeps
  `_current`/`_bitmap` on purpose so "pause then play" resumes; across channel/day/empty states
  that becomes "list is B, pressing play shows A's file" and a snapshot saved as B's evidence.
  `LoadSegments`, `ShowEmptyState` and `ClearPlaybackHead` therefore clear `_current`, the seek
  position and `PlayerImage.Source` together. A finished session is also disposed in
  `OnEnded`/`OnPaneEnded` — `StopPlayback` only owns the *running* one, so every played segment
  otherwise leaks a CTS and a process handle. Guarded by `換台或換日會丟掉上一輪的播放頭`,
  `播完的播放工作會被釋放` and `載入失敗會清空清單與時間軸`.
- **"Played to the end" cannot be inferred from `DurationSec`.** The newest segment often has
  no duration yet, so a position check never reaches the end and pressing play resumes at EOF
  and instantly ends again — the button looks broken. `_playbackEnded` is set only when ffmpeg
  reports completion and cleared by `PlayFrom`/`ClearPlaybackHead`; resume goes to 0 when it
  is set. For the same reason `PlaybackSegmentTiming.EndOf`/`LengthOf` (10s fallback) is the only
  length/end conversion: `StepFrame`, the jump-to-last-segment path, event and bookmark matching
  all use it, and `PlaybackWindow.EndOf`/`PlaybackSyncPlanner.EndOf` are thin delegates to it.
  Writing `DurationSec ?? 0` there collapses the segment window to an instant, so clicking the
  event or bookmark reports "no recording here" while the bar clearly shows a block, and
  `?? 1` made "last segment" restart at second 1. A second copy in `PlaybackSyncPlanner` (it used
  zero length for unknown duration) made the timeline draw a block that multi-pane sync then
  refused to play. Guarded by `暫停後可續播且播到尾再按會從頭`,
  `事件與書籤跳轉使用同一段結束口徑`, `段結束時刻一律使用同一個換算口徑` and
  `段長未知的段在同步時仍視為有錄影`.
- **Arrow-key seek must land on the nearest segment edge, not the last segment of the day.**
  The keys are ±10s (±60s with Ctrl, ±600s with Shift), and a day of recording is full of gaps.
  The old `SeekBy` only special-cased "before the first segment" and sent everything else to the
  end of the last segment, so one press could jump from morning to evening and silently skip the
  segment the operator wanted. Landing is now `PlaybackSeekPlanner.Resolve` (pure function in
  `HeliVMS.Shared`, behaviour-tested): inside a segment → that offset; in a gap → next segment
  start when moving forward, previous segment's last frame when moving back; past the end → the
  last frame (never wraps to the start). `ffmpeg -ss <duration>` yields no frame, hence the
  1ms `LastFrameEpsilonSeconds`. A segment's coverage is half-open `[start, end)`: landing exactly
  on an end used to count as "inside that segment at the last second", i.e. an instant
  `播放完成` even though the next segment existed — the operator saw the status flip to "ended",
  pressed the key again and only then moved on. With the half-open interval that moment is a gap
  like any other (→ next segment forward, → that segment's last frame backward), and a seam
  (next start == previous end) still belongs to the later segment without relying on the
  "latest start wins" tiebreak. Every "jump to this instant" entry point resolves through this
  one helper — timeline click, event, bookmark, the time-jump box and resume/drag-in
  (`OnBandClicked`, `OnEventSelected`, `OnBookmarkSelected`, `JumpToLocalTime`,
  `ResumePlaybackAt`) — because each of them had grown its own closed-interval `FirstOrDefault`
  loop; a gap returns no landing instead of a neighbouring segment (resume falls back to the
  latest segment and says so), so none of them can land on a block's 60th second. For the same
  reason the seek slider drag and frame-step clamp to `PlaybackSeekPlanner.LastFrameOf` (length
  − 1ms) instead of the length itself. Guarded by `PlaybackSeekPlannerTests`,
  `時間軸點擊與方向鍵共用同一份落點規則`, `時間跳轉與續播也共用同一份落點規則`,
  `拖到段尾與逐幀到段尾都停在最後一格` and `事件與書籤跳轉使用同一段結束口徑`.
- **Reverse playback is a beat, not a negative speed — and its landing must be the same planner the
  arrow keys use.** `QC_Player` lists forward/slow/**reverse**/step as one family of playback
  abilities; HeliVMS had forward only until 2026-11-11 (`docs/PLAYBACK-ALIGNMENT.md` §3). The decoder
  can only start at a keyframe and seek forward, so "negative speed" is impossible at the file level
  (`-vf reverse` has to buffer the whole segment — GBs for a 60 s 1080p clip); reverse is implemented
  as **one step back every 250 ms** (`PlaybackReversePump.TickSeconds`), step length = speed × 0.25 s
  clamped to 0.25–4 s (below that it lands on the same frame, above it skips a whole GOP).
  `PlaybackReversePump.Next` calls `PlaybackSeekPlanner.Resolve(…, forward: false)` — reverse must not
  grow a second "where do gaps lead" rule, or it silently skips segments the timeline clearly shows.
  Reaching the **first segment's 0 s stops reverse** (returning `ReachedStart`); wrapping to the last
  segment of the day looks like "reverse jumped to the evening". The frame is shown through the step
  seam (`PlayFrom` + `_pendingFramePause`), never a new decode path. Every gesture that takes the
  playhead must call `StopReverse()` — play/pause, stop, scrubbing the timeline, clicking it,
  switching channel/day/reload (`ReloadForPlaybackAsync`) and window close — otherwise the beat keeps
  pulling the picture back while the operator does something else, which reads as "playback jumps
  around on its own". Guarded by `PlaybackReversePumpTests` (includes "repeated reverse is monotonic
  and converges to the start") and `PlaybackReverseContractTests`.
- **The status line and the time readout must not keep describing a state that ended.** Three
  separate reports, one root: the last message written wins, and nothing rewrites it.
  `OnStopClicked` called `StopPlayback` and never touched `StatusText`, so pressing 停止 (or Esc,
  which had its own copy of the call) froze on the last frame while the bar still said
  「播放中（1×）」 — the operator's conclusion was "the stop button is broken". Esc now goes
  through `OnStopClicked` so the two paths cannot drift. `PreviewBandAt` overwrites `TimeText`
  with the hovered instant; with no frames coming in (paused, or a click in a gap) the readout
  stayed at a time the operator never stopped at — and that readout is what gets copied into a
  report. It is restored by `OnBandMouseLeave` and by the gap branches of `OnBandClicked`, all
  through the single `ShowPlayheadTime` (which also replaced three copies of the same
  `"{pos} / {length}"` format string). Guarded by `按停止後狀態列不會再寫著播放中` and
  `滑鼠離開時間軸後時間讀數回到播放頭`. The readout itself must lead with the **recording
  instant** — segment start + position → local time via `RecordingInstant`, formatted by
  `HeliVMS.Shared.PlaybackClock.FormatInstant` as `18:25:32:015` (時:分:秒:毫秒) — with
  `pos / length` only as a secondary. The last field is the **displayed frame's** milliseconds
  (`_posInside` is `BasePos + frame.PtsMs / 1000`), not a wall-clock estimate; the operator asked
  for it because "a second contains dozens of frames and a seconds-only readout cannot say which
  one I am looking at", and this readout is what gets copied into reports. Keep `pos / length` at
  seconds (a relative value; milliseconds there are noise). The pane OSD shows the same instant
  through the same formatter, so the main readout and a synced pane can never disagree about
  "what time is this". The **main player has its own top-left OSD too** (`PlayerOsd`, same
  `OsdText` format): in 1-split the main view is the only pane, and an operator who zoomed in from
  the wall loses the familiar corner clock if only the bottom readout carries the time. It is
  written by `ShowPlayheadTime` (the only writer of time readouts) and cleared with the playback
  head, so it can never outlive its segment. Guarded by `播放讀數以錄影時刻為主而非段內位置`,
  `單一畫面也有左上角的錄影時間OSD`, `次畫面OSD顯示錄影時刻而非段內位置`,
  `播放讀數到毫秒且主次畫面共用格式` and `PlaybackClockTests`.
- **Multi-pane playback must stay on one clock: a small tolerance AND per-pane startup-latency
  compensation — never one without the other.** 現場回報「多路回放各播各的」：每一格都落後主畫面
  不同秒數。舊容差 1.5 秒（`PlaybackWindow` 自己寫的 `PaneDriftToleranceSec`）讓每格可以先落後
  一個半秒以上才被拉回，而更根本的是：**從按下播放到第一張影格之間的啟動空窗從未被補償**——
  那格之後是等速播放，於是永遠落後它自己的啟動延遲。修正：容差改由排程核心提供
  （`PlaybackSyncPlanner.DefaultDriftToleranceSeconds` = 0.4 秒，約 10–30fps 下 4–12 張影格，
  再小會因解碼啟動抖動而不停重開 ffmpeg），跳轉位置改用
  `PlaybackSyncPlanner.CompensatedOffset(offset, 該格量到的啟動延遲)`，延遲在**第一張影格到達時**
  量測並以 `NextLatencyEstimate`（0.5/0.5 移動平均、上限 5 秒）更新。
  **等待第一張影格期間不得做漂移判定**（`ShouldReseek` 的 `waitingForFirstFrame` 分支）：
  那時的「目前時刻」就是刻意超前的跳轉位置，與母鐘比一定超出容差，照判會每一輪重開 ffmpeg、
  畫面永遠起不來；等待期間只處理「卡住」（超過 `MaxStartupLatencySeconds` 才重開一次）。
  只調容差會讓啟動慢的那格永遠追不上並每輪重開；只補償不縮容差則仍然看得到各播各的。
  **重新對時要有滯後**：單輪超容差就重開 ffmpeg，等於「解碼比母鐘慢一點」的那一格在
  開啟→落後→重開→再落後之間反覆，畫面一直閃（日誌 `restarts` 每 5 秒 > 2 而 `drift` 只是略超）。
  `PlaybackSyncPlanner.EvaluateDrift` 把「要不要重開」與「連續超容差次數」一起回傳，
  達 `DriftHysteresisTicks`＝3（約 0.45 秒）才真的重開；計數要逐輪寫回該格
  （`PlaybackWindow.Pane.DriftStreak`）並在 `PlayPane` 歸零，否則每輪都從 0 算＝滯後形同不存在、
  所有測試仍全綠。等待首幀期間（含「卡住」）一律歸零，否則首幀一到就固定再閃一次。
  Guarded by `PlaybackSyncTimingTests`,
  `多路同步用共用容差且跳轉補上啟動延遲`, `同步重開的滯後計數會寫回該格且在重開時歸零`
  and `次畫面以母鐘解算且空隙不硬跳別段`.
- **The playback clock is the file's own frame timestamps, never `frame index ÷ nominal fps`.**
  Real recordings do not deliver at their nominal rate: measured on the live system, one camera's
  segments mix 67 ms and 100 ms spacing (12.5 fps effective, `r_frame_rate` 15) and another
  declares 30 fps but delivers 13 (`-c:v copy` copies the camera's VFR timestamps). Frame-count
  playback therefore runs each camera at its own speed (1.0× / 1.2× / 2.3×) and reports an instant
  that is not the displayed frame's instant — so every pane shows a different moment while all the
  OSDs agree, nothing looks wrong on screen and nothing is logged. `PlaybackSession` now reads
  `packet=pts_time` once per file (`HeliVMS.Media.FrameTimestamps`, cached by path+length+mtime,
  shared by all panes of the same file) and maps/pace through
  `HeliVMS.Recording.PlaybackFrameTiming`; a camera stall then shows as a still frame instead of
  being fast-forwarded. Two things not to "simplify" back: **do not use `-fps_mode cfr -r <avg>`**
  — measured with this ffmpeg, a file with a genuine 5 s gap held the first post-gap frame for
  *twice* the gap (the output grid ran to 30.3 s while the content stayed at 20.0 s), i.e. a
  silently shifted timeline; and **frame stepping must use the real neighbouring frame**
  (`PlaybackFrameTiming.IndexAtOrBefore`) — a fixed 1/15 s step lands on the same frame on a
  12.5 fps file ("F does nothing") and skips several on a 13 fps one.
  Guarded by `FrameTimestampsTests`, `PlaybackFrameTimingTests`,
  `回放時刻取自檔案時間戳而不是影格序號除以名目幀率`, `逐幀步長取自檔案相鄰兩格`.
- **A pane that finished its file must not be restarted while the master is still in the same
  index segment — and a cancelled playback must not report "completed".** `segments.duration_sec`
  is wall-clock (ffmpeg connect warm-up plus camera stalls), not content length: measured +0.6 s
  for healthy cameras and **+12 s** for one that stalls. So the pane's `Resolve()` keeps returning
  that segment for another 12 s after its file ended, and the old code re-opened ffmpeg every
  150 ms (each run ending immediately) until the master moved on — a pane that visibly keeps
  re-flashing, log clean. `PlaybackWindow.OnPaneEnded` now records `FinishedSegmentId`/
  `FinishedAtUtc` and `SyncPanes` holds the last frame until the master either moves into the next
  segment or rewinds before that instant (`PlayPane` clears the mark). Separately,
  `PlaybackSession` reports `completed: false` whenever the token was cancelled — including the
  path where the read loop exits at its top edge before reading anything; reporting `true` makes
  `PlaybackWindow` treat 停止 as 已播畢, so pressing play afterwards restarts from the segment
  head (the field symptom was "stop then play jumps back to the beginning").
  Guarded by `次畫面播完後母鐘還在同段時不會一直重開` and `取消而結束的播放回報未完成`.
- **The wall-clock anchor of a segment lives in the index, not in the file — and it is the ffmpeg
  *launch* time.** `segments.start_time` is written by `BeginSegment` before ffmpeg connects, while
  the file's first frame arrives after the RTSP session is established. Measured on the live system:
  healthy cameras start 0.6–0.9 s late (harmless, and it is why the pane hold rule exists), but one
  camera takes 2–14 s per segment, so its claimed instants are that far *earlier* than the content
  really is — a constant per-segment offset that no playback-side change can recover, because the
  file carries no wall clock (PTS starts at 0, no `creation_time` tag). Two consequences: (1) a
  heading like "multi-pane playback is off by N seconds for camera X" is this, not the sync logic —
  check `duration_sec` against the file's real span (`ffprobe -show_entries packet=pts_time`, last
  minus first) before touching `PlaybackSyncPlanner`; (2) **do not "correct" it at playback time by
  shifting every instant by `duration_sec − content span`**: an internal stall (camera stalls
  mid-segment) is indistinguishable from a slow session start, and shifting a whole segment by the
  stall total moves the early part *further* off. Fixing the anchor properly means recording the
  first frame's arrival (or `-use_wallclock_as_timestamps`) and teaching `RecordingDiskImporter` the
  same value, since the importer derives `start_time` from the `seg-HHmmssfff.mp4` filename (the
  launch time). Until then the offset is at most part of a second on healthy cameras.
  **The segment *length*, in contrast, is not a wall clock at all — it is the file's own duration.**
  It used to be `_clock() − startUtc`, i.e. how long the process was scheduled, and that number
  grows without bound when Windows sleeps or the clock is corrected: measured on the live index,
  15 of 68 `final` rows differed from the file by more than 60 s, the worst two claiming 6853/6849 s
  for 250/260 s files, and four claiming 99–200 s for 0.07–0.34 s files. On screen that is hours-wide
  bands on the timeline, arrow keys landing where no picture exists, and every pane of a multi-pane
  session waiting for an instant that never comes. `SegmentRecorder` now probes the finished file
  (`HeliVMS.Media.MediaDurationProbe`, ~90 ms) and records `RecordedSegmentLength.Resolve(probed,
  wall)` — probed wins whenever it is in `(0, 24 h]`, `end_time = start + that length` so
  `PlaybackSegmentTiming.EndOf` and the index cannot disagree, and the wall clock is only the
  fail-open fallback (ffprobe missing, file locked). Do not gate the probed value on being ≤ the wall
  clock: the wall clock is the number being replaced. The recovery path
  (`TryCompleteMovedSegment`, rename succeeded but the index write failed) uses the same resolver —
  fixing only the normal path would leave the segments hit by the very clock jump this exists for.
  fMP4 (`frag_keyframe+empty_moov`) still reports `format=duration`; that is pinned by a real-ffmpeg
  test because if it did not, the whole fix would be silently inert. **Old rows do not repair
  themselves**: `SegmentDurationRepair` (desktop hourly loop and the Agent's `IntegrityWorker`, via
  `SegmentDurationRepairCoordinator.RunConfigured`, `recording.duration_repair.batch` default 100)
  walks `final` rows in id order, probes each file, and rewrites `duration_sec` + `end_time =
  start + 長度` when they differ by more than 0.5 s. It is deliberately **not** piggybacked on
  `MediaIntegrityChecker` — that one's contract is observe-only, and its `checked_at_utc` cursor would
  skip exactly the legacy rows that need fixing. The cursor lives in `app_settings`
  (`recording.duration_repair.cursor`) and is written with a raw upsert, never
  `SettingsRepository.Set` (that one records `settings.set`: a "who changed a setting" audit every
  hour would drown the real changes). `-1` means the sweep finished and later rounds do nothing —
  the hourly cost of rescanning would be pure waste, since new rows are already written correctly.
  A row whose probe fails (ffprobe missing, file locked, file deleted) is skipped and the cursor still
  advances, or the sweep would be stuck on the first locked file forever. Healthy rows differ by
  0.2–1.0 s and are left alone; `merged-HHmmssfff.mp4` hourly files are legitimately ~3600 s and must
  not be "repaired" further. Guarded by
  `RecordedSegmentLengthTests`, `MediaDurationProbeTests`,
  `錄影段長取檔案自身長度而不是牆鐘`, `探測失敗時段長退回牆鐘`,
  `補救收尾也用檔案長度而不是最後寫入時間`,
  `ListFinalAfter以主鍵前進且只回傳final`, `RepairDuration只改寫final段`,
  `段長修正游標存在執行期設定且不寫設定稽核`,
  `以檔案長度修正舊索引的段長與結束時刻`, `段長一致時不寫入`,
  `探測失敗的段跳過並繼續前進`, `游標到結尾後不再掃描`,
  `Agent會跑段長修正走共用協調器` and `桌面保留迴圈也會修正舊索引的段長`.
- **Live view: find out which side owns the delay before "optimising" it — the picture looks the
  same for all four.** 現場回報「即時監看會延遲」時，症狀只有一種，原因卻可能在四段完全不同的
  地方：①攝影機 ＋ 網路 ＋ 解碼緩衝（本機量不到，也修不了）；②本機管線積影格（畫面**愈拖愈舊**，
  可以修）；③UI 佇列積影格（UI 執行緒跟不上）；④WPF 根本在軟體繪製（遠端桌面、虛擬機、沒有可用
  顯示驅動：連把影格貼到視窗都在 CPU 上做，解碼再快也沒用）。`%TEMP%\helivms-live.log` 每 5 秒一行
  （`LiveViewDiagnosticLine`，上限 512KB）把四段分開寫：`read/emit/drop` 每秒率
  （`RtspClient.FramesRead`／`FramesEmitted`／`FramesDropped`；丟棄是**先讀進來才丟**，管線才會被
  抽乾）、`backlog=`（本機管線落後，由 `-progress` 的 `out_time_us` 與牆鐘差推得）、`age=`（畫面
  年齡，UI 佇列）、`shown=`（實際顯示 fps）與解析度，以及行首的 `wpf=`（`RenderCapability.Tier >> 16`：
  2＝完整硬體、0＝全程軟體、`?`＝呼叫端不是 WPF）。實測現場（6 台 640×360 子碼流、約 10fps）：
  `read≈shown`、`drop=0`、`age` 20–150ms ⇒ 本機沒有落後，延遲在①——所以**不要再「優化」本機**，
  先看這一行字。`wpf=` 只寫在行首（它是機器的屬性，不是某一格的），但**一律要寫**：整欄省略與
  「這台是舊版」在日誌上長得一樣，而「`wpf=0` 卻 `read≈shown`、`age` 正常」正是「該換顯示方式
  （本機繪製／關掉遠端桌面的省電模式）而不是再改解碼」的唯一證據。
  **不要為了填一個「延遲 xx ms」而編數字。** 絕對延遲在本機量不到：ffmpeg 會把輸出時間歸零
  （實測 `-copyts` 也一樣：`out_time_us=0`；`showinfo` 的 `pts_time` 同樣從 0 起算），所以畫面上
  顯示的是「本機落後量」，量不到就顯示「—」。`-use_wallclock_as_timestamps 1` 只影響時間戳
  （畫面順序由 H.264 的 POC 決定，已用 loopback 伺服器驗過），但它是讓 backlog 可信的關鍵：
  攝影機自己的時鐘快慢才不會被誤算成落後。
  **`-progress` 的行必須被過濾掉**（`RtspClient.IsProgressLine`）：它們每秒十幾行，一旦混進
  stderr 尾端那 20 行，操作員看到的「連線失敗原因」會變成 `out_time_us=…`（真正的 401 被擠掉）。
  每一格的狀態文字只在 1 秒的計時器更新（`UpdateCellMetrics`），不要恢復成每一張影格重排——
  那是最白花的一種 UI 成本，而且那些數字一秒才變一次。
  **顯示上限要用令牌桶，不是「距上一張至少 1/上限 秒」。** 攝影機是一陣一陣送的（現場實測
  一批兩三張貼在同幾毫秒內），嚴格等間距會把每一批的第二張丟掉：診斷日誌同時出現
  `read=15/s`、`drop=4–9/s`、`shown=5–10/s`——**畫面比攝影機原本還頓**，而 operator 只說得出
  「有點不流暢」。`FrameRateGate`（令牌桶，burst 2 張）修好之後同一組鏡頭變成
  `read=15/s`、`shown=15/s`、`drop≈0/s`（實測 6 路）。丟棄仍必須發生在讀取之後（管線抽乾）。
  同理，`FrameRateMeter` 要拒收同一毫秒的重複影格並把回報值夾在上限內：抖動時它算過
  `shown=772/s`，那種數字會被誤讀成「機器很快」而蓋掉真正的訊號（低 fps、高年齡）。
  Guarded by `LiveViewDiagnosticLineTests`（含 `診斷行會寫出WPF渲染層級`、
  `未知渲染層級寫成問號而不是省略`）, `LiveFrameRatesTests`, `FrameRateGateTests`,
  `LiveViewCounterTrackerTests`, `進度回報跟不上牆鐘時回報管線落後`,
  `超過顯示上限的影格是先讀進來才丟棄`, `進度回報不會蓋掉連線失敗原因` and
  `LiveViewDiagnosticsContractTests`.
  **多分割的即時監看現在會在解碼端縮尺寸**（`HeliVMS.Shared.LiveWallBudget`，純函式）：一格在
  9 格以上版面只有幾百像素寬，照原生解析度解碼再讓 WPF 用軟體縮小，等於付完整成本只為畫縮圖。
  1–4 格刻意與調整前完全相同（不縮、15fps），所以最常用的比對版面「看起來沒變」。兩個不可
  改回去的地方：(1) 永遠解**完整**影格，不像回放牆 36 格以上切「只解關鍵影格」——即時監看若
  只解關鍵影格，等於每 2–10 秒才換一張，現場會直接判定「畫面不動了」；(2) 尺寸口徑只有
  `VideoScaling` 一份，送給 ffmpeg 的 `scale=` 與讀取迴圈湊一幀的位元組數差一個像素就是每張
  影格錯位（全黑或斜切、日誌乾淨）。`RtspClient.SetDecodeWidthAsync` 只在**實際生效寬度**
  改變時才重開 ffmpeg，否則切一次分割每一格都閃「連線中」並重新協商 RTSP。錄影不受影響
  （走獨立的主流 `SegmentRecorder`）。「改用 `-c:v copy` 直送 H.264 給 WPF 解碼」的效益已量測
  否證：解碼本身 3.91 s、色轉與 1080p 管線只多 2.67 s，軟體解碼換不掉那 3.91 s。
  Guarded by `LiveWallBudgetTests`, `VideoScalingTests`, `RtspDecodeScalingTests`,
  `LiveWallScaleContractTests`.
  **解碼端縮小的濾鏡固定 `flags=area`，字串只有 `VideoScaling.DownscaleFilter` 一份**（即時監看
  與多路回放共用）：swscale 預設的 bicubic 在 3:1 以上的縮小是「取樣」而不是「平均」，同一組 6 路
  1080p15（每路 300 幀、4 輪取中位數）總 CPU bicubic 22.84 s、bilinear 19.92 s、**area 17.56 s
  （−23%）**、fast_bilinear 17.39 s；而縮到 640×360 再放大回 1080p 對原圖的 PSNR bicubic
  32.44 dB、**area 32.37 dB**、bilinear 32.14 dB、fast_bilinear 26.43 dB。也就是 area 用
  fast_bilinear 的速度換到 bicubic 的品質，而 fast_bilinear 少掉的 6 dB 就是監看畫面裡欄杆、
  車牌邊緣的鋸齒。搭 `d3d11va` 時同樣有效（bicubic 9.77 s → area 8.06 s）。少一個 `flags=area`
  畫面完全正常、只是大格數時 CPU 白花——沒有任何錯誤訊息，只有 CPU 使用率看得出來，所以必須釘在
  測試裡（`縮小濾鏡固定使用area且尺寸與換算口徑一致`、`指定解碼寬度時縮放濾鏡在輸入之後且在輸出
  格式之前`、`縮放寬高與宣告給UI的尺寸一致`）。
  **縮小解碼時連解環濾波器一起跳過（`-skip_loop_filter all`，總 CPU −15.2%）**，但只在
  `VideoScaling.ShouldSkipLoopFilter(sourceWidth, decodeWidth)` 為真時——也就是「真的縮小」。
  量測（6 路 1080p15、每路 300 幀、`scale=640:360:flags=area`、A/B 交錯配對 10 次取中位數；
  單次量測在本機會在 30.7–39.7 s 之間漂移）：38.9 s → 33.4 s，**10/10 次都較快**（最差 −7.4%、
  最好 −19.8%）。品質（跳過前後對同一張基準）：1080p→640×360 的低碼率來源 43.2–44.0 dB／
  SSIM ≥0.9938，640×360 次碼流縮到 480/384/320/256 是 42.9–45.7 dB／SSIM ≥0.9906。
  界線畫在「有沒有縮小」而不是「解碼成本高不高」，因為解環濾波器修的是 8×8 方塊邊界，而
  `flags=area` 的縮小本來就會把它平均掉；**原生尺寸（1–4 格、單格回放）一律不跳**——那是操作員
  放大看車牌的路徑，而且那時只有少數幾條解碼行程，省下來的 CPU 換不到東西。四件不要改回去的事：
  (1) **值只能是 `all`**，本機 ffmpeg 8.0.1 對 `nonref` 回 `Unable to parse option value` 並直接
  結束（＝每一格都連得上卻永遠沒有畫面，日誌只有一行 parse 錯誤）；(2) **必須在 `-i` 之前**，
  放後面會被當成輸出選項而靜默無效；(3) **來源寬度必須真的傳進去**（`RtspClient` 傳探測寬度、
  `PlaybackSession` 用 `sourceWidth`），漏傳的症狀只有「大格數時 CPU 沒下降」，畫面完全正常；
  (4) 與 `d3d11va`／`dxva2`／`-skip_frame nokey` 併用在 H.264／HEVC 8-bit／10-bit 都驗過正常
  （`qsv` 的失敗是既有的 `-vf scale` 吃不了 hw frame，與此無關）。Guarded by
  `只有真的縮小才跳過解環濾波器`、`跳過解環濾波器的引數固定是all`、
  `縮小時連解環濾波器一起跳過且放在輸入之前`（即時監看與回放各一條）、
  `沒有真的縮小時不會跳過解環濾波器` and `縮放後實際影格尺寸與參數一致`（後者走 stub 行程，
  是唯一釘住「呼叫端真的把來源寬度傳進去」的那一條）。
  兩個「預算表旗標」也走同一份硬解決策（`ChannelManager.HardwareDecodeEnabled`、
  `PlaybackWindow.HardwareDecodeEnabled` 皆 `HardwareDecodePolicy.Resolve(..., scalingActive: true)`，
  且**不快取**）：若那裡自己用 `FfmpegHwAccel.ShouldUse`，`auto` 一律回 true，預算表就會給出
  硬解才做得到的更新率上限，而解碼端因原生尺寸刻意用軟解——一張做不到的支票。
  Guarded by `硬體解碼設定與回放牆共用同一把尺` and `硬體解碼沿用即時監看的設定與探測`.
  **AI 啟用的頻道不縮小解碼寬度**（`LiveWallBudget.ApplyDetectionRule`）：AI 的輸入就是那張
  影格的像素，縮小等於把物件縮小後才辨識，遠處的人車會消失且沒有任何錯誤訊息。回原生寬度
  也正好等於調整前的行為（零回歸）。運動／遮蔽偵測不受影響（先抽樣成 16×16 亮度格）。
  判斷放在純函式而非桌面端，就是因為它只有兩種結果、但沒有執行期測試可釘——寫錯的方向
  （無條件套用）不會報錯，只會讓全站悄悄停止縮小解碼。三條路徑都要經過它：
  `SetLiveBudgetAsync`、`EnsureMainAsync`、以及 `ConfigureAi` 在開關變動當下重新套用。
  Guarded by `AI偵測啟用時不縮小解碼寬度但保留更新率上限`,
  `沒開AI的頻道仍然要縮小解碼`, `解碼寬度一律經過AI例外換算` and
  `AI開關變動時會立即重新套用解碼寬度`.
  **動解碼前先看佔比表**：`docs/LIVEVIEW.md` §3.4 有解碼／色轉／顯示三段的實測拆解，以及七條
  **已用 A/B 交錯配對否證**的候選（`-sws_flags`、`zscale` 色轉與縮小、`+faststart`、`rgb565`、
  「把來源縮到格子尺寸以降低軟繪成本」、`NearestNeighbor`）。兩個反直覺但量得很乾淨的事實：
  1080p 的解碼端有 **38% 不是解碼而是 yuv420p→BGR24 的色轉**（所以「硬解對原生 H.264 打平」），
  而 **WPF 軟繪成本 ∝ 輸出像素 × 更新率、與來源解析度無關**（同一版面把來源由 640×360 換到
  176×99，每 tick 成本 61.5→61.9 ms 完全不變）——所以「軟繪時縮小解碼讓 WPF 不必縮放」是無效
  優化，`wpf=0` 時該換的是顯示方式。重做這些實驗前先讀那一節，並照同一套協定量
  （6 路併發、每路 300 幀、`bench: utime+stime`、交錯 A/B ≥5 輪取中位數——本機單次會在
  30.4–39.6 s 之間漂移，單次不可信）。
- **`decode.hwaccel=auto` must be resolved into one concrete method, and only when it pays off —
  `auto` handed to ffmpeg is the worst option measured.** 量測環境：本機
  i5-1035G1（4C/8T）、1920×1080/15fps、6 路同時、`-re` 即時速率、每路 300 幀，總 CPU＝各 ffmpeg 的
  `bench: utime+stime`。原生 1080p：軟解 25.57 s、`d3d11va` 26.17 s（打平）、`auto` 36.42 s（最差）；
  縮 640×360：18.72 → 8.94 s（−52%，`auto` 11.09 s）；縮 480×270：24.41 → 9.39 s（−62%）；
  16 路 640×360：61.20 → 26.11 s（−57%）；24 路 640×360：113.14 → 42.53 s，而且軟解在 24 路
  **追不上即時**（wall 24.1 s 的視窗只解完 113 s 的 CPU 需求，畫面愈拖愈舊）。三個結論：
  (1) **H.264** 原生尺寸的硬解沒有好處（下載回系統記憶體的成本把省下的解碼成本吃光），多一層 GPU
  依賴只是多一種「連得上卻沒畫面」； (2) **H.265 不是這樣**——同一組量測 HEVC 8-bit 原生
  40.42/46.86 s → 35.47/36.28 s（−12%～−23%）、HEVC 10-bit 62.73/84.36 s → 41.66/46.03 s
  （−34%～−45%），縮 640×360 則是 26.00 → 7.70 s／34.95 → 11.39 s（−67%～−70%）；軟解愈貴，
  下載成本佔比愈小，所以**決策輸入是編碼格式，不是 CPU 世代**（第 10 代同時有 Ice Lake 與
  Comet Lake，媒體引擎不同代；同一代不同 iGPU 也不同，寫世代表一定有一半是錯的）； (3) 不能把
  字面 `auto` 交給 ffmpeg，它在上面每一種情境都挑得比明確指定差。
  `HeliVMS.Media.HardwareDecodePolicy.Resolve(configured, available, scalingActive, videoCodec)` 是
  **唯一**的決策點：`auto` → `d3d11va → dxva2 → qsv` 中第一個 `ffmpeg -hwaccels` 回報支援者，且
  只在「`scalingActive == true`」或「`PaysOffAtNativeSize(videoCodec)`（`hevc`／`h265`／`hvc1`／
  `hev1`）」時啟用；探測失敗／偏好清單全缺／還沒探測到編碼格式時回軟解（挑錯的症狀是連得上卻沒畫面）；
  明確值（`d3d11va`／`cuda`／`dxva2`）維持既有語意，不因原生尺寸被改掉。`qsv` 排最後是量出來的：
  本機 ffmpeg 有回報它，但 6 路實測全部失敗。四件不要改回去的事： (1) `scalingActive` 的口徑必須
  與實際解碼相同（`RtspClient.DecodeScalingActive` 走 `DecodedSize`），否則「政策以為有縮、實際沒縮」
  會讓 auto 落在最不划算的情境； (2) 決策只能有一份，即時監看（`RtspClient.EffectiveHwAccel`）與
  回放（`PlaybackSession` 建參數時）都走它，**輸入也要一致**（兩邊都傳 `VideoCodec`；漏傳會讓
  單格回放 HEVC 一律軟解，與監看差 12–45%，而兩邊畫面都正常）；
  (3) `%TEMP%\helivms-live.log` 的每一格都要寫 `hw=`（軟解寫 `hw=off`，不省略整欄）而且寫的是
  **這一輪實際生效**的方法（`RtspClient.HwAccelInUse`），不是設定值——少了它，現場「明明設了硬解
  卻還是吃滿 CPU」只能靠猜，而 `HwAccelSupported` 也已改成回答「這一輪會不會用」；
  (4) `PaysOffAtNativeSize` 是唯一判斷 HEVC 的同義詞（`hevc`／`h265`／`hvc1`／`hev1`）的地方：
  ffprobe 給 `codec_name=hevc`、mp4 的 codec tag 是 `hvc1`／`hev1`，各寫一份遲早漏掉一種。
  Guarded by `HardwareDecodePolicyTests`, `RtspHardwareDecodeTests`（stub ffmpeg 驗真實命令列，含
  HEVC probe stub）、`硬體解碼決策只存在政策裡且監看與回放共用`, `診斷行會寫出實際生效的硬體解碼方式`
  and `實際生效的硬體解碼會寫在同一行裡`. 突變檢查（每一項都先改壞確認會紅再還原）：政策拿掉
  `!scalingActive` 檢查 → Media 2 紅；`EffectiveHwAccel` 改回 `ShouldUse` → Media 3 紅＋App 契約紅；
  `Format` 不輸出 `hw=` → Shared 3 紅；`MainWindow` 不傳 `counters.HwAccel` → App 紅；
  `ChannelSession` 不傳 `HwAccelInUse` → App 紅；`PlaybackSession` 改回 `ShouldUse` → App 紅；
  拿掉 `PaysOffAtNativeSize` 的 codec 判斷 → `原生尺寸的HEVC仍會帶入硬體解碼` 紅。
  已量測但**不採納**的選項：`-threads 1/2/4`、`dxva2`（比 `d3d11va` 慢）、`-pix_fmt nv12`
  單項都沒有好處；NV12 直送＋自寫 AVX2 `NV12→BGR24` 單路 1080p 可再省約 8.9 ms/幀，但色差與
  維護風險高於收益；`scale_d3d11` GPU 縮放本機失敗（`Failed to create input view: HRESULT
  0x887A0004`，加 `-filter_hw_device` 亦同），等有第 11 代以後的實機量到再開。
- **Multi-pane playback scales to 64 panes only because each pane's decode is budgeted — and the
  budget is coupled to the sync tolerance.** 現場要求「多路回放最多 64 路」。64 格各解一條 1080p
  在算術上就是不可能的（一格 6MB 原始影格、畫面上卻只有約 110–160 像素寬），實測（本機 8 核心、
  **同時還在錄 6 路**）：64 路原生解析度＝每格 1fps 而且把錄影的 CPU 搶光；硬體解碼只到每格 2.5fps。
  `PlaybackWallBudget.For(cells, hardwareDecode)`（純函式，行為測試在 Shared.Tests）給每一格
  縮放寬度、更新率上限、解碼執行緒數，36 格以上再切到**只解關鍵影格**（`-skip_frame nokey`）：
  成本降到 1/GOP，量到的結果是 64 格首格 12 秒內到齊、之後每格每 2–20 秒換一張（＝那個 GOP 的
  長度）。**兩個必須一起改的地方**：①36 格以上的對時容差要用
  `PlaybackSyncPlanner.OverviewDriftToleranceSeconds`（30 秒）而不是 0.4 秒——關鍵影格天生落後母鐘
  一個 GOP，用 0.4 秒判定會讓 64 條解碼行程每 150ms 被重開一次（畫面永遠開不起來、CPU 全花在
  啟動 ffmpeg，日誌乾淨）；②`DisplayWidth` 的高度**自己算**（`PlaybackSession.ScaledHeight`，取偶數），
  不可用 ffmpeg 的 `scale=W:-2`——回報給 UI 的影格尺寸必須與 ffmpeg 真正輸出的完全一致，
  差一個像素就是每一張影格錯位（畫面全黑或斜切）。硬體解碼沿用即時監看的 `decode.hwaccel`
  設定與探測，並有**全域冷卻**：一次「帶了 -hwaccel 卻一幀都沒出」就停用五分鐘，
  否則 64 格會各自再撞一次同樣的牆。面板清單（1/2/4/9/16/25/36/64）與格數一律取自
  `PlaybackWallBudget.SplitOptions`，UI 不要再抄一份。
  Guarded by `PlaybackWallBudgetTests`, `PlaybackDecodeScalingTests`（含真實 ffmpeg 的縮放尺寸驗收）、
  `總覽牆的容差比一個GOP還大` and `PlaybackWallScaleContractTests`.
  兩個「看起來不重要、其實是 64 路能不能用」的細節：**次畫面的 OSD 文字有更新間隔**
  （`PaneOsdInterval`，4 次/秒）：64 格每一張影格都重寫 TextBlock 會讓 WPF 每秒重排上千次，只為了
  顯示一個每秒才變一次的數字（主畫面的讀數不受影響——只有一條文字，而且是操作員要複製進報告的）；
  以及**每 5 秒一行回放取樣**（`%TEMP%\helivms-playback.log`，`PlaybackPaneDiagnosticLine`）：
  現場說「多路回放不順」時，那一行是唯一能分辨四種成因的證據（落後母鐘多少、這一格有沒有在更新
  `frame=`、重開解碼幾次 `restarts=`、以及現在生效的 `tol=`——36 格以上是 30 秒的總覽模式，
  用 0.4 秒的期待去看它只會誤判）。`kf` 標記也必須留著：少了它，讀日誌的人會以為「每格每幾秒
  才換一張」是壞掉，而它其實是那個版面的設計。每一格還有 `hw=`，寫的是**這一輪實際生效**的
  硬體解碼（`PlaybackSession.HwAccelInUse` → `HardwareDecodeToken`：方法名／`off`／
  `off(cooldown)`），不是設定值：設 `auto` 時原生尺寸的 H.264 會是 `off`、剛失敗過的會在五分鐘
  冷卻裡也是 `off`，照設定值寫等於三種事實都寫反。沒有在播的格不寫（寫 `off` 會被讀成「正在軟解」）。
  Guarded by `PlaybackPaneDiagnosticLineTests`、`次畫面OSD文字有更新間隔`、
  `同步計時器每五秒寫一行回放診斷` and `回放診斷行寫出實際生效的硬體解碼`.
  容差與解碼預算**每一輪同步只算一次**（`SyncPanes` 開頭的 `PaneBudget()` → `ToleranceForBudget`）：
  舊寫法在逐格回圈裡呼叫容差函式，而它每次都會讀一次 `decode.hwaccel`（SQLite）並重跑預算表——
  8 路 × 每秒約 6.7 輪＝每秒 53 次 UI 執行緒上的磁碟讀取，畫面只表現為「一同步就卡一下」而日誌乾淨。
  `WritePaneDiagnostics` 也沿用同一份預算，否則 `tol=` 可能寫出與實際判定不同的一個數字。
  Guarded by `對時預算每一輪只算一次`.
- **The calendar going from "a month of blue" to "no blue at all" is data, not a bug — say so.**
  回放日曆把「當天有 final 段」的日期標成粗體＋藍字，而標記會隨**索引與磁碟的實況**消失：
  D: 滿檔時保留策略會汰除整個歷史檔案（實測攝影機 01–06 在 11/05 22:18 之前的錄影全部被刪，
  索引裡 11/04–05 有 400 多段標記 missing），日曆因此從「整個月都是藍的」變成「一格都沒有」，
  而且沒有任何解釋——操作員的第一反應是系統壞了或錄影沒在跑。`ApplyRecordingDayMarks` 在
  整月都沒有藍色時，改用 `SegmentRepository.CountByStatusInRange` 查該月的 `missing` 段數：
  **有 missing 段**＝曾經錄過、現在被汰除，就在提示列說出來（`Playback.RecordingsEvicted` 三語）；
  **沒有 missing 段**＝真的沒錄過（剛加入的頻道），不能說「被汰除」——那是在騙人。
  順帶：錄影磁碟的可用空間低於保留值（預設 2 GB）時錄影會暫停、清出空間後自動恢復，
  這是設計（磁碟壓力等級會升到 Critical）；不要為了「讓日曆有藍色」去放寬它。
  Guarded by `日曆整月沒有藍色時會說明錄影已被汰除`（含「門檻是 missing 段」與三語文案）.
- **The playback control bar must expose stepping and looping with the mouse, not only on the
  keyboard.** 現場盤點：回放只看得到播放／暫停／速度，逐幀與跳段只有鍵盤（F、PageUp…），而
  「上一張」根本不存在——新進的操作員不會知道那些鍵，也不該被要求記。現在有四顆按鈕
  （⏮上一段／◀上一張／下一張▶／下一段⏭，快捷鍵 PageUp/PageDown、F/Shift+F）、循環勾選
  （`LoopCheck`：播完回到**本次播放的起點**——有選取區間就回區間起點，落點規則與「重播選取範圍」
  共用 `PlaybackSeekPlanner.Resolve(…, forward: true)`，不是自己找第一段）與「立即同步」按鈕
  （`SyncNowButton`，只在多分割且有待同步的次畫面時可用）。兩個順序陷阱：循環必須排在
  `AutoAdvanceCheck` 之前（順序顛倒＝勾了循環還是往下一段走，永遠看不到第二輪），
  「上一段」在段中按下要先回本段開頭（與一般播放器一致；否則只想重看本段會被一路帶到前一段）。
  跨段與循環都走 `DaySegments`（同一次對帳、同一份排序），不要各自再查一次——兩份查詢遲早漂移。
  事件跳轉（`N`／`P` 與兩顆按鈕）**只移動 `EventList` 的選取**，由 `OnEventSelected` 走既有的
  `PlaybackSeekPlanner.ResolveAt` 落點規則；不要在 `StepEvent` 裡自己算時刻或直接 `PlayFrom`
  （那就成了第二份跳轉規則，事件剛好落在段尾或接縫時會停在「前一段的第 60 秒」）。
  「回到現在」同樣走 `ResolveAt`，而且跳今天時要設 `_suppressSelectionReload`，否則事件會再排一次
  載入、舊的那次會回來搶著開播。上次檢視的頻道存 `playback.channel_id`：開窗時「指定頻道」優先、
  其次才是記憶，而且記憶值必須檢查 `> 0`——頻道被刪之後字串還在，塞進清單會讓開窗失敗。
  Guarded by `逐幀跳段循環與立即同步都有按鈕與快捷鍵`, `循環回到本次播放起點且排在自動續播之前`,
  `事件跳轉與回到現在都有按鈕與快捷鍵` and `上次檢視的頻道會被記住但指定頻道優先`.
- **Day boundaries: auto-advance must cross into the next day, and playback preferences must survive
  the window.** 現場回看跨夜事故（23:40→00:20）時，當天播完就停住，操作員得自己記住看到哪裡、
  手動把日期往後一天、再找第一段。現在 `AutoAdvanceCheck` 開啟時會走 `ContinueToNextDayAsync`
  （最多往後找 `MaxCrossDayLookaheadDays` 天，只取那一天的第一段）。兩個必須保持的細節：換日要走
  既有的非同步漏斗 `ReloadForPlaybackAsync`——直接 `PlayFrom` 會在索引還沒載入時開播，兩秒後被
  那次載入的 `StopPlayback` 停掉，症狀是「跳到隔天了但畫面停住」；而且程式自己改
  `DatePick.SelectedDate` 時要設 `_suppressSelectionReload`，否則事件會再排一次載入。另外把操作員
  的習慣存起來（`playback.speed`／`playback.split`／`playback.auto_advance`／`playback.loop`），
  還原一律經 `PlaybackPreferenceValues`（壞值或越界退回預設——索引越界會讓 `SelectedIndex` 變 -1，
  畫面看起來像壞掉），並且在**改動當下**寫回而不是等關窗（關窗有自己的收尾路徑，漏掉就沒存到）。
  鍵盤補齊：Home／End＝當天第一段／最後一段、↑／↓＝上一段／下一段。Guarded by
  `當天播完會自動接隔天且等索引載入完成`, `方向鍵與HomeEnd可以跳段`,
  `回放偏好會記住且壞值退回預設` and `PlaybackPreferenceValuesTests`.
- **"Replay selection" must stop where the selection stops.** Recordings are one file per
  segment, so a selected 10:00–10:30 spans several files. `OnEnded` continues into the next
  segment while it still starts before the upper bound — but that branch must also *end the
  replay* when there is none: falling through to the "auto advance" branch below it picks the
  next segment of the **day**, not of the selection, so it starts playing footage entirely
  outside the selection (the first frame trips the upper bound and stops again). What's seen: a
  flash of unrelated video just before "selection finished", which reads as the wrong segment
  having been picked. `PlayFrom` clears `_rangeStopUtc` (it belongs to the previous operation),
  so the continuation has to restore it after the call, or the replay runs to the end of the
  day. Guarded by `區間重播跨段會接著播下一段並保留結束上界` and
  `區間播完不會接著播選取範圍外的一段`. The replay also has to *start* inside the selection:
  `OnReplayRangeClicked` picks the first segment intersecting the half-open range
  (`StartUtc < range.EndUtc && EndOf(s) > range.StartUtc`), not "the segment covering the start
  instant" — the start may sit in a gap (old closed-interval match refused the whole replay
  despite 25 minutes of recording inside it) or exactly at an end (played the previous segment's
  length → instant `已播放完畢`). Guarded by `重播選取範圍會從範圍內第一段開播`.
- **The reused playback window must re-read the channel list.** It is one window reused by the
  main app, and `ChannelCombo.Items` was snapshotted when it opened. Switching by membership in
  that list rejected cameras added since: dragging one in from live view did nothing, no error,
  no log. `SwitchChannelAsync` now asks `ChannelExists` (repository query, falling back to the
  current items on a DB error). The focus-time open path also reports
  `Playback.NoRecordingHere` instead of leaving a black player when the timestamp falls in a
  gap. Guarded by `開窗後才加入的頻道也能拖進回放`.
- **A `tmp` row whose files are both gone must not stay `tmp` forever.** `TemporarySegmentRecovery`
  used to skip it ("disk may be offline") on every run, so the backlog grew without bound and
  ate the batch limit; the live row it was supposed to rescue never got a turn. It now closes
  the row as `missing` when the volume root still exists — the same verdict
  `SegmentIndexReconciler.IsVolumeReady` uses — and leaves it alone when it does not. Do not
  shorten that to "file missing": an unplugged D: looks identical. Also do not drop the
  `MinimumIdle` guard there; `BeginSegment` writes the row before ffmpeg creates the `.tmp`,
  and condemning it in that window makes the later `CompleteSegment` (which requires `tmp`)
  update nothing. Guarded by `磁碟還在但兩個檔案都不在時會收斂成missing`,
  `磁碟不在時兩個檔案都找不到會留著等下一輪而不標成corrupt` and
  `剛開始錄而暫存檔還沒出現時不會被判成missing`.
- **A tiny control panel must not be `WindowState="Maximized"`.** `PtzWindow` is a 220x340
  always-on-top panel (direction keys, zoom, presets) meant to sit next to the picture; the
  XAML said `Maximized`, so choosing PTZ blanked the whole screen with an almost empty panel
  and looked like a crash. It compiles silently. Guarded by `PTZ面板不會以最大化開啟`.
- **A settings tab taller than the window is silently unclickable.** `SettingsWindow` is
  `WindowState="Maximized"` with `ResizeMode="NoResize"`, and every tab is a `StackPanel` with
  no scroll of its own. Storage and Notifications are taller than the usable height of a
  1366x768 screen, so WPF clipped the bottom of the page at the window edge — the last row of
  「套用」/「清理」 buttons could be seen but never clicked, with no scrollbar, no error and no
  hint that anything was missing; the operator's only workaround was changing the display
  resolution. The content column is now wrapped in `SettingsContentScroll`
  (`VerticalScrollBarVisibility="Auto"`). Three things make that wrapping safe and must stay:
  no `Height="*"` row inside it (a ScrollViewer measures with infinite height, so a star row
  collapses to 0 and the whole page vanishes), `HorizontalScrollBarVisibility="Disabled"`
  (enabling it measures the page with infinite width, so `TextWrapping` never wraps and the
  page becomes one long line), and the left navigation stays outside (inside, it scrolls away
  with the content). Guarded by `SettingsLayoutContractTests`.
- **A borderless maximized window must ship its own way out.** `WindowStyle="None"` +
  `WindowState="Maximized"` removes the system title bar, so whatever the window is, it needs a
  visible ✕ in the page (same `OnCloseClicked` pattern as Live/EventCenter) and Esc wired to
  `Close()` — `SchedulingWindow` had neither, and the only exit was Alt+F4, which operators read
  as "this window has no exit" (現場回報「沒有離開的按鈕」). Note for the contract tests:
  `SourceContract.BodyOf` returns an expression-bodied member *without* the trailing `;`, so
  assert `Close()`, not `Close();`, on `=> Close();` handlers. Guarded by
  `SchedulingWindowContractTests` and the window-wide `UiWiringContractTests`. The same rule now
  covers the export wizard (`ExportWindowContractTests`): it was a 640×560 dialog and is a
  borderless maximized two-column window, so it needed the header ✕, the labeled
  關閉 button (`IsCancel`) *and* Esc, plus `OnClosing` canceling the export CTS — closing
  mid-export must cancel ffmpeg, not orphan it. Fixed-width inputs (`Width="280"` on the channel
  combo, `"180"` on the bundle fields) clipped translated labels and long camera names;
  fullscreen form fields stretch instead, and the time range is a labelled 2×2 From/To grid
  rather than four controls sharing one row. The rule now covers every borderless maximized
  window in the app (21 of them: the settings-centre children — detection, redaction, dewarp,
  stream switch, IO, talk, camera/channel manager, monitor group, recording import, notification
  log, ONVIF wizard, map, PTZ — plus scheduling/export/export centre, the settings centre
  itself, image search, the visual-search ROI picker and the second-screen mirror wall),
  each carrying the same red ✕ at top-right plus Esc. Six of them had no close
  affordance at all and six had only a text 關閉 button; both read as "this window has no exit"
  once the title bar is gone. Guarded by `WindowExitContractTests` (table of 21 windows; each
  row reads the XAML for the ✕ and the code-behind for Esc).
- **Space governance is "keep N GB free", not "recording quota".** The Storage page used to say
  「錄影保留上限（GB）」 while `RetentionService.Apply` looped `while (usage > quotaBytes)` — set it
  to 0, or below one segment, and a single hourly sweep deleted *every* recording while the UI
  said 已套用 and the log stayed clean. It is now a free-space reserve
  (`recording.min_free_bytes`, default 2 GiB) enforced by `ApplyPressure`, which only acts when a
  drive actually drops below the threshold. `Apply`'s quota path is disabled by
  `if (quotaBytes > 0)` — **keep that guard**: `RetentionCoordinator` and `MainWindow` pass 0, and
  `>= 0` turns "disabled" into "delete everything". `recording.quota_gb` may still sit in upgraded
  databases; nothing reads it any more. Tests that need "nothing will ever fit" use
  `quotaBytes: 1`, never 0 — 0 now means disabled, so such a test quietly goes green by deleting
  nothing. **The disk-full gate belongs on every recording start point, not just the scheduler's.**
  Automatic recording (Always/Scheduled/Event) asks `DiskPressureCoordinator.ShouldStopNewRecording`
  through the scheduler's injected `diskFull` delegate, but the wall's manual per-pane record button
  (`ChannelSession.SetRecordingAsync`) only asked the license gate until 2026-11-11 — so pressing
  record on a full disk built a recorder whose every segment failed while `IsRecording` stayed true
  (`_loop is { IsCompleted: false }`), leaving the REC badge lit and nothing in the picture but one
  `recording.stalled` audit row. The block returns a `RecordingGateResult` with the new
  `DiskFull = true` flag (`Decision` stays `Valid` on purpose) so `LicenseLimitNotifier` does not file
  it as a licence-limit event — an operator sent to check a perfectly good licence is the wrong
  investigation. Guarded by `磁碟滿時每個開錄點都要擋`, which matches the **`if (…)` call shape** per
  start point, not a bare string: the first version matched the string and stayed green after the
  whole `if` block was deleted, because the explanatory comment above it contained the same text.
  Guarded by `SettingsReserveSpaceContractTests`,
  `總量配額停用時不會汰除任何區段`, `RunConfigured會依磁碟保留空間做壓力汰除` and
  `RunConfigured會依共用設定解析根目錄並清理過期快照`.
- **"How long can this disk record?" has three answers, and two of them are not a number.**
  `RecordingCapacity`／`RecordingCapacityService`（設定中心「儲存」頁與
  `GET /api/recording/capacity` 共用）：`known`（有樣本）／`at_reserve`（可用空間已低於
  `recording.min_free_bytes`，真的是 0）／`unknown`（最近一個量測窗沒有寫入，顯示「—」）。
  把 `unknown` 印成 0 會讓「錄影根本沒在跑」看起來像「磁碟滿了」——兩者的處置完全相反，
  所以逐顆清單也是「—」而不是 0。三個不要改回去的地方：(1) 速率用**實測寫入量**，
  不用名目碼率；(2) 速率的分母是 `MeasurementBasis`（最早段起點到現在，夾在
  `MinimumBasis`＝10 分鐘與量測窗之間），半小時前才開始錄的機器若除以一小時，
  速率會少一半、剩餘時間會變兩倍；(3) 寫入量只有 `RecordingVolume.WrittenBytes` 一份實作
  （壓力汰除與剩餘時間共用）——兩份會讓同一顆碟在兩個畫面上出現兩個數字。
  時長一律向下取整（`Parts`）。Guarded by `RecordingCapacityTests`,
  `RecordingCapacityServiceTests`, `RecordingCapacityApiTests`,
  `RecordingCapacityContractTests` and `寫入量只有一份實作`.
  逐項對照與參考實作的容量公式見 `docs/STORAGE-ALIGNMENT.md`（§5.1）。
- **"Disk is full" and "disk is dying" are different failures — keep them separate, and never
  render *unknown* as *healthy*.** `DiskHealth`／`DiskHealthCoordinator`（`HeliVMS.Storage`）
  answers the second one: ATA attribute parsing (12 bytes × 30 slots; attribute 5 reallocated,
  197 pending, 194 temperature, 9 power-on hours), level `Failing`／`Warning`／`Healthy`／
  `Unknown`, `Worst` ranks Unknown lowest so a disk nobody can read does not mask a failing one.
  `Failing` means only two things: the controller predicts failure, or pending sectors ≥ 1
  (reads already failing, data is leaving). Reallocated sectors or ≥55 °C are `Warning`.
  `Unknown` (not admin, controller hides SMART, RAID/USB bridge) must stay its own UI state —
  printing it as healthy is the expensive silent failure, because the screen stays green while
  the disk remaps sectors. The WMI reader (`WmiDiskHealthSource`) fails open at every step
  (volume → partition → physical disk → `root\wmi` `MSFailurePredictStatus`/`ATAPISmartData`
  matched by `PNPDeviceID`), the coordinator audits **only on level change** (a failing disk
  keeps the same level for days; hourly rows would drown the audit log) and stores its state via
  a raw `app_settings` upsert, not `SettingsRepository.Set` (that one records `settings.set`,
  which is for human edits). Both call sites matter: the desktop hourly loop and the Agent's
  `RetentionWorker.RunOnce`. The reference NVR has a SMART page but its sampling mechanism has
  **no evidence** in the reverse-engineering corpus (`docs/STORAGE-ALIGNMENT.md` §5.3) — this was
  built on documented Windows APIs, not protocol alignment. Guarded by `DiskHealthTests`,
  `DiskHealthCoordinatorTests` and `DiskHealthContractTests`.
- **The second-screen mirror wall filters frames by channel, not by cell — and it must be fed
  from the decode thread.** `WallMirrorWindow` can be pinned to a monitor group (`wall.mirror.group`)
  so the second screen keeps showing one set of cameras while the main wall pages elsewhere;
  those channels often have `Cell = -1` (unmounted from the wall but still decoding for
  recording), so the forward in `MainWindow.OnCellFrame` has to happen **before** the
  `cell is < 0 or >= MaxCells` guard — after it, the pinned group is a black screen with a clean
  log. Because that forward now runs on the decode thread, the mirror only stores the newest
  frame per cell and schedules the draw on the UI thread (`PostFrame` → `RenderCell`); writing
  `WriteableBitmap.WritePixels` from `PostFrame` throws "The calling thread cannot access this
  object", and rendering in `RenderCellFrame` again would double-draw and re-break the -1 cells.
  Cell assignment and badges both come from `MirrorChannels()` (one source of truth), and
  `ReloadGroupTabs` re-sends the source list so a renamed/deleted group cannot leave the second
  screen pointing at a name that no longer exists. Guarded by `WallMirrorContractTests`.
- **Event recording is gated by a time window, and the gate must be re-evaluated immediately.**
  `RecordingMode.Event` records only while the channel had an event within
  `recording.event_seconds` (default 30 s): `RegisterEvent` stamps `_eventTriggers[channel]`,
  `ShouldRecordEvent` compares against `now`, and the 30 s reconcile tick stops the recorder once
  the window lapses. There is no pre-buffer on purpose — a pre-roll would need one live decode
  pipeline per event-mode channel, which is the same RTSP-session budget that made audio ride the
  decode process instead of opening a second session. `RegisterEvent` also kicks
  `_timer.Change(TimeSpan.Zero, ReconcileInterval)`, otherwise an event on an idle channel waits up
  to 30 s for the recorder to start, i.e. the footage that triggered it is already gone. Every
  test that expects recording to actually start must call `ApplyLicense(cameras: 32)` first;
  without it `LicenseService.CheckRecording` blocks Event mode and the assertion passes for a
  reason that has nothing to do with the gate. Guarded by `事件錄影頻道登記事件後調和會開錄`,
  `事件窗口過後調和會停錄`, `連續事件刷新窗口不會切段` and `非事件模式的頻道忽略事件觸發`.
- **The wall's recording-mode dot: the colour is the mode, the brightness is "recording right now".**
  Every pane carries an 11 px dot in its top-right corner (created in `RebuildCells`, refreshed by
  `UpdateBadges` on the same 1 s cadence as the name badge): red = always-on, blue = schedule,
  green = event/motion, grey = manual. The palette lives in `HeliVMS.Shared.WallModeDotPalette`
  because the legend is what the operator memorises; the desktop maps those constants to brushes
  and must not keep a second colour table. Full opacity = a recorder is really running; 0.35 = the
  mode simply is not recording at the moment (outside schedule, event not triggered, manual waiting
  for the button). That distinction is the whole point: "should be recording but isn't" and "not
  supposed to be recording" used to look identical on the wall, and the mode could only be read by
  opening Settings → Channel management. The colour is never the only channel — the bottom-left
  badge reads `名稱 · 全時 ●REC（排程外）` (text from `WallBadgeTextPlanner`, reasons from
  `BlockLabel`, which drops "僅事件錄影"/"僅手動錄影" when the mode label already says it) and both
  tooltips spell the mode out. The wall-level REC chip is **centred**, not right-aligned: parked on
  the top-right corner it would cover the dot of exactly the pane underneath it. Guarded by
  `WallModeDotPaletteTests`, `WallBadgeTextPlannerTests` and
  `MonitorWallRecordingModeContractTests`.
- **An unreadable export time must be refused, never silently widened to a whole day — and the export
  must be trimmed to the requested range.** 現場回報：匯出精靈設 11:04:07.25～11:04:07.30（5 秒），
  匯出的檔案卻有 50 分鐘。兩件事疊在一起：(1) `ExportWindow` 用 `TimeSpan.TryParse` 讀時刻，
  讀不懂時**默默**把起訖當成 00:00–23:59:59，所以操作員打成 `11040725`（＝11:04:07.25，與分段檔名
  `seg-HHmmssfff.mp4` 同一種寫法）時，整天錄影都被併進成品；(2) `ExportService` 從不裁切，
  整段整段地 concat，就算時刻讀對了，「匯出 5 秒」也會拿到整段 60 秒（前面多 7 秒、後面多 48 秒）。
  修正：`HeliVMS.Shared.ClockTime` 讀得懂 `HH:mm`／`HH:mm:ss`／`HH:mm:ss.fff` 與緊湊寫法
  `HHmm`／`HHmmss`／`HHmmssff`／`HHmmssfff`（讀不懂就擋下來並說出哪個字串讀不懂）；
  `ExportService` 以輸入端的 **`-ss`（要求起點距第一段起點的秒數，`TrimStartSeconds`）＋ `-t`
  （各段交集總和，`TrimmedDurationSeconds`）** 裁切，`-ss` 必須在 `-i` 之前。**不要改用 concat
  demuxer 的 `inpoint`／`outpoint`**：實測（真實錄影檔）這個 ffmpeg 版本 `inpoint` 完全不生效
  （整段 15 秒寫 `inpoint 5` 仍輸出 15 秒），`outpoint` 才有；用字串比對的單元測試會全綠，
  而匯出仍然整段——這種「測試在測實作細節、成品還是壞的」是最貴的一種假綠。
  整體 `-ss`／`-t` 在有缺口時依然正確：concat 後缺口被壓掉，而 `-t` 算的是實際內容秒數，
  所以逐段消耗的交集總和剛好等於 `TrimmedDurationSeconds`（實測 A 段後 5.06 秒＋缺口 8 秒＋B 段前 5 秒
  → 輸出 10.07 秒）。回放帶入的區間也改寫到秒（`HH:mm` 會讓兩端各伸縮最多 59 秒）。
  Guarded by `ClockTimeTests`, `ExportTrimTests` and
  `時刻讀不懂會擋下來而不是默默匯出一整天`；裁切機制本身是拿真實錄影檔跑 ffmpeg 驗過的。
- **Adding cameras must not require typing one IP at a time.** The ONVIF wizard's address box is a
  *search expression*: a single IP/host, a CIDR (`192.168.1.0/24`), a last-octet range
  (`192.168.1.1-50`), a full range, or a comma/space separated mix
  (`HeliVMS.Devices.Onvif.DiscoveryAddressRange`; pure, capped at 512 addresses and it *reports* the
  truncation — a /16 scan would otherwise look like "I scanned everything and found nothing"). Two
  traps there: a token containing `/` is only a CIDR when the left side parses as IPv4
  (`http://cam/onvif/device_service` is a host, not a subnet), and a typo'd prefix
  (`192.168.1.0/99`) must be dropped, not silently probed as a literal host name.
  `DiscoverySweep` runs the probes in parallel (16, hard cap 32 — a hundred simultaneous ONVIF
  connections starve the cameras' RTSP/replay sessions) and `DiscoveryClient.ProbeAddressAsync` is
  the cheap per-address probe (700 ms unicast WS-Discovery, then `/onvif/device_service` on
  80/8000/8080/2020 with a 2 s timeout — **not** `DiscoverByIpAsync`, whose 42 candidates × 10 s
  timeout turns a /24 into an overnight job); a 401 counts as *found*, because the device is there
  and only its credentials are missing. Discovered devices are listed with checkboxes and added in
  one go: `OnvifEnrollmentPlanner` resolves each device sequentially (main/sub stream, no resolution
  probing — that would add 45 s of ffprobe per camera), one failure never aborts the batch, and the
  wizard hands the caller a single list (`Candidates`) so the single-camera and batch paths share
  one naming/enrollment loop. Two traps inside discovery itself: (1) **multicast must be sent from
  every interface** (`MulticastSourceAddresses`: only Up + multicast-capable IPv4 adapters, loopback
  only when there is nothing else, `IPAddress.Any` as the last resort) — an unbound socket leaves via
  the default route only, so a two-NIC NVR silently finds just one side's cameras; results are merged
  and de-duplicated through `OnvifDiscoveryIdentity` (device UUID first, XAddr as fallback), the same
  identity the sweep uses. (2) **every swept address gets a TCP pre-scan first**
  (`OnvifPortScan`, 300 ms per port) and only the listening ports get an ONVIF call: an address with
  nothing on it then costs one connect timeout instead of two seconds per port, which is also what
  makes the port list safe to widen to `OnvifEndpointResolver.CommonPorts` (8899/34567/5000 would
  otherwise never be found). The pre-scan is only a filter — TCP answering is not proof of ONVIF —
  and a timeout counts as "nothing there" while only a real cancellation propagates.
  Guarded by `DiscoveryAddressRangeTests`, `DiscoverySweepTests`, `MulticastSourceAddressesTests`,
  `OnvifPortScanTests`, `OnvifEnrollmentPlannerTests` and `OnvifBatchDiscoveryContractTests`.
  (3) **the wizard prefills credentials from the fleet** (`OnvifCredentialPlanner`, pure): the
  address the operator typed wins when it names a device we already have (most specific signal),
  otherwise the fleet's most common username+password pair; a *remembered* username that differs
  from the fleet's keeps its own field but gets **no password**, because a wrong password is masked
  and invisible while an empty one is obvious. Verified on the live index: 7 VIVOTEK cameras are
  enrolled as `root` and the 2 LAN test devices as `admin`, so a hardcoded "usually admin" default
  would have been wrong for almost the whole fleet (the tooltip said exactly that and now describes
  the real behaviour). Parsing the address box is where this goes wrong quietly: strip the URL
  scheme *before* deciding it is an expression (`rtsp://cam/live` is a camera, `192.168.1.0/24` is
  a subnet) and only treat a hyphen as a last-octet range when numbers sit on both sides
  (`192.168.1.1-50` is a range, `vivotek-01.local` is a hostname).
  Guarded by `OnvifCredentialPlannerTests` and `開窗預填上次搜尋且已加入的預設不勾`.
- **The WS-Discovery probe is a SOAP message, not the bare `<Probe>` element — and the fake camera in
  tests must be strict enough to reject anything else.** 多播搜尋在現場從未成功（都以手動 IP＋HTTP
  探測註冊），根因是 `DiscoveryClient` 送出的是**裸元素**、命名空間 2009/01 配 2005/04 的 Action，
  而測試的假設備收到什麼都回，所以從未抓到。線路格式依 ONVIF 核心規範 §5.1：SOAP 1.2 封套、
  WS-Discovery 1.0（**2005/04**）、WS-Addressing **2004/08**、`wsa:To`＝
  `urn:schemas-xmlsoap-org:ws:2005:04:discovery`、`Types`＝`dn:NetworkVideoTransmitter` 且
  Types 元素要**繫結 `dn` 前綴**（它是 QName，嚴格解析的設備找不到前綴就忽略）。三個不要改回去的地方：
  (1) **單播探測的 `To` 也是探索 URN**，不是目標位址——gsoap 的 directed probe（`SOAP_WSDD_TO_TS`）
  就是這樣送，用位址會被部分設備的 WS-Addressing 檢查擋掉；(2) **解析端要 local-name 無關命名空間**
  （`EndpointReference` 有設備放 `a:`、有設備放 `d:`；回應同時有 1.0 與 1.1 兩種），寫死 2009/01
  會讓 1.0 設備的回應整批消失——徵狀是「掃描跑完、清單是空的」；(3) **探測要重送**（每 1 秒直到逾時）：
  UDP 沒有重傳，5 秒掃描只送一個封包在 Wi-Fi 上等於賭運氣。守門測試是嚴格假攝影機
  （`嚴格假攝影機只回應合格的SOAP探測`：只回應合格探測，改壞驗證含「完全重現原始缺陷＝拿掉封套」）、
  `ParseMatch_ONVIF200504的回應也會解析` 與 `探測會重送而不是只送一次`。
  另外，真機驗證（現場 VIVOTEK，公網單播）還抓到一個只有實機才會出現的形狀：名稱 scope 是**百分比編碼**
  （`onvif://www.onvif.org/name/VIVOTEK%20IP9164-HT`），`DiscoveredDevice.NameHint` 若不解碼，
  精靈清單顯示的是「%20」而不是空白——用 `Uri.UnescapeDataString`（不是表單解碼，「+」不轉空白），
  且 `XAddrs` 常有兩個（公網＋link-local），`HttpXAddr` 取第一個 http 條目。
  Guarded by `ParseMatch_實機VIVOTEK回應的名稱會解碼且XAddr取第一個`。
- **A group tab with fewer cameras than cells must leave the extra cells empty — and both tab paths
  must share one implementation.** 現場回報：群組只設 2 支攝影機卻用 4 分割，在不同群組之間切換後
  再回到原群組，第 3、4 格「串」著另一支攝影機的畫面。三個獨立原因疊在一起：
  (1) `OnLayoutChanged`（兩個頁簽記住的格數不同時走的那條）自己實作了一次改派，從來沒呼叫
  `ClearCellsBeyondChannels`，於是 `RebuildCells → ApplyCellStateAfterRebuild` 把上一頁簽留在
  `_bitmap`／`_cellChannel`／`_cellStates` 的殘影重新投影到新格子上（那不是佔位圖，每秒重畫也蓋不掉）；
  (2) `ReconcileAsync` 的卸下條件是「未指派且 `Cell < count`」，前一頁簽 16 分割的工作階段格位編號
  4 以上會被放過，繼續把影格送進一個「現在看不到、日後會被別人接手」的格位；
  (3) 影格事件只帶格位、不帶頻道，畫面端無從判斷「這張影格是不是這一格的」。
  修正：`RebuildCells` 結尾一律 `ClearCellsBeyondChannels(WallChannels().Count)`；
  `OnLayoutChanged` 改走 `ApplyWallAsync(startIndex)`（切頁簽與改佈局只剩一個入口）；
  卸下條件就是 `Cell >= 0`；影格帶上頻道 id（`FrameArrived` → `_pendingFrameChannel` →
  `RenderCellFrame` 比對 `_cellChannel[cell]`）。Guarded by `改佈局也會清掉群組看不到的格子`,
  `影格必須屬於該格現在的頻道才畫` and `未被指派的工作階段一律移出畫面格位`.
- **A recording-schedule rule that never records must be rejected, not stored.** `IsActiveAt`
  uses a closed interval, so start == end (e.g. the old form default 00:00–00:00) is active for
  exactly one minute of the day; the scheduler ticks every 30 s and in practice captures
  nothing — a rule that silently does nothing, with no error anywhere. `BuildFromForm` now
  refuses start == end (`Schedule.SameTime`) and the form defaults to 00:00–23:59 so the most
  common action ("pick a channel, add an all-day rule") is correct by default. Guarded by
  `起訖相同時段會被擋下而不會存成不會錄的規則` and `表單預設時段是一整天`.
- **Hourly merge rewrites files — the delete must come after the index swap, never before.**
  Opt-in (`recording.hourly_merge`, Settings → Storage) consolidation packs finished hours into a
  single `merged-HHmmssfff.mp4` (remux `-c copy`, never re-encode) to cut the ~288-files-per-
  camera-day tail for backups, sync, AV scans and SMB. The order in `HourlySegmentMerger` is
  load-bearing: remux to `.tmp` → ffprobe the duration against the sum of the segments (a short
  file must never replace the index: playback would black-screen the missing span) → rename →
  one SQLite transaction (`ReplaceWithMerged`, DELETE-then-INSERT because of the
  `(channel_id, stream, start_time)` unique key) → only then delete the originals. Leftover
  small files after a failed delete are garbage-collected on the next sweep (they are no longer
  in the index and the disk importer would re-adopt them as phantom segments). Legal-held hours,
  hours with missing files, and single-segment hours are skipped. The disk importer must keep
  understanding the `merged-` prefix (same time encoding as `seg-`), and its written-time
  duration inference is WRONG for merged files (the write time is when the remux finished, not
  content end) — they are ffprobe-probed instead. Guarded by `HourlySegmentMergerTests`,
  the importer test `小時合併檔也會被掃描認得並以探測的時長建列`, `HourlyMergeContractTests`
  and the coordinator cases in `RetentionCoordinatorTests`.
  **A partially merged hour must not be declared done by its first row, and the hash read must not
  keep the new file open.** Two failures measured on the live index. (1) `MergeOne` used to derive
  "this hour is already merged" from `rows[0]`'s filename; `ListFinalBetween(from, now−1h)` only
  returns what was `final` at that moment, so segments that finished *after* the merge (a
  playback-triggered cut, a reconnect) could never enter it — the sweep saw `merged-*` first and
  went straight to `CleanLeftovers`, forever. Measured: 7 folders keep 2–3 stray `seg-*` next to a
  merged file (e.g. `攝影機01\20261107\23`: merged for 2316 s, then `seg-235445787` and
  `seg-235948449`), i.e. the whole point of merging (fewer small files) is lost there. The hour is
  now split into `alreadyMerged` (rows whose path starts `merged-`, `IsMergedFile`) and `todo`;
  `todo.Count < 2` is the only "nothing to do" test, and everything after it works on `todo`.
  A later merge therefore lands as a **second** merged file in the same hour (`merged-<later
  start>`) rather than rewriting the first — deliberately crash-safe: re-remuxing the indexed file
  would mean deleting a file the index still points at before the swap. (2)
  `SHA256.HashData(File.OpenRead(path))` opens a `FileStream` nobody disposes, so the freshly
  produced 60 MB file stayed open (`FileShare.Read`): the failure path's `TryDelete(mergedPath)`
  could never delete it, and retention (which only knows index rows) could never evict it. Live
  evidence: `攝影機05\20261110\17\merged-170348710.mp4` (63.9 MB) on disk with no row, the same
  hour's 12 `seg-*` rows still `final` with their files present — the app was closed at 18:29:26
  (`%TEMP%\helivms-shutdown.log`) mid-swap. The stream is now disposed (`using`). Related guard:
  a same-named file on disk is only treated as an orphan when **no** row (any status) references
  it — a `missing` row whose file came back must not be deleted and re-made. Guarded by
  `已經合併過的子區間不會擋住同一小時後半段的合併`,
  `合併完成後檔案沒有被雜湊讀取留在開啟狀態`, `換列失敗時不會留下刪不掉的合併檔` and
  `同名的合併檔已被索引指著時不會被刪掉重做`.
  **Duration validation must measure the inputs' own lengths, and a failure must say which hour
  and why.** `segments.duration_sec` was a wall clock (how long the recorder process ran) before the
  file-derived-length work, and it can be 3–22 % larger than the file — measured on the live index,
  **33 of 59 mergeable hours** failed the old check while the output was complete (e.g.
  `攝影機05\20261109\02`: index sum 3505.231 s vs the 14 files' own 3065.386 s, output 3065.386 s).
  Validating only against the index therefore means measuring a correct output with a ruler known to
  lie, and those hours can **never** merge, with a clean log. The order in `MergeOne` is now: compare
  the output against the index (cheap, one probe, correct in the normal case) → on mismatch measure
  every input with `TrySumInputSeconds` → compare again against that sum (a real content loss is
  still refused). Do not "simplify" this into always probing every input: that is N ffprobes per
  hour for every sweep. The merged row's `DurationSec` and `EndUtc` come from the **merged file's own
  length** (`contentSeconds`, `end = start + contentSeconds`), never from the old rows' wall-clock
  span — copying the old value into the new row is exactly how the error reaches the timeline, the
  arrow keys and multi-pane sync. Failure reporting is typed, not a counter: `HourlyMergeReport`
  carries `Failures` (`HourlyMergeFailure`: folder, hour, `MergeFailureReason`
  — `RemuxFailed`/`DurationProbeFailed`/`DurationMismatch`/`HashFailed`/`IndexSwapFailed`/
  `OrphanDeleteFailed`, each with a different remedy — and a detail string), the desktop hint says
  `Main.HourlyMergeFailed` and names one folder via `ShortFolderLabel` (`頻道\日期\小時`; the full
  path would blow up the hint bar), and the headless Agent logs a `LogWarning` naming the first
  failing folder. Per-failure detail goes to **`%TEMP%\helivms-merge.log`** (256 KB,
  `BoundedFileLog`), deliberately **not** to `audit_log`: the same hour can fail every sweep, i.e. 24
  rows a day, which would bury the `recording.stalled`/`recording.disk_full` rows that actually
  matter (the DI-audit flooding lesson). Guarded by `長度驗證不過時不會換列`,
  `索引段長比檔案長時仍會合併且新列長度取檔案自己的長度`,
  `合併失敗會記錄是哪一小時與為什麼`, `清理輪合併失敗會在主畫面說明並指出資料夾` and
  `Agent的保留清理會把合併失敗寫進log`. Contract-test trap (hit for real this round): the Agent
  fact first asserted the source contains 「小時合併失敗」 — the explanatory comment above the log
  line contains the same characters, so deleting the whole `LogWarning` kept it green. It now pins
  the message template (with the `{Failed}` placeholder) and `mergeFailures[0].FolderPath`.
- **A `corrupt` row must say why it failed — and a revived row must not keep accusing the file.**
  The live index had 207 `corrupt` rows (ch107 alone 118) with no cause anywhere in the row: the
  timeline shows a hole, the operator's only recourse was matching `recording.stalled` rows out of
  21k+ audit entries by timestamp. `segments.failure_reason` (TEXT, additive column in
  `EnsureAdditiveColumns` — no schema bump, the SchemaDrift lesson) now carries one redacted line:
  `SegmentRecorder` composes it via `SummarizeFailure` (human diagnosis first — exception first
  line / 收尾逾時; then ffmpeg stderr's **last** non-empty line — the fatal error is at the tail,
  the input-URL echo at the head; then exit code; then "no output"), `TemporarySegmentRecovery`
  writes "not MP4" / "no file path", and `SegmentRepository.MarkCorrupt(id, reason)` normalizes
  through `NormalizeFailureReason`: **redact credentials → flatten newlines → truncate to 200**, in
  that order, because ffmpeg's stderr always echoes `rtsp://user:pass@host` and truncation before
  redaction can cut the URL so the `@` is gone and the userinfo regex no longer matches — a stored
  password. A later generic `MarkCorrupt(id)` must **not** wipe an earlier specific reason
  (`COALESCE`); an explicit new reason overwrites. The flip side: a row that becomes playable again
  must drop the reason — `CompleteSegment` (rescue of a half-finished finalize) and
  `AdoptOrReviveFinal` (disk import revive) both set `failure_reason = NULL`, otherwise every future
  "show why this failed" surface accuses a healthy file. Playback: `LoadSegments` counts
  `SegmentStatus.Corrupt` rows (distinct from `missing` = evicted) into the hint
  (`Playback.FailedSegments`, 3 languages) and puts the per-row `HH:mm:ss  原因` lines into
  `LoadHint.ToolTip` — the reasons can be 200 chars, they would push the segment count out of the
  one-line hint bar; the tooltip is cleared when the day has no failed segments and in
  `ShowEmptyState`, or it keeps describing yesterday. Guarded by
  `失敗原因會連同損壞狀態一起寫進索引`, `失敗原因會遮掉帳密壓成單行並截斷`,
  `沒有新原因的損壞標記不會抹掉既有原因`, `收尾成功後不再留著失敗原因`,
  `復活成final的段不再留著失敗原因`, `錄影失敗標成corrupt時會留下遮掉帳密的失敗原因`,
  `失敗原因摘要優先取人的診斷再取stderr尾行`, `開頭不是MP4而判死時會留下失敗原因` and
  `回放提示會說明錄影失敗的段數並把原因放進工具提示`.
- **The playback finalize log must name the failing recording path — a bare exception line
  is unfollowable.** Field evidence: `%TEMP%\helivms-playback.log` accumulated 164 entries of
  「回放前收檔失敗：TaskCanceledException」 between 10-30 and 11-12 — one per playback open,
  i.e. the "cut the segment being recorded" feature never once succeeded in that window. The
  stack is a single frame (`RunQuietlyAsync` line 76) because the exception is a *canceled
  task* being awaited, not a throw: HEAD's `CutAsync` provably cannot produce it (it waits with
  `WaitAsync(timeout)` → `TimeoutException` → caught → false; verified by experiment and by
  `ffmpeg不理收尾請求時收檔逾時回false而不是TaskCanceledException`), so the running instance
  was most likely a stale pre-commit build where the cut waited on a `CancellationTokenSource`
  (canceled waiter → `WhenAll` → canceled source task → exactly this signature). Two things
  shipped so the next occurrence is decidable in one glance instead of a whole-chain audit:
  (1) `PlaybackRecordingFinalizer` sources carry **names** (`("監看牆", …)`, `("排程", …)`) and
  the failure log line is `回放前收檔失敗（{來源}）：{ex}` — a bare TCE with no name is exactly
  the 164-entry dead end; (2) the cut-timeout shape is pinned by a regression test so nobody
  reintroduces a CTS-based wait whose timeouts would resurface as anonymous `TaskCanceledException`
  lines. The swallowing behavior itself is deliberate and stays: a failed cut must not block
  opening playback (the operator just loses the newest segment from the list).
  Guarded by `一個來源失敗不會吞掉另一個來源收到的段且會留下日誌` (asserts the source name in
  the log), `來源被取消時回報零段而不是讓例外冒出來` and
  `ffmpeg不理收尾請求時收檔逾時回false而不是TaskCanceledException`.
- **A woken instance must identify itself — "restart" often isn't.** The single-instance wake
  (`BringToFront(maximize: true)`) makes the old window fill the screen exactly like a fresh
  launch, and the second process exits silently: the operator has no way to tell whether their
  update took effect. The 164-entry finalize-failure log proves a 10-30-era process can survive
  into 11-12 this way (close-to-tray + wake keeps the *original* process; the splash/login
  relaunch path too). `MainWindow.BringToFront(maximize: true)` now shows a tray balloon
  (`TrayIconHost.ShowBalloon`, `Main.WakeTitle`/`Main.WakeBody`, 3 languages) with **this
  process's start time** — an instance running for days is visible at a glance, and the text
  says "if you just updated, this is still the old build: exit from the tray and start again".
  No start time (near-impossible) → no balloon: an info-free hint is worse than no hint. The
  tray-restore path (`maximize: false`) deliberately stays quiet — the operator initiated it.
  Balloon failures (RDP sessions without a notification centre) are swallowed; waking must
  never depend on the toast. Guarded by `被喚醒的舊實例會用氣泡亮出自己的啟動時刻`.
- **PTZ capability resolution is cached across service instances — per-command re-resolution
  is a measured joystick-latency tax.** Field data (`%TEMP%\helivms-onvif-ptz.log`, now
  bounded at 512 KB — it had grown to 1.7 MB unbounded): 2,328 `resolve begin` entries in one
  day, each costing 2–4 ONVIF HTTP round trips (GetCapabilities + GetNodes verification),
  because every Web API PTZ command and every patrol step constructs a fresh
  `OnvifDeviceService` and the resolved `PtzXAddr` lived only on the instance. On the
  public-IP camera each extra round trip is 0.5–1 s — the joystick felt broken through no
  fault of the motion code. `EnsurePtzCapabilityAsync` now consults a process-wide cache
  (key = device XAddr, TTL 10 min via `PtzCapabilityTtl`) that stores **the chosen WSDL
  namespace alongside the XAddr** — a ver20-only camera's namespace is discovered during
  verification, and caching only the XAddr would make every new instance fall back to ver10
  and fail wholesale (the VIVOTEK speed-dome lesson). The "no PTZ" verdict is cached too:
  unsupported is the steady state, re-probing it per command pays the same round trips for
  nothing. Tests pinning exact round-trip counts live in the `onvif-ptz-cache` collection
  (`DisableParallelization` — the static cache is process-global state; two classes
  resetting/resolving concurrently fail randomly), and every such class resets in its
  constructor. Known remaining per-command cost: one `GetProfiles` round trip when the
  caller supplies no profile token. Guarded by `第二個實例沿用快取的PTZ能力而不是重新解析`,
  `ver20設備的命名空間隨能力一起快取`, `不支援PTZ的判定也會被快取` and `快取逾期後會重新解析`.
- **Pick the sub stream by stream number, never by a magic number in the name.** `OnvifProfileSelection`
  used to carry `"101"` in `MainKeywords` and `"102"` in `SubKeywords`, matched as plain substrings.
  The real camera at `rtsp://<host>/media/stream.sdp?profile=Profile100` exposes three profiles —
  `Profile100` / `Profile101` / `Profile102` (串流 1/2/3) — so `101` marked stream 2 as *main* and
  `102` made stream 3 the only *sub*: adding a camera auto-filled the secondary URL with stream 3.
  Worse, `profile1` matched inside `Profile100`/`Profile102` for the same reason. `Classify` now
  groups by `VideoSourceToken`, and the **order** of trailing numbers under one prefix decides:
  smallest = main, second smallest = sub, the rest stay `Unknown` (a third stream must not become a
  second sub candidate, or `SelectSub` picks it again). Keywords are only consulted for profiles the
  numbering rule did not settle, and a keyword ending in a digit only matches when the following
  character is not a digit. Two-stream families (101/102, `Profile_1`/`Profile_2`) must keep the old
  result. Guarded by `三路編號的次碼流是第二路而不是第三路`,
  `兩路純編號仍以第一路為主第二路為子` and `純編號的token不會被關鍵字判成主或子碼流`.
- **Never write `async void` shutdown handlers.** WPF begins `Shutdown` as soon as the
  last window closes, so continuations after an `await` in `MainWindow.OnWindowClosed`
  never run: `StopAllAsync()` was silently dropped and `App.OnExit` hard-killed the
  children. Shutdown steps now run on the thread pool with a bounded wait
  (`RunShutdownStep`) and timeouts are logged to `%TEMP%\helivms-shutdown.log`.
  `App.OnExit`/`OnStartup` also sweep orphan `ffmpeg` **and** `ffprobe` processes
  (`OrphanChildNames`) — ffprobe leaks are what users see as a row of `FFPROBE.exe`.
  Guarded by `ShutdownContractTests`.
- **Never run shutdown cleanup synchronously on the UI thread.** `ChannelSession.Dispose`
  used to call `DisposeAsync().AsTask().GetAwaiter().GetResult()`, and `MainWindow` then
  called it directly from `Closed`. Every `await` inside the RTSP/recorder teardown posts
  its continuation back to the same Dispatcher that is blocked in `Wait()` — a permanent
  deadlock. What's seen: the window closes, `HeliVMS.exe` stays in Task Manager forever,
  and `index.db` is never closed (a `-wal` file survives, so a fresh process reads a stale
  index). Dispose steps now go through `ShutdownSequencer` (thread pool + timeout) and
  `ChannelManager.DisposeAsync`; a `ShutdownWatchdog` force-exits (logging first) after
  the total budget, so "closed" always means closed. Guarded by
  `ShutdownSequencerTests`.
- **The on-screen keyboard is `osk.exe` — refocus the target before launching it.** The keyboard
  call button on every input used to open a hand-built WPF keyboard panel (M292: a drag-guard typo
  made every key dead). The panel is gone; `Osk.Toggle` now refocuses the target input and
  launches the system `osk.exe` (single instance — a second launch just brings it to the front;
  `Enabled` only hides/shows the call buttons). Two traps: osk sends keys to whatever has focus,
  and clicking the WPF call button moves focus to the button — skipping the refocus makes osk
  type into the button; and a 32-bit process on 64-bit Windows has System32 redirected to
  SysWOW64 where osk.exe does not exist — `ResolveOskPath` must use the `Sysnative` alias there.
  A trimmed-down install may have no osk.exe at all: the launch failure is swallowed on purpose.
  Guarded by `OskContractTests` (the old panel/layout tests are gone with the panel).
- **`Application.MainWindow` is the splash, not the monitor wall.** `SplashWindow` is the first
  window created, so the property points at it (or goes null after it closes). Looking up the
  real window that way made the single-instance wake-up silently do nothing: a second launch
  exited, the first process got the event, cast `Application.MainWindow` to `MainWindow`,
  failed, and nobody noticed. `MainWindow.Current` is the reference; re-launching calls
  `BringToFront(maximize: true)` so a window hidden in the tray comes back maximized.
  Guarded by `SingleInstanceContractTests`.
- **Only the single-instance owner may sweep orphan children.** `App.OnExit` used to call
  `KillOrphanChildren()` unconditionally, but a *second* launch reaches `OnExit` too
  (`TryAcquire` fails → `Shutdown()`) — and its sweep killed every `ffmpeg`/`ffprobe` on the
  machine, including the **first instance's live decoders and recording writers**. The operator
  sees exactly "relaunch while minimized → window comes back full screen but every camera
  disconnects and reconnects", and the in-progress recording segment is marked corrupt
  (killed ffmpeg never renames the `.tmp`). The sweep exists to clean up children of a *dead*
  instance; while the mutex is held they are somebody's live children. `OnExit` now captures
  `ownedSingleInstance = _singleInstance is not null` before disposing the guard and sweeps
  only when it was the owner; `OnStartup` already swept after the second-instance early-return.
  Guarded by `重複啟動的第二個實例不會清掃第一實例的子行程`.
- **Recordings on disk never come back to the index on their own.** `segments` is the only
  source of truth for playback; after the database is rebuilt, restored from an old backup,
  a channel is deleted and re-added (`ON DELETE CASCADE`), or the recording disk moves to
  another machine, the `seg-HHmmssfff.mp4` files are still there while playback is empty and
  the log is clean. `RecordingDiskImporter` puts them back, triggered from
  Settings → Storage → "Scan disk recordings". It **does not guess** a channel for a folder
  that matches none (e.g. a leftover `ch072`): the operator picks one in the dialog, because
  attaching A's recordings to B is evidence pointing at the wrong camera. The reverse
  direction (index has it, disk does not) is `SegmentIndexReconciler` — neither replaces the
  other. **Import writes the file's own duration, not a write-time guess.** `LastWriteTimeUtc`
  is the wall clock at which ffmpeg closed the file, so a camera that stalls mid-segment makes
  it too long (measured: `seg-005201944.mp4` inferred 74.77 s for a 35.91 s file; the two
  inflated rows reproduced their 7105 / 7102 s exactly from write time), and a file copied or
  restored gets "now" for all files. `Collect(folderPath, probeEveryFile)` probes only on the
  import path (`Import` → `true`, `Scan` → `false`) because the folder list must stay cheap
  (~90 ms per file × thousands); a failed probe (ffprobe missing, file locked, network disk
  gone, or an exception) falls back to the old inference and never aborts the batch. The probe
  is injected from the desktop call site (`RecordingImportWindow` → `MediaDurationProbe`) since
  `HeliVMS.Storage` cannot reference `HeliVMS.Media` — dropping that wiring silently restores
  the defect with a clean log. Guarded by `RecordingDiskImporterTests` and
  `RecordingImportContractTests` (`匯入時以檔案自身長度建列而不是寫入時間`,
  `探測不到的檔案退回寫入時間推斷`, `探測拋出例外不會中斷整批匯入`,
  `掃描時不會為每一個檔案啟動探測`, `匯入時以真實探測取得檔案長度`).
- **Scheduled maintenance tasks have two orderings and one non-negotiable skip.** `scheduled_tasks`
  (V57) drives reboot / snapshot / reconnect from the alarm-rules window, executed by
  `MaintenanceTaskHost` (30 s poll; the "is it due" decision lives in
  `Shared.MaintenanceTaskPlanner`, pure and behavior-tested). Three rules keep a maintenance
  task from becoming an incident:
  (1) **Mark before run** — `MarkRunStarted` writes `last_run_utc` *before* the action executes.
  Run-then-mark + a crash mid-action = the poll re-fires the same due task every 30 s, i.e. a
  machine stuck in a 30-second reboot loop.
  (2) **Audit before action** — the reboot's audit row is written before `shutdown.exe` runs;
  after a reboot this process no longer exists, so anything written later is lost.
  (3) **No catch-up for missed slots** — the planner runs a task only when the *previous*
  scheduled slot was honored and today's slot time has passed. "Time passed and last-run is
  older" would reboot the machine 30 s after the operator powers it on in the morning
  (HeliVMS auto-starts, first poll fires). A new task gets `last_run_utc = now` at `Add`, so
  its first run is always the *next* slot — without that, a 03:00 reboot created at 15:00
  reboots immediately.
  Reboot is daily/weekly only (`Validate` refuses interval; the window shows its own denial
  too). Reboot asks Windows for `shutdown /r /t 90` (survives our death) and then `Close()`s
  the main window so teardown reuses the operator's ✕ path (ffmpeg gets its `q`, the in-flight
  segment survives); `ShutdownWatchdog`'s 55 s budget fits inside the 90 s grace. Scheduled
  snapshots use a one-shot `SnapshotGrabber` ffmpeg process — never the wall's decode sessions
  (cheap cameras allow 2–4 RTSP sessions; `-rtsp_transport` is RTSP-demuxer-only and must stay
  conditional or file-based test sources fail). Reconnect restarts every monitoring session
  through the same per-session path the health tick uses, never `DisconnectAllAsync`
  (that repaints the wall). Guarded by `MaintenanceTaskPlannerTests`,
  `ScheduledTaskRepositoryTests`, `SnapshotGrabberTests`, `MaintenanceTaskContractTests`.
- **Every rules change must rebuild every engine that consumed a snapshot.** The alarm-rules
  window invokes two callbacks; `OpenRulesWindow` originally wired only `ioRulesChanged`, so
  composite event rules (ai_rules → `RuleEvaluator`) needed an app restart to take effect —
  the list looked saved, the runtime kept the old set, log clean. And `ioRulesChanged` reloaded
  only `CameraDiHost`: the camera dry-contact host and `IoMonitorHost` (Modbus) each hold their
  **own** `IoRuleHost` engine (they share only the routed writer), so a rule change touching a
  Modbus contact also waited for a restart. `ReloadIoRuleEngines` now rebuilds both hosts and
  `RebuildEventRules` is passed as `eventRulesChanged`.
  Guarded by `RulesWiringContractTests`.
- **Editing a rule is Update, never delete+re-add — and editing must not reset run state.**
  All three lists in the alarm-rules window (event rules, IO linkage, scheduled tasks) have an
  Edit button feeding a full-row Update (`RuleRepository.Update` / `IoRuleRepository.Update` /
  `ScheduledTaskRepository.Update`). Delete+re-add would change the Id and orphan the audit
  trail. Two state rules: `On*AddToggleClicked` clears the editing Id when opening the "add"
  panel (shared confirm button — otherwise "edit then add" silently overwrites the edited row),
  and `ScheduledTaskRepository.Update` deliberately does **not** touch `last_run_utc`/`last_result`
  (resetting it turns "edit the 03:00 reboot task" into "today already ran = cleared", and the
  next poll reboots the machine again). Event-rule edit parses the condition JSON back into the
  simple form via `RuleExpressionBuilder.TryParseExpression`; shapes the builder never produces
  (any/not/within/nested) are refused and the editor falls back to advanced JSON — hard-mapping
  them would corrupt the rule.
  Guarded by `RulesWiringContractTests`, `RuleUpdateTests`, `ScheduledTaskUpdateTests`,
  `RuleExpressionParseTests`.
- **`notify=false` must be serialized, not omitted.** `BuildActions` used to map
  `Notify: notify ? true : null` — with `WhenWritingNull` the `false` case vanished from the
  JSON, the runtime read "no key" and applied the default (`Notify != false` → send). The
  operator's unchecked 通知 box was silently ignored and notifications kept flowing, log clean.
  `false` is now written as `false`; the all-defaults → empty-string optimization keys off
  `Notify is not false`. Guarded by `RuleExpressionParseTests`.
- **Settings export/import is a security boundary, not a convenience dump.**
  `SettingsBackupService` (Settings centre → About → Configuration export/import) ships 22
  configuration tables (cameras, groups, schedules, IO, rules, patrols, maps, masks, smart
  wall, scheduled tasks, app_settings) as one JSON with an explicit `version`. Three rules:
  (1) The `ExportTables` whitelist **never** grows a credentials/data table — `users`,
  `auth_providers`, `license` (machine-bound), `login_sessions`, `audit_log`, `segments`,
  `alarm_events` are data or identity, and a contract test fails the build if one appears.
  (2) **Import is atomic**: FK off → one transaction (delete all → insert in parent-first
  order, explicit Ids preserved so rules' channel references stay valid) → commit → FK on;
  any failure rolls the whole file back. Imported column names come from the file, so every
  row's keys are validated against `pragma_table_info` before an INSERT is built — never
  interpolate file strings into SQL. Files with a future `version` are refused.
  (3) **Export and import are admin-only and audited** — the file carries credential material
  (SMTP/MQTT passwords and API keys DPAPI-encrypted in `app_settings`), so export sits behind
  the same `VersionRequireAdmin` gate as import, and both write audit rows
  (`settings.export` category `export` / `settings.import` category `config`). DPAPI binds to
  the original user: same-machine restore decrypts fine, cross-machine the blobs are inert
  (SafeUnprotect → null) and notification passwords must be re-entered — the settings
  description says so. Scheduled-task CRUD is audited too (`task.add/update/delete/toggle`).
  (4) **Import offers a restart and the restart must be ordered**: the app prompts after
  import; `App.OnExit` spawns the successor **after** disposing the single-instance guard and
  after the orphan sweep — spawning earlier makes the new process a "duplicate launch" that
  wakes the dying instance, leaving nothing running; and the sweep must not race the new
  process's children. Map image files and rule sound files stay in the data root (paths, not
  payloads) — cross-machine migration copies the folder too.
  Guarded by `SettingsBackupServiceTests`, `ConfigXferContractTests`.
- **Cameras-full-screen (immersive) is a layer ON TOP of window-full-screen - exit it first.**
  The top bar has a `HeaderImmersiveButton`; entering hides `TopBarBorder`/`GroupTabs`/
  `FooterBorder`, zeroes `MainAreaGrid.Margin` (the wall re-fills itself via
  `OnMonitorAreaSizeChanged`), shows a floating `ImmersiveExitButton` (the only visible
  control) and calls `SetWindowFullScreen(true)`. Two rules:
  (1) entering captures `_windowFullScreenBeforeImmersive` and exiting restores it - an
  operator already in window-full-screen must not shrink to windowed mode when leaving
  immersive;
  (2) Esc unwinds in the order it stacked: expand-cell -> **immersive** -> window
  full-screen. Exiting window-full-screen first would hand back a window whose chrome is
  still hidden. The grid geometry needs no manual work - only sizes change, and the wall
  listens to its own SizeChanged.
  Guarded by `MonitorImmersiveContractTests`.
- **`PRAGMA user_version` does not prove the columns exist.** A rebuilt/restored
  `index.db` can be at the latest version with an old `segments` table: the live one was
  `user_version=56` and had no `tier`/`origin_path`, so the `version < 54` migration never
  ran again. `SegmentRepository` then threw `no such column: tier` on every read, and its
  deliberate `catch { }` (so a slow disk cannot break playback) turned that into an empty
  list — operators reported "recordings on disk but playback shows nothing" with a clean
  log. Additive columns are now enforced unconditionally in
  `SqliteStore.EnsureAdditiveColumns`, and playback surfaces the read error instead of
  claiming there is no recording. Guarded by `SchemaDriftTests`.
- **A channel snapshots the camera's stream URLs; updating `devices` alone leaves it stale.**
  `channels.main_rtsp`/`sub_rtsp` are copies made at enrollment. Re-discovering a camera used to
  update `devices` and nothing else, so the channel list kept the old `Profile102` (stream 3) as
  the sub stream: main and recording looked perfect, only multi-split pulled the wrong stream, and
  nothing was logged. `ChannelEnrollment.SetDeviceStreams` is now the single writer for camera
  streams — it updates `devices`, then `ChannelRepository.RefreshDeviceStreamUrls` rewrites only
  the channels that still equal the **old** device values, so a manually pinned URL is never
  overwritten. The channel edit form writes back through the same call, and the list gained a sub
  stream column plus 「重新取得攝影機碼流」 (ONVIF wizard in streams-only mode) so a stale row is
  visible and repairable without deleting the channel — which would cascade away its segments and
  events. Guarded by `重新加入攝影機時仍是設備舊值的頻道會一起換成新碼流`,
  `手動改過碼流的頻道不會被重新探索覆蓋`, `套用變更會把新碼流回寫攝影機並傳播` and
  `重新取得碼流按鈕存在並用綁定攝影機開啟精靈`.
- **A camera dry contact is a token, not a terminal number — bind it explicitly.** ONVIF exposes
  DI/DO as opaque strings (`RelayOutputToken_1`, `DigitalInputToken_2`); the old mapping guessed by
  trailing number, then by 1-based list position. A camera that reports `Profile101/102/103`-style
  names, or that reorders its list after a firmware update, made the guess silently pick a different
  relay: A-terminal signals drove B-terminal rules, and the only visible symptom was a gate opening
  that nobody had configured. `io_ports.terminal_token` now records the operator's explicit binding
  and `OnvifIoOutputWriter.RelayTokenFor`/`CameraDiMapping.TokenFor` honour **only** that token when
  it is set — if the token is gone from the device list they return `null` instead of falling back to
  guessing, because a wrong action is worse than no action (it leaves `relay-not-found` and an audit
  row). With no binding the old three-step rule still applies. Settings centre → IO
  (`IoCam*` in `SettingsWindow`) is the only place that writes the binding:
  `IoPortRepository.EnsureForTerminal` is idempotent, reuses a port that already holds the number
  (`io_ports` is unique on `(channel, kind, number)`), and unbinding clears the token without
  deleting the port or its `io_rules`. Binding a DI also calls `CameraDiHost.RefreshAndStart()`, or
  the new terminal keeps waiting for the next launch before it produces events. All three desktop
  ONVIF call sites go through `OnvifServiceFactory.Create`, so one missing `SafeUnprotect` cannot
  turn into "only this feature cannot connect". Guarded by `SettingsIoCameraContractTests`,
  `綁定的端子優先於端子號推測` (DO and DI), `綁定失效時不會退回端子號推測` (DO and DI),
  `端子號已被別的埠佔用時綁定會沿用那一個而不是新增` and `同一個端子重複綁定不會多出第二個埠`.
- **An audit row per retry is not evidence — it is noise that hides the evidence.** `CameraDiMonitor`
  retries every `retryDelay` (15 s) and used to write `camera.di.failed` unconditionally in its
  `catch`. Measured on the live `index.db`: one VIVOTEK IP9164-HT whose `event_service` returns
  HTTP 500 wrote **18,568 identical rows in 9 days**, i.e. 88% of every `recording`-category audit
  row (21,546). The signal an operator actually needs — `recording.stalled` (271),
  `recording.disk_full` (39), `recording.ownership_lost` (21) — was buried under it, and answering
  "when did recording stop?" meant scrolling past twenty thousand copies of one string. Nor is
  dropping the row an option: `AuditLogQuery` has no target/action filter in the UI, and the row is
  the only place a DI failure is visible at all. `DiFailureAuditPlanner` (pure state machine,
  clock injected) therefore throttles instead: **first failure always audited** (that is the
  normal→failing edge), **a changed cause audited after `MinimumInterval` (1 min)** (a timeout/500
  oscillation would otherwise be one row per retry again), **the same cause re-audited at most once
  per `RepeatInterval` (1 h)** as a bounded heartbeat carrying `（已連續失敗 N 次）` — a fault that
  has lasted a month must not look like it never happened, and the count is what tells the operator
  it is ongoing rather than new. The one detail that makes the throttle real: **`Reset()` runs after
  a successful `_pull`, never after a successful `_subscribe`.** "Subscribe OK, pull fails" *is* this
  camera's failure shape (the 500 comes from the pull), so resetting on subscribe re-classified every
  retry as a brand-new failure and the throttle did nothing — the first test run showed
  expected 1 / actual 9. Throttling affects the audit only; the retry loop keeps re-subscribing every
  15 s and feeds events into the rule chain unchanged. Guarded by `DiFailureAuditPlannerTests` and
  `同一個失敗不會每一輪都寫稽核`, `失敗原因改變時會補記一筆並帶出連續次數`,
  `成功之後再失敗會重新寫一筆稽核`.
- **Audio rides the decode process; it never opens a second RTSP session.** `RtspClient` decodes
  with `-an`, so a camera's microphone was once never heard. The fix is NOT a second ffmpeg per
  camera: cheap cameras allow only 2-4 concurrent RTSP sessions (main-for-recording +
  sub-for-wall + main-for-single-pane is already three), and one more for sound kicks the *picture*
  off - the symptom is "the image drops when I turn the sound on" and the log only shows RTSP
  reconnects. Instead the decode process gets a second output (`AudioOutputArguments.Build`:
  `-map 0:a:0 -ac 1 -ar <rate> -acodec pcm_s16le -f s16le -flush_packets 1 udp://127.0.0.1:<port>`)
  shared by live (`RtspClient`) and playback (`PlaybackSession`). UDP on purpose: a slow reader
  drops packets (a small gap in sound); a TCP backpressure would stall ffmpeg and freeze the
  picture. `AudioStreamReceiver` binds the loopback port *before* the process starts (the port
  number has to be in the args), keeps it bound for the session's lifetime (an unbound port makes
  ffmpeg's sends fail), drains stale datagrams on Start (so pressing the speaker doesn't replay
  seconds-old audio) and re-chops the byte stream into 100 ms chunks (`PcmChunkQueue` is the bounded
  buffer: full = drop the **oldest**, with `Dropped` as the only trace). The output is armed **only
  when the probe found an audio track** (`AudioOutputArguments.ResolvePort`; `-map 0:a:0` on a
  source without audio makes ffmpeg fail outright - no picture at all), which is also why
  `SourceHasAudio` is surfaced: "listen enabled but no microphone" must be visible, not silent.
  Sample rate is probed from the device once (`WaveOutPlayer.ProbeSampleRate`, 16k first) and baked
  into the args; a mismatch shows up as wrong pitch, not as an error. No audio device (remote
  desktop) is not an error - `AudioOutput.StartAsync` returns false and logs to
  `%TEMP%\helivms-audio.log`. Wall playback is **exclusive**: one camera at a time
  (`LiveAudioController.ToggleAsync` stops the previous one first), because several cameras at once
  are indistinguishable. The per-pane speaker icon (bottom-right, outside the zoom layer so it does
  not scale) shows only for channels with `channels.listen_enabled = 1` (settings centre / channel
  management): white = clickable, blue = playing (click mutes), grey mute = no audio track. Arming
  happens at session creation, so clicking never reconnects. Playback shows the icon only when the
  segment file actually has an audio track (old recordings may not), and non-1x speeds stay silent
  (no `atempo`). When the playing camera leaves the screen (channel change, tab switch, channel
  deleted, monitoring stopped) the audio stops - decided only at layout/tab/list *settle points*
  (`EnforceAudioVisibilityAsync`), never mid-layout-change when cells are transiently empty, or
  every layout switch would cut the sound. Guarded by `AudioOutputArgumentsTests`,
  `AudioStreamReceiverTests`, `AudioOutputFfmpegTests` (runs the real production fragment into the
  receiver), `LiveAudioContractTests` and `PlaybackAudioContractTests`.
- **IO contacts are registered in Settings → IO; linkage rules live in the alarm rules window.**
  Two different questions were once answered on one page: "what contacts exist" and "what happens
  when one fires". The split mirrors Home Assistant's devices/entities vs automations: the IO page
  registers contacts (Modbus modules/channels **and** camera dry-contact terminals — both are
  `io_ports`), and `RulesWindow` owns `io_rules` (DI trigger → alarm event with a type, or switch a
  registered DO; cooldown seconds). The rule editor only offers **registered** contacts, so a rule
  cannot point at hardware that was never bound. Actions are complete rather than event-only:
  linked camera (bring it up on the wall, optionally full screen), snapshot (same path as the
  right-click capture), alarm sound (SoundPlayer on the host), map focus, reset seconds (restore the
  layout after N s) and relay output seconds (`IoRuleHost.ScheduleOutputReset` closes the relay for
  N s then releases — the "alarm rang and would not stop" complaint). Rule changes call
  `CameraDiHost.ReloadRules()` so the running monitor rebuilds its engine immediately; Modbus
  channels keep their own `io_channels` semantics and do not go through `io_rules`. The XAML ratchet
  scans whole files: no CJK in `RulesWindow.xaml` comments, no full-width punctuation in code
  strings, and one i18n key per window (reuse across two windows fails the duplicate-key check).
  Guarded by `AlarmIoRuleContractTests` and `LiveTalkContractTests`.

- **QCC reaches the NVR on the web port, and it has nowhere to type a NEMO port.**
  QCTek's QCC connects to the device's *web* port, and the real QCV8001 multiplexes NEMO control with
  HTTP on 80 (`HttpServer::PreParseRequest`). So whenever the compat service *and* Web are enabled the
  public port goes under `QctekPortMux` (loopback front, NEMO answered in-process by the same
  `QctekSession`, HTTP relayed untouched to Kestrel's ephemeral port) — **`QctekPortPlanner.Plan` must
  not require `qctek.compat.port == web.port`**. It used to, and the shipped default 5170 with
  `web.port=80` left 80 to Kestrel: the device added fine in QCC and then showed "connection lost"
  forever while every NEMO command got `400 Bad Request`, log clean. A different compat port now only
  adds the standalone `NemoServer` on `Any` alongside the mux, for a remote CMS that is pointed at that
  port explicitly; equal ports keep the mux alone (never two listeners on one port). Three things are
  load-bearing. (1) **Order**
  (`NetworkPortCoordinator.Apply`): sharing -> Web listens on an ephemeral port *first*, then the mux
  binds the public one; not sharing -> `StopMux()` *first*, then Web binds back. Reversed, one service
  hits "port already in use" and the settings page says only "failed to start", log clean. If the mux
  cannot bind, Web is restored to the public port so the admin UI stays reachable. Every settings-page
  apply goes through the coordinator and refreshes *both* status lines (sharing is decided by both
  settings, so pressing Apply on one tab can hand the port to the mux). (2) **`RemoteConsolePort` must
  report the public port**, handed to WebApi as `helivms.public_port`
  (`QctekHttpResponder.PublicPortConfigKey`) and resolved by `ResolveConsolePort`; reporting Kestrel's
  ephemeral port makes QCC store a number only this process knows ("device added, connection failed").
  (3) **The auth challenge is 27 bytes because it has no length field and no NUL** - 16 random chars +
  space + a *ten-digit* Unix second; a nine-digit nonce misaligns any client that reads a fixed 27
  bytes (reproduced: the mux golden test hung waiting for the challenge) with nothing in either log.
  The HTTP relay half-closes the *target* socket per direction (a `Close` truncates the other
  direction) and replays the bytes consumed for protocol classification. (4) **Kestrel refuses
  `http://localhost:0`**, so share mode must bind `http://127.0.0.1:0` (`HeliVMS.Shared.WebListenUrl`);
  its `InvalidOperationException: Dynamic port binding is not supported when binding to localhost`
  was swallowed by the Web start-failure path, so the whole money shot collapsed silently:
  Web never started -> no public port was freed -> the mux never bound -> only the standalone 5170
  listener existed, and QCC (which only knows the web port) showed "connection lost" with three clean
  logs. That failure now writes `%TEMP%\helivms-web.log`, the coordinator writes "Web did not start"
  to `%TEMP%\helivms-qctek.log`, and the listener URL decision is behaviour-tested in Shared.Tests.
  Guarded by `QctekPortMuxTests`, `QctekPortPlannerTests`, `QctekChallengeTests`, `WebListenUrlTests`,
  `NetworkPortContractTests` and `QctekHttpResponderTests`.
- **QCC live view: the first FRAM must be a keyframe, and the live queue must be bigger than the
  startup burst.** 現場回報「QCC 連得上但沒畫面」時，主因不是授權也不是攝影機，而是我們的每頻道
  佇列: 實測（VIVOTEK IP9164-HT、1080p15、`-c:v copy -bsf:v dump_extra`）ffmpeg 建立 RTSP 工作
  階段後把輸入緩衝一次倒出來——第一包 10279 bytes ＝ SPS/PPS/SPS/PPS/IDR，1 毫秒後又一包
  2201 bytes ＝ 27 個 P-frame，兩包都遠早於第一個 33 ms tick。舊的 `QueueCapacity = 8` 讓排在
  後面的 P-frame 把那張 IDR 擠出去，之後每一幀都是 `block1 = 0`，而 **QCC 只以 `block1 > 0`
  認定關鍵影格**，於是解碼器沒有起點: 連線正常、畫面全黑、兩邊日誌乾淨，要等下一個 GOP
  （修復前實測第 91 幀、約 7.5 秒）。兩條規則不要改回去: (1) 容量必須大於突發量（現在 64，
  約 2 秒的 30 fps 輸出）；(2) 溢位時寧可跳段也不要送解不開的影格——新的關鍵影格到達而佇列
  已滿時整段舊積壓作廢（`LiveFrameQueue.Push`），因為關鍵影格自己就帶著 SPS/PPS。第一次丟棄
  會寫進 `%TEMP%\helivms-qctek.log`（關鍵影格被丟棄另有第二行），否則這個症狀的預設值就是
  「日誌乾淨」。Guarded by `啟動突發不會把關鍵影格擠出佇列`,
  `新的關鍵影格到達時會清掉積壓的舊影格` and `啟動突發後的第一個資料幀是含SPS的關鍵影格`
  （把容量改回 8 會同時紅）。
- **QCC heartbeat commands are answered with zero bytes — never with a frame.** `ALARM_HELLO 1
  =QCV8001` arrives every 5 seconds (1,341 times in one measured session) not because the CMS is
  managing the device but because QCC's own "report alarms to CMS" setting points at
  `127.0.0.1:5170`. The real QCV8001 answers nothing: `qc_rm_alarm`'s `FUN_180004830` →
  `FUN_180005f10` writes `0` to the first byte of the 0x100 response buffer (= 0 bytes on the
  wire), and the QCC side (`qc_alarm` `FUN_180015620`) only increments a counter after sending —
  it never reads a reply. So `QctekCommandHandler` returns `null` for these. Sending an "OK "
  frame instead makes the client treat that frame as the response to its **next** command, and
  every subsequent reply is off by one: the device connects, every command goes out, and both the
  picture and every list stay empty with clean logs on both sides. `NemoSessionLoop` also logs the
  heartbeat only once per connection — 1,341 lines in a 512 KB rolling file erases the lines that
  actually explain a failure, and "the log looks clean" is worse than no log. Guarded by
  `心跳命令是已知命令且不回任何位元組` and `心跳命令只記錄第一次且不會吃掉下一條命令的回應`.
- **QCC restarting itself every 66 seconds is the machine's memory pressure, not a compat-layer
  bug.** When investigating "QCC behaves oddly" (a device that adds fine then disconnects, a
  registry experiment with no signal, `Out of Memory!` in `C:\QCTek\QCC\log\*.err`), check
  whether the log is looping `BOOT → Out of Memory! → Stop NVR 3: ShellStartPage` about every 58
  seconds of run plus 8 seconds of teardown. `QCC.exe`'s `FUN_140001ef0` calls
  `GlobalMemoryStatusEx` on a timer and, when `ullAvailPageFile * 10 < ullTotalPageFile` (free
  page file below 10 %), sets `+0x608 = 0x1417` and `FUN_140001af0` posts `0x801b` with
  `wParam = 3` to the child window; `QC_Live`'s `FUN_140015aa0` prints `ProcMemInfo` and rebuilds
  the whole NVR. Measured on this box: 13,238 / 14,031 MB committed (5.6 % free against a 1,403 MB
  threshold) while Ghidra, OpenCode, Chrome and node were running; 2,407 MB (17.2 %) after they
  closed, and the loop stops. Anything observed while the loop is running is worthless — a probe,
  click or registry change is reset before it can be measured — and this is why
  `ALARM_HELLO` timing surveys and `ImmedChannels`/`ImmedSelAll` experiments produced "no
  signal". Confirm the loop is gone (no `Out of Memory!` / `Stop NVR 3: ShellStartPage`) before
  concluding anything about live view, and never "fix" it in our own code.
- **QCC's event search answers with a *total count* and pages with raw `NEXT`/`PREV`, not a second
  query.** `SEARCH_EVENT` is the binary NEMO command behind the CMS event log page: 16-byte
  `"SEARCH_EVENT"` + u32 `0x150` (body length) + u32 page size + the 336-byte
  `__EVENT_SEARCH_PARAM` body with its first 4 bytes dropped (`+0x00`/`+0x24` `struct tm`, `+0x48`
  i32 type, `+0x4c` i16 channel, negative = all). The reply is `"OK "` + ASCII decimal **total** +
  zero fill to `+0x10`, then 0x38-byte records: i16 channel, i16 type, 44-byte wide
  `"YYYY/MM/DD HH:MM:SS"` **local** time, and at `+0x30`/`+0x32`/`+0x34` three lengths where len2 is
  UTF-16LE *including* its terminating NUL (len1 < 2, len2 < 3, len3 < 3 mean "empty"). Four things
  are load-bearing. (1) The number after `OK ` is how many events matched, **not** the page size —
  the CMS computes its page count from it, so answering with the page size shows one page and then
  an empty list the moment the operator pages. (2) Paging is not a resend: the client then sends a
  raw 4-byte `NEXT`/`PREV` with **no NEMO header and no NUL**, so the session must intercept those
  before framing (a NUL-padded command is a different frame); the total is taken from the first page
  and out-of-range page numbers clamp to the first/last page. (3) Zero results still need a frame
  (`"OK 0"`, payload 0x20) because the client returns false below 0x21 and never renders an empty
  list. (4) `SEARCH_EVENT` does **not** go through the `QCV8001_AUTH` challenge — putting it in
  `RequiresAuthorization` leaves the CMS event page hanging with nothing in either log. Type codes
  map only to 1–5 (System/Operation/Network/Event/Alarm; `line_cross`/`intrusion`/`ai_*` are Event,
  `io_*` is Alarm, offline/online is Network), 200–205 are VTMS/POS tags and those queries answer
  0 rows — mapping AI events to 205 "looks closer" but makes them vanish from the all-events query,
  which asks with type 0. Unreadable times refuse the whole query instead of widening it to a day
  (same lesson as the export wizard's "5 seconds became 50 minutes"). Guarded by
  `QctekEventPayloadsTests`, `QctekEventSearchRequestTests` and the paging cases in
  `QctekSessionTests`.
- **Timestamps bind through `SqliteStore.Iso`, never a raw `DateTime` — mixing the two makes
  string comparisons timezone-dependent.** Several columns (`finished_at`, `started_at`, …) are
  TEXT and queries compare them against an Iso-bound cutoff; a raw-bound DateTime writes a
  different text format (space separator, no Z), and which one sorts first flips between a UTC+8
  dev box and the UTC runner. The exact test passed locally and failed on CI
  (`ExportJobRepositoryTests.PurgeFinished_*`, F8's `Cancel` binding — fixed in `c3d9ce6`).
  Every new column that a query ever compares or orders by must be written AND cut off through
  the same helper; when a time test can pass on one timezone and fail on another, suspect the
  binding, not the clock.
- **Slicing a `byte[]` with a Range (`buffer[0x10..]`) produces a COPY — writing through it is a
  silent no-op.** `BinaryPrimitives.WriteUInt32LittleEndian(payload[0x10..], v)` compiles, runs,
  and writes into the temporary array the range indexer allocated; the original stays untouched.
  On a `Span<byte>`/`ReadOnlySpan<byte>` the same slice is a view and the write lands. The two
  sit side by side in the same file and both compile, so the only signal is a behavioral test
  reading the field back (`QctekPlaybackPayloadsTests.Catalog*` caught `CATALOG`'s day field
  writing zeros — fixed in the P4 follow-up). Rule: any `BinaryPrimitives.Write*` target must be
  a `Span` obtained from `AsSpan(offset)` or a span parameter, never a range-indexed array;
  when a wire-format field reads back as zero with a clean log, grep for `..]` writes first.
- **視覺搜索的雜湊只有一條管線，而取樣尺寸就是雜湊的定義。** `alarm_events.snapshot_hash` 與
  搜尋樣本都必須走 `VisualHash`（Bgra → 灰階 Rec.709 → 可選 ROI → `PerceptualHash`），
  取樣尺寸是 `VisualHash.IndexWidth`（72）×`VisualHash.IndexHeight`（64）＝ 9×8 區塊每塊
  8×8 像素。兩件事不要改回去：①服務內**不得**再寫一份灰階公式——係數或四捨五入差一點，
  兩側指紋就整組不同，漢明距離變成亂數，而症狀只是「以圖搜索找出來的都不像」；
  ②改 `IndexWidth`/`IndexHeight` 等於換掉雜湊定義，既有索引會全部不可比且**不會**自動重建，
  必須同時安排一次重建（`snapshot_hash` 由 0 補算即可）。`VisualSearchService` 只保留
  「解碼成索引尺寸的 Bgra」這一件事（`DecodeBgraIndex`），雜湊交給共用層。
  Guarded by `整張圖的雜湊與舊的灰階入口完全相同`（＝既有索引不需重建的唯一依據）、
  `索引解碼尺寸讓每個九乘八區塊剛好八乘八像素` and `灰階與雜湊都走共用的VisualHash管線`.

