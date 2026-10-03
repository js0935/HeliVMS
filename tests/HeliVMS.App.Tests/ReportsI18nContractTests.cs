using System.Text.RegularExpressions;
using HeliVMS.Storage;

namespace HeliVMS.App.Tests;

/// <summary>
/// 報表視窗 WPF 多語系契約（M57 §14.7 P1）。
///
/// <para>
/// 報表畫面同時有靜態欄位與大量動態字串（彙總、趨勢 tooltip、排程狀態）；這份契約
/// 要求畫面無硬編中文，且程式碼引用的每個 <c>Report.*</c> 鍵都必須三語齊備，
/// 避免新欄位只加繁中、其他語言直接顯示出鍵名。
/// </para>
/// </summary>
public sealed class ReportsI18nContractTests
{
    private const string Xaml = "src/HeliVMS.App/ReportsWindow.xaml";
    private const string Code = "src/HeliVMS.App/ReportsWindow.xaml.cs";

    [Fact]
    public void 報表視窗畫面不再硬編中文()
    {
        var xaml = SourceContract.Read(Xaml);
        Assert.DoesNotMatch(@"[\u4e00-\u9fff]", xaml);
    }

    [Fact]
    public void 報表視窗載入時套用多語系()
    {
        var code = SourceContract.Read(Code);

        Assert.Contains("\"Report.Title\"", code);
        Assert.Matches(@"ApplyI18n\s*\(\s*\)\s*;", code);
    }

    [Fact]
    public void 報表視窗每個多語系鍵都具備三語()
    {
        var code = SourceContract.Read(Code);

        var keys = Regex.Matches(code, "\"(Report\\.[A-Za-z0-9]+)\"")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(keys);
        foreach (var lang in I18n.Languages)
        {
            foreach (var key in keys)
            {
                // 缺鍵時 Get 會回傳 key 本身，等於畫面直接顯示 "Report.Xxx"。
                Assert.NotEqual(key, I18n.Get(lang, key));
            }
        }
    }
}
