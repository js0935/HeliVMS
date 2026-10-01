using System.Globalization;
using HeliVMS.Shared.Models;
using HeliVMS.WebApi;

namespace HeliVMS.Api.Tests;

/// <summary>M239（§14.3 串流 P0）：HLS VOD 播放清單的文字內容。</summary>
public sealed class HlsPlaylistTests
{
    [Fact]
    public void Build_EmptyList_StillEmitsValidVodPlaylist()
    {
        var text = HlsPlaylist.Build([]);

        Assert.StartsWith("#EXTM3U\n", text);
        Assert.Contains("#EXT-X-PLAYLIST-TYPE:VOD", text);
        Assert.Contains("#EXT-X-ENDLIST", text);
        Assert.DoesNotContain("#EXT-X-MAP", text);
        Assert.DoesNotContain("#EXTINF", text);
    }

    [Fact]
    public void Build_ReferencesFirstInitMapThenEveryMediaSegmentInOrder()
    {
        var start = new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);
        var text = HlsPlaylist.Build(
        [
            Segment(7, start, start.AddSeconds(15)),
            Segment(9, start.AddSeconds(15), start.AddSeconds(30)),
        ]);

        Assert.Contains($"#EXT-X-MAP:URI=\"{HlsPlaylist.InitUri(7)}\"", text);
        Assert.Contains($"#EXTINF:15.000,\n{HlsPlaylist.MediaUri(7)}\n", text);
        Assert.Contains($"#EXT-X-PROGRAM-DATE-TIME:2026-09-30T12:00:00.000Z", text);
        Assert.Contains($"#EXT-X-PROGRAM-DATE-TIME:2026-09-30T12:00:15.000Z", text);
        Assert.Equal(1, text.Split("#EXT-X-MAP").Length - 1);
        Assert.True(
            text.IndexOf(HlsPlaylist.MediaUri(7), StringComparison.Ordinal)
            < text.IndexOf(HlsPlaylist.MediaUri(9), StringComparison.Ordinal),
            "媒體段必須依索引順序");
    }

    [Fact]
    public void Build_TargetDuration_IsCeilingOfLongestSegment()
    {
        var start = new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal(16, HlsPlaylist.TargetDuration([Segment(1, start, start.AddSeconds(15.4))]));
        Assert.Equal(15, HlsPlaylist.TargetDuration([Segment(1, start, start.AddSeconds(15))]));
        Assert.Equal(1, HlsPlaylist.TargetDuration([]));
    }

    [Fact]
    public void DurationSeconds_FallsBackToDurationSecWhenEndMissing()
    {
        var start = new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal(12.5, HlsPlaylist.DurationSeconds(new SegmentRecord
        {
            Id = 1,
            StartUtc = start,
            EndUtc = null,
            DurationSec = 12.5,
        }));
        Assert.Equal(0d, HlsPlaylist.DurationSeconds(new SegmentRecord { Id = 1, StartUtc = start }));
    }

    [Fact]
    public void DurationSeconds_IgnoresEndNotAfterStart()
    {
        var start = new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal(7d, HlsPlaylist.DurationSeconds(new SegmentRecord
        {
            Id = 1,
            StartUtc = start,
            EndUtc = start,
            DurationSec = 7,
        }));
    }

    [Fact]
    public void Build_UsesInvariantDecimalsUnderCommaCulture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var start = new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);

            var text = HlsPlaylist.Build([Segment(3, start, start.AddSeconds(9.5))]);

            Assert.Contains("#EXTINF:9.500,", text);
            Assert.DoesNotContain("9,500", text);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    private static SegmentRecord Segment(long id, DateTime start, DateTime end) => new()
    {
        Id = id,
        ChannelId = 1,
        Stream = "main",
        StartUtc = start,
        EndUtc = end,
        Format = "mp4",
    };
}
