using System;
using HeliVMS.Shared;
using Xunit;

namespace HeliVMS.Shared.Tests;

/// <summary>
/// 畫面更新率／畫面年齡估算（<see cref="FrameRateMeter"/>）的行為契約。
///
/// 背景：監看介面原本沒有任何 fps／延遲數字，「畫面愈拖愈舊」只能靠肉眼發現，
/// 而且很容易被誤判成機器不夠快。用這裡的數字當量測點，才能分辨瓶頸在 UI 執行緒
/// 還是在解碼。
/// </summary>
public sealed class FrameRateMeterTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>穩定 15fps 的來源必須估出接近 15 的更新率。</summary>
    [Fact]
    public void 穩定十五幀每秒會估出十五()
    {
        var meter = new FrameRateMeter();
        for (var i = 0; i <= 30; i++)
        {
            meter.Record(T0.AddMilliseconds(i * (1000.0 / 15)));
        }

        Assert.InRange(meter.Fps, 14.9, 15.1);
    }

    /// <summary>不足兩幀時不能報數字，否則介面會閃爍出離譜的 fps。</summary>
    [Fact]
    public void 不足兩幀時回報零()
    {
        var meter = new FrameRateMeter();
        Assert.Equal(0, meter.Fps);

        meter.Record(T0);
        Assert.Equal(0, meter.Fps);
    }

    /// <summary>畫面年齡＝已經顯示多久，是「看起來LAG」的實際數字。</summary>
    [Fact]
    public void 畫面年齡是最後一張影格到現在的時間()
    {
        var meter = new FrameRateMeter();
        meter.Record(T0);
        meter.Record(T0.AddMilliseconds(66));

        Assert.Equal(TimeSpan.FromMilliseconds(100), meter.Age(T0.AddMilliseconds(166)));
    }

    /// <summary>完全沒有影格時回傳零，而不是一個巨大的年齡。</summary>
    [Fact]
    public void 沒有影格時畫面年齡為零()
    {
        var meter = new FrameRateMeter();
        Assert.Equal(TimeSpan.Zero, meter.Age(T0));
    }

    /// <summary>時間戳異常倒退時不應回傳負的年齡。</summary>
    [Fact]
    public void 時間倒退時畫面年齡不會是負數()
    {
        var meter = new FrameRateMeter();
        meter.Record(T0);

        Assert.Equal(TimeSpan.Zero, meter.Age(T0.AddSeconds(-5)));
    }

    /// <summary>
    /// 滑動窗必須真的把舊樣本丟掉：否則「前 10 秒 30fps、之後掉到 1fps」會一直顯示 30，
    /// 操作員會以為機器沒事，而實際上串流早就卡住了。
    /// </summary>
    [Fact]
    public void 滑動窗會淘汰過期樣本()
    {
        var meter = new FrameRateMeter(TimeSpan.FromSeconds(1));
        for (var i = 0; i <= 60; i++)
        {
            meter.Record(T0.AddMilliseconds(i * 33));
        }

        // 61 張共跨 2 秒，窗長 1 秒 → 只留得住最近約 1 秒（≈31 張）。
        Assert.InRange(meter.SampleCount, 29, 31);
        Assert.InRange(meter.Fps, 29.0, 31.0);

        // 換成每秒一張：窗長 1 秒撐不住三張，必須收斂到兩張、顯示約 1fps。
        for (var i = 0; i <= 30; i++)
        {
            meter.Record(T0.AddSeconds(10).AddSeconds(i));
        }

        Assert.InRange(meter.SampleCount, 1, 3);
        Assert.InRange(meter.Fps, 0.9, 1.1);
    }

    /// <summary>掉幀要能被看出來：5fps 的來源不該顯示成 15fps。</summary>
    [Fact]
    public void 掉幀會反映在更新率上()
    {
        var meter = new FrameRateMeter();
        for (var i = 0; i <= 20; i++)
        {
            meter.Record(T0.AddMilliseconds(i * 200));
        }

        Assert.InRange(meter.Fps, 4.9, 5.1);
    }

    /// <summary>同一張影格重複送出不得灌水，否則時間戳異常時 fps 會爆表。</summary>
    [Fact]
    public void 時間戳倒退會重設視窗()
    {
        var meter = new FrameRateMeter();
        meter.Record(T0);
        meter.Record(T0.AddMilliseconds(66));
        Assert.Equal(2, meter.SampleCount);

        meter.Record(T0.AddMilliseconds(-1000));

        Assert.Equal(1, meter.SampleCount);
    }

    /// <summary>清空後不得殘留舊數字，否則中斷再連線會顯示上一段串流的 fps。</summary>
    [Fact]
    public void 清空後不留殘留數字()
    {
        var meter = new FrameRateMeter();
        for (var i = 0; i <= 30; i++)
        {
            meter.Record(T0.AddMilliseconds(i * 66));
        }

        meter.Clear();

        Assert.Equal(0, meter.SampleCount);
        Assert.Equal(0, meter.Fps);
        Assert.Equal(TimeSpan.Zero, meter.Age(T0.AddMinutes(1)));
    }

    /// <summary>窗長必須為正，否則是程式錯誤。</summary>
    [Fact]
    public void 窗長必須為正()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameRateMeter(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameRateMeter(TimeSpan.FromSeconds(-1)));
    }
}