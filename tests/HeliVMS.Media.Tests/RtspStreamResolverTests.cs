using System.Security.Cryptography;
using HeliVMS.Shared;

namespace HeliVMS.Media.Tests;

/// <summary>
/// 驗證「實際拉流位址」的解析策略。
/// <para>
/// 這是安全關鍵路徑：憑證只在拉流當下由 devices.password_encrypted 組合進位址，
/// 資料庫只存裸位址。解析錯誤會造成兩種後果——
/// 沒帶到憑證則頻道完全拉不到畫面；把憑證寫進資料庫則明文外洩到備份與稽核。
/// </para>
/// </summary>
public class RtspStreamResolverTests
{
    private const string Naked = "rtsp://10.0.0.1:554/media/stream.sdp?profile=Profile100";

    [Fact]
    public void Resolve_BoundDevice_EmbedsCredentials()
    {
        var resolved = RtspStreamResolver.Resolve(
            Naked,
            deviceId: 7,
            _ => 999,
            id =>
            {
                Assert.Equal(7, id);
                return ("root", "pw123");
            });

        Assert.Equal("rtsp://root:pw123@10.0.0.1:554/media/stream.sdp?profile=Profile100", resolved);
    }

    [Fact]
    public void Resolve_LegacyChannelWithoutDeviceId_FallsBackToHostLookup()
    {
        // 舊資料 device_id 為 NULL，若不做主機回推，既有頻道就永遠拿不到憑證而拉不到畫面。
        var hostSeen = string.Empty;
        var resolved = RtspStreamResolver.Resolve(
            Naked,
            deviceId: null,
            host =>
            {
                hostSeen = host;
                return 42;
            },
            id =>
            {
                Assert.Equal(42, id);
                return ("legacyuser", "legacypw");
            });

        Assert.Equal("10.0.0.1", hostSeen);
        Assert.Equal("rtsp://legacyuser:legacypw@10.0.0.1:554/media/stream.sdp?profile=Profile100", resolved);
    }

    [Fact]
    public void Resolve_NoDeviceFound_ReturnsNakedUrl_WithoutCredentials()
    {
        var resolved = RtspStreamResolver.Resolve(Naked, null, _ => null, _ => throw new InvalidOperationException("不應被呼叫"));

        Assert.Equal(Naked, resolved);
        Assert.DoesNotContain("@", resolved, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_CredentialLookupThrows_ReturnsNakedUrl_SoOtherChannelsSurvive()
    {
        // DPAPI 解密失敗（跨機器搬移的備份）不得讓整批頻道開不起來。
        var resolved = RtspStreamResolver.Resolve(
            Naked,
            deviceId: 7,
            _ => null,
            _ => throw new CryptographicException("DPAPI 失敗"));

        Assert.Equal(Naked, resolved);
    }

    [Fact]
    public void Resolve_DeviceWithoutUsername_ReturnsNakedUrl()
    {
        var resolved = RtspStreamResolver.Resolve(Naked, 7, _ => null, _ => (string.Empty, string.Empty));

        Assert.Equal(Naked, resolved);
    }

    [Fact]
    public void Resolve_UsernameOnly_KeepsBareUrl()
    {
        // 沒有密碼的帳號等同沒有可用憑證；不可產生 "user@host" 這種半截位址。
        var resolved = RtspStreamResolver.Resolve(Naked, 7, _ => null, _ => ("root", string.Empty));

        Assert.Equal(Naked, resolved);
    }

    [Fact]
    public void Resolve_AlreadyCredentialedUrl_DoesNotDoubleEmbed()
    {
        // 若有人在精靈手動輸入含帳密的網址，不可疊成 "a:b@a:b@host"。
        var resolved = RtspStreamResolver.Resolve(
            "rtsp://old:oldpw@10.0.0.1:554/media/stream.sdp",
            7,
            _ => null,
            _ => ("new", "newpw"));

        var atCount = resolved.Count(c => c == '@');
        Assert.Equal(1, atCount);
        Assert.Contains("new:newpw@", resolved, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_UnparsableUrl_ReturnsInputUnchanged()
    {
        var resolved = RtspStreamResolver.Resolve(
            "不是合法的 rtsp 位址",
            7,
            _ => null,
            _ => ("root", "pw"));

        Assert.Equal("不是合法的 rtsp 位址", resolved);
    }

    [Fact]
    public void Resolve_NullOrEmptyUrl_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, RtspStreamResolver.Resolve(null, 7, _ => null, _ => ("u", "p")));
    }

    [Fact]
    public void Resolve_BlankUrl_ReturnsInputUnchanged()
    {
        // 空白輸入原樣返回，不做無意義的轉換或修剪，避免呼叫端拿不到原始值。
        var resolved = RtspStreamResolver.Resolve("   ", 7, _ => null, _ => ("u", "p"));

        Assert.Equal("   ", resolved);
    }

    [Fact]
    public void Resolve_ResolvedUrl_RedactsBackToOriginalNakedUrl()
    {
        // 關鍵不變式：組合結果遮蔽後必須等於原本的裸位址，
        // 代表資料庫存的內容確實不含帳密。
        var resolved = RtspStreamResolver.Resolve(Naked, 7, _ => null, _ => ("root", "pw123"));

        Assert.Equal(Naked, RtspUri.Redact(resolved));
    }
}
