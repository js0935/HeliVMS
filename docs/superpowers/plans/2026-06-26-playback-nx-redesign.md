# PlaybackView Nx Redesign Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Redesign PlaybackView to Nx Witness layout: left sidebar (search + channel) + center video grid + bottom NxTimeline + mini transport bar.

**Architecture:** Rewrite XAML layout; keep 4293-line code-behind logic intact but update element references; replace TimelineControl with NxTimeline.

**Tech Stack:** WPF .NET 10, DynamicResource colors, PathGeometry icons

## Global Constraints
- 0 errors 0 warnings on dotnet build
- All colors via DynamicResource, no hardcoded colors
- All icons via StaticResource PathGeometry
- Keep all existing code-behind logic untouched

---

### Task 1: Rewrite PlaybackView.xaml

**Files:**
- Modify: `Views/PlaybackView.xaml` (complete rewrite)
- No new files

**Layout structure:**
```
Grid (6 rows)
├── Row 0: ToolbarRow (keep existing content, remove SearchToggleBtn)
├── Row 1: MainContent (Grid 2 cols)
│   ├── Col 0: LeftSidebar (280px) - search top + channel bottom
│   └── Col 1: VideoArea - PlaybackGrid + EmptyPrompt + overlays
├── Row 2: NxTimeline (full-width)
├── Row 3: TransportBar (simplified single row)
├── Row 4-5: FullScreenOverlay + ShortcutsOverlay (span all rows)
```

**New left sidebar structure:**
- Grid.Row=0: Search header "搜尋錄影" with IconSearch
- Grid.Row=1: Quick presets (本日/1H/6H/24H/本週)
- Grid.Row=2: Time range (從/至 DatePicker + hour/min/sec TextBoxes)  
- Grid.Row=3: Filters (類型 Combo + 頻道 TextBox + 僅動態 CheckBox)
- Grid.Row=4: Search buttons row (搜尋/清除/關閉)
- Grid.Row=5: SearchStatusText
- Separator line
- Grid.Row=6: Channel header "頻道選擇" + count badge + collapse button
- Grid.Row=7: Quick buttons (全選/清除)
- Grid.Row=8: Channel search box
- Grid.Row=9: Channel list (ScrollViewer + ChannelListPanel)
- Grid.Row=10: Bottom status (ChannelStatusText + RefreshChannelBtn)

**NxTimeline zone:**
- `<controls:NxTimeline x:Name="Timeline" Grid.Row="2" .../>` (full-width, no column split)
- Height ~140px

**Transport bar (simplified):**
- Same 25 columns concept but more compact (smaller margins, thinner buttons)
- Keep all transport buttons in same relative order
- Remove FpsBadge (move logic to PerfPanel)
- Remove StopAllBtn (keep in right side)
- Keep LoopPanel, SpeedCombo, PlaybackTimeText

**x:Name mapping (new → old for code-behind compat):**
| New Name | Old Name | Notes |
|----------|----------|-------|
| SearchPanel | (was SearchPanel) | Now Grid child, not overlay |
| SearchFromDate | SearchFromDate | Same |
| SearchToDate | SearchToDate | Same |
| SearchFromHour | SearchFromHour | Same |
| ... search fields | ... | Keep same names |
| SearchResultList | SearchResultList | Same |
| SearchLoadingOverlay | SearchLoadingOverlay | Same |
| SearchStatusText | SearchStatusText | Same |
| ExecuteSearchBtn | ExecuteSearchBtn | Same |
| ChannelPanel | (was separate Border) | Now inside left sidebar |
| ChannelListPanel | ChannelListPanel | Same |
| ChannelSearchBox | ChannelSearchBox | Same |
| SelectAllBtn | SelectAllBtn | Same |
| ClearAllBtn | ClearAllBtn | Same |
| CollapseChannelBtn | CollapseChannelBtn | Same |
| ExpandChannelBtn | ExpandChannelBtn | Same |
| ChannelCountText | ChannelCountText | Same |
| ChannelStatusText | ChannelStatusText | Same |
| RefreshChannelBtn | RefreshChannelBtn | Same |
| Timeline | (was TimelineCtrl) | NxTimeline replaces TimelineControl |
| **SearchToggleBtn** | **REMOVED** | Search always visible |
| **SearchBackdrop** | **REMOVED** | Not needed |
| **BookmarkPanel** | **REMOVED** | NxTimeline renders bookmarks |
| **BookmarkListPanel** | **REMOVED** | Not needed |

