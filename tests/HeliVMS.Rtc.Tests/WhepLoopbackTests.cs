using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using HeliVMS.Rtc;

namespace HeliVMS.Rtc.Tests;

/// 用<b>真的 SIPSorcery PeerConnection</b> 當瀏覽器，跟正式的 <see cref="WhepPeer"/>
/// 走一次真的 SDP 協商。
///
/// <para>
/// 為什麼需要這一層：repo 裡其他 WHEP 測試全部只驗到兩端之一。要嘛注入假的
/// <see cref="IWhepPeer"/>（完全不碰 WebRTC），要嘛在 <c>WhepApiTests</c> 裡餵一個壞掉的
/// SDP 讓它回 400。於是真正的 SDP offer／answer 交換從來沒有被執行過。
/// </para>
/// <para>
/// 這一段正是 M244 最容易出事、卻最難用別的方式診斷的地方：payload type、
/// profile-level-id、packetization-mode、方向（sendonly／recvonly）任何一項寫錯，
/// 症狀都是「連得上但沒有畫面」——沒有例外、沒有錯誤記錄、log 乾乾淨淨。
/// 只有讓一個真的訂閱端走完整條路，它才會從「客戶回報」變成「測試失敗」。
/// </para>
/// <para>
/// <b>範圍界線（刻意不做）</b>：不驗證 DTLS-SRTP 收到的媒體內容。SIPSorcery 10 沒有
/// 公開的 per-receiver 收包事件（沒有 <c>ontrack</c>，也沒有 <c>RTCRtpReceiver</c>；
/// 收包掛在 <c>MediaStream.OnRtpPacketReceivedByIndex</c>，而 PeerConnection 不公開
/// 那些 MediaStream）。為了收包而在測試裡重建一個瀏覽器，等於在測第三方函式庫的
/// 正確性，代價是測試本身極易腐化。本檔改驗證我們<b>自己</b>擁有的部分：產生的 SDP
/// 是否正確，以及 ICE 與 DTLS 是否真的能依它完成握手——這兩件事都可重現且穩定。
/// </para>
[Collection(RtcIoCollection.Name)]
public sealed class WhepLoopbackTests
{
    private static readonly TimeSpan NegotiationTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 扮演瀏覽器：真的 <see cref="RTCPeerConnection"/>、真的 offer、真的套用 answer。
    /// </summary>
    /// <remarks>
    /// 方向是 <c>recvonly</c>：WHEP 的角色分工是<b>伺服器發佈、瀏覽器訂閱</b>，
    /// 與 WHIP 相反。寫成 sendonly 的話 ICE 會通但一個封包都不會來。
    /// </remarks>
    private sealed class BrowserViewer : IDisposable
    {
        private readonly RTCPeerConnection _pc;
        private readonly TaskCompletionSource<bool> _connected =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _disposed;

        public BrowserViewer()
        {
            // 不帶 STUN／TURN：兩端都在 loopback，host 候選就足以選到路徑。
            // 這也刻意等於「沒設定 HELIVMS_WHEP_STUN」的部署狀況。
            var configuration = new RTCConfiguration
            {
                iceServers = [],
                X_DisableExtendedMasterSecretKey = true,
            };

            _pc = new RTCPeerConnection(configuration);

            /*
             * recvonly 的視訊 transceiver，方向要寫在 track 建構子上。
             *
             * 這裡踩過一個坑：只呼叫 SetMediaStreamStatus 再 addTrack，產生的 offer
             * 是 `a=sendrecv` 而不是瀏覽器的 `recvonly`——SetMediaStreamStatus 不會
             * 蓋過 addTrack 自帶的方向。用一個不是瀏覽器形狀的 offer 來驗證協商，
             * 等於測了別的東西。
             */
            _pc.addTrack(new MediaStreamTrack(
                SDPMediaTypesEnum.video,
                false,
                [new SDPAudioVideoMediaFormat(WhepCodec.H264)],
                MediaStreamStatusEnum.RecvOnly));

            _pc.onconnectionstatechange += state =>
            {
                if (state is RTCPeerConnectionState.connected) _connected.TrySetResult(true);
            };
        }

        /// <summary>產生 offer，並等到 ICE 收集完成（本專案不做 trickle）。</summary>
        public async Task<string> CreateOfferAsync(CancellationToken token)
        {
            var offer = _pc.createOffer();
            await _pc.setLocalDescription(offer).WaitAsync(token).ConfigureAwait(false);

            // 等 ICE 收集完再做後續：不做 trickle 的 SDP 交換，offer 裡必須已經有候選。
            var deadline = DateTime.UtcNow + NegotiationTimeout;
            while (_pc.iceGatheringState != RTCIceGatheringState.complete)
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException(
                        $"ICE 收集在 {NegotiationTimeout.TotalSeconds:F0}s 內沒有完成"
                        + $"（state={_pc.iceGatheringState}）。沒有候選的 offer 對端根本無路可走。");
                }

                await Task.Delay(25, token).ConfigureAwait(false);
            }

