# Activity Overview Bar Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a 24px motion activity overview bar above the NxTimeline camera rows, color-blended by recording type, with click-to-seek and hover tooltip.

**Architecture:** Add a Canvas layer inside NxTimeline's existing Grid layout; render colored rectangles from `_cameraRecordings` segment data per pixel-column bucket; route mouse events to existing seek/selection logic.

**Tech Stack:** WPF (.NET 10), Canvas rendering via `UIElementCollection`

## Global Constraints

- Build must produce 0 errors 0 warnings
- All colors must use `DynamicResource` from Colors.xaml (no hardcoded `#` colors)
- Existing functionality (seek, zoom, selection, bookmarks, type filter) must not regress

---

### Task 1: NxTimeline.xaml — Add ActivityCanvas Layer

**Files:**
- Modify: `D:\HeliVMS\HeliVMS\Controls\NxTimeline.xaml`

**Interfaces:**
- Consumes: NxTimeline's existing Grid layout with 2 rows
- Produces: New `ActivityCanvas` Canvas + updated Grid with 3 rows

- [ ] **Step 1: Read current NxTimeline.xaml**

Read `D:\HeliVMS\HeliVMS\Controls\NxTimeline.xaml` to understand current layout.

- [ ] **Step 2: Restructure Grid layout**

Change from 2-row Grid to 3-row Grid:

```xml
<Grid>
    <Grid.RowDefinitions>
        <RowDefinition Height="24"/>        <!-- NEW: ActivityCanvas -->
        <RowDefinition Height="*"/>         <!-- CameraRowsCanvas -->
        <RowDefinition Height="16"/>        <!-- TimeScaleCanvas -->
    </Grid.RowDefinitions>
```

Move existing elements:
- `CameraRowsCanvas` Border → Grid.Row="1"
- `TimeScaleCanvas` Border → Grid.Row="2"
- `SelectionCanvas` → Grid.RowSpan="3"
- `PositionCanvas` → Grid.RowSpan="3"
- `BookmarksCanvas` → Grid.RowSpan="3"
- `GoLiveBtn` → Grid.Row="1"
- `ZoomLabel` → Grid.Row="2"

- [ ] **Step 3: Add ActivityCanvas element**

```xml
<Canvas x:Name="ActivityCanvas" Grid.Row="0"
        Height="24" ClipToBounds="True"
        Background="Transparent"
        PreviewMouseLeftButtonDown="ActivityCanvas_MouseLeftButtonDown"
        PreviewMouseRightButtonDown="ActivityCanvas_PreviewMouseRightButtonDown"
        PreviewMouseRightButtonUp="ActivityCanvas_PreviewMouseRightButtonUp"
        PreviewMouseMove="ActivityCanvas_MouseMove"
        PreviewMouseWheel="ActivityCanvas_PreviewMouseWheel"/>
```

Add thin separator border at bottom of Row 0:

```xml
<Border Grid.Row="0" BorderThickness="0,0,0,1"
        BorderBrush="{DynamicResource BorderBrush}" Opacity="0.3"
        IsHitTestVisible="False" VerticalAlignment="Bottom" Height="1"/>
```

- [ ] **Step 4: Build and verify**

```powershell
dotnet build "D:\HeliVMS\HeliVMS\HeliVMS.csproj" 2>&1
```
Expected: 0 errors 0 warnings

- [ ] **Step 5: Commit**

```powershell
git add Controls/NxTimeline.xaml
git commit -m "NxTimeline: 新增 ActivityCanvas 圖層 + 三列 Grid 佈局"
```

---

### Task 2: NxTimeline.xaml.cs — DrawActivityOverview Implementation

**Files:**
- Modify: `D:\HeliVMS\HeliVMS\Controls\NxTimeline.xaml.cs`

**Interfaces:**
- Consumes: `_cameraRecordings` (`List<CameraRecordings>`), `_visibleStart`/`_visibleEnd` (DateTime), recording type filter flags
- Produces: `DrawActivityOverview()` method, called after segment loading and on zoom/scroll changes

