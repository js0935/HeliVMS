using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HeliVMS.Media;
using HeliVMS.Recording;
using HeliVMS.Shared;
using HeliVMS.Storage;

namespace HeliVMS.App;

/// <summary>
/// 以圖搜圖（QCV8001 <c>QCJpegSearch</c> 對應功能）：查詢影像 × 當日錄影 → 相似時刻清單。
/// </summary>
/// <remarks>
/// <para>
/// 查詢指紋在背景執行緒抽取（ffmpeg 開檔、seek 要數百毫秒），掃描也一樣；UI 執行緒
/// 只做最後的結果綁定——一顆 ffmpeg 抽幀 + 逐段抽樣若跑在 UI 執行緒上，
/// 視窗會整場凍結（症狀：按了「開始搜尋」像當機）。
/// </para>
/// <para>
/// 關窗即取消（<see cref="CancellationTokenSource"/> 一路傳進抽樣器，砍行程樹）：
/// 否則孤兒 ffmpeg 會繼續讀磁碟到逾時——與整份產品的收尾原則同款。
/// </para>
/// <para>
/// 搜尋範圍限「開啟時那一日」：跳轉走母視窗 <c>ResumePlaybackAt</c>，而它的落點
/// 規則只認當日已載入的段——跨日跳轉需要換日重載，屬於另一個操作。
/// </para>
/// </remarks>
public partial class ImageSearchWindow : Window
{
    private readonly SegmentRepository _segments;
    private readonly int _channelId;
    private readonly string _channelName;
    private readonly DateTime _day;
    private readonly string? _currentFilePath;
    private readonly double _currentPosition;
    private readonly Action<DateTime> _onJump;
    private CancellationTokenSource? _cts;
    private string? _pickedFilePath;

    public ImageSearchWindow(
        SegmentRepository segments,
        int channelId,
        string channelName,
        DateTime day,
        string? currentFilePath,
        double currentPosition,
        Action<DateTime> onJump)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(channelName);
        ArgumentNullException.ThrowIfNull(onJump);
        _segments = segments;
        _channelId = channelId;
        _channelName = channelName;
        _day = day.Date;
        _currentFilePath = currentFilePath;
        _currentPosition = currentPosition;
        _onJump = onJump;

        InitializeComponent();
        ApplyI18n();

        // 沒有播放中的畫格就沒有「以當前畫格」——直接切到檔案模式並停用該選項，
        // 不讓操作員點了一個註定失敗的來源。
        if (_currentFilePath is null)
        {
            SourceCurrentRadio.IsChecked = false;
            SourceCurrentRadio.IsEnabled = false;
            SourceFileRadio.IsChecked = true;
        }

