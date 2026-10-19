using HeliVMS.Storage;

namespace HeliVMS.Storage.Tests;

/// <summary>
/// 時間軸關鍵幀刻度的密度判斷（§3.4）。
///
/// <para>
/// 這條判斷決定「時間軸上畫不畫關鍵幀刻度」。畫錯的兩種樣子都不會拋例外：
/// 全日窗下全畫＝錄影色塊被上千條深色細線糊掉，操作員看不出哪裡有錄影；
/// 判斷太保守＝放大到秒級也一條刻度都不畫，關鍵幀吸附這個功能等於沒有畫面依據。
/// </para>
/// </summary>
public sealed class PlaybackKeyframeDensityTests
{
    private static readonly TimeSpan Day = TimeSpan.FromHours(24);

    private static List<SegmentKeyframe> Every(double seconds, int count)
    {
        var keys = new List<SegmentKeyframe>(count);
        for (var i = 0; i < count; i++)
        {
            keys.Add(new SegmentKeyframe(i * seconds, i * 1024));
        }

        return keys;
    }

    /// <summary>
    /// 全日窗（86400 秒）畫在 1000px 上：每 2 秒一個關鍵幀＝約 0.023px 間隔，
    /// 全畫就是一條實心深色帶，必須判定不畫。
    /// </summary>
    [Fact]
    public void 全日窗下每秒級關鍵幀不畫刻度()
    {
        var keys = Every(2, 1000);

        Assert.False(PlaybackKeyframeDensity.ShouldDraw(keys, Day.TotalSeconds, 1000, 3));
    }

    /// <summary>
    /// 縮到 60 秒窗（1000px）後，每 2 秒一個關鍵幀＝約 33px 間隔，遠大於 3px，必須畫。
    /// 同一個區段在兩種縮放下結論相反，就是這個功能存在的意義。
    /// </summary>
    [Fact]
    public void 縮到分鐘窗後同樣的關鍵幀會畫刻度()
    {
        var keys = Every(2, 30);

        Assert.True(PlaybackKeyframeDensity.ShouldDraw(keys, 60, 1000, 3));
    }

    /// <summary>
    /// 邊界：平均間隔換算剛好等於門檻時要畫（>= 而不是 >），否則操作員放大到臨界值時
    /// 刻度會在最需要的一格消失。
    /// </summary>
    [Fact]
    public void 平均間隔剛好等於門檻像素時仍要畫()
    {
        // 100 秒窗／1000px ＝ 10px/秒；關鍵幀每 0.3 秒一個 ＝ 3px，剛好等於門檻。
        var keys = Every(0.3, 101);

        Assert.True(PlaybackKeyframeDensity.ShouldDraw(keys, 100, 1000, 3));
    }

    /// <summary>只有 0 或 1 個關鍵幀時沒有間隔可算，一律不畫（除零會是 NaN，NaN 比較永遠 false）。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void 不足兩個關鍵幀不畫刻度(int count)
    {
        Assert.False(PlaybackKeyframeDensity.ShouldDraw(Every(2, count), 60, 1000, 3));
    }

    /// <summary>空清單、零寬、零窗、零門檻都是無效輸入，必須回 false 而不是丟例外或除零。</summary>
    [Fact]
    public void 無效輸入一律不畫刻度()
    {
        var keys = Every(2, 30);

        Assert.False(PlaybackKeyframeDensity.ShouldDraw([], 60, 1000, 3));
        Assert.False(PlaybackKeyframeDensity.ShouldDraw(keys, 0, 1000, 3));
        Assert.False(PlaybackKeyframeDensity.ShouldDraw(keys, 60, 0, 3));
        Assert.False(PlaybackKeyframeDensity.ShouldDraw(keys, 60, 1000, 0));
    }

    /// <summary>
    /// 判斷要用「平均」間隔而不是任兩個相鄰的間隙：頭一個 GOP 特別短（場景切換）時，
    /// 只看第一對間隙會整段不畫——操作員放大後整段沒有刻度，卻只有那一小段真有問題。
    /// </summary>
    [Fact]
    public void 開頭有一個短GOP不會讓整段失去刻度()
    {
        // 100 秒窗／1000px ＝ 10px/秒。29 個正常 GOP（每 2 秒）＋ 開頭一個 0.1 秒的短 GOP。
        var keys = new List<SegmentKeyframe> { new(0, 0), new(0.1, 100) };
        for (var i = 1; i < 30; i++)
        {
            keys.Add(new SegmentKeyframe(0.1 + i * 2, 1000 + i * 1024));
        }

        Assert.True(PlaybackKeyframeDensity.ShouldDraw(keys, 60, 1000, 3));
    }
}
