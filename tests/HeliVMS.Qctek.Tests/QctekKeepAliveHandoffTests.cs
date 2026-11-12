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
