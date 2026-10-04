using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using HeliVMS.Alarms;
using HeliVMS.Storage;

namespace HeliVMS.App;

/// <summary>
/// 音訊感測測試視窗（M79，§5.8 L1）：以合成方波（20000）依 8 塊批次、每批 256ms 進時餵入
/// <see cref="AudioSensorCoordinator"/>，驗證判讀與事件寫入；「結算」收尾、清單顯示各頻道狀態。
/// </summary>
public partial class AudioWindow : Window
{
    private const int BlockSamples = 512;
    private const int ChunkBlocks = 8;
    private const int FeedBlocks = 24;

    private readonly SqliteStore _store;
    private readonly AudioSensorCoordinator _coordinator;
    private readonly Dictionary<int, string> _lastKind = new();
    private readonly Dictionary<int, int> _sessionEvents = new();
    private readonly ObservableCollection<ChannelRow> _rows = new();

    public AudioWindow(SqliteStore store)
    {
        _store = store;
        _coordinator = new AudioSensorCoordinator(_store);
        InitializeComponent();
        ApplyI18n();

        AudioStateList.ItemsSource = _rows;

        foreach (var id in _coordinator.Channels)
        {
            AudioChannelCombo.Items.Add($"#{id}（{ChannelName(id)}）");
        }

        if (AudioChannelCombo.Items.Count > 0)
        {
            AudioChannelCombo.SelectedIndex = 0;
        }

        RefreshViews();
    }

    /// <summary>依現況語言套用說明、標籤、按鈕、工具提示與清單欄位標題。</summary>
    private void ApplyI18n()
    {
        Title = Localizer.T("Audio.Title");
        AudioDescriptionText.Text = Localizer.T("Audio.Description");
        AudioChannelLabel.Text = Localizer.T("Audio.Channel");
        AudioFeedButton.Content = Localizer.T("Audio.Feed");
        AudioFeedButton.ToolTip = Localizer.T("Audio.FeedTip");
        AudioResetButton.Content = Localizer.T("Audio.Reset");
        AudioResetButton.ToolTip = Localizer.T("Audio.ResetTip");
        AudioFlushButton.Content = Localizer.T("Audio.Flush");
        AudioFlushButton.ToolTip = Localizer.T("Audio.FlushTip");

        if (AudioStateList.View is GridView grid && grid.Columns.Count >= 4)
        {
            grid.Columns[0].Header = Localizer.T("Audio.ColChannel");
            grid.Columns[1].Header = Localizer.T("Audio.ColEnabled");
            grid.Columns[2].Header = Localizer.T("Audio.ColLastKind");
            grid.Columns[3].Header = Localizer.T("Audio.ColEvents");
        }
    }

    private sealed record ChannelRow(string ChannelText, string EnabledText, string LastKindText, string EventCountText);

    private void RefreshViews()
    {
        _rows.Clear();
        foreach (var id in _coordinator.Channels)
        {
            _rows.Add(new ChannelRow(
                $"#{id}（{ChannelName(id)}）",
                _coordinator.IsEnabled(id) ? "Y" : "N",
                _lastKind.GetValueOrDefault(id, "-"),
                _sessionEvents.GetValueOrDefault(id, 0).ToString()));
        }
    }

    private string ChannelName(int id)
    {
        var name = new ChannelRepository(_store).Get(id)?.Name;
        return name ?? string.Format(CultureInfo.InvariantCulture, Localizer.T("Audio.ChannelFallback"), id);
    }

    private int SelectedId()
        => AudioChannelCombo.SelectedIndex >= 0 && AudioChannelCombo.SelectedIndex < _coordinator.Channels.Count
            ? _coordinator.Channels[AudioChannelCombo.SelectedIndex]
            : 0;

    private void OnFeedClicked(object sender, RoutedEventArgs e)
    {
        var id = SelectedId();
        if (id == 0)
        {
            AudioStatusText.Text = Localizer.T("Audio.NoChannel");
            return;
        }

        _coordinator.EventInserted += (_, _) =>
        {
            _sessionEvents[id] = _sessionEvents.GetValueOrDefault(id) + 1;
        };

        var utc = DateTime.UtcNow;
        AudioBlockResult? last = null;
        var sent = 0;
        while (sent < FeedBlocks)
        {
            var take = Math.Min(ChunkBlocks, FeedBlocks - sent);
            var results = _coordinator.Feed(id, Square(20000, BlockSamples * take), utc);
            last = results.Count > 0 ? results[^1] : last;
            sent += take;
            utc = utc.AddMilliseconds(take * 32);
        }

        _lastKind[id] = last?.Kind.ToString() ?? "-";
        AudioStatusText.Text = $"fed:{id};blocks={FeedBlocks};last={_lastKind[id]}";
        RefreshViews();
    }

    private void OnResetClicked(object sender, RoutedEventArgs e)
    {
        var id = SelectedId();
        if (id == 0)
        {
            return;
        }

        _coordinator.Reset(id);
        _lastKind[id] = "-";
        AudioStatusText.Text = $"reset:{id}";
        RefreshViews();
    }

    private void OnFlushClicked(object sender, RoutedEventArgs e)
    {
        var id = SelectedId();
        _coordinator.Flush();
        AudioStatusText.Text = $"flushed:{_coordinator.Channels.Count}ch;events={_sessionEvents.GetValueOrDefault(id, 0)}";
        RefreshViews();
    }

    private static short[] Square(short amplitude, int count)
    {
        var samples = new short[count];
        Array.Fill(samples, amplitude);
        return samples;
    }
}