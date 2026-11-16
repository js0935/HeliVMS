using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace HeliVMS.Rtc;

/// <summary>一次 WHEP 協商的結果。</summary>
/// <param name="Answer">可直接回給瀏覽器的 answer SDP。</param>
public readonly record struct WhepAnswer(string Answer);

/// <summary>
/// 一個觀看者的 WebRTC 連線所需的能力。
/// <para>
/// 抽出介面只為了一個實務理由：<c>WhepPeer</c> 的建構會真的建立
/// <c>RTCPeerConnection</c>（綁 UDP socket、產生 DTAS 憑證）。若測試直接用它，
/// 每次都得到一個會佔用真實資源又立刻被丟掉的連線，而會話帳本的測試根本不需要
/// 任何 WebRTC。介面讓那些測試可以用假的實作。
/// </para>
/// </summary>
public interface IWhepPeer : IDisposable
{
    /// <summary>處理瀏覽器的 offer 並產生 answer。</summary>
    Task<WhepAnswer> NegotiateAsync(string offerSdp, CancellationToken token);

    /// <summary>把攝影機來的一個 RTP 封包轉給瀏覽器（不解碼）。</summary>
    void Forward(RtpHeader header, ReadOnlySpan<byte> payload);

    /// <summary>關閉連線並釋放底層資源。</summary>
    void Close(string reason);

    /// <summary>
    /// 對端連線已經確定斷了（SIPSorcery 回報 disconnected／failed／closed）。
    /// </summary>
    /// <remarks>
    /// 這是<b>唯一</b>能收掉「瀏覽器消失但媒體還在流」那條會話的訊號：逾時回收看的是
    /// 「有沒有封包轉送出去」，而這種會話每一秒都還在轉送，因此永遠不會逾時。
    /// 沒有證據時必須回 <c>false</c>——誤判會把正在看的操作員踢掉。
    /// </remarks>
    bool IsDisconnected { get; }
}

/// <summary>
/// 單一觀看者的 WebRTC 連線（瀏覽器一邊，伺服器一邊）。
/// <para>
/// 這一型別刻意做得很薄：所有真正的複雜度都留在 <see cref="LiveStreamService"/>，
/// 而 SIPSorcery 的建立／協商／關閉集中在此，好讓協商失敗只有一個地方會發生。
/// </para>
/// <para>
/// 伺服器在這裡是<b>送</b>端（answer 為 <c>a=sendonly</c>）：WHEP 的角色分工是
/// 伺服器發佈、瀏覽器訂閱，與 WHIP 相反。
/// </para>
/// </summary>
public sealed class WhepPeer : IWhepPeer
{
    private readonly WhepOptions _options;
    private readonly RTCPeerConnection _pc;
    private bool _disposed;

    /// <summary>
    /// 這一條會話<b>協商到的</b>視訊 payload type（見 <see cref="WhepPayloadType"/>）。
    /// </summary>
    /// <remarks>
    /// 協商前是我們的常數（<see cref="LiveEncodeOptions.PayloadType"/>），協商後被 answer 裡真正的
    /// 編號取代。<see cref="NegotiateAsync"/> 之前的 <see cref="Forward"/> 沒有意義（還沒有會話），
    /// 所以不會有「用常數送出去」的窗口。
    /// </remarks>
    public int VideoPayloadType { get; private set; } = LiveEncodeOptions.PayloadType;

