using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HeliVMS.Licensing;
using HeliVMS.Media;
using HeliVMS.Recording;
using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.App;

/// <summary>
/// 主視窗：M1 監看＋錄影控制中心。
/// </summary>
public partial class MainWindow : Window
{
    private readonly string _dataDir;
    private RtspClient? _rtsp;
    private SegmentRecorder? _recorder;
    private SqliteStore? _store;
    private SegmentRepository? _repo;
    private WriteableBitmap? _bitmap;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        _dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HeliVMS");
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var state = new LicenseManager().ValidateDefault();
        StatusText.Text = state.IsValid
            ? $"禾秝軟體開發團隊 · 已授權（{state.Payload!.Cameras} 路）"
            : $"未授權：{state.Message ?? state.Status.ToString()}";
    }

    private async void OnConnectClicked(object sender, RoutedEventArgs e)
    {
        if (_rtsp is { IsRunning: true })
        {
            await StopPreviewAsync();
            return;
        }

        var url = UrlBox.Text.Trim();
        if (url.Length == 0)
        {
            StatusOverlay.Text = "請輸入 RTSP 位址";
            return;
        }

        var rtsp = new RtspClient(url);
        rtsp.FrameDecoded += OnFrameDecoded;
        rtsp.Reconnecting += OnReconnecting;
        _rtsp = rtsp;

        StatusOverlay.Text = "連線中…";
        await rtsp.StartAsync();

        ConnectButton.Content = "中斷";
        RecordButton.IsEnabled = true;
        HintText.Text = $"連線中：{url}";
    }

    private async Task StopPreviewAsync()
    {
        if (_recorder is { IsRecording: true })
        {
            await _recorder.StopAsync();
            _recorder = null;
            RecordButton.Content = "錄影";
            RecBadge.Visibility = Visibility.Collapsed;
        }

        if (_rtsp is not null)
        {
            await _rtsp.StopAsync();
            await _rtsp.DisposeAsync();
            _rtsp = null;
        }

        _bitmap = null;
        LiveImage.Source = null;
        StatusOverlay.Text = string.Empty;
        ResText.Text = string.Empty;
        ConnectButton.Content = "連線";
        RecordButton.IsEnabled = false;
    }

    private void OnReconnecting(object? sender, Exception e)
    {
        Dispatcher.Invoke(() => StatusOverlay.Text = $"重連中…（{e.Message}）");
    }

    private void OnFrameDecoded(object? sender, VideoFrame frame)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (_bitmap is null ||
                _bitmap.PixelWidth != frame.Width ||
                _bitmap.PixelHeight != frame.Height)
            {
                _bitmap = new WriteableBitmap(
                    frame.Width, frame.Height, 96, 96, PixelFormats.Bgr24, null);
                LiveImage.Source = _bitmap;
                ResText.Text = $"{frame.Width}×{frame.Height}";
            }

            _bitmap.Lock();
            _bitmap.WritePixels(
                new Int32Rect(0, 0, frame.Width, frame.Height),
                frame.Pixels,
                frame.Stride,
                0);
            _bitmap.Unlock();

            var state = StatusOverlay.Text;
            if (state.StartsWith("連線中", StringComparison.Ordinal) ||
                state.StartsWith("重連中", StringComparison.Ordinal))
            {
                StatusOverlay.Text = $"即時監看 · {frame.Width}×{frame.Height}";
            }
        });
    }

    private async void OnRecordClicked(object sender, RoutedEventArgs e)
    {
        if (_recorder is { IsRecording: true })
        {
            await (_recorder?.StopAsync() ?? Task.CompletedTask);
            _recorder = null;
            RecordButton.Content = "錄影";
            RecBadge.Visibility = Visibility.Collapsed;
            HintText.Text = "錄影已停止。";
            return;
        }

        var url = UrlBox.Text.Trim();
        if (url.Length == 0)
        {
            return;
        }

        _store ??= new SqliteStore(Path.Combine(_dataDir, "helivms.db"));
        _store.Initialize();
        _repo ??= new SegmentRepository(_store);

        var recDir = Path.Combine(_dataDir, "recordings");
        Directory.CreateDirectory(recDir);

        var channelId = ChannelCombo.SelectedIndex + 1;
        var recorder = new SegmentRecorder(_repo);
        await recorder.StartAsync(channelId, url, recDir, segmentSeconds: 15);

        _recorder = recorder;
        RecordButton.Content = "停止錄影";
        RecBadge.Visibility = Visibility.Visible;
        HintText.Text = $"錄影中：{recDir}";
    }

    private async void OnWindowClosed(object? sender, EventArgs e)
    {
        if (_recorder is not null)
        {
            await _recorder.StopAsync();
            _recorder = null;
        }

        if (_rtsp is not null)
        {
            await _rtsp.StopAsync();
            _rtsp = null;
        }

        _store?.Dispose();
    }
}