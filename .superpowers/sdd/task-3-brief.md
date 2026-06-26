### Task 3: LIVE / SYNC / CLND / THMB Code-Behind + GoLive

**Files:**
- Modify: `Views/PlaybackView.xaml.cs`
- Modify: `Views/PlaybackView.xaml` (add CLND Popup + Calendar)

**Interfaces:**
- Consumes: `_coordinator`, `Timeline`, `FilterDatePicker`, `_isPlaying`
- Produces: `_liveMode`, `_syncEnabled` fields; LiveBtn/SyncBtn/ClndBtn/ThmbBtn handlers; ToggleLiveMode, ToggleSync; CalendarPopup + Calendar control

- [ ] **Step 1: Add state fields near line 30**

```csharp
private bool _liveMode;
private bool _syncEnabled = true;
```

- [ ] **Step 2: Add PlayPauseBtn_Click unified handler**

Replace `PlayBtn_Click` and `PauseBtn_Click` with unified toggle:
```csharp
private void PlayPauseBtn_Click(object sender, RoutedEventArgs e) {
    if (_isPlaying) {
        _coordinator?.Pause();
        _isPlaying = false;
    } else {
        _coordinator?.Play();
        _isPlaying = true;
    }
    UpdateButtonStates();
}
```

- [ ] **Step 3: Subscribe Timeline.GoLiveRequested in OnPlaybackLoaded**

Add after line 180:
```csharp
Timeline.GoLiveRequested += OnTimelineGoLiveRequested;
```

Add unsubscribe in `OnPlaybackUnloaded` line 209:
```csharp
Timeline.GoLiveRequested -= OnTimelineGoLiveRequested;
```

Add handler method:
```csharp
private void OnTimelineGoLiveRequested(object? sender, EventArgs e) {
    ToggleLiveMode();
}
```

- [ ] **Step 4: LiveBtn handlers + ToggleLiveMode**

```csharp
private void LiveBtn_Checked(object sender, RoutedEventArgs e) {
    ToggleLiveMode();
}

private void LiveBtn_Unchecked(object sender, RoutedEventArgs e) {
    _liveMode = false;
}

private void ToggleLiveMode() {
    _liveMode = true;
    LiveBtn.IsChecked = true;
    // Seek to current time
    var nowSecs = (DateTime.Now - DateTime.Today).TotalSeconds;
    Timeline.PositionSeconds = Math.Clamp(nowSecs, 0, 86400);
    Timeline_SeekRequested(Math.Clamp(nowSecs, 0, 86400));
    UpdateButtonStates();
}
```

Add live mode exit when user interacts with timeline:
```csharp
// In OnTimelinePositionChanged or Timeline_PreviewMouseLeftButtonDown equivalent
private void OnTimelinePositionChanged(object? sender, double seconds) {
    if (_liveMode) {
        _liveMode = false;
        LiveBtn.IsChecked = false;  // Style trigger handles Background change
    }
    // ... existing position update code
}
```

- [ ] **Step 5: SyncBtn handlers + ToggleSync**

```csharp
private void SyncBtn_Checked(object sender, RoutedEventArgs e) {
    _syncEnabled = true;
    if (_coordinator is not null) {
        // Re-sync all players to master
        var masterId = _coordinator.GetMasterId();
        if (masterId is not null) {
            foreach (var player in _activePlayers) {
                if (player.CameraId != masterId) {
                    player.SetMaster(false);
                }
            }
        }
    }
    // NxBlueToggleButton style handles Background change via trigger
}

private void SyncBtn_Unchecked(object sender, RoutedEventArgs e) {
    _syncEnabled = false;
}
```

- [ ] **Step 6: ClndBtn_Click with Popup + Calendar**

In XAML (add inside `</Grid>` but before the closing tag for Row 3, or as overlay):

```xml
<!-- CLND Calendar Popup -->
<Popup x:Name="CalendarPopup" Grid.RowSpan="4"
       PlacementTarget="{Binding ElementName=ClndBtn}"
       Placement="Top" StaysOpen="False"
       AllowsTransparency="True"
       PopupAnimation="Slide">
    <Border Background="{DynamicResource SurfaceBrush}"
            BorderBrush="{DynamicResource BorderBrush}"
            BorderThickness="1" CornerRadius="6"
            Padding="8" Effect="{StaticResource DropShadowEffect}">
        <StackPanel>
            <Calendar x:Name="ClndCalendar"
                      Background="Transparent"
                      Foreground="{DynamicResource TextBrush}"
                      BorderThickness="0"
                      SelectedDatesChanged="ClndCalendar_SelectedDatesChanged"/>
            <Button Content="今天" Style="{StaticResource SecondaryButton}"
                    Height="22" FontSize="10" Padding="8,0" Margin="0,4,0,0"
                    Click="ClndCalendar_Today_Click"/>
        </StackPanel>
    </Border>
</Popup>
```

Code-behind:
```csharp
private void ClndBtn_Click(object sender, RoutedEventArgs e) {
    ClndCalendar.DisplayDate = FilterDatePicker.SelectedDate ?? DateTime.Today;
    CalendarPopup.IsOpen = true;
}

private void ClndCalendar_SelectedDatesChanged(object? sender, SelectionChangedEventArgs e) {
    if (ClndCalendar.SelectedDate.HasValue) {
        FilterDatePicker.SelectedDate = ClndCalendar.SelectedDate.Value;
        CalendarPopup.IsOpen = false;
        // Trigger date change
        FilterDatePicker_SelectedDateChanged(FilterDatePicker, null);
    }
}

private void ClndCalendar_Today_Click(object sender, RoutedEventArgs e) {
    FilterDatePicker.SelectedDate = DateTime.Today;
    CalendarPopup.IsOpen = false;
    FilterDatePicker_SelectedDateChanged(FilterDatePicker, null);
}
```

- [ ] **Step 7: ThmbBtn handlers**

```csharp
private void ThmbBtn_Checked(object sender, RoutedEventArgs e) {
    Timeline.ToggleThumbnails(true);
}

private void ThmbBtn_Unchecked(object sender, RoutedEventArgs e) {
    Timeline.ToggleThumbnails(false);
}
```

- [ ] **Step 8: Build and verify**

```powershell
dotnet build "D:\HeliVMS\HeliVMS\HeliVMS.csproj" 2>&1
```
Expected: 0 errors 0 warnings

---