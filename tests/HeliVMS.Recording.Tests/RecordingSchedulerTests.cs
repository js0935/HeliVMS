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

    private RecordingScheduler NewScheduler(Func<int, bool>? isCellRecording = null)
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
            });

    private int AddChannel(string name) => _channels.Add(name, "rtsp://example.invalid/live");

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

        public bool Stopped { get; private set; }

        public bool Disposed { get; private set; }

        public Task StartAsync(
            int channelId,
            string rtspUrl,
            string recordingsRoot,
            string stream = "main",
            int? segmentSeconds = null)
        {
            ChannelId = channelId;
            RtspUrl = rtspUrl;
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