# Nx Witness 風格播放面板改造 — 實作計劃

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 將 PlaybackView 傳輸控制列與全螢幕控制項改造為 Nx Witness 風格

**Architecture:** Row 3 Grid 從 18 欄簡化為 12 欄，移除 JumpStart/JumpEnd/Stop/Loop 面板；ComboBox 改為連續 Slider；新增 LIVE/SYNC/CLND/THMB 按鈕；NxTimeline 新增 Row 0 ThumbnailCanvas

**Tech Stack:** WPF .NET 10, Canvas rendering

## 檔案架構

| 檔案 | 狀態 | 職責 |
|---|---|---|
| `Views/PlaybackView.xaml` | 修改 | Row 3 傳輸控制列重構；Row 0-3 FullScreenOverlay 調整 |
| `Views/PlaybackView.xaml.cs` | 修改 | 新增狀態欄位、Sliding/LIVE/SYNC/CLND/THMB 處理常式 |
| `Controls/NxTimeline.xaml` | 修改 | 新增 Row 0 ThumbnailCanvas；RowIndex 偏移 |
| `Controls/NxTimeline.xaml.cs` | 修改 | ToggleThumbnails, DrawThumbnails stub |

## Global Constraints

- Build must produce 0 errors 0 warnings
- 移除的按鈕功能需可透過快捷鍵存取（spec §1 表格）
- 顏色一律使用 `DynamicResource`，無硬編碼色碼

---

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

### Task 2: Transport Controls Layout Restructure

**Files:**
- Modify: `Views/PlaybackView.xaml` lines 732-923 (Row 3 controls)
- Modify: `Views/PlaybackView.xaml.cs` lines 3393-3406 (UpdateButtonStates)

**Interfaces:**
- Consumes: Existing Grid.Column layout
- Produces: New 12-column layout with PlayPauseBtn (merged), LIVE/SYNC/CLND/THMB buttons

- [ ] **Step 1: Restructure Row 3 Grid.ColumnDefinitions**

Replace current 18-column definitions with 12-column:

```xml
<Grid.ColumnDefinitions>
    <ColumnDefinition Width="Auto"/>  <!-- 0: PlayPauseBtn (merged) -->
    <ColumnDefinition Width="Auto"/>  <!-- 1: StepBackBtn -->
    <ColumnDefinition Width="Auto"/>  <!-- 2: StepFwdBtn -->
    <ColumnDefinition Width="Auto"/>  <!-- 3: Separator -->
    <ColumnDefinition Width="Auto"/>  <!-- 4: SegPrevBtn (JumpPrevRecBtn) -->
    <ColumnDefinition Width="Auto"/>  <!-- 5: SegNextBtn (JumpNextRecBtn) -->
    <ColumnDefinition Width="Auto"/>  <!-- 6: SpeedSliderPanel (Task 1) -->
    <ColumnDefinition Width="Auto"/>  <!-- 7: PlaybackTimeText -->
    <ColumnDefinition Width="*"/>     <!-- 8: Spacer -->
    <ColumnDefinition Width="Auto"/>  <!-- 9: LiveBtn / SyncBtn / ClndBtn / ThmbBtn group -->
    <ColumnDefinition Width="Auto"/>  <!-- 10: BookmarkBtn + StopAllBtn -->
    <ColumnDefinition Width="Auto"/>  <!-- 11: FpsBadge -->
</Grid.ColumnDefinitions>
```

- [ ] **Step 2: Replace Play/Pause separate buttons with merged PlayPauseBtn**

Remove:
```xml
<Border Grid.Column="2" Background="...">  <!-- Play + Pause container -->
    <StackPanel Orientation="Horizontal">
        <Button x:Name="PlayBtn" .../>
        <Border Width="1" .../>
        <Button x:Name="PauseBtn" .../>
    </StackPanel>
</Border>
```

Add:
```xml
<Button Grid.Column="0" x:Name="PlayPauseBtn"
        Style="{StaticResource ModernButton}"
        Height="28" Width="38" FontSize="13"
        BorderThickness="0" Click="PlayPauseBtn_Click"
        ToolTip="播放 / 暫停 (Space)">
    <Path x:Name="PlayPauseIcon" Data="{StaticResource IconPlay}"
          Fill="{DynamicResource TextBrush}" Width="14" Height="14" Stretch="Uniform"/>
</Button>
```

