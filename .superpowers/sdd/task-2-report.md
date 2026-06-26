# Task 2: Transport Controls Layout Restructure — Report

## Summary of Changes

### XAML (`Views/PlaybackView.xaml`)
- Replaced 19-column Grid definition with 12-column layout
- **Removed:** JumpStartBtn, JumpEndBtn, StopBtn, PlayBtn, PauseBtn, LoopPanel (all children), BookmarkListBtn, ExportClipBtn
- **Added:** `PlayPauseBtn` (merged Play/Pause, col 0), LIVE/SYNC/CLND/THMB button group (col 9)
- **Re-assigned:** StepBackBtn→col 1, StepFwdBtn→col 2, Separator→col 3, JumpPrevRecBtn→col 4, JumpNextRecBtn→col 5, SpeedSliderPanel→col 6, PlaybackTimeText→col 7, FpsBadge→col 11
- **Grouped:** BookmarkBtn + StopAllBtn in StackPanel at col 10

### Styles (`Styles/Styles.xaml`)
- Added `NxToggleButton` base style + 3 variants: `NxGreenToggleButton`, `NxBlueToggleButton`, `NxOrangeToggleButton`

### Code-behind (`Views/PlaybackView.xaml.cs`)
- **Replaced** `UpdateButtonStates()` — references new controls + PlayPauseIcon toggle
- **Added stubs:** `PlayPauseBtn_Click`, `LiveBtn_Checked/Unchecked`, `SyncBtn_Checked/Unchecked`, `ClndBtn_Click`, `ThmbBtn_Checked/Unchecked` (all `// TODO: Task 3`)
- **Removed handlers:** PlayBtn_Click, PauseBtn_Click, StopBtn_Click, JumpStartBtn_Click, JumpEndBtn_Click, LoopSetABtn_Click, LoopSetBBtn_Click, LoopToggleBtn_Click, LoopClearBtn_Click, BookmarkListBtn_Click, ExportClipBtn_Click, UpdateLoopVisual
- **Updated references:** Space key handler → calls PlayPauseBtn_Click; OnPlayerPlayPauseRequested → calls PlayPauseBtn_Click; FsTransportBtn_Click → removed jumpStart/jumpEnd, play/pause → PlayPauseBtn_Click
- **Removed dead code:** `_loopA`, `_loopB`, `_loopEnabled` fields; loop-seeking in timer callback; A-B Loop keyboard shortcuts (I, O, L)

## Build Result

```
dotnet build "D:\HeliVMS\HeliVMS\HeliVMS.csproj"
建置成功。0 個警告 0 個錯誤
```

## Self-Review Notes

- All TODO stubs (`// TODO: Task 3`) require implementation in the next task
- BookmarkBtn still keeps `BookmarkIcon`/`BookmarkFeedback` named elements for visual feedback
- `OnTimelineSelectionChanged` placeholder left empty since ExportClipBtn was removed
- Full-screen transport bar still has `FsJumpStartBtn`/`FsJumpEndBtn` buttons (Tag="jumpStart"/"jumpEnd") but `FsTransportBtn_Click` no longer handles those tags — they remain in XAML as inert until Task 3

## Files Changed

- `Styles/Styles.xaml` (+53 lines)
- `Views/PlaybackView.xaml` (-22 lines net)
- `Views/PlaybackView.xaml.cs` (-77 lines net)
