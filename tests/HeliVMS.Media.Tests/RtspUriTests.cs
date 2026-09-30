using HeliVMS.Shared;
using HeliVMS.Media;

namespace HeliVMS.Media.Tests;

/// <summary>驗證含認證資訊之 RTSP 位址建構（ONVIF GetStreamUri 不含帳密，需另行嵌入）。</summary>
public class RtspUriTests
{
    [Fact]
    public void WithCredentials_EmbedsUsernameAndPassword()
    {
        var result = RtspUri.WithCredentials("rtsp://192.168.1.64:554/media/stream.sdp?profile=Profile1", "root", "pass123");

        Assert.Equal("rtsp://root:pass123@192.168.1.64:554/media/stream.sdp?profile=Profile1", result);
    }

    [Fact]
    public void WithCredentials_NullPassword_StillSuppliesUserInfo()
    {
        var result = RtspUri.WithCredentials("rtsp://192.168.1.64/live.sdp", "root", null);

        Assert.Equal("rtsp://root@192.168.1.64/live.sdp", result);
    }

    [Fact]
    public void WithCredentials_EscapesReservedCharacters_AndRoundTrips()
    {
        // 帳密含 @ : / 等字元時必須跳脫，否則位址會被錯誤解析。
        // 關鍵不變條件：解析回來的 UserInfo 必須與原帳密完全相同。
        const string User = "us@er";
        const string Password = "p@ss:1/2";

        var result = RtspUri.WithCredentials("rtsp://cam/live.sdp", User, Password);

        // UserInfo 為跳脫後字串，須解碼後比對。
        var parsed = new Uri(result);
        var parts = Uri.UnescapeDataString(parsed.UserInfo).Split(':', 2);

        Assert.Equal(User, parts[0]);
        Assert.Equal(Password, parts[1]);
        Assert.Equal("/live.sdp", parsed.AbsolutePath);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a uri")]
    public void WithCredentials_UnusableUrl_ReturnsInput(string url)
    {
        Assert.Equal(url, RtspUri.WithCredentials(url, "root", "pass"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void WithCredentials_NoUsername_ReturnsInputUnchanged(string? username)
    {
        const string Url = "rtsp://192.168.1.64/live.sdp";

        Assert.Equal(Url, RtspUri.WithCredentials(Url, username, "pass"));
    }

    [Fact]
    public void WithCredentials_DoesNotMutateCallersUrl()
    {
        // 字串為不可變，但此處確認組合結果可安全用於暫存欄位而不影響原始位址。
        const string Bare = "rtsp://192.168.1.64:554/live.sdp";

        var first = RtspUri.WithCredentials(Bare, "root", "pw1");
        var second = RtspUri.WithCredentials(Bare, "root", "pw2");

        Assert.Equal("rtsp://root:pw1@192.168.1.64:554/live.sdp", first);
        Assert.Equal("rtsp://root:pw2@192.168.1.64:554/live.sdp", second);
        Assert.DoesNotContain("@", Bare, StringComparison.Ordinal);
    }

    [Fact]
    public void WithCredentials_PreservesQueryAndPort()
    {
        // ONVIF GetStreamUri 常帶 profile 查詢參數，組合帳密時不可遺失。
        var result = RtspUri.WithCredentials(
            "rtsp://10.0.0.5:8554/live/ch0?profile=Profile1&starttime=now",
            "root",
            "pw");

        var uri = new Uri(result);
        Assert.Equal(8554, uri.Port);
        Assert.Equal("/live/ch0", uri.AbsolutePath);
        Assert.Equal("?profile=Profile1&starttime=now", uri.Query);
    }

    [Theory]
    [InlineData("rtsp://root:secret@10.0.0.1:554/live.sdp", "rtsp://10.0.0.1:554/live.sdp")]
    [InlineData("rtsp://root@10.0.0.1/live.sdp", "rtsp://10.0.0.1/live.sdp")]
    [InlineData("rtsp://10.0.0.1/live.sdp", "rtsp://10.0.0.1/live.sdp")]
    [InlineData("", "")]
    [InlineData("not a uri", "not a uri")]
    public void Redact_RemovesUserInfo(string input, string expected)
    {
        // 錯誤訊息會顯示在 UI 提示，故含帳密的位址必須遮蔽後才可外流。
        Assert.Equal(expected, RtspUri.Redact(input));
    }

    [Fact]
    public void Redact_NeverLeaksPassword()
    {
        var redacted = RtspUri.Redact(RtspUri.WithCredentials("rtsp://10.0.0.1/live.sdp", "root", "p@ss:1/2"));

        Assert.DoesNotContain("p@ss", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("root", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("@", redacted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("rtsp://125.227.64.37:554/media/stream.sdp?profile=Profile1", "125.227.64.37")]
    [InlineData("rtsp://cam.local/live.sdp", "cam.local")]
    [InlineData("rtsp://[2001:db8::1]:554/live.sdp", "[2001:db8::1]")]
    [InlineData("rtsp://root:pw@10.0.0.5/live.sdp", "10.0.0.5")]
    [InlineData("", "")]
    [InlineData("not a uri", "")]
    public void Host_ExtractsCameraHost(string url, string expected)
    {
        // 既有頻道 device_id 為 NULL 時，需由位址主機回推設備以取得憑證。
        Assert.Equal(expected, RtspUri.Host(url));
    }

    [Fact]
    public void RedactText_StripsCredentialsFromFfmpegStderr()
    {
        // ffmpeg 失敗時會回顯輸入網址，整段並非合法 URI（Redact 無效），必須逐段遮蔽。
        const string stderr = """
            Input #0, rtsp, from 'rtsp://root:sup3rsecret@10.0.0.1:554/live.sdp':
              Stream mapping:
            [rtsp @ 0000] method DESCRIBE failed: 401 Unauthorized
            """;

        var redacted = RtspUri.RedactText(stderr);

        Assert.DoesNotContain("sup3rsecret", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("root", redacted, StringComparison.Ordinal);
        Assert.Contains("rtsp://10.0.0.1:554/live.sdp", redacted, StringComparison.Ordinal);

        // 其餘診斷內容必須保留，否則失去排查價值。
        Assert.Contains("401 Unauthorized", redacted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("a rtsp://u:p@h:554/s b rtsp://x:y@h2:554/s2 c", "a rtsp://h:554/s b rtsp://h2:554/s2 c")]
    [InlineData("no urls here", "no urls here")]
    [InlineData("mailto:someone@example.com", "mailto:someone@example.com")]
    [InlineData("rtsp://10.0.0.1/live.sdp", "rtsp://10.0.0.1/live.sdp")]
    [InlineData("", "")]
    public void RedactText_HandlesMultipleAndAbsentUris(string input, string expected)
    {
        Assert.Equal(expected, RtspUri.RedactText(input));
    }

    [Fact]
    public void RedactText_HandlesPasswordContainingAtSign()
    {
        // 密碼含未跳脫的 @ 時，需移除到最後一個 @ 為止，否則尾巴會被當成密碼保留。
        var redacted = RtspUri.RedactText("err rtsp://root:p@ss@10.0.0.1/live.sdp failed");

        Assert.Equal("err rtsp://10.0.0.1/live.sdp failed", redacted);
    }
}
