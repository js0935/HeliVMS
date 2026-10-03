using System.Text.RegularExpressions;
using HeliVMS.Storage;

namespace HeliVMS.App.Tests;

/// <summary>
/// POS 視窗 WPF 多語系契約（M57 §14.7 P1）。
///
/// <para>
/// 畫面文字與動態訊息（查詢筆數、匯入統計、對帳摘要）皆須走 <c>Localizer</c>;
/// 這份契約擋硬編中文回流，並要求程式碼引用的每個 <c>Pos.*</c> 鍵三語齊備。
/// </para>
/// </summary>
public sealed class PosI18nContractTests
{
    private const string Xaml = "src/HeliVMS.App/PosWindow.xaml";
    private const string Code = "src/HeliVMS.App/PosWindow.xaml.cs";

    [Fact]
    public void POS視窗畫面不再硬編中文()
    {
        var xaml = SourceContract.Read(Xaml);
        Assert.DoesNotMatch(@"[\u4e00-\u9fff]", xaml);
    }

    [Fact]
    public void POS視窗載入時套用多語系()
    {
        var code = SourceContract.Read(Code);

        Assert.Contains("\"Pos.Title\"", code);
        Assert.Matches(@"ApplyI18n\s*\(\s*\)\s*;", code);
    }

    [Fact]
    public void POS視窗每個多語系鍵都具備三語()
    {
        var code = SourceContract.Read(Code);

        var keys = Regex.Matches(code, "\"(Pos\\.[A-Za-z0-9]+)\"")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(keys);
        foreach (var lang in I18n.Languages)
        {
            foreach (var key in keys)
            {
                Assert.NotEqual(key, I18n.Get(lang, key));
            }
        }
    }
}
