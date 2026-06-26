# Task 5: FullScreenOverlay Sync — Simplify Transport Layout

## What Changed

**Views/PlaybackView.xaml** (lines 954-1004):
- Replaced 11-column definition with simplified 8-column layout
- Removed `FsJumpStartBtn`, `FsPlayBtn`, `FsPauseBtn`, `FsJumpEndBtn`
- Remapped `FsStepBackBtn` → Col 0, `FsStepFwdBtn` → Col 2, `FsSpeedBtn` → Col 5, `FsClockText` → Col 7
- Added merged `FsPlayPauseBtn` at Col 1 with `FsPlayPauseIcon` (toggles between IconPlay/IconPause)
- Added `FsSegPrevBtn` at Col 3 and `FsSegNextBtn` at Col 4
- Added spacer column at Col 6 (Width="*")

**Views/PlaybackView.xaml.cs** (lines 3359-3370, 3383-3404):
- Replaced `UpdateFsTransportState` — now enables/disables all fullscreen transport buttons based on `_activePlayers.Count > 0`, and toggles `FsPlayPauseIcon.Data` + `FsPlayPauseBtn.Tag` based on `_isPlaying`
- Updated `FsTransportBtn_Click` — merged "play"/"pause" cases to both call `PlayPauseBtn_Click` directly; added "segPrev"/"segNext" cases calling `JumpPrevRecBtn_Click`/`JumpNextRecBtn_Click`

## Build Result

```
建置成功。
    0 個警告
    0 個錯誤
```

## Self-Review Notes

- No lingering references to `FsJumpStartBtn`, `FsJumpEndBtn`, `FsPlayBtn`, `FsPauseBtn` in any `.xaml` or `.cs` files
- All new buttons (`FsPlayPauseBtn`, `FsSegPrevBtn`, `FsSegNextBtn`, `FsPlayPauseIcon`) referenced only in the two modified files
- `JumpPrevRecBtn_Click` and `JumpNextRecBtn_Click` exist at lines 2005 and 2009
