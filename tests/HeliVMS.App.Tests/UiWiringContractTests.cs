using System.Text.RegularExpressions;

namespace HeliVMS.App.Tests;

/// <summary>
/// 所有視窗的選單與按鈕「真的有接線」契約。
/// </summary>
/// <remarks>
/// <para>
/// 為什麼需要這份契約：桌面端有 37 個 XAML 視窗、338 個事件處理器接線。
/// WPF 的 XAML 接線<b>不會在建置期驗證</b>——把 <c>Click="OnFooClicked"</c>
///
/// 打成一個不存在的方法名，編譯照樣過得了，執行期才在「按下去那一刻」丟
/// <see cref="System.InvalidOperationException"/>。而且症狀很難看：畫面上按鈕在、
/// 點下去沒反應，使用者會以為程式當掉了，而不是接線打錯字。
/// </para>
/// <para>
/// 這份契約掃全部 XAML（不是只挑主視窗），因為「按鈕壞掉」這種問題正是
/// 最容易被漏掉的：主力視窗有人天天按，冷門視窗（例如 LegalHold、EdgeBackfill）
/// 可能整個部署期間沒人點過，等到出事時已經在現場了。
/// </para>
/// <para>
/// 邊界：這份契約只能證明「接線存在且處理器不是空的」。它<b>不能</b>證明
/// 按下去之後功能是對的——那需要 WPF 執行期測試環境，桌面專案目前沒有。
/// 誠實地說明能驗到哪裡，比讓人以為按鈕全數驗過要好。
/// </para>
/// </remarks>
public sealed class UiWiringContractTests
{
    /// <summary>
    /// 掃描 XAML 事件屬性時要涵蓋的事件。
    /// </summary>
    /// <remarks>
    /// 刻意<b>不</b>含 <c>Command</c>：這個專案不用 MVVM，掃到只會得到
    /// 找不到的事件而誤報。每一項加進來都要能對應到真的會讓控制項動起來的事件。
    /// </remarks>
    private static readonly string[] EventAttributes =
    [
        "Click", "SelectionChanged", "Checked", "Unchecked", "Toggled",
        "TextChanged", "KeyDown", "PreviewKeyDown", "MouseDoubleClick",
        "MouseEnter", "MouseLeave", "MouseMove", "ContextMenuOpening",
        "DragOver", "Drop", "ValueChanged", "PasswordChanged",
        "GotFocus", "LostFocus", "SizeChanged", "Loaded", "Unloaded", "Closing",
    ];

