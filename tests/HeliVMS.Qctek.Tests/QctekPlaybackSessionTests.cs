using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using HeliVMS.Qctek.Live;
using HeliVMS.Qctek.Nemo;

namespace HeliVMS.Qctek.Tests;

/// <summary>
/// QCC 遠端回放通道：段與段的接續、以及 FRAM 上的錄影時刻。
/// </summary>
/// <remarks>
/// <para>
/// 這兩件事在畫面上都不會報錯——QCC 只會「播完一段就停住」或「進度條走的時間和畫面
/// 對不上」，兩邊日誌都乾淨。所以用 loopback 的 <see cref="TcpListener"/> 真的跑一次
/// <see cref="QctekPlaybackSession.RunSync"/>，把寫出去的 FRAM 讀回來驗時戳。
/// </para>
/// <para>
/// 假的 H.264 素材只需要 NAL 型別正確：SPS／PPS 建立參數集，接著 N 個 slice 的第一個
/// macroblock bit（<c>content[1] &amp; 0x80</c>）成立，分組器才會把上一格收成一個 AU。
/// </para>
/// </remarks>
public sealed class QctekPlaybackSessionTests
{
    // ── 測試素材 ────────────────────────────────────────────────────────────

    private static byte[] SpSc() => [0x00, 0x00, 0x00, 0x01, 0x67, 0x42, 0x00, 0x1f];
    private static byte[] PpSc() => [0x00, 0x00, 0x00, 0x01, 0x68, 0xce, 0x3c, 0x80];
    private static byte[] IdrSc() => [0x00, 0x00, 0x00, 0x01, 0x65, 0x88, 0x84, 0x11];
    private static byte[] SliceSc() => [0x00, 0x00, 0x00, 0x01, 0x41, 0x9a, 0x22, 0x33];

    /// <summary>一個關鍵影格＋<paramref name="frames"/>−1 個 P 影格（＝剛好 frames 個 AU）。</summary>
    private static byte[] AnnexB(int frames)
    {
        var payload = new List<byte>();
        payload.AddRange(SpSc());
        payload.AddRange(PpSc());
        for (var i = 0; i < frames; i++)
        {
            payload.AddRange(i == 0 ? IdrSc() : SliceSc());
        }

        return [.. payload];
    }

    private sealed class FakeSource(byte[] annexB) : IQctekLiveSource
    {
        private readonly MemoryStream _stream = new(annexB);

        public int Disposed { get; private set; }

        public Stream Output => _stream;

