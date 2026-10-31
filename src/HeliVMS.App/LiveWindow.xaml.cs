using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HeliVMS.App.Services;
using HeliVMS.Media;
using HeliVMS.Recording;
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
///
/// <para>
/// 視窗刻意是「小視窗」而非滿版：地圖的用途是一邊看平面圖一邊確認某個點位有沒有事，
/// 全螢幕會把剛點的那張地圖整個蓋掉，操作員得先關窗才回得到地圖。多開幾個圖釘時以
/// 階梯錯位排開，才不會疊成一張只看到最後開的那個。
/// </para>
/// </remarks>
public partial class LiveWindow : Window
{
    /// <summary>目前開著的即時畫面數量；只用來決定新視窗的錯位量。僅在 UI 執行緒存取。</summary>
    private static int _openCount;

    private readonly SqliteStore _store;
    private readonly ChannelRepository _channels;
    private readonly int _channelId;
    private readonly ChannelManager? _manager;

    /// <summary>
    /// 回放前收檔的服務；由主視窗一路傳進來。null 時回放照舊只看索引現況。
    /// </summary>
    private readonly IPlaybackRecordingFinalizer? _playbackFinalizer;
    private readonly int _cascadeIndex;

    /// <summary>只保留一張待畫影格；繪圖跟不上時直接丟掉舊的，避免畫面愈拖愈舊。</summary>
    private readonly LatestFrameSlots<VideoFrame> _pendingFrames = new(1);

    private WriteableBitmap? _bitmap;
    private ChannelSession? _borrowed;
    private RtspClient? _own;
    private bool _closing;

    /// <summary>
    /// 已開著的即時畫面，以頻道 id 為鍵：同一路攝影機只留一個視窗。
    /// </summary>
    /// <remarks>
    /// 圖釘很容易被連點（第一下沒反應就會再點一下），而每開一個視窗就是一條 RTSP 連線
    /// 加一份解碼。更重要的是「同一個畫面開五個視窗疊在一起」對操作員毫無意義——他分不出
    /// 哪個是哪個，關窗還會一個一個冒出來。所以重複點同一顆圖釘的行為改成「把已經開著的
    /// 那個視窗提到最前面」。只在 UI 執行緒存取。
    /// </remarks>
    private static readonly Dictionary<int, LiveWindow> s_open = new();

    public LiveWindow(
        SqliteStore store,
        int channelId,
        ChannelManager? manager = null,
        IPlaybackRecordingFinalizer? playbackFinalizer = null)
    {
        _store = store;
        _channels = new ChannelRepository(store);
        _channelId = channelId;
        _manager = manager;
        _playbackFinalizer = playbackFinalizer;
        _cascadeIndex = _openCount++;

        InitializeComponent();
        ApplyI18n();
        Retitle();

        // 註冊在「建立視窗」而不是「點擊圖釘」的路徑上：任何開窗來源（地圖、事件中心）
        // 都自動得到「同一頻道只有一個視窗」，不必每個呼叫端各寫一次。
        s_open[channelId] = this;

        Loaded += (_, _) =>
        {
            CascadeFromCenter();
            _ = StartAsync();
        };
        KeyDown += OnKeyDown;
    }

