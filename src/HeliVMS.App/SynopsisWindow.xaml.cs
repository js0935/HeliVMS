using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using HeliVMS.Alarms;
using HeliVMS.Storage;
using Path = System.IO.Path;

namespace HeliVMS.App;

/// <summary>影片摘要視窗（M63，§5.9）：選頻道與時段，把偵測動態幀拼貼成單張預覽＋manifest。</summary>
public partial class SynopsisWindow : Window
{
    private readonly SqliteStore _store;
    private readonly string _synopsisRoot;
    private readonly AlarmEventRepository _events;

    public SynopsisWindow(SqliteStore store, string dataRoot)
    {
        _store = store;
        _synopsisRoot = Path.Combine(dataRoot, "synopsis");
        _events = new AlarmEventRepository(store);
        InitializeComponent();
        ApplyI18n();

        var channels = new ChannelRepository(store).List();
        SynopsisChannelCombo.ItemsSource = channels
            .Select(c => new ChannelItem(c.Id, string.Format(
                CultureInfo.InvariantCulture, Localizer.T("Synopsis.ChannelItem"), c.Id, c.Name)))
            .ToList();
        SynopsisChannelCombo.DisplayMemberPath = "Label";
        SynopsisChannelCombo.SelectedIndex = channels.Count > 0 ? 0 : -1;

        var now = DateTime.Now;
        SynopsisFromBox.Text = now.AddHours(-24).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        SynopsisToBox.Text = now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    private sealed record ChannelItem(int Id, string Label);

    /// <summary>依現況語言套用標題、欄位與按鈕文字（M57）。</summary>
    private void ApplyI18n()
    {
        Title = Localizer.T("Synopsis.Title");
        SynopsisHeadingText.Text = Localizer.T("Synopsis.Heading");
        SynopsisChannelLabel.Text = Localizer.T("Synopsis.Channel");
        SynopsisFromLabel.Text = Localizer.T("Synopsis.From");
        SynopsisToLabel.Text = Localizer.T("Synopsis.To");
        SynopsisBuildButton.Content = Localizer.T("Synopsis.Build");
        SynopsisBuildButton.ToolTip = Localizer.T("Synopsis.BuildTip");
        SynopsisTimeHintText.Text = Localizer.T("Synopsis.TimeHint");
    }

    private void OnBuildClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            if (SynopsisChannelCombo.SelectedItem is not ChannelItem channel)
            {
                SynopsisStatusText.Text = Localizer.T("Synopsis.NoChannel");
                return;
            }

            if (!TryParseTime(SynopsisFromBox.Text, out var fromLocal) ||
                !TryParseTime(SynopsisToBox.Text, out var toLocal))
            {
                SynopsisStatusText.Text = Localizer.T("Synopsis.BadTime");
                return;
            }

            var fromUtc = fromLocal.ToUniversalTime();
            var toUtc = toLocal.ToUniversalTime();
            if (toUtc < fromUtc)
            {
                SynopsisStatusText.Text = Localizer.T("Synopsis.ToBeforeFrom");
                return;
            }

            var events = _events.ListByQuery(new AlarmEventRepository.QueryArgs
            {
                ChannelId = channel.Id,
                FromUtc = fromUtc,
                ToUtc = toUtc,
                Limit = 2000,
            });

            var result = SynopsisBuilder.Build(
                new SynopsisRequest(channel.Id, fromUtc, toUtc),
                events,
                _synopsisRoot);

            if (result is null)
            {
                SynopsisSummaryText.Text = Localizer.T("Synopsis.NoEvents");
                SynopsisImage.Source = null;
                SynopsisStatusText.Text = Localizer.T("Synopsis.NotBuilt");
                return;
            }

            SynopsisSummaryText.Text = result.SummaryText;
            SynopsisImage.Source = new BitmapImage(new Uri(result.SheetPath, UriKind.Absolute))
            {
                CacheOption = BitmapCacheOption.OnLoad,
            };
            SynopsisStatusText.Text = string.Format(
                CultureInfo.InvariantCulture,
                Localizer.T("Synopsis.Built"), result.SheetPath, result.ManifestPath);
        }
        catch (Exception ex)
        {
            SynopsisStatusText.Text = string.Format(
                CultureInfo.InvariantCulture, Localizer.T("Synopsis.Failed"), ex.Message);
        }
    }

    private static bool TryParseTime(string text, out DateTime local)
        => DateTime.TryParseExact(
            text.Trim(),
            "yyyy-MM-dd HH:mm",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out local);
}