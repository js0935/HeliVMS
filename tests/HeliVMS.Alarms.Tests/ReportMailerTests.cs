using HeliVMS.Storage;
using Xunit;

namespace HeliVMS.Alarms.Tests;

/// <summary>
/// 排程報表寄送（§14.1 #9）：驗證「到達時點才寄、同日不重複、未設定不寄」，
/// 並確認成功後把寄送時間寫回 <c>report.mail.last_sent</c>（重啟後不補寄的關鍵）。
/// </summary>
public sealed class ReportMailerTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _dataRoot;
    private readonly SqliteStore _store;

    public ReportMailerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-reportmail-{Guid.NewGuid():N}.db");
        _dataRoot = Path.Combine(Path.GetTempPath(), $"helivms-reportmail-{Guid.NewGuid():N}");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
    }

    public void Dispose()
    {
        _store.Dispose();
        try
        {
            File.Delete(_dbPath);
            File.Delete(_dbPath + "-wal");
            File.Delete(_dbPath + "-shm");
        }
        catch (IOException)
        {
        }

        try
        {
            if (Directory.Exists(_dataRoot))
            {
                Directory.Delete(_dataRoot, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private SettingsRepository Settings => new(_store);

    private void EnableSmtp(FakeSmtpServer smtp)
    {
        Settings.Set("notify.smtp.enabled", "true");
        Settings.Set("notify.smtp.from", "sender@helivms.local");
        Settings.Set("notify.smtp.to", "ops@helivms.local");
        Settings.Set("notify.smtp.host", smtp.Host);
        Settings.Set("notify.smtp.port", smtp.Port.ToString());
    }

    private void EnableDailySchedule()
    {
        Settings.Set(ReportSchedule.EnabledKey, "1");
        Settings.Set(ReportSchedule.CadenceKey, "daily");
        Settings.Set(ReportSchedule.TimeKey, "00:00");
    }

    [Fact]
    public async Task 到達排程時刻會產生CSV並寄出且記錄寄送時間()
    {
        using var smtp = new FakeSmtpServer();
        EnableSmtp(smtp);
        EnableDailySchedule();

        var outcome = await new ReportMailer(_store, _dataRoot).RunIfDueAsync(DateTime.UtcNow);

        Assert.True(outcome.Sent);
        Assert.NotNull(outcome.Path);
        Assert.True(File.Exists(outcome.Path));
        Assert.NotNull(Settings.Get(ReportSchedule.LastSentKey));

        var msg = await WaitForSmtpAsync(smtp, TimeSpan.FromSeconds(5));
        Assert.NotNull(msg);
        Assert.Contains(Path.GetFileName(outcome.Path!), msg.Payload);
    }

    [Fact]
    public async Task 同日第二次執行不再寄送()
    {
        using var smtp = new FakeSmtpServer();
        EnableSmtp(smtp);
        EnableDailySchedule();

        var now = DateTime.UtcNow;
        var first = await new ReportMailer(_store, _dataRoot).RunIfDueAsync(now);
        var second = await new ReportMailer(_store, _dataRoot).RunIfDueAsync(now);

        Assert.True(first.Sent);
        Assert.False(second.Sent);

        // 第一次確實寄出一封；第二次因同日已寄送而不得再寄。
        Assert.True(smtp.TryDequeueMessage(out _));
        Assert.False(smtp.TryDequeueMessage(out _));
    }

    [Fact]
    public async Task 未啟用排程不寄送()
    {
        using var smtp = new FakeSmtpServer();
        EnableSmtp(smtp);
        Settings.Set(ReportSchedule.EnabledKey, "0");

        var outcome = await new ReportMailer(_store, _dataRoot).RunIfDueAsync(DateTime.UtcNow);

        Assert.False(outcome.Sent);
        Assert.False(smtp.TryDequeueMessage(out _));
    }

    [Fact]
    public async Task 排程啟用但SMTP未設定時不寄送並回報()
    {
        EnableDailySchedule();
        Settings.Set("notify.smtp.enabled", "false");

        var outcome = await new ReportMailer(_store, _dataRoot).RunIfDueAsync(DateTime.UtcNow);

        Assert.False(outcome.Sent);
        Assert.Contains("SMTP", outcome.Message);
    }

    private static async Task<FakeSmtpMessage?> WaitForSmtpAsync(FakeSmtpServer smtp, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (smtp.TryDequeueMessage(out var m))
            {
                return m;
            }

            await Task.Delay(30);
        }

        return null;
    }
}
