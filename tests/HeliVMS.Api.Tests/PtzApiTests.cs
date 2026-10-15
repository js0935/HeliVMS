using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HeliVMS.Devices.Onvif;
using HeliVMS.Shared.Models;
using HeliVMS.Storage;
using HeliVMS.WebApi;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace HeliVMS.Api.Tests;

/// <summary>
/// Web 端 PTZ 端點（M25 的遠端對應）。以假的 <see cref="IOnvifPtzGateway"/> 取代真 ONVIF I/O，
/// 驗證的是端點層真正負責的事：鑑權、頻道→設備的解析（404／409）、輸入夾制與例外→狀態碼映射。
/// SOAP 本身由 HeliVMS.Devices.Tests 涵蓋，不在這裡重造一份假攝影機。
/// </summary>
public sealed class PtzApiFactory : WebApplicationFactory<Program>
{
    public const string Key = "helivms-dev-key";

    public string DbPath { get; } = Path.Combine(Path.GetTempPath(), $"helivms-ptz-{Guid.NewGuid():N}.db");

    public FakePtzGateway Gateway { get; } = new();

    /// <summary>綁定了設備的頻道；在 <see cref="ConfigureWebHost"/> 期間寫入。</summary>
    public int BoundChannelId { get; private set; }

    /// <summary>沒有綁定任何設備的種子頻道（id 1）。</summary>
    public const int UnboundChannelId = 1;

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.UseSetting("HELIVMS_DB", DbPath);
        builder.UseSetting("HELIVMS_API_KEY", Key);
        builder.UseSetting("HELIVMS_LICENSE_PUBLIC_KEY", TestLicenseSeeder.PublicPem);
        builder.ConfigureTestServices(services => services.AddSingleton<IOnvifPtzGateway>(Gateway));

