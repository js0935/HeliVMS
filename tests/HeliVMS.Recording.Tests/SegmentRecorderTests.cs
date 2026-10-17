using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using HeliVMS.Recording;
using HeliVMS.Shared;
using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.Recording.Tests;

/// <summary>
/// 錄影工作單元（<see cref="SegmentRecorder"/>）的收尾行為。
///
/// 這個類別是錄影管道的核心，卻一直只有排程層的假替身被測到；真正的 ffmpeg
/// 啟停、fMP4 參數、暫存檔改名、SHA-256、索引回寫全靠人工驗證。
/// 這裡用可注入的行程工廠（<see cref="IProcessFactory"/>）與音訊探測委派，
/// 讓每一條收尾路徑都能被斷言，而不需要攝影機、RTSP 或 ffmpeg。
///
/// 最重要的兩條保證：正常結束的檔案一定要有正確的 SHA-256 且索引為 final；
/// ffmpeg 失敗時絕不能留下正式檔或 final 索引（否則時間軸會出現打不開的片段）。
/// </summary>
public sealed class SegmentRecorderTests : IDisposable
{
    private const string RtspUrl = "rtsp://example.invalid/live";
    private const int SegmentSeconds = 42;

    private readonly string _dbPath;
    private readonly string _recordingsRoot;
    private readonly SqliteStore _store;
    private readonly SegmentRepository _segments;
    private readonly int _channelId;

    public SegmentRecorderTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-rec-{Guid.NewGuid():N}.db");
        _recordingsRoot = Path.Combine(Path.GetTempPath(), $"helivms-rec-out-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_recordingsRoot);
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _segments = new SegmentRepository(_store);
        _channelId = new ChannelRepository(_store).Add("測試頻道", RtspUrl);
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
    /// ffmpeg 正常結束（退出碼 0）時，暫存檔必須被改名為正式檔、SHA-256 必須與磁碟
    /// 上的位元組一致、索引必須是 final，並以 <see cref="SegmentRecorder.SegmentCompleted"/>
    /// 回報一段可用的錄影。
    ///
    /// SHA-256 是 §11.5 防竄改的依據；算錯或沒回寫，之後的證據驗證會把正常片段判成偽造。
    /// </summary>
    [Fact]
    public async Task 正常結束時會把暫存檔改名寫入索引並回報完成()
    {
        var (recorder, factory, first) = BeginFirstSegment(FirstCompletesThenPending);
        var segment = await first.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(recorder.IsRecording);
        Assert.Equal(_channelId, recorder.ChannelId);
        Assert.Equal("main", recorder.Stream);
        Assert.Equal(SegmentSeconds, recorder.SegmentSeconds);

        Assert.Equal(SegmentStatus.Final, segment.Status);
        Assert.False(segment.FilePath.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));

        var stored = Assert.IsType<SegmentRecord>(_segments.Get(segment.Id));
        Assert.Equal(SegmentStatus.Final, stored.Status);
        Assert.True(File.Exists(stored.FilePath));
        Assert.Equal(factory.Processes[0].Output.Length, (int)stored.SizeBytes);
        // 結束時間與長度取自真實收尾時刻（見 SegmentRecorder），不再固定等於設定秒數，
        // 兩者必須彼此一致。
        Assert.NotNull(stored.EndUtc);
        Assert.Equal(
            (stored.EndUtc!.Value - stored.StartUtc).TotalSeconds,
            stored.DurationSec!.Value,
            2);
        Assert.Equal(Sha256Of(factory.Processes[0].Output), stored.Sha256);

        await recorder.DisposeAsync();
    }

    /// <summary>
    /// 每個區段結束後都必須釋放它的 ffmpeg 行程與管線控制代碼。
    ///
    /// 長期錄影是「一天 1,440 段 × 16 路」，若每段都漏一組 handle，程式會先耗盡控制代碼、
    /// 接著在完全無關的地方（例如開檔）以看似隨機的例外崩掉——而 log 只會指向崩潰點。
    /// </summary>
    [Fact]
    public async Task 區段收尾後會釋放ffmpeg行程()
    {
        var (recorder, factory, first) = BeginFirstSegment(FirstCompletesThenPending);
        await first.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(
            factory.Processes[0].Disposed,
            "區段結束後沒有釋放 ffmpeg 行程，控制代碼會隨每段累積外洩。");

        await recorder.DisposeAsync();
    }

