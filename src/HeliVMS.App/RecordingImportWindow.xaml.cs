using System.Windows;
using HeliVMS.Storage;

namespace HeliVMS.App;

/// <summary>匯入畫面的一個頻道選項；<see cref="Id"/> 為 0 代表「略過」。</summary>
public sealed record RecordingImportChannelOption(int Id, string Name);

/// <summary>匯入畫面的一列：磁碟上的一個頻道資料夾 → 目標頻道。</summary>
/// <remarks>
/// 屬性刻意維持可寫且不實作 <c>INotifyPropertyChanged</c>：ComboBox 的
/// <c>SelectedValue</c> 是 TwoWay 繫結，使用者選擇時會直接寫回這裡；
/// 這一列在畫面顯示後不會由程式改動，因此不需要變更通知。
/// </remarks>
public sealed class RecordingImportRow
{
    public string FolderPath { get; init; } = string.Empty;

    public string FolderName { get; init; } = string.Empty;

    public string FilesText { get; init; } = string.Empty;

    public string SizeText { get; init; } = string.Empty;

    public string RangeText { get; init; } = string.Empty;

    public IReadOnlyList<RecordingImportChannelOption> ChannelOptions { get; init; } = [];

    /// <summary>操作員選定的頻道；0 表示略過這個資料夾。</summary>
    public int SelectedChannelId { get; set; }
}

/// <summary>
/// 把磁碟上「索引裡沒有」的錄影補回索引（見 <see cref="RecordingDiskImporter"/>）。
/// </summary>
/// <remarks>
/// <para>
/// 為什麼需要這個畫面：索引被重建、還原成舊備份、頻道刪掉又重加，或整顆錄影碟換機器時，
/// 檔案都還在，回放卻完全空白——而程式沒有任何入口可以把它們補回去，
/// 操作員只能看到一個乾淨的日誌與一個空的回放清單。
/// </para>
/// <para>
/// 對不到頻道的資料夾（例如頻道 72 已刪除、留下 <c>ch072</c>）預設是「略過」而不是猜一個：
/// 把 A 台的錄影掛到 B 台名下，等於讓證據指向錯的攝影機，比看不到更糟。
/// </para>
/// </remarks>
public partial class RecordingImportWindow : Window
{
    private readonly SqliteStore _store;
    private readonly List<RecordingImportRow> _rows;
    private readonly int _totalFiles;

    /// <summary>本次匯入的結果；尚未執行匯入（或匯入失敗）時為 null。</summary>
    public RecordingImportReport? Report { get; private set; }

    public RecordingImportWindow(SqliteStore store, IReadOnlyList<RecordingImportFolder> folders)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));

        InitializeComponent();

        var channels = new ChannelRepository(_store).List();
        var options = new List<RecordingImportChannelOption>
        {
            new(0, Localizer.T("RecordingImport.Skip")),
        };
        options.AddRange(channels.Select(c => new RecordingImportChannelOption(c.Id, c.Name)));

        _rows = folders
            .Select(f => new RecordingImportRow
            {
                FolderPath = f.FolderPath,
                FolderName = f.FolderName,
                FilesText = f.FileCount.ToString("N0"),
                SizeText = FormatBytes(f.SizeBytes),
                RangeText = string.Format(
                    Localizer.T("RecordingImport.Range"),
                    f.FromUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                    f.ToUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")),
                ChannelOptions = options,
                SelectedChannelId = RecordingDiskImporter.DefaultChannelId(f),
            })
            .ToList();
        _totalFiles = folders.Sum(f => f.FileCount);

        FolderList.ItemsSource = _rows;
        ApplyI18n();
    }

    private void ApplyI18n()
    {
        Title = Localizer.T("RecordingImport.Title");
        HeadingText.Text = Localizer.T("RecordingImport.Heading");
        DescriptionText.Text = Localizer.T("RecordingImport.Description");
        ColFolder.Header = Localizer.T("RecordingImport.ColFolder");
        ColFiles.Header = Localizer.T("RecordingImport.ColFiles");
        ColSize.Header = Localizer.T("RecordingImport.ColSize");
        ColRange.Header = Localizer.T("RecordingImport.ColRange");
        ColChannel.Header = Localizer.T("RecordingImport.ColChannel");
        ImportButton.Content = Localizer.T("RecordingImport.Import");
        ImportButton.ToolTip = Localizer.T("RecordingImport.ImportTip");
        CloseButton.Content = Localizer.T("RecordingImport.Close");

        if (Report is null)
        {
            StatusText.Text = string.Format(Localizer.T("RecordingImport.Found"), _rows.Count, _totalFiles);
        }
    }

    private async void OnImportClicked(object sender, RoutedEventArgs e)
    {
        var picked = RecordingDiskImporter.SelectAssignments(
            _rows.Select(r => (r.FolderPath, r.SelectedChannelId)));

        if (picked.Count == 0)
        {
            // 全部選「略過」時直接匯入只會得到一個「0 段」的假成功，不如講清楚。
            StatusText.Text = Localizer.T("RecordingImport.NoSelection");
            return;
        }

        ImportButton.IsEnabled = false;
        StatusText.Text = Localizer.T("RecordingImport.Importing");

        try
        {
            // 幾千段就是幾千次 INSERT，留在 UI 執行緒會讓整個設定中心「沒有回應」。
            var report = await Task.Run(() =>
                new RecordingDiskImporter(new SegmentRepository(_store)).Import(picked));

            Report = report;
            StatusText.Text = string.Format(
                Localizer.T("RecordingImport.Done"),
                report.Imported,
                report.Revived,
                report.Skipped,
                FormatBytes(report.Bytes));
        }
        catch (Exception ex)
        {
            Report = null;
            StatusText.Text = string.Format(Localizer.T("RecordingImport.Failed"), ex.Message);
            ImportButton.IsEnabled = true;
        }
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();

    private static string FormatBytes(long bytes) => bytes >= 1024d * 1024 * 1024
        ? $"{bytes / 1024d / 1024 / 1024:0.#}GB"
        : $"{bytes / 1024d / 1024:0.#}MB";
}
