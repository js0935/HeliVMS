using HeliVMS.Alarms;
using HeliVMS.Shared.Models;

namespace HeliVMS.Alarms.Tests;

/// <summary>
/// 影片摘要外部縫（M294 §14.1 #8）：<see cref="IVideoSummaryProvider"/> ＋決定性後備
/// <see cref="CollageVideoSummaryProvider"/>。後備保證與 M63 拼貼建構器逐一致，
/// 讓真機換模型實作時呼叫端與既有測試都不必改。
/// </summary>
public class VideoSummaryProviderTests
{
    private static string TempDir(Guid id, string name)
        => Path.Combine(Path.GetTempPath(), $"helivms-summary-{name}-{id:N}");

    private static byte[] Solid(int w, int h, byte b, byte g, byte r)
    {
        var px = new byte[w * h * 3];
        for (var i = 0; i < w * h; i++)
        {
            px[i * 3] = b;
            px[i * 3 + 1] = g;
            px[i * 3 + 2] = r;
        }

        return px;
    }

    private static AlarmEventRecord Event(string type, DateTime utc, string? snapshot = null) => new()
    {
        ChannelId = 1,
        EventType = type,
        StartUtc = utc,
        SnapshotPath = snapshot,
    };

    private static string WriteSnapshot(string dir, string name, byte b = 0, byte g = 0, byte r = 255)
    {
        var path = Path.Combine(dir, name);
        BmpFile.Save(Solid(64, 36, b, g, r), 64, 36, path);
        return path;
    }

    [Fact]
    public void 預設供應商的識別碼非空且穩定()
    {
        var a = CollageVideoSummaryProvider.Default;
        var b = new CollageVideoSummaryProvider();
        Assert.False(string.IsNullOrWhiteSpace(a.ModelId));
        Assert.Equal(a.ModelId, b.ModelId);
    }

    [Fact]
    public void 後備供應商輸出與拼貼建構器逐一致()
    {
        var id = Guid.NewGuid();
        var dir = TempDir(id, "parity");
        var viaProvider = Path.Combine(dir, "provider");
        var viaBuilder = Path.Combine(dir, "builder");
        Directory.CreateDirectory(dir);
        try
        {
            var snap = WriteSnapshot(dir, "a.bmp", 12, 34, 56);
            var t0 = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);
            var events = new[] { Event("person", t0, snap), Event("car", t0.AddSeconds(30), snap) };
            var request = new SynopsisRequest(1, t0.AddMinutes(-1), t0.AddHours(1));

            var providerResult = CollageVideoSummaryProvider.Default.Generate(request, events, viaProvider);
            var builderResult = SynopsisBuilder.Build(request, events, viaBuilder);

            Assert.NotNull(providerResult);
            Assert.NotNull(builderResult);
            Assert.Equal(builderResult!.SummaryText, providerResult!.SummaryText);
            Assert.Equal(builderResult.Manifest.FrameCount, providerResult.Manifest.FrameCount);
            Assert.Equal(builderResult.Manifest.Rows, providerResult.Manifest.Rows);
            Assert.Equal(builderResult.Manifest.Columns, providerResult.Manifest.Columns);
            Assert.Equal(
                builderResult.Manifest.Cells.Select(c => (c.Index, c.EventType, c.Utc)),
                providerResult.Manifest.Cells.Select(c => (c.Index, c.EventType, c.Utc)));
            Assert.Equal(
                File.ReadAllBytes(builderResult.SheetPath),
                File.ReadAllBytes(providerResult.SheetPath));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void 沒有可用快照時回傳_null_且不寫任何檔()
    {
        var id = Guid.NewGuid();
        var dir = TempDir(id, "empty");
        Directory.CreateDirectory(dir);
        try
        {
            var t0 = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);
            var events = new[] { Event("person", t0), Event("car", t0.AddMinutes(1)) };

            var result = CollageVideoSummaryProvider.Default.Generate(
                new SynopsisRequest(1, t0.AddHours(-1), t0.AddHours(1)),
                events,
                dir);

            Assert.Null(result);
            Assert.Empty(Directory.GetFiles(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void 後備供應商忠實轉發請求參數()
    {
        // 委派若傳錯參數（例如漏掉 EventTypes／MaxFrames，或用了預設縮圖尺寸），
        // 這裡就會與直接呼叫建構器的結果不一致。
        var id = Guid.NewGuid();
        var dir = TempDir(id, "params");
        var viaProvider = Path.Combine(dir, "provider");
        var viaBuilder = Path.Combine(dir, "builder");
        Directory.CreateDirectory(dir);
        try
        {
            var a = WriteSnapshot(dir, "a.bmp");
            var b = WriteSnapshot(dir, "b.bmp", 9, 9, 9);
            var t0 = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);
            var events = new[]
            {
                Event("person", t0, a),
                Event("car", t0.AddMinutes(1), b),
                Event("person", t0.AddMinutes(2), b),
            };
            var request = new SynopsisRequest(7, t0.AddHours(-1), t0.AddHours(1))
            {
                ThumbWidth = 160,
                ThumbHeight = 90,
                Columns = 2,
                MaxFrames = 2,
                EventTypes = ["person"],
            };

            var viaP = CollageVideoSummaryProvider.Default.Generate(request, events, viaProvider);
            var viaB = SynopsisBuilder.Build(request, events, viaBuilder);

            Assert.NotNull(viaP);
            Assert.NotNull(viaB);
            Assert.Equal(2, viaP!.Manifest.FrameCount);
            Assert.Equal(1, viaP.Manifest.Rows);
            Assert.Equal(2, viaP.Manifest.Columns);
            Assert.Equal(160, viaP.Manifest.ThumbWidth);
            Assert.All(viaP.Manifest.Cells, c => Assert.Equal("person", c.EventType));
            Assert.Equal(viaB!.SummaryText, viaP.SummaryText);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void 自訂供應商可取代後備()
    {
        // 縫的意義：呼叫端只認 IVideoSummaryProvider，注入別的實作就換掉整個產生邏輯。
        var fake = new FakeVideoSummaryProvider();
        IVideoSummaryProvider provider = fake;

        var result = provider.Generate(
            new SynopsisRequest(3, DateTime.UnixEpoch, DateTime.UtcNow),
            [],
            Path.GetTempPath());

        Assert.Equal("fake-model", provider.ModelId);
        Assert.NotNull(result);
        Assert.Equal("自訂摘要", result!.SummaryText);
        Assert.Equal(1, fake.Calls);
    }

    private sealed class FakeVideoSummaryProvider : IVideoSummaryProvider
    {
        public int Calls { get; private set; }

        public string ModelId => "fake-model";

        public SynopsisResult? Generate(SynopsisRequest request, IReadOnlyList<AlarmEventRecord> events, string outputDir)
        {
            Calls++;
            var manifest = new SynopsisManifest(
                request.ChannelId, request.FromUtc, request.ToUtc,
                request.ThumbWidth, request.ThumbHeight, request.Columns, 0,
                request.FromUtc, request.ToUtc, 0, 0, false, "", "", []);
            return new SynopsisResult(manifest, "", "", "自訂摘要");
        }
    }
}
