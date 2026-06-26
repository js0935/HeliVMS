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