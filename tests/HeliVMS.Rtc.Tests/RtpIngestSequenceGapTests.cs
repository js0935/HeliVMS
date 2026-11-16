using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using HeliVMS.Rtc;

namespace HeliVMS.Rtc.Tests;

/// <summary>
/// ingest 端「依序號推算漏包」的行為測試。
/// <para>
/// 這個計數器存在的理由很具體：現場「WHEP 連得上、ICE／DTLS 都 established、
/// Chrome 每秒送 PLI、畫面十幾秒才出來一張」時，掉包可能發生在兩個完全不同的地方——
/// <b>ffmpeg→本機</b>（socket 緩衝太小、抽包太慢）或 <b>本機→瀏覽器</b>（轉送／SRTP）。
/// 兩者的畫面症狀一模一樣，處置卻相反；沒有這個數字就只能靠猜測去改編碼參數。
/// </para>
/// <para>
/// 因為本專案沒有接 RTX／NACK（SIPSorcery 不重傳），掉一個封包就毀一張影格，
/// 掉在 IDR 上更是要等下一個關鍵影格——所以「漏了幾個」必須量得準，
/// 包含 16 位元序號回繞，以及不能把遲到的舊封包算成遺失。
/// </para>
/// <para>
/// 這裡綁真的 loopback UDP socket 並送真的封包，因此掛在 <c>rtc-io</c> collection：
/// 這種測試與其他 socket／ffmpeg 測試平行跑時，埠會被別人搶走而變成間歇性假紅。
/// </para>
/// </summary>
[Collection(RtcIoCollection.Name)]
public class RtpIngestSequenceGapTests
{
    /// <summary>組一個最小的合法 RTP 封包（標頭 + 5 bytes payload）。</summary>
    private static byte[] Packet(ushort sequenceNumber) => PacketOfSize(sequenceNumber, 5);

    /// <summary>
    /// 組一個指定 payload 長度的 RTP 封包。
    /// <para>
    /// 爆量測試必須用<b>真實大小</b>的封包：17 bytes 的假封包怎麼塞都不會滿，
    /// 這個測試就會在「完全沒有修好」的程式上也全綠——那比沒有測試更糟。
    /// </para>
    /// </summary>
    private static byte[] PacketOfSize(ushort sequenceNumber, int payloadLength)
    {
        var packet = new byte[RtpHeader.FixedHeaderSize + payloadLength];
        packet[0] = 0x80;
        packet[1] = 0x80 | LiveEncodeOptions.PayloadType;
        packet[2] = (byte)(sequenceNumber >> 8);
        packet[3] = (byte)sequenceNumber;
        packet[4] = 0x00;
        packet[5] = 0x00;
        packet[6] = 0x03;
        packet[7] = 0xE8;
        packet[8] = 0xCA;
        packet[9] = 0xFE;
        packet[10] = 0xBA;
        packet[11] = 0xBE;
        packet[12] = 0x00;
        packet[13] = 0x00;
        packet[14] = 0x00;
        packet[15] = 0x01;
        packet[16] = 0x65;
        return packet;
    }

    private static async Task SendAsync(RtpIngest ingest, params ushort[] sequences)
    {
        using var udp = new UdpClient();
        foreach (var seq in sequences)
        {
            await udp.SendAsync(Packet(seq), ingest.Target, CancellationToken.None);
        }
    }

    /// <summary>
    /// UDP 沒有順序保證，這裡等的是「封包被處理完」而不是「送出去」。
    /// 用輪詢而不是固定 sleep，才不會在 CI 慢一點時假紅。
    /// </summary>
    private static async Task WaitForPacketsAsync(RtpIngest ingest, long expected)
    {
        var deadline = Stopwatch.StartNew();
        while (ingest.PacketCount < expected && deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(10);
        }

        Assert.True(
            ingest.PacketCount >= expected,
            $"等了 5 秒只收到 {ingest.PacketCount}/{expected} 個封包（loopback 送不進來，測試環境有問題）。");
    }

    [Fact]
    public async Task 序號空隙會累計成漏包數()
    {
        await using var ingest = new RtpIngest();
        ingest.Start();

        await SendAsync(ingest, 100, 101, 104);
        await WaitForPacketsAsync(ingest, 3);

        // 103 之前跳過了 102、103 兩個號碼 → 漏 2 包。
        Assert.Equal(3, ingest.PacketCount);
        Assert.Equal(2, ingest.PacketsMissed);
    }

    [Fact]
    public async Task 序號回繞不會被算成六萬多個漏包()
    {
        await using var ingest = new RtpIngest();
        ingest.Start();

        // 65535 → 0 是回繞後的下一個號碼，中間沒有空隙；1 則是跳過了 0。
        await SendAsync(ingest, 65534, 65535, 1);
        await WaitForPacketsAsync(ingest, 3);

        Assert.Equal(1, ingest.PacketsMissed);
    }

    [Fact]
    public async Task 重複與遲到的舊封包都不算漏包()
    {
        await using var ingest = new RtpIngest();
        ingest.Start();

        // 100 重送一次（UDP 重複）以及 99 遲到（亂序）都不該被記成遺失，
        // 否則一次亂序就報出上萬個遺失，這個數字就沒有人會相信了。
        await SendAsync(ingest, 100, 101, 100, 99, 102);
        await WaitForPacketsAsync(ingest, 5);

        Assert.Equal(0, ingest.PacketsMissed);
    }

    [Fact]
    public async Task 沒有漏包時計數器保持零()
    {
        await using var ingest = new RtpIngest();
        ingest.Start();

        await SendAsync(ingest, 1, 2, 3, 4, 5);
        await WaitForPacketsAsync(ingest, 5);

        Assert.Equal(0, ingest.PacketsMissed);
    }

    [Fact]
    public async Task 一次爆量送出的封包不會被socket緩衝吃掉()
    {
        await using var ingest = new RtpIngest();

        // 模擬「ffmpeg 一次吐出一整張 1080p IDR」：250 個封包（約 360 KB）幾乎同時到達，
        // 而抽包端此刻正忙（第一張封包刻意卡住 300 ms 才放手）。
        // 這是現場的實際形狀：2 Mbps 的 IDR 約 70–80 個封包、110 KB，而 Windows 的 UDP
        // 預設接收緩衝只有 64 KB——整張關鍵影格在緩衝裡就掉了一半，瀏覽器因此永遠
        // 湊不出一張完整的 IDR，只能一直送 PLI（症狀：ICE／DTLS 都通了、畫面十幾秒才出來）。
        var first = new TaskCompletionSource();
        ingest.OnPacket = (_, _) =>
        {
            if (!first.Task.IsCompleted)
            {
                first.TrySetResult();
                Thread.Sleep(300);
            }
        };

        ingest.Start();

        using var udp = new UdpClient();
        for (ushort seq = 1; seq <= 250; seq++)
        {
            await udp.SendAsync(PacketOfSize(seq, 1460), ingest.Target, CancellationToken.None);
        }

        await WaitForPacketsAsync(ingest, 250);

        Assert.Equal(250, ingest.PacketCount);
        Assert.Equal(0, ingest.PacketsMissed);
    }
}
