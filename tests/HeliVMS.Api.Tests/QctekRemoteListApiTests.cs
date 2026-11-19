using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using HeliVMS.Qctek.Nemo;
using HeliVMS.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace HeliVMS.Api.Tests;

/// <summary>
/// QCC 遠端設定頁的兩份清單端點（<c>/QCCamList.bin</c>、<c>/QCUserList.bin</c>）走的是
/// 真的 Web 主機：中介層要從 DI 拿到 <see cref="ChannelRepository"/>／
/// <see cref="UserRepository"/> 才有內容。<c>QctekRemoteListTests</c> 只釘住線路版型，
/// 這裡釘住「資料真的有接上去」——漏接的症狀是 0 列（QCC 顯示空清單），不會有任何錯誤。
/// </summary>
public sealed class QctekRemoteListApiTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public QctekRemoteListApiTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task 遠端設定清單端點會回傳真的相機與帳號而不是零列()
    {
        var channels = new ChannelRepository(_factory.Services.GetRequiredService<SqliteStore>());
        channels.Add("QCC前門", "rtsp://10.9.9.1/main");
        channels.Add("QCC後門", "rtsp://10.9.9.2/main");

        var repositories = new UserRepository(_factory.Services.GetRequiredService<SqliteStore>());
        repositories.CreateUser("qccadmin", PasswordHasher.Hash("pw1"), "admin");
        repositories.CreateUser("qccview", PasswordHasher.Hash("pw2"), "viewer");

        using var client = _factory.CreateClient();

        var (cameraBody, cameraType) = await GetBinaryAsync(client, "/QCCamList.bin");
        var (userBody, userType) = await GetBinaryAsync(client, "/QCUserList.bin");

        // 真機的兩支清單回應是 application/octet-stream; charset=utf-8。
        Assert.Equal("application/octet-stream; charset=utf-8", cameraType);
        Assert.Equal("application/octet-stream; charset=utf-8", userType);

        Assert.Equal("RMCL", Encoding.ASCII.GetString(cameraBody, 0, 4));
        Assert.Equal("RMUL", Encoding.ASCII.GetString(userBody, 0, 4));
        Assert.Equal((uint)cameraBody.Length, ReadU32(cameraBody, 4));
        Assert.Equal((uint)userBody.Length, ReadU32(userBody, 4));

        var cameraNames = ReadCameraNames(cameraBody);
        Assert.Contains("QCC前門", cameraNames);
        Assert.Contains("QCC後門", cameraNames);

        var listed = ReadUsers(userBody);
        Assert.Equal(QctekRemoteList.AdminPermission, listed["qccadmin"].Permission);
        Assert.Equal(QctekRemoteList.DefaultPermission, listed["qccview"].Permission);

        // 序號（+0x0c）必須是帳號自己的 Id：QCC 之後用它刪除／更新帳號，
        // 用列的位置會在刪掉一個帳號之後全部錯位（刪到別人）。
        foreach (var user in repositories.ListUsers())
        {
            Assert.Equal(user.Id, listed[user.Username].Sequence);
        }
    }

    [Fact]
    public async Task 遠端設定清單會用Basic帳號標記目前登入者()
    {
        var users = new UserRepository(_factory.Services.GetRequiredService<SqliteStore>());
        users.CreateUser("qcccurrent", PasswordHasher.Hash("pw3"), "admin");
        users.CreateUser("qccother", PasswordHasher.Hash("pw4"), "admin");

        using var client = _factory.CreateClient();
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes("qcccurrent:anything"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);

        var (body, _) = await GetBinaryAsync(client, "/QCUserList.bin");
        var listed = ReadUsers(body);

        // isCurrentUser 讀的是 +0x18 那個 u32 的 0x20（不是權限位元）；標錯人會讓 QCC
        // 把別人當成自己（改名／改密碼的對象）。
        Assert.Equal((uint)QctekRemoteList.CurrentUserBit, listed["qcccurrent"].Flag);
        Assert.Equal(0u, listed["qccother"].Flag);
        // 旗標不得混進權限位元。
        Assert.Equal(QctekRemoteList.AdminPermission, listed["qcccurrent"].Permission);
    }

    private static async Task<(byte[] Body, string? ContentType)> GetBinaryAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadAsByteArrayAsync(), response.Content.Headers.ContentType?.ToString());
    }

    /// <summary>掃過每一列取相機名稱（不假設資料庫回傳的順序）。</summary>
    private static List<string> ReadCameraNames(byte[] body)
    {
        var count = (int)ReadU32(body, 8);
        var names = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var offset = QctekRemoteList.HeaderLength + (i * QctekRemoteList.CameraRowLength);
            names.Add(Encoding.Unicode.GetString(body, offset, 0x82).TrimEnd('\0'));
        }

        return names;
    }

    /// <summary>使用者列的關鍵欄位：名稱在 <c>+0x28</c>（ANSI 32 bytes）、權限在 <c>+0x08</c>、
    /// 序號在 <c>+0x0c</c>、目前登入者旗標在 <c>+0x18</c>。</summary>
    private sealed record ListedUser(int Permission, int Sequence, uint Flag);

    private static Dictionary<string, ListedUser> ReadUsers(byte[] body)
    {
        var count = (int)ReadU32(body, 8);
        var users = new Dictionary<string, ListedUser>(count, StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            var offset = QctekRemoteList.HeaderLength + (i * QctekRemoteList.UserRowLength);
            var name = Encoding.ASCII.GetString(body, offset + 0x28, 32).TrimEnd('\0');
            users[name] = new ListedUser(
                BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(offset + 0x08)),
                BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(offset + 0x0c)),
                BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(offset + 0x18)));
        }

        return users;
    }

    private static uint ReadU32(byte[] buffer, int offset)
        => BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset));
}