    /// <summary>視窗清單（排除 App.xaml，它是資源字典不是視窗）。</summary>
    private static IEnumerable<(string Xaml, string Code)> WindowSources()
    {
        var dir = Path.Combine(SourceContract.RepoRoot(), "src", "HeliVMS.App");
        foreach (var xaml in Directory.EnumerateFiles(dir, "*.xaml").OrderBy(p => p, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(xaml);
            if (name == "App.xaml")
            {
                continue;
            }

            var code = Path.Combine(
                dir,
                Path.GetFileNameWithoutExtension(name) + ".xaml.cs");
            yield return (
                Path.Combine("src", "HeliVMS.App", name).Replace('\\', '/'),
                File.Exists(code)
                    ? Path.Combine("src", "HeliVMS.App", Path.GetFileName(code)).Replace('\\', '/')
                    : string.Empty);
        }
    }

    /// <summary>移除 XML註解，避免註解裡的 XAML 片段被當成真的控制項。</summary>
    private static string WithoutComments(string xaml)
        => Regex.Replace(xaml, "<!--.*?-->", string.Empty, RegexOptions.Singleline);

    [Fact]
    public void 每個視窗都有對應的程式碼後置檔()
    {
        var missing = WindowSources()
            .Where(w => string.IsNullOrEmpty(w.Code))
            .Select(w => w.Xaml)
            .ToList();

        // 沒有 .xaml.cs 的視窗仍然合法（純 XAML 視窗），但這份專案每一個都有事件接線，
        // 所以出現這種組合就是接線無處可去。
        Assert.True(
            missing.Count == 0,
            $"這些 XAML 沒有對應的 .xaml.cs，事件接線會無處可去：{string.Join(", ", missing)}");
    }

    /// <summary>
    /// XAML 上接的每一個事件處理器，在 code-behind 裡都必須真的存在。
    /// </summary>
    /// <remarks>
    /// 這一條擋的就是「打字打錯的按鈕」：<c>Click="OnSaveCliked"</c>（Cliked）
    /// 會編譯成功、畫面正常，只有使用者按下去時才炸。
    /// </remarks>
    [Fact]
    public void XAML接線的事件處理器都存在於程式碼後置()
    {
        var dangling = new List<string>();

        foreach (var (xamlPath, codePath) in WindowSources())
        {
            var xaml = WithoutComments(SourceContract.Read(xamlPath));
            if (string.IsNullOrEmpty(codePath))
            {
                continue;
            }

            var code = SourceContract.Read(codePath);
            var handlers = EventAttributes
                .SelectMany(attr => Regex.Matches(xaml, $@"\b{attr}=""(?<h>\w+)""")
                    .Select(m => $"{attr}={m.Groups["h"].Value}"))
                .Distinct(StringComparer.Ordinal);

            foreach (var reference in handlers)
            {
                var name = reference[(reference.IndexOf('=') + 1)..];
                if (!Regex.IsMatch(code, $@"\b{Regex.Escape(name)}\s*\("))
                {
                    dangling.Add($"{Path.GetFileName(xamlPath)}: {reference}");
                }
            }
        }

        Assert.True(
            dangling.Count == 0,
            $"XAML 接到不存在的事件處理器（按下去才會在執行期爆掉）：{string.Join("; ", dangling)}");
    }

    /// <summary>
    /// 接了線的事件處理器不能是空的。
    /// </summary>
    /// <remarks>
    /// 空的 <c>Closing="OnClosing"</c> 搭配空的 <c>private void OnClosing() { }</c>
    /// 是一種特別惡劣的假整合：介面看起來有做清理生命週期，實際上什麼都沒做。
    /// 操作員在報告裡讀到「視窗關閉時有資源回收」是從程式碼看來的，不是從症狀看出來的——
    /// 而真正會漏的情況（例如背景工作沒取消）正好被這個空方法掩蓋。
    /// </remarks>
    [Fact]
    public void 事件處理器不得為空()
    {
        var empty = new List<string>();

        foreach (var (xamlPath, codePath) in WindowSources())
        {
            if (string.IsNullOrEmpty(codePath))
            {
                continue;
            }

            var xaml = WithoutComments(SourceContract.Read(xamlPath));
            var code = SourceContract.Read(codePath);
            var names = EventAttributes
                .SelectMany(attr => Regex.Matches(xaml, $@"\b{attr}=""(?<h>\w+)""")
                    .Select(m => m.Groups["h"].Value))
                .Distinct(StringComparer.Ordinal);

            foreach (var name in names)
            {
                var body = SourceContract.BodyOf(code, name);
                var meaningful = StripCommentsAndPunctuation(body);

                if (body.Length == 0)
                {
                    empty.Add($"{Path.GetFileName(xamlPath)}: {name} 找不到實作");
                }
                else if (meaningful.Length == 0)
                {
                    empty.Add($"{Path.GetFileName(xamlPath)}: {name} 本體是空的");
                }
            }
        }

        Assert.True(
            empty.Count == 0,
            $"事件處理器接了線卻什麼都沒做（假的整合）：{string.Join("; ", empty)}");
    }

    /// <summary>
    /// 每個 Button／MenuItem 都必須有 <c>Click</c>，不能只是畫在那裡。
    /// </summary>
    /// <remarks>
    /// CheckBox／RadioButton 不在這份契約內：它們沒有 Click 才是正常的，
    /// 狀態在存檔或切換時被讀取。它們由 <see cref="每個核取方塊都有被讀取"/> 把關。
    /// </remarks>
    [Fact]
    public void 每個按鈕與選單項目都有點擊處理器()
    {
        var dead = new List<string>();

        foreach (var (xamlPath, _) in WindowSources())
        {
            var xaml = WithoutComments(SourceContract.Read(xamlPath));
            foreach (Match tag in Regex.Matches(xaml, @"<(Button|MenuItem|ToggleButton|RadioButton)\b(?![.\w])[^>]*>"))
            {
                if (tag.Value.Contains("Click=", StringComparison.Ordinal))
                {
                    continue;
                }

                var name = Regex.Match(tag.Value, @"x:Name=""(?<n>[^""]+)""");
                var label = Regex.Match(tag.Value, @"Content=""(?<c>[^""]{0,30})""");
                var what = name.Success
                    ? name.Groups["n"].Value
                    : label.Success ? label.Groups["c"].Value : "<無名稱>";

                dead.Add($"{Path.GetFileName(xamlPath)}: {what}");
            }
        }

        Assert.True(
            dead.Count == 0,
            $"這些按鈕／選單項目沒有 Click 處理器，按下去不會有任何事：{string.Join("; ", dead)}");
    }

    /// <summary>
    /// 每個有 <c>x:Name</c> 的核取方塊都要在 code-behind 被讀取過。
    /// </summary>
    /// <remarks>
    /// 這是「整合了但沒接線」最容易發生、也最難察覺的形式：勾選方塊畫在設定頁上，
    /// 使用者勾了、按了儲存、介面回報「已儲存」，而那個值從來沒被讀取過。
    /// 現場症狀是設定改了沒有任何效果，而沒有任何錯誤——比按鈕壞掉更難查。
    /// </remarks>
    [Fact]
    public void 每個核取方塊都有被讀取()
    {
        var unread = new List<string>();

        foreach (var (xamlPath, codePath) in WindowSources())
        {
            if (string.IsNullOrEmpty(codePath))
            {
                continue;
            }

            var xaml = WithoutComments(SourceContract.Read(xamlPath));
            var code = SourceContract.Read(codePath);

            foreach (Match tag in Regex.Matches(xaml, @"<CheckBox\b[^>]*>"))
            {
                var name = Regex.Match(tag.Value, @"x:Name=""(?<n>[^""]+)""");
                if (!name.Success)
                {
                    continue;
                }

                var id = name.Groups["n"].Value;
                if (!Regex.IsMatch(code, $@"\b{Regex.Escape(id)}\b"))
                {
                    unread.Add($"{Path.GetFileName(xamlPath)}: {id}");
                }
            }
        }

        Assert.True(
            unread.Count == 0,
            $"這些核取方塊從未被讀取，勾了不會有任何效果：{string.Join("; ", unread)}");
    }

    /// <summary>
    /// 主視窗用程式碼建立的右鍵選單項目都必須接上處理器。
    /// </summary>
    /// <remarks>
    /// 右鍵選單是在 <c>BuildCells</c> 裡用 <c>AddMenuItem</c> 動態建的，
    /// XAML 掃不到，所以 <see cref="XAML接線的事件處理器都存在於程式碼後置"/> 抓不到它們。
    /// 少了這條，一個寫錯的右鍵選單處理器會完全沒有測試保護。
    /// </remarks>
    [Fact]
    public void 右鍵選單項目都有接線()
    {
        var code = SourceContract.Read("src/HeliVMS.App/MainWindow.xaml.cs");
        var wired = Regex.Matches(code, @"AddMenuItem\(\s*menu,\s*(?:""[^""]*""|Localizer\.T\(""[^""]+""\)),\s*(?<h>\w+)")
            .Select(m => m.Groups["h"].Value)
            .ToList();

        Assert.True(wired.Count >= 8, $"右鍵選單項目只有 {wired.Count} 個，少於預期的 8 個（掃描可能失效）");

        var missing = wired
            .Where(name => !Regex.IsMatch(code, $@"\b{Regex.Escape(name)}\s*\("))
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"右鍵選單接到不存在的處理器：{string.Join("; ", missing)}");

        // 每個項目都必須有實作，不能只是被列出來。
        var empty = wired
            .Where(name => IsEmptyBody(SourceContract.BodyOf(code, name)))
            .ToList();

        Assert.True(empty.Count == 0, $"右鍵選單項目的處理器是空的：{string.Join("; ", empty)}");
    }

