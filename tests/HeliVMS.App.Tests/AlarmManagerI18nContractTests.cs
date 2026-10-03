using System.Text.RegularExpressions;
using HeliVMS.Storage;

namespace HeliVMS.App.Tests;

/// <summary>
/// 警報管理器 WPF 多語系契約（M57 §14.7 P1）。
///
/// <para>
/// 桌面端沒有 WPF 執行期測試環境，這份契約把「畫面文字不得硬編中文」與
/// 「程式碼引用的每個多語系鍵都必須三語齊備」釘住。沒有這道棘輪，
/// 之後新增欄位很容易又寫回硬編字串，而現有測試不會察覺。
/// </para>
/// </summary>
public sealed class AlarmManagerI18nContractTests
{
    private const string Xaml = "src/HeliVMS.App/AlarmManagerWindow.xaml";
    private const string Code = "src/HeliVMS.App/AlarmManagerWindow.xaml.cs";

    [Fact]
    public void 警報管理器畫面不再硬編中文()
    {
        var xaml = SourceContract.Read(Xaml);
        Assert.DoesNotMatch(@"[\u4e00-\u9fff]", xaml);
    }

    [Fact]
    public void 警報管理器載入時套用多語系()
    {
        var code = SourceContract.Read(Code);

        Assert.Contains("ApplyI18n", code);
        Assert.Contains("\"AlarmManager.Title\"", code);

        // 建構子必須呼叫，否則視窗會停在 XAML 的空白文字。
        Assert.Matches(@"ApplyI18n\s*\(\s*\)\s*;", code);
    }

    [Fact]
    public void 警報管理器每個多語系鍵都具備三語()
    {
        var code = SourceContract.Read(Code);

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(code, "\"(AlarmManager\\.[A-Za-z]+)\""))
        {
            keys.Add(m.Groups[1].Value);
        }

        foreach (var status in AlarmEventStatus.All)
        {
            keys.Add("AlarmStatus." + status);
        }

        foreach (var priority in AlarmPriority.All)
        {
            keys.Add("AlarmPriority." + priority);
        }

        Assert.NotEmpty(keys);
        foreach (var lang in I18n.Languages)
        {
            foreach (var key in keys)
            {
                // 缺鍵時 Get 會回傳 key 本身，等於畫面直接顯示 "AlarmManager.Xxx"。
                Assert.NotEqual(key, I18n.Get(lang, key));
            }
        }
    }
}
