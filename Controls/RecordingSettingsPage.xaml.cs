// ============================================================
// HeliVMS - 智慧影像管理系統     禾秝軟體開發團隊 / 代碼設計：洪俊士 / 版本：V1.0.0
// ============================================================

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HeliVMS.Models;
using HeliVMS.Services;
using Microsoft.Extensions.DependencyInjection;

namespace HeliVMS.Controls;

public partial class RecordingSettingsPage : UserControl
{
    private readonly ICameraService _cameraService;
    private readonly IEventService _eventLog;
    private readonly IRecordingService _recordingService;

    private const int TotalChannels = 64;
    private List<ChannelItem> _channels = new();
    private ChannelItem? _selectedChannel;
    private CameraRecordingConfigData? _selectedConfig;
    private ScheduleMode _currentMode = ScheduleMode.Continuous;
    private bool _isPainting;
    private bool _showAllChannels = true;

    private static readonly string[] DayNames = { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" };

    private static readonly Color ColorContinuous = Color.FromRgb(0x21, 0x96, 0xF3);
    private static readonly Color ColorMotion = Color.FromRgb(0x4C, 0xAF, 0x50);
    private static readonly Color ColorAlarm = Color.FromRgb(0xF4, 0x43, 0x36);
    private static readonly Color ColorSmart = Color.FromRgb(0xE9, 0x1E, 0x63);
    private static readonly Color ColorWeighted = Color.FromRgb(0xFF, 0x98, 0x00);
    private static readonly Color ColorNone = Colors.Transparent;

    // === PERFORMANCE: Pre-allocated frozen brushes (no new SolidColorBrush per cell) ===
    private static readonly Brush BrushContinuous = FreezeBrush(new SolidColorBrush(ColorContinuous));
    private static readonly Brush BrushMotion = FreezeBrush(new SolidColorBrush(ColorMotion));
    private static readonly Brush BrushAlarm = FreezeBrush(new SolidColorBrush(ColorAlarm));
    private static readonly Brush BrushSmart = FreezeBrush(new SolidColorBrush(ColorSmart));
    private static readonly Brush BrushWeighted = FreezeBrush(new SolidColorBrush(ColorWeighted));
    private static readonly Brush BrushNone = FreezeBrush(new SolidColorBrush(ColorNone));
    private static readonly Brush CellBorderBrush = FreezeBrush(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)));

    private static Brush FreezeBrush(SolidColorBrush b) { b.Freeze(); return b; }

    // === PERFORMANCE: Cached resource lookups (avoid FindResource in loops) ===
    private Brush? _resSecondarySurface;
    private Brush? _resBorder;
    private Brush? _resText;
    private Brush? _resSecondaryText;

    // === PERFORMANCE: Cell grid built once, colors updated in-place ===
    private readonly Border?[,] _scheduleCells = new Border?[7, 24];
    private bool _scheduleGridReady;
    private Action? _onCamerasChanged;

    public RecordingSettingsPage()
    {
        InitializeComponent();
        _cameraService = App.Services.GetRequiredService<ICameraService>();
        _eventLog = App.Services.GetRequiredService<IEventService>();
        _recordingService = App.Services.GetRequiredService<IRecordingService>();

        _onCamerasChanged = () => Dispatcher.InvokeAsync(LoadCameras);
        _cameraService.CamerasChanged += _onCamerasChanged;
        Unloaded += (_, _) =>
        {
            if (_onCamerasChanged is not null)
            {
                _cameraService.CamerasChanged -= _onCamerasChanged;
            }
        };
    }

    public void RefreshCameraList()
    {
        LoadCameras();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Cache resource lookups once (avoid FindResource in loops)
        _resSecondarySurface = FindResource("SecondarySurfaceBrush") as Brush ?? Brushes.DimGray;
        _resBorder = FindResource("BorderBrush") as Brush ?? Brushes.Gray;
        _resText = FindResource("TextBrush") as Brush ?? Brushes.White;
        _resSecondaryText = FindResource("SecondaryTextBrush") as Brush ?? Brushes.Gray;

        LoadCameras();
        SelectMode(ScheduleMode.Continuous);
    }

    #region Channel List
    private void LoadCameras()
    {
        // Reuse shared channel list from ChannelManagementPage if available
        var shared = ChannelManagementPage.CurrentChannels;
        if (shared is not null && shared.Count == TotalChannels)
        {
            _channels = shared.ToList();
        }
        else
        {
            var allCameras = _cameraService.GetAllCameras();
            var channels = new List<ChannelItem>(TotalChannels);
            for (int ch = 1; ch <= TotalChannels; ch++)
            {
                Camera? cam = null;
                foreach (var c in allCameras)
                {
                    if (c.ChannelNumber == ch) { cam = c; break; }
                }
                channels.Add(new ChannelItem
                {
                    ChannelNumber = ch,
                    DisplayName = cam?.Name ?? $"CH{ch}",
                    IpAddress = cam?.IpAddress ?? "",
                    Manufacturer = cam?.Manufacturer ?? "",
                    Model = cam?.Model ?? "",
                    Camera = cam,
                });
            }
            _channels = channels;
        }

        CameraListPanel.Children.Clear();
        if (_showAllChannels)
        {
            for (int ci = 0; ci < _channels.Count; ci++)
            {
                var row = BuildChannelRow(_channels[ci]);
                CameraListPanel.Children.Add(row);
            }
        }
        else
        {
            for (int ci = 0; ci < _channels.Count; ci++)
            {
                var channel = _channels[ci];
                if (channel.Camera is null || string.IsNullOrEmpty(channel.IpAddress))
                    continue;
                var row = BuildChannelRow(channel);
                CameraListPanel.Children.Add(row);
            }
        }

        if (_selectedChannel is not null)
        {
            ChannelItem? match = null;
            foreach (var c in _channels)
            {
                if (c.ChannelNumber == _selectedChannel.ChannelNumber) { match = c; break; }
            }
            if (match?.Camera is not null && !string.IsNullOrEmpty(match.IpAddress))
            {
                SelectChannel(match);
            }
            else
            {
                _selectedChannel = null;
                _selectedConfig = null;
                ClearScheduleGrid();
                SelectedCameraName.Text = "請選擇已指派攝影機的頻道以檢視排程";
                AudioCheckBox.IsChecked = false;
            }
        }
        else
        {
            _selectedConfig = null;
            ClearScheduleGrid();
            SelectedCameraName.Text = "請選擇已指派攝影機的頻道以檢視排程";
            AudioCheckBox.IsChecked = false;
        }
    }

    private Border BuildChannelRow(ChannelItem channel)
    {
        var hasCamera = channel.Camera is not null && !string.IsNullOrEmpty(channel.IpAddress);
        var config = hasCamera ? CameraRecordingConfigData.Deserialize(channel.Camera!.RecordingConfigJson) : null;
        var isSelected = _selectedChannel?.ChannelNumber == channel.ChannelNumber;

        var row = new Border
        {
            Tag = channel.ChannelNumber,
            Padding = new Thickness(8, 6, 8, 6),
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = _resBorder ?? Brushes.Gray,
            Cursor = hasCamera ? Cursors.Hand : Cursors.Arrow,
            Background = isSelected
                ? (_resSecondarySurface ?? Brushes.DarkSlateGray)
                : Brushes.Transparent,
            Opacity = hasCamera ? 1.0 : 0.45,
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(115) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var chText = new TextBlock
        {
            Text = $"{channel.ChannelNumber}",
            FontSize = 12,
            Foreground = _resSecondaryText ?? Brushes.Gray,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(chText, 0);
        grid.Children.Add(chText);

        var namePanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        if (hasCamera)
        {
            var isRec = _recordingService.IsRecording(channel.Camera!.Id);
            var dot = new Border
            {
                Width = 8, Height = 8,
                CornerRadius = new CornerRadius(4),
                Background = isRec ? Brushes.LimeGreen : Brushes.Transparent,
                Margin = new Thickness(0, 0, 4, 0),
                BorderThickness = new Thickness(0),
                ToolTip = isRec ? "錄影中" : null,
            };
            namePanel.Children.Add(dot);
        }

        namePanel.Children.Add(new TextBlock
        {
                Text = hasCamera ? channel.DisplayName : "未連線",
            FontSize = 12,
            Foreground = hasCamera
                ? (_resText ?? Brushes.White)
                : (_resSecondaryText ?? Brushes.Gray),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        Grid.SetColumn(namePanel, 1);
        grid.Children.Add(namePanel);

        if (hasCamera)
        {
            var audioCheck = new CheckBox
            {
                IsChecked = config?.EnableAudio ?? false,
                ToolTip = "啟用音訊",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0),
            };
            var camId = channel.Camera!.Id;
            audioCheck.Checked += (_, _) => UpdateAudioForCamera(camId, true);
            audioCheck.Unchecked += (_, _) => UpdateAudioForCamera(camId, false);
            Grid.SetColumn(audioCheck, 2);
            grid.Children.Add(audioCheck);
        }

        row.Child = grid;
        row.MouseDown += (_, _) =>
        {
            if (hasCamera) { SelectChannel(channel); }
        };
        return row;
    }

    private void RefreshCameraListSelection()
    {
        var selBrush = _resSecondarySurface ?? Brushes.DarkSlateGray;
        foreach (var child in CameraListPanel.Children)
        {
            if (child is Border b && b.Tag is int chNum)
            {
                b.Background = (chNum == _selectedChannel?.ChannelNumber)
                    ? selBrush
                    : Brushes.Transparent;
            }
        }
    }

    private void BatchToggle_Click(object sender, RoutedEventArgs e)
    {
        _showAllChannels = !_showAllChannels;
        LoadCameras();
    }
    #endregion

    #region Channel Selection
    private void SelectChannel(ChannelItem channel)
    {
        _selectedChannel = channel;
        _selectedConfig = CameraRecordingConfigData.Deserialize(channel.Camera!.RecordingConfigJson)
                          ?? new CameraRecordingConfigData();

        RefreshCameraListSelection();
        SelectedCameraName.Text = $"CH{channel.ChannelNumber} - {channel.DisplayName}";
        AudioCheckBox.IsChecked = _selectedConfig.EnableAudio;
        BuildScheduleGrid();
    }

    private void UpdateAudioForCamera(string cameraId, bool enabled)
    {
        ChannelItem? channel = null;
        foreach (var c in _channels)
        {
            if (c.Camera?.Id == cameraId) { channel = c; break; }
        }
        var camera = channel?.Camera;
        if (camera is null) { return; }
        var config = CameraRecordingConfigData.Deserialize(camera.RecordingConfigJson);
        if (config is null) { return; }
        config.EnableAudio = enabled;
        camera.RecordingConfigJson = config.Serialize();
        _cameraService.UpdateCamera(camera);
    }
    #endregion

    #region Schedule Grid
    private void ClearScheduleGrid()
    {
        ScheduleGrid.Children.Clear();
        ScheduleGrid.RowDefinitions.Clear();
        ScheduleGrid.ColumnDefinitions.Clear();
        Array.Clear(_scheduleCells, 0, _scheduleCells.Length);
        _scheduleGridReady = false;
    }

    private void BuildScheduleGrid()
    {
        if (_selectedConfig is null)
        {
            ClearScheduleGrid();
            return;
        }

        // Build the grid structure only once; subsequent calls just update colors
        if (!_scheduleGridReady)
        {
            BuildScheduleGridStructure();
            _scheduleGridReady = true;
        }

        UpdateScheduleGridColors();
    }

    private void BuildScheduleGridStructure()
    {
        // Build grid: 5 columns (0=day label, 1-24=hours)
        ScheduleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        for (int c = 0; c < 24; c++)
        {
            ScheduleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        }

        // Row 0: Hour headers
        ScheduleGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Corner cell (empty top-left)
        var corner = new Border
        {
            Background = _resSecondarySurface,
            BorderThickness = new Thickness(0.5),
            BorderBrush = _resBorder,
            Child = new TextBlock
            {
                Text = "時段",
                FontSize = 9,
                Foreground = _resSecondaryText,
                HorizontalAlignment = HorizontalAlignment.Center,
            }
        };
        Grid.SetRow(corner, 0);
        Grid.SetColumn(corner, 0);
        ScheduleGrid.Children.Add(corner);

        // Hour labels in row 0, col 1-24
        for (int h = 0; h < 24; h++)
        {
            var hourCell = new Border
            {
                Background = _resSecondarySurface,
                BorderThickness = new Thickness(0.5),
                BorderBrush = _resBorder,
                Child = new TextBlock
                {
                    Text = $"{h:D2}",
                    FontSize = 8,
                    Foreground = _resSecondaryText,
                    HorizontalAlignment = HorizontalAlignment.Center,
                }
            };
            Grid.SetRow(hourCell, 0);
            Grid.SetColumn(hourCell, h + 1);
            ScheduleGrid.Children.Add(hourCell);
        }

        // 7 day rows (1-7), each with 24 hour cells
        for (int d = 0; d < 7; d++)
        {
            ScheduleGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(18) });

        // Day label at col 0
            var dayCell = new Border
            {
                Background = _resSecondarySurface,
                BorderThickness = new Thickness(0.5),
                BorderBrush = _resBorder,
                Child = new TextBlock
                {
                    Text = DayNames[d],
                    FontSize = 10,
                    Foreground = _resText,
                    HorizontalAlignment = HorizontalAlignment.Center,
                }
            };
            Grid.SetRow(dayCell, d + 1);
            Grid.SetColumn(dayCell, 0);
            ScheduleGrid.Children.Add(dayCell);

        // 24 hour cells in col 1-24
            for (int h = 0; h < 24; h++)
            {
                var cell = new Border
                {
                    Tag = (d, h),
                    Background = BrushNone,
                    BorderThickness = new Thickness(0.5),
                    BorderBrush = CellBorderBrush,
                    Cursor = Cursors.Pen,
                    ToolTip = $"{DayNames[d]} {h:D2}:00",
                };
                cell.MouseDown += Cell_MouseDown;
                cell.MouseEnter += Cell_MouseEnter;
                cell.MouseUp += Cell_MouseUp;

                _scheduleCells[d, h] = cell;

                Grid.SetRow(cell, d + 1);
                Grid.SetColumn(cell, h + 1);
                ScheduleGrid.Children.Add(cell);
            }
        }
    }

    private void UpdateScheduleGridColors()
    {
        if (_selectedConfig is null) { return; }
        for (int d = 0; d < 7; d++)
        {
            for (int h = 0; h < 24; h++)
            {
                var mode = GetModeForCell(_selectedConfig, d, h);
                var cell = _scheduleCells[d, h];
                if (cell is not null)
                {
                    cell.Background = GetBrushForMode(mode);
                }
            }
        }
    }

    private static Brush GetBrushForMode(ScheduleMode mode)
    {
        return mode switch
        {
            ScheduleMode.Continuous => BrushContinuous,
            ScheduleMode.Motion => BrushMotion,
            ScheduleMode.Alarm => BrushAlarm,
            ScheduleMode.Smart => BrushSmart,
            ScheduleMode.Weighted => BrushWeighted,
            _ => BrushNone,
        };
    }

    private void Cell_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border cell || _selectedConfig is null) { return; }
        _isPainting = true;
        ApplyCell(cell);
        e.Handled = true;
    }

    private void Cell_MouseEnter(object sender, MouseEventArgs e)
    {
        if (!_isPainting || sender is not Border cell || _selectedConfig is null) { return; }
        ApplyCell(cell);
    }

    private void Cell_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _isPainting = false;
    }

    private void ApplyCell(Border cell)
    {
        if (_selectedConfig is null) { return; }
        var (day, hour) = ((int, int))cell.Tag;
        var currentMode = GetModeForCell(_selectedConfig, day, hour);

        ClearCell(_selectedConfig, day, hour);

        if (currentMode != _currentMode)
        {
            SetCell(_selectedConfig, day, hour, _currentMode);
        }

        cell.Background = GetBrushForMode(
            currentMode == _currentMode ? ScheduleMode.None : _currentMode);
    }

    private static void ClearCell(CameraRecordingConfigData config, int day, int hour)
    {
        if (config.ContinuousSchedule is not null && day < config.ContinuousSchedule.Length)
        {
            config.ContinuousSchedule[day].Hours[hour] = false;
        }
        if (config.MotionSchedule is not null && day < config.MotionSchedule.Length)
        {
            config.MotionSchedule[day].Hours[hour] = false;
        }
        if (config.AlarmSchedule is not null && day < config.AlarmSchedule.Length)
        {
            config.AlarmSchedule[day].Hours[hour] = false;
        }
        if (config.SmartSchedule is not null && day < config.SmartSchedule.Length)
        {
            config.SmartSchedule[day].Hours[hour] = false;
        }
        if (config.WeightedSchedule is not null && day < config.WeightedSchedule.Length)
        {
            config.WeightedSchedule[day].Hours[hour] = false;
        }
    }

    private static void SetCell(CameraRecordingConfigData config, int day, int hour, ScheduleMode mode)
    {
        switch (mode)
        {
            case ScheduleMode.Continuous:
                if (config.ContinuousSchedule is not null && day < config.ContinuousSchedule.Length)
                {
                    config.ContinuousSchedule[day].Hours[hour] = true;
                }
                break;
            case ScheduleMode.Motion:
                if (config.MotionSchedule is not null && day < config.MotionSchedule.Length)
                {
                    config.MotionSchedule[day].Hours[hour] = true;
                }
                break;
            case ScheduleMode.Alarm:
                if (config.AlarmSchedule is not null && day < config.AlarmSchedule.Length)
                {
                    config.AlarmSchedule[day].Hours[hour] = true;
                }
                break;
            case ScheduleMode.Smart:
                if (config.SmartSchedule is not null && day < config.SmartSchedule.Length)
                {
                    config.SmartSchedule[day].Hours[hour] = true;
                }
                break;
            case ScheduleMode.Weighted:
                if (config.WeightedSchedule is not null && day < config.WeightedSchedule.Length)
                {
                    config.WeightedSchedule[day].Hours[hour] = true;
                }
                break;
        }
    }

    private static ScheduleMode GetModeForCell(CameraRecordingConfigData config, int day, int hour)
    {
        if (config.WeightedSchedule is not null && day < config.WeightedSchedule.Length &&
            config.WeightedSchedule[day].Hours[hour])
            return ScheduleMode.Weighted;
        if (config.SmartSchedule is not null && day < config.SmartSchedule.Length &&
            config.SmartSchedule[day].Hours[hour])
            return ScheduleMode.Smart;
        if (config.AlarmSchedule is not null && day < config.AlarmSchedule.Length &&
            config.AlarmSchedule[day].Hours[hour])
            return ScheduleMode.Alarm;
        if (config.MotionSchedule is not null && day < config.MotionSchedule.Length &&
            config.MotionSchedule[day].Hours[hour])
            return ScheduleMode.Motion;
        if (config.ContinuousSchedule is not null && day < config.ContinuousSchedule.Length &&
            config.ContinuousSchedule[day].Hours[hour])
            return ScheduleMode.Continuous;
        return ScheduleMode.None;
    }
    #endregion

    #region Mode Selection
    private void ModeSelector_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border border || border.Tag is not string tag) { return; }
        var mode = tag switch
        {
            "Continuous" => ScheduleMode.Continuous,
            "Motion" => ScheduleMode.Motion,
            "Alarm" => ScheduleMode.Alarm,
            "Smart" => ScheduleMode.Smart,
            "Weighted" => ScheduleMode.Weighted,
            _ => ScheduleMode.None,
        };
        SelectMode(mode);
    }

    private void SelectMode(ScheduleMode mode)
    {
        _currentMode = mode;
        var all = new[] { ModeContinuousBorder, ModeMotionBorder, ModeAlarmBorder, ModeSmartBorder, ModeWeightedBorder, ModeNoneBorder };
        foreach (var b in all) { b.BorderBrush = Brushes.Transparent; }

        var sel = mode switch
        {
            ScheduleMode.Continuous => ModeContinuousBorder,
            ScheduleMode.Motion => ModeMotionBorder,
            ScheduleMode.Alarm => ModeAlarmBorder,
            ScheduleMode.Smart => ModeSmartBorder,
            ScheduleMode.Weighted => ModeWeightedBorder,
            _ => ModeNoneBorder,
        };
        sel.BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
    }
    #endregion

    #region Quick Presets
    /// <summary>Apply quick preset: continuous/motion/alarm/smart/weighted</summary>
    private void QuickPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string preset) { return; }
        if (_selectedConfig is null) { return; }

        switch (preset)
        {
            case "06-18":
                ApplyPresetToAllDays(_selectedConfig, 6, 18);
                break;
            case "18-06":
                ApplyPresetToAllDays(_selectedConfig, 18, 24);
                ApplyPresetToAllDays(_selectedConfig, 0, 6);
                break;
            case "00-24":
                ApplyPresetToAllDays(_selectedConfig, 0, 24);
                break;
            case "weekend":
                for (int d = 5; d <= 6; d++)
                    for (int h = 0; h < 24; h++)
                    {
                        ClearCell(_selectedConfig, d, h);
                        SetCell(_selectedConfig, d, h, _currentMode);
                    }
                break;
            case "clear":
                for (int d = 0; d < 7; d++)
                    for (int h = 0; h < 24; h++)
                    {
                        ClearCell(_selectedConfig, d, h);
                    }
                break;
        }

        BuildScheduleGrid();
    }

    private void ApplyPresetToAllDays(CameraRecordingConfigData config, int startHour, int endHour)
    {
        for (int d = 0; d < 7; d++)
            for (int h = startHour; h < endHour; h++)
            {
                ClearCell(config, d, h);
                SetCell(config, d, h, _currentMode);
            }
    }
    #endregion

    #region Audio
    private void AudioCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_selectedChannel?.Camera is null || _selectedConfig is null) { return; }
        _selectedConfig.EnableAudio = AudioCheckBox.IsChecked ?? false;
    }
    #endregion

    #region Save / Apply All
    private void SetAllContinuous_Click(object sender, RoutedEventArgs e)
    {
        var configured = new List<ChannelItem>(_channels.Count);
        foreach (var c in _channels)
        {
            if (c.Camera is not null && !string.IsNullOrEmpty(c.IpAddress))
            {
                configured.Add(c);
            }
        }

        if (configured.Count == 0)
        {
            MessageBox.Show("沒有已設定的攝影機可套用", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var result = MessageBox.Show(
            $"此操作將重設所有 {configured.Count} 臺攝影機的錄影設定為預設值\n並清除各攝影機目前的錄影排程",
            "重設錄影設定", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) { return; }

        var defaultConfig = new CameraRecordingConfigData();
        var json = defaultConfig.Serialize();
        int updated = 0;

        foreach (var ch in configured)
        {
            ch.Camera!.RecordingConfigJson = json;
            _cameraService.UpdateCamera(ch.Camera);
            updated++;
        }

        _eventLog.LogInfo(EventCategories.Setting, "RecordingSettings",
            $"已套用預設錄影設定至 {updated} 臺攝影機");

        LoadCameras();

        if (_selectedChannel is not null)
        {
            ChannelItem? match = null;
            foreach (var c in _channels)
            {
                if (c.ChannelNumber == _selectedChannel.ChannelNumber) { match = c; break; }
            }
            if (match is not null) { SelectChannel(match); }
        }

        MessageBox.Show($"已成功為 {updated} 臺攝影機重設錄影設定", "完成",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Save_Click()
    {
        if (_selectedChannel?.Camera is null || _selectedConfig is null) { return; }
        _selectedChannel.Camera.RecordingConfigJson = _selectedConfig.Serialize();
        _cameraService.UpdateCamera(_selectedChannel.Camera);
        _eventLog.LogInfo(EventCategories.Setting, "RecordingSettings",
            $"已成功儲存攝影機設定：{_selectedChannel.DisplayName}");
    }

    private void SaveAll_Click(object sender, RoutedEventArgs e)
    {
        Save_Click();
        MessageBox.Show("錄影設定已成功儲存", "儲存完成", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ApplyAll_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedChannel?.Camera is null || _selectedConfig is null)
        {
            MessageBox.Show("請先選取一個已指派攝影機的頻道", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var configuredChannels = new List<ChannelItem>(_channels.Count);
        foreach (var c in _channels)
        {
            if (c.Camera is not null && !string.IsNullOrEmpty(c.IpAddress))
            {
                configuredChannels.Add(c);
            }
        }

        var result = MessageBox.Show(
            $"即將將「{_selectedChannel.DisplayName}」的錄影設定套用至其他 {configuredChannels.Count - 1} 臺攝影機\n已啟用音訊的攝影機將保留其音訊設定",
            "套用設定", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) { return; }

        var templateJson = _selectedConfig.Serialize();
        int applied = 0;

        foreach (var ch in configuredChannels)
        {
            if (ch.ChannelNumber == _selectedChannel.ChannelNumber) { continue; }
            var cam = ch.Camera!;
            var newConfig = CameraRecordingConfigData.Deserialize(templateJson);
            if (newConfig is null) { continue; }
            var orig = CameraRecordingConfigData.Deserialize(cam.RecordingConfigJson);
            if (orig is not null) { newConfig.EnableAudio = orig.EnableAudio; }
            cam.RecordingConfigJson = newConfig.Serialize();
            _cameraService.UpdateCamera(cam);
            applied++;
        }

        _eventLog.LogInfo(EventCategories.Setting, "RecordingSettings",
            $"已成功將設定套用至 {applied} 臺攝影機（範本：{_selectedChannel.DisplayName}）");
        MessageBox.Show($"已成功套用至 {applied} 臺攝影機", "設定已套用", MessageBoxButton.OK, MessageBoxImage.Information);
        LoadCameras();
    }
    #endregion
}