- [ ] **Step 3: Re-assign existing buttons to new columns**

```xml
<!-- Column 1: StepBackBtn -->
<Button Grid.Column="1" x:Name="StepBackBtn" ... Margin="2,0,0,0" .../>

<!-- Column 2: StepFwdBtn -->
<Button Grid.Column="2" x:Name="StepFwdBtn" ... Margin="2,0,0,0" .../>

<!-- Column 3: Separator -->
<Separator Grid.Column="3" Style="{StaticResource {x:Static ToolBar.SeparatorStyleKey}}" Margin="4,0"/>

<!-- Column 4: SegPrevBtn (was JumpPrevRecBtn) -->
<Button Grid.Column="4" x:Name="JumpPrevRecBtn" ... Margin="2,0,0,0" .../>

<!-- Column 5: SegNextBtn (was JumpNextRecBtn) -->
<Button Grid.Column="5" x:Name="JumpNextRecBtn" ... Margin="2,0,0,0" .../>

<!-- Column 6: SpeedSliderPanel (already added by Task 1) -->

<!-- Column 7: PlaybackTimeText -->
<Border Grid.Column="7" Background="{DynamicResource SecondarySurfaceBrush}"
        CornerRadius="4" Padding="8,3" Margin="6,0,0,0">
    <TextBlock x:Name="PlaybackTimeText" .../>
</Border>
```

- [ ] **Step 4: Add LIVE, SYNC, CLND, THMB button group (Column 9)**

```xml
<!-- Column 9: LIVE / SYNC / CLND / THMB group -->
<StackPanel Grid.Column="9" Orientation="Horizontal" Margin="6,0,0,0">
    <Border Background="{DynamicResource SecondarySurfaceBrush}"
            CornerRadius="4" Padding="2">
        <StackPanel Orientation="Horizontal">
            <ToggleButton x:Name="LiveBtn"
                          Content="LIVE" FontSize="9" FontWeight="Bold"
                          Height="24" Padding="6,0" Cursor="Hand"
                          Style="{StaticResource NxGreenToggleButton}"
                          Checked="LiveBtn_Checked" Unchecked="LiveBtn_Unchecked"
                          ToolTip="切換即時模式 (L)"/>
            <Separator Style="{StaticResource {x:Static ToolBar.SeparatorStyleKey}}"/>
            <ToggleButton x:Name="SyncBtn"
                          Content="SYNC" FontSize="9" FontWeight="Bold"
                          Height="24" Padding="6,0" Cursor="Hand"
                          IsChecked="True"
                          Style="{StaticResource NxBlueToggleButton}"
                          Checked="SyncBtn_Checked" Unchecked="SyncBtn_Unchecked"
                          ToolTip="切換多頻道同步 (Ctrl+S)"/>
            <Separator Style="{StaticResource {x:Static ToolBar.SeparatorStyleKey}}"/>
            <Button x:Name="ClndBtn"
                    Style="{StaticResource SecondaryButton}"
                    Height="24" Width="28" Padding="0" FontSize="9"
                    Click="ClndBtn_Click" ToolTip="日曆 (Ctrl+C)">
                <TextBlock Text="CLND" FontSize="9" FontWeight="Bold"
                           Foreground="{DynamicResource TextBrush}"/>
            </Button>
            <Separator Style="{StaticResource {x:Static ToolBar.SeparatorStyleKey}}"/>
            <ToggleButton x:Name="ThmbBtn"
                          Content="THMB" FontSize="9" FontWeight="Bold"
                          Height="24" Padding="6,0" Cursor="Hand"
                          Style="{StaticResource NxOrangeToggleButton}"
                          Checked="ThmbBtn_Checked" Unchecked="ThmbBtn_Unchecked"
                          ToolTip="切換縮圖預覽列 (T)"/>
        </StackPanel>
    </Border>
</StackPanel>
```

- [ ] **Step 5: Add NxToggleButton styles (4 variants)**

