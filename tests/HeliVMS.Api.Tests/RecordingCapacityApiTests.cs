using System.Net.Http.Headers;
using System.Net.Http.Json;
using HeliVMS.Storage;

namespace HeliVMS.Api.Tests;

/// <summary>
/// <c>/api/recording/capacity</c>：剩餘可錄時間（QCV8001 對照的落地，見
/// <c>docs/STORAGE-ALIGNMENT.md</c> 缺口 1）。
/// </summary>
/// <remarks>
/// <para>
/// 這個端點存在的理由是「現場問這顆碟還能錄多久」——而它最容易錯的地方是把
/// 「沒有寫入樣本」與「已低於保留空間」都回成 0。兩者的處置完全相反
/// （一個要去看錄影有沒有在跑，一個要去清磁碟），所以狀態字串被逐一釘住。
/// </para>
/// <para>
/// 每個測試用<b>自己的</b> host（不共用 <c>IClassFixture</c>）：端點讀的是「最近一小時
/// 這顆碟的寫入量」，共用資料庫時另一個測試剛種下的區段會讓「沒有樣本」的案例
/// 變成「有樣本」——那種紅燈只會在特定執行順序下出現，比沒有測試更難查。
/// </para>
/// </remarks>
public sealed class RecordingCapacityApiTests : IDisposable
{
    private const string Key = "helivms-dev-key";
    private const long GiB = 1024L * 1024 * 1024;

    private readonly ApiFactory _factory = new();
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        _factory.Dispose();
        foreach (var dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task 沒有寫入樣本時回未知而不是零()
    {
        // 錄影磁碟設在暫存目錄所在的磁碟；保留空間設 0，所以「未知」只可能來自沒有樣本。
        ConfigureRecordingDisk();
        EnsureChannel();
        Service<SettingsRepository>().Set(RecordingStorage.MinFreeSettingKey, "0");

        using var client = Client();
        var body = await ReadAsync<CapacityBody>(await client.GetAsync("/api/recording/capacity"));

        Assert.Equal("unknown", body.State);
        Assert.Equal(0, body.RemainingSeconds);
        Assert.Equal(0, body.BytesPerHour);
        Assert.NotEmpty(body.Drives);
    }

    [Fact]
    public async Task 有寫入樣本時回已知並帶出可錄秒數()
    {
        ConfigureRecordingDisk();
        EnsureChannel();
        Service<SettingsRepository>().Set(RecordingStorage.MinFreeSettingKey, "0");

        // 最近半小時寫了 1 GB → 2 GB/小時。
        var segments = Service<SegmentRepository>();
        var start = DateTime.UtcNow.AddMinutes(-30);
        var file = Path.Combine(Path.GetTempPath(), $"helivms-cap-{Guid.NewGuid():N}.mp4");
        File.WriteAllBytes(file, new byte[16]);
        var id = segments.BeginSegment(1, "main", file, start);
        segments.CompleteSegment(id, start.AddSeconds(30), GiB, 30, new string('b', 64));

        try
        {
            using var client = Client();
            var body = await ReadAsync<CapacityBody>(await client.GetAsync("/api/recording/capacity"));

            Assert.Equal("known", body.State);
            Assert.True(body.RemainingSeconds > 0, "有樣本又有可用空間時一定要有可錄秒數");

            // 基準是「最早段起點到現在」，而現在比段起點晚了幾毫秒，所以速率略低於 2 GB/小時
            // ——用區間而不是等號，否則這個斷言會隨執行時間抖動。
            Assert.InRange(body.BytesPerHour, 2d * GiB * 0.98, 2d * GiB * 1.02);
            Assert.InRange(body.Drives[0].BasisMinutes, 29.9, 30.1);
        }
        finally
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
            }
        }
    }

    private HttpClient Client()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Key);
        return client;
    }

    private T Service<T>() where T : notnull => (T)_factory.Services.GetService(typeof(T))!;

    /// <summary>把錄影磁碟設定指到暫存目錄所在的磁碟，讓端點找得到一顆碟。</summary>
    private void ConfigureRecordingDisk()
    {
        var root = Path.Combine(Path.GetTempPath(), $"helivms-cap-root-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        _tempDirs.Add(root);
        Service<SettingsRepository>().Set(
            RecordingStorage.DisksSettingKey,
            RecordingStorage.SerializeDisks([new RecordingStorage.RecordingDiskDto { Root = root, Primary = true }]));
    }

    private void EnsureChannel()
    {
        Service<SqliteStore>().Execute(
            "INSERT OR IGNORE INTO channels (id, name, main_rtsp, recording_mode) VALUES (1, 'cap-ch', 'rtsp://test/1', 0);");
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        var value = await response.Content.ReadFromJsonAsync<T>();
        Assert.NotNull(value);
        return value!;
    }

    private sealed record CapacityBody(
        long ReserveBytes,
        double WindowMinutes,
        string State,
        double RemainingSeconds,
        double BytesPerHour,
        List<CapacityDriveBody> Drives);

    private sealed record CapacityDriveBody(
        string Root,
        long TotalBytes,
        long FreeBytes,
        long WrittenBytes,
        double BasisMinutes,
        string State,
        double RemainingSeconds);
}
