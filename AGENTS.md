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
  generated file. Also asserted by `SolutionCoverageTests`.
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
  instant** (segment start + position → local `HH:mm:ss` via `RecordingInstant`), with
  `pos / length` only as a secondary — operators copy this readout into reports and use it to
  correlate events, and a segment-relative number matches hundreds of different wall-clock
  moments in a day. The pane OSD shows the same instant through the same helper, so the main
  readout and a synced pane can never disagree about "what time is this". The **main player has
  its own top-left OSD too** (`PlayerOsd`, same `OsdText` format): in 1-split the main view is
  the only pane, and an operator who zoomed in from the wall loses the familiar corner clock if
  only the bottom readout carries the time. It is written by `ShowPlayheadTime` (the only writer
  of time readouts) and cleared with the playback head, so it can never outlive its segment.
  Guarded by `播放讀數以錄影時刻為主而非段內位置`, `單一畫面也有左上角的錄影時間OSD` and
  `次畫面OSD顯示錄影時刻而非段內位置`.
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
  `SchedulingWindowContractTests` and the window-wide `UiWiringContractTests`.
- **A recording-schedule rule that never records must be rejected, not stored.** `IsActiveAt`
  uses a closed interval, so start == end (e.g. the old form default 00:00–00:00) is active for
  exactly one minute of the day; the scheduler ticks every 30 s and in practice captures
  nothing — a rule that silently does nothing, with no error anywhere. `BuildFromForm` now
  refuses start == end (`Schedule.SameTime`) and the form defaults to 00:00–23:59 so the most
  common action ("pick a channel, add an all-day rule") is correct by default. Guarded by
  `起訖相同時段會被擋下而不會存成不會錄的規則` and `表單預設時段是一整天`.
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
- **`Application.MainWindow` is the splash, not the monitor wall.** `SplashWindow` is the first
  window created, so the property points at it (or goes null after it closes). Looking up the
  real window that way made the single-instance wake-up silently do nothing: a second launch
  exited, the first process got the event, cast `Application.MainWindow` to `MainWindow`,
  failed, and nobody noticed. `MainWindow.Current` is the reference; re-launching calls
  `BringToFront(maximize: true)` so a window hidden in the tray comes back maximized.
  Guarded by `SingleInstanceContractTests`.
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