Add to Styles.xaml:
```xml
<!-- Base NxToggleButton style -->
<Style x:Key="NxToggleButton" TargetType="ToggleButton">
    <Setter Property="Background" Value="Transparent"/>
    <Setter Property="Foreground" Value="{DynamicResource SecondaryTextBrush}"/>
    <Setter Property="BorderThickness" Value="0"/>
    <Setter Property="FontSize" Value="9"/>
    <Setter Property="FontWeight" Value="Bold"/>
    <Setter Property="Height" Value="24"/>
    <Setter Property="Template">
        <Setter.Value>
            <ControlTemplate TargetType="ToggleButton">
                <Border x:Name="Root" Background="{TemplateBinding Background}"
                        BorderThickness="0" CornerRadius="3" Padding="6,0">
                    <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
                </Border>
                <ControlTemplate.Triggers>
                    <Trigger Property="IsChecked" Value="True">
                        <Setter TargetName="Root" Property="Background" Value="{DynamicResource PrimaryBrush}"/>
                        <Setter Property="Foreground" Value="{DynamicResource TextBrush}"/>
                    </Trigger>
                </ControlTemplate.Triggers>
            </ControlTemplate>
        </Setter.Value>
    </Setter>
</Style>

<!-- LIVE green toggle -->
<Style x:Key="NxGreenToggleButton" BasedOn="{StaticResource NxToggleButton}" TargetType="ToggleButton">
    <Style.Triggers>
        <Trigger Property="IsChecked" Value="True">
            <Setter Property="Background" Value="{DynamicResource SuccessBrush}"/>
        </Trigger>
    </Style.Triggers>
</Style>

<!-- SYNC blue toggle -->
<Style x:Key="NxBlueToggleButton" BasedOn="{StaticResource NxToggleButton}" TargetType="ToggleButton">
    <Style.Triggers>
        <Trigger Property="IsChecked" Value="True">
            <Setter Property="Background" Value="{DynamicResource RecordingContinuousBrush}"/>
        </Trigger>
    </Style.Triggers>
</Style>

<!-- THMB orange toggle -->
<Style x:Key="NxOrangeToggleButton" BasedOn="{StaticResource NxToggleButton}" TargetType="ToggleButton">
    <Style.Triggers>
        <Trigger Property="IsChecked" Value="True">
            <Setter Property="Background" Value="{DynamicResource RecordingMotionBrush}"/>
        </Trigger>
    </Style.Triggers>
</Style>
```

- [ ] **Step 6: Add remaining buttons (Column 10)**

```xml
<!-- Column 10: BookmarkBtn + StopAllBtn -->
<StackPanel Grid.Column="10" Orientation="Horizontal" Margin="4,0,0,0">
    <Button x:Name="BookmarkBtn" Style="{StaticResource SecondaryButton}"
            Height="26" Width="28" Padding="0" Margin="0,0,2,0"
            Click="BookmarkBtn_Click" ToolTip="標記書籤 (B)">
        <Path Data="{StaticResource IconBookmark}" Fill="{DynamicResource TextBrush}"
              Width="12" Height="12" Stretch="Uniform"/>
    </Button>
    <Button x:Name="StopAllBtn" Content="⏹"
            Style="{StaticResource SecondaryButton}"
            Height="26" FontSize="10" Padding="0" Width="28"
            Foreground="{DynamicResource ErrorBrush}"
            Click="StopAllBtn_Click" ToolTip="全部停止"/>
</StackPanel>
```

- [ ] **Step 7: FpsBadge (Column 11)**

```xml
<Border Grid.Column="11" x:Name="FpsBadge" ... Margin="2,0,0,0" .../>
```

- [ ] **Step 8: Update UpdateButtonStates for new controls**

