### Task 1: Speed Slider — ComboBox → 連續對數 Slider

**Files:**
- Modify: `Views/PlaybackView.xaml` lines 823-835
- Modify: `Views/PlaybackView.xaml.cs` lines 1916-1941

**Interfaces:**
- Consumes: `SpeedCombo_SelectionChanged` → 移除；`_coordinator.SetPlaybackRate(rate)`
- Produces: `SpeedSlider_ValueChanged` handler; `SliderValueToSpeed()` helper; `SliderToDisplayText()` helper

- [ ] **Step 1: Replace XAML ComboBox + label with Slider**

Replace lines 823-835:
```xml
<TextBlock Grid.Column="10" Text="速度" VerticalAlignment="Center"
           Foreground="{DynamicResource SecondaryTextBrush}"
           FontSize="10" FontWeight="SemiBold" Margin="0,0,2,0"/>
<ComboBox Grid.Column="11" x:Name="SpeedCombo" ... />
```

With:
```xml
<Border Grid.Column="10" x:Name="SpeedSliderPanel" MinWidth="120"
        Background="{DynamicResource SecondarySurfaceBrush}"
        CornerRadius="4" Padding="6,0" Margin="4,0,0,0">
    <Grid>
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width="Auto"/>
            <ColumnDefinition Width="*"/>
        </Grid.ColumnDefinitions>
        <TextBlock x:Name="SpeedLabel" Text="1x" FontSize="10" FontWeight="SemiBold"
                   Foreground="{DynamicResource TextBrush}"
                   VerticalAlignment="Center" MinWidth="24"/>
        <Slider Grid.Column="1" x:Name="SpeedSlider"
                Minimum="0" Maximum="100" Value="50"
                TickFrequency="1" IsSnapToTickEnabled="False"
                Width="100" Height="20" Margin="4,0,0,0"
                ValueChanged="SpeedSlider_ValueChanged"
                Style="{StaticResource ModernSlider}"
                VerticalAlignment="Center"/>
    </Grid>
</Border>
```

- [ ] **Step 2: Add SpeedSlider_ValueChanged handler + helpers**

Add after `StopAllBtn_Click` (line 1914):

```csharp
private void SpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) {
    var speed = SliderValueToSpeed(SpeedSlider.Value);
    _coordinator?.SetPlaybackRate(speed);
    UpdatePlayerSpeedBadges(speed);
    ShowSpeedOSDOnPlayers(speed);
    SpeedLabel.Text = SliderToDisplayText(speed);
    UpdateFsSpeedDisplay();
}

private static double SliderValueToSpeed(double sliderVal) {
    // Logarithmic: 0→0.25x, 50→1x, 100→32x
    return Math.Round(0.25 * Math.Pow(2, sliderVal / 25.0), 2);
}

private static string SliderToDisplayText(double speed) {
    return speed >= 1.0 ? $"{(int)speed}x" : $"{speed:F2}x".TrimEnd('0').TrimEnd('.') + "x";
}
```

- [ ] **Step 3: Update SpeedRates → remove, update Fs speed display**

Replace `SpeedRates` (line 1916) — remove the entire line.

Update `UpdateFsSpeedDisplay()` (line 3499):
```csharp
private void UpdateFsSpeedDisplay() {
    FsSpeedBtn.Content = SpeedLabel.Text;
}
```

- [ ] **Step 4: Update FsTransportBtn_Click speed cycle**

Change the "speed" case in `FsTransportBtn_Click`:
```csharp
case "speed":
    // Cycle: 1x→2x→4x→8x→16x→1x
    var currentSpeed = SliderValueToSpeed(SpeedSlider.Value);
    var nextSpeed = currentSpeed >= 16 ? 1.0 : currentSpeed * 2;
    var sliderVal = nextSpeed switch {
        0.25 => 0, 0.5 => 25, 1.0 => 50, 2.0 => 75, 4.0 => 87, 8.0 => 94, 16.0 => 100,
        _ => 50
    };
    SpeedSlider.Value = sliderVal;
    break;
```

- [ ] **Step 5: Add ModernSlider style to Styles.xaml**

Check if `ModernSlider` style exists. If not, add to `Styles.xaml`:
```xml
<Style x:Key="ModernSlider" TargetType="Slider">
    <Setter Property="Foreground" Value="{DynamicResource PrimaryBrush}"/>
    <Setter Property="Background" Value="{DynamicResource InputBackgroundBrush}"/>
    <Setter Property="BorderBrush" Value="{DynamicResource BorderBrush}"/>
    <Setter Property="BorderThickness" Value="0"/>
    <Setter Property="IsSnapToTickEnabled" Value="False"/>
    <Setter Property="TickFrequency" Value="1"/>
</Style>
```

- [ ] **Step 6: Build and verify**

```powershell
dotnet build "D:\HeliVMS\HeliVMS\HeliVMS.csproj" 2>&1
```
Expected: 0 errors 0 warnings

---