    /// <summary>
    /// 子視窗「首次建立」時也要套用 focus，不能只在已開啟分支套用。
    /// </summary>
    /// <remarks>
    /// 事件中心的未確認徽章與通知紀錄雙擊沒有對應的建構子參數，初始狀態只能靠 focus 帶入。
    /// 先前 <c>OpenChild</c> 只在「已開啟」分支呼叫 focus，第一次點徽章時 focus 被丟掉，
    /// 畫面停在預設 24 小時範圍，徽章指涉的舊未確認事件看不到也清不掉（實際症狀：徽章卡住不降）。
    /// </remarks>
    [Fact]
    public void 子視窗首次建立也會套用focus()
    {
        var code = SourceContract.Read("src/HeliVMS.App/MainWindow.xaml.cs");
        var body = SourceContract.BodyOf(code, "OpenChild");
        Assert.NotEqual(string.Empty, body);

        Assert.Contains("focus?.Invoke(existing)", body);
        Assert.Matches(@"window\.Show\(\)\s*;?\s*focus\?\.Invoke\(window\)", body);
    }

    /// <summary>
    /// 監控中心頂欄的佈局下拉路數順序固定：1／4／6／9／12／16／20／25／30／36／42／56／64 路
    /// （能整齊貼合 16:9 畫面的路數併入既有的平方數並延伸到 64）；
    /// 最底另有「復原攝影機排序」項目（<c>LayoutItemUndo</c>），它不是一種佈局，不計入格數對應。
    /// </summary>
    /// <remarks>
    /// XAML 的 <c>ComboBoxItem</c> 順序、<c>LayoutCellCounts</c> 與 <c>MaxLayoutIndex</c> 三者必須一致，
    /// 否則下拉顯示的「第 N 項」與實際格數會對不上（例如點「16 路」卻套用 12 格）。
    /// 任何一方被單獨改動，這個契約都會亮紅燈。
    /// </remarks>
    [Fact]
    public void 佈局下拉路數順序固定且與格數對應一致()
    {
        var xaml = SourceContract.Read("src/HeliVMS.App/MainWindow.xaml");
        var items = Regex.Matches(xaml, @"x:Name=""(LayoutItem\w+)""")
            .Select(m => m.Groups[1].Value)
            .ToList();
        var expected = new[]
        {
            "LayoutItemSingle", "LayoutItem4", "LayoutItem6", "LayoutItem9", "LayoutItem12",
            "LayoutItem16", "LayoutItem20", "LayoutItem25", "LayoutItem30", "LayoutItem36",
            "LayoutItem42", "LayoutItem56", "LayoutItem64",
        };
        // 扣掉尾端的「復原」動作項目後，實際佈局選項必須與 LayoutCellCounts 一一對應；
        // 且復原項目必須排在最後（順序若顛倒，SelectedIndex 與格數的對應就會錯位）。
        Assert.Equal(expected, items.Where(i => i != "LayoutItemUndo").ToList());
        Assert.Equal("LayoutItemUndo", items[^1]);

        var code = SourceContract.Read("src/HeliVMS.App/MainWindow.xaml.cs");
        Assert.Contains("LayoutCellCounts = [1, 4, 6, 9, 12, 16, 20, 25, 30, 36, 42, 56, 64]", code);
        Assert.Contains("MaxLayoutIndex = 12", code);
    }

