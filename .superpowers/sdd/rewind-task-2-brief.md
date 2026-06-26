### Task 2: Click-Temporary + Mouse Wheel + State-Aware + Fs Speed Cycle

**Files:**
- Modify: `Views/PlaybackView.xaml.cs` — add 5 fields, add 4 event handlers, update PlayPauseBtn_Click, update FsTransportBtn_Click "speed" case
- Modify: `Views/PlaybackView.xaml` — wire PreviewMouse* events to SpeedSlider

**Interfaces:**
- Consumes: `SliderValueToSpeed(double, bool)` (Task 1), `SpeedToDisplayText(double)` (Task 1), `_isPlaying`
- Produces: `_isTemporarySpeed`, `_defaultSpeed` fields; `SpeedSlider_PreviewMouseDown/Up/Move/Wheel` handlers

- [ ] **Step 1: Add state fields after `_syncEnabled` (line 50)**

Add after line 50:
```csharp
private bool _isTemporarySpeed;
private double _defaultSpeed = 1.0;
```

- [ ] **Step 2: Update PlayPauseBtn_Click to update default speed + slider position**

Current (lines 1895-1904):
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

Replace with:
```csharp
private void PlayPauseBtn_Click(object sender, RoutedEventArgs e) {
    if (_isPlaying) {
        _coordinator?.Pause();
        _isPlaying = false;
        _defaultSpeed = 0.0;
    } else {
        _coordinator?.Play();
        _isPlaying = true;
        _defaultSpeed = 1.0;
    }
    // Recalculate speed at current slider position with new state
    if (!_isTemporarySpeed) {
        var speed = SliderValueToSpeed(SpeedSlider.Value);
        _coordinator?.SetPlaybackRate(speed);
        UpdatePlayerSpeedBadges(speed);
        ShowSpeedOSDOnPlayers(speed);
        SpeedLabel.Text = SpeedToDisplayText(speed);
        UpdateFsSpeedDisplay();
    }
    UpdateButtonStates();
}
```

- [ ] **Step 3: Add temporary speed handlers**

Add after `SpeedSlider_ValueChanged`:
```csharp
private Point _sliderMouseDownPos;

private void SpeedSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e) {
    _sliderMouseDownPos = e.GetPosition(SpeedSlider);
    _isTemporarySpeed = true;
    _defaultSpeed = _isPlaying ? 1.0 : 0.0;
    SpeedSlider.CaptureMouse();
}

private void SpeedSlider_PreviewMouseMove(object sender, MouseEventArgs e) {
    if (e.LeftButton == MouseButtonState.Pressed && _isTemporarySpeed) {
        var pos = e.GetPosition(SpeedSlider);
        if (Math.Abs(pos.X - _sliderMouseDownPos.X) > 5 ||
            Math.Abs(pos.Y - _sliderMouseDownPos.Y) > 5) {
            _isTemporarySpeed = false; // became a drag
        }
    }
}

private void SpeedSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e) {
    SpeedSlider.ReleaseMouseCapture();
    if (_isTemporarySpeed) {
        SpeedSlider.Value = 50; // revert to center
        _isTemporarySpeed = false;
        _coordinator?.SetPlaybackRate(_defaultSpeed);
        UpdatePlayerSpeedBadges(_defaultSpeed);
        ShowSpeedOSDOnPlayers(_defaultSpeed);
        SpeedLabel.Text = SpeedToDisplayText(_defaultSpeed);
        UpdateFsSpeedDisplay();
    }
}

private void SpeedSlider_PreviewMouseWheel(object sender, MouseWheelEventArgs e) {
    var delta = e.Delta > 0 ? 4 : -4;
    var newVal = Math.Clamp(SpeedSlider.Value + delta, 0, 100);
    SpeedSlider.Value = newVal;
    _isTemporarySpeed = true;
    _defaultSpeed = _isPlaying ? 1.0 : 0.0;
    e.Handled = true;
}
```

- [ ] **Step 4: Add SpeedSlider_MouseLeave to revert temporary speed**

Add after mouse wheel handler:
```csharp
private void SpeedSlider_MouseLeave(object sender, MouseEventArgs e) {
    if (_isTemporarySpeed) {
        _isTemporarySpeed = false;
        SpeedSlider.Value = 50;
        _coordinator?.SetPlaybackRate(_defaultSpeed);
        UpdatePlayerSpeedBadges(_defaultSpeed);
        ShowSpeedOSDOnPlayers(_defaultSpeed);
        SpeedLabel.Text = SpeedToDisplayText(_defaultSpeed);
        UpdateFsSpeedDisplay();
    }
}
```

- [ ] **Step 5: Update FsTransportBtn_Click "speed" case for new formula**

Current (lines 3413-3421):
```csharp
case "speed":
    var currentSpeed = SliderValueToSpeed(SpeedSlider.Value);
    var nextSpeed = currentSpeed >= 16 ? 1.0 : currentSpeed * 2;
    var sliderVal = nextSpeed switch {
        0.25 => 0, 0.5 => 25, 1.0 => 50, 2.0 => 75, 4.0 => 87, 8.0 => 94, 16.0 => 100,
        _ => 50
    };
    SpeedSlider.Value = sliderVal;
    break;
```

Replace with:
```csharp
case "speed": {
    var currentSpeed = Math.Abs(SliderValueToSpeed(SpeedSlider.Value));
    var nextSpeed = currentSpeed >= 16 ? 1.0 : currentSpeed * 2;
    var sliderVal = nextSpeed switch {
        1.0 => 50, 2.0 => 62, 4.0 => 75, 8.0 => 87, 16.0 => 100,
        _ => 50
    };
    SpeedSlider.Value = sliderVal;
    break;
}
```

Note: The slider values now map to the new bidirectional formula. At val=62 → ~2x, val=75 → 4x, val=87 → ~8x, val=100 → 16x.

- [ ] **Step 6: Wire up XAML events**

In `Views/PlaybackView.xaml`, add these event handlers to the SpeedSlider element (line ~834):
```xml
PreviewMouseDown="SpeedSlider_PreviewMouseDown"
PreviewMouseUp="SpeedSlider_PreviewMouseUp"
PreviewMouseMove="SpeedSlider_PreviewMouseMove"
PreviewMouseWheel="SpeedSlider_PreviewMouseWheel"
MouseLeave="SpeedSlider_MouseLeave"
```

Find the SpeedSlider in the XAML and add the attributes.

- [ ] **Step 7: Build and verify**

Run:
```powershell
dotnet build "D:\HeliVMS\HeliVMS\HeliVMS.csproj"
```
Expected: 0 errors 0 warnings

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "Task 2: 點擊暫時變速 + 滾輪 + 狀態感知 + Fs 速度循環"
```