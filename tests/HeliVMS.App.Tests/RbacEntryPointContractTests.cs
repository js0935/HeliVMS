using System.Text.RegularExpressions;

namespace HeliVMS.App.Tests;

/// <summary>
/// 角色入口契約（M233／§18.6「本機帳號＋RBAC」）。
///
/// <para>
/// 授權契約（<see cref="LicenseEntryPointContractTests"/>）防的是「藏了按鈕但 handler 沒擋旗標」，
/// 角色限制有一模一樣的形狀：<c>MainWindow.ApplyRoleRestrictions</c> 會把管理功能對 viewer 設成
/// <c>IsEnabled = false</c>，但那只是關掉滑鼠——真正負責硬擋的是 handler 裡的
/// <c>RequireAdmin()</c>／<c>SessionContext.IsAdmin</c>。少了它，viewer 用鍵盤或程式化呼叫照樣進得去。
/// </para>
///
/// <para>
/// 與授權契約相同的理由：桌面端沒有 WPF 執行期測試，靠人工複查擋不住第 N 次「新增按鈕忘了 guard」。
/// 這份契約把「每個停用給 viewer 的按鈕，其接線到的 handler 必須擋管理員」寫成可執行斷言，
/// 並用來源對帳把 <c>ApplyRoleRestrictions</c> 與表格綁在一起，避免有人加了按鈕卻沒補守衛。
/// </para>
/// </summary>
public sealed class RbacEntryPointContractTests
{
    /// <summary>
    /// <c>ApplyRoleRestrictions</c> 對 viewer 停用的按鈕：接線到的 handler（必要時追一層轉呼叫）
    /// 必須擋管理員。表格與 <c>ApplyRoleRestrictions</c> 雙向對帳。
    /// </summary>
    private static readonly (string Window, string Element, string Handler)[] ViewerDisabled =
    [
        ("MainWindow", "SettingsButton", "OnSettingsClicked"),
        ("MainWindow", "ExportButton", "OnExportClicked"),
        ("MainWindow", "ExportCenterButton", "OnExportCenterClicked"),
        ("MainWindow", "RedactionButton", "OnRedactionClicked"),
        ("MainWindow", "DewarpButton", "OnDewarpClicked"),
        ("MainWindow", "ReportsButton", "OnReportsClicked"),
        ("MainWindow", "RulesButton", "OnRulesClicked"),
        ("MainWindow", "SynopsisButton", "OnSynopsisClicked"),
        ("MainWindow", "LegalHoldButton", "OnLegalHoldClicked"),
        ("MainWindow", "AuditButton", "OnAuditClicked"),
        ("MainWindow", "AddChannelButton", "OnAddChannelClicked"),
        ("MainWindow", "OnvifButton", "OnOnvifClicked"),
        ("MainWindow", "ScheduleButton", "OnScheduleClicked"),
        ("MainWindow", "PatrolButton", "OnPatrolClicked"),
        ("MainWindow", "PtzButton", "OnPtzClicked"),
        ("MainWindow", "RecordButton", "OnRecordClicked"),
        ("MainWindow", "FailoverButton", "OnFailoverClicked"),
        ("MainWindow", "AudioButton", "OnAudioClicked"),
    ];

    /// <summary>
    /// 沒有對 viewer 停用、但動作本身限管理員的入口（例如事件中心 CSV 匯出與匯出中心同權限）。
    /// 這種「看得到、按下去才被擋」的入口，handler 是唯一的防線，同樣要進契約。
    /// </summary>
    private static readonly (string Window, string Element, string Handler)[] HandlerOnly =
    [
        ("EventCenterWindow", "ExportCsvButton", "OnExportCsvClicked"),
    ];

