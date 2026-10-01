using System.Collections.Concurrent;
using System.Reflection;
using System.Text.RegularExpressions;
using HeliVMS.Licensing;
using HeliVMS.Storage;

namespace HeliVMS.App.Tests;

/// <summary>
/// 授權入口契約（M226／§19.4「合併檢查，避免遠程繞過」）。
///
/// <para>
/// 稽核在接連三個里程碑裡找到<strong>同一類</strong>缺陷共四個案子，形狀都一樣——
/// 「可見性綁 X、handler 要 Y」或「handler 根本沒擋」：
/// </para>
/// <list type="bullet">
/// <item><description><c>AiToggle</c> 可見性綁 <c>ai.l1</c>、handler 要 <c>ai</c>：只買 ai 的客戶看得到開關、一按就失敗。</description></item>
/// <item><description><c>PatrolButton</c>／<c>OpenPatrolWindow</c> 完全沒擋，但 <c>/api/patrols</c> 屬 <c>schedule</c>：遠端被擋、本機可繞。</description></item>
/// <item><description>設定中心「企業身份」整區與三個 handler 沒擋，但 <c>/api/auth/providers</c> 屬 <c>ad</c>。</description></item>
/// <item><description><c>ShareWindow</c>／<c>OpenShareWindow</c> 沒擋，但 <c>/api/shares</c> 屬 <c>remote</c>。</description></item>
/// </list>
///
/// <para>
/// 桌面端沒有任何測試專案，靠人工複查擋不住第五次。這份契約把
/// 「每個受旗標控制的元素，XAML 實際接線到的 handler 必須擋住<strong>同一個</strong>旗標」
/// 寫成可執行的斷言：隱藏只是體面（鍵盤與程式化點擊繞得過），handler 才是硬擋。
/// </para>
///
/// <para>
/// 斷言走原始碼而非反射：<c>HeliVMS.App</c> 是 WPF 專案，測試若要參照就得拖進
/// Windows Desktop 執行期，而這些守衛都是 private 方法、不開視窗也驗不到。
/// 契約要驗的正是「有沒有接錯線」，讀原始碼反而是直接對象。
/// </para>
/// </summary>
public sealed class LicenseEntryPointContractTests
{
    /// <summary>受旗標控制的按鈕／開關：可見性綁的旗標必須等於 XAML 接線到的 handler 所擋的旗標。</summary>
    private static readonly Gate[] Buttons =
    [
        new("MainWindow", "ScheduleButton", nameof(LicenseFeatures.Schedule), "OnScheduleClicked"),
        new("MainWindow", "PatrolButton", nameof(LicenseFeatures.Schedule), "OnPatrolClicked"),
        new("MainWindow", "DetectionButton", nameof(LicenseFeatures.AiL1), "OnDetectionClicked"),
        new("MainWindow", "AudioButton", nameof(LicenseFeatures.Ai), "OnAudioClicked"),
        new("MainWindow", "MapButton", nameof(LicenseFeatures.Gis), "OnMapClicked"),
        new("MainWindow", "IoButton", nameof(LicenseFeatures.Gis), "OnIoClicked"),
        new("MainWindow", "AiToggle", nameof(LicenseFeatures.Ai), "OnAiToggleChanged"),
        new("SettingsWindow", "LaunchScheduleButton", nameof(LicenseFeatures.Schedule), "OnLaunchScheduleClicked"),
        new("SettingsWindow", "LaunchDetectionButton", nameof(LicenseFeatures.AiL1), "OnLaunchDetectionClicked"),
        new("SettingsWindow", "TamperEnabledBox", nameof(LicenseFeatures.Ai), "OnTamperToggled"),
        new("SettingsWindow", "ShareApplyButton", nameof(LicenseFeatures.Remote), "OnApplyShareClicked"),
        new("ShareWindow", "ShareCreateButton", nameof(LicenseFeatures.Remote), "OnCreateClicked"),
        new("ExportCenterWindow", "ShareFromExportButton", nameof(LicenseFeatures.Remote), "OnShareClicked"),
        new("EventCenterWindow", "MapLocateButton", nameof(LicenseFeatures.Gis), "OnMapLocateClicked"),
    ];