    /// <summary>
    /// 錄影參數是負載平衡的：`-c:v copy` 確保不轉碼、`-f mp4` 加
    /// `frag_keyframe+empty_moov+default_base_moof+faststart` 是 fMP4 分段與邊錄邊放的
    /// 前提，`-t` 決定區段長度。任一改動都會造成「有檔但時間軸壞掉」。
    /// </summary>
    [Fact]
    public async Task ffmpeg參數必須直拷畫面並輸出fMP4()
    {
        var (recorder, factory, first) = BeginFirstSegment(FirstCompletesThenPending);
        await first.WaitAsync(TimeSpan.FromSeconds(10));

        var psi = factory.Processes[0].StartInfo;
        var args = psi.ArgumentList;

        Assert.Equal("ffmpeg", psi.FileName);
        Assert.Equal("tcp", ValueAfter(args, "-rtsp_transport"));
        Assert.Equal(RtspUrl, ValueAfter(args, "-i"));
        Assert.Equal("copy", ValueAfter(args, "-c:v"));
        Assert.Equal("mp4", ValueAfter(args, "-f"));
        Assert.Equal(
            "frag_keyframe+empty_moov+default_base_moof+faststart",
            ValueAfter(args, "-movflags"));
        Assert.Equal(SegmentSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture), ValueAfter(args, "-t"));
        Assert.Equal("0:v:0", ValueAfter(args, "-map"));
        Assert.EndsWith(".tmp", args[^1], StringComparison.OrdinalIgnoreCase);