        public ValueTask DisposeAsync()
        {
            Disposed++;
            _stream.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class PlaybackContext : IQctekDeviceContext
    {
        public IReadOnlyList<QctekPlaybackSegment> Segments { get; init; } = [];

        /// <summary>回放通道實際問過的「從這一刻找段」（依序）。</summary>
        public ConcurrentQueue<DateTime> CoverLookups { get; } = new();

        public ConcurrentQueue<DateTime> FromLookups { get; } = new();

        public Func<string, IQctekLiveSource?>? SourceFactory { get; init; }

        public string ServerVersion => "QCV8001";

        public int ChannelCount => 1;

        public IReadOnlyList<string> ChannelNames => ["攝影機001"];

        public int MaxStreams => 2;

        public bool HasVnc => false;

        public bool TryAuthenticate(string user, string password) => true;

        public bool IsKnownUser(string user) => true;

        public IQctekLiveSource? CreateLiveSource(int channelIndex, int subStream) => null;

        public IReadOnlyList<bool> GetRecordingMonthDays(int channelIndex, int year, int month) => new bool[31];

        public IReadOnlyList<byte> GetRecordingHourMatrix(int channelIndex, int year, int month) => new byte[768];

        public QctekPlaybackSegment? FindSegmentCovering(int channelIndex, DateTime fromUtc)
        {
            CoverLookups.Enqueue(fromUtc);
            return Segments.FirstOrDefault(segment => segment.StartUtc <= fromUtc && fromUtc < EndOf(segment));
        }

        public QctekPlaybackSegment? FindSegmentFrom(int channelIndex, DateTime fromUtc)
        {
            FromLookups.Enqueue(fromUtc);
            return Segments.FirstOrDefault(segment => segment.StartUtc >= fromUtc);
        }

        public QctekEventPage SearchEvents(QctekEventSearchRequest query, int offset, int limit)
            => QctekEventPage.Empty;

        public IQctekLiveSource? CreatePlaybackSource(string filePath) => SourceFactory?.Invoke(filePath);

        /// <summary>
        /// 段尾的口徑必須與產品程式碼的 <c>SegmentEndOf</c> 一致（檔案時間戳優先、
        /// 索引段長只接受合理值）——這個 stub 就是測試的判準，寫成「永遠相信索引」
        /// 會讓壞值測試變成無限迴圈（測到的其實是 stub 自己）。
        /// </summary>
        private static DateTime EndOf(QctekPlaybackSegment segment)
        {
            if (segment.FrameTimes is { Count: > 0 } times && times[^1] > 0)
            {
                return segment.StartUtc.AddSeconds(times[^1]);
            }

            return segment.StartUtc.AddSeconds(segment.DurationSeconds is > 0 and <= 3660
                ? segment.DurationSeconds
                : 300);
        }
    }

    // ── 通道測試工具 ────────────────────────────────────────────────────────

    /// <summary>真的跑一次回放通道（loopback TCP），讀回 <paramref name="expectedFrames"/> 個 FRAM。</summary>
    private static async Task<List<Frame>> PlayAsync(PlaybackContext context, int expectedFrames)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            using var client = new TcpClient();
            var connecting = client.ConnectAsync(
                IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, timeout.Token);
            using var accepted = await listener.AcceptTcpClientAsync(timeout.Token);
            await connecting;

            var serverStream = accepted.GetStream();
            var run = Task.Run(() =>
                QctekPlaybackSession.RunSync(serverStream, context.Segments[0], context, null));

            var frames = new List<Frame>();
            while (frames.Count < expectedFrames)
            {
                frames.Add(await ReadFrameAsync(client.GetStream(), timeout.Token));
            }

            await run.WaitAsync(TimeSpan.FromSeconds(10), timeout.Token);
            return frames;
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// 真的跑一次回放通道，並把幀流<b>讀到對端關閉</b>為止——用來驗「最後一格有沒有送出去」：
    /// 少送一格時迴圈不會報錯，只是少一幀，等固定幀數的版本會卡到逾時才紅。
    /// </summary>
    private static async Task<List<Frame>> PlayToEndAsync(PlaybackContext context)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            using var client = new TcpClient();
            var connecting = client.ConnectAsync(
                IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, timeout.Token);
            using var accepted = await listener.AcceptTcpClientAsync(timeout.Token);
            await connecting;

            var run = Task.Run(() =>
            {
                try
                {
                    QctekPlaybackSession.RunSync(accepted.GetStream(), context.Segments[0], context, null);
                }
                finally
                {
                    // 回放結束就關閉伺服器端：客戶端才看得到 EOF（測試要讀到「沒有下一幀」）。
                    accepted.GetStream().Dispose();
                }
            });

            var frames = new List<Frame>();
            try
            {
                while (true)
                {
                    frames.Add(await ReadFrameAsync(client.GetStream(), timeout.Token));
                }
            }
            catch (EndOfStreamException)
            {
                // 對端播完並關閉連線：正常的結束方式。
            }

            await run.WaitAsync(TimeSpan.FromSeconds(10), timeout.Token);
            return frames;
        }
        finally
        {
            listener.Stop();
        }
    }

    private sealed record Frame(int Channel, ushort Status, byte[] Media, byte[] Payload)
    {
        public long UnixSeconds => BinaryPrimitives.ReadInt64LittleEndian(Media.AsSpan(0x18));

        public uint MicroSeconds => BinaryPrimitives.ReadUInt32LittleEndian(Media.AsSpan(0x14));
    }

