using System.Net;
using System.Net.Http.Headers;
using System.Text;
using HeliVMS.Shared.Models;
using HeliVMS.Storage;
using HeliVMS.WebApi;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace HeliVMS.Api.Tests;

/// <summary>
/// M239（§14.3 串流 P0）：遠程回放的 HLS 端點。
/// 這裡啟一個獨立的主機（自己的資料庫與錄影根目錄），因為串流有自己的路徑政策，
/// 不該和 <see cref="ApiFactory"/> 的保留／清理行為混在一起。
/// </summary>
public sealed class StreamApiFactory : WebApplicationFactory<Program>
{
    public const string Key = "helivms-stream-key";

    public string DbPath { get; } = Path.Combine(Path.GetTempPath(), $"helivms-stream-{Guid.NewGuid():N}.db");

    /// <summary>錄影根目錄；只有落在這裡的分段檔可以透過 API 讀取。</summary>
    public string RecordingsDir { get; } = Path.Combine(Path.GetTempPath(), $"helivms-stream-rec-{Guid.NewGuid():N}");

    /// <summary>允許根目錄之外的暫存目錄，用來驗證路徑政策會擋下來。</summary>
    public string OutsideDir { get; } = Path.Combine(Path.GetTempPath(), $"helivms-stream-outside-{Guid.NewGuid():N}");

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        Directory.CreateDirectory(RecordingsDir);
        Directory.CreateDirectory(OutsideDir);
        builder.UseSetting("HELIVMS_DB", DbPath);
        builder.UseSetting(RecordedSegmentPolicy.RecordingsRootConfigKey, RecordingsDir);
        builder.UseSetting("HELIVMS_API_KEY", Key);
        builder.UseSetting("HELIVMS_LICENSE_PUBLIC_KEY", TestLicenseSeeder.PublicPem);

        using var store = new SqliteStore(DbPath);
        store.Initialize();
        TestLicenseSeeder.Apply(
            store,
            LicenseFeatures.All.ToArray(),
            new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc),
            expiresUtc: null);
    }

    public string WriteSegment(string directory, string name, byte[] bytes)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing)
        {
            return;
        }

        foreach (var dir in new[] { RecordingsDir, OutsideDir })
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        try
        {
            File.Delete(DbPath);
        }
        catch (IOException)
        {
        }
    }
}

public sealed class StreamApiTests : IClassFixture<StreamApiFactory>, IDisposable
{
    private readonly StreamApiFactory _factory;

    public StreamApiTests(StreamApiFactory factory) => _factory = factory;

    public void Dispose() => GC.SuppressFinalize(this);

    private HttpClient Client(bool withKey = true)
    {
        var client = _factory.CreateClient();
        if (withKey)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", StreamApiFactory.Key);
        }

