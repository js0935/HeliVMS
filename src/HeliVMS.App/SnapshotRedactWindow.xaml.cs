using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using HeliVMS.Storage;

namespace HeliVMS.App;

/// <summary>
/// 快照一鍵遮蔽（M116，§14.7 #5）：把已記錄在 <see cref="RedactionRepository"/> 的
/// <see cref="RedactionSources.Snapshot"/> 區域燒錄進事件快照，另存為 -redacted 檔。
/// 先前 <see cref="SnapshotRedactionService"/> 全 repo 沒有任何呼叫端。
/// </summary>
public partial class SnapshotRedactWindow : Window
{
    private readonly SqliteStore _store;
    private readonly RedactionRepository _redactions;
    private readonly string _snapshotPath;
    private readonly long _eventId;
    private readonly int _channelId;
    private readonly DateTime _occurredAtUtc;
    private List<RedactionRegion> _regions = [];

    private sealed record RegionRow(RedactionRegion Region, string Label);

    public SnapshotRedactWindow(
        SqliteStore store, string snapshotPath, long eventId, int channelId, DateTime occurredAtUtc)
    {
        _store = store;
        _redactions = new RedactionRepository(store);
        _snapshotPath = snapshotPath;
        _eventId = eventId;
        _channelId = channelId;
        _occurredAtUtc = occurredAtUtc;
        InitializeComponent();
        ApplyI18n();
        LoadPreview(snapshotPath);
        ReloadRegions();
    }

    /// <summary>依現況語言套用標題、欄位與按鈕文字（M57）。事件編號為動態，故於此組字。</summary>
    private void ApplyI18n()
    {
        Title = string.Format(CultureInfo.InvariantCulture, Localizer.T("SnapRedact.Title"), _eventId);
        SnapRedactHint.Text = Localizer.T("SnapRedact.Loading");
        SnapRedactHeadingText.Text = Localizer.T("SnapRedact.Heading");
        RegionWidthLabel.Text = Localizer.T("SnapRedact.Width");
        RegionHeightLabel.Text = Localizer.T("SnapRedact.Height");
        AddRegionButton.Content = Localizer.T("SnapRedact.Add");
        RemoveRegionButton.Content = Localizer.T("SnapRedact.Remove");
        ApplyRedactButton.Content = Localizer.T("SnapRedact.Apply");
        ApplyRedactButton.ToolTip = Localizer.T("SnapRedact.ApplyTip");
    }

    private void LoadPreview(string path)
    {
        if (!File.Exists(path))
        {
            SnapRedactHint.Text = Localizer.T("SnapRedact.Gone");
            return;
        }

        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            SnapPreview.Source = bmp;
            SnapRedactHint.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or System.Runtime.InteropServices.COMException)
        {
            SnapRedactHint.Text = string.Format(
                CultureInfo.InvariantCulture, Localizer.T("SnapRedact.OpenFailed"), ex.Message);
            SnapRedactHint.Visibility = Visibility.Visible;
        }
    }

    private void ReloadRegions()
    {
        _regions = _redactions.QueryBySource(RedactionSources.Snapshot, _eventId).ToList();
        RegionList.ItemsSource = _regions
            .Select(r => new RegionRow(r, string.Format(
                CultureInfo.InvariantCulture,
                Localizer.T("SnapRedact.RegionItem"),
                r.X,
                r.Y,
                r.Width,
                r.Height,
                Localizer.T(r.Filled ? "SnapRedact.Filled" : "SnapRedact.Blur"))))
            .ToList();
        RegionList.DisplayMemberPath = nameof(RegionRow.Label);
    }

    private void OnAddRegionClicked(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(RegionX.Text, out var x) || !int.TryParse(RegionY.Text, out var y) ||
            !int.TryParse(RegionW.Text, out var w) || !int.TryParse(RegionH.Text, out var h) ||
            w <= 0 || h <= 0)
        {
            SnapRedactStatusText.Text = Localizer.T("SnapRedact.NeedPositive");
            return;
        }

        _redactions.Add(
            RedactionSources.Snapshot, _eventId, _channelId, _occurredAtUtc,
            x, y, w, h, filled: true, DateTime.UtcNow);
        ReloadRegions();
        SnapRedactStatusText.Text = string.Format(
            CultureInfo.InvariantCulture, Localizer.T("SnapRedact.Added"), x, y, w, h);
    }

    private void OnRemoveRegionClicked(object sender, RoutedEventArgs e)
    {
        // DisplayMemberPath 之下 SelectedItem 仍是 RegionRow。
        if (RegionList.SelectedItem is not RegionRow row)
        {
            SnapRedactStatusText.Text = Localizer.T("SnapRedact.PickToRemove");
            return;
        }

        _redactions.Remove(row.Region.Id);
        ReloadRegions();
        SnapRedactStatusText.Text = Localizer.T("SnapRedact.Removed");
    }

    private void OnApplyRedactClicked(object sender, RoutedEventArgs e)
    {
        if (_regions.Count == 0)
        {
            SnapRedactStatusText.Text = Localizer.T("SnapRedact.NoRegions");
            return;
        }

        if (!File.Exists(_snapshotPath))
        {
            SnapRedactStatusText.Text = Localizer.T("SnapRedact.GoneApply");
            return;
        }

        try
        {
            var bytes = File.ReadAllBytes(_snapshotPath);
            var result = new SnapshotRedactionService(_store).Redact(bytes, _eventId);

            var dir = Path.Combine(Path.GetDirectoryName(_snapshotPath) ?? ".", "redacted");
            Directory.CreateDirectory(dir);
            var ext = Path.GetExtension(_snapshotPath);
            if (string.IsNullOrEmpty(ext))
            {
                ext = ".jpg";
            }

            var outputPath = Path.Combine(dir, Path.GetFileNameWithoutExtension(_snapshotPath) + "-redacted" + ext);
            File.WriteAllBytes(outputPath, result.Image);
            ShowBitmap(result.Image);

            SnapRedactHint.Visibility = Visibility.Collapsed;
            SnapRedactStatusText.Text = string.Format(
                CultureInfo.InvariantCulture,
                Localizer.T("SnapRedact.Applied"),
                result.AppliedRegions,
                _regions.Count,
                outputPath);
        }
        catch (Exception ex)
        {
            SnapRedactStatusText.Text = string.Format(
                CultureInfo.InvariantCulture, Localizer.T("SnapRedact.Failed"), ex.Message);
        }
    }

    private void ShowBitmap(byte[] image)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = new MemoryStream(image);
        bmp.EndInit();
        bmp.Freeze();
        SnapPreview.Source = bmp;
    }
}
