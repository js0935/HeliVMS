using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HeliVMS.App.Services;
using HeliVMS.Media;
using HeliVMS.Shared;
using HeliVMS.Shared.Models;
using HeliVMS.Storage;
using StreamKind = HeliVMS.Storage.StreamKind;

namespace HeliVMS.App;

/// <summary>
/// 單一頻道即時畫面（電子地圖圖釘單擊時的落點）。
/// </summary>
/// <remarks>
/// 這裡刻意<em>優先重用</em> <see cref="ChannelManager"/> 已在跑的監看工作階段：地圖上一點就
/// 多開一條 RTSP 連線，操作員連點幾個圖釘就會讓同一台攝影機的連線數暴增，也多吃一份解碼
/// CPU。只有在主視窗沒有該頻道的監看時（例如地圖由事件中心開啟、沒有管理器可用）才自建
/// <see cref="RtspClient"/>，並且由本視窗負責把它關掉。
/// </remarks>
public partial class LiveWindow : Window
{
    private readonly SqliteStore _store;
    private readonly ChannelRepository _channels;
    private readonly int _channelId;
    private readonly ChannelManager? _manager;

    /// <summary>只保留一張待畫影格；繪圖跟不上時直接丟掉舊的，避免畫面愈拖愈舊。</summary>
    private readonly LatestFrameSlots<VideoFrame> _pendingFrames = new(1);

    private WriteableBitmap? _bitmap;
    private ChannelSession? _borrowed;
    private RtspClient? _own;
    private bool _closing;

    public LiveWindow(SqliteStore store, int channelId, ChannelManager? manager = null)
    {
        _store = store;
        _channels = new ChannelRepository(store);
        _channelId = channelId;
        _manager = manager;

        InitializeComponent();
        ApplyI18n();
        Retitle();

        Loaded += (_, _) => _ = StartAsync();
        KeyDown += OnKeyDown;
    }

    /// <summary>依現況語言套用標題、按鈕與說明（M57）。</summary>
    private void ApplyI18n()
    {
        Title = Localizer.T("Live.Title");
        PlaybackButton.Content = Localizer.T("Live.Playback");
        PlaybackButton.ToolTip = Localizer.T("Live.PlaybackTip");
        CloseButton.ToolTip = Localizer.T("Live.CloseTip");
        PlaybackMenuItem.Header = Localizer.T("Live.Playback");
        FooterText.Text = Localizer.T("Live.Footer");
    }

    /// <summary>標題帶上頻道名稱，多路同時開啟時才分得出哪一個是哪一台。</summary>
    private void Retitle()
    {
        var channel = _channels.Get(_channelId);
        var text = channel is null
            ? string.Format(Localizer.T("Live.NoChannel"), _channelId)
            : string.Format(Localizer.T("Live.TitleChannel"), channel.Name, _channelId);
        Title = text;
        HeaderTitle.Text = text;
    }

    private async Task StartAsync()
    {
        if (_closing)
        {
            return;
        }

        var channel = _channels.Get(_channelId);
        if (channel is null)
        {
            SetStatus(string.Format(Localizer.T("Live.NoChannel"), _channelId));
            return;
        }

        if (_manager?.ChannelSessionFor(_channelId) is { IsMonitoring: true } session)
        {
            _borrowed = session;
            session.FrameArrived += OnSessionFrame;
            session.StateChanged += OnSessionState;

            // 借用的工作階段不會補送目前的狀態事件；用最後一張影格的時間判斷，
            // 否則畫面已經在動、標題卻一直寫「連線中」。
            var age = DateTime.UtcNow - session.LastFrameUtc;
            if (session.LastFrameUtc != DateTime.MinValue && age < TimeSpan.FromSeconds(15))
            {
                StateText.Text = Localizer.T("Live.Streaming");
            }
            else
            {
                SetStatus(Localizer.T("Live.Connecting"));
            }

            return;
        }

        var url = ResolveUrl(channel);
        if (string.IsNullOrWhiteSpace(url))
        {
            SetStatus(Localizer.T("Live.NoStream"));
            return;
        }

        var client = new RtspClient(
            url,
            hwAccel: new SettingsRepository(_store).Get("decode.hwaccel"));
        _own = client;
        client.FrameDecoded += OnClientFrame;
        client.StateChanged += OnClientState;
        client.Reconnecting += OnClientReconnecting;

        SetStatus(Localizer.T("Live.Connecting"));
        try
        {
            await client.StartAsync();
        }
        catch (Exception ex)
        {
            SetStatus(string.Format(Localizer.T("Live.Failed"), ex.Message));
        }
    }

    /// <summary>
    /// 解析實際拉流位址：資料庫只存裸位址，設備帳密在連線當下才嵌入，
    /// 與 <c>ChannelManager.ResolveStreamUrl</c> 採同一條規則（見 <see cref="RtspStreamResolver"/>）。
    /// 單畫面優先主流；沒有主流時退回次流，總比什麼都不顯示好。
    /// </summary>
    private string ResolveUrl(ChannelInfo channel)
    {
        var devices = new DeviceRepository(_store, new AuditLogRepository(_store));
        var raw = StreamRouting.DisplayUrl(channel.MainStreamUrl, channel.SubStreamUrl, StreamKind.Main);
        if (string.IsNullOrWhiteSpace(raw))
        {
            raw = StreamRouting.DisplayUrl(channel.MainStreamUrl, channel.SubStreamUrl, StreamKind.Sub);
        }

        return RtspStreamResolver.Resolve(
            raw,
            channel.DeviceId,
            host => devices.FindByIp(host)?.Id,
            devices.GetRtspCredentials);
    }

