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
    private readonly RTCPeerConnection _pc;
    private bool _disposed;

    public WhepPeer(WhepOptions options)
    {
        var configuration = new RTCConfiguration
        {
            iceServers = options.StunServers
                .Select(url => new RTCIceServer { urls = url })
                .ToList(),
            // 純託管 SharpSRTP 預設走 EMS（RFC 7627 的擴充 master secret）。
            // 部分舊版 Safari 仍只認 RFC 5764 的預設 master secret，故保留可關閉。
            X_DisableExtendedMasterSecretKey = true,
        };

        _pc = new RTCPeerConnection(configuration);

        var track = new MediaStreamTrack(SDPMediaTypesEnum.video, false, [new SDPAudioVideoMediaFormat(WhepCodec.H264)]);
        _pc.addTrack(track);
        _pc.SetMediaStreamStatus(SDPMediaTypesEnum.video, MediaStreamStatusEnum.SendOnly);
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

        return new WhepAnswer(local.ToString());
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
            LiveEncodeOptions.PayloadType,
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