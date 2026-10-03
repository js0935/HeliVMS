using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using HeliVMS.Storage;

namespace HeliVMS.App;

/// <summary>
/// 稽核日誌瀏覽視窗（M109，§14.1）：依類別／操作者／動作／時間範圍查詢「誰、何時、做了什麼」，可匯出 CSV。
///
/// <para>
/// 稽核日誌是資安治理的證據，桌面端原本完全沒有瀏覽入口（只有寫入面由裝置異動帶入）。
/// 匯出與 WebApi 的 <c>/api/audit/export.csv</c> 共用 <see cref="AuditLogCsv"/>，兩份輸出逐位元相同，
/// 才能互相對帳；匯出時套用目前篩選但不套用畫面的筆數上限，避免只匯出看得到的那一頁。
/// </para>
/// </summary>
public partial class AuditLogWindow : Window
{
    private readonly string _dataRoot;
    private readonly AuditLogRepository _audit;
    private IReadOnlyList<AuditLogEntry> _rows = [];

    /// <summary>類別選單：第一項「全部」對應 null，其餘為 <see cref="AuditCategories"/> 的常數值。</summary>
    private static readonly (string LabelKey, string? Value)[] Categories =
    [
        ("Audit.CatAll", null),
        ("Audit.CatAuth", AuditCategories.Auth),
        ("Audit.CatConfig", AuditCategories.Config),
        ("Audit.CatExport", AuditCategories.Export),
        ("Audit.CatEvidence", AuditCategories.Evidence),
        ("Audit.CatShare", AuditCategories.Share),
        ("Audit.CatRetention", AuditCategories.Retention),
        ("Audit.CatLegalHold", AuditCategories.LegalHold),
        ("Audit.CatLicense", AuditCategories.License),
    ];

    /// <summary>稽核清單顯示列（時間已轉本地時區）。</summary>
    private sealed record AuditRow(string Time, string Category, string Action, string Actor, string Target, string Detail);

    public AuditLogWindow(SqliteStore store, string dataRoot)
    {
        _dataRoot = dataRoot;
        _audit = new AuditLogRepository(store);
        InitializeComponent();
        ApplyI18n();

        AuditCategoryCombo.ItemsSource = Categories
            .Select(c => new ComboBoxItem { Content = Localizer.T(c.LabelKey), Tag = c.Value })
            .ToArray();
        AuditCategoryCombo.SelectedIndex = 0;
        AuditLimitCombo.ItemsSource = new[] { 100, 200, 500 };
        AuditLimitCombo.SelectedIndex = 0;

        Query();
    }

    /// <summary>依現況語言套用標題、篩選標籤與欄位文字。</summary>
    private void ApplyI18n()
    {
        Title = Localizer.T("Audit.Title");
        AuditHeadingText.Text = Localizer.T("Audit.Heading");
        AuditCategoryLabel.Text = Localizer.T("Audit.Category");
        AuditActorLabel.Text = Localizer.T("Audit.Actor");
        AuditActorBox.ToolTip = Localizer.T("Audit.ActorTip");
        AuditActionLabel.Text = Localizer.T("Audit.Action");
        AuditActionBox.ToolTip = Localizer.T("Audit.ActionTip");
        AuditFromLabel.Text = Localizer.T("Audit.From");
        AuditToLabel.Text = Localizer.T("Audit.To");
        AuditLimitLabel.Text = Localizer.T("Audit.Limit");
        AuditQueryButton.Content = Localizer.T("Audit.Query");
        AuditQueryButton.ToolTip = Localizer.T("Audit.QueryTip");
        AuditExportButton.Content = Localizer.T("Audit.Export");
        AuditExportButton.ToolTip = Localizer.T("Audit.ExportTip");

        if (AuditList.View is GridView grid && grid.Columns.Count >= 6)
        {
            grid.Columns[0].Header = Localizer.T("Audit.ColTime");
            grid.Columns[1].Header = Localizer.T("Audit.ColCategory");
            grid.Columns[2].Header = Localizer.T("Audit.ColAction");
            grid.Columns[3].Header = Localizer.T("Audit.ColActor");
            grid.Columns[4].Header = Localizer.T("Audit.ColTarget");
            grid.Columns[5].Header = Localizer.T("Audit.ColDetail");
        }
    }

