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

    /// <summary>送一個帶指定 SSRC 的封包（模擬「同一個埠上還有第二個發送者」）。</summary>
    private static async Task SendWithSsrcAsync(RtpIngest ingest, ushort sequenceNumber, uint ssrc)
    {
        var packet = Packet(sequenceNumber);
        packet[8] = (byte)(ssrc >> 24);
        packet[9] = (byte)(ssrc >> 16);
        packet[10] = (byte)(ssrc >> 8);
        packet[11] = (byte)ssrc;

        using var udp = new UdpClient();
        await udp.SendAsync(packet, ingest.Target, CancellationToken.None);
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
    public async Task 不是本串流的封包不會被算成收包也不推進序號()
    {
        // 真正的來源：ffmpeg 把 RTCP 送到 `rtp://host:port` 的 port + 1，而那個號碼常常正好是
        // 另一條通道的 ingest socket（loopback 的臨時埠是連續配發的）。RTCP 也是 version 2、
        // 長度欄位不為零，所以會通過 RTP 的標頭檢查——它的「序號」其實是 RTCP 長度（Sender
        // Report 常見值 6），它的「SSRC」是 NTP 秒數高位。實測後果：每隔 5 秒一次，序號從
        // 三萬多「跳回 6」，`packetsMissed` 於是報出 30,000+ 的假漏包。
        await using var ingest = new RtpIngest();
        var foreign = new List<(byte PayloadType, ushort SequenceField)>();
        ingest.OnForeignPacket = (pt, seq) => foreign.Add((pt, seq));
        ingest.Start();

        await SendAsync(ingest, 100);
        await SendRtcpSenderReportAsync(ingest);
        await SendAsync(ingest, 101);
        await WaitForPacketsAsync(ingest, 2);   // 只等真正的 RTP

        Assert.Equal(2, ingest.PacketCount);
        Assert.Equal(0, ingest.PacketsMissed);
        Assert.Equal(1, ingest.ForeignPackets);
        var seen = Assert.Single(foreign);
        // RTP 標頭的 PT 欄位只有 7 位元，所以 RTCP Sender Report 的 200（0xC8）讀出來是 72
        // （0xC8 & 0x7F）——現場日誌裡的 `pt=72` 就是它。
        Assert.Equal(72, seen.PayloadType);
        Assert.Equal(6, seen.SequenceField);    // RTCP 長度欄（＝被誤讀成序號的那個位址）
    }

    /// <summary>送一個 RTCP Sender Report 形狀的封包到 ingest 埠（ffmpeg 會送到 port + 1）。</summary>
    private static async Task SendRtcpSenderReportAsync(RtpIngest ingest)
    {
        // 0x80 = V2/無 padding/RC=0；0xC8 = PT 200（Sender Report）；長度 6（7 個 32-bit 字）；
        // SSRC、NTP 秒數高位、NTP 小數、RTP 時間戳、封包數、位元組數。
        var packet = new byte[28];
        packet[0] = 0x80;
        packet[1] = 0xC8;
        packet[2] = 0x00;
        packet[3] = 0x06;
        packet[4] = 0x11; packet[5] = 0x22; packet[6] = 0x33; packet[7] = 0x44;
        packet[8] = 0xEE; packet[9] = 0xA6; packet[10] = 0x11; packet[11] = 0xE2;   // NTP 秒數高位
        for (var i = 12; i < packet.Length; i++) packet[i] = (byte)i;

        using var udp = new UdpClient();
        await udp.SendAsync(packet, ingest.Target, CancellationToken.None);
    }

    [Fact]
    public async Task 一次大跳號會被單獨回報而不是只算成漏包()
    {
        // 現場問題：「packetsMissed 三萬多」是不是掉包？穩定掉包與序號跳號（publisher 重啟、
        // 攝影機端重建串流）在計數器上長得一樣，但處置完全不同——前者要查緩衝與抽包速度，
        // 後者什麼都不用修。所以超過門檻的一次空隙要另外回報出來。
        await using var ingest = new RtpIngest();
        var jumps = new List<(int Gap, ushort From, ushort To)>();
        ingest.OnLargeGap = (gap, from, to) => jumps.Add((gap, from, to));
        ingest.Start();

        await SendAsync(ingest, 100, 30000);

        await WaitForPacketsAsync(ingest, 2);

        var jump = Assert.Single(jumps);
        // Gap 是「兩包之間隔了幾個序號」（30000 − 100 = 29900），漏包數才是它減一（101..29999）。
        // 這個差別就是本專案的量測腳本踩過的坑：把 gap 當成漏包數會變成每一包都多算一個。
        Assert.Equal(29900, jump.Gap);
        Assert.Equal(100, jump.From);
        Assert.Equal(30000, jump.To);
        Assert.Equal(29899, ingest.PacketsMissed);
    }

    [Fact]
    public async Task 第二個SSRC出現時會被回報()
    {
        // 同一個 socket 上兩個發送者（publisher 重建時舊的還沒被殺掉、新的已經開始送，而
        // loopback 埠被系統回收成同一個號碼）會讓序號交錯；漏包計數器於是報出「一次跳了三萬」，
        // 看起來像掉包災難，其實一個封包都沒掉。這條回報是分辨兩者的唯一線索。
        await using var ingest = new RtpIngest();
        var changes = new List<(uint From, uint To, ushort Seq)>();
        ingest.OnSourceChange = (from, to, seq) => changes.Add((from, to, seq));
        ingest.Start();

        await SendAsync(ingest, 100);                                   // SSRC 固定 0xCAFEBABE
        await SendWithSsrcAsync(ingest, 200, 0x12345678);
        await WaitForPacketsAsync(ingest, 2);

        var change = Assert.Single(changes);
        Assert.Equal(0xCAFEBABEu, change.From);
        Assert.Equal(0x12345678u, change.To);
    }

    [Fact]
    public async Task 換了SSRC不會被算成漏包()
    {
        // 序號空間是「每個 SSRC 各自一條」。同一個埠上出現第二個發送者（publisher 重建、
        // ffmpeg 重新初始化輸出）時，拿舊基準去比會報出一次三萬多的假漏包——實測就是這樣
        // 出現「packetsMissed 三萬多」而畫面完全正常。
        await using var ingest = new RtpIngest();
        ingest.Start();

        await SendAsync(ingest, 100, 101);                              // 發送者 A（0xCAFEBABE）
        await SendWithSsrcAsync(ingest, 6, 0x12345678);                 // 發送者 B：序號從 6 開始
        await SendWithSsrcAsync(ingest, 7, 0x12345678);
        await WaitForPacketsAsync(ingest, 4);

        Assert.Equal(0, ingest.PacketsMissed);
        Assert.Equal(4, ingest.PacketCount);
    }

    [Fact]
    public async Task 正常的一個接一個不會被當成大跳號()
    {
        // 反向保證：門檻寫得太小（例如 >= 1）會讓每一包都寫一行日誌，那就等於沒有這個診斷。
        await using var ingest = new RtpIngest();
        var jumps = new List<(int Gap, ushort From, ushort To)>();
        ingest.OnLargeGap = (gap, from, to) => jumps.Add((gap, from, to));
        ingest.Start();

        await SendAsync(ingest, 1, 2, 3, 4, 5, 6, 50, 51);

        await WaitForPacketsAsync(ingest, 8);

        Assert.Empty(jumps);
        Assert.Equal(43, ingest.PacketsMissed);   // 7..49 是真的漏掉（43 包），但那不是「跳號」
        Assert.True(RtpIngest.LargeGapThreshold > 43);
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