    /// <summary>
    /// 受旗標控制但沒有按鈕語意的元素（輸入欄、狀態文字）：只需可見性綁定正確。
    /// 列在此處是為了讓涵蓋率檢查知道它們是「有意不驗」而非漏列。
    /// </summary>
    private static readonly Gate[] DataFields =
    [
        new("SettingsWindow", "ShareEnabledBox", nameof(LicenseFeatures.Remote)),
        new("SettingsWindow", "SharePortBox", nameof(LicenseFeatures.Remote)),
        new("SettingsWindow", "ShareBaseUrlBox", nameof(LicenseFeatures.Remote)),
        new("SettingsWindow", "ShareServiceStatusText", nameof(LicenseFeatures.Remote)),
    ];

    /// <summary>整區隱藏的容器：容器沒有 Click 可驗，改為逐一手動列出區內 handler。</summary>
    private static readonly (Gate Gate, string[] Handlers)[] Sections =
    [
        (
            new Gate("SettingsWindow", "EnterpriseSection", nameof(LicenseFeatures.AdSso)),
            ["OnEntAddClicked", "OnEntToggleClicked", "OnEntDeleteClicked"]
        ),
    ];

    /// <summary>能力 →（遠端路徑前綴、桌面入口旗標）。桌面旗標可為遠端旗標的「更嚴版本」。</summary>
    private static readonly Capability[] Capabilities =
    [
        new("/api/recording/schedules", "ScheduleButton", nameof(LicenseFeatures.Schedule)),
        new("/api/patrols", "PatrolButton", nameof(LicenseFeatures.Schedule)),
        new("/api/shares", "ShareCreateButton", nameof(LicenseFeatures.Remote)),
        new("/api/auth/providers", "EnterpriseSection", nameof(LicenseFeatures.AdSso)),
        new("/api/detections", "DetectionButton", nameof(LicenseFeatures.AiL1)),
        new("/api/audio", "AudioButton", nameof(LicenseFeatures.Ai)),
    ];

    /// <summary>
    /// 遠端有擋、但 WPF 端確實沒有對應入口的前綴。列在此處是為了讓反向涵蓋率檢查
    /// 知道它們是「查過了、確定沒有桌面入口」而不是漏比——新增一個沒有桌面入口的
    /// 能力時要補一列說明理由，而不是讓斷言默默失效。
    /// </summary>
    private static readonly (string Prefix, string Reason)[] NoDesktopEntry =
    [
        ("/api/clip", "向量／文字檢索只有 API 與 lib.js 的純函式 renderer，WPF 尚無對應視窗"),
    ];

    /// <summary>旗標蘊含：較嚴的旗標必須同時開通較鬆的旗標。</summary>
    private static readonly (string Stronger, string Weaker)[] Implications =
    [
        (LicenseFeatures.AiL1, LicenseFeatures.Ai),
        (LicenseFeatures.AiL2, LicenseFeatures.Ai),
    ];

    public static TheoryData<string, string, string, string> GatedButtons()
    {
        var data = new TheoryData<string, string, string, string>();
        foreach (var gate in Buttons)
        {
            data.Add(gate.Window, gate.Element, gate.Feature, gate.Handler);
        }

        return data;
    }