    /// <summary>
    /// 主視窗以真正的全螢幕（無邊框滿版）啟動，且可按鈕／Esc 退出回可調整大小的視窗。
    /// </summary>
    /// <remarks>
    /// 只設 <c>WindowState="Maximized"</c> 會留下標題列、工作列也露出來，看起來是「最大化」不是
    /// 「滿版」——正是操作員抱怨「開起來不是全畫面」的來源。少了 <c>WindowStyle="None"</c> +
    /// <c>ResizeMode="NoResize"</c> 這兩個屬性就會退回那個狀態，所以在這裡釘住。
    /// </remarks>
    [Fact]
    public void 主視窗以全螢幕啟動()
    {
        var xaml = SourceContract.Read("src/HeliVMS.App/MainWindow.xaml");
        Assert.Matches(@"WindowStyle=""None""", xaml);
        Assert.Matches(@"ResizeMode=""NoResize""", xaml);

        var code = SourceContract.Read("src/HeliVMS.App/MainWindow.xaml.cs");
        Assert.Contains("ExitFullScreenMenuItem", code);

        var body = SourceContract.BodyOf(code, "SetWindowFullScreen");
        Assert.NotEqual(string.Empty, body);
        Assert.Contains("WindowStyle.None", body);
        Assert.Contains("WindowState.Maximized", body);
    }