    /// <summary>
    /// 開啟某個頻道的即時畫面；該頻道已經開著就把原視窗提到最前面，不再開第二個。
    /// </summary>
    /// <param name="store">資料庫。</param>
    /// <param name="channelId">要顯示的頻道。</param>
    /// <param name="manager">主視窗的管理器；沒有時本視窗自建連線。</param>
    /// <param name="owner">擁有者視窗（地圖）；重複使用既有視窗時忽略。</param>
    /// <param name="playbackFinalizer">回放前收檔的服務，一路傳給本視窗開的回放。</param>
    public static void ShowFor(
        SqliteStore store,
        int channelId,
        ChannelManager? manager = null,
        Window? owner = null,
        IPlaybackRecordingFinalizer? playbackFinalizer = null)
    {
        if (s_open.TryGetValue(channelId, out var existing))
        {
            if (existing.IsLoaded)
            {
                existing.BringToFront();
                return;
            }

            // 建到一半就失敗的殘留（Show 前就擲回），直接丟掉再開一個。
            s_open.Remove(channelId);
            _openCount = Math.Max(0, _openCount - 1);
        }

        var window = new LiveWindow(store, channelId, manager, playbackFinalizer);
        if (owner is not null)
        {
            window.Owner = owner;
        }

        try
        {
            window.Show();
        }
        catch
        {
            // 開不起來也要把註冊清掉，否則這條頻道之後永遠只會「還原」一個沒開成的視窗。
            s_open.Remove(channelId);
            throw;
        }
    }

    /// <summary>
    /// 把已開著的視窗提到最前面（可能被地圖或其他即時畫面蓋住）。
    /// </summary>
    private void BringToFront()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        // 只呼叫 Activate 在「視窗屬於同一個 owner 群組」時偶爾不會真的提到最上層
        // （例如另一個即時畫面正好在它前面）。Topmost 一開一關可以強制它回到前景，
        // 又不會留下「永遠置頂」這種更惱人的行為。
        Activate();
        Topmost = true;
        Topmost = false;
    }

    /// <summary>
    /// 第二個以後的視窗往右下錯開。少了這段，從地圖連開三個圖釘只會看到最上面那一張，
    /// 另外兩個完全被蓋住，看起來像「點了沒反應」。
    /// </summary>
    private void CascadeFromCenter()
    {
        if (_cascadeIndex <= 0)
        {
            return;
        }

        // 最多錯開 6 階，再多的話視窗會跑到螢幕外。
        var step = 26.0 * Math.Min(_cascadeIndex, 6);
        Left += step;
        Top += step;

        var area = SystemParameters.WorkArea;
        if (Left + Width > area.Right)
        {
            Left = Math.Max(area.Left, area.Right - Width);
        }

        if (Top + Height > area.Bottom)
        {
            Top = Math.Max(area.Top, area.Bottom - Height);
        }
    }

    /// <summary>
    /// 拖曳自繪標題列移動視窗（無邊框視窗沒有系統標題列可按）。
    /// </summary>
    /// <remarks>
    /// 標題列上還有「回放」與「關閉」兩顆按鈕。事件會從按鈕冒泡到標題列，若直接在
    /// 標題列啟動 <see cref="Window.DragMove"/>，它會接管滑鼠捕獲並吃掉隨後的 Click，
    /// 按鈕看起來就完全沒反應——所以碰到按鈕就放行。
    /// </remarks>
    private void OnHeaderDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed || FindButton(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 使用者在 DragMove 之前就放開滑鼠時會擲；不影響任何狀態。
        }
    }

    private static System.Windows.Controls.Button? FindButton(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is System.Windows.Controls.Button button)
            {
                return button;
            }

            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }

        return null;
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

    /// <summary>以本視窗的頻道開回放，並直接播當日最後一段。</summary>
    /// <remarks>
    /// 只帶頻道而不起播，會得到一個「選好台、清單有資料、播放器全黑」的視窗——從即時畫面
    /// 按回放的人會以為回放壞了。地圖圖釘的右鍵回放（<see cref="MapWindow.OpenPlayback"/>）
    /// 走同一條路。
    /// </remarks>
    private void OpenPlayback()
    {
        try
        {
            var playback = new PlaybackWindow(_store, _channelId, null, autoPlayLatest: true, playbackFinalizer: _playbackFinalizer) { Owner = this };
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
        _openCount = Math.Max(0, _openCount - 1);

        // 只移除「自己」：同一頻道萬一已經換成新視窗（舊的正在關），不能把新的那筆註冊刪掉。
        if (s_open.TryGetValue(_channelId, out var registered) && ReferenceEquals(registered, this))
        {
            s_open.Remove(_channelId);
        }

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
