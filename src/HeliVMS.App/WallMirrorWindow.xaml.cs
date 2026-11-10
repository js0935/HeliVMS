using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HeliVMS.Shared;
using HeliVMS.Shared.Models;

namespace HeliVMS.App;

/// <summary>
/// 一格要顯示的狀態（由主牆每秒推來一次）。<see cref="DotBrush"/> 直接帶著已凍結的筆刷，
/// 鏡像端不重算色票——色票只有主牆那一份，兩端各轉一次就是兩個會漂移的圖例。
/// </summary>
/// <param name="Text">左下角徽章文字（主牆組好的那一份，含錄影方式與沒錄的原因）。</param>
/// <param name="Tip">錄影方式的 tooltip。</param>
/// <param name="DotBrush">右上角指示燈的筆刷；<see langword="null"/>＝這一格不收燈。</param>
/// <param name="DotOpacity">燈的明暗（全亮＝正在錄）。</param>
public sealed record MirrorBadge(string Text, string? Tip, System.Windows.Media.Brush? DotBrush, double DotOpacity);

/// <summary>
/// 第二螢幕鏡像牆（QC_Live 的多螢幕監看對齊）：
/// 主牆解碼的幀<b>轉發</b>到這裡的格位，<b>不另開解碼行程、不重連</b>——
/// 只訂閱主牆已經畫出來的原始影格（<see cref="RenderFrame"/>）。
/// </summary>
/// <remarks>
/// <para>
/// 格數由 <see cref="MirrorWallBudget.SplitOptions"/> 決定（1／2／4／9／16，預設 4）：鏡像不花解碼
/// 成本，花的是每格一張 <c>WriteableBitmap</c> 的貼圖成本，所以上限刻意低於主牆的 64 格。
/// 操作員在第二螢幕上按右上角的分割鈕循環切換，<see cref="SplitChanged"/> 讓主視窗把它記進
/// <c>wall.mirror.split</c>——不記的話每次開機都要重設一次，而「昨天明明是 9 格」這種回報
/// 永遠查不到原因。
/// </para>
/// <para>
/// 刻意不做的：拖曳、右鍵、PTZ——控制權留在主視窗（操作員在主螢幕控制、第二螢幕純顯示，
/// 正是錄影牆的用法）。每格的時間 OSD 每 500ms 才更新一次（每幀改 TextBlock 會讓 WPF 每秒
/// 重排上百次，同 <c>PaneOsdInterval</c> 的教訓）。
/// </para>
/// <para>
/// 拔插：訂閱 <c>SystemEvents.DisplaySettingsChanged</c>。所在螢幕被拔掉時，搬到另一個非主螢幕；
/// 一個都不剩就<b>關窗</b>而不是縮成主螢幕上的小窗——小窗會蓋在主牆上，操作員看到的是
/// 「主畫面多了一塊奇怪的縮圖」，而真正該發生的是回到單螢幕（主視窗的勾選也會同步取消）。
/// </para>
/// </remarks>
public partial class WallMirrorWindow : Window
{
    /// <summary>格位（依序對應主牆目前頁簽的頻道）。</summary>
    private sealed class MirrorCell
    {
        public required Image Image { get; init; }
        public required TextBlock Label { get; init; }
        public required System.Windows.Shapes.Ellipse Dot { get; init; }
        public WriteableBitmap? Bitmap { get; set; }
        public int ChannelId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string BadgeText { get; set; } = string.Empty;
        public long LastLabelTicks;
    }

    private readonly List<MirrorCell> _cells = [];
    private IReadOnlyList<ChannelInfo> _lastChannels = [];
    private string? _screenDeviceName;
    private bool _closed;

    /// <summary>分割數被操作員改變（要寫回設定）。</summary>
    public event EventHandler<int>? SplitChanged;

    public WallMirrorWindow()
    {
        InitializeComponent();
        ApplyI18n();
        SetSplit(MirrorWallBudget.DefaultCells);
        PlaceOnSecondary(closeWhenNoSecondary: false);
    }

    /// <summary>目前的格數。</summary>
    public int CurrentSplit => _cells.Count;

    private void ApplyI18n()
    {
        Title = Localizer.T("Main.MirrorTitle");
        CloseButton.ToolTip = Localizer.T("Main.MirrorCloseTip");
        SplitButton.ToolTip = Localizer.T("Main.MirrorSplitTip");
    }