    public static TheoryData<string, string, string> GatedDataFields()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var gate in DataFields)
        {
            data.Add(gate.Window, gate.Element, gate.Feature);
        }

        return data;
    }

    public static TheoryData<string, string> GatedSections()
    {
        var data = new TheoryData<string, string>();
        foreach (var (gate, _) in Sections)
        {
            data.Add(gate.Window, gate.Element);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(GatedButtons))]
    public void 可見性綁定的旗標_等於_handler擋的旗標(string window, string element, string feature, string handler)
    {
        var flag = FeatureValue(feature);
        Assert.True(
            VisibilityBindings(window, element).Contains(flag),
            $"{window}.{element} 應以 {feature}（{flag}）控制可見性");

        Assert.True(
            GuardedFeatures(window, handler).Contains(flag),
            $"{window} 的 {handler} 應擋住 {feature}（{flag}）");
    }

    [Theory]
    [MemberData(nameof(GatedButtons))]
    public void handler擋的旗標_不得比可見性更寬鬆(string window, string element, string feature, string handler)
    {
        // 反向也釘死：handler 只擋同一個旗標。曾經發生過可見性綁 ai.l1、handler 要 ai，
        // 結果只買 ai 的客戶「看得到開關、一按就被擋」——那是授權不一致，不是更嚴格。
        var expected = FeatureValue(feature);
        var guards = GuardedFeatures(window, handler);

        Assert.True(
            guards.Length > 0,
            $"{window}.{element} 的 {handler} 沒有任何 RequireFeature／Allows 守衛："
            + "藏起按鈕擋不掉鍵盤與程式化點擊");

        Assert.Equal(new[] { expected }, guards);
    }

    [Theory]
    [MemberData(nameof(GatedButtons))]
    public void XAML實際接線的handler_與契約表一致(string window, string element, string feature, string handler)
    {
        // 契約表若寫了一個沒被接線的 handler 名稱，等於在驗一個不存在的方法，
        // 真正的守衛會被宣告「沒有守衛」。這條把表與 XAML 綁在一起。
        Assert.Equal(new[] { handler }, WiredHandlers(window, element));

        Assert.True(
            VisibilityBindings(window, element).Contains(FeatureValue(feature)),
            $"{window}.{element}（{feature}）在 XAML 上已找不到接線對應的旗標綁定，契約表可能已過期");
    }

    [Theory]
    [MemberData(nameof(GatedDataFields))]
    public void 資料欄位只要求可見性綁定正確(string window, string element, string feature)
        => Assert.Contains(FeatureValue(feature), VisibilityBindings(window, element));

    [Fact]
    public void 容器內每個handler都擋同一個旗標()
    {
        foreach (var (gate, handlers) in Sections)
        {
            var flag = FeatureValue(gate.Feature);
            Assert.Contains(flag, VisibilityBindings(gate.Window, gate.Element));

            foreach (var handler in handlers)
            {
                var guards = GuardedFeatures(gate.Window, handler);
                Assert.True(guards.Length > 0, $"{gate.Window} 的 {handler} 沒有旗標守衛");
                Assert.Equal(new[] { flag }, guards);
            }
        }
    }

    [Fact]
    public void 契約表涵蓋所有旗標可見性綁定()
    {
        // 新增一個 Apply(Element, LicenseFeatures.X) 卻沒列進契約表，等於有人加了受控元素
        // 卻沒想過它的 handler 該擋什麼。逼著新增的人同時補上守衛。
        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (var gate in Buttons.Cast<Gate>().Concat(DataFields).Concat(Sections.Select(s => s.Gate)))
        {
            declared.Add(Key(gate.Window, gate.Element));
        }

        var undeclared = AppSources()
            .SelectMany(pair => BindingsIn(pair.Value)
                .Select(binding => Key(WindowOf(pair.Key), binding.Element))
                .Where(key => !declared.Contains(key))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(key => key, StringComparer.Ordinal))
            .ToList();

        Assert.True(
            undeclared.Count == 0,
            $"下列旗標可見性綁定未列入契約表，請補上元素與其 handler 的守衛：{string.Join("、", undeclared)}");
    }

    [Fact]
    public void 旗標檢查一律使用具名常數()
    {
        // 字串旗標會漂移：改名時編譯器不會報錯，只會在執行時靜默失效（未授權時整個功能關掉）。
        var literals = AppSources()
            .Where(pair => Regex.IsMatch(pair.Value, "(?:RequireFeature|Allows|Apply)\\s*\\(\\s*\""))
            .Select(pair => Path.GetFileName(pair.Key))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.True(literals.Count == 0, $"旗標檢查不可使用字串常數：{string.Join("、", literals)}");
    }

    /// <summary>
    /// 桌面入口不得比遠端 API 寬鬆：同一能力，遠端要 X 而本機只要較弱的 Y 就是繞過。
    /// 桌面可以更嚴（偵測設定要 ai.l1、遠端只要 ai），但不能更鬆。
    /// </summary>
    [Fact]
    public void 桌面入口不得比遠端API寬鬆()
    {
        var rules = RemoteGateRules();

        foreach (var capability in Capabilities)
        {
            Assert.True(
                rules.TryGetValue(capability.Prefix, out var remoteFlag),
                $"LicenseGateMiddleware 未涵蓋 {capability.Prefix}：遠端沒擋、本機擋了只是碰巧");

            var localFlag = FeatureValue(capability.Feature);
            Assert.True(
                remoteFlag == localFlag || Implies(localFlag, remoteFlag),
                $"{capability.Element} 在本機要 {localFlag}，但 {capability.Prefix} 在遠端只要 {remoteFlag}"
                + "——遠端被擋、本機可繞，等於沒有合併檢查");
        }
    }

    /// <summary>
    /// 反向涵蓋率：<see cref="Capabilities"/> 只往下比，遠端多擋一條規則時不會有人發現。
    /// 閘門新增前綴而桌面忘了接（或忘了說明為何沒有入口），都要在這裡被逼出來。
    /// </summary>
    [Fact]
    public void 遠端閘門的每條規則都要有桌面對應或明確豁免()
    {
        var rules = RemoteGateRules();
        var accounted = Capabilities.Select(c => c.Prefix)
            .Concat(NoDesktopEntry.Select(e => e.Prefix))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = rules.Keys
            .Where(prefix => !accounted.Contains(prefix))
            .OrderBy(prefix => prefix, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"LicenseGateMiddleware 新增了 {string.Join("、", missing)}，但桌面端沒有對應入口，"
            + $"也未在 NoDesktopEntry 說明原因——請補上桌面契約或明確豁免");
    }

    /// <summary>
    /// 豁免清單本身要準確：寫了不存在的規則等於給自己開了一個永遠不會被驗的後門。
    /// </summary>
    [Fact]
    public void 桌面豁免清單只列真正存在的遠端規則()
    {
        var rules = RemoteGateRules();

        foreach (var (prefix, reason) in NoDesktopEntry)
        {
            Assert.True(rules.ContainsKey(prefix), $"NoDesktopEntry 的 {prefix} 已不在閘門規則表，請移除");
            Assert.False(string.IsNullOrWhiteSpace(reason), $"{prefix} 必須寫明為何沒有桌面入口");
        }
    }

    [Fact]
    public void 等級矩陣裡桌面旗標蘊含遠端旗標()
    {
        // 旗標蘊含關係要真的成立，否則上面那條只是空殼：若某等級有 ai.l1 卻沒給 ai，
        // 客戶就會遇到「偵測設定看得到、/api/detections 卻 403」。
        foreach (var tier in LicenseTiers.All)
        {
            foreach (var (stronger, weaker) in Implications)
            {
                if (tier.Features.Contains(stronger))
                {
                    Assert.True(
                        tier.Features.Contains(weaker),
                        $"等級「{tier.Name}」有 {stronger} 卻沒有 {weaker}：蘊含關係不成立");
                }
            }
        }
    }

    private static bool Implies(string flag, string weaker) =>
        flag == weaker || Implications.Any(pair => pair.Stronger == flag && pair.Weaker == weaker);

    private static string Key(string window, string element) => $"{window}.{element}";

    /// <summary><c>MainWindow.xaml.cs</c> → <c>MainWindow</c>（契約表以視窗名稱索引）。</summary>
    private static string WindowOf(string codePath) =>
        Path.GetFileName(codePath).Replace(".xaml.cs", string.Empty, StringComparison.Ordinal);

    private static string FeatureValue(string constantName)
    {
        var field = typeof(LicenseFeatures).GetField(constantName, BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                $"LicenseFeatures 沒有 {constantName} 常數；契約表與實作已漂移，請一併更新。");

        return (string)field.GetRawConstantValue()!;
    }

    /// <summary>某視窗內，元素以 <c>LicenseUiGate.Apply</c> 綁定到的旗標值。</summary>
    private static string[] VisibilityBindings(string window, string element) =>
    [
        .. BindingsIn(Read($"src/HeliVMS.App/{window}.xaml.cs"))
            .Where(binding => binding.Element == element)
            .Select(binding => FeatureValue(binding.Feature))
            .Distinct(StringComparer.Ordinal),
    ];

    private static Binding[] BindingsIn(string code) =>
    [
        .. Regex.Matches(code, @"Apply\(\s*(\w+)\s*,\s*LicenseFeatures\.(\w+)")
            .Select(m => new Binding(m.Groups[1].Value, m.Groups[2].Value))
            .Distinct(),
    ];

    /// <summary>XAML 實際接線到的事件處理器（Click／Checked／Unchecked）。</summary>
    private static string[] WiredHandlers(string window, string element)
    {
        var tag = ElementMarkup(Read($"src/HeliVMS.App/{window}.xaml"), element);

        return tag.Length == 0
            ? []
            :
            [
                .. Regex.Matches(tag, @"\b(?:Click|Checked|Unchecked)=""(\w+)""")
                    .Select(m => m.Groups[1].Value)
                    .Distinct(StringComparer.Ordinal),
            ];
    }

    /// <summary>XAML 中該元素的完整標籤（含跨行屬性）。</summary>
    private static string ElementMarkup(string xaml, string element) =>
        Regex.Match(
            xaml,
            $@"<[A-Za-z][^<>]*?x:Name=""{Regex.Escape(element)}""[^<>]*?>",
            RegexOptions.Singleline).Value;

    /// <summary>handler（必要時追一層 <c>=&gt; Method()</c> 轉呼叫）實際擋住的旗標值。</summary>
    private static string[] GuardedFeatures(string window, string handler)
    {
        var body = BodyOf(Read($"src/HeliVMS.App/{window}.xaml.cs"), handler);
        if (body.Length == 0)
        {
            return [];
        }

        var guards = Regex.Matches(body, @"(?:RequireFeature|Allows)\s*\(\s*LicenseFeatures\.(\w+)")
            .Select(m => FeatureValue(m.Groups[1].Value))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // 轉呼叫：private void OnPatrolClicked(...) => OpenPatrolWindow();
        // 守衛在被轉呼叫的方法裡，照樣要驗到——否則這一層等於沒有。
        if (guards.Count == 0)
        {
            var forwarded = Regex.Match(body, @"=>\s*(\w+)\s*\(");
            if (forwarded.Success)
            {
                return GuardedFeatures(window, forwarded.Groups[1].Value);
            }
        }

        return [.. guards];
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

    /// <summary>WebApi 端 <c>LicenseGateMiddleware</c> 的路徑前綴 → 旗標值（遠端那一側的真相）。</summary>
    private static Dictionary<string, string> RemoteGateRules()
    {
        var source = Read("src/HeliVMS.WebApi/LicenseGateMiddleware.cs");
        var rules = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (Match match in Regex.Matches(source, @"\(""([^""]+)"",\s*LicenseFeatures\.(\w+)\)"))
        {
            rules[match.Groups[1].Value] = FeatureValue(match.Groups[2].Value);
        }

        Assert.True(rules.Count > 0, "LicenseGateMiddleware 的規則表解析不到，契約檢查失效");
        return rules;
    }

    private static IEnumerable<KeyValuePair<string, string>> AppSources() =>
        AppSourceCache.GetOrAdd(
            AppDirectory,
            dir => Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
                .ToDictionary(path => path, File.ReadAllText, StringComparer.Ordinal));

    private static string AppDirectory => Path.Combine(RepoRoot(), "src", "HeliVMS.App");

    private static readonly ConcurrentDictionary<string, string> FileCache = new(StringComparer.Ordinal);

    private static readonly ConcurrentDictionary<string, Dictionary<string, string>> AppSourceCache =
        new(StringComparer.Ordinal);

    private static string Read(string relativePath) =>
        FileCache.GetOrAdd(relativePath, path => File.ReadAllText(Path.Combine(RepoRoot(), path)));

    /// <summary>
    /// 往上找解決方案檔當作 repo 根目錄；找不到就丟出——寧可測試紅，
    /// 也不要靜默跳過整份契約（跳過等於測試看起來有跑、實際什麼都沒驗）。
    /// </summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HeliVMS.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException(
                $"自 {AppContext.BaseDirectory} 往上找不到 HeliVMS.slnx，無法驗證授權入口契約。");
    }

    private sealed record Gate(string Window, string Element, string Feature, string Handler = "");

    private sealed record Binding(string Element, string Feature);

    private sealed record Capability(string Prefix, string Element, string Feature);
}
