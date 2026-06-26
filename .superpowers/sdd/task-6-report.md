# Task 6: THMB ThumbnailCanvas in NxTimeline

## Changes Made

### `Controls/NxTimeline.xaml`
- Changed `Grid.RowDefinitions` from 3 rows to 4 rows:
  - Row 0 (Auto): ThumbnailStrip — 48px when visible
  - Row 1 (24px): ActivityCanvas (shifted from Row 0)
  - Row 2 (*): CameraRowsCanvas (shifted from Row 1)
  - Row 3 (16px): TimeScaleCanvas (shifted from Row 2)
- Added `ThumbnailCanvas` at `Grid.Row="0"` with `Height="48"`, `ClipToBounds="True"`, `Visibility="Collapsed"`, and `PreviewMouseMove="ThumbnailCanvas_MouseMove"`
- Updated all existing Grid.Row offsets (+1): ActivityCanvas, ActivityCanvas Border, CameraRowsCanvas Border, TimeScaleCanvas Border, ZoomLabel, GoLiveBtn
- Updated overlay RowSpan from 3 to 4: SelectionCanvas, PositionCanvas, BookmarksCanvas

### `Controls/NxTimeline.xaml.cs`
- Added `_showThumbnails` field (line 46)
- Filled in `ToggleThumbnails(bool show)` — toggles visibility, draws or clears thumbnails
- Added `DrawThumbnails()` — renders recording segments as thin translucent colored bars at bottom of thumbnail strip
- Added `SegsToThumbX(double secs)` — converts seconds to X position using ThumbnailCanvas width
- Added `ThumbnailCanvas_MouseMove` — shows timestamp tooltip on hover
- Updated `DrawAll()` to call `DrawThumbnails()` when `_showThumbnails` is true

## Build Result
```
建置成功。
    0 個警告
    0 個錯誤
```

## Self-Review Notes
- `GetColorForRecordType()` returns `Color` struct — used directly with `Color.FromArgb(60, color.R, color.G, color.B)` ✓
- `IsTypeVisible()` takes `int recordType` — matches `seg.RecordType` ✓
- `SegsToThumbX` is independent from `SecsToX` — uses `ThumbnailCanvas.ActualWidth` not `CameraRowsCanvas.ActualWidth` ✓
- All existing Grid.Row values incremented correctly; no stale references ✓
- Overlay canvases RowSpan=4 covers all 4 rows ✓
- `ToggleThumbnails` is public — matches the stub that already existed ✓

## Concerns
None.