    /// <summary>
    /// 子視窗（含設定中心）關閉後，監控中心會回到前景，但<b>不置頂</b>。
    /// </summary>
    /// <remarks>
    /// 症狀是「進設定中心操作完關掉，找不到監控中心」：主視窗是無邊框全螢幕（<c>WindowStyle="None"</c>），
    /// 在子視窗關閉後不一定會被系統帶回前景，可能就沉到別的視窗後面或停在最小化（<c>OnStateChanged</c>
    /// 會把最小化的主視窗 <c>Hide()</c>）。因此 <c>OpenChild</c> 的關閉路徑要呼叫 <c>BringToFront</c>
    /// （<c>Show</c>／還原／<c>Activate</c>）。
    /// <para>
    /// 但依使用者要求<b>取消置頂</b>：先前 <c>Topmost="True"</c> 會讓監控中心一直蓋在設定中心等子視窗之上，
    /// 反而妨礙操作。因此 XAML 不再有 <c>Topmost</c>，<c>BringToFront</c> 也不再設 <c>Topmost = true</c>；
    /// 若有人把置頂加回來，這個契約就會亮紅燈。
    /// </para>
    /// </remarks>
    [Fact]
    public void 子視窗關閉後主視窗回到前景但不置頂()
    {
        var xaml = SourceContract.Read("src/HeliVMS.App/MainWindow.xaml");
        Assert.DoesNotMatch(@"Topmost=""True""", xaml);

        var code = SourceContract.Read("src/HeliVMS.App/MainWindow.xaml.cs");
        Assert.Contains("BringToFront", SourceContract.BodyOf(code, "OpenChild"));

        var body = SourceContract.BodyOf(code, "BringToFront");
        Assert.NotEqual(string.Empty, body);
        Assert.Contains("Activate()", body);
        Assert.DoesNotContain("Topmost", body);

        // 例外：重複啟動時允許「暫時」置頂把視窗推上來（第一個行程沒有前景權，
        // Activate() 可能被 Windows 拒絕），但一定要立刻取消。永久置頂仍然禁止。
        var force = SourceContract.BodyOf(code, "ForceForeground");
        Assert.False(string.IsNullOrWhiteSpace(force), "找不到 ForceForeground 的實作");
        Assert.Contains("Topmost = true", force);
        Assert.Contains("Topmost = false", force);
    }

    /// <summary>
    /// 主視窗應為滿版時，被 Windows 還原成一般大小會自行套回最大化（不會「自己縮小」）。
    /// </summary>
    /// <remarks>
    /// 症狀：進設定中心操作完、用滑鼠按 X 關閉後，監控中心縮成一個 1280×800 的一般視窗。
    /// 主視窗是無邊框＋<c>WindowState="Maximized"</c>，它的一般大小是 XAML 的 <c>Width/Height</c>；
    /// owned 子視窗關閉、主視窗被系統重新取得前景時，Windows 會依還原尺寸把它退回 <c>Normal</c>。
    /// 修正是在 <c>OnStateChanged</c> 內「應為滿版卻變成 Normal」時套回最大化，並讓
    /// <c>BringToFront</c> 最小化還原時回到最大化。少了任一段，這個縮小症狀就會復發。
    /// </remarks>
    [Fact]
    public void 主視窗滿版被系統還原成一般大小會自行套回最大化()
    {
        var code = SourceContract.Read("src/HeliVMS.App/MainWindow.xaml.cs");

        var state = SourceContract.BodyOf(code, "OnStateChanged");
        Assert.NotEqual(string.Empty, state);
        Assert.Contains("_isWindowFullScreen", state);
        Assert.Contains("WindowState.Normal", state);
        Assert.Contains("WindowState = WindowState.Maximized", state);

        var bring = SourceContract.BodyOf(code, "BringToFront");
        Assert.NotEqual(string.Empty, bring);
        Assert.Contains("_isWindowFullScreen ? WindowState.Maximized", bring);
    }

