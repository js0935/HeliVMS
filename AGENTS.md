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