- [ ] **Step 1: Write new PlaybackView.xaml**

Write the full XAML with:
- New grid layout (6 rows)
- Left sidebar (280px) with search (top) + channel (bottom)
- NxTimeline in full-width row (replaces TimelineControl)
- Simplified transport bar
- Keep FullScreenOverlay, ShortcutsOverlay, PerfPanel
- All DynamicResource colors, PathGeometry icons
- Keep same x:Name for code-behind compatible elements

- [ ] **Step 2: Build check**

```bash
dotnet build 2>&1
```
Expected: compiles (may have warnings about unused elements in code-behind, but no errors)

---

### Task 2: Update PlaybackView.xaml.cs references

**Files:**
- Modify: `Views/PlaybackView.xaml.cs` (update element references, NxTimeline binding)

**Changes needed:**
1. Replace `TimelineCtrl` references → `Timeline` (NxTimeline)
2. Remove SearchBackdrop/SearchToggleBtn references
3. Remove BookmarkPanel/BookmarkListPanel references
4. Add NxTimeline event handlers
5. Update channel panel toggle (CollapseChannelBtn now hides left sidebar width)

**Specific code changes:**

- [ ] **Step 1: Replace TimelineCtrl → Timeline**

In PlaybackView.xaml.cs, replace all occurrences of `TimelineCtrl` with `Timeline`:
- Field declarations
- Event handler registrations (`TimelineCtrl.SeekRequested`, `TimelineCtrl.PositionChanged`)
- Method calls (`TimelineCtrl.LoadSegments()`, `TimelineCtrl.SetPosition()`)

The NxTimeline API differs from TimelineControl:
```csharp
// OLD (TimelineControl):
TimelineCtrl.LoadSegments(cameraIds, segments);
TimelineCtrl.SetPosition(dateTime);
TimelineCtrl.SeekRequested += OnTimelineSeek;
TimelineCtrl.PositionChanged += OnTimelinePositionChanged;

// NEW (NxTimeline):
Timeline.LoadSegments(cameraIds, segments, cameraNames);
Timeline.SetPosition(dateTime);
Timeline.PositionChanged += OnTimelineSeek;
Timeline.SelectionChanged += OnTimelineSelectionChanged;
Timeline.ExportRangeRequested += OnTimelineExportRange;
Timeline.GoLiveRequested += OnTimelineGoLive;
```

- [ ] **Step 2: Remove SearchBackdrop references**

Find and remove:
```csharp
// Remove these:
SearchBackdrop.Visibility = ...
SearchBackdrop_MouseLeftButtonDown
// Also remove SearchToggleBtn references
```

- [ ] **Step 3: Remove BookmarkPanel references**

Find and remove:
```csharp
// Remove these:
BookmarkPanel.Width = 220; // or visibility toggle
BookmarkPanel.Visibility = ...
ClearBookmarksBtn_Click
```

- [ ] **Step 4: Update CollapseChannelBtn / ExpandChannelBtn**

Update to collapse the entire left sidebar column instead of just hiding the channel panel Border:
```csharp
private void CollapseChannelBtn_Click(object sender, RoutedEventArgs e) {
    LeftSidebarColumn.Width = new GridLength(0);
    ExpandChannelBtn.Visibility = Visibility.Visible;
    ExpandChannelBtn.Content = "▶";
}

private void ExpandChannelBtn_Click(object sender, RoutedEventArgs e) {
    LeftSidebarColumn.Width = new GridLength(280);
    ExpandChannelBtn.Visibility = Visibility.Collapsed;
}
```

