using HeliVMS.Storage;

namespace HeliVMS.App.Tests;

/// <summary>
/// 回放 UI 必須說出「這個格數的每格預算」（縮到多寬、更新率上限、是否總覽模式）。
/// </summary>
/// <remarks>
/// <para>
/// 沒有這行，「多路回放會卡頓」是無法回答的：36 格以上**設計上**只解關鍵影格
/// （每 2–20 秒換一張，見 <c>PlaybackWallBudget</c>），而畫面上沒有任何字說明——
/// 操作員只會回報「卡頓」，維運也只能猜。預算表的數字全部來自純函式，UI 不得自己抄一份。
/// </para>
/// </remarks>
public sealed class PlaybackWallBudgetUiContractTests
{
    private const string Code = "src/HeliVMS.App/PlaybackWindow.xaml.cs";
    private const string Xaml = "src/HeliVMS.App/PlaybackWindow.xaml";

    private static readonly string[] Keys =
    [
        "Playback.WallBudget",
        "Playback.WallBudgetNative",
        "Playback.WallBudgetUnlimited",
        "Playback.WallOverviewMode",
    ];

    [Fact]
    public void UI有每格預算的文字且預算來自純函式()
    {
        Assert.Contains("x:Name=\"WallBudgetText\"", SourceContract.Read(Xaml));

        var source = SourceContract.Read(Code);
        var at = source.IndexOf("WallBudgetText.Text =", StringComparison.Ordinal);
        Assert.True(at >= 0, "找不到設定每格預算文字的地方");

        var method = SourceContract.EnclosingMethod(source, at);
        var body = SourceContract.BodyOf(source, method);

        // 預算表只有一份（PlaybackWallBudget）：UI 自己抄一份格數→寬度的表，遲早與解碼端不同步。
        Assert.Contains("PlaybackWallBudget.For", body);
        Assert.Contains("budget.KeyframesOnly", body);
        Assert.Contains("Playback.WallOverviewMode", body);
    }

    [Fact]
    public void 總覽模式的說明與格數門檻都由預算表決定()
    {
        // 36 格以上是總覽模式（設計取捨），16 格不是——UI 的文案分支必須跟著這條線走。
        Assert.True(HeliVMS.Shared.PlaybackWallBudget.For(64).KeyframesOnly);
        Assert.True(HeliVMS.Shared.PlaybackWallBudget.For(36).KeyframesOnly);
        Assert.False(HeliVMS.Shared.PlaybackWallBudget.For(16).KeyframesOnly);
    }

    [Fact]
    public void 每格預算的文案三語齊備()
    {
        foreach (var lang in I18n.Languages)
        {
            foreach (var key in Keys)
            {
                Assert.NotEqual(key, I18n.Get(lang, key));
            }
        }
    }
}