        return client;
    }

    private SegmentRepository Segments => _factory.Services.GetRequiredService<SegmentRepository>();

    private long SeedSegment(string directory, DateTime startUtc, DateTime endUtc, out byte[] fileBytes)
    {
        fileBytes = SyntheticFmp4();
        var path = _factory.WriteSegment(directory, $"seg-{Guid.NewGuid():N}.mp4", fileBytes);
        var id = Segments.BeginSegment(1, "main", path, startUtc);
        Segments.CompleteSegment(id, endUtc, fileBytes.Length, (endUtc - startUtc).TotalSeconds, "sha");
        return id;
    }

    [Fact]
    public async Task Playlist_RequiresApiKey()
    {
        using var client = Client(withKey: false);

        var response = await client.GetAsync(
            $"/api/stream/1/playlist.m3u8?stream=main&from={HttpUtility(DateTime.UtcNow.AddHours(-1))}&to={HttpUtility(DateTime.UtcNow)}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Playlist_EmitsVodPlaylistWithMapAndSegments()
    {
        var from = DateTime.UtcNow.Date.AddHours(6);
        var id = SeedSegment(_factory.RecordingsDir, from.AddMinutes(1), from.AddMinutes(1).AddSeconds(15), out _);
        using var client = Client();

        var response = await client.GetAsync(
            $"/api/stream/1/playlist.m3u8?stream=main&from={HttpUtility(from)}&to={HttpUtility(from.AddHours(1))}");
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HlsPlaylist.ContentType, response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("#EXTM3U", text);
        Assert.Contains("#EXT-X-PLAYLIST-TYPE:VOD", text);
        Assert.Contains(HlsPlaylist.InitUri(id), text);
        Assert.Contains(HlsPlaylist.MediaUri(id), text);
        Assert.Contains("#EXT-X-ENDLIST", text);
    }

    [Fact]
    public async Task Playlist_EmptyWindowIs404()
    {
        using var client = Client();
        var from = DateTime.UtcNow.Date.AddYears(-3);

        var response = await client.GetAsync(
            $"/api/stream/1/playlist.m3u8?stream=main&from={HttpUtility(from)}&to={HttpUtility(from.AddHours(1))}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Playlist_WindowWiderThanADayIs400()
    {
        using var client = Client();
        var from = DateTime.UtcNow.Date;

        var response = await client.GetAsync(
            $"/api/stream/1/playlist.m3u8?stream=main&from={HttpUtility(from)}&to={HttpUtility(from.AddHours(25))}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Playlist_InvertedWindowIs400()
    {
        using var client = Client();
        var from = DateTime.UtcNow.Date;

        var response = await client.GetAsync(
            $"/api/stream/1/playlist.m3u8?stream=main&from={HttpUtility(from.AddHours(2))}&to={HttpUtility(from)}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task InitAndMedia_ConcatenateBackToTheRecordedFile()
    {
        var from = DateTime.UtcNow.Date.AddHours(3);
        var id = SeedSegment(_factory.RecordingsDir, from, from.AddSeconds(15), out var fileBytes);
        using var client = Client();

        var init = await client.GetByteArrayAsync(HlsPlaylist.InitUri(id));
        var media = await client.GetByteArrayAsync(HlsPlaylist.MediaUri(id));
        var stitched = new byte[init.Length + media.Length];
        init.CopyTo(stitched, 0);
        media.CopyTo(stitched, init.Length);

        Assert.Equal(fileBytes, stitched);
        Assert.Equal("ftyp", Encoding.ASCII.GetString(init, 4, 4));
        Assert.Equal("moof", Encoding.ASCII.GetString(media, 4, 4));
    }

    [Fact]
    public async Task Init_UsesInitContentTypeAndMediaIsIsoSegment()
    {
        var from = DateTime.UtcNow.Date.AddHours(2);
        var id = SeedSegment(_factory.RecordingsDir, from, from.AddSeconds(15), out _);
        using var client = Client();

        var init = await client.GetAsync(HlsPlaylist.InitUri(id));
        var media = await client.GetAsync(HlsPlaylist.MediaUri(id));

        Assert.Equal("video/mp4", init.Content.Headers.ContentType?.MediaType);
        Assert.Equal("video/iso.segment", media.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task SegmentOutsideRecordingsRootIsForbidden()
    {
        var from = DateTime.UtcNow.Date.AddHours(1);
        var id = SeedSegment(_factory.OutsideDir, from, from.AddSeconds(15), out _);
        using var client = Client();

        var init = await client.GetAsync(HlsPlaylist.InitUri(id));
        var media = await client.GetAsync(HlsPlaylist.MediaUri(id));

        Assert.Equal(HttpStatusCode.Forbidden, init.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, media.StatusCode);
    }

    [Fact]
    public async Task UnknownSegmentIs404()
    {
        using var client = Client();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(HlsPlaylist.InitUri(999999))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(HlsPlaylist.MediaUri(999999))).StatusCode);
    }

    [Fact]
    public async Task SegmentStillRecordingIs404()
    {
        var path = _factory.WriteSegment(_factory.RecordingsDir, "in-progress.mp4", SyntheticFmp4());
        var id = Segments.BeginSegment(1, "main", path, DateTime.UtcNow);
        using var client = Client();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(HlsPlaylist.InitUri(id))).StatusCode);
    }

    [Fact]
    public async Task NonFmp4SegmentIsBadRequest()
    {
        var from = DateTime.UtcNow.Date.AddHours(5);
        var bytes = Encoding.ASCII.GetBytes("this is not an mp4 file at all");
        var path = _factory.WriteSegment(_factory.RecordingsDir, "garbage.mp4", bytes);
        var id = Segments.BeginSegment(1, "main", path, from);
        Segments.CompleteSegment(id, from.AddSeconds(15), bytes.Length, 15, "sha");
        using var client = Client();

        var response = await client.GetAsync(HlsPlaylist.InitUri(id));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task DeletedSegmentFileIs404()
    {
        var from = DateTime.UtcNow.Date.AddHours(4);
        var bytes = Mp4Fixture.Fmp4();
        var aliveId = SeedSegment(_factory.RecordingsDir, from, from.AddSeconds(15), out _);
        var missingId = Segments.BeginSegment(
            1,
            "main",
            Path.Combine(_factory.RecordingsDir, "gone.mp4"),
            from.AddMinutes(5));
        Segments.CompleteSegment(missingId, from.AddMinutes(5).AddSeconds(15), bytes.Length, 15, "sha");
        using var client = Client();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(HlsPlaylist.InitUri(missingId))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(HlsPlaylist.InitUri(aliveId))).StatusCode);
    }

    [Fact]
    public async Task SegmentLedger_DoesNotLeakHostPath()
    {
        var from = DateTime.UtcNow.Date.AddHours(7);
        SeedSegment(_factory.RecordingsDir, from.AddMinutes(1), from.AddMinutes(1).AddSeconds(15), out _);
        using var client = Client();

        var json = await client.GetStringAsync(
            $"/api/recording/segments?channelId=1&stream=main&from={HttpUtility(from)}&to={HttpUtility(from.AddHours(1))}");

        Assert.DoesNotContain("file_path", json);
        Assert.DoesNotContain(_factory.RecordingsDir, json);
        Assert.Contains("\"sha256\"", json);
    }

    private static byte[] SyntheticFmp4() => Mp4Fixture.Fmp4();

    private static string HttpUtility(DateTime value) =>
        Uri.EscapeDataString(value.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture));
}