- [ ] **Step 1: Read NxTimeline.xaml.cs to find render pipeline and segment data structures**

Read the file to understand:
- `_cameraRecordings` type (`List<CameraRecordings>` where `CameraRecordings` has `CameraId` and `Segments` list)
- `RecordedSegment` has `StartTime`, `EndTime`, `RecordType`
- `_visibleStart` / `_visibleEnd` fields
- Where `DrawCameraRows()` is called (to add `DrawActivityOverview()` call alongside)
- `RecordType` enum values: 0=Continuous, 1=Motion, 2=Alarm, 3=AI

- [ ] **Step 2: Add activity overview rendering fields**

Add to NxTimeline class:

```csharp
private const double ActivityBarHeight = 24.0;
private const double ActivityMinOpacity = 0.15;
private const double ActivityOpacityPerSegment = 0.20;
private const double ActivityMaxOpacity = 0.85;
// Type priority: Alarm(red) > Motion(orange) > AI(yellow) > Continuous(blue)
// These map to RecordType enum: 0=Continuous, 1=Motion, 2=Alarm, 3=AI
// Priority order (highest first): 2(Alarm), 1(Motion), 3(AI), 0(Continuous)
private static readonly int[] ActivityTypePriority = { 2, 1, 3, 0 };
```

- [ ] **Step 3: Implement DrawActivityOverview method**

```csharp
private void DrawActivityOverview() {
    ActivityCanvas.Children.Clear();
    if (_cameraRecordings is null || _cameraRecordings.Count == 0) return;

    double totalWidth = ActivityCanvas.ActualWidth;
    if (totalWidth <= 0) return;

    double visibleDuration = (_visibleEnd - _visibleStart).TotalSeconds;
    if (visibleDuration <= 0) return;

    double secPerPixel = visibleDuration / totalWidth;

    // Pre-compute colors per type for opacity blending
    var baseColors = new Dictionary<int, Color> {
        { 0, GetResourceColor("RecordingContinuousBrush") }, // Blue
        { 1, GetResourceColor("RecordingMotionBrush") },     // Orange
        { 2, GetResourceColor("RecordingAlarmBrush") },      // Red
        { 3, GetResourceColor("RecordingAiBrush") }          // Gold
    };

    // Pre-filter visible segments from all cameras
    var visibleSegments = _cameraRecordings
        .Where(cr => cr.Segments is not null)
        .SelectMany(cr => cr.Segments!)
        .Where(s => s.EndTime > _visibleStart && s.StartTime < _visibleEnd)
        .ToList();

    // Layer all rectangles on a single WriteableBitmap-like approach using Shapes
    // For performance with many columns, batch similar-column colors
    var rects = new List<System.Windows.Shapes.Rectangle>();
    int prevType = -1;
    double prevOpacity = 0;
    double runStart = 0;

    for (int x = 0; x < (int)totalWidth; x++) {
        double bucketStartSec = x * secPerPixel;
        double bucketEndSec = (x + 1) * secPerPixel;
        DateTime bucketStart = _visibleStart.AddSeconds(bucketStartSec);
        DateTime bucketEnd = _visibleStart.AddSeconds(bucketEndSec);

        // Count segments overlapping this bucket by type
        int[] counts = new int[4];
        foreach (var seg in visibleSegments) {
            if (seg.EndTime <= bucketStart || seg.StartTime >= bucketEnd) continue;
            int t = (int)seg.RecordType;
            if (t >= 0 && t < 4) counts[t]++;
        }

        // Determine dominant type by priority
        int dominantType = -1;
        int maxCount = 0;
        foreach (int pri in ActivityTypePriority) {
            if (counts[pri] > 0) {
                dominantType = pri;
                maxCount = counts[pri];
                break;
            }
        }

        if (dominantType < 0) {
            // No activity — flush any running segment
            if (prevType >= 0) {
                AddActivityRect(rects, baseColors[prevType], prevOpacity, runStart, x, totalWidth);
                prevType = -1;
            }
            continue;
        }

        double opacity = Math.Min(ActivityMinOpacity + maxCount * ActivityOpacityPerSegment, ActivityMaxOpacity);

        // Merge adjacent same-color/same-opacity
        if (dominantType == prevType && Math.Abs(opacity - prevOpacity) < 0.01) {
            continue; // extend run
        }

        if (prevType >= 0) {
            AddActivityRect(rects, baseColors[prevType], prevOpacity, runStart, x, totalWidth);
        }
        prevType = dominantType;
        prevOpacity = opacity;
        runStart = x;
    }

    // Flush last run
    if (prevType >= 0) {
        AddActivityRect(rects, baseColors[prevType], prevOpacity, runStart, (int)totalWidth, totalWidth);
    }

    // Add all rects to canvas
    foreach (var r in rects) {
        ActivityCanvas.Children.Add(r);
    }
}

private static void AddActivityRect(List<System.Windows.Shapes.Rectangle> rects, Color baseColor, double opacity, double start, double end, double totalWidth) {
    double left = start;
    double width = end - start;
    if (width <= 0) width = 1;

    var brush = new SolidColorBrush(Color.FromArgb(
        (byte)(opacity * 255),
        baseColor.R, baseColor.G, baseColor.B));
    brush.Freeze();

    rects.Add(new System.Windows.Shapes.Rectangle {
        Width = width,
        Height = ActivityBarHeight,
        Fill = brush,
        Canvas.Left = left,
        Canvas.Top = 0
    });
}

private static Color GetResourceColor(string resourceKey) {
    if (System.Windows.Application.Current?.TryFindResource(resourceKey) is SolidColorBrush scb)
        return scb.Color;
    // Resource color returns
    return resourceKey switch {
        "RecordingContinuousBrush" => Color.FromRgb(33, 150, 243), // Blue
        "RecordingMotionBrush" => Color.FromRgb(255, 87, 34),      // Orange
        "RecordingAlarmBrush" => Color.FromRgb(244, 67, 54),       // Red
        "RecordingAiBrush" => Color.FromRgb(255, 193, 7),          // Gold
        _ => Colors.Gray
    };
}
```

