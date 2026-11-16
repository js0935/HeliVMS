using HeliVMS.Rtc;
using Xunit;

namespace HeliVMS.Rtc.Tests;

/// <summary>
/// 「送出的 RTP payload type 必須等於協商到的那一個」。
/// </summary>
/// <remarks>
/// <para>
/// 現場症狀是「網頁連得上、畫面全黑」：我們固定用自己 offer 的編號 96 送出 H.264 位元流，
/// 而 Chrome 的 offer 用 102 表示 H.264、96 表示 VP8；SIPSorcery 的 answer 沿用對端編號，
/// 於是 Chrome 把 H.264 交給 VP8 解碼器——<c>framesDecoded=0</c>、<c>keyFramesDecoded=0</c>、
/// <c>pliCount</c> 一直往上加，ICE 與 DTLS 全部正常，日誌乾淨。
/// </para>
/// <para>
/// 下面第一條用的是<b>實機抓下來的 answer</b>（2026-11-16 02:xx，Chrome 157 對本機 80 埠），
/// 所以它不是「我以為 Chrome 會送什麼」，而是當天真實發生的字串。
/// </para>
/// </remarks>
public class WhepPayloadTypeTests
{
    private const string RealChromeAnswer = """
        v=0
        o=- 16164 0 IN IP4 127.0.0.1
        s=sipsorcery
        t=0 0
        a=group:BUNDLE 0
        m=video 9 UDP/TLS/RTP/SAVP 102 108 116 41
        c=IN IP4 0.0.0.0
        a=ice-ufrag:AVKY
        a=ice-pwd:JVVDCIQVIHXDETWQQZWFKPIF
        a=fingerprint:sha-256 40:9A:53:E5:67:AA:57:0D:89:41:2A:63:A3:27:EE:6B:C6:3F:85:3B:56:61:47:57:4D:95:CD:A2:FC:92:7A:BE
        a=setup:active
        a=candidate:99356187 1 udp 2113937663 192.168.0.13 60380 typ host generation 0
        a=mid:0
        a=rtpmap:102 H264/90000
        a=rtcp-fb:102 transport-cc
        a=fmtp:102 level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42001f
        a=rtpmap:108 H264/90000
        a=fmtp:108 level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42e01f
        a=rtpmap:116 H264/90000
        a=fmtp:116 level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=4d001f
        a=rtpmap:41 H264/90000
        a=fmtp:41 level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=f4001f
        """;

    [Fact]
    public void 實機Chrome的answer取到的是H264的第一個編號()
    {
        // 不是 96（我們的常數），也不是 41／116／108：是 m-line 裡屬於 H.264 的最小編號。
        Assert.Equal(102, WhepPayloadType.FromAnswerSdp(RealChromeAnswer));
    }

    [Fact]
    public void mline只有H264時取第一個編號()
    {
        const string answer = """
            v=0
            m=video 9 UDP/TLS/RTP/SAVP 96
            a=rtpmap:96 H264/90000
            a=fmtp:96 packetization-mode=1
            """;

        Assert.Equal(96, WhepPayloadType.FromAnswerSdp(answer));
    }

    [Fact]
    public void 第一個編號不是H264時要挑H264的那一個()
    {
        // 對端把 VP8 排在前面（某些 offer 就是這樣）。取第一個會把 H.264 位元流標成 VP8——
        // 同一個黑畫面，只是換一個方向錯。
        const string answer = """
            v=0
            m=video 9 UDP/TLS/RTP/SAVP 96 102
            a=rtpmap:96 VP8/90000
            a=rtpmap:102 H264/90000
            a=fmtp:102 packetization-mode=1
            """;

        Assert.Equal(102, WhepPayloadType.FromAnswerSdp(answer));
    }

    [Fact]
    public void 沒有rtpmap時退回第一個編號而不是猜()
    {
        const string answer = """
            v=0
            m=video 9 UDP/TLS/RTP/SAVP 97
            a=mid:0
            """;

        Assert.Equal(97, WhepPayloadType.FromAnswerSdp(answer));
    }

    [Fact]
    public void 沒有視訊mline或空字串時回null讓呼叫端維持原行為()
    {
        Assert.Null(WhepPayloadType.FromAnswerSdp(null));
        Assert.Null(WhepPayloadType.FromAnswerSdp(string.Empty));
        Assert.Null(WhepPayloadType.FromAnswerSdp("v=0\nm=audio 9 UDP/TLS/RTP/SAVP 111\n"));
    }

    [Fact]
    public void 只看第一條視訊mline()
    {
        // 兩條視訊 m-line（多鏡頭）時，本系統只推一條；取第二條會送錯編號。
        const string answer = """
            v=0
            m=video 9 UDP/TLS/RTP/SAVP 102
            a=rtpmap:102 H264/90000
            m=video 9 UDP/TLS/RTP/SAVP 108
            a=rtpmap:108 H264/90000
            """;

        Assert.Equal(102, WhepPayloadType.FromAnswerSdp(answer));
    }
}