        await recorder.DisposeAsync();
    }

    /// <summary>
    /// 來源有 AAC 音訊時必須映射音訊並以 copy 收錄，避免無謂轉碼。
    /// 少了 `-map 0:a:0?` 會變成靜音錄影，而畫面上完全看不出來。
    /// </summary>
    [Fact]
    public async Task 有音訊時加入對應的映射與編碼參數()
    {
        var (recorder, factory, first) = BeginFirstSegment(
            FirstCompletesThenPending,
            _ => "-c:a copy");
        await first.WaitAsync(TimeSpan.FromSeconds(10));

        var args = factory.Processes[0].StartInfo.ArgumentList;
        Assert.Contains("0:a:0?", args);
        Assert.Equal("copy", ValueAfter(args, "-c:a"));

        await recorder.DisposeAsync();
    }

    /// <summary>
    /// 來源無音訊時不得加入音訊映射與 `-c:a`，否則 ffmpeg 會因為找不到音訊流而整段失敗。
    /// </summary>
    [Fact]
    public async Task 無音訊時不加入音訊映射()
    {
        var (recorder, factory, first) = BeginFirstSegment(
            FirstCompletesThenPending,
            _ => string.Empty);
        await first.WaitAsync(TimeSpan.FromSeconds(10));

        var args = factory.Processes[0].StartInfo.ArgumentList;
        Assert.DoesNotContain("0:a:0?", args);
        Assert.DoesNotContain("-c:a", args);

        await recorder.DisposeAsync();
    }

    /// <summary>
    /// ffmpeg 以非零退出碼結束時，絕不能留下正式檔或 final 索引：暫存檔要刪掉、
    /// 索引要標成 corrupt，而且不得發出 <see cref="SegmentRecorder.SegmentCompleted"/>。
    ///
    /// 若這裡漏了，時間軸會出現一個「看起來存在、實際打不開」的片段，
    /// 而且匯出／回放會拿到損壞檔案卻沒有任何錯誤來源。
    /// </summary>
    [Fact]
    public async Task ffmpeg非零退出時刪除暫存標記異常且不回報完成()
    {
        var completedFired = false;
        var factory = new FakeProcessFactory((index, psi) =>
        {
            var p = new FakeProcess(psi, exitCode: 1);
            if (index == 0)
            {
                p.ProduceAndExit();
            }

            return p;
        });

        await using var recorder = new SegmentRecorder(_segments, factory, _ => string.Empty);
        recorder.SegmentCompleted += (_, _) => completedFired = true;
        await recorder.StartAsync(_channelId, RtspUrl, _recordingsRoot, "main", SegmentSeconds);

        await WaitUntilAsync(() => _segments.ListByRange(
            _channelId,
            "main",
            DateTime.UtcNow.AddMinutes(-5),
            DateTime.UtcNow.AddMinutes(5)).Any(s => s.Status == SegmentStatus.Corrupt));

        var corrupt = Assert.Single(
            _segments.ListByRange(
                _channelId,
                "main",
                DateTime.UtcNow.AddMinutes(-5),
                DateTime.UtcNow.AddMinutes(5)),
            s => s.Status == SegmentStatus.Corrupt);

        Assert.Equal(SegmentStatus.Corrupt, corrupt.Status);
        Assert.False(File.Exists(corrupt.FilePath));
        Assert.False(File.Exists(corrupt.FilePath + ".tmp"));
        Assert.Empty(_segments.ListAllFinal());
        Assert.False(completedFired);
    }

    /// <summary>
    /// 音訊探測（ffprobe）每場錄影只該做一次：它是慢的、且會佔攝影機的 RTSP session。
    /// 若每段都探測，長時間錄影會反覆擠掉主串流。
    /// </summary>
    [Fact]
    public async Task 音訊探測在整個錄影期間只執行一次()
    {
        var probeCalls = 0;
        var twoDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completedCount = 0;

        var factory = new FakeProcessFactory((index, psi) =>
        {
            var p = new FakeProcess(psi, exitCode: 0);
            if (index == 0)
            {
                // 第一段延到下一秒才收尾，使下一段的開始時間跨秒，避免同日同秒的檔名碰撞
                // （正式每段 600 秒不會發生，但測試是即時收尾）。
                _ = Task.Run(async () =>
                {
                    await Task.Delay(1100);
                    p.ProduceAndExit();
                });
            }
            else
            {
                p.ProduceAndExit();
            }

            return p;
        });

        await using var recorder = new SegmentRecorder(
            _segments,
            factory,
            _ =>
            {
                Interlocked.Increment(ref probeCalls);
                return "-c:a copy";
            });

        recorder.SegmentCompleted += (_, _) =>
        {
            if (Interlocked.Increment(ref completedCount) >= 2)
            {
                twoDone.TrySetResult();
            }
        };

        await recorder.StartAsync(_channelId, RtspUrl, _recordingsRoot, "main", SegmentSeconds);
        await twoDone.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, probeCalls);
    }

    /// <summary>
    /// 停止錄影時必須終止進行中的 ffmpeg，否則關閉程式後會留下孤兒行程繼續佔用
    /// RTSP session 直到逾時，下一次開錄會拿不到串流。
    /// </summary>
    [Fact]
    public async Task 停止時會終止進行中的ffmpeg行程並結束錄影()
    {
        var (recorder, factory, first) = BeginFirstSegment(FirstCompletesThenPending);
        await first.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => factory.Processes.Count >= 2);

        await recorder.StopAsync();

        Assert.True(factory.Processes[1].KillCount >= 1);
        Assert.False(recorder.IsRecording);

        await recorder.DisposeAsync();
    }

    /// <summary>
    /// ffmpeg 的 stderr 會回顯輸入網址，其中含攝影機帳密；診斷緩衝（<see cref="SegmentRecorder.LastFfError"/>）
    /// 可能被顯示在 UI 或寫進日誌，因此必須先遮蔽。同時保留退出碼，否則「連線被拒」與
    /// 「格式錯誤」在診斷上無法區分。
    /// </summary>
    [Fact]
    public async Task ffmpeg的stderr診斷會遮蔽帳密且保留退出碼()
    {
        // 第一段立刻以退出碼 1 結束；之後的行程停住，避免下一段把診斷緩衝清掉而產生競態。
        var gate = new ManualResetEventSlim(false);
        var factory = new FakeProcessFactory((index, psi) =>
        {
            if (index == 0)
            {
                var failed = new FakeProcess(
                    psi,
                    exitCode: 1,
                    stderr: "Input #0, rtsp, from 'rtsp://admin:s3cret@10.0.0.5/live':\n");
                failed.ProduceAndExit();
                return failed;
            }

            gate.Wait(TimeSpan.FromSeconds(15));
            return new FakeProcess(psi, exitCode: 0);
        });

        await using var recorder = new SegmentRecorder(_segments, factory, _ => string.Empty);
        await recorder.StartAsync(_channelId, RtspUrl, _recordingsRoot, "main", SegmentSeconds);

        try
        {
            await WaitUntilAsync(() => recorder.LastExitCode is not null);

            Assert.Equal(1, recorder.LastExitCode);
            Assert.DoesNotContain("s3cret", recorder.LastFfError);
            Assert.DoesNotContain("admin", recorder.LastFfError);
            Assert.Contains("rtsp://10.0.0.5/live", recorder.LastFfError);
        }
        finally
        {
            gate.Set();
        }
    }

    /// <summary>
    /// 錄影用的 ffmpeg 啟動時必須登錄到全域行程登錄表，非零退出要標記為失敗（§13-2）。
    ///
    /// 現場症狀：機器變慢時，維運沒有任何單一地方看得出「此刻開著幾條錄影 ffmpeg」，
    /// 只能逐頻道點開。少了這個登錄，健康度面板的 ffmpeg 總數會永遠偏低，
    /// 真正失控的來源就被掩蓋。
    /// </summary>
    [Fact]
    public async Task 錄影行程會登錄到全域登錄表且失敗會被標記()
    {
        var registry = new FfmpegProcessRegistry();

        // 第一段立刻以退出碼 1 結束；之後的行程停住，避免競態。
        var gate = new ManualResetEventSlim(false);
        var factory = new FakeProcessFactory((index, psi) =>
        {
            if (index == 0)
            {
                var failed = new FakeProcess(psi, exitCode: 1, stderr: "boom");
                failed.ProduceAndExit();
                return failed;
            }

            gate.Wait(TimeSpan.FromSeconds(15));
            return new FakeProcess(psi, exitCode: 0);
        });

        await using var recorder = new SegmentRecorder(_segments, factory, _ => string.Empty, registry: registry);
        await recorder.StartAsync(_channelId, RtspUrl, _recordingsRoot, "main", SegmentSeconds);

        try
        {
            await WaitUntilAsync(() => registry.Snapshot().TotalFailed == 1);

            var snapshot = registry.Snapshot();
            Assert.True(snapshot.TotalStarted >= 1, "啟動錄影 ffmpeg 時必須登錄");
            Assert.Equal(1, snapshot.TotalFailed);
            Assert.Equal($"ch{_channelId:000}/main", snapshot.LastFailureKey);
        }
        finally
        {
            gate.Set();
        }
    }

    /// <summary>
    /// 音訊探測（ffprobe）失敗時，錄影迴圈必須活下來、把原因記進 <see cref="SegmentRecorder.LastFailure"/>，
    /// 且不得啟動 ffmpeg。若這裡讓例外逃逸，整台頻道的錄影會永久停擺而沒有任何提示。
    /// </summary>
    [Fact]
    public async Task 音訊探測失敗會記錄原因且不啟動ffmpeg()
    {
        var factory = new FakeProcessFactory((_, psi) => new FakeProcess(psi, exitCode: 0));
        await using var recorder = new SegmentRecorder(
            _segments,
            factory,
            _ => throw new InvalidOperationException("ffprobe 爆炸"));

        await recorder.StartAsync(_channelId, RtspUrl, _recordingsRoot, "main", SegmentSeconds);
        await WaitUntilAsync(() => recorder.LastFailure is not null);

        Assert.Contains("ffprobe 爆炸", recorder.LastFailure);
        Assert.Empty(factory.Processes);
    }

    /// <summary>
    /// 停止與釋放都可能被重複觸發（DI 容器與 using 各釋放一次）。第二次不得因為已釋放的
    /// <see cref="CancellationTokenSource"/> 而丟出 <see cref="ObjectDisposedException"/>，
    /// 否則應用程式關閉時會被非預期例外打斷。
    /// </summary>
    [Fact]
    public async Task 重複停止與重複釋放不會丟出例外()
    {
        var (recorder, factory, first) = BeginFirstSegment(FirstCompletesThenPending);
        await first.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => factory.Processes.Count >= 2);

        await recorder.StopAsync();
        await recorder.StopAsync();
        await recorder.DisposeAsync();
        await recorder.DisposeAsync();

        Assert.False(recorder.IsRecording);
    }

    /// <summary>
    /// 失敗重試的退避要指數成長並且有上限。
    ///
    /// 固定 5 秒重試的後果不是「比較勤勞」，而是每次失敗都往 segments 表寫一筆
    /// corrupt：一台拔掉網路線的攝影機一天 17,280 筆，16 台就是 27 萬筆，
    /// 而 segments 同時是回放時間軸與用量統計會掃的表。這裡釘住成長與上限，
    /// 順便確保上限之後不再繼續長（否則長時間離線會把退避推到小時級）。
    /// </summary>
    [Fact]
    public void 失敗重試的退避會指數成長並停在上限()
    {
        Assert.Equal(SegmentRecorder.BaseRetryDelay, SegmentRecorder.RetryDelayFor(1));
        Assert.Equal(
            SegmentRecorder.BaseRetryDelay * 2,
            SegmentRecorder.RetryDelayFor(2));
        Assert.Equal(
            SegmentRecorder.BaseRetryDelay * 4,
            SegmentRecorder.RetryDelayFor(3));

        // 超過上限後維持不變。
        Assert.Equal(SegmentRecorder.MaxRetryDelay, SegmentRecorder.RetryDelayFor(20));
        Assert.Equal(SegmentRecorder.MaxRetryDelay, SegmentRecorder.RetryDelayFor(500));
    }

    /// <summary>
    /// 退避要讓「壞掉的攝影機一天寫幾筆 corrupt 記錄」降到可接受範圍。
    ///
    /// 一天 288 筆是上限退避（5 分鐘一次）下的量級；原本的固定 5 秒是 17,280 筆。
    /// 這個測試把改善幅度寫成陳述句，因為「大概有變少」不足以阻止有人改回固定間隔。
    /// </summary>
    [Fact]
    public void 退避上限讓離線攝影機每天的失敗記錄降到五百筆以內()
    {
        var perDay = TimeSpan.FromDays(1).Ticks / SegmentRecorder.MaxRetryDelay.Ticks;
        Assert.True(perDay <= 500, $"每天 {perDay} 筆 corrupt 記錄，超出可接受範圍。");
    }

    /// <summary>失敗次數很大時不能因為位元位移溢出而算出負的間隔。</summary>
    [Fact]
    public void 失敗次數極大時退避不會溢出成負值()
    {
        Assert.True(SegmentRecorder.RetryDelayFor(int.MaxValue) > TimeSpan.Zero);
    }

    /// <summary>
    /// 多磁碟錄影的核心：每個區段都要重新問一次「這一段寫哪一顆碟」。
    ///
    /// 若只在啟動時決定一次根目錄，主要碟寫滿之後仍會一直往同一顆寫，每次 ffmpeg 都失敗、
    /// 退避到 5 分鐘一次，而畫面照常——這正是「磁碟滿了自動續錄下一顆」要修掉的無聲失效。
    /// 這裡用會依序回兩個目錄的選擇器，確認兩個連續區段真的落在不同磁碟。
    /// </summary>
    [Fact]
    public async Task 多磁碟選擇器會讓連續區段寫到不同磁碟()
    {
        var rootA = Path.Combine(Path.GetTempPath(), $"helivms-rec-a-{Guid.NewGuid():N}");
        var rootB = Path.Combine(Path.GetTempPath(), $"helivms-rec-b-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootA);
        Directory.CreateDirectory(rootB);
        try
        {
            var factory = new FakeProcessFactory(FirstTwoCompleteThenPending);
            var completed = new List<SegmentRecord>();
            var bothDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var picked = 0;

            var recorder = new SegmentRecorder(
                _segments,
                factory,
                _ => string.Empty,
                rootSelector: () => picked++ == 0 ? rootA : rootB);
            recorder.SegmentCompleted += (_, segment) =>
            {
                lock (completed)
                {
                    completed.Add(segment);
                    if (completed.Count == 2)
                    {
                        bothDone.TrySetResult();
                    }
                }
            };

            _ = recorder.StartAsync(_channelId, RtspUrl, _recordingsRoot, "main", SegmentSeconds);
            await bothDone.Task.WaitAsync(TimeSpan.FromSeconds(10));

            lock (completed)
            {
                Assert.StartsWith(rootA, completed[0].FilePath, StringComparison.OrdinalIgnoreCase);
                Assert.StartsWith(rootB, completed[1].FilePath, StringComparison.OrdinalIgnoreCase);
            }

            await recorder.DisposeAsync();
        }
        finally
        {
            Directory.Delete(rootA, recursive: true);
            Directory.Delete(rootB, recursive: true);
        }
    }

    /// <summary>
    /// 錄影資料夾要使用注入的頻道名稱，而不是舊制 <c>ch{id}</c>：這是操作員在磁碟上認得出
    /// 「哪個資料夾是哪台攝影機」的唯一依據（M311）。名稱由排程／監看牆以同一套規則算出。
    /// </summary>
    [Fact]
    public async Task 錄影資料夾會使用注入的頻道名稱()
    {
        var completed = new TaskCompletionSource<SegmentRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new FakeProcessFactory(FirstCompletesThenPending);
        await using var recorder = new SegmentRecorder(
            _segments,
            factory,
            _ => string.Empty,
            folderNameFor: _ => "前門");
        recorder.SegmentCompleted += (_, segment) => completed.TrySetResult(segment);

        await recorder.StartAsync(_channelId, RtspUrl, _recordingsRoot, "main", SegmentSeconds);
        var segment = await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.StartsWith(
            Path.Combine(_recordingsRoot, "前門") + Path.DirectorySeparatorChar,
            segment.FilePath,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 沒有提供頻道名稱時退回舊制 <c>ch{id:000}</c>，確保未接上命名規則的呼叫端仍能錄影，
    /// 而不是因為拿不到名稱就寫到根目錄或直接失敗。
    /// </summary>
    [Fact]
    public async Task 未提供資料夾名稱時退回頻道編號目錄()
    {
        var completed = new TaskCompletionSource<SegmentRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new FakeProcessFactory(FirstCompletesThenPending);
        await using var recorder = new SegmentRecorder(_segments, factory, _ => string.Empty);
        recorder.SegmentCompleted += (_, segment) => completed.TrySetResult(segment);

        await recorder.StartAsync(_channelId, RtspUrl, _recordingsRoot, "main", SegmentSeconds);
        var segment = await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.StartsWith(
            Path.Combine(_recordingsRoot, $"ch{_channelId:000}") + Path.DirectorySeparatorChar,
            segment.FilePath,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 同一秒內起始的兩段錄影必須各寫成不同檔名，否則後段會因為 <c>File.Move</c> 撞名而整段遺失。
    ///
    /// 現場症狀：攝影機 RTSP 串流突然 EOF、ffmpeg 迅速以退出碼 0 結束時，下一段會在同一秒內開錄。
    /// 秒級檔名（<c>seg-HHmmss</c>）讓第二段的 <c>File.Move</c> 擲 IOException，被 catch 當成損壞段
    /// 刪掉——該段影片直接消失，log 只有一筆 corrupt，操作員從時間軸只看到一個缺口。
    /// 這裡用注入時鐘把兩段壓進同一秒、固定同一個 <c>HHmmss</c>，確定檔名帶毫秒且兩段都寫成 final。
    /// </summary>
    [Fact]
    public async Task 同一秒內起始的兩段不會因檔名碰撞而遺失()
    {
        var t = new DateTime(2026, 1, 2, 10, 0, 0, DateTimeKind.Utc);
        var clockCalls = 0;
        // 每段會呼叫時鐘兩次（開始、結束）。這裡讓兩段的「開始」落在同一秒、相差 50 毫秒，
        // 這正是正式環境的碰撞場景：秒級檔名相同、毫秒級不同。舊的 HHmmss 會撞名，
        // 新的 HHmmssfff 不會。
        Func<DateTime> clock = () =>
        {
            var n = Interlocked.Increment(ref clockCalls);
            return n switch
            {
                1 => t,                              // 第一段開始（10:00:00.000）
                2 => t.AddMilliseconds(10),          // 第一段結束
                3 => t.AddMilliseconds(50),          // 第二段開始（10:00:00.050，同一秒）
                4 => t.AddMilliseconds(60),          // 第二段結束
                _ => t.AddMilliseconds(60 + n),      // 之後（行程停住，不再收尾）
            };
        };

        var twoDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completedCount = 0;
        var factory = new FakeProcessFactory(FirstTwoCompleteThenPending);
        await using var recorder = new SegmentRecorder(
            _segments,
            factory,
            _ => string.Empty,
            clock: clock);
        recorder.SegmentCompleted += (_, _) =>
        {
            if (Interlocked.Increment(ref completedCount) >= 2)
            {
                twoDone.TrySetResult();
            }
        };

        await recorder.StartAsync(_channelId, RtspUrl, _recordingsRoot, "main", SegmentSeconds);
        await twoDone.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var finals = _segments.ListAllFinal();
        Assert.Equal(2, finals.Count);
        Assert.Equal(2, finals.Select(s => s.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(
            finals,
            s => Assert.Matches(@"seg-\d{9}\.mp4$", s.FilePath));
    }

    /// <summary>
    /// 區段的結束時間與長度必須反映真實收尾時刻，不能固定等於設定的分段秒數。
    ///
    /// 現場症狀：攝影機 RTSP 串流中斷時，ffmpeg 會提早以退出碼 0 結束，檔案只有幾秒，
    /// 索引卻記成整整一段（例如 60 秒）。回放時間軸因此畫出一段沒有畫面的區塊，HLS 的
    /// EXTINF 也宣稱 60 秒，播放器在檔案真正結尾之後空等。
    /// 這裡用注入時鐘模擬「只錄了 5 秒就結束」，確定索引記的是 5 秒而不是設定值。
    /// </summary>
    [Fact]
    public async Task 提早結束時區段長度取真實收尾時刻而非設定秒數()
    {
        var t = new DateTime(2026, 3, 4, 9, 0, 0, DateTimeKind.Utc);
        var clockCalls = 0;
        Func<DateTime> clock = () =>
        {
            var n = Interlocked.Increment(ref clockCalls);
            return n == 1 ? t : t.AddSeconds(5);
        };

        var completed = new TaskCompletionSource<SegmentRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new FakeProcessFactory(FirstCompletesThenPending);
        await using var recorder = new SegmentRecorder(
            _segments,
            factory,
            _ => string.Empty,
            clock: clock);
        recorder.SegmentCompleted += (_, segment) => completed.TrySetResult(segment);

        await recorder.StartAsync(_channelId, RtspUrl, _recordingsRoot, "main", SegmentSeconds);
        var segment = await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(t.AddSeconds(5), segment.EndUtc!.Value, TimeSpan.FromSeconds(1));
        Assert.Equal(5d, segment.DurationSec!.Value, 1);
        Assert.NotEqual(SegmentSeconds, segment.DurationSec!.Value);

        var stored = Assert.IsType<SegmentRecord>(_segments.Get(segment.Id));
        Assert.Equal(5d, stored.DurationSec!.Value, 1);
    }

    /// <summary>第一、第二段立即成功收尾；之後的行程停住，直到被停止或取消。</summary>
    private static FakeProcess FirstTwoCompleteThenPending(int index, ProcessStartInfo psi)
    {
        var process = new FakeProcess(psi, exitCode: 0);
        if (index < 2)
        {
            process.ProduceAndExit();
        }

        return process;
    }

    private (SegmentRecorder Recorder, FakeProcessFactory Factory, Task<SegmentRecord> First) BeginFirstSegment(
        Func<int, ProcessStartInfo, FakeProcess> make,
        Func<string, string>? audioProbe = null)
    {
        var completed = new TaskCompletionSource<SegmentRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new FakeProcessFactory(make);
        var recorder = new SegmentRecorder(_segments, factory, audioProbe ?? (_ => string.Empty));
        recorder.SegmentCompleted += (_, segment) => completed.TrySetResult(segment);
        _ = recorder.StartAsync(_channelId, RtspUrl, _recordingsRoot, "main", SegmentSeconds);
        return (recorder, factory, completed.Task);
    }

    /// <summary>第一段立即成功收尾；之後的行程停住，直到被停止或取消。</summary>
    private static FakeProcess FirstCompletesThenPending(int index, ProcessStartInfo psi)
    {
        var process = new FakeProcess(psi, exitCode: 0);
        if (index == 0)
        {
            process.ProduceAndExit();
        }

        return process;
    }

    private static string? ValueAfter(IList<string> args, string flag)
    {
        var index = args.IndexOf(flag);
        return index >= 0 && index + 1 < args.Count ? args[index + 1] : null;
    }

    private static string Sha256Of(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("等待條件成立逾時。");
            }

            await Task.Delay(20);
        }
    }

    private sealed class FakeProcessFactory : IProcessFactory
    {
        private readonly Func<int, ProcessStartInfo, FakeProcess> _make;

        public FakeProcessFactory(Func<int, ProcessStartInfo, FakeProcess> make) => _make = make;

        public List<FakeProcess> Processes { get; } = [];

        public IProcess Start(ProcessStartInfo startInfo)
        {
            var process = _make(Processes.Count, startInfo);
            Processes.Add(process);
            return process;
        }
    }

    private sealed class FakeProcess : IProcess
    {
        private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly string _outputPath;

        public FakeProcess(ProcessStartInfo startInfo, int exitCode, string stderr = "")
        {
            StartInfo = startInfo;
            _outputPath = startInfo.ArgumentList[^1];
            Output = Encoding.UTF8.GetBytes($"fake-mp4-{Guid.NewGuid():N}");
            ExitCode = exitCode;
            StandardError = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(stderr)));
            StandardOutput = new StreamReader(new MemoryStream());
        }

        public ProcessStartInfo StartInfo { get; }

        public byte[] Output { get; }

        public StreamReader StandardOutput { get; }

        public StreamReader StandardError { get; }

        public bool HasExited { get; private set; }

        public int ExitCode { get; }

        public int KillCount { get; private set; }

        /// <summary>模擬 ffmpeg 寫出（部分）檔案後結束；失敗情境也會留下暫存檔供驗證刪除。</summary>
        public void ProduceAndExit()
        {
            File.WriteAllBytes(_outputPath, Output);
            HasExited = true;
            _exited.TrySetResult();
        }

        public async Task WaitForExitAsync(CancellationToken cancellationToken = default)
            => await _exited.Task.WaitAsync(cancellationToken);

        public void Kill()
        {
            KillCount++;
            HasExited = true;
            _exited.TrySetCanceled();
        }

        /// <summary>行程是否已被釋放；用來確認錄影收尾會歸還管線控制代碼。</summary>
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