        SensitivityCombo.SelectedIndex = 1; // 標準
        IntervalCombo.SelectedIndex = 1;    // 15 秒
    }

    private void ApplyI18n()
    {
        Title = Localizer.T("ImageSearch.Title");
        HeaderTitle.Text = string.Format(Localizer.T("ImageSearch.HeaderTitle"), _channelName);
        HeaderCloseButton.ToolTip = Localizer.T("ImageSearch.Close");
        SourceHeader.Text = Localizer.T("ImageSearch.SourceHeader");
        SourceCurrentRadio.Content = Localizer.T("ImageSearch.SourceCurrent");
        SourceFileRadio.Content = Localizer.T("ImageSearch.SourceFile");
        PickFileButton.Content = Localizer.T("ImageSearch.PickFile");
        PickedFileText.Text = _pickedFilePath is null ? string.Empty : Path.GetFileName(_pickedFilePath);
        RangeText.Text = string.Format(Localizer.T("ImageSearch.RangeLabel"), _day.ToString("yyyy-MM-dd"));
        SensitivityLabel.Text = Localizer.T("ImageSearch.Sensitivity");
        SetComboItems(SensitivityCombo, "ImageSearch.Strict", "ImageSearch.Standard", "ImageSearch.Loose");
        IntervalLabel.Text = Localizer.T("ImageSearch.Interval");
        SetComboItems(IntervalCombo, "ImageSearch.Interval5", "ImageSearch.Interval15", "ImageSearch.Interval30", "ImageSearch.Interval60");
        SearchButton.Content = Localizer.T("ImageSearch.Search");
        CancelButton.Content = Localizer.T("ImageSearch.Cancel");
        TimeCol.Header = Localizer.T("ImageSearch.ColTime");
        DistanceCol.Header = Localizer.T("ImageSearch.ColDistance");
        JumpButton.Content = Localizer.T("ImageSearch.Jump");
        FooterHint.Text = Localizer.T("ImageSearch.FooterHint");
        HintText.Text = Localizer.T("ImageSearch.Hint");
        StatusText.Text = Localizer.T("ImageSearch.Idle");
    }

    private static void SetComboItems(ComboBox combo, params string[] keys)
    {
        for (var i = 0; i < keys.Length && i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item)
            {
                item.Content = Localizer.T(keys[i]);
            }
        }
    }

    /// <summary>切換查詢來源：只有檔案模式需要「選擇檔案」；同時清掉上一輪結果——
    /// 不同來源的命中混在同一張清單裡，會被誤讀成新查詢的結果。</summary>
    private void OnSourceChanged(object sender, RoutedEventArgs e)
    {
        PickFileButton.IsEnabled = SourceFileRadio.IsChecked == true;
        ResultList.ItemsSource = null;
        JumpButton.IsEnabled = false;
        StatusText.Text = Localizer.T("ImageSearch.Idle");
    }

    private void OnPickFileClicked(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = Localizer.T("ImageSearch.FileFilter"),
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) == true)
        {
            _pickedFilePath = dlg.FileName;
            PickedFileText.Text = Path.GetFileName(dlg.FileName);
            SourceFileRadio.IsChecked = true;
        }
    }

    private void OnSearchClicked(object sender, RoutedEventArgs e)
    {
        if (_cts is not null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        SearchButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        JumpButton.IsEnabled = false;
        ResultList.ItemsSource = null;
        StatusText.Text = Localizer.T("ImageSearch.Searching");

        var intervalSeconds = IntervalSeconds();
        var threshold = Threshold();
        var day = _day;
        var fromUtc = TimeZoneInfo.ConvertTimeToUtc(day);
        var toUtc = TimeZoneInfo.ConvertTimeToUtc(day.AddDays(1));
        var useCurrent = SourceCurrentRadio.IsChecked == true;
        var filePath = useCurrent ? _currentFilePath : _pickedFilePath;
        var position = Math.Max(0, _currentPosition);

        // 來源選擇在 UI 執行緒先讀好再帶進背景——WPF 控制項屬性不能從工作執行緒讀。
        _ = Task.Run(
            () => RunSearch(useCurrent, filePath, position, fromUtc, toUtc, intervalSeconds, threshold, token),
            token)
            .ContinueWith(
                t => Dispatcher.BeginInvoke(new Action(() => OnSearchCompleted(t))),
                TaskScheduler.Default);
    }

    private IReadOnlyList<FrameSimilarityHit> RunSearch(
        bool useCurrent,
        string? filePath,
        double position,
        DateTime fromUtc,
        DateTime toUtc,
        double intervalSeconds,
        int threshold,
        CancellationToken token)
    {
        if (filePath is null)
        {
            throw new InvalidOperationException(Localizer.T("ImageSearch.NoQuery"));
        }

        ulong queryHash;
        if (useCurrent)
        {
            // 當前畫格：用與掃描端同一條管線（ffmpeg 對同一檔案 seek 到當前位置抽一幀）——
            // 直接從 WPF 的 bitmap 轉像素會多一套縮放實作，遲早與掃描端不一致。
            var samples = new List<GraySample>();
            FrameSampleExtractor.ExtractSamples(
                filePath,
                TimeSpan.FromSeconds(position),
                span: TimeSpan.FromSeconds(0.6),
                intervalSeconds: 0.5,
                maxSamples: 1,
                samples.Add,
                cancellationToken: token);
            if (samples.Count == 0)
            {
                throw new InvalidOperationException(Localizer.T("ImageSearch.NoQuery"));
            }

            queryHash = PerceptualHash.FromGrayImage(
                samples[0].Gray32x32, FrameSampleExtractor.SampleSize, FrameSampleExtractor.SampleSize);
        }
        else
        {
            queryHash = FrameSimilarityScanner.HashImage(filePath, cancellationToken: token);
        }

        var scanner = new FrameSimilarityScanner(_segments);
        return scanner.Scan(
            new FrameSimilarityQuery(_channelId, "main", fromUtc, toUtc, queryHash)
            {
                Options = new FrameSimilarityOptions
                {
                    SampleInterval = TimeSpan.FromSeconds(intervalSeconds),
                    MaxHammingDistance = threshold,
                },
            },
            token);
    }

    private void OnSearchCompleted(Task<IReadOnlyList<FrameSimilarityHit>> task)
    {
        _cts?.Dispose();
        _cts = null;
        SearchButton.IsEnabled = true;
        CancelButton.IsEnabled = false;

        if (task.IsCanceled)
        {
            StatusText.Text = Localizer.T("ImageSearch.Cancelled");
            return;
        }

        if (task.IsFaulted)
        {
            StatusText.Text = string.Format(
                Localizer.T("ImageSearch.Failed"),
                task.Exception?.GetBaseException().Message ?? string.Empty);
            return;
        }

        var hits = task.Result;
        ResultList.ItemsSource = hits
            .Select(h => new HitRow(
                h,
                PlaybackClock.FormatInstant(h.OccurredAtUtc.ToLocalTime()),
                string.Format(Localizer.T("ImageSearch.DistanceFormat"), h.Distance, PerceptualHash.Bits)))
            .ToList();
        StatusText.Text = string.Format(Localizer.T("ImageSearch.Done"), hits.Count);
        JumpButton.IsEnabled = hits.Count > 0;
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void OnResultDoubleClicked(object sender, MouseButtonEventArgs e) => JumpToSelected();

    private void OnJumpClicked(object sender, RoutedEventArgs e) => JumpToSelected();

    private void JumpToSelected()
    {
        if (ResultList.SelectedItem is not HitRow row)
        {
            return;
        }

        _onJump(row.Hit.OccurredAtUtc.ToLocalTime());
        Close();
    }

    private double IntervalSeconds() => IntervalCombo.SelectedIndex switch
    {
        0 => 5,
        2 => 30,
        3 => 60,
        _ => 15,
    };

    private int Threshold() => SensitivityCombo.SelectedIndex switch
    {
        0 => 6,
        2 => 14,
        _ => 10,
    };

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // 關窗即取消：抽樣器收到 token 會砍掉 ffmpeg 行程樹（不留下讀磁碟的孤兒）。
        _cts?.Cancel();
    }

    private sealed record HitRow(FrameSimilarityHit Hit, string TimeText, string DistanceText);
}
