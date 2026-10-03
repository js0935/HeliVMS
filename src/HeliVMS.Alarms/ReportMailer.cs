using System.Globalization;
using System.IO;
using System.Text;
using HeliVMS.Storage;

namespace HeliVMS.Alarms;

/// <summary>報表排程一次執行結果（供 UI／日誌顯示，不丟例外）。</summary>
public sealed record ReportMailOutcome(bool Sent, string Message, string? Path);

/// <summary>
/// 排程報表寄送（§14.1 #9）：由桌面計時器週期性呼叫 <see cref="RunIfDueAsync"/>，
/// 到達設定時點才產生 CSV 並以既有 <c>notify.smtp.*</c> 寄出。
///
/// <para>
/// 關鍵設計：是否「到期」由 <see cref="ReportSchedule.IsDue"/> 以本地日曆日／週判斷，
/// 並用 <see cref="ReportSchedule.LastSentKey"/> 記錄最後成功寄送時間。計時器可以每幾分鐘
/// 輪詢而不用擔心重複寄送；程式重啟後也會讀回上次寄送時間，不會一重啟就補寄。
/// </para>
/// </summary>
public sealed class ReportMailer
{
    private static readonly TimeOnly FallbackTime = new(8, 0);

    private readonly ReportRepository _reports;
    private readonly SettingsRepository _settings;
    private readonly string _dataRoot;

    public ReportMailer(SqliteStore store, string dataRoot)
    {
        _reports = new ReportRepository(store);
        _settings = new SettingsRepository(store);
        _dataRoot = dataRoot;
    }

    /// <summary>若目前時點符合排程則寄送，否則回傳未寄送與原因／下次時間。</summary>
    public async Task<ReportMailOutcome> RunIfDueAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        var enabledRaw = _settings.GetOrDefault(ReportSchedule.EnabledKey, "0");
        var enabled = enabledRaw == "1" || (bool.TryParse(enabledRaw, out var b) && b);
        var cadence = ReportSchedule.ParseCadence(_settings.Get(ReportSchedule.CadenceKey));
        if (!enabled || cadence == ReportCadence.Off)
        {
            return new ReportMailOutcome(false, "報表排程未啟用", null);
        }

        var at = ReportSchedule.TryParseTime(_settings.Get(ReportSchedule.TimeKey), out var parsedAt)
            ? parsedAt
            : FallbackTime;
        var weeklyDay = ReportSchedule.TryParseDay(_settings.Get(ReportSchedule.WeeklyDayKey), out var parsedDay)
            ? parsedDay
            : DayOfWeek.Monday;
        var lastSentUtc = DateTime.TryParse(
            _settings.Get(ReportSchedule.LastSentKey),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var lastSent)
            ? lastSent
            : (DateTime?)null;

        var now = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(now, TimeZoneInfo.Local);
        var lastLocal = lastSent is { } l
            ? TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(l, DateTimeKind.Utc), TimeZoneInfo.Local)
            : (DateTime?)null;

        if (!ReportSchedule.IsDue(nowLocal, lastLocal, cadence, at, weeklyDay))
        {
            var dueNext = ReportSchedule.NextDueLocal(nowLocal, cadence, at, weeklyDay);
            return new ReportMailOutcome(
                false,
                dueNext is { } n ? $"下次寄送：{n:yyyy-MM-dd HH:mm}" : "未到寄送時間",
                null);
        }

        var notify = NotificationSettings.Load(_settings);
        if (!notify.SmtpEnabled)
        {
            return new ReportMailOutcome(false, "SMTP 未設定（設定中心 → 通知）", null);
        }

        var to = now;
        var from = to.AddDays(-ReportSchedule.WindowDays(cadence));
        var csv = ReportCsv.Build(
            _reports.ListRecordingSummary(from, to),
            _reports.GetDisconnectCount(from, to),
            _reports.ListCapacityTrend(from, to),
            _reports.ListAiEventSummary(from, to));

        var dir = Path.Combine(_dataRoot, "reports");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"report-scheduled-{to:yyyyMMddHHmmss}.csv");
        await File.WriteAllTextAsync(path, csv, new UTF8Encoding(true), ct).ConfigureAwait(false);

        var subject = $"HeliVMS 排程報表（{ReportSchedule.ToKey(cadence)}，{from:yyyy-MM-dd} ~ {to:yyyy-MM-dd}）";
        var body =
            "HeliVMS 排程報表\n"
            + $"期間：{from:yyyy-MM-dd HH:mm:ss} ~ {to:yyyy-MM-dd HH:mm:ss} UTC\n"
            + $"排程：{ReportSchedule.ToKey(cadence)} {at:HH\\:mm}\n"
            + "附件為 CSV。\n";

        var ok = await new SmtpNotifier().SendReportAsync(notify, subject, body, path).ConfigureAwait(false);
        if (!ok)
        {
            return new ReportMailOutcome(false, "寄送失敗（請檢查 SMTP 設定）", path);
        }

        _settings.Set(ReportSchedule.LastSentKey, SqliteStore.Iso(to));
        var next = ReportSchedule.NextDueLocal(nowLocal, cadence, at, weeklyDay);
        return new ReportMailOutcome(
            true,
            next is { } nd ? $"已寄送；下次：{nd:yyyy-MM-dd HH:mm}" : "已寄送",
            path);
    }
}