    private void OnQueryClicked(object sender, RoutedEventArgs e) => Query();

    private void Query()
    {
        try
        {
            var from = FromUtc();
            var to = ToUtc();
            if (from is { } f && to is { } t && f >= t)
            {
                AuditStatusText.Text = Localizer.T("Audit.RangeInvalid");
                return;
            }

            var limit = AuditLimitCombo.SelectedItem is int n ? n : 100;
            var q = BuildQuery(limit);

            _rows = _audit.List(q);
            var total = _audit.Count(q);
            AuditList.ItemsSource = _rows.Select(ToRow).ToList();
            AuditStatusText.Text = string.Format(
                CultureInfo.InvariantCulture, Localizer.T("Audit.Shown"), _rows.Count, total);
        }
        catch (Exception ex)
        {
            AuditStatusText.Text = string.Format(
                CultureInfo.InvariantCulture, Localizer.T("Audit.QueryFailed"), ex.Message);
        }
    }

    private void OnExportClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var from = FromUtc();
            var to = ToUtc();
            if (from is { } f && to is { } t && f >= t)
            {
                AuditStatusText.Text = Localizer.T("Audit.RangeInvalid");
                return;
            }

            // 匯出套用同一組篩選，但不受畫面筆數上限限制（稽核證據不該只截到第一頁）。
            var rows = _audit.List(BuildQuery(limit: 0));
            if (rows.Count == 0)
            {
                AuditStatusText.Text = Localizer.T("Audit.NoData");
                return;
            }

            var dir = Path.Combine(_dataRoot, "audit");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"audit-{DateTime.UtcNow:yyyyMMddHHmmss}.csv");
            File.WriteAllText(path, AuditLogCsv.Render(rows), new UTF8Encoding(true)); // UTF-8 BOM（Excel 相容）

            AuditStatusText.Text = string.Format(
                CultureInfo.InvariantCulture, Localizer.T("Audit.Exported"), rows.Count, path);
        }
        catch (Exception ex)
        {
            AuditStatusText.Text = string.Format(
                CultureInfo.InvariantCulture, Localizer.T("Audit.ExportFailed"), ex.Message);
        }
    }

    private AuditLogQuery BuildQuery(int limit) => new()
    {
        Category = SelectedCategory(),
        Actor = Trimmed(AuditActorBox),
        Action = Trimmed(AuditActionBox),
        FromUtc = FromUtc(),
        ToUtc = ToUtc(),
        Limit = limit,
    };

    private static AuditRow ToRow(AuditLogEntry e) => new(
        e.OccurredAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        e.Category,
        e.Action,
        e.Actor,
        e.TargetType is { Length: > 0 } tt
            ? e.TargetId is { } id ? $"{tt}#{id}" : tt
            : string.Empty,
        e.Detail ?? string.Empty);

    private string? SelectedCategory() =>
        AuditCategoryCombo.SelectedItem is ComboBoxItem { Tag: string value } && value.Length > 0 ? value : null;

    private static string? Trimmed(TextBox box) =>
        box.Text.Trim().Length > 0 ? box.Text.Trim() : null;

    private DateTime? FromUtc() =>
        AuditFromDate.SelectedDate is { } d
            ? DateTime.SpecifyKind(d.Date, DateTimeKind.Local).ToUniversalTime()
            : null;

    /// <summary>「迄」含當日：換算為隔日 00:00 的 UTC 右開邊界（查詢為左閉右開）。</summary>
    private DateTime? ToUtc() =>
        AuditToDate.SelectedDate is { } d
            ? DateTime.SpecifyKind(d.Date.AddDays(1), DateTimeKind.Local).ToUniversalTime()
            : null;
}
