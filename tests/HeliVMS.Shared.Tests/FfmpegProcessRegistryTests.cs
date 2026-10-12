using HeliVMS.Shared;
using Xunit;

namespace HeliVMS.Shared.Tests;

/// <summary>
/// ffmpeg 子行程登錄表（<see cref="FfmpegProcessRegistry"/>）的行為契約（§13-2 行程治理）。
///
/// 現場症狀：解碼、錄影、發佈三條路徑各自開 ffmpeg，卻沒有任何單一地方說得出
/// 「這台現在開著幾條」。當機器開始卡，維運只能一個個視窗點開來數。此登錄表把三條
/// 路徑的總量、尖峰與最近一次失敗集中曝光；它刻意只記錄、不強制終止——見型別註解。
/// </summary>
public sealed class FfmpegProcessRegistryTests
{
    [Fact]
    public void 登錄後執行中計數增加而處置後歸零()
    {
        var registry = new FfmpegProcessRegistry();

        var job = registry.Register(FfmpegJobKind.Record, "ch001/main");
        Assert.Equal(1, registry.Snapshot().Running);

        job.Dispose();

        var after = registry.Snapshot();
        Assert.Equal(0, after.Running);
        Assert.Equal(1, after.TotalStarted);
        Assert.Equal(1, after.TotalFinished);
    }

    [Fact]
    public void 依用途分類統計()
    {
        var registry = new FfmpegProcessRegistry();

        using var a = registry.Register(FfmpegJobKind.Decode, "a");
        using var b = registry.Register(FfmpegJobKind.Decode, "b");
        using var c = registry.Register(FfmpegJobKind.Record, "c");
        using var d = registry.Register(FfmpegJobKind.Publish, "d");

        var s = registry.Snapshot();
        Assert.Equal(4, s.Running);
        Assert.Equal(2, s.Decode);
        Assert.Equal(1, s.Record);
        Assert.Equal(1, s.Publish);
    }

    [Fact]
    public void 失敗會被記錄且計入失敗次數()
    {
        var registry = new FfmpegProcessRegistry();
        var job = registry.Register(FfmpegJobKind.Record, "ch007/sub");

        job.Fail("rtsp 連線失敗");
        job.Dispose();

        var s = registry.Snapshot();
        Assert.Equal(1, s.TotalFailed);
        Assert.Equal("ch007/sub", s.LastFailureKey);
        Assert.Equal("rtsp 連線失敗", s.LastFailure);
        Assert.False(string.IsNullOrEmpty(s.LastFailureUtc));
    }

    [Fact]
    public void 重複標記失敗只算一次()
    {
        // LivePublisher 的行程退出任務與 StopAsync 可能都嘗試標記；重複計數會讓
        // 健康度的「失敗」數字失真，掩蓋真正的問題。
        var registry = new FfmpegProcessRegistry();
        var job = registry.Register(FfmpegJobKind.Publish, "publish");

        job.Fail("第一次");
        job.Fail("第二次");

        Assert.Equal(1, registry.Snapshot().TotalFailed);
        Assert.Equal("第一次", registry.Snapshot().LastFailure);
    }

    [Fact]
    public void 尖峰保留歷史最大值()
    {
        var registry = new FfmpegProcessRegistry();
        var j1 = registry.Register(FfmpegJobKind.Decode, "1");
        var j2 = registry.Register(FfmpegJobKind.Decode, "2");
        var j3 = registry.Register(FfmpegJobKind.Decode, "3");

        Assert.Equal(3, registry.Snapshot().PeakConcurrent);

        j3.Dispose();
        j2.Dispose();
        j1.Dispose();

        Assert.Equal(3, registry.Snapshot().PeakConcurrent);
        Assert.Equal(0, registry.Snapshot().Running);
    }

    [Fact]
    public void 超過容量時回報逾量()
    {
        var registry = new FfmpegProcessRegistry { Capacity = 2 };

        using var a = registry.Register(FfmpegJobKind.Decode, "a");
        using var b = registry.Register(FfmpegJobKind.Decode, "b");
        Assert.False(registry.Snapshot().OverCapacity);

        using var c = registry.Register(FfmpegJobKind.Decode, "c");
        Assert.True(registry.Snapshot().OverCapacity);
        Assert.Equal(2, registry.Snapshot().Capacity);
    }

    [Fact]
    public void 未設容量時永不逾量()
    {
        // 逾量必須是「有人真的設了上限」才算；預設 0 代表不限制。
        var registry = new FfmpegProcessRegistry();
        var jobs = new List<FfmpegProcessRegistry.FfmpegJob>();
        for (var i = 0; i < 50; i++)
        {
            jobs.Add(registry.Register(FfmpegJobKind.Record, $"{i}"));
        }

        Assert.Equal(50, registry.Snapshot().Running);
        Assert.False(registry.Snapshot().OverCapacity);

        foreach (var job in jobs)
        {
            job.Dispose();
        }
    }

    [Fact]
    public void 重複處置只算一次結束()
    {
        var registry = new FfmpegProcessRegistry();
        var job = registry.Register(FfmpegJobKind.Record, "ch");

        job.Dispose();
        job.Dispose();

        Assert.Equal(1, registry.Snapshot().TotalFinished);
    }

    [Fact]
    public void Reset清空所有統計()
    {
        var registry = new FfmpegProcessRegistry();
        registry.Register(FfmpegJobKind.Record, "ch").Fail("x");

        registry.Reset();

        var s = registry.Snapshot();
        Assert.Equal(0, s.Running);
        Assert.Equal(0, s.TotalStarted);
        Assert.Equal(0, s.TotalFailed);
        Assert.Null(s.LastFailureKey);
        Assert.Null(s.LastFailure);
    }

    [Fact]
    public void 空鍵會被拒絕()
    {
        var registry = new FfmpegProcessRegistry();

        Assert.Throws<ArgumentException>(() => registry.Register(FfmpegJobKind.Decode, " "));
    }
}
