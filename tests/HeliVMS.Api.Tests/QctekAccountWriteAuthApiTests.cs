using System.Net;
using System.Net.Http.Headers;
using System.Text;
using HeliVMS.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace HeliVMS.Api.Tests;

/// <summary>
/// QCC 帳號寫入的授權：真機的三支端點都<b>先授權（401）再解析（400）</b>，而且要求
/// 登入者是 admin。
/// </summary>
/// <remarks>
/// <para>
/// 順序本身就是規格：反過來會讓「帳密錯」看起來像「請求格式錯」，QCC 會改送另一種格式
/// 而不是提示使用者改密碼。這裡也釘住第二條憑證來源——查詢的 <c>hBase64</c>
/// （<c>base64("user:pass")</c>）：QCC 的裝置精靈在某些路徑只帶它、不帶 Basic 標頭。
/// </para>
/// <para>
/// <c>auth.enabled = 0</c> 時比照桌面版放行（那是「這台機器沒有設登入」的既有語意）；
/// 這裡測的是啟用之後的行為，所以在建構式把設定打開——同一個類別共用一份資料庫。
/// </para>
/// </remarks>
public sealed class QctekAccountWriteAuthApiTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    private readonly UserRepository _users;

    public QctekAccountWriteAuthApiTests(ApiFactory factory)
    {
        _factory = factory;
        var store = factory.Services.GetRequiredService<SqliteStore>();
        _users = new UserRepository(store);

        foreach (var user in _users.ListUsers())
        {
            _users.DeleteUser(user.Id);
        }

        new SettingsRepository(store).Set("auth.enabled", "1");
    }

    [Fact]
    public async Task 沒有憑證的寫入請求回401()
    {
        var target = _users.CreateUser("nobasica", PasswordHasher.Hash("pw"), "viewer");
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync($"/QCRemoveAccount.bin?usr={target}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotNull(_users.GetById(target));
    }

    [Fact]
    public async Task viewer的憑證不足以寫入帳號()
    {
        var admin = _users.CreateUser("authadmin", PasswordHasher.Hash("apw"), "admin");
        var viewer = _users.CreateUser("authview", PasswordHasher.Hash("vpw"), "viewer");
        using var client = Basic("authview:vpw");

        using var response = await client.GetAsync($"/QCRemoveAccount.bin?usr={admin}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotNull(_users.GetById(viewer));
    }

    [Fact]
    public async Task admin的Basic憑證可以寫入()
    {
        _users.CreateUser("authadmin", PasswordHasher.Hash("apw"), "admin");
        var target = _users.CreateUser("authtarget", PasswordHasher.Hash("pw"), "viewer");
        using var client = Basic("authadmin:apw");

        using var response = await client.GetAsync($"/QCRemoveAccount.bin?usr={target}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(_users.GetById(target));
    }

    [Fact]
    public async Task admin密碼錯誤時回401而且不會鎖住帳號()
    {
        var admin = _users.CreateUser("authadmin", PasswordHasher.Hash("apw"), "admin");
        using var client = Basic("authadmin:wrong");

        using var response = await client.GetAsync($"/QCRemoveAccount.bin?usr=1");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        // 走 PasswordHasher 而不是 AuthService.Authenticate：遠端亂試不可以累計失敗次數
        // 把桌面登入一起鎖掉（那一支會寫 login.fail 並在 5 次之後鎖定）。
        Assert.Equal(0, _users.GetById(admin)!.FailedLogins);
    }

    [Fact]
    public async Task 查詢字串裡的hBase64也可以當作憑證()
    {
        _users.CreateUser("authadmin", PasswordHasher.Hash("apw"), "admin");
        var target = _users.CreateUser("authtarget", PasswordHasher.Hash("pw"), "viewer");
        using var client = _factory.CreateClient();

        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes("authadmin:apw"));
        using var response = await client.GetAsync(
            $"/QCRemoveAccount.bin?hBase64={credentials}&usr={target}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(_users.GetById(target));
    }

    private HttpClient Basic(string credentials)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials)));
        return client;
    }
}
