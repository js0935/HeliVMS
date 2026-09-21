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

        var channels = new ChannelRepository(store).List();
        SynopsisChannelCombo.ItemsSource = channels.Select(c => new ChannelItem(c.Id, $"頻道 {c.Id}（{c.Name}）")).ToList();
        SynopsisChannelCombo.DisplayMemberPath = "Label";
        SynopsisChannelCombo.SelectedIndex = channels.Count > 0 ? 0 : -1;

        var now = DateTime.Now;
        SynopsisFromBox.Text = now.AddHours(-24).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        SynopsisToBox.Text = now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    private sealed record ChannelItem(int Id, string Label);

    private void OnBuildClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            if (SynopsisChannelCombo.SelectedItem is not ChannelItem channel)
            {
                SynopsisStatusText.Text = "尚未選擇頻道。";
                return;
            }

            if (!TryParseTime(SynopsisFromBox.Text, out var fromLocal) ||
                !TryParseTime(SynopsisToBox.Text, out var toLocal))
            {
                SynopsisStatusText.Text = "時間格式應為 yyyy-MM-dd HH:mm。";
                return;
            }

            var fromUtc = fromLocal.ToUniversalTime();
            var toUtc = toLocal.ToUniversalTime();
            if (toUtc < fromUtc)
            {
                SynopsisStatusText.Text = "訖時間早於起時間。";
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
                SynopsisSummaryText.Text = "此時間範圍內沒有可摘要的偵測事件（需要快照）。";
                SynopsisImage.Source = null;
                SynopsisStatusText.Text = "未產生摘要。";
                return;
            }

            SynopsisSummaryText.Text = result.SummaryText;
            SynopsisImage.Source = new BitmapImage(new Uri(result.SheetPath, UriKind.Absolute))
            {
                CacheOption = BitmapCacheOption.OnLoad,
            };
            SynopsisStatusText.Text = $"拼貼：{result.SheetPath}\nmanifest：{result.ManifestPath}";
        }
        catch (Exception ex)
        {
            SynopsisStatusText.Text = $"產生失敗：{ex.Message}";
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