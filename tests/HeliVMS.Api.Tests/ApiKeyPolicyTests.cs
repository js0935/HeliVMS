using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using HeliVMS.Storage;
using HeliVMS.WebApi;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace HeliVMS.Api.Tests;

/// <summary>非 Development 環境且未設定金鑰時的 API 主機。</summary>
public sealed class ProductionApiFactory : WebApplicationFactory<Program>
{
    public string DbPath { get; } = Path.Combine(Path.GetTempPath(), $"helivms-api-prod-{Guid.NewGuid():N}.db");

    /// <summary>
    /// 指向一個不存在的授權檔，確保測試不會讀到開發機上真正的授權。
    /// </summary>
    /// <remarks>
    /// 這個 factory 刻意沿用<em>內嵌</em>公鑰（不覆寫 <c>HELIVMS_LICENSE_PUBLIC_KEY</c>），
    /// 因為它要驗的是「正式組態」的行為。但少了這行，<c>LicenseService</c> 會去讀
    /// <c>%LOCALAPPDATA%\HeliVMS\license.lic</c>：CI 上沒有該檔所以綠，開發機只要匯入過
    /// 一張含 <c>remote</c> 的授權，<c>/api/shares</c> 就會從預期的 403 變成 200——
    /// <c>ApiKeyCheck_RunsBeforeLicenseGate</c> 於是以為授權閘門壞了。指到不存在的檔案即可自足。
    /// </remarks>
    public string LicensePath { get; } = Path.Combine(Path.GetTempPath(), $"helivms-api-prod-lic-absent-{Guid.NewGuid():N}.lic");