    /// <summary>
    /// 底欄狀態列有「目前日期時間」時鐘，且由每秒巡檢更新。
    /// </summary>
    /// <remarks>
    /// 監看畫面沒有 OSD、事件列也只有分秒，操作員對照即時畫面與事件時間時需要的「現在時間」
    /// 只能靠這個時鐘。若哪天它被拿掉或忘了在 <c>UpdateCellStatus</c> 內更新，
    /// 畫面會停在某個固定秒數而沒有錯誤——所以釘住「XAML 有控制項」＋「每秒路徑有更新它」。
    /// </remarks>
    [Fact]
    public void 底欄狀態列有目前日期時間時鐘()
    {
        var xaml = SourceContract.Read("src/HeliVMS.App/MainWindow.xaml");
        Assert.Contains("x:Name=\"ClockText\"", xaml);

        var code = SourceContract.Read("src/HeliVMS.App/MainWindow.xaml.cs");
        var tick = SourceContract.BodyOf(code, "UpdateCellStatus");
        Assert.Contains("UpdateClock()", tick);

        var clock = SourceContract.BodyOf(code, "UpdateClock");
        Assert.NotEqual(string.Empty, clock);
        Assert.Contains("DateTime.Now", clock);
    }

    /// <summary>
    /// 關閉主視窗（頂欄「離開」、Alt+F4、工作列）會先跳「最小化／離開」選擇，而非靜默收進系統匣。
    /// </summary>
    /// <remarks>
    /// 舊行為是關閉鍵直接收進系統匣：按了 X 的人以為程式結束了，其實還在背景錄影，想真正結束
    /// 又只能翻系統匣選單。這條釘住「關閉前一定經過選擇」——少了它，這個 UX 會退回靜默隱藏。
    /// </remarks>
    [Fact]
    public void 關閉主視窗會先詢問最小化或離開()
    {
        var code = SourceContract.Read("src/HeliVMS.App/MainWindow.xaml.cs");
        var body = SourceContract.BodyOf(code, "OnClosing");
        Assert.NotEqual(string.Empty, body);
        Assert.Contains("ExitChoiceWindow.Ask", body);
        Assert.Contains("ExitChoice.Minimize", body);
        Assert.Contains("ExitChoice.Quit", body);

        var xaml = SourceContract.Read("src/HeliVMS.App/MainWindow.xaml");
        Assert.Contains("OnExitClicked", xaml);

        var dialog = SourceContract.Read("src/HeliVMS.App/ExitChoiceWindow.xaml.cs");
        Assert.Contains("ExitChoice.Minimize", SourceContract.BodyOf(dialog, "OnMinimizeClicked"));
        Assert.Contains("ExitChoice.Quit", SourceContract.BodyOf(dialog, "OnQuitClicked"));
        Assert.Contains("ExitChoice.Cancel", SourceContract.BodyOf(dialog, "OnCancelClicked"));
    }

    /// <summary>
    /// 去掉註解與純排版字元，留下「有沒有真的做事」的判斷依據。
    /// </summary>
    /// <remarks>
    /// <c>//.*</c> 這裡<b>不能</b>加 <see cref="RegexOptions.Singleline"/>：
    /// Singleline 會讓 <c>.</c> 也吃掉換行，於是第一個行註解就把整個方法本體
    /// （到檔案結尾）清成空字串，於是所有「第一行是註解」的方法都被誤判成空實作——
    /// 這個專案的每個事件處理器第一行都是註解，所以整條契約會全滅。
    /// 行註解與區塊註解必須分兩次替換。
    /// </remarks>
    private static string StripCommentsAndPunctuation(string body)
    {
        var stripped = Regex.Replace(body, @"//[^\r\n]*", string.Empty);
        stripped = Regex.Replace(stripped, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(stripped, @"[\s{};]", string.Empty);
    }

    private static bool IsEmptyBody(string body)
    {
        var meaningful = StripCommentsAndPunctuation(body);
        return body.Length == 0 || meaningful.Length == 0;
    }
}
