using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HeliVMS.Shared.Models;

namespace HeliVMS.App;

/// <summary>
/// 第二螢幕鏡像牆（QC_Live 的多螢幕監看對齊，MVP）：
/// 主牆解碼的幀<b>轉發</b>到這裡的 2×2 格位，<b>不另開解碼行程、不重連</b>——
/// 只訂閱主牆已經畫出來的原始影格（<see cref="RenderFrame"/>）。
/// 格位固定四格：主牆目前頁簽的前四台；頻道異動時以 <see cref="SetChannels"/> 重新對位。
/// </summary>
/// <remarks>
/// <para>
/// 刻意不做的：佈局選擇、拖曳、右鍵、PTZ——控制權留在主視窗（操作員在主螢幕控制、
/// 第二螢幕純顯示，正是錄影牆的用法）。每格的時間 OSD 每 500ms 才更新一次
/// （每幀改 TextBlock 會讓 WPF 每秒重排上百次，同 PaneOsdInterval 的教訓）。
/// </para>
/// <para>
/// 依賴 WinForms 的 <c>Screen.AllScreens</c> 定位第二螢幕；App 專案需啟用
/// <c>UseWindowsForms</c>（若未啟用，此視窗會在編譯期報錯——那是有意的守門）。
/// </para>
/// </remarks>
public partial class WallMirrorWindow : Window
{
    /// <summary>MVP 的固定格數：2×2。更多的格位與佈局選擇屬下一輪。</summary>
    public const int CellCount = 4;

    private sealed class MirrorCell
    {
        public required Image Image { get; init; }
        public required TextBlock Label { get; init; }
        public WriteableBitmap? Bitmap { get; set; }
        public int ChannelId { get; set; }
        public string Name { get; set; } = string.Empty;
        public long LastLabelTicks;
    }

    private readonly MirrorCell?[] _cells = new MirrorCell[CellCount];
    private bool _closed;

    public WallMirrorWindow()
    {
        InitializeComponent();
        for (var i = 0; i < CellCount; i++)
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
            var inner = new Grid();
            inner.Children.Add(image);
            inner.Children.Add(label);
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
            _cells[i] = new MirrorCell { Image = image, Label = label };
        }

        ApplyI18n();
        PutOnSecondaryScreen();
    }

    private void ApplyI18n()
    {
        Title = Localizer.T("Main.MirrorTitle");
        CloseButton.ToolTip = Localizer.T("Main.MirrorCloseTip");
    }

    /// <summary>有第二個螢幕就放第二螢幕（工作區最大化），否則退回主螢幕的視窗化備用位置。</summary>
    private void PutOnSecondaryScreen()
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        WindowStartupLocation = WindowStartupLocation.Manual;
        if (screens.Length >= 2)
        {
            var secondary = screens.First(s => !s.Primary);
            WindowState = WindowState.Maximized;
            Left = secondary.WorkingArea.Left;
            Top = secondary.WorkingArea.Top;
            Width = secondary.WorkingArea.Width;
            Height = secondary.WorkingArea.Height;
        }
        else
        {
            WindowState = WindowState.Normal;
            Left = 80;
            Top = 80;
        }
    }

    /// <summary>設定格位與頻道對應（主牆頁簽的前 <see cref="CellCount"/> 台，依牆序）。</summary>
    public void SetChannels(IReadOnlyList<ChannelInfo> wallChannels)
    {
        if (_closed)
        {
            return;
        }

        for (var i = 0; i < CellCount; i++)
        {
            var cell = _cells[i];
            if (cell is null)
            {
                continue;
            }

            var channel = i < wallChannels.Count ? wallChannels[i] : null;
            cell.ChannelId = channel?.Id ?? 0;
            cell.Name = channel?.Name ?? string.Empty;
            cell.Label.Text = channel is null ? string.Empty : channel.Name;
            if (channel is null)
            {
                cell.Image.Source = null;
                cell.Bitmap = null;
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

        for (var i = 0; i < CellCount; i++)
        {
            var cell = _cells[i];
            if (cell is null || cell.ChannelId != channelId)
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
                cell.Label.Text = $"{cell.Name} · {frame.TimestampUtc.ToLocalTime():HH:mm:ss}";
            }

            return;
        }
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }

    /// <summary>主視窗在關閉時呼叫：清格位並解除幀轉發（轉發點在主視窗的 RenderCellFrame）。</summary>
    public void Shutdown()
    {
        _closed = true;
        for (var i = 0; i < CellCount; i++)
        {
            var cell = _cells[i];
            if (cell is null)
            {
                continue;
            }

            cell.Image.Source = null;
            cell.Bitmap = null;
        }
    }
}