- [ ] **Step 4: Find render pipeline and add DrawActivityOverview call**

Search for where `DrawCameraRows()` is called and add `DrawActivityOverview()` right after it. Also call it after segments are loaded:

Find locations like:
- After `LoadSegments()` → calls `DrawCameraRows()`, `DrawTimeScale()`, etc.
- After zoom/pan changes → same paint methods

Add `DrawActivityOverview()` after each `DrawCameraRows()` call.

- [ ] **Step 5: Build and verify**

```powershell
dotnet build "D:\HeliVMS\HeliVMS\HeliVMS.csproj" 2>&1
```
Expected: 0 errors 0 warnings

- [ ] **Step 6: Commit**

```powershell
git add Controls/NxTimeline.xaml.cs
git commit -m "NxTimeline: 實作 DrawActivityOverview 活動熱度渲染"
```

---

### Task 3: ActivityCanvas Mouse Interaction

**Files:**
- Modify: `D:\HeliVMS\HeliVMS\Controls\NxTimeline.xaml.cs`

**Interfaces:**
- Consumes: ActivityCanvas mouse events
- Produces: Click-to-seek, right-drag selection, hover tooltip

- [ ] **Step 1: Add click-to-seek handler**

```csharp
private void ActivityCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) {
    var pos = e.GetPosition(ActivityCanvas);
    double ratio = pos.X / ActivityCanvas.ActualWidth;
    double totalSec = (_visibleEnd - _visibleStart).TotalSeconds;
    var target = _visibleStart.AddSeconds(ratio * totalSec);
    SetPositionSilent(target);
    PositionChanged?.Invoke(this, target);

    // Capture mouse for drag (same as CameraRowsCanvas)
    ActivityCanvas.CaptureMouse();
    _isDragging = true;
    _dragStartTime = target;
}

private void ActivityCanvas_MouseMove(object sender, MouseEventArgs e) {
    var pos = e.GetPosition(ActivityCanvas);
    double ratio = pos.X / ActivityCanvas.ActualWidth;
    double totalSec = (_visibleEnd - _visibleStart).TotalSeconds;
    var hoverTime = _visibleStart.AddSeconds(ratio * totalSec);

    // Update tooltip
    UpdateActivityTooltip(hoverTime, pos);

    // Handle selection drag
    if (_isDragging && e.RightButton == MouseButtonState.Pressed) {
        // Reuse existing selection logic via CameraRowsCanvas approach
        HandleSelectionDrag(pos);
    }
}

private void ActivityCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) {
    if (_isDragging) {
        ActivityCanvas.ReleaseMouseCapture();
        _isDragging = false;
    }
}
```