    /// <summary>
    /// 改變格數並重建格位；非法值由 <see cref="MirrorWallBudget.Clamp"/> 夾回預設。
    /// 重建後立即以最後一次的頻道清單重新對位——否則切一次分割就要等下次切頁簽才有畫面。
    /// </summary>
    public void SetSplit(int cells)
    {
        if (_closed)
        {
            return;
        }

        var count = MirrorWallBudget.Clamp(cells);
        var (rows, columns) = MirrorWallBudget.GridOf(count);

        MirrorGrid.Rows = rows;
        MirrorGrid.Columns = columns;
        MirrorGrid.Children.Clear();
        _cells.Clear();

        for (var i = 0; i < count; i++)
        {
            _cells.Add(CreateCell());
        }

        SplitButton.Content = count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        SetChannels(_lastChannels);
        SplitChanged?.Invoke(this, count);
    }

    private MirrorCell CreateCell()
    {
        var image = new Image { Stretch = Stretch.UniformToFill };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.Linear);
        var label = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(4),
            Padding = new Thickness(7, 2, 7, 2),
            Background = new SolidColorBrush(Color.FromArgb(0xB0, 0x0D, 0x15, 0x26)),
            Foreground = Brushes.White,
            FontSize = 12,
            Text = string.Empty,
        };
        // 右上角的錄影方式指示燈（顏色＝模式、明暗＝現在有沒有在錄），與主牆同一套色票。
        var dot = new System.Windows.Shapes.Ellipse
        {
            Width = 11,
            Height = 11,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(6),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        var inner = new Grid();
        inner.Children.Add(image);
        inner.Children.Add(label);
        inner.Children.Add(dot);
        var border = new Border
        {
            Margin = new Thickness(1),
            Background = new SolidColorBrush(Color.FromRgb(0x06, 0x09, 0x0F)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1F, 0x3A, 0x5F)),
            BorderThickness = new Thickness(1),
            Child = inner,
            ClipToBounds = true,
        };
        MirrorGrid.Children.Add(border);
        return new MirrorCell { Image = image, Label = label, Dot = dot };
    }

    /// <summary>
    /// 套用主牆推來的每格狀態（第 i 筆對應第 i 格，依牆序）。每秒一次，與主牆的
    /// <c>UpdateBadges</c> 同一個節奏。
    /// </summary>
    public void ApplyBadges(IReadOnlyList<MirrorBadge> badges)
    {
        if (_closed)
        {
            return;
        }

        for (var i = 0; i < _cells.Count; i++)
        {
            var cell = _cells[i];
            var badge = i < badges.Count ? badges[i] : null;

            cell.BadgeText = badge?.Text ?? string.Empty;
            cell.Label.ToolTip = badge?.Tip;
            cell.Dot.Fill = badge?.DotBrush;
            cell.Dot.Opacity = badge?.DotOpacity ?? 1.0;
            cell.Dot.Visibility = badge?.DotBrush is null ? Visibility.Collapsed : Visibility.Visible;

            // 影格還沒到也要畫（攝影機離線時操作員更需要看到「這格是哪一台、該不該在錄」）。
            cell.Label.Text = WallMirrorLabel.Compose(cell.BadgeText, cell.Name, DateTime.Now);
        }
    }

    /// <summary>有第二個螢幕就放第二螢幕（工作區最大化），否則退回主螢幕的視窗化備用位置。</summary>
    private void PlaceOnSecondary(bool closeWhenNoSecondary)
    {
        var screens = System.Windows.Forms.Screen.AllScreens;

        // 原本所在的螢幕還在（只是換了解析度）就留在原地：Windows 會自己把最大化視窗
        // 重排到新的工作區，這裡再動一次只會閃一下。只有「那個螢幕不見了」才需要搬家。
        var target = screens.FirstOrDefault(s => string.Equals(s.DeviceName, _screenDeviceName, StringComparison.Ordinal))
            ?? screens.FirstOrDefault(s => !s.Primary);

        if (target is null)
        {
            if (closeWhenNoSecondary)
            {
                // 第二螢幕被拔掉：關窗而不是縮在主螢幕上蓋住主牆。
                Close();
                return;
            }

            // 初次開窗時本來就只有一個螢幕（使用者自己按的）：沿用視窗化備用位置。
            WindowState = WindowState.Normal;
            Left = 80;
            Top = 80;
            _screenDeviceName = screens.FirstOrDefault()?.DeviceName;
            return;
        }

        var moved = !string.Equals(target.DeviceName, _screenDeviceName, StringComparison.Ordinal);
        _screenDeviceName = target.DeviceName;
        if (!moved)
        {
            return;
        }

        // 最大化視窗的 Left/Top 會被忽略，要先回到 Normal 才能指定新的工作區。
        WindowState = WindowState.Normal;
        Left = target.WorkingArea.Left;
        Top = target.WorkingArea.Top;
        Width = target.WorkingArea.Width;
        Height = target.WorkingArea.Height;
        WindowState = WindowState.Maximized;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // 螢幕拔插／換解析度。事件可能從系統執行緒送來，視窗屬性只能在 UI 執行緒動。
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (_closed)
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_closed)
            {
                PlaceOnSecondary(closeWhenNoSecondary: true);
            }
        }));
    }

    /// <summary>設定格位與頻道對應（主牆頁簽的前 N 台，依牆序）。</summary>
    public void SetChannels(IReadOnlyList<ChannelInfo> wallChannels)
    {
        if (_closed)
        {
            return;
        }

        _lastChannels = wallChannels;

        for (var i = 0; i < _cells.Count; i++)
        {
            var cell = _cells[i];
            var channel = i < wallChannels.Count ? wallChannels[i] : null;
            cell.ChannelId = channel?.Id ?? 0;
            cell.Name = channel?.Name ?? string.Empty;
            cell.BadgeText = string.Empty;
            cell.Label.ToolTip = null;
            cell.Label.Text = WallMirrorLabel.Compose(null, cell.Name, DateTime.Now);
            if (channel is null)
            {
                cell.Image.Source = null;
                cell.Bitmap = null;
                cell.Dot.Fill = null;
                cell.Dot.Visibility = Visibility.Collapsed;
            }
        }
    }

    /// <summary>
    /// 畫一幀（主牆 UI 執行緒轉發過來的原始 BGR24 影格）。
    /// 頻道不在鏡像格位內、或視窗已關：直接忽略。
    /// </summary>
    public void RenderFrame(int channelId, VideoFrame frame)
    {
        if (_closed)
        {
            return;
        }

        // 不提早 return：同一台若同時佔了兩格（主牆把小群組放大到 9 格時會發生），
        // 提早結束只會讓後面那格永遠停在舊畫面，且沒有任何錯誤訊息。
        foreach (var cell in _cells)
        {
            if (cell.ChannelId != channelId)
            {
                continue;
            }

            if (cell.Bitmap is null || cell.Bitmap.PixelWidth != frame.Width || cell.Bitmap.PixelHeight != frame.Height)
            {
                cell.Bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, System.Windows.Media.PixelFormats.Bgr24, null);
                cell.Image.Source = cell.Bitmap;
            }

            cell.Bitmap.Lock();
            try
            {
                cell.Bitmap.WritePixels(
                    new Int32Rect(0, 0, frame.Width, frame.Height),
                    frame.Pixels,
                    frame.Stride,
                    0);
            }
            finally
            {
                cell.Bitmap.Unlock();
            }

            // 時間 OSD 每 500ms 一次：每幀改 TextBlock 會讓 WPF 每秒重排上百次。
            var ticks = Environment.TickCount64;
            if (ticks - cell.LastLabelTicks > 500)
            {
                cell.LastLabelTicks = ticks;
                cell.Label.Text = WallMirrorLabel.Compose(cell.BadgeText, cell.Name, frame.TimestampUtc.ToLocalTime());
            }
        }
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();

    /// <summary>循環切換分割（1→2→4→9→16→1）。</summary>
    private void OnSplitClicked(object sender, RoutedEventArgs e)
    {
        var options = MirrorWallBudget.SplitOptions;
        var current = options.ToList().IndexOf(_cells.Count);
        SetSplit(options[(current + 1) % options.Count]);
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        // 一定要退訂：SystemEvents 是行程層級的靜態事件，留著就是每次開窗多一個永久的
        // 訂閱者（視窗關了還在收事件，然後對著已釋放的 Dispatcher 排工作）。
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        Shutdown();
        base.OnClosed(e);
    }

    /// <summary>清格位並停止轉發（主視窗關閉或鏡像窗關閉時呼叫；可重複呼叫）。</summary>
    public void Shutdown()
    {
        _closed = true;
        foreach (var cell in _cells)
        {
            cell.Image.Source = null;
            cell.Bitmap = null;
        }
    }
}
