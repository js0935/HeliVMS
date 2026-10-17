using HeliVMS.Storage;

namespace HeliVMS.Storage.Tests;

/// <summary>
/// NTP 校時的排程判斷與設定讀寫（M310）。
///
/// <para>
/// 校時搬到 <c>HeliVMS.Agent</c> 服務後，「什麼時候該校時」成了唯一讓服務動起來的判斷：
/// 判斷若永遠回 false，時間再怎麼偏都不會被修正，畫面卻只會顯示上次的偏移量；
/// 判斷若永遠回 true，服務每 30 秒就對外打一次 NTP、還可能反覆改動系統時鐘。
/// 這裡直接對 <see cref="NtpSettings.IsDue"/> 與設定鍵往返做行為驗證，不依賴服務本身。
/// </para>
/// </summary>
public sealed class NtpSettingsTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly SettingsRepository _settings;

    public NtpSettingsTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-ntp-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _settings = new SettingsRepository(_store);
    }

    public void Dispose()
    {
        _store.Dispose();
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var p = _dbPath + suffix;
            if (File.Exists(p))
            {
                File.Delete(p);
            }
        }
    }

    private static readonly TimeSpan Interval = NtpSettings.DefaultInterval;

    [Fact]
    public void 第一次沒有上次嘗試時間時視為到期()
    {
        // 服務剛啟動、還沒校過時，應該先校一次；否則要等滿 6 小時才會校正。
        Assert.True(NtpSettings.IsDue(null, null, null, DateTime.UtcNow, Interval));
    }

    [Fact]
    public void 距上次嘗試未滿週期不校時()
    {
        var now = DateTime.UtcNow;
        Assert.False(NtpSettings.IsDue(now - TimeSpan.FromHours(1), null, null, now, Interval));
    }

    [Fact]
    public void 距上次嘗試已滿週期要校時()
    {
        var now = DateTime.UtcNow;
        Assert.True(NtpSettings.IsDue(now - TimeSpan.FromHours(7), null, null, now, Interval));
    }

    [Fact]
    public void 有未套用的請求就立即校時即使未到週期()
    {
        var now = DateTime.UtcNow;
        Assert.True(NtpSettings.IsDue(now - TimeSpan.FromMinutes(5), now, null, now, Interval));
    }

    [Fact]
    public void 請求已套用且未到週期不重複校時()
    {
        var now = DateTime.UtcNow;
        var request = now - TimeSpan.FromMinutes(5);
        Assert.False(NtpSettings.IsDue(now - TimeSpan.FromMinutes(1), request, request, now, Interval));
    }

    [Fact]
    public void 比上次套用更新的請求會再次觸發校時()
    {
        var now = DateTime.UtcNow;
        var applied = now - TimeSpan.FromHours(1);
        var newerRequest = now - TimeSpan.FromSeconds(1);
        Assert.True(NtpSettings.IsDue(now - TimeSpan.FromMinutes(1), newerRequest, applied, now, Interval));
    }

    [Fact]
    public void 逾期未套用的請求可被讀回且能依設定鍵判定啟用()
    {
        Assert.False(NtpSettings.IsEnabled(_settings));

        _settings.Set(NtpSettings.Enabled, "1");
        Assert.True(NtpSettings.IsEnabled(_settings));

        NtpSettings.RequestSync(_settings);
        var request = NtpSettings.ReadUtc(_settings, NtpSettings.RequestUtc);
        Assert.NotNull(request);
        // 讀回的時間要能落在合理範圍（證明格式與解析一致，跨行程才不會對不上）。
        Assert.True((DateTime.UtcNow - request!.Value).Duration() < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void 設定留空時伺服器退回預設值()
    {
        Assert.Equal(NtpSettings.DefaultServer, NtpSettings.ReadServer(_settings));

        _settings.Set(NtpSettings.Server, "  ntp.example.com  ");
        Assert.Equal("ntp.example.com", NtpSettings.ReadServer(_settings));
    }

    [Fact]
    public void 上次對時時間與偏移可往返()
    {
        Assert.Null(NtpSettings.ReadLast(_settings));

        var when = DateTime.UtcNow;
        _settings.Set(NtpSettings.LastSyncUtc, NtpSettings.FormatUtc(when));
        _settings.Set(NtpSettings.LastOffsetMs, "-1234");

        var last = NtpSettings.ReadLast(_settings);
        Assert.NotNull(last);
        Assert.Equal(-1234, last!.Value.OffsetMs);
        Assert.Equal(when, last.Value.ServerUtc.ToUniversalTime(), TimeSpan.FromSeconds(1));
    }
}