- [ ] **Step 2: Add tooltip helper**

```csharp
private void UpdateActivityTooltip(DateTime time, Point position) {
    // Count segments at this position
    double totalSec = (_visibleEnd - _visibleStart).TotalSeconds;
    double pixelSec = totalSec / ActivityCanvas.ActualWidth;
    var bucketStart = time;
    var bucketEnd = time.AddSeconds(pixelSec);

    int[] counts = new int[4];
    foreach (var cr in _cameraRecordings) {
        if (cr.Segments is null) continue;
        foreach (var seg in cr.Segments) {
            if (seg.EndTime <= bucketStart || seg.StartTime >= bucketEnd) continue;
            int t = (int)seg.RecordType;
            if (t >= 0 && t < 4) counts[t]++;
        }
    }

    string tooltip = $"{time:HH:mm:ss} — {counts.Sum()} segments";
    var parts = new List<string>();
    if (counts[0] > 0) parts.Add($"Continuous {counts[0]}");
    if (counts[1] > 0) parts.Add($"Motion {counts[1]}");
    if (counts[2] > 0) parts.Add($"Alarm {counts[2]}");
    if (counts[3] > 0) parts.Add($"AI {counts[3]}");
    if (parts.Count > 0) tooltip += $" ({string.Join(" / ", parts)})";

    ActivityCanvas.ToolTip = tooltip;
}

private void HandleSelectionDrag(Point pos) {
    // Reuse existing right-drag selection logic
    // _selectionStart / _selectionEnd are standard fields in NxTimeline
    double ratio = pos.X / ActivityCanvas.ActualWidth;
    double totalSec = (_visibleEnd - _visibleStart).TotalSeconds;
    var currentTime = _visibleStart.AddSeconds(ratio * totalSec);

    if (_selectionStart is null) {
        _selectionStart = currentTime;
        _selectionEnd = currentTime;
    } else {
        _selectionEnd = currentTime;
    }
    DrawSelection();
    SelectionChanged?.Invoke(this, EventArgs.Empty);
}
```

- [ ] **Step 3: Wire right-click drag on ActivityCanvas**

```csharp
private void ActivityCanvas_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e) {
    var pos = e.GetPosition(ActivityCanvas);
    double ratio = pos.X / ActivityCanvas.ActualWidth;
    double totalSec = (_visibleEnd - _visibleStart).TotalSeconds;
    _selectionStart = _visibleStart.AddSeconds(ratio * totalSec);
    _selectionEnd = null;
    ActivityCanvas.CaptureMouse();
    _isDragging = true;
    e.Handled = true;
}

private void ActivityCanvas_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e) {
    if (_isDragging) {
        ActivityCanvas.ReleaseMouseCapture();
        _isDragging = false;
    }
    e.Handled = true;
}

private void ActivityCanvas_PreviewMouseWheel(object sender, MouseWheelEventArgs e) {
    // Delegate to existing zoom handler
    ZoomAtMouse(e.Delta > 0, e.GetPosition(ActivityCanvas));
    e.Handled = true;
}
```

- [ ] **Step 4: Add required fields if not already present**

Check if `_isDragging` and `_selectionStart`/`_selectionEnd` fields exist. If not, add them:

```csharp
private bool _isDragging;
private DateTime? _selectionStart;
private DateTime? _selectionEnd;
```

Check if `DrawSelection()` method exists. If not, add stub:

