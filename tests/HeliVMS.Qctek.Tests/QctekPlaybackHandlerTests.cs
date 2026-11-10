using System.Buffers.Binary;
using System.Text;
using HeliVMS.Qctek.Nemo;
using Xunit;

namespace HeliVMS.Qctek.Tests;

/// <summary>
/// 回放命令（GET_MONTH_DATE／CATALOG／OPENFILE）的派送測試：命令 → 線路位元組。
/// 釘三件事：回應的框型（framed binary）、payload 的位移、找不到段時的 raw "FAIL "。
/// 這三條命令是 QCC 回放頁的整條鏈——月曆空了就選不了日期、目錄空了就選不了時段、
/// OPENFILE FAIL 了就永遠黑屏，而且全部日誌乾淨。
/// </summary>
public sealed class QctekPlaybackHandlerTests
{
    /// <summary>可注入月曆／小時矩陣／錄影段的 stub。</summary>
    private sealed class PlaybackContext : IQctekDeviceContext
    {
        public string ServerVersion => "QCV8001";
        public int ChannelCount => 7;
        public IReadOnlyList<string> ChannelNames
            => Enumerable.Range(1, 7).Select(static i => $"攝影機{i:000}").ToList();
        public int MaxStreams => 2;
        public bool HasVnc { get; init; }
        public bool TryAuthenticate(string user, string password) => user == "admin";
        public bool IsKnownUser(string user) => user == "admin";

        public HeliVMS.Qctek.Live.IQctekLiveSource? CreateLiveSource(int channelIndex, int subStream) => null;

        public IReadOnlyList<bool> GetRecordingMonthDays(int channelIndex, int year, int month)
            => MonthDays;

        public IReadOnlyList<byte> GetRecordingHourMatrix(int channelIndex, int year, int month)
            => HourMatrix;

        public HeliVMS.Qctek.Live.QctekPlaybackSegment? FindSegmentFrom(int channelIndex, DateTime fromUtc)
            => Segment is null || Segment.ChannelIndex != channelIndex ? null
                : Segment with { StartUtc = fromUtc };

        public HeliVMS.Qctek.Live.QctekPlaybackSegment? FindSegmentCovering(int channelIndex, DateTime fromUtc)
            => Segment is null || Segment.ChannelIndex != channelIndex ? null : Segment;

        public QctekEventPage SearchEvents(QctekEventSearchRequest query, int offset, int limit)
            => QctekEventPage.Empty;

        public HeliVMS.Qctek.Live.IQctekLiveSource? CreatePlaybackSource(string filePath) => null;

        public bool[] MonthDays { get; set; } = new bool[31];
        public byte[] HourMatrix { get; set; } = new byte[768];
        public HeliVMS.Qctek.Live.QctekPlaybackSegment? Segment { get; set; }
    }

    private static byte[]? Handle(string command)
        => QctekCommandHandler.Handle(QctekRequest.Nemo(command), new PlaybackContext());

    private static byte[] PayloadOf(byte[]? wire)
    {
        Assert.NotNull(wire);
        Assert.True(NemoMessage.TryDecode(wire!, out var payload, out _));
        return payload;
    }

    [Fact]
    public void 月曆查詢回0x40的framed回應且帶天旗標()
    {
        var context = new PlaybackContext { MonthDays = MakeDays(3, 17) };
        var wire = QctekCommandHandler.Handle(
            QctekRequest.Nemo("GET_MONTH_DATE 1 2026 11"), context);

        var payload = PayloadOf(wire);
        Assert.Equal(0x40, payload.Length);
        Assert.Equal("OK", Encoding.ASCII.GetString(payload, 0, 2));
        Assert.Equal(1, payload[0x10 + 2]);
        Assert.Equal(1, payload[0x10 + 16]);
        Assert.Equal(0, payload[0x10 + 0]);
    }

    [Fact]
    public void 目錄查詢回0x314的framed回應且帶小時矩陣()
    {
        var context = new PlaybackContext { HourMatrix = MakeMatrix(day: 10, hour: 13, amount: 42) };
        var wire = QctekCommandHandler.Handle(
            QctekRequest.Nemo("CATALOG 2026 11 10 1"), context);

        var payload = PayloadOf(wire);
        Assert.Equal(0x314, payload.Length);
        Assert.Equal("OK", Encoding.ASCII.GetString(payload, 0, 2));
        // 位移 0x10 是查詢的「日」（u32）。
        Assert.Equal(10u, BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(0x10)));
        Assert.Equal(42, payload[0x14 + (10 - 1) * 24 + 13]);
    }

    [Fact]
    public void 開檔回OK加檔案資訊且頻道是一based()
    {
        var context = new PlaybackContext
        {
            Segment = new HeliVMS.Qctek.Live.QctekPlaybackSegment(
                ChannelIndex: 2,
                FilePath: @"C:\recordings\seg-120000.mp4",
                StartUtc: new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc),
                SizeBytes: 54321),
        };
        // 參數順序＝寬鬆解析器釘住的「年月日在前、頻道最後」（與 CATALOG 同序）。
        var wire = QctekCommandHandler.Handle(
            QctekRequest.Nemo("OPENFILE 2026 11 10 2"), context);

        Assert.NotNull(wire);
        var text = Encoding.ASCII.GetString(wire!);
        // raw 回應（非 NEMO 框）：parserFileInfo 的四個數字，頻道 1-based。
        Assert.StartsWith("OK 3 54321 ", text, StringComparison.Ordinal);
        Assert.EndsWith("\0", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 開檔找不到段回raw的FAIL含NUL()
    {
        var wire = QctekCommandHandler.Handle(
            QctekRequest.Nemo("OPENFILE 2026 11 10 3"), new PlaybackContext());

        Assert.NotNull(wire);
        // raw 回應由 QctekReply.Raw 補上結尾 NUL（與 OK 版本同一框法）。
        Assert.Equal("FAIL \0", Encoding.ASCII.GetString(wire!));
    }

    [Fact]
    public void 壞請求的月曆回空旗標而不是丟例外()
    {
        // 解析不出年月：回「全沒錄影」的 0x40，讓 QCC 顯示空月曆（而不是斷線）。
        var wire = QctekCommandHandler.Handle(
            QctekRequest.Nemo("GET_MONTH_DATE 1"), new PlaybackContext());

        var payload = PayloadOf(wire);
        Assert.Equal(0x40, payload.Length);
        Assert.All(payload[0x10..], b => Assert.Equal(0, b));
    }

    private static bool[] MakeDays(params int[] oneBasedDays)
    {
        var days = new bool[31];
        foreach (var d in oneBasedDays)
        {
            days[d - 1] = true;
        }

        return days;
    }

    private static byte[] MakeMatrix(int day, int hour, byte amount)
    {
        var matrix = new byte[768];
        matrix[(day - 1) * 24 + hour] = amount;
        return matrix;
    }
}
