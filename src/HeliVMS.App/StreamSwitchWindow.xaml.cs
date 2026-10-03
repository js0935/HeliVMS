using System.Collections.ObjectModel;
using System.Windows;
using HeliVMS.App.Services;
using HeliVMS.Storage;

namespace HeliVMS.App;

/// <summary>
/// 雙碼流 Smart 切流調整視窗（M77，§15.2）：以 <see cref="StreamSwitcher"/> 就單一頻道
/// 「目前主碼率／收視人數／維持期」與近窗事件（爆量按鈕）評估，採納建議後更新頻道現行串流。
/// </summary>
public partial class StreamSwitchWindow : Window
{
    private const long DefaultBandwidthLimit = 60_000_000;

    private readonly ChannelRepository _channels;
    private readonly StreamPreferenceRepository _prefs;
    private readonly StreamSwitcher _switcher;
    private readonly ChannelManager? _manager;
    private readonly Dictionary<long, long> _bitrate = new();
    private readonly Dictionary<long, int> _viewers = new();
    private readonly Dictionary<long, StreamSwitchDecision> _lastDecision = new();
    private readonly Dictionary<long, int> _eventCount = new();
    private readonly ObservableCollection<ChannelRow> _rows = new();

    public StreamSwitchWindow(SqliteStore store, ChannelManager? manager = null)
    {
        _channels = new ChannelRepository(store);
        _prefs = new StreamPreferenceRepository(store);
        _manager = manager;
        // 有管理器就共用它的切流狀態與實際連線；否則退化成視窗內自建，但至少從資料庫恢復。
        _switcher = manager?.Switcher ?? CreateStandaloneSwitcher();
        InitializeComponent();

        StreamStateList.ItemsSource = _rows;

        foreach (var c in _channels.List())
        {
            StreamChannelCombo.Items.Add($"#{c.Id}（{c.Name}）");
            _bitrate[c.Id] = 10_000_000;
            _viewers[c.Id] = 2;
        }

        if (StreamChannelCombo.Items.Count > 0)
        {
            StreamChannelCombo.SelectedIndex = 0;
        }

        RefreshViews();
    }

    private StreamSwitcher CreateStandaloneSwitcher()
    {
        var switcher = new StreamSwitcher(mainBandwidthLimitBytesPerSec: DefaultBandwidthLimit);
        foreach (var c in _channels.List())
        {
            switcher.Prime(c.Id, _prefs.GetKind(c.Id), _prefs.GetLastSwitchUtc(c.Id));
        }

        return switcher;
    }

    private sealed record ChannelRow(long Id, string ChannelText, string CurrentText, string LastSwitchText, string DecisionText);

    private long SelectedId()
    {
        var idx = StreamChannelCombo.SelectedIndex;
        if (idx < 0)
        {
            return 0;
        }

        var list = _channels.List();
        return list[idx].Id;
    }

    private void RefreshViews()
    {
        _rows.Clear();
        foreach (var c in _channels.List())
        {
            var current = _switcher.GetCurrent(c.Id);
            var last = _switcher.GetLastSwitch(c.Id);
            var decision = _lastDecision.GetValueOrDefault(c.Id);
            var currentText = current == StreamKind.Main
                ? "Main"
                : string.IsNullOrWhiteSpace(c.SubStreamUrl) ? "Sub（無次碼流，實際用 Main）" : "Sub";
            _rows.Add(new ChannelRow(
                c.Id,
                $"#{c.Id}（{c.Name}）",
                currentText,
                last is null ? "-" : last.Value.ToLocalTime().ToString("HH:mm:ss"),
                decision?.NoChange == true ? "hold" : decision is null ? "-" : $"->{(decision.Target == StreamKind.Main ? "Main" : "Sub")}（{decision.Reason}）"));
        }
    }

    private void OnBurstClicked(object sender, RoutedEventArgs e)
    {
        var id = SelectedId();
        if (id == 0)
        {
            StreamDecisionText.Text = "請先選擇頻道。";
            return;
        }

        _eventCount[id] = _eventCount.GetValueOrDefault(id) + 1;
        var now = DateTime.UtcNow;
        var decision = _switcher.Evaluate(id, _bitrate[id], new[] { ("ai_intrusion", now) }, _viewers[id], now);
        _lastDecision[id] = decision;
        StreamDecisionText.Text = $"已注入事件（次數 {_eventCount[id]}）。建議：{Describe(decision)}";
        RefreshViews();
    }

    private void OnEvalClicked(object sender, RoutedEventArgs e)
    {
        var id = SelectedId();
        if (id == 0)
        {
            StreamDecisionText.Text = "請先選擇頻道。";
            return;
        }

        _bitrate[id] = long.TryParse(StreamBitrateText.Text, out var b) && b >= 0 ? b : 10_000_000;
        _viewers[id] = int.TryParse(StreamViewersText.Text, out var v) && v >= 0 ? v : 0;

        var now = DateTime.UtcNow;
        _lastDecision[id] = _switcher.Evaluate(id, _bitrate[id], Array.Empty<(string, DateTime)>(), _viewers[id], now);
        StreamDecisionText.Text = $"評估（主碼率 {_bitrate[id]}、收視 {_viewers[id]}）。建議：{Describe(_lastDecision[id])}";
        RefreshViews();
    }

    private async void OnApplyClicked(object sender, RoutedEventArgs e)
    {
        var id = SelectedId();
        if (id == 0 || !_lastDecision.TryGetValue(id, out var decision) || decision.NoChange)
        {
            StreamDecisionText.Text = "無可套用之建議（先「評估」且非維持）。";
            return;
        }

        if (decision.Target == StreamKind.Sub && string.IsNullOrWhiteSpace(_channels.Get((int)id)?.SubStreamUrl))
        {
            StreamDecisionText.Text = "此頻道未設定次碼流，無法切至 Sub。";
            return;
        }

        try
        {
            if (_manager is not null)
            {
                await _manager.ApplyStreamKindAsync((int)id, decision.Target);
            }
            else
            {
                var now = DateTime.UtcNow;
                _switcher.ApplySwitch(id, decision.Target, now);
                _prefs.Save(id, decision.Target, now);
            }
        }
        catch (Exception ex)
        {
            StreamDecisionText.Text = $"切流失敗：{ex.Message}";
            return;
        }

        _bitrate[id] = decision.Target == StreamKind.Main ? 10_000_000 : 3_000_000;
        _lastDecision.Remove(id);
        StreamDecisionText.Text = $"applied:{id}->{(decision.Target == StreamKind.Main ? "Main" : "Sub")}（{decision.Reason}）";
        RefreshViews();
    }

    private static string Describe(StreamSwitchDecision decision)
        => decision.NoChange ? "hold" : $"->{(decision.Target == StreamKind.Main ? "Main" : "Sub")}（{decision.Reason}）";
}