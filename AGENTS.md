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
- `HeliVMS.LicenseProducer.Tests` targets `net10.0-windows`.
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
  Adding a socket- or process-touching test class means adding it to that collection.
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
  Guarded by `PlaybackSyncTimingTests`,
  `多路同步用共用容差且跳轉補上啟動延遲` and `次畫面以母鐘解算且空隙不硬跳別段`.
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
  window in the app (18 of them: the settings-centre children — detection, redaction, dewarp,
  stream switch, IO, talk, camera/channel manager, monitor group, recording import, notification
  log, ONVIF wizard, map, PTZ — plus scheduling/export/export centre and the settings centre
  itself), each carrying the same red ✕ at top-right plus Esc. Six of them had no close
  affordance at all and six had only a text 關閉 button; both read as "this window has no exit"
  once the title bar is gone. Guarded by `WindowExitContractTests` (table of 18 windows; each
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
  nothing. Guarded by `SettingsReserveSpaceContractTests`,
  `總量配額停用時不會汰除任何區段`, `RunConfigured會依磁碟保留空間做壓力汰除` and
  `RunConfigured會依共用設定解析根目錄並清理過期快照`.
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
  other. Guarded by `RecordingDiskImporterTests` and `RecordingImportContractTests`.
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
