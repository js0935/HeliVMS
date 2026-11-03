using System.Globalization;
using System.Windows;
using HeliVMS.Recording.Talk;
using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.App;

/// <summary>
/// 雙向對講視窗（M300，§14.1 #11）：選擇頻道與來源（主機麥克風即時喊話，或音訊檔），
/// 經 <see cref="TalkService"/> 啟動對講子行程送到攝影機喇叭，顯示進行中狀態，並可隨時中止。
/// </summary>
/// <remarks>
/// <para>
/// 對講子行程由來源決定何時結束（麥克風模式要按停止才會結束；檔案模式播完即結束），
/// 因此 UI 用「進行中／已結束」兩個狀態表示。停止＝取消 <see cref="TalkSession.WaitAsync"/>，
/// 由它負責砍掉子行程。
/// </para>
/// <para>
/// 麥克風的選擇存進 app_settings（<see cref="TalkSettings.MicrophoneKey"/>）：監看牆右下角的
/// 麥克風按鈕用的是同一台裝置——若兩邊各自記一份，就會出現「在這裡測得好好的、按牆上的
/// 按鈕卻沒聲音」這種最難查的分歧。
/// </para>
/// </remarks>
public partial class TalkWindow : Window
{
    private readonly TalkService _talk = new();
    private readonly SettingsRepository _settings;
    private readonly IReadOnlyList<ChannelInfo> _channels;
    private TalkSession? _session;
    private CancellationTokenSource? _cts;
    private IReadOnlyList<string> _microphones = [];

    public TalkWindow(SqliteStore store)
    {
        InitializeComponent();
        ApplyI18n();

        _settings = new SettingsRepository(store);
        _channels = new ChannelRepository(store).List();
        foreach (var channel in _channels)
        {
            TalkChannelCombo.Items.Add($"#{channel.Id}（{channel.Name}）");
        }

        if (TalkChannelCombo.Items.Count > 0)
        {
            TalkChannelCombo.SelectedIndex = 0;
        }

        // 預設即時喊話（最常用的用法）；上一次選的麥克風帶回來。
        TalkSourceCombo.SelectedIndex = 0;
        ReloadMicrophones(preferred: _settings.Get(TalkSettings.MicrophoneKey));

        UpdateButtons();
        TalkStatusText.Text = Localizer.T("Talk.Ready");
    }

    /// <summary>是否處於「主機麥克風即時喊話」模式。</summary>
    private bool UseMicrophone => TalkSourceCombo.SelectedIndex == 0;

    /// <summary>依現況語言套用標題、標籤與按鈕文字。</summary>
    private void ApplyI18n()
    {
        Title = Localizer.T("Talk.Title");
        TalkDescriptionText.Text = Localizer.T("Talk.Description");
        TalkChannelLabel.Text = Localizer.T("Talk.Channel");
        TalkSourceLabel.Text = Localizer.T("Talk.Source");
        TalkSourceMicrophone.Content = Localizer.T("Talk.SourceMicrophone");
        TalkSourceFile.Content = Localizer.T("Talk.SourceFile");
        TalkMicRefreshButton.Content = Localizer.T("Talk.MicRefresh");
        TalkMicRefreshButton.ToolTip = Localizer.T("Talk.MicRefreshTip");
        TalkAudioLabel.Text = Localizer.T("Talk.AudioFile");
        TalkPreferredLabel.Text = Localizer.T("Talk.PreferredUrl");
        TalkStartButton.Content = Localizer.T("Talk.Start");
        TalkStopButton.Content = Localizer.T("Talk.Stop");
    }

    /// <summary>切換來源：麥克風模式顯示裝置選單，檔案模式顯示檔案選擇。</summary>
    private void OnTalkSourceChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (TalkMicCombo is null || TalkAudioRow is null)
        {
            return;   // InitializeComponent 建樹期間的第一次觸發，控制項還沒齊。
        }

        var mic = UseMicrophone;
        TalkMicCombo.Visibility = mic ? Visibility.Visible : Visibility.Collapsed;
        TalkMicRefreshButton.Visibility = mic ? Visibility.Visible : Visibility.Collapsed;
        TalkAudioRow.Visibility = mic ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>重新列舉主機的錄音裝置（拔插麥克風之後要重列）。</summary>
    private void OnRefreshMicsClicked(object sender, RoutedEventArgs e) =>
        ReloadMicrophones(preferred: TalkMicCombo.SelectedItem as string);

    private void ReloadMicrophones(string? preferred)
    {
        TalkStatusText.Text = Localizer.T("Talk.MicListing");
        _microphones = MicrophoneCatalog.List();

        TalkMicCombo.Items.Clear();
        foreach (var device in _microphones)
        {
            TalkMicCombo.Items.Add(device);
        }

        if (TalkMicCombo.Items.Count == 0)
        {
            TalkStatusText.Text = Localizer.T("Talk.NoMicrophone");
            UpdateButtons();
            return;
        }

        // 帶回上一次的選擇；沒選過就用第一台。
        var index = preferred is null ? -1 : TalkMicCombo.Items.IndexOf(preferred);
        TalkMicCombo.SelectedIndex = index >= 0 ? index : 0;
        TalkStatusText.Text = Localizer.T("Talk.Ready");
        UpdateButtons();
    }

    private void OnMicSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_settings is null || TalkMicCombo.SelectedItem is not { } device)
        {
            return;
        }

        // 存下來：監看牆右下角的麥克風按鈕用的是同一台裝置。
        _settings.Set(TalkSettings.MicrophoneKey, device.ToString()!);
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

        string? microphone = null;
        if (UseMicrophone)
        {
            microphone = TalkMicCombo.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(microphone))
            {
                TalkStatusText.Text = Localizer.T("Talk.NoMicrophone");
                return;
            }
        }

        var audio = TalkAudioBox.Text.Trim();
        if (!UseMicrophone)
        {
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
        }

        var preferred = TalkPreferredBox.Text.Trim();
        var request = new TalkRequest(
            channel.Id,
            channel.MainStreamUrl,
            audio,
            preferred.Length == 0 ? null : preferred,
            null,
            microphone);

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
        TalkStatusText.Text = microphone is not null
            ? string.Format(CultureInfo.InvariantCulture, Localizer.T("Talk.SendingMic"), microphone)
            : string.Format(CultureInfo.InvariantCulture, Localizer.T("Talk.Sending"), System.IO.Path.GetFileName(audio));

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
        var usable = _session is null
            && (UseMicrophone ? TalkMicCombo.Items.Count > 0 : true);
        TalkStartButton.IsEnabled = usable;
        TalkStopButton.IsEnabled = _session is not null;
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();

    private void OnWindowKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            Close();
        }
    }
}
