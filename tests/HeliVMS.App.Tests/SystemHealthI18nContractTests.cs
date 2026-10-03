using System.Text.RegularExpressions;
using HeliVMS.Storage;

namespace HeliVMS.App.Tests;

/// <summary>
/// 系統健康視窗 WPF 多語系契約（M57 §14.7）。
///
/// <para>
/// 標題、欄位標頭、摘要與更新／失敗訊息皆須走 <c>Localizer</c>；
/// 這份契約擋硬編中文回流，並要求程式碼引用的每個 <c>Health.*</c> 鍵三語齊備。
/// </para>
/// </summary>
public sealed class SystemHealthI18nContractTests
{
    private const string Xaml = "src/HeliVMS.App/SystemHealthWindow.xaml";
    private const string Code = "src/HeliVMS.App/SystemHealthWindow.xaml.cs";

    [Fact]
    public void 系統健康視窗畫面不再硬編中文()
    {
        var xaml = SourceContract.Read(Xaml);
        Assert.DoesNotMatch(@"[\u4e00-\u9fff]", xaml);
    }

    [Fact]
    public void 系統健康視窗載入時套用多語系()
    {
        var code = SourceContract.Read(Code);

        Assert.Contains("\"Health.Title\"", code);
        Assert.Matches(@"ApplyI18n\s*\(\s*\)\s*;", code);
    }

    [Fact]
    public void 系統健康視窗每個多語系鍵都具備三語()
    {
        var code = SourceContract.Read(Code);

        var keys = Regex.Matches(code, "\"(Health\\.[A-Za-z0-9]+)\"")
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
