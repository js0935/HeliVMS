using System.Globalization;
using HeliVMS.Qctek.Nemo;

namespace HeliVMS.Qctek.Tests;

/// <summary>
/// <c>QCV8001_AUTH</c> 挑戰的線路形狀。
/// </summary>
/// <remarks>
/// 挑戰沒有結尾 NUL、也沒有長度欄位，長度就是協定的一部分：16 字元＋空白＋十位整數。
/// 少一位（例如整數只有九位數）會讓「讀固定 27 bytes」的用戶端把下一個回應的第一個
/// 位元組吞進挑戰裡，整條連線從此錯位，而兩邊的日誌都乾淨。
/// </remarks>
public class QctekChallengeTests
{
    [Fact]
    public void 挑戰固定27位元組且尾端是十位Unix秒()
    {
        var before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var challenge = QctekChallenge.Create();
        var after = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        Assert.Equal(QctekChallenge.TotalLength, challenge.Length);
        Assert.Equal(27, QctekChallenge.TotalLength);

        var space = challenge.IndexOf(' ');
        Assert.Equal(QctekChallenge.PrefixLength, space);

        var prefix = challenge[..space];
        Assert.All(prefix, c => Assert.True(QctekChallenge.Alphabet.Contains(c)));
        Assert.False(prefix.Contains(' '));

        var stamp = long.Parse(challenge[(space + 1)..], CultureInfo.InvariantCulture);
        Assert.InRange(stamp, before, after);
    }

    [Fact]
    public void 每次挑戰的前綴都不同()
    {
        // 前綴是 digest 的隨機輸入；固定不變等於讓同一個挑戰可以被重播。
        var seen = Enumerable.Range(0, 8).Select(_ => QctekChallenge.Create()).ToHashSet();
        Assert.Equal(8, seen.Count);
    }
}
