using System.Diagnostics;
using System.Text;
using HeliVMS.Media;
using HeliVMS.Recording;
using HeliVMS.Shared.Models;

namespace HeliVMS.Recording.Tests;

/// <summary>
/// 回放工作單元（<see cref="PlaybackSession"/>）的行程與事件行為。
///
/// 這個類別把 ffprobe 與 ffmpeg 都藏在 static <c>Process.Start</c> 之後，過去只能整台
/// 機器手動播一段來確認；但它的失敗模式全都很安靜——seek 超過段長會卡住不發事件、
/// 停止後行程沒被砍掉會留下孤兒佔用檔案、讀不到半格時被當成正常播完。
/// 這些都不會拋例外，只會讓時間軸停在某一格或系統慢慢耗盡資源。
///
/// 這裡以可注入的行程工廠與探測委派，直接斷言結束原因（completed）與程序終止。
/// </summary>
public sealed class PlaybackSessionTests : IDisposable
{
    private static readonly StreamProbeInfo Info = new(2, 2, "h264", null, 25);

    private readonly List<string> _tempFiles = [];

    public void Dispose()
    {
        foreach (var path in _tempFiles)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>
    /// 檔案不存在時必須立刻以「未完成」結束，且不得啟動 ffmpeg：
    /// 索引與磁碟不同步（例如保留策略剛刪檔）時，播放器要能分辨這是失敗而非播完。
    /// </summary>
    [Fact]
    public async Task 檔案不存在時直接回報未完成且不啟動ffmpeg()
    {
        var factory = new FakeProcessFactory(_ => new FakeProcess(new MemoryStream()));
        await using var session = new PlaybackSession(
            Segment(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.mp4"), 10),
            processFactory: factory,
            probe: _ => Info);

        PlaybackEndedEventArgs? ended = null;
        session.Ended += (_, e) => ended = e;

        await session.PlayAsync();

        Assert.NotNull(ended);
        Assert.False(ended!.Completed);
        Assert.Empty(factory.Started);
    }

    /// <summary>
    /// 起點已越過段尾（seek 大於長度）時，必須直接以「已完成」結束：這是使用者把游標
    /// 拖到最末端，屬正常結束，而非錯誤。少了這條守衛會啟動一個註定空轉的 ffmpeg。
    /// </summary>
    [Fact]
    public async Task 起點已超過段長度時直接以完成結束()
    {
        var factory = new FakeProcessFactory(_ => new FakeProcess(new MemoryStream()));
        await using var session = new PlaybackSession(
            Segment(CreateEmptyFile(), 10),
            processFactory: factory,
            probe: _ => Info);

        PlaybackEndedEventArgs? ended = null;
        session.Ended += (_, e) => ended = e;

        await session.PlayAsync(seekSeconds: 15);

        Assert.NotNull(ended);
        Assert.True(ended!.Completed);
        Assert.Empty(factory.Started);
    }

    /// <summary>
    /// 參數是回放協定的一部分：<c>-ss</c> 決定段內起點、<c>-t</c> 為剩餘長度、
    /// <c>-f rawvideo -pix_fmt bgr24</c> 是給 <c>WriteableBitmap</c> 的原始影格格式、
    /// <c>pipe:1</c> 才會走標準輸出。任一改動都會造成「連上但沒有畫面」且沒有錯誤訊息。
    /// </summary>
    [Fact]
    public async Task ffmpeg參數需帶段內起點與rawvideo格式()
    {
        var file = CreateEmptyFile();
        var factory = new FakeProcessFactory(_ => new FakeProcess(new MemoryStream()));
        await using var session = new PlaybackSession(
            Segment(file, 10),
            processFactory: factory,
            probe: _ => Info);

        await session.PlayAsync(seekSeconds: 3);

        var args = Assert.Single(factory.Started).StartInfo.ArgumentList;
        Assert.Equal("3", ValueAfter(args, "-ss"));
        Assert.Equal("7", ValueAfter(args, "-t"));
        Assert.Equal(file, ValueAfter(args, "-i"));
        Assert.Equal("rawvideo", ValueAfter(args, "-f"));
        Assert.Equal("bgr24", ValueAfter(args, "-pix_fmt"));
        Assert.Equal("pipe:1", args[^1]);
    }

    /// <summary>
    /// 讀到一整格時要發出一次 <see cref="PlaybackSession.FrameDecoded"/>，並在管道讀完後
    /// 以「已完成」結束。幀數（FrameIndex）是時間標記與進度條的唯一依據，漏算會讓畫面
    /// 與時間軸錯位。
    /// </summary>
    [Fact]
    public async Task 解出一幀後串流結束會回報完成()
    {
        var pixels = new byte[Info.Width * Info.Height * 3];
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)i;
        }

        var factory = new FakeProcessFactory(_ => new FakeProcess(new MemoryStream(pixels)));
        await using var session = new PlaybackSession(
            Segment(CreateEmptyFile(), 10),
            processFactory: factory,
            probe: _ => Info);

        var frames = new List<VideoFrame>();
        PlaybackEndedEventArgs? ended = null;
        session.FrameDecoded += (_, frame) => frames.Add(frame);
        session.Ended += (_, e) => ended = e;

        await session.PlayAsync();

        var frame = Assert.Single(frames);
        Assert.Equal(pixels, frame.Pixels);
        Assert.Equal(Info.Width, frame.Width);
        Assert.Equal(Info.Height, frame.Height);
        Assert.Equal(1, session.FramesRead);
        Assert.Equal(1, session.FrameIndex);
        Assert.True(ended!.Completed);
        Assert.False(session.IsPlaying);
    }

