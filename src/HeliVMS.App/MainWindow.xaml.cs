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
/// 主視窗：M1–M2 監看（單一/多格佈局）與錄影控制中心。
/// 資料基準目錄 C:\HeliVMSData（§2.1）。
/// </summary>
public partial class MainWindow : Window
{
    private const string DefaultDataRoot = @"C:\HeliVMSData";
    private const int MaxCells = 4;

    private readonly string _dataRoot;
    private readonly string _legacyDir;
    private SqliteStore? _store;
    private ChannelRepository? _channels;
    private SegmentRepository? _segRepo;
    private RtspClient?[] _rtsp = new RtspClient?[MaxCells];
    private WriteableBitmap?[] _bitmap = new WriteableBitmap?[MaxCells];
    private SegmentRecorder? _recorder;
    private IReadOnlyList<ChannelInfo> _channelList = [];

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        _legacyDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HeliVMS");
        _dataRoot = ResolveDataRoot();
    }

    /// <summary>由執行目錄 Assets 資料夾載入品牌圖檔。</summary>
    public static BitmapSource CreateBitmap(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", fileName);
        return BitmapFrame.Create(
            new Uri(path, UriKind.Absolute),
            BitmapCreateOptions.None,
            BitmapCacheOption.OnLoad);
    }

    /// <summary>資料根目錄：優先 C:\HeliVMSData（§2.1）；不可寫時降級至本機資料夾。</summary>
    private static string ResolveDataRoot()
    {
        var fromEnv = Environment.GetEnvironmentVariable("HELIVMS_DATA");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return fromEnv;
        }

        try
        {
            Directory.CreateDirectory(DefaultDataRoot);
            return DefaultDataRoot;
        }
        catch (UnauthorizedAccessException)
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HeliVMS");
        }
        catch (IOException)
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HeliVMS");
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Title = $"{Title} | db={Path.Combine(_dataRoot, "index.db")}";
        var icon = CreateBitmap("app.ico");
        Icon = icon;
        HeaderLogo.Source = icon;
        MigrateLegacyData();

        _store = new SqliteStore(Path.Combine(_dataRoot, "index.db"));
        _store.Initialize();
        _channels = new ChannelRepository(_store);
        _segRepo = new SegmentRepository(_store);
        _channels.EnsureSeedChannels();

        ChannelCombo.SelectionChanged += OnChannelSelectionChanged;
        RefreshChannelCombo();

        var state = new LicenseManager().ValidateDefault();
        StatusText.Text = state.IsValid
            ? $"禾秝軟體開發團隊 · 已授權（{state.Payload!.Cameras} 路）"
            : $"未授權：{state.Message ?? state.Status.ToString()}";
    }

    /// <summary>將 M1 舊資料（%LOCALAPPDATA%\HeliVMS）遷移至 C:\HeliVMSData（若尚未存在）。</summary>
    private void MigrateLegacyData()
    {
        if (!Directory.Exists(_legacyDir))
        {
            return;
        }

        Directory.CreateDirectory(_dataRoot);
        var targetDb = Path.Combine(_dataRoot, "index.db");
        if (!File.Exists(targetDb))
        {
            var legacyDb = Path.Combine(_legacyDir, "helivms.db");
            if (File.Exists(legacyDb))
            {
                File.Copy(legacyDb, targetDb);
            }
        }

        var legacyRec = Path.Combine(_legacyDir, "recordings");
        var targetRec = Path.Combine(_dataRoot, "recordings");
        if (Directory.Exists(legacyRec) && !Directory.Exists(targetRec))
        {
            Directory.Move(legacyRec, targetRec);
        }
    }

    private void RefreshChannelCombo()
    {
        _channelList = _channels!.List().OrderBy(c => c.Id).ToList();
        ChannelCombo.ItemsSource = _channelList;
        ChannelCombo.DisplayMemberPath = nameof(ChannelInfo.Name);
        ChannelCombo.SelectedIndex = _channelList.Count > 0 ? 0 : -1;
    }

    private void OnChannelSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ChannelCombo.SelectedItem is ChannelInfo ch)
        {
            UrlBox.Text = ch.MainStreamUrl;
        }
    }

    private void OnLayoutChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateCellLayout();
    }

    private void UpdateCellLayout()
    {
        if (ImageCell1 is null)
        {
            return;
        }

        var single = LayoutCombo.SelectedIndex == 0;
        ImageCell1.Visibility = TextCell1.Visibility =
            ImageCell2.Visibility = TextCell2.Visibility =
            ImageCell3.Visibility = TextCell3.Visibility =
                single ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void OnConnectClicked(object sender, RoutedEventArgs e)
    {
        if (_rtsp.Any(c => c is { IsRunning: true }))
        {
            await DisconnectAllAsync();
            return;
        }

        if (_channelList.Count == 0)
        {
            SetCellStatus(0, "無可用頻道", null);
            return;
        }

        var start = Math.Max(ChannelCombo.SelectedIndex, 0);
        var cells = LayoutCombo.SelectedIndex == 0 ? 1 : MaxCells;

        for (var i = 0; i < cells; i++)
        {
            var channel = _channelList[(start + i) % _channelList.Count];
            var rtsp = new RtspClient(channel.MainStreamUrl);
            var cell = i;
            rtsp.FrameDecoded += (_, f) => OnCellFrame(cell, f);
            rtsp.Reconnecting += (_, ex) => OnCellReconnect(cell, ex);
            _rtsp[cell] = rtsp;
            SetCellStatus(cell, "連線中…", null);
            await rtsp.StartAsync();
        }

        ConnectButton.Content = "中斷";
        RecordButton.IsEnabled = true;
        HintText.Text = $"連線中：{_channelList[start].MainStreamUrl}";
    }

    private void OnCellReconnect(int cell, Exception ex)
    {
        Dispatcher.Invoke(() => SetCellStatus(cell, $"重連中…（{ex.Message}）", null));
    }

    private void OnCellFrame(int cell, VideoFrame frame)
    {
        Dispatcher.InvokeAsync(() =>
        {
            var pending = _bitmap[cell];
            if (pending is null ||
                pending.PixelWidth != frame.Width ||
                pending.PixelHeight != frame.Height)
            {
                pending = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgr24, null);
                _bitmap[cell] = pending;
                SetCellSource(cell, pending);
            }

            pending.Lock();
            try
            {
                pending.WritePixels(
                    new Int32Rect(0, 0, frame.Width, frame.Height),
                    frame.Pixels,
                    frame.Stride,
                    0);
            }
            finally
            {
                pending.Unlock();
            }

            if (GetCellStatusText(cell) is { } s &&
                (s.StartsWith("連線中", StringComparison.Ordinal) ||
                 s.StartsWith("重連中", StringComparison.Ordinal)))
            {
                SetCellStatus(cell, $"即時監看 · {frame.Width}×{frame.Height}", null);
            }
        });
    }

    private async Task DisconnectAllAsync()
    {
        if (_recorder is { IsRecording: true })
        {
            await (_recorder?.StopAsync() ?? Task.CompletedTask);
            _recorder = null;
            RecordButton.Content = "錄影";
            RecBadge.Visibility = Visibility.Collapsed;
        }

        for (var i = 0; i < MaxCells; i++)
        {
            var rtsp = _rtsp[i];
            if (rtsp is not null)
            {
                await rtsp.StopAsync();
                await rtsp.DisposeAsync();
                _rtsp[i] = null;
            }

            _bitmap[i] = null;
            SetCellSource(i, null);
            SetCellStatus(i, "未連線", null);
        }

        ConnectButton.Content = "連線";
        RecordButton.IsEnabled = false;
        HintText.Text = "已中斷。";
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

        if (ChannelCombo.SelectedItem is not ChannelInfo channel)
        {
            return;
        }

        var root = Path.Combine(_dataRoot, "recordings");
        var recorder = new SegmentRecorder(_segRepo!);
        await recorder.StartAsync(channel.Id, channel.MainStreamUrl, root, "main", segmentSeconds: 15);

        _recorder = recorder;
        RecordButton.Content = "停止錄影";
        RecBadge.Visibility = Visibility.Visible;
        HintText.Text = $"錄影中：{Path.Combine(root, $"ch{channel.Id:000}")}";
    }

    private void OnAddChannelClicked(object sender, RoutedEventArgs e)
    {
        var url = UrlBox.Text.Trim();
        if (url.Length == 0)
        {
            return;
        }

        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            name = $"頻道 {_channelList.Count + 1}";
        }

        _channels!.Add(name, url);
        RefreshChannelCombo();
        HintText.Text = $"已加入頻道「{name}」。";
    }

    private Image CellImage(int cell) => cell switch
    {
        0 => ImageCell0,
        1 => ImageCell1,
        2 => ImageCell2,
        _ => ImageCell3,
    };

    private TextBlock CellText(int cell) => cell switch
    {
        0 => TextCell0,
        1 => TextCell1,
        2 => TextCell2,
        _ => TextCell3,
    };

    private void SetCellSource(int cell, BitmapSource? source) => CellImage(cell).Source = source;

    private void SetCellStatus(int cell, string text, SolidColorBrush? brush)
    {
        var block = CellText(cell);
        block.Text = text;
        block.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        if (brush is not null)
        {
            block.Foreground = brush;
        }
    }

    private string? GetCellStatusText(int cell) => CellText(cell).Text;

    private async void OnWindowClosed(object? sender, EventArgs e)
    {
        await DisconnectAllAsync();
        _store?.Dispose();
    }
}