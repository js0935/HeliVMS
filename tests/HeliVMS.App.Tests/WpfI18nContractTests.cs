using System.Text.RegularExpressions;
using HeliVMS.Storage;

namespace HeliVMS.App.Tests;

/// <summary>
/// WPF 視窗多語系棘輪（M57 §14.7）。逐窗在地化時，把 <c>(名稱, XAML, 程式碼, 鍵前綴)</c>
/// 加進 <see cref="Windows"/>，這三條就一併守住：畫面不再硬編中文、載入時呼叫
/// <c>ApplyI18n</c>、程式碼引用的鍵在每個語言都有翻譯。缺一即紅。
/// </summary>
public sealed class WpfI18nContractTests
{
    public static IEnumerable<object[]> Windows =>
    [
        ["SigningKeysWindow", "src/HeliVMS.App/SigningKeysWindow.xaml", "src/HeliVMS.App/SigningKeysWindow.xaml.cs", new[] { "Signing" }],
        ["MapWindow", "src/HeliVMS.App/MapWindow.xaml", "src/HeliVMS.App/MapWindow.xaml.cs", new[] { "Map" }],
        ["NotificationLogWindow", "src/HeliVMS.App/NotificationLogWindow.xaml", "src/HeliVMS.App/NotificationLogWindow.xaml.cs", new[] { "NotifyLog" }],
        ["AuditLogWindow", "src/HeliVMS.App/AuditLogWindow.xaml", "src/HeliVMS.App/AuditLogWindow.xaml.cs", new[] { "Audit" }],
        ["LegalHoldWindow", "src/HeliVMS.App/LegalHoldWindow.xaml", "src/HeliVMS.App/LegalHoldWindow.xaml.cs", new[] { "LegalHold" }],
        ["EvidenceWindow", "src/HeliVMS.App/EvidenceWindow.xaml", "src/HeliVMS.App/EvidenceWindow.xaml.cs", new[] { "Evidence" }],
        ["IoWindow", "src/HeliVMS.App/IoWindow.xaml", "src/HeliVMS.App/IoWindow.xaml.cs", new[] { "Io" }],
        ["SynopsisWindow", "src/HeliVMS.App/SynopsisWindow.xaml", "src/HeliVMS.App/SynopsisWindow.xaml.cs", new[] { "Synopsis" }],
        ["SnapshotRedactWindow", "src/HeliVMS.App/SnapshotRedactWindow.xaml", "src/HeliVMS.App/SnapshotRedactWindow.xaml.cs", new[] { "SnapRedact" }],
    ];

    [Theory]
    [MemberData(nameof(Windows))]
    public void 視窗畫面不再硬編中文(string name, string xaml, string code, string[] prefixes)
    {
        _ = name;
        _ = code;
        _ = prefixes;
        Assert.DoesNotMatch(@"[\u4e00-\u9fff]", SourceContract.Read(xaml));
    }

    [Theory]
    [MemberData(nameof(Windows))]
    public void 視窗載入時套用多語系(string name, string xaml, string code, string[] prefixes)
    {
        _ = name;
        _ = xaml;
        var source = SourceContract.Read(code);
        Assert.Contains($"\"{prefixes[0]}.Title\"", source);
        Assert.Matches(@"ApplyI18n\s*\(\s*\)\s*;", source);
    }

    [Theory]
    [MemberData(nameof(Windows))]
    public void 視窗每個多語系鍵都具備三語(string name, string xaml, string code, string[] prefixes)
    {
        _ = name;
        _ = xaml;
        var source = SourceContract.Read(code);

        var keys = prefixes
            .SelectMany(prefix => Regex.Matches(source, $"\"({prefix}\\.[A-Za-z0-9]+)\"")
                .Select(m => m.Groups[1].Value))
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
