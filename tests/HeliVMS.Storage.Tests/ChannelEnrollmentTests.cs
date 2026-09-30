namespace HeliVMS.Storage.Tests;

/// <summary>
/// 驗證頻道加入（<see cref="ChannelEnrollment"/>）的資料庫不變式。
/// <para>
/// 兩條最重要的不變式：
/// 1. sqlite 檔案中不得出現明文密碼（憑證只存 DPAPI 密文）。
/// 2. channels.main_rtsp 不得含帳密（否則備份／稽核／匯出都會洩漏）。
/// 這兩點若被破壞，洩漏點會擴散到 DB 備份、稽核記錄與畫面匯出，遠比單一連線錯誤嚴重。
/// </para>
/// </summary>
public sealed class ChannelEnrollmentTests : IDisposable
{
    private const string Password = "topsecretpw";

    private readonly string _dbPath;
    private readonly SqliteStore _store;

    public ChannelEnrollmentTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-enroll-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
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

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Enroll_CreatesDeviceAndBindsChannel()
    {
        var channelId = ChannelEnrollment.Enroll(
            _store,
            "Lobby",
            "rtsp://10.0.0.5:554/stream.sdp",
            "10.0.0.5",
            80,
            "root",
            Password);

        var channel = new ChannelRepository(_store).Get(channelId);
        Assert.NotNull(channel);
        Assert.NotNull(channel.DeviceId);

        var (username, password) = new DeviceRepository(_store, new AuditLogRepository(_store))
            .GetRtspCredentials(channel.DeviceId.Value);
        Assert.Equal("root", username);
        Assert.Equal(Password, password);
    }

    [Fact]
    public void Enroll_SameDeviceTwice_ReusesDevice_AndRepairsWrongCredentials()
    {
        var first = ChannelEnrollment.Enroll(_store, "Lobby", "rtsp://10.0.0.5:554/a.sdp", "10.0.0.5", 80, "root", "wrongpw");
        var firstDevice = new ChannelRepository(_store).Get(first)!.DeviceId!.Value;

        // 同一台攝影機帶正確密碼重新加入：應沿用同一筆設備並就地修正密碼。
        // 若不修正，使用者重按一次加入仍會沿用壞掉的憑證而拉不到畫面。
        var second = ChannelEnrollment.Enroll(_store, "Lobby", "rtsp://10.0.0.5:554/a.sdp", "10.0.0.5", 80, "root", Password);
        var secondDevice = new ChannelRepository(_store).Get(second)!.DeviceId!.Value;

        Assert.Equal(firstDevice, secondDevice);
        Assert.Single(new DeviceRepository(_store, new AuditLogRepository(_store)).List());

        var (_, password) = new DeviceRepository(_store, new AuditLogRepository(_store)).GetRtspCredentials(secondDevice);
        Assert.Equal(Password, password);
    }

    [Fact]
    public void Enroll_SameDeviceWithSameCredentials_DoesNotRewrite()
    {
        var id = ChannelEnrollment.Enroll(_store, "Lobby", "rtsp://10.0.0.5:554/a.sdp", "10.0.0.5", 80, "root", Password);
        var deviceId = new ChannelRepository(_store).Get(id)!.DeviceId!.Value;

        // 再加一次完全相同的憑證，結果仍必須可正常解密。
        ChannelEnrollment.Enroll(_store, "Lobby2", "rtsp://10.0.0.5:554/b.sdp", "10.0.0.5", 80, "root", Password);

        var (username, password) = new DeviceRepository(_store, new AuditLogRepository(_store)).GetRtspCredentials(deviceId);
        Assert.Equal("root", username);
        Assert.Equal(Password, password);
    }

    [Theory]
    [InlineData(null, "root", Password)]
    [InlineData("10.0.0.9", null, Password)]
    [InlineData("", "root", Password)]
    [InlineData("10.0.0.9", "  ", Password)]
    public void Enroll_WithoutUsableDevice_BindsNull_SoManualUrlsStillWork(string? ip, string? user, string? pw)
    {
        // 使用者手動輸入 RTSP 網址時不會有設備資訊，此時只建立頻道，
        // 不可因為缺憑證就讓整個加入流程失敗。
        var channelId = ChannelEnrollment.Enroll(_store, "Manual", "rtsp://10.0.0.9:554/manual.sdp", ip, 80, user, pw);

        var channel = new ChannelRepository(_store).Get(channelId);
        Assert.NotNull(channel);
        Assert.Null(channel.DeviceId);
        Assert.Equal("rtsp://10.0.0.9:554/manual.sdp", channel.MainStreamUrl);
    }

    [Fact]
    public void Enroll_StoresStreamUrlWithoutCredentials()
    {
        var channelId = ChannelEnrollment.Enroll(
            _store,
            "Lobby",
            "rtsp://10.0.0.5:554/stream.sdp",
            "10.0.0.5",
            80,
            "root",
            Password);

        var channel = new ChannelRepository(_store).Get(channelId);
        Assert.NotNull(channel);
        Assert.DoesNotContain("@", channel.MainStreamUrl, StringComparison.Ordinal);
        Assert.Equal("rtsp://10.0.0.5:554/stream.sdp", channel.MainStreamUrl);
    }

    [Fact]
    public void Enroll_NeverWritesPlaintextPasswordToDatabaseFile()
    {
        ChannelEnrollment.Enroll(_store, "Lobby", "rtsp://10.0.0.5:554/a.sdp", "10.0.0.5", 80, "root", Password);
        ChannelEnrollment.Enroll(_store, "Yard", "rtsp://10.0.0.6:554/a.sdp", "10.0.0.6", 80, "admin", "anotherpw");

        _store.Dispose();

        // 直接掃描 DB 檔案本體（而非透過 API），確保連 WAL 之外的備份／複製檔也不含明文。
        var raw = File.ReadAllText(_dbPath, System.Text.Encoding.Latin1);
        Assert.DoesNotContain(Password, raw, StringComparison.Ordinal);
        Assert.DoesNotContain("anotherpw", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("root:", raw, StringComparison.Ordinal);
    }

    [Fact]
    public void Enroll_DefaultPortWhenZero()
    {
        var channelId = ChannelEnrollment.Enroll(_store, "Lobby", "rtsp://10.0.0.5:554/a.sdp", "10.0.0.5", 0, "root", Password);
        var deviceId = new ChannelRepository(_store).Get(channelId)!.DeviceId!.Value;

        Assert.Equal(80, new DeviceRepository(_store, new AuditLogRepository(_store)).Get(deviceId)!.Port);
    }
}