    public WhepPeer(WhepOptions options)
    {
        _options = options;

        var configuration = new RTCConfiguration
        {
            iceServers = BuildIceServers(options),
            // 純託管 SharpSRTP 預設走 EMS（RFC 7627 的擴充 master secret）。
            // 部分舊版 Safari 仍只認 RFC 5764 的預設 master secret，故保留可關閉。
            X_DisableExtendedMasterSecretKey = true,
        };

        _pc = new RTCPeerConnection(configuration);

        // 分頁被關掉、瀏覽器當掉或網路斷掉時，DELETE 不一定送得到，而媒體仍持續有封包
        // （每一包都會 Touch 會話），所以「沒有活動」永遠不會成立：那條會話會一直佔著
        // 觀看名額，publisher 也永遠不關——實測瀏覽器被 kill 之後，10 路 publisher 與
        // 10 條會話在幾分鐘後仍然活著（狀態端點回報 viewers=1，而已經沒有瀏覽器了）。
        //
        // 三個訊號，任何一個成立就足夠；全部都是「對端自己說的」，我們不猜：
        //   1. connectionState 變成 disconnected／failed／closed。
        //   2. SIPSorcery 的媒體逾時：它在一個期間內<b>沒有收到</b>任何 RTP／RTCP。
        //      這是關鍵的一個——我們持續送媒體出去並不算「收到」，所以它與送出去的量無關。
        //   3. 對端送來 RTCP BYE（正常關閉）。
        // 反過來說，這裡刻意<b>不</b>用「我們送不出去」判斷：本機 UDP 送給一個已關閉的埠
        // 不一定會回報錯誤，Windows 上的失敗形式也不一致。
        _pc.onconnectionstatechange += state =>
        {
            if (state is RTCPeerConnectionState.disconnected
                or RTCPeerConnectionState.failed
                or RTCPeerConnectionState.closed)
            {
                _disconnected = true;
                WhepDiagnostics.Write($"peer={PeerTag} connection-state {state} viewer_silent_seconds={ViewerSilenceSeconds()}");
            }
        };

        // 觀眾的存活訊號：瀏覽器會持續回 RTCP（RR，沒有畫面時是 PLI——實測每秒一次），
        // 所以「有沒有收到對端的封包」正是「這個人還在不在」。單調時鐘（TickCount64）而非牆鐘：
        // 這裡要的是「距上次收到多久」，而牆鐘被校正時那個距離會變成負的或好幾十年。
        _pc.OnReceiveReport += (_, _, _) => Interlocked.Exchange(ref _viewerActivityMs, Environment.TickCount64);
        _pc.OnTimeout += media =>
        {
            _viewerTimedOut = true;
            WhepDiagnostics.Write($"peer={PeerTag} timeout media={media} viewer_silent_seconds={ViewerSilenceSeconds()}");
        };
        _pc.OnRtcpBye += reason =>
        {
            _disconnected = true;
            WhepDiagnostics.Write($"peer={PeerTag} rtcp-bye reason={reason}");
        };

        var track = new MediaStreamTrack(SDPMediaTypesEnum.video, false, [new SDPAudioVideoMediaFormat(WhepCodec.H264)]);
        _pc.addTrack(track);
        _pc.SetMediaStreamStatus(SDPMediaTypesEnum.video, MediaStreamStatusEnum.SendOnly);
    }

    /// <inheritdoc />
    public bool IsDisconnected => _disconnected || _viewerTimedOut;

    private volatile bool _disconnected;
    private volatile bool _viewerTimedOut;
    private long _viewerActivityMs;

    /// <summary>觀眾最後一次送來 RTCP 距今幾秒；從未收到時回 <c>-1</c>。</summary>
    private long ViewerSilenceSeconds()
    {
        var last = Interlocked.Read(ref _viewerActivityMs);
        return last == 0 ? -1 : (Environment.TickCount64 - last) / 1000;
    }

    /// <summary>日誌行的身分欄：同一條會話的多次事件要能歸在一起（WhepPeer 不知道自己的會話 ID）。</summary>
    private int PeerTag => GetHashCode();

    /// <summary>
    /// 把設定展開成 SIPSorcery 的 ICE 伺服器清單。
    /// <para>
    /// 順序有意義：STUN 在前、TURN 在後，讓瀏覽器優先走直連路徑，把 TURN 當最後手段——
    /// relay 的流量與延遲成本都高得多。
    /// </para>
    /// </summary>
    internal static List<RTCIceServer> BuildIceServers(WhepOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var servers = new List<RTCIceServer>(options.StunServers.Length + options.TurnServers.Count);
        foreach (var url in options.StunServers)
        {
            servers.Add(new RTCIceServer { urls = url });
        }

        foreach (var turn in options.TurnServers)
        {
            servers.Add(new RTCIceServer
            {
                urls = turn.Url,
                username = turn.Username,
                credential = turn.Credential,
            });
        }

        return servers;
    }