    /// <summary>
    /// <see cref="PlaybackSession.Stop"/> 必須終止進行中的 ffmpeg，並以「未完成」結束。
    /// 若只取消讀取而不砍行程，被佔用的區段會讓之後的保留策略刪不掉檔（Windows 檔案鎖），
    /// 這是使用者回報的孤兒行程症狀。
    /// </summary>
    [Fact]
    public async Task 停止播放會終止行程並回報未完成()
    {
        var factory = new FakeProcessFactory(_ => new FakeProcess(new BlockingReadStream()));
        await using var session = new PlaybackSession(
            Segment(CreateEmptyFile(), 10),
            processFactory: factory,
            probe: _ => Info);

        PlaybackEndedEventArgs? ended = null;
        session.Ended += (_, e) => ended = e;

        var playTask = session.PlayAsync();
        await WaitUntilAsync(() => session.IsPlaying);

        session.Stop();
        await playTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(ended);
        Assert.False(ended!.Completed);
        Assert.True(Assert.Single(factory.Started).KillCount >= 1);
        Assert.False(session.IsPlaying);
    }

    private SegmentRecord Segment(string filePath, double durationSec) => new()
    {
        Id = 1,
        ChannelId = 1,
        Stream = "main",
        StartUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        FilePath = filePath,
        DurationSec = durationSec,
        Status = SegmentStatus.Final,
    };

    private string CreateEmptyFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"helivms-play-{Guid.NewGuid():N}.mp4");
        File.WriteAllBytes(path, []);
        _tempFiles.Add(path);
        return path;
    }

    private static string? ValueAfter(IList<string> args, string flag)
    {
        var index = args.IndexOf(flag);
        return index >= 0 && index + 1 < args.Count ? args[index + 1] : null;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
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
        private readonly Func<ProcessStartInfo, FakeProcess> _make;

        public FakeProcessFactory(Func<ProcessStartInfo, FakeProcess> make) => _make = make;

        public List<FakeProcess> Started { get; } = [];

        public IProcess Start(ProcessStartInfo startInfo)
        {
            var process = _make(startInfo);
            process.StartInfo = startInfo;
            Started.Add(process);
            return process;
        }
    }

    private sealed class FakeProcess : IProcess
    {
        public FakeProcess(Stream standardOutput, int exitCode = 0)
        {
            StandardOutput = new StreamReader(standardOutput, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
            StandardError = new StreamReader(new MemoryStream(), Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
            ExitCode = exitCode;
        }

        public StreamReader StandardOutput { get; }

        public StreamReader StandardError { get; }

        public ProcessStartInfo StartInfo { get; set; } = new();

        public bool HasExited { get; private set; }

        public int ExitCode { get; }

        public int KillCount { get; private set; }

        public Task WaitForExitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void Kill()
        {
            KillCount++;
            HasExited = true;
        }

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>永不回傳資料、只在取消時結束的串流，用來驗證停止流程。</summary>
    private sealed class BlockingReadStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }
}