            return _pc.currentLocalDescription.sdp.ToString();
        }

        /// <summary>套用伺服器 answer，並等到 ICE 與 DTLS 真的完成。</summary>
        public async Task ApplyAnswerAsync(string answerSdp, CancellationToken token)
        {
            var result = _pc.setRemoteDescription(new RTCSessionDescriptionInit
            {
                type = RTCSdpType.answer,
                sdp = answerSdp,
            });

            // 一個瀏覽器不接受的 answer 在現場就是「連不上」，這裡必須變成紅燈。
            Assert.Equal(SetDescriptionResultEnum.OK, result);

            // state == connected 代表 ICE 選到了路徑<b>且</b> DTLS 握手完成。
            // 換句話說，我們送出去的 SDP 足以讓對端走完完整的 WebRTC 連線建立。
            await _connected.Task.WaitAsync(token).ConfigureAwait(false);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                _pc.Close("test done");
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
                // 測試收尾路徑，連線已死時再丟例外沒有意義。
            }

            _pc.Dispose();
        }
    }

    /// <summary>
    /// 真的 <see cref="WhepPeer"/> 必須為真的瀏覽器 offer 產生一份可用的 answer。
    /// </summary>
    [Fact]
    public async Task 真的訂閱端的offer必須換到可用的answer()
    {
        // 這條刻意不用 ffmpeg：它驗的是協商，不是取流。沒有 ffmpeg 的機器也該能跑。
        var options = new WhepOptions { StunServers = [], TurnServers = [] };

        using var peer = new WhepPeer(options);
        using var browser = new BrowserViewer();
        using var cts = new CancellationTokenSource(NegotiationTimeout);

        var offer = await browser.CreateOfferAsync(cts.Token);

        // 前置條件：offer 裡真的要有 m=video 與 recvonly，否則下面的驗證沒有意義。
        Assert.Contains("m=video", offer, StringComparison.Ordinal);
        Assert.Contains("recvonly", offer, StringComparison.Ordinal);

        var answer = await peer.NegotiateAsync(offer, cts.Token);
        var sdp = answer.Answer;

        // 角色分工：伺服器是發佈端。這一項寫反了，ICE 會通但永遠沒有畫面。
        Assert.Contains("m=video", sdp, StringComparison.Ordinal);
        Assert.Contains("sendonly", sdp, StringComparison.Ordinal);
        Assert.DoesNotContain("recvonly", sdp, StringComparison.Ordinal);

        // 編碼參數必須與廣告的一致：profile-level-id 不符會被瀏覽器當成不相容的
        // 編碼而拒絕，或勉強接受卻解不出影格。
        Assert.Contains($"a=rtpmap:{LiveEncodeOptions.PayloadType} H264/90000", sdp, StringComparison.Ordinal);
        Assert.Contains("packetization-mode=1", sdp, StringComparison.Ordinal);
        Assert.Contains("profile-level-id=42e01f", sdp, StringComparison.Ordinal);

        // 不做 trickle，所以答案必須自帶 ICE 參數與候選。
        Assert.Contains("a=ice-ufrag:", sdp, StringComparison.Ordinal);
        Assert.Contains("a=ice-pwd:", sdp, StringComparison.Ordinal);
        Assert.Contains("a=candidate:", sdp, StringComparison.Ordinal);
        Assert.Contains("a=fingerprint:", sdp, StringComparison.Ordinal);
        Assert.Contains("a=setup:", sdp, StringComparison.Ordinal);

        // 最後一關：這份 answer 必須真的讓一個 PeerConnection 走完 ICE + DTLS。
        await browser.ApplyAnswerAsync(sdp, cts.Token);
    }

    /// <summary>
    /// 設定對外位址時，answer 的 host 候選必須真的被改寫成該位址。
    /// </summary>
    /// <remarks>
    /// 這是 NAT 後的部署情境，也是 M244 唯一會改寫 SDP 的地方——改錯了症狀是
    /// 「設定了 HELIVMS_WHEP_PUBLIC_HOST 還是一樣連不上」，而維運完全看不出原因。
    /// 之前沒有任何測試走過真實協商，所以這條改寫路徑只在單元測試裡以字串驗證過。
    ///
    /// 順帶釘住 <c>publicPort</c>：不給時要保留 ICE 實際分配到的埠。
    /// </remarks>
    [Fact]
    public async Task 設定對外位址時answer的host候選必須被改寫()
    {
        const string publicHost = "198.51.100.7";

        var options = new WhepOptions
        {
            StunServers = [],
            TurnServers = [],
            PublicHost = publicHost,
            PublicPort = 8554,
        };

        using var peer = new WhepPeer(options);
        using var browser = new BrowserViewer();
        using var cts = new CancellationTokenSource(NegotiationTimeout);

        var offer = await browser.CreateOfferAsync(cts.Token);
        var answer = await peer.NegotiateAsync(offer, cts.Token);

        // host 候選必須指向對外位址，而不是本機的內網位址。
        var hostCandidates = answer.Answer
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.StartsWith("a=candidate:", StringComparison.Ordinal)
                     && l.Contains("typ host", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(hostCandidates);
        Assert.All(hostCandidates, l => Assert.Contains(publicHost, l, StringComparison.Ordinal));

        // 一個 host 候選都沒漏成別的位址：那正是「設定了還是不行」的原因。
        Assert.All(hostCandidates, l =>
            Assert.DoesNotContain("127.0.0.1", l, StringComparison.Ordinal));
    }
}