```csharp
private void DrawSelection() {
    SelectionCanvas.Children.Clear();
    if (_selectionStart is null || _selectionEnd is null) return;

    var start = (DateTime)_selectionStart;
    var end = (DateTime)_selectionEnd;
    if (start > end) (start, end) = (end, start);

    double totalSec = (_visibleEnd - _visibleStart).TotalSeconds;
    double left = (start - _visibleStart).TotalSeconds / totalSec * SelectionCanvas.ActualWidth;
    double width = (end - start).TotalSeconds / totalSec * SelectionCanvas.ActualWidth;

    var rect = new System.Windows.Shapes.Rectangle {
        Width = Math.Max(width, 1),
        Height = SelectionCanvas.ActualHeight,
        Fill = new SolidColorBrush(Color.FromArgb(40, 33, 150, 243)),
        Canvas.Left = left
    };
    SelectionCanvas.Children.Add(rect);
}
```

- [ ] **Step 5: Build and verify**

```powershell
dotnet build "D:\HeliVMS\HeliVMS\HeliVMS.csproj" 2>&1
```
Expected: 0 errors 0 warnings

- [ ] **Step 6: Commit**

```powershell
git add Controls/NxTimeline.xaml.cs
git commit -m "NxTimeline: ActivityCanvas 滑鼠互動 — seek/selection/tooltip"
```

---

### Task 4: Wire DrawActivityOverview into Render Pipeline

**Files:**
- Modify: `D:\HeliVMS\HeliVMS\Controls\NxTimeline.xaml.cs`

- [ ] **Step 1: Add DrawActivityOverview to all paint entry points**

Search for ALL calls to `DrawCameraRows()` in the file. Add `DrawActivityOverview()` immediately after each one.

Typical locations:
- In `LoadSegments()` method (after segment data is updated)
- In zoom change handler
- In pan/scroll handler
- In `InvalidateVisual()` or paint override

- [ ] **Step 2: Add InvalidateActivity() public method**

For external callers (PlaybackView) to trigger activity bar redraw:

```csharp
public void InvalidateActivity() {
    if (Dispatcher.CheckAccess())
        DrawActivityOverview();
    else
        Dispatcher.InvokeAsync(DrawActivityOverview);
}
```

- [ ] **Step 3: Build and verify**

```powershell
dotnet build "D:\HeliVMS\HeliVMS\HeliVMS.csproj" 2>&1
```
Expected: 0 errors 0 warnings

- [ ] **Step 4: Commit**

```powershell
git add Controls/NxTimeline.xaml.cs
git commit -m "NxTimeline: DrawActivityOverview 整合至渲染管線"
```

---

### Task 5: PlaybackView Height Adjustment

**Files:**
- Modify: `D:\HeliVMS\HeliVMS\Views\PlaybackView.xaml`

- [ ] **Step 1: Check NxTimeline height in PlaybackView**

The NxTimeline is declared as:
```xml
<controls:NxTimeline x:Name="Timeline" Height="180" .../>
```

Change to `Height="204"` (180 + 24 for activity bar) or remove fixed height and let it auto-size.

Since NxTimeline's internal Grid now has `RowDefinition Height="24"` + `*` + `16`, the total height depends on the `*` row. Remove the fixed height and let the parent Grid row define the height, or increase to 204:

```xml
<controls:NxTimeline x:Name="Timeline" Height="204" .../>
```

- [ ] **Step 2: Build and verify**

```powershell
dotnet build "D:\HeliVMS\HeliVMS\HeliVMS.csproj" 2>&1
```
Expected: 0 errors 0 warnings

- [ ] **Step 3: Commit**

```powershell
git add Views/PlaybackView.xaml
git commit -m "PlaybackView: NxTimeline 高度 180→204 配合 ActivityCanvas"
```

---

### Task 6: Final Build & Push

- [ ] **Step 1: Full build**

```powershell
dotnet build "D:\HeliVMS\HeliVMS\HeliVMS.csproj" 2>&1
```
Expected: 0 errors 0 warnings

- [ ] **Step 2: Commit any remaining changes**

```powershell
git add -A
git commit -m "Activity Overview Bar: 完整實作活動熱度概覽列"
git push
```