    public string? ApiKey { get; set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Production);
        builder.UseSetting("HELIVMS_DB", DbPath);
        builder.UseSetting("HELIVMS_LICENSE", LicensePath);
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
    public async Task ApiKeyCheck_RunsBeforeLicenseGate()
    {
        // 順序有意義（Program.cs 是 ApiKeyAuth → LicenseGate）：
        // 未通過金鑰驗證的呼叫者不該從狀態碼分得出「這台買了什麼」，403 等於洩漏授權狀態。
        // 反過來，金鑰正確但沒買遠端，就必須是 403；若是 401，前端會一直要人重試登入而不是
        // 去畫「請升級」畫面。
        using var factory = new ProductionApiFactory { ApiKey = "order-check-key" };

        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/shares")).StatusCode);

        using var keyed = factory.CreateClient();
        keyed.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "order-check-key");
        Assert.Equal(HttpStatusCode.Forbidden, (await keyed.GetAsync("/api/shares")).StatusCode);
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
            new AccountSessionStore(),
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
            new AccountSessionStore(),
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
            new AccountSessionStore(),
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
            new AccountSessionStore(),
            NullLogger<ApiKeyAuthMiddleware>.Instance);

        Assert.Equal(
            StatusCodes.Status200OK,
            await InvokeAsync(middleware, IPAddress.Loopback, "explicit-local-key"));
        Assert.Equal(
            StatusCodes.Status401Unauthorized,
            await InvokeAsync(middleware, IPAddress.Loopback, ApiKeyAuthMiddleware.DefaultDevKey));
    }

    [Fact]
    public async Task DisableSwitch_WithProductionAndNoKey_AllowsAnonymousCallers()
    {
        // HELIVMS_API_KEY_DISABLED=1 是完全關閉驗證的逃生門：即使 Production、沒設金鑰，管理端點也要放行。
        // 供受信任的本機環境用；預設不開啟，否則部署設定一打錯就等於門戶洞開。
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ApiKeyAuthMiddleware.DisabledConfigKey] = "1",
            })
            .Build();
        var middleware = new ApiKeyAuthMiddleware(
            _ => Task.CompletedTask,
            config,
            new StubEnvironment { EnvironmentName = Environments.Production },
            new AccountSessionStore(),
            NullLogger<ApiKeyAuthMiddleware>.Instance);

        Assert.Equal(StatusCodes.Status200OK, await InvokeAsync(middleware, IPAddress.Loopback, key: null));
        Assert.Equal(StatusCodes.Status200OK, await InvokeAsync(middleware, IPAddress.Loopback, "any-wrong-key"));
    }

    [Fact]
    public async Task DisableSwitch_Unset_KeepsApiProtected()
    {
        // 沒設定開關時不得放行——這是預設 fail-closed 的迴歸，避免有人把「關閉」誤寫成預設值。
        var middleware = new ApiKeyAuthMiddleware(
            _ => Task.CompletedTask,
            new ConfigurationBuilder().Build(),
            new StubEnvironment { EnvironmentName = Environments.Production },
            new AccountSessionStore(),
            NullLogger<ApiKeyAuthMiddleware>.Instance);

        Assert.Equal(StatusCodes.Status401Unauthorized, await InvokeAsync(middleware, IPAddress.Loopback, key: null));
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
            new AccountSessionStore(),
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
            new AccountSessionStore(),
            NullLogger<ApiKeyAuthMiddleware>.Instance);

        Assert.Equal(StatusCodes.Status200OK, await InvokeAsync(middleware, "/", IPAddress.Loopback, key: null));
        Assert.Equal(StatusCodes.Status200OK, await InvokeAsync(middleware, "/index.html", IPAddress.Loopback, key: null));
    }

    [Fact]
    public async Task SessionToken_IsAcceptedAsBearerCredential()
    {
        // M306：AccountSessionStore 簽發的 token 與 API 金鑰同層——登入成功後不必再餵金鑰。
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ApiKeyAuthMiddleware.ApiKeyConfigKey] = "right-key",
            })
            .Build();
        var sessions = new AccountSessionStore();
        var middleware = new ApiKeyAuthMiddleware(
            _ => Task.CompletedTask,
            config,
            new StubEnvironment { EnvironmentName = Environments.Production },
            sessions,
            NullLogger<ApiKeyAuthMiddleware>.Instance);

        var token = sessions.Create("op1", "admin", "操作員一");

        Assert.Equal(StatusCodes.Status200OK, await InvokeAsync(middleware, IPAddress.Loopback, token));
        Assert.Equal(StatusCodes.Status401Unauthorized, await InvokeAsync(middleware, IPAddress.Loopback, "forged-token"));
        Assert.Equal(StatusCodes.Status200OK, await InvokeAsync(middleware, IPAddress.Loopback, "right-key"));
    }

    [Fact]
    public async Task SessionToken_RevokedByLogout_IsRejected()
    {
        var config = new ConfigurationBuilder().Build();
        var sessions = new AccountSessionStore();
        var middleware = new ApiKeyAuthMiddleware(
            _ => Task.CompletedTask,
            config,
            new StubEnvironment { EnvironmentName = Environments.Production },
            sessions,
            NullLogger<ApiKeyAuthMiddleware>.Instance);

        var token = sessions.Create("op1", "admin", "操作員一");
        Assert.Equal(StatusCodes.Status200OK, await InvokeAsync(middleware, IPAddress.Loopback, token));

        sessions.Revoke(token);
        Assert.Equal(StatusCodes.Status401Unauthorized, await InvokeAsync(middleware, IPAddress.Loopback, token));
    }

    [Fact]
    public async Task AccountsAuthenticate_IsReachableWithoutAnApiKey()
    {
        // M306：登入本身就是授權來源。無金鑰的瀏覽器必須能先登入，才能拿到後續的 token。
        using var factory = new ProductionApiFactory();
        var users = new UserRepository(factory.Services.GetRequiredService<SqliteStore>());
        users.CreateUser("webadmin", PasswordHasher.Hash("pw123"), "admin", "頁面管理員");

        using var anonymous = factory.CreateClient();
        var ok = await anonymous.PostAsJsonAsync(
            "/api/accounts/authenticate", new { username = "webadmin", password = "pw123" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var body = await ReadJsonAsync(ok);
        Assert.Equal("admin", body.Role);
        Assert.False(string.IsNullOrWhiteSpace(body.Token), "登入成功必須簽發工作階段 token");

        var bad = await anonymous.PostAsJsonAsync(
            "/api/accounts/authenticate", new { username = "webadmin", password = "wrong" });
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
        var badBody = await bad.Content.ReadAsStringAsync();
        Assert.Contains("密碼錯誤", badBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionToken_FromLogin_GrantsApiAccessWithoutAnApiKey()
    {
        // 用金鑰才拿得到的端點，登入拿到的 token 要同樣拿得到——這是「登入取得 API 存取權」的真義。
        using var factory = new ProductionApiFactory();
        var users = new UserRepository(factory.Services.GetRequiredService<SqliteStore>());
        users.CreateUser("webview", PasswordHasher.Hash("pw456"), "viewer", "觀看者");

        using var anonymous = factory.CreateClient();
        var login = await anonymous.PostAsJsonAsync(
            "/api/accounts/authenticate", new { username = "webview", password = "pw456" });
        var token = (await ReadJsonAsync(login)).Token;

        using var session = factory.CreateClient();
        session.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.OK, (await session.GetAsync("/api/channels")).StatusCode);

        using var expired = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await expired.GetAsync("/api/channels")).StatusCode);
    }

    [Fact]
    public async Task Logout_RevokesTheSessionTokenEndToEnd()
    {
        using var factory = new ProductionApiFactory();
        var users = new UserRepository(factory.Services.GetRequiredService<SqliteStore>());
        users.CreateUser("webadmin", PasswordHasher.Hash("pw123"), "admin", "頁面管理員");

        using var anonymous = factory.CreateClient();
        var login = await anonymous.PostAsJsonAsync(
            "/api/accounts/authenticate", new { username = "webadmin", password = "pw123" });
        var token = (await ReadJsonAsync(login)).Token;

        using var session = factory.CreateClient();
        session.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.OK, (await session.GetAsync("/api/channels")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await session.PostAsync("/api/accounts/logout", content: null)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await session.GetAsync("/api/channels")).StatusCode);
    }

    [Fact]
    public async Task SessionToken_ExpiresAfterItsTtl()
    {
        // 工作階段有有效期限：就算沒登出，過期 token 也不能再用，否則一次登入永久有效。
        var clock = new FixedClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var sessions = new AccountSessionStore(clock);
        var token = sessions.Create("op1", "admin", "操作員一");

        Assert.True(sessions.TryGet(token, out _));

        clock.Now = clock.Now.Add(AccountSessionStore.SessionTtl);
        Assert.False(sessions.TryGet(token, out _));
    }

    private sealed class FixedClock : TimeProvider
    {
        private DateTimeOffset _now;

        public FixedClock(DateTimeOffset now) => _now = now;

        public DateTimeOffset Now
        {
            get => _now;
            set => _now = value;
        }

        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed record LoginBody(string Role, string? DisplayName, string? Token);

    private static async Task<LoginBody> ReadJsonAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();
        return System.Text.Json.JsonSerializer.Deserialize<LoginBody>(
            json, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
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