        using var store = new SqliteStore(DbPath);
        store.Initialize();
        TestLicenseSeeder.Apply(
            store,
            LicenseFeatures.All.ToArray(),
            new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc),
            expiresUtc: null);

        var audit = new AuditLogRepository(store);
        var channels = new ChannelRepository(store, audit);
        channels.EnsureSeedChannels();
        var deviceId = new DeviceRepository(store, audit)
            .Add("測試攝影機", "10.0.0.9", 80, "admin", "pw", "vivotek");
        BoundChannelId = channels.Add("綁定頻道", "rtsp://10.0.0.9/main", null, deviceId);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing)
        {
            return;
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

/// <summary>可錄下最後一次呼叫、並可切換成拋出 ONVIF 例外的假 gateway。</summary>
public sealed class FakePtzGateway : IOnvifPtzGateway
{
    public bool ThrowNotSupported { get; set; }

    public bool ThrowBackendFailure { get; set; }

    public string? LastProfileToken { get; private set; }

    public (double Pan, double Tilt, double Zoom)? LastMove { get; private set; }

    public string? LastPresetName { get; private set; }

    public string? LastGotoPresetToken { get; private set; }

    private void MaybeThrow()
    {
        if (ThrowNotSupported)
        {
            throw new OnvifException(OnvifErrorCode.NotSupported, "設備不支援 ONVIF PTZ。");
        }

        if (ThrowBackendFailure)
        {
            throw new OnvifException(OnvifErrorCode.Connection, "連線被拒。");
        }
    }

    public Task<IReadOnlyList<PtzProfileInfo>> GetProfilesAsync(DeviceRecord device, CancellationToken cancellation)
    {
        MaybeThrow();
        return Task.FromResult<IReadOnlyList<PtzProfileInfo>>([new PtzProfileInfo("Profile_1", "main", "main · 1920×1080 · H264")]);
    }

    public Task ContinuousMoveAsync(DeviceRecord device, string? profileToken, double pan, double tilt, double zoom, CancellationToken cancellation)
    {
        MaybeThrow();
        LastProfileToken = profileToken;
        LastMove = (pan, tilt, zoom);
        return Task.CompletedTask;
    }

    public Task StopAsync(DeviceRecord device, string? profileToken, CancellationToken cancellation)
    {
        MaybeThrow();
        LastProfileToken = profileToken;
        return Task.CompletedTask;
    }

    public Task HomeAsync(DeviceRecord device, string? profileToken, CancellationToken cancellation)
    {
        MaybeThrow();
        LastProfileToken = profileToken;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PtzPresetInfo>> GetPresetsAsync(DeviceRecord device, string? profileToken, CancellationToken cancellation)
    {
        MaybeThrow();
        LastProfileToken = profileToken;
        return Task.FromResult<IReadOnlyList<PtzPresetInfo>>([new PtzPresetInfo("1", "大門")]);
    }

    public Task<string> SetPresetAsync(DeviceRecord device, string? profileToken, string? name, CancellationToken cancellation)
    {
        MaybeThrow();
        LastProfileToken = profileToken;
        LastPresetName = name;
        return Task.FromResult("2");
    }

    public Task GotoPresetAsync(DeviceRecord device, string? profileToken, string presetToken, CancellationToken cancellation)
    {
        MaybeThrow();
        LastProfileToken = profileToken;
        LastGotoPresetToken = presetToken;
        return Task.CompletedTask;
    }

    public Task RemovePresetAsync(DeviceRecord device, string? profileToken, string presetToken, CancellationToken cancellation)
    {
        MaybeThrow();
        LastProfileToken = profileToken;
        return Task.CompletedTask;
    }
}

public sealed class PtzApiTests : IClassFixture<PtzApiFactory>
{
    private readonly PtzApiFactory _factory;

    public PtzApiTests(PtzApiFactory factory)
    {
        _factory = factory;

        // 觸發主機建立，讓 ConfigureWebHost 先把種子頻道與綁定頻道寫進資料庫，
        // 測試才能拿到 BoundChannelId（建構子本身不建主機）。
        _ = factory.Services;
    }

    private HttpClient Client(bool withKey = true)
    {
        var client = _factory.CreateClient();
        if (withKey)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", PtzApiFactory.Key);
        }

        return client;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    [Fact]
    public async Task 未帶金鑰的PTZ請求會被擋下()
    {
        using var client = Client(withKey: false);

        var response = await client.GetAsync($"/api/ptz/{PtzApiFactory.UnboundChannelId}/profiles");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task 不存在的頻道回傳404()
    {
        using var client = Client();

        var response = await client.GetAsync("/api/ptz/99999/profiles");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task 未綁定設備的頻道回傳409()
    {
        using var client = Client();

        var response = await client.GetAsync($"/api/ptz/{PtzApiFactory.UnboundChannelId}/profiles");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task 綁定設備的頻道可列出已標示的Profiles()
    {
        _factory.Gateway.ThrowNotSupported = false;
        using var client = Client();

        var response = await client.GetAsync($"/api/ptz/{_factory.BoundChannelId}/profiles");
        var body = await ReadAsync<PtzProfilesResponse>(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var profile = Assert.Single(body.Profiles);
        Assert.Equal("Profile_1", profile.Token);
        Assert.Contains("1920×1080", profile.Label);
    }

    [Fact]
    public async Task 裝置不支援PTZ時回傳409()
    {
        _factory.Gateway.ThrowNotSupported = true;
        try
        {
            using var client = Client();

            var response = await client.GetAsync($"/api/ptz/{_factory.BoundChannelId}/profiles");

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        }
        finally
        {
            _factory.Gateway.ThrowNotSupported = false;
        }
    }

    [Fact]
    public async Task 裝置後端失敗時回傳502()
    {
        _factory.Gateway.ThrowBackendFailure = true;
        try
        {
            using var client = Client();

            var response = await client.PostAsJsonAsync(
                $"/api/ptz/{_factory.BoundChannelId}/stop",
                new PtzProfileRequest("Profile_1"));

            Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        }
        finally
        {
            _factory.Gateway.ThrowBackendFailure = false;
        }
    }

    [Fact]
    public async Task 移動速度會被夾在負一到一之間並帶上Profile()
    {
        _factory.Gateway.ThrowNotSupported = false;
        using var client = Client();

        var response = await client.PostAsJsonAsync(
            $"/api/ptz/{_factory.BoundChannelId}/move",
            new PtzMoveRequest("Profile_1", Pan: 5, Tilt: 0, Zoom: -3));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Profile_1", _factory.Gateway.LastProfileToken);
        Assert.Equal((1.0, 0.0, -1.0), _factory.Gateway.LastMove);
    }

    [Fact]
    public async Task 未帶Profile時由gateway決定預設Profile()
    {
        _factory.Gateway.ThrowNotSupported = false;
        using var client = Client();

        var response = await client.PostAsJsonAsync(
            $"/api/ptz/{_factory.BoundChannelId}/home",
            new PtzProfileRequest(null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(_factory.Gateway.LastProfileToken);
    }

    [Fact]
    public async Task 可新增並列出預設點()
    {
        _factory.Gateway.ThrowNotSupported = false;
        using var client = Client();

        var save = await client.PostAsJsonAsync(
            $"/api/ptz/{_factory.BoundChannelId}/presets",
            new PtzPresetSaveRequest("Profile_1", "  倉庫  "));
        var presets = await client.GetAsync($"/api/ptz/{_factory.BoundChannelId}/presets?profileToken=Profile_1");
        var body = await ReadAsync<List<PtzPresetInfo>>(presets);

        Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        Assert.Equal("倉庫", _factory.Gateway.LastPresetName);
        Assert.Equal("大門", Assert.Single(body).Name);
    }

    [Fact]
    public async Task 前往與刪除預設點會把權杖轉交gateway()
    {
        _factory.Gateway.ThrowNotSupported = false;
        using var client = Client();

        var gotoResponse = await client.PostAsJsonAsync(
            $"/api/ptz/{_factory.BoundChannelId}/presets/7/goto",
            new PtzProfileRequest("Profile_1"));
        var deleteResponse = await client.DeleteAsync(
            $"/api/ptz/{_factory.BoundChannelId}/presets/7?profileToken=Profile_1");

        Assert.Equal(HttpStatusCode.OK, gotoResponse.StatusCode);
        Assert.Equal("7", _factory.Gateway.LastGotoPresetToken);
        Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);
    }
}