    /// <summary>
    /// 處理瀏覽器的 offer 並產生 answer。
    /// </summary>
    /// <remarks>
    /// 例外會被轉成帶明確訊息的 <see cref="WhepNegotiationException"/>：直接把
    /// SIPSorcery 的 <c>VideoIncompatible</c> 之類原始碼丟給 API 端點，
    /// 只會得到一個對維運毫無幫助的 500。
    /// </remarks>
    public async Task<WhepAnswer> NegotiateAsync(string offerSdp, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(offerSdp);

        // 這層大小上限是對抗「一個 offer 就吃掉整台主機記憶體」的第一道線。
        // 真實的 WHEP offer 約 1.5–3 KB；64 KB 已遠高於上限。
        if (offerSdp.Length > WhepLimits.MaxOfferBytes)
        {
            throw new WhepNegotiationException(
                $"offer 過大（{offerSdp.Length} bytes，上限 {WhepLimits.MaxOfferBytes}）");
        }

        SetDescriptionResultEnum result;
        try
        {
            result = _pc.setRemoteDescription(new RTCSessionDescriptionInit
            {
                type = RTCSdpType.offer,
                sdp = offerSdp,
            });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IndexOutOfRangeException)
        {
            throw new WhepNegotiationException($"offer 不是合法的 SDP：{ex.Message}");
        }

        if (result != SetDescriptionResultEnum.OK)
        {
            // VideoIncompatible 的實務意義是「瀏覽器只認 Safari 專屬的 H.265，
            // 或編碼不符」；這值得一個可讀的訊息，因為它是選編碼時的後果。
            throw new WhepNegotiationException(
                $"無法與瀏覽器協商視訊格式（{result}）。本系統只提供 H.264／packetization-mode=1。");
        }

        token.ThrowIfCancellationRequested();

        var answer = _pc.createAnswer();
        await _pc.setLocalDescription(answer).WaitAsync(token).ConfigureAwait(false);

        // ICE 候選必須收完才能給瀏覽器：本專案刻意不做 trickle（無 PATCH 端點），
        // 所以答案裡若缺候選，瀏覽器永遠選不到路徑。
        var local = _pc.currentLocalDescription.sdp;
        if (local.Media.Count == 0 || local.Media[0].IceCandidates.Count == 0)
        {
            throw new WhepNegotiationException("ICE 候選收集為空：請檢查 HELIVMS_WHEP_STUN 與網路防火牆設定。");
        }

        var answerSdp = local.ToString();

        // payload type 由對端決定：answer 的 m=video 用 Chrome offer 的編號（常見 102），
        // 而我們原本固定送 96（自己 offer 的編號）。96 在 Chrome 的 offer 裡是 VP8，
        // 於是它把 H.264 位元流交給 VP8 解碼器——連得上、一張都解不出來。
        // 這裡把真正協商到的編號記下來，Forward 之後照它送。
        VideoPayloadType = WhepPayloadType.FromAnswerSdp(answerSdp) ?? LiveEncodeOptions.PayloadType;

        // 有設定對外位址就改寫 host 候選。改寫不到時要明確報錯：靜默回一個仍然只有內網位址的
        // answer，維運只會看到「設定了還是不行」，而真正的原因是設定值沒被套用。
        if (_options.PublicHost is { } publicHost)
        {
            if (!IceCandidateRewriter.HasHostCandidate(answerSdp))
            {
                throw new WhepNegotiationException(
                    "已設定 HELIVMS_WHEP_PUBLIC_HOST，但這台機器的 ICE 候選裡沒有 host 候選可替換。");
            }

            answerSdp = IceCandidateRewriter.Rewrite(answerSdp, publicHost, _options.PublicPort);
        }

        return new WhepAnswer(answerSdp);
    }

    /// <summary>
    /// 把攝影機來的一個 RTP 封包轉給瀏覽器。
    /// <para>
    /// <b>不解碼</b>：payload 原封不動送出，只重組標頭的時序欄位。
    /// 這是 M244 讓 CPU 只隨通道數（而非觀看人數）成長的地方。
    /// </para>
    /// </summary>
    public void Forward(RtpHeader header, ReadOnlySpan<byte> payload)
    {
        if (_disposed) return;

        _pc.SendRtpRaw(
            SDPMediaTypesEnum.video,
            payload.ToArray(),
            header.Timestamp,
            header.MarkerBit ? 1 : 0,
            // 必須是**協商到的**編號（見 VideoPayloadType）：寫死成我們的常數 96 時，
            // 瀏覽器會照它自己 offer 的對應把位元流交給另一個解碼器，症狀是連得上、沒有畫面。
            VideoPayloadType,
            header.SequenceNumber);
    }

    public void Close(string reason)
    {
        if (_disposed) return;
        try
        {
            _pc.Close(reason);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // 關閉是清理路徑，連線已死時再丟例外沒有意義。
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _pc.Dispose();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
        }
    }
}

/// <summary>WHEP 協商失敗。呼叫端據此回 400，並記錄原因。</summary>
public sealed class WhepNegotiationException : Exception
{
    public WhepNegotiationException(string message) : base(message)
    {
    }
}

/// <summary>WHEP 協議的硬性限制。</summary>
public static class WhepLimits
{
    /// <summary>offer SDP 的大小上限。</summary>
    public const int MaxOfferBytes = 64 * 1024;

    /// <summary>單一 RTP 封包大小上限（略高於 MTU 1500 以容忍 IP/VLAN 開銷）。</summary>
    public const int MaxRtpPacketBytes = 2048;
}

/// <summary>M244 對外廣告的編碼參數。</summary>
public static class WhepCodec
{
    /// <summary>
    /// H.264 baseline／constrained baseline，90000 Hz，packetization-mode=1。
    /// <para>
    /// 選 H.264 而非 VP8/AV1，因為 Safari／iOS 的 WebRTC 只認 H.264；
    /// 選 baseline 而非 high，因為 baseline 無 B 影格，不需重排即可低延遲。
    /// profile-level-id 用 42e01f（constrained baseline level 3.1），
    /// 這是 Chrome／Safari／Firefox 交集最廣的一組。
    /// </para>
    /// </summary>
    public static VideoFormat H264 { get; } = new(
        VideoCodecsEnum.H264,
        LiveEncodeOptions.PayloadType,
        90000,
        "level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42e01f");
}