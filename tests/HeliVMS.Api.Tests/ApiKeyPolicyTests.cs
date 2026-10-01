using System.Net;
using System.Net.Http.Headers;
using HeliVMS.WebApi;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace HeliVMS.Api.Tests;

/// <summary>非 Development 環境且未設定金鑰時的 API 主機。</summary>
public sealed class ProductionApiFactory : WebApplicationFactory<Program>
{
    public string DbPath { get; } = Path.Combine(Path.GetTempPath(), $"helivms-api-prod-{Guid.NewGuid():N}.db");

    public string? ApiKey { get; set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Production);
        builder.UseSetting("HELIVMS_DB", DbPath);
        if (ApiKey is not null)
        {
            builder.UseSetting(ApiKeyAuthMiddleware.ApiKeyConfigKey, ApiKey);
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing)
        {
            return;
        }

        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            try
            {
                File.Delete(DbPath + suffix);
            }
            catch (IOException)
            {
            }
        }
    }
}

public class ApiKeyPolicyTests
{
    [Fact]
    public async Task Production_WithoutConfiguredKey_RejectsEvenDevKey()
    {
        using var factory = new ProductionApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", ApiKeyAuthMiddleware.DefaultDevKey);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/channels")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/config")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/health")).StatusCode);
    }

    [Fact]
    public async Task Production_WithConfiguredKey_AcceptsOnlyThatKey()
    {
        using var factory = new ProductionApiFactory { ApiKey = "s3cret-rotation-2026" };
        using var client = factory.CreateClient();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "s3cret-rotation-2026");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/channels")).StatusCode);

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", ApiKeyAuthMiddleware.DefaultDevKey);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/channels")).StatusCode);
    }

    [Fact]
    public async Task Production_Unauthorized_CarriesWwwAuthenticateChallenge()
    {
        using var factory = new ProductionApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/channels");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task RepeatedFailuresFromOneAddress_AreRateLimited()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ApiKeyAuthMiddleware.ApiKeyConfigKey] = "right-key",
            })
            .Build();
        var middleware = new ApiKeyAuthMiddleware(
            _ => Task.CompletedTask,
            config,
            new StubEnvironment { EnvironmentName = Environments.Production },
            NullLogger<ApiKeyAuthMiddleware>.Instance);

        for (var i = 0; i < 25; i++)
        {
            await InvokeAsync(middleware, remote: IPAddress.Parse("10.1.2.3"), key: "wrong-key");
        }

        var blocked = await InvokeAsync(middleware, remote: IPAddress.Parse("10.1.2.3"), key: "right-key");
        Assert.Equal(StatusCodes.Status429TooManyRequests, blocked);

        var other = await InvokeAsync(middleware, remote: IPAddress.Parse("10.9.9.9"), key: "right-key");
        Assert.Equal(StatusCodes.Status200OK, other);
    }

    [Fact]
    public async Task Health_BypassesKeyCheck()
    {
        var config = new ConfigurationBuilder().Build();
        var middleware = new ApiKeyAuthMiddleware(
            _ => Task.CompletedTask,
            config,
            new StubEnvironment { EnvironmentName = Environments.Production },
            NullLogger<ApiKeyAuthMiddleware>.Instance);

        var context = await InvokeAsync(middleware, "/api/health", IPAddress.Loopback, "anything");
        Assert.Equal(StatusCodes.Status200OK, context);
    }

    [Fact]
    public async Task Development_WithoutConfiguredKey_AcceptsDevKey()
    {
        // 本機開發不設金鑰時沿用 dev 金鑰（見 middleware 類別註解），否則開發者每一步都要配 key。
        var middleware = new ApiKeyAuthMiddleware(
            _ => Task.CompletedTask,
            new ConfigurationBuilder().Build(),
            new StubEnvironment { EnvironmentName = Environments.Development },
            NullLogger<ApiKeyAuthMiddleware>.Instance);

        Assert.Equal(
            StatusCodes.Status200OK,
            await InvokeAsync(middleware, IPAddress.Loopback, ApiKeyAuthMiddleware.DefaultDevKey));
        Assert.Equal(
            StatusCodes.Status401Unauthorized,
            await InvokeAsync(middleware, IPAddress.Loopback, "not-the-dev-key"));
    }

    [Fact]
    public async Task ConfiguredKey_OverridesDevelopmentFallback()
    {
        // 有設定金鑰時，即使是 Development 也以設定值為準——否則正式環境誤帶 Development 就會退回 dev 金鑰。
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ApiKeyAuthMiddleware.ApiKeyConfigKey] = "explicit-local-key",
            })
            .Build();
        var middleware = new ApiKeyAuthMiddleware(
            _ => Task.CompletedTask,
            config,
            new StubEnvironment { EnvironmentName = Environments.Development },
            NullLogger<ApiKeyAuthMiddleware>.Instance);

        Assert.Equal(
            StatusCodes.Status200OK,
            await InvokeAsync(middleware, IPAddress.Loopback, "explicit-local-key"));
        Assert.Equal(
            StatusCodes.Status401Unauthorized,
            await InvokeAsync(middleware, IPAddress.Loopback, ApiKeyAuthMiddleware.DefaultDevKey));
    }

    [Fact]
    public async Task QueryStringKey_IsOnlyAcceptedForWebSockets()
    {
        // 一般請求把金鑰放 query string 不得通過：秘密會進 URL、瀏覽器歷史與 access log。
        // WebSocket 無法自訂 header，才破例允許 ?key=。
        var middleware = new ApiKeyAuthMiddleware(
            _ => Task.CompletedTask,
            new ConfigurationBuilder().Build(),
            new StubEnvironment { EnvironmentName = Environments.Development },
            NullLogger<ApiKeyAuthMiddleware>.Instance);

        var plain = await InvokeQueryKeyAsync(middleware, ApiKeyAuthMiddleware.DefaultDevKey, webSocket: false);
        Assert.Equal(StatusCodes.Status401Unauthorized, plain);

        var ws = await InvokeQueryKeyAsync(middleware, ApiKeyAuthMiddleware.DefaultDevKey, webSocket: true);
        Assert.Equal(StatusCodes.Status200OK, ws);
    }

    [Fact]
    public async Task NonApiPath_BypassesKeyCheck()
    {
        // 中間件只保護 /api/*；SPA 靜態檔若也要金鑰，網頁主控台連 HTML 都載不到。fail-closed 不能連門都關上。
        var middleware = new ApiKeyAuthMiddleware(
            _ => Task.CompletedTask,
            new ConfigurationBuilder().Build(),
            new StubEnvironment { EnvironmentName = Environments.Production },
            NullLogger<ApiKeyAuthMiddleware>.Instance);

        Assert.Equal(StatusCodes.Status200OK, await InvokeAsync(middleware, "/", IPAddress.Loopback, key: null));
        Assert.Equal(StatusCodes.Status200OK, await InvokeAsync(middleware, "/index.html", IPAddress.Loopback, key: null));
    }

    private static async Task<int> InvokeAsync(ApiKeyAuthMiddleware middleware, IPAddress remote, string? key)
        => await InvokeAsync(middleware, "/api/channels", remote, key);

    private static async Task<int> InvokeAsync(
        ApiKeyAuthMiddleware middleware,
        string path,
        IPAddress remote,
        string? key)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Connection.RemoteIpAddress = remote;
        if (key is not null)
        {
            context.Request.Headers.Authorization = $"Bearer {key}";
        }

        await middleware.InvokeAsync(context);
        return context.Response.StatusCode;
    }

    private static async Task<int> InvokeQueryKeyAsync(ApiKeyAuthMiddleware middleware, string key, bool webSocket)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/channels";
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        if (webSocket)
        {
            context.Features.Set<IHttpWebSocketFeature>(new StubWebSocketFeature());
        }

        context.Request.QueryString = new QueryString($"?key={key}");

        await middleware.InvokeAsync(context);
        return context.Response.StatusCode;
    }

    private sealed class StubWebSocketFeature : IHttpWebSocketFeature
    {
        public bool IsWebSocketRequest => true;

        public Task<System.Net.WebSockets.WebSocket> AcceptAsync(WebSocketAcceptContext context)
            => throw new NotSupportedException();
    }

    private sealed class StubEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;

        public string ApplicationName { get; set; } = "HeliVMS.Api.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