Replace `UpdateButtonStates()`:
```csharp
private void UpdateButtonStates() {
    var hasContent = _activePlayers.Count > 0;
    PlayPauseBtn.IsEnabled = hasContent;
    StepBackBtn.IsEnabled = hasContent;
    StepFwdBtn.IsEnabled = hasContent;
    JumpPrevRecBtn.IsEnabled = hasContent;
    JumpNextRecBtn.IsEnabled = hasContent;
    StopAllBtn.IsEnabled = hasContent;
    BookmarkBtn.IsEnabled = hasContent;
    LayoutBtn.IsEnabled = hasContent;

    // Update icon based on play state
    PlayPauseIcon.Data = _isPlaying
        ? (Geometry)TryFindResource("IconPause") ?? (Geometry)Application.Current?.TryFindResource("IconPause")
        : (Geometry)TryFindResource("IconPlay") ?? (Geometry)Application.Current?.TryFindResource("IconPlay");
    PlayPauseBtn.ToolTip = _isPlaying ? "暫停 (Space)" : "播放 (Space)";

    UpdateFsTransportState();
}
```

- [ ] **Step 9: Remove removed button XAML elements**

Remove these XAML elements:
- `JumpStartBtn` (line 758)
- `JumpEndBtn` (line 794)
- `StopBtn` (line 813)
- `LoopPanel` + children (lines 837-861)
- `BookmarkListBtn` (lines 890-896)
- `ExportClipBtn` (lines 897-902)

Keep `LayoutBtn` in its existing position (move to column 10 or keep in toolbar).

- [ ] **Step 10: Build and verify**

```powershell
dotnet build "D:\HeliVMS\HeliVMS\HeliVMS.csproj" 2>&1
```
Expected: 0 errors 0 warnings

---

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

### Task 4: Remove Deprecated Handlers + Cleanup

**Files:**
- Modify: `Views/PlaybackView.xaml.cs`

- [ ] **Step 1: Remove deprecated handler methods**

Remove these entire method bodies (keep stubs that just log? No, just remove):
- `JumpStartBtn_Click` (lines 1987-1990)
- `JumpEndBtn_Click` (lines 1992-1995)
- `StopBtn_Click` (lines 1908-1910)
- `LoopSetABtn_Click` (lines 1944-1950)
- `LoopSetBBtn_Click` (lines 1952-1957)
- `LoopToggleBtn_Click` (lines 1959-1962)
- `LoopClearBtn_Click` (lines 1964-1968)
- `UpdateLoopVisual` (lines 1971-1985)
- `PlayBtn_Click` (lines 1896-1900)
- `PauseBtn_Click` (lines 1902-1906)
- `SpeedCombo_SelectionChanged` (lines 1918-1927)

Keep `StopAllBtn_Click` (line 1912). Remove `StopBtn_Click`.

Remove `_loopA`, `_loopB`, `_loopEnabled` fields (lines 60-62).

- [ ] **Step 2: Update FsTransportBtn_Click**

Remove "jumpStart" and "jumpEnd" cases. Update "play" case to use PlayPauseBtn:
```csharp
case "play":
case "pause":
    PlayPauseBtn_Click(sender, e);
    break;
```

- [ ] **Step 3: Clean up using directives**

Remove any unused using directives that were only used by removed code.

- [ ] **Step 4: Build and verify**

```powershell
dotnet build "D:\HeliVMS\HeliVMS\HeliVMS.csproj" 2>&1
```
Expected: 0 errors 0 warnings

---

### Task 5: FullScreenOverlay Sync — Simplify Transport Layout

**Files:**
- Modify: `Views/PlaybackView.xaml` lines 931-1022
- Modify: `Views/PlaybackView.xaml.cs` lines 3494-3543

- [ ] **Step 1: Restructure Fs transport Grid.ColumnDefinitions**

Replace Fs bottom bar columns with simplified version:
```xml
<Grid>
    <Grid.ColumnDefinitions>
        <ColumnDefinition Width="Auto"/>  <!-- 0: FsStepBack -->
        <ColumnDefinition Width="Auto"/>  <!-- 1: FsPlayPauseBtn (merged) -->
        <ColumnDefinition Width="Auto"/>  <!-- 2: FsStepFwd -->
        <ColumnDefinition Width="Auto"/>  <!-- 3: FsSegPrev -->
        <ColumnDefinition Width="Auto"/>  <!-- 4: FsSegNext -->
        <ColumnDefinition Width="Auto"/>  <!-- 5: FsSpeedBtn -->
        <ColumnDefinition Width="*"/>     <!-- 6: spacer -->
        <ColumnDefinition Width="Auto"/>  <!-- 7: FsClockText -->
    </Grid.ColumnDefinitions>
    ...
</Grid>
```

