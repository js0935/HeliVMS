using System.Security.Cryptography;
using HeliVMS.Licensing;
using HeliVMS.Licensing.Crypto;
using HeliVMS.Recording;
using HeliVMS.Shared.Models;
using HeliVMS.Storage;
using Microsoft.Data.Sqlite;

namespace HeliVMS.Recording.Tests;

/// <summary>
/// 排程錄影的啟停決策。這些規則以前沒有任何測試能碰到：
/// <see cref="RecordingScheduler.ReconcileAsync"/> 會直接 <c>new SegmentRecorder</c>()
/// 並啟動 ffmpeg，於是「該不該開錄」只能在真的開了錄影機之後才看得到。
///
/// 這裡用假的錄影工作單元（<see cref="FakeSegmentRecorder"/>）驅動排程，
/// 規則本身被驗證，不需要攝影機、不需要 RTSP、也不需要 ffmpeg。
/// 真正的錄影行程封裝（<see cref="SegmentRecorder"/>）要靠 integration 測試，那是另一層問題。
///
/// 注意：排程的啟停狀態存在 <c>RecordingScheduler._active</c>，是<b>實例狀態</b>。
/// 因此同一組 arrange/act 必須用同一個排程實例走完，不能每次呼叫
/// <c>ReconcileAsync</c> 都新建一個——否則第二個實例的 <c>_active</c> 是空的，
/// 測試會測到一個不存在於正式程式的情境。
///
/// 決策規則本身在 <c>RecordingIntentTests</c>，這裡只驗「規則有沒有真的被執行」。
/// 兩邊都要有：規則寫對但沒接上，跟規則沒寫，是同樣嚴重的失效。
/// </summary>
public sealed class RecordingSchedulerTests : IDisposable
{
    // 測試金鑰對：注入對應的 LicenseManager，故不需動 EmbeddedPublicKey。
    private static readonly RSA TestKey = RSA.Create(2048);
    private static readonly string TestPublicPem = RsaPem.ToPublicPem(TestKey);

    private readonly string _dbPath;
    private readonly string _recordingsRoot;
    private readonly SqliteStore _store;
    private readonly LicenseService _license;
    private readonly ChannelRepository _channels;
    private readonly RecordingScheduleRepository _schedules;
    private readonly List<FakeSegmentRecorder> _started = [];

    public RecordingSchedulerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-sched-{Guid.NewGuid():N}.db");
        _recordingsRoot = Path.Combine(Path.GetTempPath(), $"helivms-sched-rec-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_recordingsRoot);
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _license = new LicenseService(_store, new LicenseManager(TestPublicPem));
        _channels = new ChannelRepository(_store);
        _schedules = new RecordingScheduleRepository(_store);
    }

    public void Dispose()
    {
        _store.Dispose();
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var path = _dbPath + suffix;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        if (Directory.Exists(_recordingsRoot))
        {
            Directory.Delete(_recordingsRoot, recursive: true);
        }
    }

    /// <summary>
    /// 排程不得對「監看畫面已經在錄」的頻道再開一台錄影機。
    ///
    /// 現場症狀：同一頻道兩個 ffmpeg 行程搶同一批區段檔——磁碟用量翻倍、
    /// 區段互相覆蓋、索引與磁碟上的實際檔案對不上，而且沒有任何錯誤或例外。
    /// 這是排程最容易犯、又最難從畫面上看出來的錯。
    /// </summary>
    [Fact]
    public async Task 監看中已在錄影的頻道不會被排程再開一台錄影機()
    {
        ApplyLicense(cameras: 32);
        var channel = AddChannel("車道 1");
        AddSchedule(channel, ActiveNow());
        var busy = new HashSet<int> { channel };

        using var scheduler = NewScheduler(isCellRecording: busy.Contains);
        await scheduler.ReconcileAsync();

        Assert.Empty(_started);
    }

