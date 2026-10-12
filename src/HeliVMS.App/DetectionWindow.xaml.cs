using System.Globalization;
using System.Windows;
using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.App;

/// <summary>AI 偵測記錄查詢（M11）：日期/頻道/類別/信心過濾＋統計。</summary>
public partial class DetectionWindow : Window
{
    private static readonly string[] DetectorClasses =
    {
        "person", "car", "motorcycle", "bus", "truck", "dog", "cat",
    };

    private readonly SqliteStore _store;
    private readonly DetectionRepository _repo;
    private readonly Dictionary<int, string> _channelNames = new();
    private string _allLabel = string.Empty;

    public DetectionWindow(SqliteStore store)
    {
        InitializeComponent();
        _store = store;
        _repo = new DetectionRepository(store);
        ApplyI18n();
    }

    /// <summary>
    /// 套用目前語系到偵測記錄視窗的所有靜態文字（M57 §14.7）。XAML 刻意全部留空，
    /// 文字一律經此指派，由 WpfI18nContractTests 的棘輪釘住三語齊備。
    /// </summary>
    private void ApplyI18n()
    {
        Title = Localizer.T("Detection.Title");
        FilterHintText.Text = Localizer.T("Detection.FilterHint");
        ChannelLabel.Text = Localizer.T("Detection.Channel");
        FromLabel.Text = Localizer.T("Detection.From");
        ToLabel.Text = Localizer.T("Detection.To");
        ClassLabel.Text = Localizer.T("Detection.Class");
        MinConfLabel.Text = Localizer.T("Detection.MinConf");
        SearchButton.Content = Localizer.T("Detection.Search");
        SearchButton.ToolTip = Localizer.T("Detection.SearchTip");
        StatsLabelText.Text = Localizer.T("Detection.StatsLabel");
        StatText.Text = Localizer.T("Detection.NotQueried");
        CountText.Text = string.Format(Localizer.T("Detection.Count"), 0);
        ColTime.Header = Localizer.T("Detection.ColTime");
        ColChannel.Header = Localizer.T("Detection.Channel");
        ColClass.Header = Localizer.T("Detection.Class");
        ColConfidence.Header = Localizer.T("Detection.ColConfidence");
        ColBbox.Header = Localizer.T("Detection.ColBbox");
        _allLabel = Localizer.T("Detection.AllItems");
    }

    private sealed record Row(string TimeLabel, string ChannelName, string Class, string ConfidenceLabel, string BboxLabel);

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var now = DateTime.Now;
        FromDateBox.Text = now.ToString("yyyy-MM-dd");
        FromTimeBox.Text = "00:00";
        ToDateBox.Text = now.ToString("yyyy-MM-dd");
        ToTimeBox.Text = now.ToString("HH:mm");

        var items = new List<object> { new ChannelOption(0, _allLabel) };
        foreach (var c in new ChannelRepository(_store).List())
        {
            items.Add(new ChannelOption(c.Id, c.Name));
            _channelNames[c.Id] = c.Name;
        }

        ChannelFilter.ItemsSource = items;
        ChannelFilter.DisplayMemberPath = nameof(ChannelOption.Label);
        ChannelFilter.SelectedIndex = 0;
        var classes = new List<string> { _allLabel };
        classes.AddRange(DetectorClasses);
        ClassFilter.ItemsSource = classes;
        ClassFilter.SelectedIndex = 0;
    }

    private sealed record ChannelOption(int Id, string Label);

    private static bool TryParseLocal(string date, string time, out DateTime local)
    {
        local = DateTime.MinValue;
        if (!DateTime.TryParse(date, CultureInfo.CurrentCulture, DateTimeStyles.None, out var d) ||
            !TimeOnly.TryParse(time.Trim(), out var t))
        {
            return false;
        }

        local = d.Date + t.ToTimeSpan();
        return true;
    }

    private void OnSearchClicked(object sender, RoutedEventArgs e)
    {
        if (!TryParseLocal(FromDateBox.Text, FromTimeBox.Text, out var fromLocal) ||
            !TryParseLocal(ToDateBox.Text, ToTimeBox.Text, out var toLocal))
        {
            StatText.Text = Localizer.T("Detection.BadRange");
            return;
        }

        var args = new DetectionRepository.QueryArgs
        {
            ChannelId = ChannelFilter.SelectedItem is ChannelOption c && c.Id != 0 ? c.Id : null,
            Class = ClassFilter.SelectedItem is string s && s != _allLabel ? s : null,
            FromUtc = DateTime.SpecifyKind(fromLocal, DateTimeKind.Local).ToUniversalTime(),
            ToUtc = DateTime.SpecifyKind(toLocal, DateTimeKind.Local).ToUniversalTime(),
            MinConfidence = ParseConfidence(),
            Limit = 2000,
        };

        var rows = _repo.ListByQuery(args);
        DetailList.ItemsSource = rows.Select(r => new Row(
            r.DetectedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff"),
            _channelNames.TryGetValue(r.ChannelId, out var name) ? name : $"#{r.ChannelId}",
            r.Class,
            $"{r.Confidence:0.000}",
            $"{r.X:0.00}, {r.Y:0.00}, {r.W:0.00}, {r.H:0.00}"))
            .ToList();

        CountText.Text = string.Format(Localizer.T("Detection.Count"), rows.Count);
        var stats = _repo.CountByClass(args);
        StatText.Text = stats.Count == 0
            ? Localizer.T("Detection.NoRecords")
            : string.Join("　·　", stats.Select(x => $"{x.Class} × {x.Count}"));
    }

    private float? ParseConfidence()
    {
        if (string.IsNullOrWhiteSpace(MinConfBox.Text))
        {
            return null;
        }

        return float.TryParse(MinConfBox.Text, CultureInfo.InvariantCulture, out var v)
            ? Math.Clamp(v, 0f, 1f)
            : null;
    }
}