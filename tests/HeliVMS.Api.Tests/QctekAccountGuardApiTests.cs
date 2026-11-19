using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using HeliVMS.Qctek.Nemo;
using HeliVMS.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace HeliVMS.Api.Tests;

/// <summary>
/// QCC 帳號寫入的保護規則：<b>不能把自己或最後一個 admin 鎖在門外</b>。
/// </summary>
/// <remarks>
/// <para>
/// 真機允許把唯一的 admin 降成 viewer、也允許刪掉自己；QCC 上只是勾錯一格。
/// HeliVMS 這裡刻意比真機嚴格，而且<b>不看 <c>auth.enabled</c></b>：登入未啟用時把唯一
/// 的 admin 降級，之後也沒有任何人有管理權限可以改回來（桌面的「最後一個 admin」規則
/// 同樣是無條件保護）。
/// </para>
/// <para>
/// 每個測試都先把 <c>users</c> 清空再建立自己要的角色——<c>IClassFixture</c> 讓同一個
/// 類別共用一份資料庫，而「最後一個 admin」是<b>整張表</b>的性質，殘留的帳號會讓斷言
/// 通過或失敗取決於測試執行順序。
/// </para>
/// </remarks>
public sealed class QctekAccountGuardApiTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    private readonly UserRepository _users;

    public QctekAccountGuardApiTests(ApiFactory factory)
    {
        _factory = factory;
        _users = new UserRepository(factory.Services.GetRequiredService<SqliteStore>());

        foreach (var user in _users.ListUsers())
        {
            _users.DeleteUser(user.Id);
        }
    }

    [Fact]
    public async Task 最後一個admin不會被降級成viewer()
    {
        var admin = _users.CreateUser("guardonly", PasswordHasher.Hash("pw"), "admin");
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync($"/QCUpdateAccount.bin?{EncodeRow(0x08, admin, "guardonly")}");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("admin", _users.GetById(admin)!.Role);
    }

    [Fact]
    public async Task 最後一個admin不會被停用()
    {
        var admin = _users.CreateUser("guardonly", PasswordHasher.Hash("pw"), "admin");
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync($"/QCUpdateAccount.bin?{EncodeRow(0, admin, "guardonly")}");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.True(_users.GetById(admin)!.Enabled);
    }

    [Fact]
    public async Task 最後一個admin不會被刪除()
    {
        var admin = _users.CreateUser("guardonly", PasswordHasher.Hash("pw"), "admin");
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync($"/QCRemoveAccount.bin?usr={admin}");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.NotNull(_users.GetById(admin));
    }

    [Fact]
    public async Task 目前登入的帳號不會被自己刪除()
    {
        var self = _users.CreateUser("guardself", PasswordHasher.Hash("pw"), "admin");
        _users.CreateUser("guardother", PasswordHasher.Hash("pw"), "admin");

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("guardself:anything")));

        using var response = await client.GetAsync($"/QCRemoveAccount.bin?usr={self}");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.NotNull(_users.GetById(self));
    }

    [Fact]
    public async Task 不是最後一個的admin可以刪除()
    {
        _users.CreateUser("guardkeep", PasswordHasher.Hash("pw"), "admin");
        var target = _users.CreateUser("guardextra", PasswordHasher.Hash("pw"), "admin");
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync($"/QCRemoveAccount.bin?usr={target}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(_users.GetById(target));
    }

    private static string EncodeRow(int permission, int sequence, string name)
    {
        var row = new byte[QctekRemoteList.UserRowLength];
        BinaryPrimitives.WriteInt32LittleEndian(row.AsSpan(0x08), permission);
        BinaryPrimitives.WriteInt32LittleEndian(row.AsSpan(0x0c), sequence);
        Encoding.Latin1.GetBytes(name).CopyTo(row.AsSpan(0x28));
        return Convert.ToBase64String(row);
    }
}