- [ ] **Step 2: Replace Fs Play/Pause with merged button**

Remove `FsPlayBtn` and `FsPauseBtn` elements. Add single `FsPlayPauseBtn`:
```xml
<Button Grid.Column="1" x:Name="FsPlayPauseBtn"
        Style="{StaticResource SecondaryButton}"
        Height="30" Width="34" FontSize="12"
        Background="{DynamicResource SecondaryTextBrush}" BorderThickness="0"
        Foreground="{DynamicResource TextBrush}" Margin="4,0,0,0"
        Click="FsTransportBtn_Click" Tag="play" ToolTip="播放 / 暫停">
    <Path x:Name="FsPlayPauseIcon" Data="{StaticResource IconPlay}"
          Fill="{DynamicResource TextBrush}" Width="12" Height="12" Stretch="Uniform"/>
</Button>
```

- [ ] **Step 3: Add FsSegPrev/FsSegNext buttons**

```xml
<Button Grid.Column="3" x:Name="FsSegPrevBtn" ... Tag="segPrev" ToolTip="前一段"/>
<Button Grid.Column="4" x:Name="FsSegNextBtn" ... Tag="segNext" ToolTip="後一段"/>
```

- [ ] **Step 4: Update Fs transport code-behind**

Replace `UpdateFsTransportState`:
```csharp
private void UpdateFsTransportState() {
    var hasContent = _activePlayers.Count > 0;
    FsPlayPauseBtn.IsEnabled = hasContent;
    FsStepBackBtn.IsEnabled = hasContent;
    FsStepFwdBtn.IsEnabled = hasContent;
    FsSegPrevBtn.IsEnabled = hasContent;
    FsSegNextBtn.IsEnabled = hasContent;
    FsSpeedBtn.IsEnabled = hasContent;

    // Update icon
    var fsIcon = _isPlaying ? "IconPause" : "IconPlay";
    FsPlayPauseIcon.Data = (Geometry)FindResource(fsIcon);
    FsPlayPauseBtn.Tag = _isPlaying ? "pause" : "play";
}
```

- [ ] **Step 5: Update FsTransportBtn_Click with new tags**

Add cases for "segPrev" and "segNext":
```csharp
case "segPrev":
    JumpPrevRecBtn_Click(sender, e);
    break;
case "segNext":
    JumpNextRecBtn_Click(sender, e);
    break;
```

- [ ] **Step 6: Build and verify**

```powershell
dotnet build "D:\HeliVMS\HeliVMS\HeliVMS.csproj" 2>&1
```
Expected: 0 errors 0 warnings

---

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

### Task 7: Final Build & Verify

- [ ] **Step 1: Full solution build**

```powershell
dotnet build "D:\HeliVMS\HeliVMS\HeliVMS.csproj" 2>&1
```
Expected: 0 errors 0 warnings

- [ ] **Step 2: Verify all handlers referenced in XAML exist in code-behind**

Check each removed/added handler:
- PlayPauseBtn_Click ✓
- SpeedSlider_ValueChanged ✓
- LiveBtn_Checked, LiveBtn_Unchecked ✓
- SyncBtn_Checked, SyncBtn_Unchecked ✓
- ClndBtn_Click, ClndCalendar_SelectedDatesChanged, ClndCalendar_Today_Click ✓
- ThmbBtn_Checked, ThmbBtn_Unchecked ✓
- Removed: JumpStartBtn_Click, JumpEndBtn_Click, StopBtn_Click, Loop*, PlayBtn_Click, PauseBtn_Click, SpeedCombo_SelectionChanged ✓

- [ ] **Step 3: Verify no hardcoded colors**

Check all new XAML elements for `Background="White"` or similar — all must use `{DynamicResource ...}`.

- [ ] **Step 4: Commit**

```powershell
git add -A
git commit -m "PlaybackView: Nx Witness 風格播放面板改造 — Speed Slider/LIVE/SYNC/CLND/THMB + 傳輸列簡化"
```