    private static async Task<Frame> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[8];
        await stream.ReadExactlyAsync(header, ct);
        var channel = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4));
        var status = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6));
        if (status != 0)
        {
            return new Frame(channel, status, [], []);
        }

        var media = new byte[QctekLivePackets.MediaHeaderLength];
        await stream.ReadExactlyAsync(media, ct);
        var video = BinaryPrimitives.ReadInt32LittleEndian(media.AsSpan(0x08));
        var block1 = BinaryPrimitives.ReadInt32LittleEndian(media.AsSpan(0x0c));
        var block2 = BinaryPrimitives.ReadInt32LittleEndian(media.AsSpan(0x10));
        var payload = new byte[block1 + block2 + video];
        await stream.ReadExactlyAsync(payload, ct);
        return new Frame(channel, status, media, payload);
    }

    private static void AssertInstant(DateTime expectedUtc, Frame frame)
    {
        var expected = new DateTimeOffset(expectedUtc);
        Assert.Equal(expected.ToUnixTimeSeconds(), frame.UnixSeconds);
        Assert.Equal((uint)(expected.Millisecond * 1000), frame.MicroSeconds);
    }

    private static readonly DateTime Start = new(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc);

    // ── 段與段 ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task 段尾以段自己的長度接下一段而不是固定五分鐘()
    {
        // 分段長度可設 5–600 秒（預設 300）。舊版把「下一段從哪裡接」硬寫成 300 秒：
        // 設 60 秒時每播完一段就往前跳 4 分鐘，被跳過的整段錄影永遠播不到，
        // 而畫面上只看到「跳到別的時刻」。
        var first = new QctekPlaybackSegment(0, Start, @"C:\rec\a.mp4", DurationSeconds: 60);
        var second = new QctekPlaybackSegment(0, Start.AddSeconds(60), @"C:\rec\b.mp4", DurationSeconds: 60);
        var context = new PlaybackContext
        {
            Segments = [first, second],
            SourceFactory = _ => new FakeSource(AnnexB(frames: 1)),
        };

        var frames = await PlayAsync(context, expectedFrames: 2);

        Assert.Equal(2, frames.Count);
        Assert.Contains(Start.AddSeconds(60), context.CoverLookups);
        Assert.DoesNotContain(Start.AddSeconds(300), context.CoverLookups);
        AssertInstant(second.StartUtc, frames[1]);
    }

    [Fact]
    public async Task 段長未知時退回五分鐘接段()
    {
        // 舊索引／沒有 end_time 的段：沒有段長可用時維持原本的 5 分鐘假設，
        // 不要讓「不知道」變成「留在同一段重播」。
        var first = new QctekPlaybackSegment(0, Start, @"C:\rec\a.mp4");
        var second = new QctekPlaybackSegment(0, Start.AddSeconds(300), @"C:\rec\b.mp4");
        var context = new PlaybackContext
        {
            Segments = [first, second],
            SourceFactory = _ => new FakeSource(AnnexB(frames: 1)),
        };

        var frames = await PlayAsync(context, expectedFrames: 2);

        Assert.Equal(2, frames.Count);
        Assert.Contains(Start.AddSeconds(300), context.CoverLookups);
    }

    [Fact]
    public async Task 段長是壞掉的牆鐘值時退回五分鐘而不是跳到好幾小時後()
    {
        // 索引的 duration_sec 曾是牆鐘（程序排程時間）：睡眠或校時之後會長成 6853 秒
        // 這種值（實測存在）。照它接段的話，下一段直接跳到好幾個小時之後——
        // QCC 的時間軸上是一大片空白，而且「跳過去」的那一段其實存在。
        var first = new QctekPlaybackSegment(0, Start, @"C:\rec\a.mp4", DurationSeconds: 6853);
        var second = new QctekPlaybackSegment(0, Start.AddSeconds(300), @"C:\rec\b.mp4", DurationSeconds: 60);
        var context = new PlaybackContext
        {
            Segments = [first, second],
            SourceFactory = _ => new FakeSource(AnnexB(frames: 1)),
        };

        var frames = await PlayAsync(context, expectedFrames: 2);

        Assert.Equal(2, frames.Count);
        Assert.Contains(Start.AddSeconds(300), context.CoverLookups);
        Assert.DoesNotContain(Start.AddSeconds(6853), context.CoverLookups);
    }

    [Fact]
    public async Task 段尾以檔案最後一格為準而不是索引的段長()
    {
        // 索引段長是牆鐘（含連線暖機與攝影機停頓），檔案時間戳才是內容：
        // 實測健康攝影機差 0.6 秒，停頓過的可差到 12 秒。用索引接段會讓下一段的
        // FRAM 時戳憑空往前跳，QCC 的進度條與 OSD 都與畫面不一致。
        var first = new QctekPlaybackSegment(
            0, Start, @"C:\rec\a.mp4", DurationSeconds: 400, FrameTimes: [0.0, 2.0]);
        var second = new QctekPlaybackSegment(0, Start.AddSeconds(2), @"C:\rec\b.mp4", DurationSeconds: 60);
        var context = new PlaybackContext
        {
            Segments = [first, second],
            SourceFactory = _ => new FakeSource(AnnexB(frames: 1)),
        };

        var frames = await PlayAsync(context, expectedFrames: 2);

        Assert.Equal(2, frames.Count);
        Assert.Contains(Start.AddSeconds(2), context.CoverLookups);
        Assert.DoesNotContain(Start.AddSeconds(400), context.CoverLookups);
    }

    // ── FRAM 時戳 ───────────────────────────────────────────────────────────

    [Fact]
    public async Task 回放時戳取自檔案自己的封包時間而不是影格序號乘33毫秒()
    {
        // 實測：宣稱 15fps 的錄影檔實際只送 12.5fps（間隔 67ms 與 100ms 混用）。
        // 用「影格序號 × 33ms」蓋章，第 4 格的宣稱時刻比真實內容慢 2.4 倍——
        // QCC 的進度條與 OSD 都在騙人，而畫面本身完全正常。
        var times = new[] { 0.0, 0.067, 0.1, 0.4 };
        var segment = new QctekPlaybackSegment(
            0,
            Start.AddMilliseconds(789),
            @"C:\rec\a.mp4",
            DurationSeconds: 60,
            FrameTimes: times);
        var context = new PlaybackContext
        {
            Segments = [segment],
            SourceFactory = _ => new FakeSource(AnnexB(frames: times.Length)),
        };

        var frames = await PlayAsync(context, expectedFrames: times.Length);

        Assert.Equal(times.Length, frames.Count);
        for (var i = 0; i < times.Length; i++)
        {
            AssertInstant(segment.StartUtc.AddSeconds(times[i]), frames[i]);
        }
    }

    [Fact]
    public async Task 讀不到影格時間時退回三三毫秒節奏()
    {
        // ffprobe 不在、檔案壞掉：回放照播，只是時刻退回近似值——
        // 寧可時刻粗略，也不要整個回放開不起來。
        var segment = new QctekPlaybackSegment(0, Start, @"C:\rec\a.mp4", DurationSeconds: 60);
        var context = new PlaybackContext
        {
            Segments = [segment],
            SourceFactory = _ => new FakeSource(AnnexB(frames: 2)),
        };

        var frames = await PlayAsync(context, expectedFrames: 2);

        AssertInstant(Start, frames[0]);
        AssertInstant(Start.AddMilliseconds(33), frames[1]);
    }

    // ── 段尾與收尾 ──────────────────────────────────────────────────────────

    [Fact]
    public async Task 每段的最後一格也會送出去()
    {
        // 分組器要看到「下一個 AU 的 slice」才知道上一個 AU 結束；檔案最後一格的
        // 後面沒有東西，只能靠 Flush 收尾。少了它每一段都少一格，而且沒有任何錯誤：
        // 60 秒的段只少 1 幀看不出來，但 QCC 每到段尾就停半拍，接段時畫面會頓一下。
        var times = new[] { 0.0, 0.1, 0.2 };
        var segment = new QctekPlaybackSegment(
            0, Start, @"C:\rec\a.mp4", DurationSeconds: 60, FrameTimes: times);
        var context = new PlaybackContext
        {
            Segments = [segment],
            SourceFactory = _ => new FakeSource(AnnexB(frames: times.Length)),
        };

        var frames = await PlayToEndAsync(context);

        Assert.Equal(times.Length, frames.Count);
        AssertInstant(Start.AddSeconds(times[^1]), frames[^1]);
    }

    // ── 失敗路徑 ────────────────────────────────────────────────────────────

    [Fact]
    public async Task 來源建不起來時不會卡在同一段重試()
    {
        // ffmpeg 不在 PATH 或檔案被鎖住：舊版回 segment.StartUtc，外層重新找到同一段、
        // 再一次建立失敗——沒有輸出的忙迴圈，一整顆 CPU 就這樣燒著，日誌每毫秒一行。
        var context = new PlaybackContext
        {
            Segments = [new QctekPlaybackSegment(0, Start, @"C:\rec\a.mp4", DurationSeconds: 60)],
            SourceFactory = _ => null,
        };

        await PlayAsync(context, expectedFrames: 0);

        Assert.True(context.CoverLookups.Count <= 2, "來源建立失敗後回放迴圈仍在同一段打轉。");
    }
}
