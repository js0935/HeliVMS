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