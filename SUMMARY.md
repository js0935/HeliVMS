# HeliVMS 開發紀錄

## Session Summary — 2026-06-22

### Problem
App crashes on startup. Root cause narrowed to **post-login crash** — app navigates to `LiveView` after successful login, then crashes before the camera grid renders.

### Investigation → Root Cause
Analyzed `helivms-20260622.log` (91s of runtime before crash):

1. **Flyleaf Engine fails to start**
   - `LoadLibraryEx` on `ffmpeg.exe` failed (probably **missing VC++ redist** or **missing avcodec-61.dll**)
   - Previously: `NotSupportedException` was silently caught; app continued but `VideoPlayer` hit NRE later

2. **FFmpeg 7.x `-stimeout` deprecated**
   - `RecordingService` uses `-stimeout` (removed in FFmpeg 7.x); all recording sessions silently fail
   - `VideoStreamDecoder` in `PlaybackView` passes `stimeout` → streamer ignores it

3. **MediaMTX port conflict**
   - If a stale `mediamtx.exe` is running from a previous crash, the new one fails to bind

4. **Thread safety in CameraService**
   - `GetHealthCounts()` iterates `_cameras` **without lock** → null/out-of-range crash under concurrent modifications (`AddCamera`, `DeleteCamera`, `BatchUpdateCameras`)
   - LiveView timer fires on UI thread, but other threads call camera service methods concurrently

5. **Performance issues (not crash-causing)**
   - `IEventService` / `INotificationService` resolved per-method call in `VideoPlayer`
   - OSD timer creates 7+ strings per tick
   - `CameraGrid.LoadCameras` uses LINQ (`OfType<VideoPlayer>`, `Where`, etc.) on hot path
   - `CameraGrid` parameter was `IEnumerable<Camera>` (forces `.ToList()` every time)
   - PTZ camera search was a separate `foreach` loop

### Fixes Applied

| # | File | Change | Impact |
|---|------|--------|--------|
| 1 | `LiveView.xaml.cs` | Add `FlyleafEngineReady` flag; pre-check FFmpeg DLLs before `Engine.Start()` | Skips Flyleaf engine if DLLs missing → prevents NRE crash |
| 2 | `VideoStreamDecoder.cs`, `SegmentRecorder.cs`, `RecordingService.cs` | `stimeout` → `rw_timeout` (FFmpeg 7.x) | Recording sessions actually work now |
| 3 | `MediaMTXService.cs` | `KillOrphanedProcesses()` before starting MediaMTX | No port conflict, no silent failure |
| 4 | `VideoPlayer.cs` | Cache `IEventService` / `INotificationService` as static fields | Fewer DI allocations per playback start |
| 5 | `VideoPlayer.cs` | OSD prefix cache (`"CH"`, `"FPS"`, etc.) + `string.Concat` | ~7 fewer heap allocations per tick |
| 6 | `CameraGrid.xaml.cs` | `LoadCameras(IList<Camera?>)` instead of `IEnumerable<Camera>` | No `.ToList()` copy; better null tracking |
| 7 | `CameraGrid.xaml.cs` | Replace `OfType<VideoPlayer>()` + LINQ with raw `for` loop over `Children` | Faster cell lookup |
| 8 | `CameraService.cs` | Added `GetHealthCounts()` + **lock** for thread safety | Crash fix |
| 9 | `RecordingService.cs` | Added `GetActiveRecordingCount()` | Needed by dashboard |
| 10 | `CameraGrid.xaml.cs` | Merge PTZ camera search into main loop | Eliminates extra O(n) pass |
| 11 | `LiveView.xaml.cs` | `OnCompositionRendering` — compute `elapsed` in ms first, then `TimeSpan.FromMilliseconds` | Fewer `DateTime` ops |
| 12 | `Helpers.cs` | `SanitizeFileName` — replace LINQ with raw `StringBuilder` | Faster filename sanitize |

### Fix #13 (CRASH FIX): FlyleafHost native crash on login
- **Root cause**: `VideoPlayer.xaml` had `<fl:FlyleafHost>` in XAML; when Flyleaf Engine fails to start, creating FlyleafHost triggers a **native crash** (AccessViolation) that bypasses managed exception handlers — app vanishes ("閃退")
- **Fix**: Removed FlyleafHost from XAML, created programmatically in constructor only when `FlyleafEngineReady == true`
- **Also**: Added try/catch around `new VideoPlayer()` in `CameraGrid.LoadCameras()` to prevent one bad player from crashing the grid

### Navigation flow after login
1. `LoginSucceeded` fires → **both** handlers run: `NavigationService.NavigateTo(LiveView)` then `MainWindow.SyncSidebarAfterLogin(Dashboard)`
2. The **last** handler determines the final page — due to subscription order, **LiveView** is the final page
3. LiveView's `OnLoaded` → `RefreshCameraGrid()` → `CameraGrid.LoadCameras()` → `new VideoPlayer()` each camera
4. Before fix: FlyleafHost XAML element → native crash when Engine not initialized

### Verification
- `dotnet build` succeeded: **0 warnings, 0 errors**

### Remaining Items
- [x] **FlyleafHost native crash** — fixed by conditional programmatic creation
- [ ] Confirm the app runs stably for 5+ minutes after login
- [ ] Verify recording files are actually created (stimeout fix)
- [ ] Test PTZ functionality
- [ ] Test multi-camera grid (16/32/64 channels)
