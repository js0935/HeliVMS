using System.Text;
using HeliVMS.Qctek.Live;
using HeliVMS.Qctek.Nemo;
using Xunit;

namespace HeliVMS.Qctek.Tests;

/// <summary>
/// QCC 的 keepalive（raw 0x90，QCTEK_RM 開頭）直接送入時要交棒 live 通道——
/// 黃金抓包（docs/QCC-LIVE-DIAGNOSIS.md §5c）證明真機的預覽長連線沒有
/// GET_LIVE_START，第一個 keepalive 就是 live 通道的起點。
/// </summary>
public class QctekKeepAliveHandoffTests
{
    [Fact]
    public void keepalive直接送入會交棒live通道且不耗用位元組()
    {
        var session = new QctekSession(new KeepAliveContext());

        // 第一個 keepalive 完整到達（0x90 bytes）。
        var packet = KeepAlivePacket();
        var result = session.Advance(packet);

        Assert.True(result.Live);
        Assert.Null(result.Reply);
        // 位元組全部保留給 QctekLiveSession（它的 initial 會含這一個與後續的
        // keepalive）；交棒本身不耗用——耗用交給 NemoSessionLoop 的
        // pending.Consume(result.Consumed)，這裡回 0 表示「原樣帶走」。
        Assert.Equal(0, result.Consumed);
    }

    [Fact]
    public void keepalive帶著後續半個keepalive時交棒並保留全部緩衝()
    {
        var session = new QctekSession(new KeepAliveContext());
        var first = KeepAlivePacket();
        var buffer = new byte[first.Length + 0x30]; // 1 個完整 + 半個（全零）
        first.CopyTo(buffer, 0);

        var result = session.Advance(buffer);

        Assert.True(result.Live);
        Assert.Equal(0, result.Consumed);
    }

    [Fact]
    public void 一般文字命令不受keepalive識別影響()
    {
        var session = new QctekSession(new KeepAliveContext());

        var result = session.Advance("CHECK_SERVER_VERSION CLIENT_AUTH QCTEK\0"u8.ToArray());

        Assert.False(result.Live);
        Assert.NotNull(result.Reply);
    }

    [Fact]
    public void 警報心跳夾在keepalive之間時重新同步不丟後面的keepalive()
    {
        // 運行時實錄：QCC 把 ALARM_HELLO（NEMO 框）發到與預覽共用的連線上。
        // PendingKeepAlives.Resync 要丟掉 NEMO 框、對齊下一個 QCTEK_RM 頭，
        // 讓後面的 keepalive 正常解析——斷線會同時殺掉警報與預覽。
        var keep1 = KeepAlivePacket();
        var alarm = Encoding.ASCII.GetBytes("NEMO 22         ALARM_HELLO 1 =QCV8001\0");
        var keep2 = KeepAlivePacket();
        var stream = new MemoryStream();
        stream.Write(keep1);
        stream.Write(alarm);
        stream.Write(keep2);

        var pending = new PendingKeepAlives(stream.ToArray());

        Assert.True(pending.TryParseNext(out _, out _)); // keep1

        Assert.True(pending.Resync() > 0); // 丟掉 NEMO 框
        Assert.True(pending.TryParseNext(out var sub, out var bits)); // keep2
        Assert.True(bits[0]);
        Assert.Equal(0, sub);
    }

    [Fact]
    public void 只有雜訊沒有下一個頭時保留尾端半個頭()
    {
        var noise = Encoding.ASCII.GetBytes("NEMO 22         ALARM_HELLO 1 =QCV8001NEMO 22");
        var pending = new PendingKeepAlives(noise);

        var discarded = pending.Resync();

        // 保留尾端 7 bytes（可能是半個 QCTEK_RM 頭），其餘全丟。
        Assert.Equal(noise.Length - 7, discarded);
    }

    private static byte[] KeepAlivePacket()
    {
        var packet = new byte[QctekLiveSession.KeepAliveLength];
        "QCTEK_RM"u8.CopyTo(packet);
        packet[8] = 0x01;
        packet[11] = 0x01; // 01 00 00 01
        packet[16] = 0x01; // 位圖 bit0 = 頻道 0
        return packet;
    }

    private sealed class KeepAliveContext : IQctekDeviceContext
    {
        public string ServerVersion => "QCV8001";

        public int ChannelCount => 1;

        public IReadOnlyList<string> ChannelNames => ["攝影機001"];

        public int MaxStreams => 2;

        public bool HasVnc => false;

        public bool TryAuthenticate(string user, string password) => user == "admin";

        public bool IsKnownUser(string user) => user == "admin";

        public IQctekLiveSource? CreateLiveSource(int channelIndex, int subStream) => null;

        public IReadOnlyList<bool> GetRecordingMonthDays(int channelIndex, int year, int month) => new bool[31];

        public IReadOnlyList<byte> GetRecordingHourMatrix(int channelIndex, int year, int month) => new byte[768];

        public QctekPlaybackSegment? FindSegmentFrom(int channelIndex, DateTime fromUtc) => null;

        public QctekPlaybackSegment? FindSegmentCovering(int channelIndex, DateTime fromUtc) => null;

        public QctekEventPage SearchEvents(QctekEventSearchRequest query, int offset, int limit)
            => QctekEventPage.Empty;

        public IQctekLiveSource? CreatePlaybackSource(string filePath) => null;
    }
}
