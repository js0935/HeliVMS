using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using HeliVMS.Rtc;

namespace HeliVMS.Rtc.Tests;

/// <summary>
/// 用<b>真正的 ffmpeg</b> 驗證 publisher 管線。
///
/// <para>
/// 為什麼需要這一層：合成 RTP 封包能驗證解析器，卻驗證不了「ffmpeg 願不願意接受這些參數」。
/// M244 首次實作時把輸出 muxer 寫成 <c>-f rte</c>——ffmpeg 根本沒有這個 muxer，會直接回
/// "Requested output format 'rte' is not known" 而拒絕啟動，於是每一路 publisher 都在啟動瞬間掛掉，
/// 症狀卻看起來只是「攝影機連不上」。所有純函式測試當時都全綠，因為它們根本不碰 ffmpeg。
/// </para>
///
/// <para>
/// 這裡用 <c>lavfi testsrc</c> 當攝影機，於是<b>不需要真實攝影機</b>就能驗到完整的
/// libx264 → RTP → <see cref="RtpIngest"/> → <see cref="RtpHeader"/> 路徑。
/// 環境沒有 ffmpeg（或沒有 libx264）時會被<b>略過</b>而不是失敗：驗證外部工具的行為
/// 不是產品契約，但也不能假設每台機器都裝了 ffmpeg。
/// </para>
/// </summary>
public sealed class PublishPipelineTests
{
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(45);

    /// <summary>取出 <see cref="LiveEncodeOptions.BuildArguments"/> 實際使用的輸出 muxer 名稱。</summary>
    private static string MuxerOf(LiveEncodeOptions options)
    {
        var args = options.BuildArguments("rtsp://127.0.0.1/live", "rtp://127.0.0.1:5004").ToArray();
        return args[Array.IndexOf(args, "-f") + 1];
    }

    private static Process StartProbe(params string[] probeArgs)
        => PublishPipelineProbe.StartProbe(probeArgs);

    private static bool FfmpegAvailable() => PublishPipelineProbe.FfmpegAvailable;

    private static bool Libx264Available() => PublishPipelineProbe.Libx264Available;

    /// <summary>
    /// ffmpeg 接受的 muxer 名稱必須真的存在於這台機器的 ffmpeg。
    /// 這是本檔案存在的第一個理由：拼錯 muxer 的後果是 100% 串流失效，而回報出來的症狀會誤導排查方向。
    /// </summary>
    [SkippableFact]
    public void 指定的RtpMuxer必須真的存在於ffmpeg()
    {
        Skip.IfNot(FfmpegAvailable(), "ffmpeg 不在 PATH 上。");

        using var probe = StartProbe("-hide_banner", "-muxers");
        var muxers = probe.StandardOutput.ReadToEnd();
        probe.WaitForExit(20_000);

        var muxer = MuxerOf(LiveEncodeOptions.Default);

        // 只比對 muxer 名稱所在的行，避免 "rtp" 誤命中 "rtp_mpegts" 或 SDP 內容。
        var declared = muxers
            .Split('\n')
            .Any(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Any(token => token.Equals(muxer, StringComparison.Ordinal)));

        Assert.True(
            declared,
            $"ffmpeg 沒有宣告 muxer「{muxer}」——LiveEncodeOptions 用的是不存在的輸出格式名稱。");
    }

    /// <summary>
    /// 以 <c>lavfi testsrc</c> 取代攝影機，跑通 ffmpeg → RTP ingest → 解析 → 轉發的原料來源。
    /// </summary>
    [SkippableFact]
    public async Task 真的Ffmpeg能把H264送進Ingest並被解析()
    {
        Skip.IfNot(FfmpegAvailable(), "ffmpeg 不在 PATH 上。");
        Skip.IfNot(Libx264Available(), "這個 ffmpeg 沒有 libx264。");

        var idr = new TaskCompletionSource<RtpHeader>(TaskCreationOptions.RunContinuationsAsynchronously);
        var payloadPackets = 0;
        var markerFrames = 0;

        await using var ingest = new RtpIngest();
        ingest.OnPacket = (header, payload) =>
        {
            if (payload.Length == 0) return;

            Interlocked.Increment(ref payloadPackets);
            if (header.MarkerBit) Interlocked.Increment(ref markerFrames);

            // H.264 over RTP 是 packetization-mode 1：payload 第一個位元組就是 NAL header。
            // 只認真正的 IDR（NAL type 5），因為第一個送出的影格之前可能有別的 NAL。
            if ((payload.Span[0] & 0x1F) == 5) idr.TrySetResult(header);
        };

        ingest.Start();

        // 測試版編碼參數（低解析度、明確短 GOP），避免這支測試變慢。
        var encode = new LiveEncodeOptions(15, 300, 1.0);
        var gop = (15 * 1).ToString(CultureInfo.InvariantCulture);

        var args = new List<string>
        {
            "-hide_banner",
            "-loglevel", "error",
            "-f", "lavfi",
            "-i", "testsrc=size=320x240:rate=15",
            "-t", "6",
            "-an",
            "-c:v", "libx264",
            "-preset", "ultrafast",
            "-tune", "zerolatency",
            "-profile:v", "baseline",
            "-pix_fmt", "yuv420p",
            "-g", gop,
            "-keyint_min", gop,
            "-sc_threshold", "0",
            // muxer 名稱取自 BuildArguments：這支測試不自己再寫一份，
            // 否則改了正式程式碼這裡卻不會跟著變，測試就失去意義。
            "-f", MuxerOf(encode),
            "-payload_type", LiveEncodeOptions.PayloadType.ToString(CultureInfo.InvariantCulture),
            $"rtp://127.0.0.1:{ingest.Port}",
        };

        using var ffmpeg = new Process { StartInfo = { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true } };
        ffmpeg.StartInfo.FileName = "ffmpeg";
        foreach (var arg in args) ffmpeg.StartInfo.ArgumentList.Add(arg);
        ffmpeg.Start();

        var stderrTask = ffmpeg.StandardError.ReadToEndAsync();

        var gotIdr = await Task.WhenAny(idr.Task, Task.Delay(ReceiveTimeout)) == idr.Task;
        if (!ffmpeg.WaitForExit(15_000)) ffmpeg.Kill(entireProcessTree: true);

        var stderr = await stderrTask;

        Assert.True(gotIdr, $"真的 ffmpeg 沒有送出可解析的 RTP 封包（exit={ffmpeg.ExitCode}）。stderr：\n{stderr}");
        Assert.Equal(0, ffmpeg.ExitCode);

        var header = await idr.Task;

        // PT 必須與 WHEP answer 宣告的一致；不一致瀏覽器端就解不出畫面。
        Assert.Equal(LiveEncodeOptions.PayloadType, header.PayloadType);
        Assert.Equal(RtpHeader.FixedHeaderSize, header.PayloadOffset);
        Assert.True(header.PayloadLength > 0);

        // 短 GOP 與 marker bit 是「viewer 加入後很快看到畫面」的前提，順便在真實封包上確認。
        Assert.True(
            Volatile.Read(ref markerFrames) >= 1,
            $"沒有任何影格帶 marker bit（共收到 {Volatile.Read(ref payloadPackets)} 個有 payload 的封包）。");
        Assert.True(ingest.PacketCount > 10, $"只收到 {ingest.PacketCount} 個封包，遠少於預期。");
    }
}