    private void OnSessionFrame(object? sender, VideoFrame frame) => QueueFrame(frame);

    private void OnClientFrame(object? sender, VideoFrame frame) => QueueFrame(frame);

    /// <summary>
    /// 把影格排進 UI 佇列：同一時間最多一張在途，繪製期間抵達的影格直接覆蓋前一張。
    /// </summary>
    /// <remarks>
    /// 每張影格都 <c>InvokeAsync</c> 會讓 Dispatcher 佇列無上限膨脹，UI 一跟不上畫面就
    /// 愈拖愈舊（症狀是 CPU 沒滿載但延遲持續增加）。
    /// </remarks>
    private void QueueFrame(VideoFrame frame)
    {
        if (_closing)
        {
            return;
        }

        if (!_pendingFrames.TrySchedule(0, frame))
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(RenderFrame), DispatcherPriority.Render);
    }

    private void RenderFrame()
    {
        if (_closing)
        {
            return;
        }

        var frame = _pendingFrames.Take(0);
        if (frame is null)
        {
            return;
        }

        if (_bitmap is null || _bitmap.PixelWidth != frame.Width || _bitmap.PixelHeight != frame.Height)
        {
            _bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgr24, null);
            FrameImage.Source = _bitmap;
        }

        _bitmap.Lock();
        try
        {
            _bitmap.WritePixels(
                new Int32Rect(0, 0, frame.Width, frame.Height),
                frame.Pixels,
                frame.Stride,
                0);
        }
        finally
        {
            _bitmap.Unlock();
        }

        FrameImage.Visibility = Visibility.Visible;

        // 有畫面就把「連線中」蓋掉；狀態列仍保留文字，離開畫面時才不會突然空白。
        if (OverlayStatus.Visibility == Visibility.Visible)
        {
            SetStatus(string.Empty);
        }
    }

    private void OnSessionState(object? sender, RtspState state) => OnDispatcher(() => ShowState(state));

    private void OnClientState(object? sender, RtspState state) => OnDispatcher(() => ShowState(state));

    private void OnClientReconnecting(object? sender, Exception ex)
        => OnDispatcher(() => SetStatus($"{Localizer.T("Live.Reconnecting")} {Summarize(ex)}".TrimEnd()));

    private void OnDispatcher(Action action)
    {
        if (_closing)
        {
            return;
        }

        Dispatcher.BeginInvoke(action, DispatcherPriority.Background);
    }

    private void ShowState(RtspState state)
    {
        if (state == RtspState.Streaming)
        {
            StateText.Text = Localizer.T("Live.Streaming");
            if (FrameImage.Visibility == Visibility.Visible)
            {
                SetStatus(string.Empty);
            }

            return;
        }

        StateText.Text = state switch
        {
            RtspState.Connecting => Localizer.T("Live.Connecting"),
            RtspState.Reconnecting => Localizer.T("Live.Reconnecting"),
            _ => Localizer.T("Live.Stopped"),
        };

        if (state != RtspState.Connecting && FrameImage.Visibility != Visibility.Visible)
        {
            SetStatus(StateText.Text);
        }
    }

    private void SetStatus(string text)
    {
        StatusText.Text = text;
        OverlayStatus.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>把連線例外壓成一句可讀原因；整段 ffmpeg stderr 會把版面撐爆。</summary>
    private static string Summarize(Exception? ex)
    {
        if (ex is null || string.IsNullOrWhiteSpace(ex.Message))
        {
            return string.Empty;
        }

        var text = ex.Message.Trim();
        var newline = text.IndexOfAny(['\r', '\n']);
        if (newline >= 0)
        {
            text = text[..newline];
        }

        return text.Length <= 120 ? text : text[..120] + "…";
    }

    private void OnPlaybackClicked(object sender, RoutedEventArgs e) => OpenPlayback();

    private void OpenPlayback()
    {
        try
        {
            var playback = new PlaybackWindow(_store, _channelId) { Owner = this };
            playback.Show();
        }
        catch (Exception ex)
        {
            SetStatus(string.Format(Localizer.T("Live.Failed"), ex.Message));
        }
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _closing = true;
        _pendingFrames.Clear();

        // 借來的工作階段屬於主視窗，只能退訂，不能停掉或釋放，否則關掉地圖圖釘的即時畫面
        // 會順手把監看牆上同一路畫面也弄黑。
        if (_borrowed is { } borrowed)
        {
            borrowed.FrameArrived -= OnSessionFrame;
            borrowed.StateChanged -= OnSessionState;
            _borrowed = null;
        }

        if (_own is { } own)
        {
            own.FrameDecoded -= OnClientFrame;
            own.StateChanged -= OnClientState;
            own.Reconnecting -= OnClientReconnecting;
            _own = null;
            _ = StopOwnAsync(own);
        }

        base.OnClosed(e);
    }

    /// <summary>自己建立的連線要自己收乾淨（含 ffmpeg 子行程），否則關窗後行程會留在背景。</summary>
    private static async Task StopOwnAsync(RtspClient client)
    {
        try
        {
            await client.StopAsync();
        }
        catch
        {
            // 關窗路徑不該因收線失敗而拋出。
        }

        try
        {
            await client.DisposeAsync();
        }
        catch
        {
        }
    }
}
