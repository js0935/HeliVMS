# Task 3 Report: LIVE/SYNC/CLND/THMB Code-Behind + GoLive

## What was implemented

**`Views/PlaybackView.xaml.cs`:**
- Added `_liveMode` and `_syncEnabled` fields (lines ~49-50)
- Replaced 8 TODO stubs with real implementations:
  - `PlayPauseBtn_Click` — toggles `_isPlaying` and calls `_coordinator.Play()`/`Pause()`
  - `LiveBtn_Checked` / `LiveBtn_Unchecked` — toggles live mode via `ToggleLiveMode()`
  - `ToggleLiveMode()` — seeks to current time, sets `LiveBtn.IsChecked`
  - `SyncBtn_Checked` / `SyncBtn_Unchecked` — manages `_syncEnabled` and re-syncs slaves to master
  - `ClndBtn_Click` — opens CalendarPopup
  - `ClndCalendar_SelectedDatesChanged` — sets date and triggers `FilterDatePicker_SelectedDateChanged`
  - `ClndCalendar_Today_Click` — jumps to today
  - `ThmbBtn_Checked` / `ThmbBtn_Unchecked` — calls `Timeline.ToggleThumbnails(true/false)`
  - `OnTimelineGoLiveRequested` — handles GoLiveRequested event from Timeline
- Subscribed `Timeline.GoLiveRequested` in `OnPlaybackLoaded` (after BookmarkRequested)
- Unsubscribed in `OnPlaybackUnloaded` (before ReturnPendingFrameBuffers)
- Added live mode auto-exit in `OnTimelinePositionChanged`

**`Views/PlaybackView.xaml`:**
- Added CalendarPopup (Popup with Calendar + "今天" button) before closing `</Grid>`

## Stub methods/events added to NxTimeline

- `NxTimeline.ToggleThumbnails(bool show)` — empty stub in `Controls/NxTimeline.xaml.cs:159`
- The `GoLiveRequested` event already existed in NxTimeline (line 60)

## Build result

`dotnet build` — **0 errors, 0 warnings**

## Commit

2cf4d1f — 實作 Task 3: LIVE/SYNC/CLND/THMB 按鈕邏輯 + GoLive 事件

## Self-review notes

- `_syncEnabled` field guard in `SyncBtn_Checked` prevents redundant re-sync calls
- `FilterDatePicker_SelectedDateChanged` called with `null!` for `e` parameter when triggered from Calendar
- All interactions exit live mode when user drags timeline or seeks
- `ToggleThumbnails` is a stub that will be properly implemented in Task 6
- CalendarPopup uses Placement="Top" to appear above the CLND button
