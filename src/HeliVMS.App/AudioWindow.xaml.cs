using System.Collections.ObjectModel;
using System.Windows;
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
        => new ChannelRepository(_store).Get(id)?.Name ?? $"頻道{id}";

    private int SelectedId()
        => AudioChannelCombo.SelectedIndex >= 0 && AudioChannelCombo.SelectedIndex < _coordinator.Channels.Count
            ? _coordinator.Channels[AudioChannelCombo.SelectedIndex]
            : 0;

    private void OnFeedClicked(object sender, RoutedEventArgs e)
    {
        var id = SelectedId();
        if (id == 0)
        {
            AudioStatusText.Text = "無可用頻道（需 audio_enabled=1）。";
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