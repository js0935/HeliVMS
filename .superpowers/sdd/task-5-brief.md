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