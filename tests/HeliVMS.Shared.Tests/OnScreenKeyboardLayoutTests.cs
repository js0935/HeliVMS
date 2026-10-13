using HeliVMS.Shared;
using Xunit;

namespace HeliVMS.Shared.Tests;

/// <summary>
/// 螢幕虛擬鍵盤版面（<see cref="OnScreenKeyboardLayout"/>）的行為契約（M292）。
/// </summary>
/// <remarks>
/// 觸發這個里程碑的實際回報是「鍵盤叫得出來但按鍵沒反應、也不能切換大小寫」。
/// 按鍵失效另有其因（見 Osk.cs 的拖曳守衛），但大小寫切換鍵在原版面裡根本不存在，
/// 字母也只有大寫。這組測試把「有 Shift 鍵、字母齊備、預設小寫」釘住，
/// 避免版面又退化成只有單一大小寫而沒有測試會發現。
/// </remarks>
public sealed class OnScreenKeyboardLayoutTests
{
    private static List<string> Keys(bool shift)
        => OnScreenKeyboardLayout.Rows(shift).SelectMany(row => row).ToList();

    [Fact]
    public void 預設為小寫字母()
    {
        var keys = Keys(shift: false);

        Assert.Contains("q", keys);
        Assert.Contains("a", keys);
        Assert.Contains("z", keys);
        Assert.DoesNotContain("Q", keys);
    }

    [Fact]
    public void 切換後為大寫字母()
    {
        var keys = Keys(shift: true);

        Assert.Contains("Q", keys);
        Assert.Contains("Z", keys);
        Assert.DoesNotContain("q", keys);
    }

    [Fact]
    public void 二十六個英文字母各出現一次()
    {
        var letters = Keys(shift: false)
            .Where(k => k.Length == 1 && char.IsLetter(k[0]))
            .ToList();

        Assert.Equal(26, letters.Count);
        Assert.Equal(26, letters.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            "abcdefghijklmnopqrstuvwxyz".Select(c => c.ToString()),
            letters.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void 五個特殊鍵各恰好一個()
    {
        var keys = Keys(shift: false);

        foreach (var special in new[]
        {
            OnScreenKeyboardLayout.ShiftKey,
            OnScreenKeyboardLayout.SpaceKey,
            OnScreenKeyboardLayout.ClearKey,
            OnScreenKeyboardLayout.BackspaceKey,
            OnScreenKeyboardLayout.CollapseKey,
        })
        {
            Assert.Equal(1, keys.Count(k => k == special));
        }
    }

    [Fact]
    public void 數字與常用符號齊備()
    {
        var keys = Keys(shift: false);

        foreach (var c in "0123456789.@_")
        {
            Assert.Contains(c.ToString(), keys);
        }
    }

    [Fact]
    public void 每一列的按鍵不重複()
    {
        // 同一鍵在同一列出現兩次＝版面打錯；大小寫切換也不該產生重複。
        foreach (var shift in new[] { false, true })
        {
            foreach (var row in OnScreenKeyboardLayout.Rows(shift))
            {
                Assert.Equal(row.Count, row.Distinct(StringComparer.Ordinal).Count());
            }
        }
    }

    [Fact]
    public void 切換大小寫只改字母其餘不變()
    {
        var lower = OnScreenKeyboardLayout.Rows(shift: false);
        var upper = OnScreenKeyboardLayout.Rows(shift: true);

        Assert.Equal(lower.Count, upper.Count);
        for (var r = 0; r < lower.Count; r++)
        {
            Assert.Equal(lower[r].Count, upper[r].Count);
            for (var c = 0; c < lower[r].Count; c++)
            {
                var a = lower[r][c];
                var b = upper[r][c];
                if (a.Length == 1 && char.IsLetter(a[0]))
                {
                    Assert.Equal(a.ToUpperInvariant(), b);
                }
                else
                {
                    Assert.Equal(a, b);
                }
            }
        }
    }
}
