using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HeliVMS.Devices.Onvif;
using HeliVMS.Shared.Models;
using HeliVMS.Storage;
using HeliVMS.WebApi;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace HeliVMS.Api.Tests;

/// <summary>
/// Web 端攝影機 IO 端點（乾接點）。以假的 <see cref="IOnvifIoGateway"/> 取代真 ONVIF I/O，
/// 驗證端點層真正負責的事：鑑權、頻道→設備解析（404／409）、例外→狀態碼映射、狀態切換轉交。
/// SOAP 本身由 HeliVMS.Devices.Tests 的 OnvifIoServiceTests 涵蓋。
/// </summary>
public sealed class IoApiFactory : WebApplicationFactory<Program>
{
    public const string Key = "helivms-dev-key";

    public string DbPath { get; } = Path.Combine(Path.GetTempPath(), $"helivms-io-{Guid.NewGuid():N}.db");

    public FakeIoGateway Gateway { get; } = new();

    /// <summary>綁定了設備的頻道；在 <see cref="ConfigureWebHost"/> 期間寫入。</summary>
    public int BoundChannelId { get; private set; }

    /// <summary>沒有綁定任何設備的種子頻道（id 1）。</summary>
    public const int UnboundChannelId = 1;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("HELIVMS_DB", DbPath);
        builder.UseSetting("HELIVMS_API_KEY", Key);
        builder.UseSetting("HELIVMS_LICENSE_PUBLIC_KEY", TestLicenseSeeder.PublicPem);
        builder.ConfigureTestServices(services => services.AddSingleton<IOnvifIoGateway>(Gateway));

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
public sealed class FakeIoGateway : IOnvifIoGateway
{
    public bool ThrowNotSupported { get; set; }

    public bool ThrowBackendFailure { get; set; }

    public string? LastRelayToken { get; private set; }

    public bool? LastActive { get; private set; }

    private void MaybeThrow()
    {
        if (ThrowNotSupported)
        {
            throw new OnvifException(OnvifErrorCode.NotSupported, "設備不支援 ONVIF IO。");
        }

        if (ThrowBackendFailure)
        {
            throw new OnvifException(OnvifErrorCode.Connection, "連線被拒。");
        }
    }

    public Task<IReadOnlyList<IoInputInfo>> GetDigitalInputsAsync(DeviceRecord device, CancellationToken cancellation)
    {
        MaybeThrow();
        return Task.FromResult<IReadOnlyList<IoInputInfo>>([new IoInputInfo("DI_1", 1, "門磁", "門磁 (#1)")]);
    }

    public Task<IReadOnlyList<IoOutputInfo>> GetRelayOutputsAsync(DeviceRecord device, CancellationToken cancellation)
    {
        MaybeThrow();
        return Task.FromResult<IReadOnlyList<IoOutputInfo>>([new IoOutputInfo("Relay_1", "Bistable", true, "警報燈", "警報燈 (Bistable)")]);
    }

    public Task SetRelayOutputAsync(DeviceRecord device, string relayToken, bool active, CancellationToken cancellation)
    {
        MaybeThrow();
        LastRelayToken = relayToken;
        LastActive = active;
        return Task.CompletedTask;
    }
}

public sealed class IoApiTests : IClassFixture<IoApiFactory>
{
    private readonly IoApiFactory _factory;

    public IoApiTests(IoApiFactory factory)
    {
        _factory = factory;

        // 觸發主機建立，讓 ConfigureWebHost 先把種子頻道與綁定頻道寫進資料庫。
        _ = factory.Services;
    }

    private HttpClient Client(bool withKey = true)
    {
        var client = _factory.CreateClient();
        if (withKey)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", IoApiFactory.Key);
        }

        return client;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    [Fact]
    public async Task 未帶金鑰的IO請求會被擋下()
    {
        using var client = Client(withKey: false);

        var response = await client.GetAsync($"/api/io/{IoApiFactory.UnboundChannelId}/inputs");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task 不存在的頻道回傳404()
    {
        using var client = Client();

        var response = await client.GetAsync("/api/io/99999/inputs");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task 未綁定設備的頻道回傳409()
    {
        using var client = Client();

        var response = await client.GetAsync($"/api/io/{IoApiFactory.UnboundChannelId}/inputs");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task 綁定設備的頻道可列出數位輸入與繼電器輸出()
    {
        _factory.Gateway.ThrowNotSupported = false;
        using var client = Client();

        var inputsResponse = await client.GetAsync($"/api/io/{_factory.BoundChannelId}/inputs");
        var outputsResponse = await client.GetAsync($"/api/io/{_factory.BoundChannelId}/outputs");
        var inputs = await ReadAsync<IoInputsResponse>(inputsResponse);
        var outputs = await ReadAsync<IoOutputsResponse>(outputsResponse);

        Assert.Equal(HttpStatusCode.OK, inputsResponse.StatusCode);
        var input = Assert.Single(inputs.Inputs);
        Assert.Equal("DI_1", input.Token);
        Assert.Equal(1, input.Id);
        Assert.Contains("#1", input.Label);

        Assert.Equal(HttpStatusCode.OK, outputsResponse.StatusCode);
        var output = Assert.Single(outputs.Outputs);
        Assert.Equal("Relay_1", output.Token);
        Assert.True(output.Bistable);
    }

    [Fact]
    public async Task 裝置不支援IO時回傳409()
    {
        _factory.Gateway.ThrowNotSupported = true;
        try
        {
            using var client = Client();

            var response = await client.GetAsync($"/api/io/{_factory.BoundChannelId}/inputs");

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

            var response = await client.GetAsync($"/api/io/{_factory.BoundChannelId}/outputs");

            Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        }
        finally
        {
            _factory.Gateway.ThrowBackendFailure = false;
        }
    }

    [Fact]
    public async Task 切換繼電器會把權杖與狀態轉交gateway()
    {
        _factory.Gateway.ThrowNotSupported = false;
        using var client = Client();

        var response = await client.PostAsJsonAsync(
            $"/api/io/{_factory.BoundChannelId}/outputs/Relay_1/state",
            new IoRelayStateRequest(Active: true));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Relay_1", _factory.Gateway.LastRelayToken);
        Assert.True(_factory.Gateway.LastActive);
    }
}