    /// <summary>
    /// 全時模式的頻道必須真的被開錄，就算完全沒有排程。
    ///
    /// 這是「長期監控錄影」最直接的失效：<c>recording_mode</c> 的預設值是 Always，
    /// 設定頁預設也顯示「全時」，但排程器從來不讀這個欄位。
    /// 現場症狀是操作員看著畫面上的「全時」以為在錄影，實際上一段都沒有——
    /// 沒有錯誤、沒有警告、log 乾淨，等到他要調閱影像時才發現。
    /// 這裡守的是「規則有沒有真的接上執行」，規則本身的細節在 RecordingIntentTests。
    /// </summary>
    [Fact]
    public async Task 全時模式的頻道不需要排程就會被排程器開錄()
    {
        ApplyLicense(cameras: 32);
        var channel = AddChannel("車道 1", RecordingMode.Always);

        using var scheduler = NewScheduler();
        await scheduler.ReconcileAsync();

        var recorder = Assert.Single(_started);
        Assert.Equal(channel, recorder.ChannelId);
        Assert.Contains(channel, scheduler.ActiveChannelIds);
    }

    /// <summary>
    /// 全時頻道被停用後，排程要真的把錄影機收掉。
    ///
    /// 只驗證「會開」不夠：開了卻停不掉，操作員按了停用，磁碟照樣被吃滿，
    /// 而且畫面顯示已經停用、實際上沒有——比不開更難查。
    /// </summary>
    [Fact]
    public async Task 全時頻道被停用後排程會停止錄影()
    {
        ApplyLicense(cameras: 32);
        var channel = AddChannel("車道 1", RecordingMode.Always);

        using var scheduler = NewScheduler();
        await scheduler.ReconcileAsync();
        var recorder = Assert.Single(_started);
        Assert.False(recorder.Stopped);

        DisableChannel(channel);
        await scheduler.ReconcileAsync();

        Assert.True(recorder.Stopped);
        Assert.True(recorder.Disposed);
        Assert.Empty(scheduler.ActiveChannelIds);
    }

    /// <summary>
    /// 全時模式的開錄要寫下 always 這個來源，不能混在 schedule 裡。
    ///
    /// 出事要追的是「誰開的」：使用者自己設的全時，和排程把它拉起來的，
    /// 該走的處理完全不同。混成同一個標籤，這個問題就永遠查不出來。
    /// </summary>
    [Fact]
    public async Task 全時模式開錄寫下always而非schedule來源()
    {
        ApplyLicense(cameras: 32);
        AddChannel("車道 1", RecordingMode.Always);

        using var scheduler = NewScheduler();
        await scheduler.ReconcileAsync();

        var starts = RecordingAudits("recording.start");
        var entry = Assert.Single(starts);
        Assert.Contains($"source={RecordingGateSources.Always}", entry.Detail ?? string.Empty);
    }

    /// <summary>排程模式的開錄要寫下 schedule，與全時區隔開。</summary>
    [Fact]
    public async Task 排程模式開錄寫下schedule來源()
    {
        ApplyLicense(cameras: 32);
        AddSchedule(AddChannel("車道 1"), ActiveNow());

        using var scheduler = NewScheduler();
        await scheduler.ReconcileAsync();

        var entry = Assert.Single(RecordingAudits("recording.start"));
        Assert.Contains($"source={RecordingGateSources.Schedule}", entry.Detail ?? string.Empty);
    }

    /// <summary>停錄也要留下紀錄，否則查不到「這台攝影機最後一次有錄影是什麼時候」。</summary>
    [Fact]
    public async Task 停止錄影會寫下稽核()
    {
        ApplyLicense(cameras: 32);
        var channel = AddChannel("車道 1", RecordingMode.Always);

        using var scheduler = NewScheduler();
        await scheduler.ReconcileAsync();
        DisableChannel(channel);
        await scheduler.ReconcileAsync();

        Assert.Single(RecordingAudits("recording.stop"));
    }