- [ ] **Step 5: Build check**

```bash
dotnet build 2>&1
```
Expected: 0 errors 0 warnings

---

### Task 3: Wire NxTimeline data flow

**Files:**
- Modify: `Views/PlaybackView.xaml.cs` (add NxTimeline integration)

- [ ] **Step 1: Initialize NxTimeline on Load**

In the Loaded handler or after channel selection changes:
```csharp
private void UpdateTimeline() {
    var selectedIds = _channelItems
        .Where(c => c.IsChecked)
        .Select(c => c.CameraId)
        .ToList();
    
    if (selectedIds.Count == 0) return;
    
    var segments = _recordingService.GetRecordings(selectedIds, _currentDate);
    var names = _channelItems
        .Where(c => c.IsChecked)
        .ToDictionary(c => c.CameraId, c => c.CameraName ?? c.CameraId);
    
    Timeline.LoadSegments(selectedIds, segments, names);
}
```

- [ ] **Step 2: Handle NxTimeline events**

```csharp
private void OnTimelineSeek(object? sender, double seconds) {
    var targetTime = _currentDate.Date + TimeSpan.FromSeconds(seconds);
    SeekAll(targetTime);
}

private void OnTimelineSelectionChanged(object? sender, (DateTime start, DateTime end)? selection) {
    if (selection.HasValue) {
        _exportStart = selection.Value.start;
        _exportEnd = selection.Value.end;
        ExportClipBtn.IsEnabled = true;
    } else {
        ExportClipBtn.IsEnabled = false;
    }
}

private void OnTimelineExportRange(object? sender, (DateTime start, DateTime end) range) {
    _exportStart = range.start;
    _exportEnd = range.end;
    ExportClipBtn_Click(sender, new RoutedEventArgs());
}

private void OnTimelineGoLive(object? sender, EventArgs e) {
    FilterDatePicker.SelectedDate = DateTime.Today;
    PlaybackHourCombo.SelectedItem = DateTime.Now.Hour.ToString("00");
    LoadTimeRangeBtn_Click(sender, new RoutedEventArgs());
}
```

- [ ] **Step 3: Sync playback position to timeline**

In the playback tick/dispatcher timer that updates positions:
```csharp
// Add this line where position is updated
var currentSecs = (currentPlayTime - _currentDate.Date).TotalSeconds;
Timeline.PositionSeconds = currentSecs;
```

- [ ] **Step 4: Build check**

```bash
dotnet build 2>&1
```
Expected: 0 errors 0 warnings

---

### Task 4: Clean up and verify

**Files:**
- Verify: `Views/PlaybackView.xaml`
- Verify: `Views/PlaybackView.xaml.cs`

- [ ] **Step 1: Remove unused using statements from PlaybackView.xaml.cs**

Run code cleanup or manually remove any unused usings.

- [ ] **Step 2: Full build**

```bash
dotnet build 2>&1
```

- [ ] **Step 3: Verify key functionality**

Manual verification checklist:
1. Toolbar: date picker works, hour selector works, load button loads recordings
2. Left sidebar: search presets work, time range inputs work, search button triggers search
3. Left sidebar: channel checkboxes toggle, select all/clear all work
4. Channel panel collapse/expand works (left sidebar shrinks to 0/restores to 280px)
5. NxTimeline: shows colored recording bars for selected channels
6. NxTimeline: zoom buttons (1H/6H/12H/24H) work
7. NxTimeline: click to seek updates video position
8. Transport bar: play/pause/step/speed controls work
9. Full screen: F11 enters/exits, transport controls work
10. Bookmarks: B key adds bookmark, shown on timeline
11. Export: selection bracket + export button work
