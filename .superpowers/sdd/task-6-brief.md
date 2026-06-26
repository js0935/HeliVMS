### Task 6: THMB ThumbnailCanvas in NxTimeline

**Files:**
- Modify: `Controls/NxTimeline.xaml`
- Modify: `Controls/NxTimeline.xaml.cs`

- [ ] **Step 1: Add Row 0 ThumbnailCanvas to NxTimeline Grid**

Change Grid.RowDefinitions from 3 to 4:
```xml
<Grid>
    <Grid.RowDefinitions>
        <RowDefinition Height="Auto"/>   <!-- 0: ThumbnailStrip (48px when visible) -->
        <RowDefinition Height="24"/>     <!-- 1: ActivityCanvas -->
        <RowDefinition Height="*"/>      <!-- 2: CameraRowsCanvas -->
        <RowDefinition Height="16"/>     <!-- 3: TimeScaleCanvas -->
    </Grid.RowDefinitions>
```

Add ThumbnailCanvas:
```xml
<Canvas x:Name="ThumbnailCanvas" Grid.Row="0"
        Height="48" ClipToBounds="True"
        Background="{DynamicResource OverlayBrush}"
        Visibility="Collapsed"/>
```

- [ ] **Step 2: Update existing elements Grid.Row + Grid.RowSpan offsets**

All existing elements +1:
- ActivityCanvas: `Grid.Row="0"` → `Grid.Row="1"`
- Border separator for Activity: `Grid.Row="0"` → `Grid.Row="1"`
- CameraRowsCanvas Border: `Grid.Row="1"` → `Grid.Row="2"`
- TimeScaleCanvas Border: `Grid.Row="2"` → `Grid.Row="3"`
- SelectionCanvas: `Grid.RowSpan="3"` → `Grid.RowSpan="4"`
- PositionCanvas: `Grid.RowSpan="3"` → `Grid.RowSpan="4"`
- BookmarksCanvas: `Grid.RowSpan="3"` → `Grid.RowSpan="4"`
- ZoomLabel: `Grid.Row="2"` → `Grid.Row="3"`
- GoLiveBtn: `Grid.Row="1"` → `Grid.Row="2"`

- [ ] **Step 3: Add _showThumbnails field + ToggleThumbnails + DrawThumbnails stub**

Add field:
```csharp
private bool _showThumbnails;
```

Add methods:
```csharp
public void ToggleThumbnails(bool show) {
    _showThumbnails = show;
    ThumbnailCanvas.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    if (show) DrawThumbnails();
    else ThumbnailCanvas.Children.Clear();
}

private void DrawThumbnails() {
    ThumbnailCanvas.Children.Clear();
    var w = ThumbnailCanvas.ActualWidth;
    if (w <= 0 || _segments.Count == 0) return;

    var totalSecs = ZoomLevels[_zoomIndex];
    var viewEnd = _viewStartSeconds + totalSecs;
    var h = ThumbnailCanvas.ActualHeight;

    // Placeholder rendering: draw time-labelled blocks at segment boundaries
    foreach (var seg in _segments) {
        if (!IsTypeVisible(seg.RecordType)) continue;
        var segStart = (seg.StartTime - _timelineDay).TotalSeconds;
        var segEnd = seg.EndTime.HasValue
            ? (seg.EndTime.Value - _timelineDay).TotalSeconds
            : _viewStartSeconds + totalSecs;
        if (segEnd < _viewStartSeconds || segStart > viewEnd) continue;

        var x1 = Math.Max(0, SegsToThumbX(segStart));
        var x2 = Math.Min(w, SegsToThumbX(segEnd));
        var rectW = Math.Max(1, x2 - x1);

        var color = GetColorForRecordType(seg.RecordType);
        var rect = new Rectangle {
            Width = rectW, Height = 4,
            Fill = new SolidColorBrush(Color.FromArgb(60, color.R, color.G, color.B)),
            ToolTip = $"{seg.CameraId}: {seg.StartTime:HH:mm}"
        };
        Canvas.SetLeft(rect, x1);
        Canvas.SetTop(rect, h - 5);
        ThumbnailCanvas.Children.Add(rect);
    }
}

private double SegsToThumbX(double secs) {
    var w = ThumbnailCanvas.ActualWidth;
    if (w <= 0 || ZoomLevels[_zoomIndex] <= 0) return 0;
    return (secs - _viewStartSeconds) / ZoomLevels[_zoomIndex] * w;
}
```

- [ ] **Step 4: Add ThumbnailCanvas mouse events for hover tooltip**

```xml
PreviewMouseMove="ThumbnailCanvas_MouseMove"
```
Add handler:
```csharp
private void ThumbnailCanvas_MouseMove(object sender, MouseEventArgs e) {
    var pos = e.GetPosition(ThumbnailCanvas);
    var w = ThumbnailCanvas.ActualWidth;
    if (w <= 0) return;
    var secs = _viewStartSeconds + pos.X / w * ZoomLevels[_zoomIndex];
    var time = _timelineDay.AddSeconds(secs);
    ThumbnailCanvas.ToolTip = $"{time:HH:mm:ss}";
}
```

- [ ] **Step 5: Build and verify**

```powershell
dotnet build "D:\HeliVMS\HeliVMS\HeliVMS.csproj" 2>&1
```
Expected: 0 errors 0 warnings

---