    /// <summary>
    /// 稽核記錄不得含 RTSP 帳密。
    ///
    /// 稽核查詢會顯示在稽核視窗、也能經 API 讀取與匯出，所以寫進去等於把憑證散播到
    /// 每一份稽核備份。診斷上只需要知道「連的是哪一台」，主機與路徑足以回推問題，
    /// 帳密沒有任何診斷價值。
    /// </summary>
    [Fact]
    public async Task 稽核記錄不會寫出串流帳密()
    {
        ApplyLicense(cameras: 32);
        var channelId = _channels.Add(
            "車道 1",
            "rtsp://operator:s3cr3t@cam-7.example.invalid:554/live",
            recordingMode: RecordingMode.Always);

        using var scheduler = NewScheduler();
        await scheduler.ReconcileAsync();

        var detail = Assert.Single(RecordingAudits("recording.start")).Detail ?? string.Empty;
        Assert.DoesNotContain("s3cr3t", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("operator", detail, StringComparison.Ordinal);
        // 遮罩之後仍要留得住足以回推問題的主機。
        Assert.Contains("cam-7.example.invalid", detail, StringComparison.Ordinal);
        Assert.Equal(channelId, Assert.Single(RecordingAudits("recording.start")).TargetId);
    }

    /// <summary>
    /// 錄影機連續錄不成時要留下稽核。
    ///
    /// 這是「攝影機離線但系統看起來正常」的唯一出路：ffmpeg 連不上時錄影迴圈
    /// 仍在跑（IsRecording 為 true）、稽核也已寫過 recording.start，
    /// 磁碟上卻沒有檔案。沒有這筆紀錄，無人值守的現場可以離線好幾天沒人發現。
    /// </summary>
    [Fact]
    public async Task 錄影連續失敗會寫下停滯稽核()
    {
        ApplyLicense(cameras: 32);
        AddChannel("車道 1", RecordingMode.Always);

        using var scheduler = NewScheduler();
        await scheduler.ReconcileAsync();
        _started[0].SimulateStall(failures: 3);

        var entry = Assert.Single(RecordingAudits("recording.stalled"));
        Assert.Contains("failures=3", entry.Detail ?? string.Empty, StringComparison.Ordinal);
    }

    /// <summary>
    /// 停滯稽核不得帶出串流帳密。
    ///
    /// ffmpeg 的 stderr 幾乎一定包含
    /// <c>Input #0, rtsp, from 'rtsp://user:pass@host/...'</c>，
    /// 那是整段文字而不是合法 URI，只能用 RedactText 逐段取代。
    /// 稽核會被匯出，所以這裡漏一次就是憑證外洩一次。
    /// </summary>
    [Fact]
    public async Task 停滯稽核會遮蔽ffmpeg輸出裡的帳密()
    {
        ApplyLicense(cameras: 32);
        _channels.Add(
            "車道 1",
            "rtsp://operator:s3cr3t@cam-7.example.invalid/live",
            recordingMode: RecordingMode.Always);

        using var scheduler = NewScheduler();
        await scheduler.ReconcileAsync();
        _started[0].SimulateStall(
            failures: 3,
            ffError: "Input #0, rtsp, from 'rtsp://operator:s3cr3t@cam-7.example.invalid/live': Invalid data");

        var detail = Assert.Single(RecordingAudits("recording.stalled")).Detail ?? string.Empty;
        Assert.DoesNotContain("s3cr3t", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("operator", detail, StringComparison.Ordinal);
        Assert.Contains("cam-7.example.invalid", detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// 啟動 ffmpeg 失敗時的例外訊息裡會帶著完整命令行參數，其中就有含帳密的 RTSP URL。
    /// 稽核會被匯出，所以這裡漏一次就是憑證外洩一次。
    /// </summary>
    [Fact]
    public async Task 停滯稽核會遮蔽啟動例外裡的帳密()
    {
        ApplyLicense(cameras: 32);
        _channels.Add(
            "車道 1",
            "rtsp://operator:s3cr3t@cam-7.example.invalid/live",
            recordingMode: RecordingMode.Always);

        using var scheduler = NewScheduler();
        await scheduler.ReconcileAsync();
        _started[0].SimulateStall(
            failures: 3,
            failure: "System.ComponentModel.Win32Exception: 找不到指定的檔案"
                + "\n命令列: ffmpeg -rtsp_transport tcp -i rtsp://operator:s3cr3t@cam-7.example.invalid/live ...");

        var detail = Assert.Single(RecordingAudits("recording.stalled")).Detail ?? string.Empty;
        Assert.DoesNotContain("s3cr3t", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("operator", detail, StringComparison.Ordinal);
        Assert.Contains("cam-7.example.invalid", detail, StringComparison.Ordinal);
    }

    /// <summary>停止錄影後就不能再收到事件，否則已釋放的錄影機會被排程器一直引用。</summary>
    [Fact]
    public async Task 停止錄影後不再收到停滯事件()
    {
        ApplyLicense(cameras: 32);
        var channel = AddChannel("車道 1", RecordingMode.Always);

        using var scheduler = NewScheduler();
        await scheduler.ReconcileAsync();
        DisableChannel(channel);
        await scheduler.ReconcileAsync();

        var recorder = _started[0];
        recorder.SimulateStall(failures: 5);

        Assert.Empty(RecordingAudits("recording.stalled"));
    }

    private IReadOnlyList<AuditLogEntry> RecordingAudits(string action)
        => new AuditLogRepository(_store).List(new AuditLogQuery
        {
            Category = AuditCategories.Recording,
            Action = action,
            FromUtc = DateTime.UtcNow.AddHours(-1),
            ToUtc = DateTime.UtcNow.AddHours(1),
        });

    /// <summary>時段命中且沒有別人正在錄，就該開錄。</summary>
    [Fact]
    public async Task 時段命中時排程會啟動錄影()
    {
        ApplyLicense(cameras: 32);
        var channel = AddChannel("車道 1");
        AddSchedule(channel, ActiveNow());

        using var scheduler = NewScheduler();
        await scheduler.ReconcileAsync();

        var recorder = Assert.Single(_started);
        Assert.Equal(channel, recorder.ChannelId);
        Assert.Equal("rtsp://example.invalid/live", recorder.RtspUrl);
        Assert.False(recorder.Stopped);
        Assert.Contains(channel, scheduler.ActiveChannelIds);
    }

    /// <summary>
    /// 沒有匯入授權時排程不得啟動錄影。
    ///
    /// 排程是遠端可寫入的路徑（POST /api/recording/schedules），
    /// 閘門漏掉等於有人能從網路開免費錄影。
    /// </summary>
    [Fact]
    public async Task 沒有授權時排程不啟動錄影()
    {
        var channel = AddChannel("車道 1");
        AddSchedule(channel, ActiveNow());

        using var scheduler = NewScheduler();
        await scheduler.ReconcileAsync();

        Assert.Empty(_started);
    }

    /// <summary>
    /// 額度用完時排程不得啟動錄影，且事件中心要留得下痕跡。
    ///
    /// 閘門是以「頻道 ID 是否超過授權路數」判定（見 <c>LicenseService.CheckRecording</c>），
    /// 所以這裡給 1 路授權卻對第 2 個頻道排程。
    ///
    /// 只擋不報的話，操作員只會看到「排程沒生效」，不會知道是額度用完了。
    /// </summary>
    [Fact]
    public async Task 額度用盡時排程不啟動錄影且寫下事件()
    {
        ApplyLicense(cameras: 1);
        AddChannel("車道 1");
        var overQuota = AddChannel("車道 2");
        AddSchedule(overQuota, ActiveNow());

        using var scheduler = NewScheduler();
        await scheduler.ReconcileAsync();

        Assert.Empty(_started);
        var limitEvents = new AlarmEventRepository(_store).ListByQuery(new AlarmEventRepository.QueryArgs
        {
            ChannelId = overQuota,
            EventType = "license_limit",
            // 查詢預設的 FromUtc/ToUtc 是 DateTime.MinValue，不給範圍等於查不到任何東西。
            FromUtc = DateTime.UtcNow.AddHours(-1),
            ToUtc = DateTime.UtcNow.AddHours(1),
        });
        Assert.NotEmpty(limitEvents);
    }

    /// <summary>停用的排程不該開錄——停用是操作員唯一的關閉手段。</summary>
    [Fact]
    public async Task 停用的排程不會啟動錄影()
    {
        ApplyLicense(cameras: 32);
        var channel = AddChannel("車道 1");
        AddSchedule(channel, ActiveNow(), enabled: false);

        using var scheduler = NewScheduler();
        await scheduler.ReconcileAsync();

        Assert.Empty(_started);
    }

    /// <summary>時段外的排程不該開錄。</summary>
    [Fact]
    public async Task 時段外的排程不會啟動錄影()
    {
        ApplyLicense(cameras: 32);
        var channel = AddChannel("車道 1");
        AddSchedule(channel, WindowThatIsClosedRightNow());

        using var scheduler = NewScheduler();
        await scheduler.ReconcileAsync();

        Assert.Empty(_started);
    }

    /// <summary>
    /// 排程不能指向不存在的頻道。
    ///
    /// 原本以為要靠 <c>ReconcileAsync</c> 裡的 <c>TryGetValue</c> 擋，
    /// 實測發現 <c>recording_schedule</c> 有指向 <c>channels</c> 的外鍵，
    /// 在資料庫層就被擋掉了。真正保護系統的是這個外鍵，排程裡的檢查只是縱深防禦。
    /// </summary>
    [Fact]
    public void 排程不能指向不存在的頻道()
    {
        var error = Assert.Throws<SqliteException>(() => AddSchedule(channelId: 987654, ActiveNow()));

        // 19 = SQLITE_CONSTRAINT（外鍵違規）。
        Assert.Equal(19, error.SqliteErrorCode);
    }

    /// <summary>
    /// 時段結束後必須真的把錄影機停下來，否則會 24 小時錄下去——
    /// 使用者設了「只在營業時間錄影」卻發現磁碟被吃滿。
    /// </summary>
    [Fact]
    public async Task 時段結束後排程會停止錄影()
    {
        ApplyLicense(cameras: 32);
        var channel = AddChannel("車道 1");
        var record = AddSchedule(channel, ActiveNow());

        using var scheduler = NewScheduler();
        await scheduler.ReconcileAsync();
        var recorder = Assert.Single(_started);
        Assert.False(recorder.Stopped);

        // 同一筆排程改成已經結束的時段，排程應把錄影機收掉。
        var closed = WindowThatIsClosedRightNow();
        _schedules.Upsert(record with { StartMinute = closed.Start, EndMinute = closed.End });
        await scheduler.ReconcileAsync();

        Assert.True(recorder.Stopped);
        Assert.True(recorder.Disposed);
        Assert.Empty(scheduler.ActiveChannelIds);
    }

    /// <summary>應用程式關閉時要把所有排程錄影收乾淨，否則會留下孤兒 ffmpeg 行程。</summary>
    [Fact]
    public async Task 停止全部會停下並釋放每一台錄影機()
    {
        ApplyLicense(cameras: 32);
        foreach (var name in new[] { "車道 1", "車道 2", "車道 3" })
        {
            AddSchedule(AddChannel(name), ActiveNow());
        }

        using var scheduler = NewScheduler();
        await scheduler.ReconcileAsync();
        Assert.Equal(3, _started.Count);

        await scheduler.StopAllAsync();

        Assert.All(_started, r => Assert.True(r.Stopped));
        Assert.All(_started, r => Assert.True(r.Disposed));
        Assert.Empty(scheduler.ActiveChannelIds);
    }

    /// <summary>
    /// 排程開錄必須帶入設備憑證。
    ///
    /// 現場症狀：攝影機憑證存成 DPAPI 加密的設備欄位、頻道只存裸位址；
    /// 全時錄影若對裸位址的 ffprobe 收到 401，會連續三次失敗後發出「錄影異常」，
    /// 但同一台機器拖進方格的手動監看（走 ChannelManager 已解析的位址）畫面完全正常。
    /// 使用者看到的是「一拖進來就說錄影壞了、畫面卻好好的」。
    /// </summary>
    [Fact]
    public async Task 排程開錄會帶入設備憑證()
    {
        ApplyLicense(cameras: 8);
        var devices = new DeviceRepository(_store, new AuditLogRepository(_store));
        var deviceId = devices.Add("大門", "10.0.0.9", 554, "admin", "s3cret", "generic");
        var channelId = _channels.Add("大門", "rtsp://10.0.0.9:554/live", deviceId: deviceId);

        using var scheduler = NewScheduler();
        await scheduler.ReconcileAsync();

        var recorder = Assert.Single(_started);
        Assert.Contains("admin:s3cret@10.0.0.9", recorder.RtspUrl);
        Assert.Contains(channelId, scheduler.ActiveChannelIds);
    }

    /// <summary>
    /// 設了總頻寬上限且主碼流需求超載時，排程要改用子碼流錄影並指向子碼流位址。
    ///
    /// 這是 §14.7 #12「跨設備協調」在排程端唯一可見的效果：錄影管線是 -c:v copy，
    /// 執行中改不了碼率，唯一能少佔頻寬的手段就是換成攝影機的低碼率子碼流。
    /// 只算了配額卻沒有換串流，等於整個治理沒有出口。
    /// </summary>
    [Fact]
    public async Task 設定頻寬上限後超載頻道會改用子碼流錄影()
    {
        ApplyLicense(cameras: 32);
        var channelId = _channels.Add(
            "車道 1",
            "rtsp://example.invalid/main",
            subRtsp: "rtsp://example.invalid/sub",
            recordingMode: RecordingMode.Always);
        new SettingsRepository(_store).Set(RecordingQualityCoordinator.BudgetSettingKey, "1000000");

        using var scheduler = NewScheduler(observedBps: _ => 5_000_000);
        await scheduler.ReconcileAsync();

        var recorder = Assert.Single(_started);
        Assert.Equal("sub", recorder.Stream);
        Assert.Equal("rtsp://example.invalid/sub", recorder.RtspUrl);
        Assert.Contains(channelId, scheduler.ActiveChannelIds);
    }

    /// <summary>
    /// 沒設上限時一律主碼流——這是預設路徑，治理功能必須是 opt-in，不得改變既有站台行為。
    /// </summary>
    [Fact]
    public async Task 未設頻寬上限時維持主碼流()
    {
        ApplyLicense(cameras: 32);
        _channels.Add(
            "車道 1",
            "rtsp://example.invalid/main",
            subRtsp: "rtsp://example.invalid/sub",
            recordingMode: RecordingMode.Always);

        using var scheduler = NewScheduler(observedBps: _ => 5_000_000);
        await scheduler.ReconcileAsync();

        var recorder = Assert.Single(_started);
        Assert.Equal("main", recorder.Stream);
        Assert.Equal("rtsp://example.invalid/main", recorder.RtspUrl);
    }

    /// <summary>設了上限但配額充足時仍用主碼流，不會無條件降級。</summary>
    [Fact]
    public async Task 上限充足時維持主碼流()
    {
        ApplyLicense(cameras: 32);
        _channels.Add(
            "車道 1",
            "rtsp://example.invalid/main",
            subRtsp: "rtsp://example.invalid/sub",
            recordingMode: RecordingMode.Always);
        new SettingsRepository(_store).Set(RecordingQualityCoordinator.BudgetSettingKey, "100000000");

        using var scheduler = NewScheduler(observedBps: _ => 5_000_000);
        await scheduler.ReconcileAsync();

        Assert.Equal("main", Assert.Single(_started).Stream);
    }

    /// <summary>沒有子碼流的頻道即使超載也不降級，維持主碼流位址。</summary>
    [Fact]
    public async Task 沒有子碼流的頻道即使超載仍用主碼流()
    {
        ApplyLicense(cameras: 32);
        _channels.Add("車道 1", "rtsp://example.invalid/main", recordingMode: RecordingMode.Always);
        new SettingsRepository(_store).Set(RecordingQualityCoordinator.BudgetSettingKey, "1000000");

        using var scheduler = NewScheduler(observedBps: _ => 5_000_000);
        await scheduler.ReconcileAsync();

        var recorder = Assert.Single(_started);
        Assert.Equal("main", recorder.Stream);
        Assert.Equal("rtsp://example.invalid/main", recorder.RtspUrl);
    }

    /// <summary>
    /// 上限是在錄影途中才設定的：既有錄影必須被切換到子碼流，而不是等它自然停掉才生效。
    ///
    /// 切換＝停掉舊錄影機、用子碼流位址重開一台（會少一小段），這是 -c:v copy 下
    /// 換串流的必然代價；不切的話操作員設了上限卻看不到任何效果。
    /// </summary>
    [Fact]
    public async Task 設定上限後既有錄影會被切換到子碼流()
    {
        ApplyLicense(cameras: 32);
        var channelId = _channels.Add(
            "車道 1",
            "rtsp://example.invalid/main",
            subRtsp: "rtsp://example.invalid/sub",
            recordingMode: RecordingMode.Always);

        using var scheduler = NewScheduler(observedBps: _ => 5_000_000);
        await scheduler.ReconcileAsync();
        Assert.Equal("main", Assert.Single(_started).Stream);

        new SettingsRepository(_store).Set(RecordingQualityCoordinator.BudgetSettingKey, "1000000");
        await scheduler.ReconcileAsync();

        Assert.True(_started[0].Stopped);
        Assert.True(_started[0].Disposed);
        var switched = _started[1];
        Assert.Equal(channelId, switched.ChannelId);
        Assert.Equal("sub", switched.Stream);
        Assert.Contains(channelId, scheduler.ActiveChannelIds);
    }

    private RecordingScheduler NewScheduler(Func<int, bool>? isCellRecording = null, Func<int, long>? observedBps = null)
        => new(
            _store,
            _recordingsRoot,
            isCellRecording,
            reconcileInterval: TimeSpan.FromHours(1),
            license: _license,
            recorderFactory: () =>
            {
                var fake = new FakeSegmentRecorder();
                _started.Add(fake);
                return fake;
            },
            observedBps: observedBps);

    private int AddChannel(string name, RecordingMode mode = RecordingMode.Scheduled)
        => _channels.Add(name, "rtsp://example.invalid/live", recordingMode: mode);

    /// <summary>停用頻道。停用是操作員在設定頁關閉錄影的手段，必須真的生效。</summary>
    private void DisableChannel(int channelId)
    {
        var current = _channels.Get(channelId);
        Assert.NotNull(current);
        _channels.Update(new ChannelInfo
        {
            Id = current.Id,
            DeviceId = current.DeviceId,
            Name = current.Name,
            MainStreamUrl = current.MainStreamUrl,
            SubStreamUrl = current.SubStreamUrl,
            Codec = current.Codec,
            AudioEnabled = current.AudioEnabled,
            AudioEncoder = current.AudioEncoder,
            MotionEnabled = current.MotionEnabled,
            MotionSensitivity = current.MotionSensitivity,
            RecordingMode = current.RecordingMode,
            Enabled = false,
        });
    }

    private RecordingScheduleRecord AddSchedule(int channelId, (int Start, int End) window, bool enabled = true)
        => _schedules.Upsert(new RecordingScheduleRecord
        {
            ChannelId = channelId,
            DaysMask = AllDays,
            StartMinute = window.Start,
            EndMinute = window.End,
            Enabled = enabled,
        });

    /// <summary>涵蓋整週的位元遮罩（Sun=0 … Sat=6），避免測試跨日時不穩。</summary>
    private const int AllDays = 0b1111111;

    /// <summary>「現在一定命中」的時段：從現在起 30 分鐘、跨 2 小時。</summary>
    private static (int Start, int End) ActiveNow()
    {
        var start = MinuteOf(DateTime.Now) - 30;
        return (Wrap(start), Wrap(start + 120));
    }

    /// <summary>「現在一定不命中」的時段：4 小時前那一小時的頭 30 分鐘。</summary>
    private static (int Start, int End) WindowThatIsClosedRightNow()
    {
        var start = MinuteOf(DateTime.Now.AddHours(-4));
        return (Wrap(start), Wrap(start + 30));
    }

    private static int MinuteOf(DateTime local) => (local.Hour * 60) + local.Minute;

    private static int Wrap(int minuteOfDay) => ((minuteOfDay % 1440) + 1440) % 1440;

    private void ApplyLicense(int cameras)
        => _license.Apply(
            LicenseSerializer.Sign(
                new LicensePayload
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Machine = string.Empty,
                    IssuedUtc = DateTime.UtcNow.AddDays(-1),
                    ExpiresUtc = DateTime.UtcNow.AddDays(30),
                    Cameras = cameras,
                    Features = ["core"],
                    Issuer = "測試",
                },
                TestKey),
            "admin",
            DateTime.UtcNow);

    /// <summary>
    /// 假的錄影工作單元。只記錄被呼叫了什麼，不碰 ffmpeg、不碰 RTSP。
    /// <see cref="RecordingScheduler"/> 需要的只有「啟動、停止、釋放」三個動作。
    /// </summary>
    private sealed class FakeSegmentRecorder : ISegmentRecorder
    {
        public int ChannelId { get; private set; }

        public string RtspUrl { get; private set; } = string.Empty;

        /// <summary>排程實際要求使用的串流（<c>main</c>／<c>sub</c>）。</summary>
        public string Stream { get; private set; } = string.Empty;

        public bool Stopped { get; private set; }

        public bool Disposed { get; private set; }

        public event EventHandler<RecordingStallInfo>? RecordingStalled;

        public event EventHandler<int>? RecordingResumed;

        /// <summary>模擬這台錄影機一直錄不成（攝影機離線）。</summary>
        public void SimulateStall(int failures = 3, string ffError = "", string failure = "")
            => RecordingStalled?.Invoke(
                this,
                new RecordingStallInfo(ChannelId, failures, failure, ffError));

        /// <summary>模擬壞掉之後又真的寫出片段了。</summary>
        public void SimulateResume()
            => RecordingResumed?.Invoke(this, ChannelId);

        public Task StartAsync(
            int channelId,
            string rtspUrl,
            string recordingsRoot,
            string stream = "main",
            int? segmentSeconds = null)
        {
            ChannelId = channelId;
            RtspUrl = rtspUrl;
            Stream = stream;
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            Stopped = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}