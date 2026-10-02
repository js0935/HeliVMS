using SIPSorcery.Net;

namespace HeliVMS.Rtc.Tests;

/// <summary>
/// ICE 候選改寫的契約測試。
/// <para>
/// 這段邏輯直接決定「跨網段能不能連上」，而且失敗時症狀是「有設定卻還是不行」——
/// 沒有任何錯誤訊息可以指認原因，所以只能靠測試釀死規則。
/// </para>
/// </summary>
public class IceCandidateRewriterTests
{
    private const string HostCandidate = "a=candidate:1 1 UDP 2130706431 192.168.1.20 54321 typ host generation 0 ufrag abcd network-cost 999";
    private const string SrflxCandidate = "a=candidate:2 1 UDP 1694498815 203.0.113.7 54322 typ srflx raddr 192.168.1.20 rport 54321 generation 0";
    private const string RelayCandidate = "a=candidate:3 1 UDP 16777215 198.51.100.9 40000 typ relay raddr 203.0.113.7 rport 54322 generation 0";

    private static string Sdp(params string[] candidates) => string.Join(
        "\r\n",
        [
            "v=0",
            "o=- 4611731400430051336 2 IN IP4 127.0.0.1",
            "s=-",
            "t=0 0",
            "a=group:BUNDLE 0",
            "a=ice-ufrag:abcd",
            "a=ice-pwd:0123456789abcdef01234567",
            "a=fingerprint:sha-256 AA:BB:CC",
            "m=video 9 UDP/TLS/RTP/SAVPF 96",
            "c=IN IP4 0.0.0.0",
            "a=setup:passive",
            "a=mid:0",
            "a=rtcp-mux",
            "a=sendrecv",
            "a=rtpmap:96 H264/90000",
            .. candidates,
            "a=end-of-candidates",
            "",
        ]);

    [Fact]
    public void 主機候選的位址與埠都換成對外值()
    {
        var rewritten = IceCandidateRewriter.Rewrite(Sdp(HostCandidate), "203.0.113.50", 3478);

        Assert.Contains("a=candidate:1 1 UDP 2130706431 203.0.113.50 3478 typ host", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void 沒有指定對外埠時保留原本的listen埠()
    {
        // 埠映射（port forward）通常由防火牆決定實際對外埠，維運常常只知道位址。
        // 這種情況下換掉位址卻把埠也換掉，會直接讓本來可用的部署壞掉。
        var rewritten = IceCandidateRewriter.Rewrite(Sdp(HostCandidate), "203.0.113.50", publicPort: null);

        Assert.Contains("203.0.113.50 54321 typ host", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void 反射與relay候選完全不動()
    {
        // srflx／relay 是 STUN 與 TURN 產生的，本來就是對外位址；改寫只會把它弄錯。
        var original = Sdp(HostCandidate, SrflxCandidate, RelayCandidate);
        var rewritten = IceCandidateRewriter.Rewrite(original, "203.0.113.50", 3478);

        Assert.Contains(SrflxCandidate, rewritten, StringComparison.Ordinal);
        Assert.Contains(RelayCandidate, rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void 候選的其他欄位原封不動()
    {
        var rewritten = IceCandidateRewriter.Rewrite(Sdp(HostCandidate), "203.0.113.50", 3478);

        // foundation、priority、generation、ufrag、network-cost 都必須保留，
        // 少一個就會改變 ICE 的配對優先序。
        Assert.Contains("a=candidate:1 1 UDP 2130706431 203.0.113.50 3478 typ host generation 0 ufrag abcd network-cost 999",
            rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void 非候選的行完全不動()
    {
        var original = Sdp(HostCandidate);
        var rewritten = IceCandidateRewriter.Rewrite(original, "203.0.113.50", 3478);

        foreach (var line in original.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line.StartsWith("a=candidate:", StringComparison.Ordinal)) continue;
            Assert.Contains(line, rewritten, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void 保留原本的換行風格()
    {
        // 瀏覽器對 SDP 的換行容忍度不統一；把 CRLF 換成 LF 只會換來一個難查的協商失敗。
        var lf = Sdp(HostCandidate).Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains("\r\n", IceCandidateRewriter.Rewrite(Sdp(HostCandidate), "203.0.113.50", 3478), StringComparison.Ordinal);
        Assert.DoesNotContain("\r\n", IceCandidateRewriter.Rewrite(lf, "203.0.113.50", 3478), StringComparison.Ordinal);
    }

    [Fact]
    public void 沒有host候選時不會假裝改寫成功()
    {
        var original = Sdp(SrflxCandidate, RelayCandidate);

        Assert.False(IceCandidateRewriter.HasHostCandidate(original));
        Assert.Equal(original, IceCandidateRewriter.Rewrite(original, "203.0.113.50", 3478));
    }

    [Fact]
    public void 有host候選時可以被偵測到()
    {
        Assert.True(IceCandidateRewriter.HasHostCandidate(Sdp(HostCandidate)));
    }

    [Fact]
    public void 欄位數不足的怪候選原樣保留()
    {
        // 寧可不改，也不要猜測格式後改出一個壞掉的候選。
        var odd = "a=candidate:1 1 UDP";
        var sdp = Sdp(odd);

        Assert.Equal(sdp, IceCandidateRewriter.Rewrite(sdp, "203.0.113.50", 3478));
    }
}

/// <summary>設定到 SIPSorcery ICE 伺服器清單的展開測試。</summary>
public class WhepIceServerTests
{
    [Fact]
    public void Stun排在Turn之前讓瀏覽器優先直連()
    {
        // relay 的流量與延遲成本高得多，能直連就不該走 TURN。
        var servers = WhepPeer.BuildIceServers(new WhepOptions
        {
            StunServers = ["stun:stun.example.com:3478"],
            TurnServers = [new WhepIceServer("turn:turn.example.com:3478", "u", "p")],
        });

        Assert.Equal(2, servers.Count);
        Assert.Equal("stun:stun.example.com:3478", servers[0].urls);
        Assert.Equal("turn:turn.example.com:3478", servers[1].urls);
    }

    [Fact]
    public void Turn的認證會一起帶上去()
    {
        var servers = WhepPeer.BuildIceServers(new WhepOptions
        {
            TurnServers = [new WhepIceServer("turn:turn.example.com:3478?transport=tcp", "user", "secret")],
        });

        var turn = Assert.Single(servers);
        Assert.Equal("user", turn.username);
        Assert.Equal("secret", turn.credential);
    }

    [Fact]
    public void 沒有任何設定時不產生Ice伺服器()
    {
        // 空的 iceServers 是合法且常見的本機部署；塞預設 STUN 反而會讓離線環境卡在 gathering。
        Assert.Empty(WhepPeer.BuildIceServers(new WhepOptions()));
    }

    [Fact]
    public void 展開時保留原始順序()
    {
        var servers = WhepPeer.BuildIceServers(new WhepOptions
        {
            StunServers = ["stun:a:3478", "stun:b:3478"],
            TurnServers =
            [
                new WhepIceServer("turn:a:3478", null, null),
                new WhepIceServer("turns:b:5349", "u", "p"),
            ],
        });

        Assert.Equal(
            ["stun:a:3478", "stun:b:3478", "turn:a:3478", "turns:b:5349"],
            servers.Select(s => s.urls).ToArray());
    }
}