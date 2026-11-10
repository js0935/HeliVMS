using HeliVMS.Shared;
using Xunit;

namespace HeliVMS.Shared.Tests;

/// <summary>
/// 第二螢幕鏡像牆的格數決策（<see cref="MirrorWallBudget"/>）。
/// </summary>
/// <remarks>
/// <para>
/// 這些看似「只是幾個整數」的判斷，錯了不會有任何錯誤訊息：<c>UniformGrid</c> 會照給的列×欄
/// 排出一面看得到、但每格比例怪異的牆，而「格數比設定少」只會被操作員記成「偶爾怪怪的」。
/// 儲存值壞掉是升過版、改過選單之後才會踩到，桌面端沒有 WPF 執行期測試，所以決策放在純函式。
/// </para>
/// </remarks>
public class MirrorWallBudgetTests
{
    [Fact]
    public void 鏡像牆分割選項沿用回放牆清單且上限十六格()
    {
        // 上限刻意低於主牆：鏡像不花解碼成本，花的是每格一張 WriteableBitmap 的貼圖成本。
        Assert.Equal([1, 2, 4, 9, 16], MirrorWallBudget.SplitOptions);

        // 而且是同一份清單的子集，不是自己抄一份（兩份清單遲早漂移）。
        Assert.All(
            MirrorWallBudget.SplitOptions,
            cells => Assert.Contains(cells, PlaybackWallBudget.SplitOptions));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(4, 2)]
    [InlineData(9, 3)]
    [InlineData(16, 4)]
    public void 格數換算成列與欄是正方形優先(int cells, int expectedColumns)
    {
        var (rows, columns) = MirrorWallBudget.GridOf(cells);

        Assert.Equal(expectedColumns, columns);
        Assert.Equal((int)Math.Ceiling(cells / (double)expectedColumns), rows);
        Assert.True(rows * columns >= cells, "列×欄必須容得下所有格位，否則最後幾台不會出現");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("7")]
    [InlineData("25")]
    [InlineData("64")]
    [InlineData("-4")]
    [InlineData("0")]
    public void 儲存值不在選項裡會退回預設而不是排出不存在的版面(string? stored)
    {
        Assert.Equal(MirrorWallBudget.DefaultCells, MirrorWallBudget.Resolve(stored));
        Assert.Equal(4, MirrorWallBudget.DefaultCells);
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("2", 2)]
    [InlineData("4", 4)]
    [InlineData("9", 9)]
    [InlineData("16", 16)]
    public void 合法儲存值會原樣還原(string stored, int expected)
    {
        Assert.Equal(expected, MirrorWallBudget.Resolve(stored));
    }

    [Fact]
    public void 預設值本身就是合法選項所以壞值一定排得出畫面()
    {
        Assert.Contains(MirrorWallBudget.DefaultCells, MirrorWallBudget.SplitOptions);
        Assert.Equal(MirrorWallBudget.DefaultCells, MirrorWallBudget.Clamp(0));
        Assert.Equal(MirrorWallBudget.DefaultCells, MirrorWallBudget.Clamp(-5));
        Assert.Equal(MirrorWallBudget.DefaultCells, MirrorWallBudget.Clamp(int.MaxValue));
    }

    [Fact]
    public void 呼叫端傳入非法fallback時仍然回到預設()
    {
        // fallback 由呼叫端給（例如「上次用的格數」），它自己也可能已不在清單裡。
        Assert.Equal(MirrorWallBudget.DefaultCells, MirrorWallBudget.Resolve("abc", fallback: 36));
        Assert.Equal(9, MirrorWallBudget.Resolve("abc", fallback: 9));
    }
}