    public static TheoryData<string, string, string> ViewerDisabledButtons()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var (window, element, handler) in ViewerDisabled)
        {
            data.Add(window, element, handler);
        }

        return data;
    }

    public static TheoryData<string, string, string> HandlerOnlyButtons()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var (window, element, handler) in HandlerOnly)
        {
            data.Add(window, element, handler);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ViewerDisabledButtons))]
    public void 停用給viewer的按鈕_handler要擋管理員(string window, string element, string handler)
    {
        Assert.True(
            EnforcesAdmin(window, handler),
            $"{window}.{element} 對 viewer 停用，但 {handler} 沒有 RequireAdmin()／SessionContext.IsAdmin："
            + "viewer 用鍵盤或程式化呼叫照樣進得去");
    }

    [Theory]
    [MemberData(nameof(HandlerOnlyButtons))]
    public void 只在handler擋管理員的入口_仍要擋(string window, string element, string handler)
    {
        Assert.True(
            EnforcesAdmin(window, handler),
            $"{window}.{element} 沒有對 viewer 停用，因此 {handler} 必須是硬擋，但它沒有擋管理員");
    }

    [Theory]
    [MemberData(nameof(ViewerDisabledButtons))]
    [MemberData(nameof(HandlerOnlyButtons))]
    public void 契約表的按鈕與XAML接線一致(string window, string element, string handler)
    {
        Assert.Equal(handler, WiredHandler(window, element));
    }

    [Fact]
    public void 角色限制涵蓋所有停用按鈕()
    {
        // ApplyRoleRestrictions 是停用清單的真相；表格少一列就等於有個停用按鈕沒人驗 handler。
        var declared = ViewerDisabled
            .Select(entry => entry.Element)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(DisabledForViewers(), declared);
    }

    [Fact]
    public void 角色限制要在主視窗載入時套用()
    {
        // 忘了呼叫 ApplyRoleRestrictions，viewer 就會看到整排管理按鈕，且沒有任何測試會發現。
        Assert.Contains(
            "ApplyRoleRestrictions",
            BodyOf(Read("src/HeliVMS.App/MainWindow.xaml.cs"), "OnLoaded"));
    }

    /// <summary><c>ApplyRoleRestrictions</c> 內以 <c>isAdmin</c> 停用的元素名稱。</summary>
    private static string[] DisabledForViewers()
    {
        var body = BodyOf(Read("src/HeliVMS.App/MainWindow.xaml.cs"), "ApplyRoleRestrictions");
        Assert.NotEqual(string.Empty, body);

        return
        [
            .. Regex.Matches(body, @"(\w+)\.IsEnabled\s*=\s*isAdmin")
                .Select(m => m.Groups[1].Value)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal),
        ];
    }

    /// <summary>handler（必要時追一層轉呼叫）是否擋管理員。</summary>
    private static bool EnforcesAdmin(string window, string method, int depth = 0)
    {
        if (depth > 3)
        {
            return false;
        }

        var body = BodyOf(Read($"src/HeliVMS.App/{window}.xaml.cs"), method);
        if (body.Length == 0)
        {
            return false;
        }

        if (Regex.IsMatch(body, @"RequireAdmin\s*\(|SessionContext\.IsAdmin"))
        {
            return true;
        }

        // 轉呼叫：OnPatrolClicked() => OpenPatrolWindow()，或 OnPtzClicked 內呼叫 OpenPtz(...)。
        foreach (var forwarded in Regex.Matches(body, @"\b(Open\w+)\s*\(")
                     .Select(m => m.Groups[1].Value)
                     .Distinct(StringComparer.Ordinal))
        {
            if (EnforcesAdmin(window, forwarded, depth + 1))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>XAML 實際接線到的事件處理器。</summary>
    private static string WiredHandler(string window, string element)
    {
        var xaml = Read($"src/HeliVMS.App/{window}.xaml");
        var tag = Regex.Match(
            xaml,
            $@"<[A-Za-z][^<>]*?x:Name=""{Regex.Escape(element)}""[^<>]*?>",
            RegexOptions.Singleline).Value;

        var handler = Regex.Match(tag, @"\bClick=""(\w+)""");
        return handler.Success ? handler.Groups[1].Value : string.Empty;
    }

    private static string BodyOf(string source, string method)
    {
        var signature = Regex.Match(
            source,
            $@"(?:private|public|internal|protected|static)[\s\w<>\[\],\.\?]*\b{Regex.Escape(method)}\s*\(");

        if (!signature.Success)
        {
            return string.Empty;
        }

        var i = signature.Index + signature.Length - 1;
        var depth = 0;
        for (; i < source.Length; i++)
        {
            if (source[i] == '(')
            {
                depth++;
            }
            else if (source[i] == ')' && --depth == 0)
            {
                i++;
                break;
            }
        }

        while (i < source.Length && char.IsWhiteSpace(source[i]))
        {
            i++;
        }

        if (i + 1 < source.Length && source[i] == '=' && source[i + 1] == '>')
        {
            var end = source.IndexOf(';', i);
            return end < 0 ? string.Empty : source[i..end];
        }

        if (i >= source.Length || source[i] != '{')
        {
            return string.Empty;
        }

        var open = i;
        depth = 0;
        for (; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}' && --depth == 0)
            {
                return source[open..(i + 1)];
            }
        }

        return string.Empty;
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(RepoRoot(), relativePath));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HeliVMS.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException(
                $"自 {AppContext.BaseDirectory} 往上找不到 HeliVMS.slnx，無法驗證角色入口契約。");
    }
}
