# 雙向速度滑桿 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Transform the playback speed slider from forward-only (0.25x–32x) to bidirectional (-16x–16x) with Nx Witness-style click-hold temporary speed, mouse wheel support, and state-aware positioning.

**Architecture:** Single-file change to `Views/PlaybackView.xaml.cs` (~200 lines net). Two tasks: (1) formula + display updates (stand-alone), (2) interaction logic (click-hold, mouse wheel, state-aware, Fs speed cycle). Both tasks modify the same file, so coordination on field/method names is critical.

**Tech Stack:** WPF .NET 10, C#, no new dependencies.

## Global Constraints

- Build must produce 0 errors 0 warnings (`dotnet build "D:\HeliVMS\HeliVMS\HeliVMS.csproj"`)
- No hardcoded Color/Brush — use `DynamicResource` or `FindResource`
- Follow existing code patterns (private methods, same indentation, same comment style)

---

### Task 1: Formula + Display Updates

**Files:**
- Modify: `Views/PlaybackView.xaml.cs` lines 1986-2006 (SliderValueToSpeed, SliderToDisplayText, ShowSpeedOSDOnPlayers, UpdatePlayerSpeedBadges)
- Modify: `Views/PlaybackView.xaml.cs` lines 1977-1984 (SpeedSlider_ValueChanged)

**Interfaces:**
- Consumes: `_isPlaying` (line 44), `_coordinator.SetPlaybackRate(double)`
- Produces: `SliderValueToSpeed(double sliderVal, bool isPlaying)` — static, returns ± speed
- Produces: `SliderValueToSpeedPaused(double sliderVal)` — static, returns 0-center speed
- Produces: `SpeedToDisplayText(double speed)` — static, handles negatives

- [ ] **Step 1: Replace SliderValueToSpeed with bidirectional version**

Current (line 1986-1988):
```csharp
private static double SliderValueToSpeed(double sliderVal) {
    return Math.Round(0.25 * Math.Pow(2, sliderVal / 14.285714), 2);
}
```

Replace with:
```csharp
private double SliderValueToSpeed(double sliderVal) {
    if (!_isPlaying) return SliderValueToSpeedPaused(sliderVal);
    if (sliderVal == 50.0) return 1.0;
    var offset = (sliderVal - 50.0) / 12.5;
    return Math.Round(Math.Pow(2, offset), 2);
}

private static double SliderValueToSpeedPaused(double sliderVal) {
    if (sliderVal == 50.0) return 0.0;
    var sign = sliderVal > 50.0 ? 1.0 : -1.0;
    var offset = Math.Abs(sliderVal - 50.0) / 16.6667;
    return Math.Round(sign * 0.25 * Math.Pow(2, offset), 2);
}
```

Note: Changed from `static` to instance method (needs `_isPlaying`).

- [ ] **Step 2: Replace SliderToDisplayText with SpeedToDisplayText (handles negatives)**

Current (line 1990-1992):
```csharp
private static string SliderToDisplayText(double speed) {
    return speed >= 1.0 ? $"{(int)speed}x" : $"{speed:F2}".TrimEnd('0').TrimEnd('.') + "x";
}
```

Replace with:
```csharp
private static string SpeedToDisplayText(double speed) {
    if (speed == 0.0) return "0x";
    var prefix = speed < 0 ? "-" : "";
    var abs = Math.Abs(speed);
    return abs >= 1.0
        ? $"{prefix}{(int)abs}x"
        : $"{prefix}{abs:F2}".TrimEnd('0').TrimEnd('.') + "x";
}
```

- [ ] **Step 3: Update ShowSpeedOSDOnPlayers and UpdatePlayerSpeedBadges for negatives**

Current (lines 1994-2006):
```csharp
private void ShowSpeedOSDOnPlayers(double rate) {
    var text = rate >= 1.0 ? $"{(int)rate}x" : $"{rate:F2}".TrimEnd('0').TrimEnd('.') + "x";
    foreach (var player in _activePlayers) {
        player.ShowSpeedOSD(text);
    }
}

private void UpdatePlayerSpeedBadges(double rate) {
    var text = rate >= 1.0 ? $"{(int)rate}x" : $"{rate:F2}".TrimEnd('0').TrimEnd('.') + "x";
    foreach (var player in _activePlayers) {
        player.SetSpeedText(text);
    }
}
```

Replace with:
```csharp
private void ShowSpeedOSDOnPlayers(double rate) {
    var text = SpeedToDisplayText(rate);
    foreach (var player in _activePlayers) {
        player.ShowSpeedOSD(text);
    }
}

private void UpdatePlayerSpeedBadges(double rate) {
    var text = SpeedToDisplayText(rate);
    foreach (var player in _activePlayers) {
        player.SetSpeedText(text);
    }
}
```

- [ ] **Step 4: Update SpeedSlider_ValueChanged to pass _isPlaying**

Current (lines 1977-1984):
```csharp
private void SpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) {
    var speed = SliderValueToSpeed(SpeedSlider.Value);
    _coordinator?.SetPlaybackRate(speed);
    UpdatePlayerSpeedBadges(speed);
    ShowSpeedOSDOnPlayers(speed);
    SpeedLabel.Text = SliderToDisplayText(speed);
    UpdateFsSpeedDisplay();
}
```

Replace with:
```csharp
private void SpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) {
    var speed = SliderValueToSpeed(SpeedSlider.Value);
    _coordinator?.SetPlaybackRate(speed);
    UpdatePlayerSpeedBadges(speed);
    ShowSpeedOSDOnPlayers(speed);
    SpeedLabel.Text = SpeedToDisplayText(speed);
    UpdateFsSpeedDisplay();
}
```

- [ ] **Step 5: Build and verify**

Run:
```powershell
dotnet build "D:\HeliVMS\HeliVMS\HeliVMS.csproj"
```
Expected: 0 errors 0 warnings

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "Task 1: 雙向速度公式 — 倒帶支援 + 負速度顯示"
```

---

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
