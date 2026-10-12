using HeliVMS.Storage;

namespace HeliVMS.Storage.Tests;

/// <summary>
/// 圖釘自動布局（M284，§14.7 #11）。
///
/// 守的失效是「重疊的圖釘讓操作員以為鏡頭沒設定」：只要兩支相機落點接近，扇形與圖釘就完全
/// 疊住，畫面上只看得到最上面那一個。這裡把布局演算法釘成可注入的純函式，逐條驗證「重疊
/// 必被推開」與「不重疊的原封不動」。
/// </summary>
public sealed class MapLayoutTests
{
    private static MapLayout.Pin Pin(int id, double x, double y) => new(id, x, y);

    /// <summary>沒有圖釘時回傳空清單，不能拋例外。</summary>
    [Fact]
    public void 沒有圖釘時回傳空清單()
    {
        Assert.Empty(MapLayout.Resolve([]));
    }

    /// <summary>本來就拉開的圖釘不應被移動——自動布局只解決重疊，不重排整個版面。</summary>
    [Fact]
    public void 未重疊的圖釘位置保持不變()
    {
        var result = MapLayout.Resolve([Pin(1, 0.2, 0.2), Pin(2, 0.8, 0.8)]);

        var first = result.Single(p => p.Id == 1);
        var second = result.Single(p => p.Id == 2);
        Assert.Equal(0.2, first.X, 6);
        Assert.Equal(0.2, first.Y, 6);
        Assert.Equal(0.8, second.X, 6);
        Assert.Equal(0.8, second.Y, 6);
    }

    /// <summary>兩個落點完全相同的圖釘必須被推開到至少一個間距。</summary>
    [Fact]
    public void 兩點重疊時會被推開到至少一個間距()
    {
        var result = MapLayout.Resolve([Pin(1, 0.5, 0.5), Pin(2, 0.5, 0.5)]);

        Assert.Equal(2, result.Count);
        Assert.True(Distance(result[0], result[1]) >= MapLayout.DefaultMinSpacing - 1e-9);
    }

    /// <summary>一堆圖釘疊在同一點時，兩兩之間都要拉開到間距以上。</summary>
    [Fact]
    public void 多點重疊時兩兩都不小於間距()
    {
        var pins = Enumerable.Range(1, 6).Select(i => Pin(i, 0.5, 0.5)).ToList();
        var result = MapLayout.Resolve(pins);

        Assert.Equal(6, result.Count);
        for (var i = 0; i < result.Count; i++)
        {
            for (var j = i + 1; j < result.Count; j++)
            {
                Assert.True(
                    Distance(result[i], result[j]) >= MapLayout.DefaultMinSpacing - 1e-9,
                    $"圖釘 {result[i].Id} 與 {result[j].Id} 仍重疊。");
            }
        }
    }

    /// <summary>同一個輸入要得到同一個輸出，且與輸入順序無關（三個程序/兩次按鈕結果一致）。</summary>
    [Fact]
    public void 結果具決定性且與輸入順序無關()
    {
        var forward = MapLayout.Resolve([Pin(1, 0.5, 0.5), Pin(2, 0.5, 0.5), Pin(3, 0.51, 0.5)]);
        var reversed = MapLayout.Resolve([Pin(3, 0.51, 0.5), Pin(2, 0.5, 0.5), Pin(1, 0.5, 0.5)]);

        Assert.Equal(forward, reversed);
    }

    /// <summary>每個輸入圖釘都要有且只有一個落點——自動布局不能弄丟圖釘。</summary>
    [Fact]
    public void 每個輸入都恰好有一個落點()
    {
        var ids = new[] { 7, 2, 9 };
        var result = MapLayout.Resolve([Pin(7, 0.1, 0.1), Pin(2, 0.1, 0.1), Pin(9, 0.1, 0.1)]);

        Assert.Equal(ids.OrderBy(i => i), result.Select(p => p.Id).OrderBy(i => i));
    }

    /// <summary>超出圖面的座標要夾回邊距內，避免推開後圖釘跑到看不見的地方。</summary>
    [Fact]
    public void 超出邊界的座標會被夾回邊距內()
    {
        var result = MapLayout.Resolve([Pin(1, 2.0, -1.0)]);
        var p = result.Single();

        Assert.InRange(p.X, MapLayout.DefaultMargin, 1 - MapLayout.DefaultMargin);
        Assert.InRange(p.Y, MapLayout.DefaultMargin, 1 - MapLayout.DefaultMargin);
    }

    /// <summary>間距為零或負值時改用預設間距，而不是把圖釘疊在一起。</summary>
    [Fact]
    public void 間距非正時改用預設值()
    {
        var result = MapLayout.Resolve([Pin(1, 0.5, 0.5), Pin(2, 0.5, 0.5)], minSpacing: 0);
        Assert.True(Distance(result[0], result[1]) >= MapLayout.DefaultMinSpacing - 1e-9);
    }

    private static double Distance(MapLayout.Placement a, MapLayout.Placement b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }
}
