using System.Globalization;
using System.Windows;
using HeliVMS.Recording.Talk;
using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.App;

/// <summary>
/// 雙向對講視窗（M300，§14.1 #11）：選擇頻道與音訊檔，經 <see cref="TalkService"/> 啟動一次
/// 對講子行程送到攝影機喇叭，顯示進行中狀態，並可隨時中止。
/// </summary>
/// <remarks>
/// 對講是一次性的短命子行程（音訊播完即結束），因此 UI 用「進行中／已結束」兩個狀態表示，
/// 而不是常駐開關。停止＝取消 <see cref="TalkSession.WaitAsync"/>，由它負責砍掉子行程。
/// 此視窗只負責啟動與顯示；供應商解析與指令組裝都在 <see cref="TalkService"/> 內，
/// 桌面端不需要知道各廠牌的差別。
/// </remarks>
public partial class TalkWindow : Window
{
    private readonly TalkService _talk = new();
    private readonly IReadOnlyList<ChannelInfo> _channels;
    private TalkSession? _session;
    private CancellationTokenSource? _cts;

    public TalkWindow(SqliteStore store)
    {
        InitializeComponent();
        ApplyI18n();

        _channels = new ChannelRepository(store).List();
        foreach (var channel in _channels)
        {
            TalkChannelCombo.Items.Add($"#{channel.Id}（{channel.Name}）");
        }

        if (TalkChannelCombo.Items.Count > 0)
        {
            TalkChannelCombo.SelectedIndex = 0;
        }

        UpdateButtons();
        TalkStatusText.Text = Localizer.T("Talk.Ready");
    }

    /// <summary>依現況語言套用標題、標籤與按鈕文字。</summary>
    private void ApplyI18n()
    {
        Title = Localizer.T("Talk.Title");
        TalkDescriptionText.Text = Localizer.T("Talk.Description");
        TalkChannelLabel.Text = Localizer.T("Talk.Channel");
        TalkAudioLabel.Text = Localizer.T("Talk.AudioFile");
        TalkBrowseButton.Content = Localizer.T("Talk.Browse");
        TalkPreferredLabel.Text = Localizer.T("Talk.PreferredUrl");
        TalkStartButton.Content = Localizer.T("Talk.Start");
        TalkStopButton.Content = Localizer.T("Talk.Stop");
    }

    private void OnBrowseClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Localizer.T("Talk.PickTitle"),
            Filter = Localizer.T("Talk.FileFilter"),
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) == true)
        {
            TalkAudioBox.Text = dialog.FileName;
        }
    }

    private async void OnStartClicked(object sender, RoutedEventArgs e)
    {
        if (_session is not null)
        {
            return;
        }

        var index = TalkChannelCombo.SelectedIndex;
        if (index < 0 || index >= _channels.Count)
        {
            TalkStatusText.Text = Localizer.T("Talk.NoChannel");
            return;
        }

        var channel = _channels[index];
        if (string.IsNullOrWhiteSpace(channel.MainStreamUrl))
        {
            TalkStatusText.Text = Localizer.T("Talk.NoStream");
            return;
        }

        var audio = TalkAudioBox.Text.Trim();
        if (audio.Length == 0)
        {
            TalkStatusText.Text = Localizer.T("Talk.NoAudio");
            return;
        }

        if (!System.IO.File.Exists(audio))
        {
            TalkStatusText.Text = string.Format(
                CultureInfo.InvariantCulture, Localizer.T("Talk.FileMissing"), audio);
            return;
        }

        var preferred = TalkPreferredBox.Text.Trim();
        var request = new TalkRequest(
            channel.Id,
            channel.MainStreamUrl,
            audio,
            preferred.Length == 0 ? null : preferred,
            null);

        try
        {
            _session = _talk.Start(request);
        }
        catch (InvalidOperationException ex)
        {
            // 沒有可用供應商（例如頻道沒有 RTSP 位址卻也沒指定對講位址）：直接把原因顯示出來。
            TalkStatusText.Text = ex.Message;
            return;
        }

        var session = _session;
        _cts = new CancellationTokenSource();
        UpdateButtons();
        TalkStatusText.Text = string.Format(
            CultureInfo.InvariantCulture,
            Localizer.T("Talk.Sending"),
            System.IO.Path.GetFileName(audio));

        try
        {
            var exitCode = await session.WaitAsync(_cts.Token);
            TalkStatusText.Text = exitCode == 0
                ? Localizer.T("Talk.Done")
                : string.Format(CultureInfo.InvariantCulture, Localizer.T("Talk.ExitCode"), exitCode);
        }
        catch (OperationCanceledException)
        {
            TalkStatusText.Text = Localizer.T("Talk.Stopped");
        }
        finally
        {
            _session = null;
            _cts?.Dispose();
            _cts = null;
            UpdateButtons();
        }
    }

    private void OnStopClicked(object sender, RoutedEventArgs e)
    {
        if (_cts is null)
        {
            TalkStatusText.Text = Localizer.T("Talk.Stopped");
            return;
        }

        // 取消等待會讓 WaitAsync 砍掉子行程並擲回 OperationCanceledException（在上面的 catch 收尾）。
        _cts.Cancel();
    }

    /// <summary>關窗時中止進行中的對講，避免留下孤兒 ffmpeg 子行程。</summary>
    protected override void OnClosed(EventArgs e)
    {
        _cts?.Cancel();
        base.OnClosed(e);
    }

    private void UpdateButtons()
    {
        TalkStartButton.IsEnabled = _session is null;
        TalkStopButton.IsEnabled = _session is not null;
    }
}
