using HeliVMS.Shared;
using Xunit;

namespace HeliVMS.Shared.Tests;

/// <summary>
/// 回放選擇區間的起止手柄（§8.5）行為契約：抓哪一側就只動那一側，跨過對側要交換。
/// </summary>
/// <remarks>
/// 這裡守住兩個操作員一眼就會看出來的錯誤：拖左緣卻右緣也跟著動（區間無法精修），
/// 以及拖過對側之後畫面兩端來回彈（手柄身分沒跟著交換）。兩者都不會拋例外，只會讓人
/// 覺得「這個選取壞掉了」。
/// </remarks>
public sealed class PlaybackRangeHandleTests
{
    private static readonly DateTime T0 = new(2026, 9, 12, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void 拖左緣只動左端()
    {
        var (start, end, edge) = PlaybackRangeHandle.Drag(
            T0, T0.AddMinutes(30), T0.AddMinutes(5), PlaybackRangeHandle.Left);

        Assert.Equal(T0.AddMinutes(5), start);
        Assert.Equal(T0.AddMinutes(30), end);
        Assert.Equal(PlaybackRangeHandle.Left, edge);
    }

    [Fact]
    public void 拖右緣只動右端()
    {
        var (start, end, edge) = PlaybackRangeHandle.Drag(
            T0, T0.AddMinutes(30), T0.AddMinutes(40), PlaybackRangeHandle.Right);

        Assert.Equal(T0, start);
        Assert.Equal(T0.AddMinutes(40), end);
        Assert.Equal(PlaybackRangeHandle.Right, edge);
    }

    [Fact]
    public void 左緣拖過右緣會交換兩端並把手柄換到對側()
    {
        var (start, end, edge) = PlaybackRangeHandle.Drag(
            T0, T0.AddMinutes(30), T0.AddMinutes(40), PlaybackRangeHandle.Left);

        Assert.Equal(T0.AddMinutes(30), start);
        Assert.Equal(T0.AddMinutes(40), end);
        Assert.Equal(PlaybackRangeHandle.Right, edge);
    }

    [Fact]
    public void 右緣拖過左緣會交換兩端並把手柄換到對側()
    {
        var (start, end, edge) = PlaybackRangeHandle.Drag(
            T0, T0.AddMinutes(30), T0.AddMinutes(-10), PlaybackRangeHandle.Right);

        Assert.Equal(T0.AddMinutes(-10), start);
        Assert.Equal(T0, end);
        Assert.Equal(PlaybackRangeHandle.Left, edge);
    }

    /// <summary>交換之後繼續往同一方向拖要能一路延伸；身分沒換的話這裡會縮回去。</summary>
    [Fact]
    public void 交換後繼續同向拖曳會延伸不會彈回()
    {
        var (_, _, flipped) = PlaybackRangeHandle.Drag(
            T0, T0.AddMinutes(30), T0.AddMinutes(40), PlaybackRangeHandle.Left);
        var (start, end, edge) = PlaybackRangeHandle.Drag(
            T0.AddMinutes(30), T0.AddMinutes(40), T0.AddMinutes(50), flipped);

        Assert.Equal(T0.AddMinutes(30), start);
        Assert.Equal(T0.AddMinutes(50), end);
        Assert.Equal(PlaybackRangeHandle.Right, edge);
    }

    [Fact]
    public void 指標在容差內命中靠近的一側()
    {
        Assert.Equal(PlaybackRangeHandle.Left, PlaybackRangeHandle.HitEdge(103, 100, 400, 6));
        Assert.Equal(PlaybackRangeHandle.Right, PlaybackRangeHandle.HitEdge(396, 100, 400, 6));
    }

    [Fact]
    public void 指標在容差外不算抓到手柄()
    {
        Assert.Equal(0, PlaybackRangeHandle.HitEdge(150, 100, 400, 6));
        Assert.Equal(0, PlaybackRangeHandle.HitEdge(400 + 7, 100, 400, 6));
    }

    /// <summary>極窄的區間兩側容差重疊時取較近的一側；平手取左，行為要可預期。</summary>
    [Fact]
    public void 兩側容差重疊時取較近的一側()
    {
        Assert.Equal(PlaybackRangeHandle.Left, PlaybackRangeHandle.HitEdge(99, 100, 104, 6));
        Assert.Equal(PlaybackRangeHandle.Right, PlaybackRangeHandle.HitEdge(103, 100, 104, 6));
        Assert.Equal(PlaybackRangeHandle.Left, PlaybackRangeHandle.HitEdge(102, 100, 104, 6));
    }
}
