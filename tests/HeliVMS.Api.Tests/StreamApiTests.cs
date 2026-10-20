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

    /// <summary>
    /// 寫入一筆 final 段之後把檔案刪掉，模擬「使用者直接從磁碟刪除錄影」。
    ///
    /// <para>
    /// 索引的 (channel_id, stream, start_time) 有唯一鍵，每個測試的時刻都必須自己選一個不衝突的
    /// 時間（原本各自硬寫偏移就是為了避開）。把「建段→刪檔」收斂成一個輔助方法，讓新增的對帳
    /// 測試不必再重複貼同一段樣板。
    /// </para>
    /// </summary>
    private long SeedDeletedSegment(DateTime startUtc)
    {
        var path = _factory.WriteSegment(_factory.RecordingsDir, $"seg-{Guid.NewGuid():N}.mp4", SyntheticFmp4());
        var id = Segments.BeginSegment(1, "main", path, startUtc);
        Segments.CompleteSegment(id, startUtc.AddSeconds(15), 128, 15, "sha");
        File.Delete(path);
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
        var path = _factory.WriteSegment(_factory.RecordingsDir, $"seg-{Guid.NewGuid():N}.mp4", bytes);
        var id = Segments.BeginSegment(1, "main", path, from);
        Segments.CompleteSegment(id, from.AddSeconds(15), bytes.Length, 15, "sha");
        using var client = Client();

        var response = await client.GetAsync(HlsPlaylist.InitUri(id));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// 磁碟上的檔案被刪掉之後，播放清單不能再列出它，且索引要被就地改成 missing。
    ///
    /// <para>
    /// 現場症狀是「明明有錄影，播放到一半就卡住」：清單照著索引寫出 #EXTINF，播放器去要
    /// init/media 時吃 404，整條播放中斷，而日誌只看到 404、看不出是索引過期。
    /// 這個測試同時釘住「清單不列」與「索引被修正」——只做前者會在下一輪查詢再犯一次。
    /// </para>
    /// </summary>
    [Fact]
    public async Task 檔案被刪掉後播放清單不再列出且索引標成遺失()
    {
        var from = DateTime.UtcNow.Date.AddHours(9);
        var aliveId = SeedSegment(_factory.RecordingsDir, from, from.AddSeconds(15), out _);
        var doomedId = SeedDeletedSegment(from.AddMinutes(1));
        using var client = Client();

        var response = await client.GetAsync(
            $"/api/stream/1/playlist.m3u8?stream=main&from={HttpUtility(from)}&to={HttpUtility(from.AddHours(1))}");
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(HlsPlaylist.MediaUri(aliveId), text);
        Assert.DoesNotContain(HlsPlaylist.MediaUri(doomedId), text);
        Assert.Equal(SegmentStatus.Missing, Segments.Get(doomedId)!.Status);
    }

    /// <summary>
    /// 遠端主控台的區段清單也不能再回報已被刪除的段。
    ///
    /// <para>
    /// 這是操作員查調閱清單最直接的畫面；索引沒對帳時它會列出整天的「錄影」，
    /// 點下去才 404。清單與時間軸、播放清單三者口徑必須一致。
    /// </para>
    /// </summary>
    [Fact]
    public async Task 檔案被刪掉後區段清單也不再列出()
    {
        var from = DateTime.UtcNow.Date.AddHours(11);
        var aliveId = SeedSegment(_factory.RecordingsDir, from, from.AddSeconds(15), out _);
        var doomedId = SeedDeletedSegment(from.AddMinutes(1));
        using var client = Client();

        var body = System.Text.Json.JsonSerializer.Deserialize<ApiEndpoints.Paged<ApiEndpoints.SegmentListItem>>(
            await client.GetStringAsync(
                $"/api/recording/segments?channelId=1&stream=main&from={HttpUtility(from)}&to={HttpUtility(from.AddHours(1))}"),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;

        Assert.Contains(body.Items, s => s.Id == aliveId);
        Assert.DoesNotContain(body.Items, s => s.Id == doomedId);
    }

    /// <summary>
    /// 時間軸（日曆／回放條）也不得再畫出已被刪除的段。
    ///
    /// <para>
    /// 這是操作員看到的第一個畫面：日期有粗體、時間軸有一整條 bar，點下去卻放不出東西。
    /// 三個端點（清單／播放清單）都對帳了，時間軸若漏掉，畫面就自相矛盾——這一則釘住第三條。
    /// </para>
    /// </summary>
    [Fact]
    public async Task 檔案被刪掉後時間軸也不再畫出區段()
    {
        var dayStart = DateTime.UtcNow.Date.AddDays(3);
        var aliveId = SeedSegment(_factory.RecordingsDir, dayStart.AddHours(2), dayStart.AddHours(2).AddSeconds(15), out _);
        var doomedId = SeedDeletedSegment(dayStart.AddHours(2).AddMinutes(1));
        using var client = Client();

        var timeline = System.Text.Json.JsonSerializer.Deserialize<PlaybackTimeline>(
            await client.GetStringAsync(
                $"/api/recording/timeline?channelId=1&stream=main&day={HttpUtility(dayStart)}"),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;

        Assert.Single(timeline.Bars);
        Assert.Equal(2d / 24d, timeline.Bars[0].LeftFraction, 6);
        Assert.Equal(SegmentStatus.Missing, Segments.Get(doomedId)!.Status);
        Assert.Equal(SegmentStatus.Final, Segments.Get(aliveId)!.Status);
    }

    [Fact]
    public async Task DeletedSegmentFileIs404()
    {
        // 這一則是「已對帳過的索引」情境：段已被標成 missing，直接打 init 端點仍要 404。
        // 分批刪檔 + 對帳由 檔案被刪掉後播放清單不再列出且索引標成遺失 涵蓋。
        var from = DateTime.UtcNow.Date.AddHours(4);
        var aliveId = SeedSegment(_factory.RecordingsDir, from.AddSeconds(1), from.AddSeconds(16), out _);
        var missingId = Segments.BeginSegment(
            1,
            "main",
            Path.Combine(_factory.RecordingsDir, "gone.mp4"),
            from.AddMinutes(5));
        Segments.CompleteSegment(missingId, from.AddMinutes(5).AddSeconds(15), 128, 15, "sha");
        Segments.MarkMissing(missingId);
        using var client = Client();

        Assert.Equal(SegmentStatus.Missing, Segments.Get(missingId)!.Status);
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
