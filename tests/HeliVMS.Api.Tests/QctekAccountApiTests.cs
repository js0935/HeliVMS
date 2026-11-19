using System.Buffers.Binary;
using System.Net;
using System.Text;
using HeliVMS.Qctek.Nemo;
using HeliVMS.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace HeliVMS.Api.Tests;

/// <summary>
/// QCC 遠端設定頁的帳號寫入端點（<c>/QCRemoveAccount.bin</c>、<c>/QCUpdateAccount.bin</c>、
/// <c>/QCCopyAccount.bin</c>）走真的 Web 主機：線路格式由 <c>QctekAccountEndpointTests</c>
/// 釘住，這裡釘住「資料庫真的被改了」與四種狀態碼的分界。
/// </summary>
/// <remarks>
/// 這一頁操作的是登入帳號，出錯的代價不對稱：刪錯人／降錯權限不會有任何錯誤訊息，
/// 只會在下次登入時發現。所以每個測試都自己建立需要的帳號，不依賴共用資料庫裡的其他列。
/// </remarks>
public sealed class QctekAccountApiTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    private readonly UserRepository _users;

    public QctekAccountApiTests(ApiFactory factory)
    {
        _factory = factory;
        _users = new UserRepository(factory.Services.GetRequiredService<SqliteStore>());
    }

    [Fact]
    public async Task 刪除帳號端點會真的把帳號從資料庫移除()
    {
        var target = _users.CreateUser("qccdel", PasswordHasher.Hash("pw"), "viewer");
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync($"/QCRemoveAccount.bin?usr={target}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
        Assert.Null(_users.GetById(target));
        // 真機的型號標頭是 QCC 認得這台裝置的一部分。
        Assert.Equal(QctekHttpResponder.ProductName, HeaderValue(response, "Server"));
        Assert.Equal("close", HeaderValue(response, "Connection"));
    }

    [Fact]
    public async Task 刪除不存在的帳號回五百()
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/QCRemoveAccount.bin?usr=987654");

        // 真機找不到序號回 false → 500；不是 404，也不是靜靜成功。
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task 刪除缺少usr回四百()
    {
        using var client = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync("/QCRemoveAccount.bin")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync("/QCRemoveAccount.bin?foo=1")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync("/QCRemoveAccount.bin?usr")).StatusCode);
    }

    [Fact]
    public async Task 更新既有帳號會回傳帳號序號並套用名稱與權限()
    {
        var target = _users.CreateUser("qccold", PasswordHasher.Hash("pw"), "viewer");
        using var client = _factory.CreateClient();

        var payload = EncodeRow(0x1c, target, "qccrenamed");
        using var response = await client.GetAsync($"/QCUpdateAccount.bin?{payload}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(target.ToString(), await response.Content.ReadAsStringAsync());

        var updated = _users.GetById(target);
        Assert.NotNull(updated);
        Assert.Equal("qccrenamed", updated.Username);
        Assert.Equal("admin", updated.Role);
        Assert.True(updated.Enabled);
    }

    [Fact]
    public async Task 更新序號為負時會新增帳號並回傳新的序號()
    {
        using var client = _factory.CreateClient();

        var payload = EncodeRow(0x08, -1, "qcccreated");
        using var response = await client.GetAsync($"/QCUpdateAccount.bin?{payload}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(int.TryParse(body, out var id), $"回應必須是帳號序號，實際是「{body}」");

        var created = _users.GetById(id);
        Assert.NotNull(created);
        Assert.Equal("qcccreated", created.Username);
        Assert.Equal("viewer", created.Role);
        Assert.True(created.Enabled);

        // 密碼是隨機的（QCC 的雜湊無法轉成 PBKDF2），所以一定不是空字串。
        Assert.False(string.IsNullOrEmpty(created.PasswordHash));
    }

    [Fact]
    public async Task 新增停用權限的帳號會建立成停用狀態()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync($"/QCUpdateAccount.bin?{EncodeRow(0, -1, "qccdisabled")}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var created = _users.GetByUsername("qccdisabled");
        Assert.NotNull(created);
        Assert.False(created.Enabled);
    }

    [Fact]
    public async Task 更新成別人已在用的名稱回五百且不改動任何一邊()
    {
        _users.CreateUser("qcctaken", PasswordHasher.Hash("pw"), "viewer");
        var target = _users.CreateUser("qccfree", PasswordHasher.Hash("pw"), "viewer");
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(
            $"/QCUpdateAccount.bin?{EncodeRow(0x08, target, "qcctaken")}");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("qccfree", _users.GetById(target)!.Username);
        Assert.NotNull(_users.GetByUsername("qcctaken"));
    }

    [Fact]
    public async Task 更新payload長度不符或不是base64回四百()
    {
        using var client = _factory.CreateClient();

        // 0x227 bytes：差一個 byte 就是真機的長度檢查失敗（400，不是 500）。
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync($"/QCUpdateAccount.bin?{Convert.ToBase64String(new byte[0x227])}")).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync("/QCUpdateAccount.bin?not-base64!!")).StatusCode);
    }

    [Fact]
    public async Task 複製權限來自己一般帳號時不會改動任何帳號()
    {
        var admin = _users.CreateUser("qcccopyadmin", PasswordHasher.Hash("pw"), "admin");
        var viewer = _users.CreateUser("qcccopyview", PasswordHasher.Hash("pw"), "viewer");
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync($"/QCCopyAccount.bin?{EncodeCopy(0x08, viewer, selectAll: true)}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());

        // 真機只換權限的 bit0 並複製時間／Excel 欄位；HeliVMS 的三種權限 bit0 都是 0，
        // 所以驗證通過但沒有任何變更——尤其是 admin 不會被停用。
        Assert.Equal("admin", _users.GetById(admin)!.Role);
        Assert.True(_users.GetById(admin)!.Enabled);
    }

    [Fact]
    public async Task 複製權限來源是admin時回五百()
    {
        var admin = _users.CreateUser("qcccopysrc", PasswordHasher.Hash("pw"), "admin");
        using var client = _factory.CreateClient();

        // 真機要求來源是「一般帳號」（(perm & 0x1c) == 8），否則整體失敗。
        using var response = await client.GetAsync($"/QCCopyAccount.bin?{EncodeCopy(0x1c, admin, selectAll: false)}");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task 帳號端點的查詢不會把base64帳密寫進診斷日誌()
    {
        using var client = _factory.CreateClient();
        var payload = EncodeRow(0x08, -1, "qcclogcheck");

        // 診斷日誌是跨執行累積的（上限 512KB）：不清掉的話，先前版本或突變測試留下的
        // 內容會讓這條測試紅在一個早就修好的問題上（實際發生過，紅在 pos 302433）。
        // 清空之後讀到的才是「這次請求寫了什麼」。
        var log = Path.Combine(Path.GetTempPath(), "helivms-qctek.log");
        try
        {
            File.Delete(log);
        }
        catch (IOException)
        {
            // 另一個測試類別正在寫同一份日誌：它只會多寫幾行，不影響下面的斷言。
        }

        using var response = await client.GetAsync($"/QCUpdateAccount.bin?{payload}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // 80 埠的診斷日誌是操作員唯一能看到「QCC 送了什麼」的地方，所以每個請求都留一行；
        // 但 update 的 query 就是 base64 的帳號與密碼（真機的 hBase64 也是明文的 base64），
        // 整段寫進日誌等於把密碼落地。這裡釘住「有留痕、但沒有內容」。
        Assert.True(File.Exists(log), "帳號端點必須留下診斷日誌");

        var text = await File.ReadAllTextAsync(log);
        Assert.Contains("/QCUpdateAccount.bin", text);
        Assert.DoesNotContain(payload[..32], text);
    }

    private static string EncodeRow(int permission, int sequence, string name)
    {
        var row = new byte[QctekRemoteList.UserRowLength];
        BinaryPrimitives.WriteInt32LittleEndian(row.AsSpan(0x08), permission);
        BinaryPrimitives.WriteInt32LittleEndian(row.AsSpan(0x0c), sequence);
        Encoding.Latin1.GetBytes(name).CopyTo(row.AsSpan(0x28));
        return Convert.ToBase64String(row);
    }

    private static string EncodeCopy(int permission, int sequence, bool selectAll)
    {
        var payload = new byte[QctekAccountEndpoint.CopyPayloadLength];
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(0x08), permission);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(0x0c), sequence);
        Encoding.Latin1.GetBytes("source").CopyTo(payload.AsSpan(0x28));
        if (selectAll)
        {
            payload.AsSpan(QctekRemoteList.UserRowLength).Fill(0xff);
        }

        return Convert.ToBase64String(payload);
    }

    private static string? HeaderValue(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
}
