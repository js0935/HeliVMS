using HeliVMS.Rtc;
using Xunit;

namespace HeliVMS.Rtc.Tests;

/// <summary>
/// 「真的瀏覽器 offer 協商出來的編號，就是我們送封包用的編號」。
/// </summary>
/// <remarks>
/// <para>
/// 這是現場「網頁連得上、畫面全黑」的守門測試。Chrome 的 offer 用 <b>96 表示 VP8</b>、
/// 102 表示 H.264；本系統的常數也是 96，於是舊版把 H.264 位元流標成 96 送出去，Chrome 交給
/// VP8 解碼器——<c>framesDecoded=0</c>、畫面全黑、日誌乾淨、ICE 與 DTLS 都連上。
/// </para>
/// <para>
/// offer 逐字取自 2026-11-16 實機抓下來的 Chrome offer（只裁掉與視訊格式無關的 extmap 與
/// 其餘 30 幾個格式）：那一份就是當時協商成 102 的同一份，不是「我以為 Chrome 會送什麼」。
/// </para>
/// </remarks>
[Collection(RtcIoCollection.Name)]
public sealed class WhepNegotiatedPayloadTypeTests
{
    private const int ChromeH264PayloadType = 102;

    private const string ChromeOffer = """
        v=0
        o=- 2642440267667155890 2 IN IP4 127.0.0.1
        s=-
        t=0 0
        a=group:BUNDLE 0
        a=msid-semantic: WMS
        m=video 9 UDP/TLS/RTP/SAVPF 96 97 102 108
        c=IN IP4 0.0.0.0
        a=rtcp:9 IN IP4 0.0.0.0
        a=ice-ufrag:KWu6
        a=ice-pwd:HxEbw5Y6H2h7RvLb1ggdxf+O
        a=ice-options:trickle
        a=fingerprint:sha-256 51:9B:B9:AE:FD:3C:DA:C6:B0:B3:C1:98:AF:E6:73:E2:76:34:7A:E4:9E:15:E2:9E:ED:A8:98:01:35:10:08:20
        a=setup:actpass
        a=mid:0
        a=recvonly
        a=rtcp-mux
        a=rtpmap:96 VP8/90000
        a=rtpmap:97 rtx/90000
        a=fmtp:97 apt=96
        a=rtpmap:102 H264/90000
        a=rtcp-fb:102 transport-cc
        a=fmtp:102 level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42001f
        a=rtpmap:108 H264/90000
        a=rtcp-fb:108 transport-cc
        a=fmtp:108 level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42e01f
        """;

    [Fact]
    public async Task 真的Chrome的offer協商後送出的編號與答案一致且不是常數()
    {
        var options = new WhepOptions { StunServers = [], TurnServers = [] };
        using var peer = new WhepPeer(options);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var answer = await peer.NegotiateAsync(ChromeOffer, cts.Token);
        var negotiated = WhepPayloadType.FromAnswerSdp(answer.Answer);

        Assert.NotNull(negotiated);
        Assert.Equal(negotiated!.Value, peer.VideoPayloadType);

        // 這一行就是整個 bug：舊版不論協商結果都用 96 送，而 96 在 Chrome 的 offer 裡是 VP8。
        Assert.NotEqual(LiveEncodeOptions.PayloadType, peer.VideoPayloadType);
        Assert.Equal(ChromeH264PayloadType, peer.VideoPayloadType);

        // 答案的視訊 m-line 必須真的列出這個編號，否則上面的相等只是兩邊一起錯。
        var videoLine = answer.Answer
            .Split('\n')
            .Select(l => l.Trim())
            .First(l => l.StartsWith("m=video", StringComparison.Ordinal));
        Assert.Contains(ChromeH264PayloadType.ToString(System.Globalization.CultureInfo.InvariantCulture), videoLine);
    }
}
