namespace HeliVMS.App.Tests;

/// <summary>
/// 第二螢幕鏡像牆（QC_Live 的多螢幕監看對齊）的接線契約。
/// 釘五件事：鏡像窗<b>不另開解碼、不重連</b>（幀從主牆的 RenderCellFrame 轉發——
/// 重連是 F11 全黑的舊傷，任何人在鏡像路徑上叫 ConnectAsync 都要被擋）、
/// 關窗必須解除轉發（否則每開一次留一個 幀接收者）、關窗必須取消主視窗的勾選
/// （三條關窗路徑——✕、Esc、螢幕被拔——都不走 toggle 分支，勾選會一直說謊）、
/// 格數跟著頁簽重設且切分割後仍有畫面、邊框窗自帶出口（✕＋Esc，與全 App 的無框窗同一規則）。
/// </summary>
public sealed class WallMirrorContractTests
{
    private const string MirrorCode = "src/HeliVMS.App/WallMirrorWindow.xaml.cs";
    private const string MirrorXaml = "src/HeliVMS.App/WallMirrorWindow.xaml";
    private const string MainCode = "src/HeliVMS.App/MainWindow.xaml.cs";

    [Fact]
    public void 鏡像窗自帶出口且有分割按鈕()
    {
        var xaml = SourceContract.Read(MirrorXaml);
        var source = SourceContract.Read(MirrorCode);

        Assert.Contains("WindowStyle=\"None\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"MirrorGrid\"", xaml);
        Assert.Matches(
            @"<Button[^>]*x:Name=""CloseButton""[^>]*Click=""OnCloseClicked""",
            xaml);

        // 分割鈕是第二螢幕唯一的控制項：鏡像窗拿掉標題列之後，操作員只剩它與 ✕。
        Assert.Matches(
            @"<Button[^>]*x:Name=""SplitButton""[^>]*Click=""OnSplitClicked""",
            xaml);
        Assert.Contains("Main.MirrorSplitTip", SourceContract.BodyOf(source, "ApplyI18n"));

        Assert.Contains("Key.Escape", SourceContract.BodyOf(source, "OnWindowKeyDown"));
    }

    [Fact]
    public void 幀從主牆轉發且鏡像不開新解碼()
    {
        var main = SourceContract.Read(MainCode);
        var onFrame = SourceContract.BodyOf(main, "OnCellFrame");

        // 轉發點只有一個，而且在「格位過濾」之前：鏡像牆可以顯示不在主牆格位上的頻道
        // （自己的群組），那些影格的 Cell 是 -1，先過濾就永遠送不到。
        Assert.Contains("mirror.PostFrame(channelId, frame)", onFrame);
        Assert.True(
            onFrame.IndexOf("mirror.PostFrame(channelId, frame)", StringComparison.Ordinal) <
            onFrame.IndexOf("cell is < 0 or >= MaxCells", StringComparison.Ordinal),
            "影格轉發必須排在格位過濾之前，否則自訂來源（不在主牆格位上）永遠是黑的");

        Assert.DoesNotContain("mirror.", SourceContract.BodyOf(main, "RenderCellFrame"));

        var mirror = SourceContract.Read(MirrorCode);
        // 鏡像窗只寫自己那張 WriteableBitmap，絕不碰 RTSP/ConnectAsync，也不自己查資料庫。
        Assert.Contains("public void PostFrame(int channelId, VideoFrame frame)", mirror);
        Assert.Contains("public bool WantsChannel(int channelId)", mirror);
        Assert.DoesNotContain("ConnectAsync", mirror);
        Assert.DoesNotContain("RtspClient", mirror);
        Assert.DoesNotContain("ChannelGroupRepository", mirror);

        // WriteableBitmap 只能在 UI 執行緒寫：解碼執行緒進來的 PostFrame 只放「待畫影格」＋排程，
        // 真正的 WritePixels 在 RenderCell（UI 執行緒）。寫錯邊的症狀是擲回
        // 「The calling thread cannot access this object」，或是偶發的畫面撕裂。
        Assert.DoesNotContain("WritePixels", SourceContract.BodyOf(mirror, "PostFrame"));
        Assert.Contains("WritePixels", SourceContract.BodyOf(mirror, "RenderCell"));

        // 同一台若佔了兩格（放大某個小群組時會發生），兩格都要更新：
        // 提早結束只會讓後面那格永遠停在舊畫面，而且沒有任何錯誤訊息。
        Assert.Contains("cell.Pending = frame", SourceContract.BodyOf(mirror, "PostFrame"));
        Assert.Contains("continue;", SourceContract.BodyOf(mirror, "PostFrame"));
    }

    [Fact]
    public void 鏡像有自己的頻道來源且與主牆共用同一份清單口徑()
    {
        var main = SourceContract.Read(MainCode);

        // 來源＝跟隨主牆頁簽或某個監看群組；格位與徽章都必須走 MirrorChannels，
        // 兩邊各查一次遲早會出現「畫面是 A 組、徽章是 B 組」。
        var mirrorChannels = SourceContract.BodyOf(main, "MirrorChannels");
        Assert.Contains("_mirror?.SourceGroupId", mirrorChannels);
        Assert.Contains("_groups.ListChannelIds(groupId)", mirrorChannels);
        Assert.Contains("WallChannels()", mirrorChannels);

        Assert.Contains("mirror.SetChannels(MirrorChannels())", SourceContract.BodyOf(main, "OnMirrorToggleClicked"));
        Assert.Contains("var channels = MirrorChannels();", SourceContract.BodyOf(main, "PushMirrorBadges"));

        // 來源與格數一樣：改動當下寫回設定，不等關窗（關窗有自己的收尾路徑）。
        Assert.Contains("wall.mirror.group", main);
        Assert.Contains("settings.Set(", SourceContract.BodyOf(main, "OnMirrorToggleClicked"));

        // 群組被刪除／改名後要重送來源清單，否則來源鈕顯示一個不存在的名字、也切不回來。
        Assert.Contains("_mirror?.SetSources(MirrorSources(), _mirror.SourceGroupId)", SourceContract.BodyOf(main, "ReloadGroupTabs"));

        // 鏡像窗只知道來源清單與目前選哪一個（顯示層），群組的查詢留在主視窗。
        var mirror = SourceContract.Read(MirrorCode);
        Assert.Contains("public void SetSources(IReadOnlyList<MirrorSource> sources, long? current)", mirror);
        Assert.Contains("public event EventHandler? SourceChanged", mirror);
        Assert.Matches(@"<Button[^>]*x:Name=""SourceButton""[^>]*Click=""OnSourceClicked""", SourceContract.Read(MirrorXaml));
        Assert.Contains("Main.MirrorSourceTip", SourceContract.BodyOf(mirror, "ApplyI18n"));
    }

    [Fact]
    public void 關窗解除轉發且頁簽切換重設格位()
    {
        var main = SourceContract.Read(MainCode);

        var toggle = SourceContract.BodyOf(main, "OnMirrorToggleClicked");
        Assert.Contains("mirror.Shutdown()", toggle);
        Assert.Contains("mirror.SetChannels(MirrorChannels())", toggle);

        var rebuild = SourceContract.BodyOf(main, "RebuildCells");
        Assert.Contains("_mirror?.SetChannels(MirrorChannels())", rebuild);
    }

    [Fact]
    public void 關閉鏡像窗會取消主視窗的勾選()
    {
        // 現場症狀：按 ✕ 或拔掉第二螢幕後窗已關，按鈕仍顯示「開著」，再按一次才開得起來
        // ——操作員的結論是「這顆按鈕壞了」。三條關窗路徑都不經過 toggle 分支，所以只能
        // 在 Closed 補這一行。
        var toggle = SourceContract.BodyOf(SourceContract.Read(MainCode), "OnMirrorToggleClicked");
        Assert.Contains("MirrorToggle.IsChecked = false", toggle);
    }

    [Fact]
    public void 分割跟著設定記住且切換後仍對得上頻道()
    {
        var main = SourceContract.Read(MainCode);
        var toggle = SourceContract.BodyOf(main, "OnMirrorToggleClicked");

        // 格數記在 wall.mirror.split；開窗與之後的每次切換都寫回，不等關窗才存。
        Assert.Contains("wall.mirror.split", main);
        Assert.Contains("MirrorWallBudget.Resolve(settings.Get(MirrorSplitKey))", toggle);
        Assert.Contains("mirror.SplitChanged", toggle);

        var mirror = SourceContract.Read(MirrorCode);
        var setSplit = SourceContract.BodyOf(mirror, "SetSplit");

        // 切分割必須重建格位後立刻用最後一次清單重新對位，否則要等下次切頁簽才有畫面。
        Assert.Contains("ApplyChannels(_lastChannels)", setSplit);
        Assert.Contains("MirrorWallBudget.Clamp(cells)", setSplit);
        Assert.Contains("MirrorWallBudget.GridOf", setSplit);
    }

    [Fact]
    public void 鏡像牆的徽章與指示燈由主牆推來而不是自己算()
    {
        // 兩面牆對同一台給出不同答案時，操作員唯一能做的就是兩邊都不信。所以鏡像端
        // 不碰錄影方式、不碰阻塞原因、不碰色票——那些只有一份（主牆的 UpdateBadges）。
        var mirror = SourceContract.Read(MirrorCode);
        Assert.DoesNotContain("WallModeDotPalette", mirror);
        Assert.DoesNotContain("WallBadgeTextPlanner", mirror);
        Assert.DoesNotContain("RecordingMode", mirror);

        var main = SourceContract.Read(MainCode);
        var push = SourceContract.BodyOf(main, "PushMirrorBadges");
        Assert.Contains("_mirror.ApplyBadges(badges)", push);
        Assert.Contains("WallBadgeTextPlanner.Compose", push);
        Assert.Contains("ModeBrush(mode)", push);
        Assert.Contains("WallModeDotPalette.OpacityFor(recording)", push);

        // 同一個一秒節奏（UpdateBadges 的尾巴），不另開計時器。
        Assert.Contains("PushMirrorBadges()", SourceContract.BodyOf(main, "UpdateBadges"));

        // 開窗與切分割都要當下補一次，否則新格位要等下一秒才有徽章。
        Assert.Contains("PushMirrorBadges()", SourceContract.BodyOf(main, "OnMirrorToggleClicked"));

        // 鏡像端只做顯示：時刻由共用組字器接上（徽章字串自己不含時刻）。
        Assert.Contains("WallMirrorLabel.Compose", SourceContract.BodyOf(mirror, "ApplyBadges"));
    }

    [Fact]
    public void 第二螢幕被拔掉時關窗且會退訂系統事件()
    {
        var mirror = SourceContract.Read(MirrorCode);

        // 訂閱在 OnSourceInitialized、退訂在 OnClosed：SystemEvents 是行程層級靜態事件，
        // 漏退訂＝每開一次窗多一個永久訂閱者，關窗後還對著已釋放的 Dispatcher 排工作。
        Assert.Contains("SystemEvents.DisplaySettingsChanged +=", SourceContract.BodyOf(mirror, "OnSourceInitialized"));
        Assert.Contains("SystemEvents.DisplaySettingsChanged -=", SourceContract.BodyOf(mirror, "OnClosed"));

        // 一個非主螢幕都不剩就關窗，不縮成主螢幕上的小窗（那會蓋住主牆，
        // 看起來像主畫面多了塊奇怪的縮圖）。
        var place = SourceContract.BodyOf(mirror, "PlaceOnSecondary");
        Assert.Contains("closeWhenNoSecondary", place);
        Assert.Contains("Close()", place);

        // 事件可能從系統執行緒送來，必須排回 UI 執行緒才動視窗屬性。
        Assert.Contains("Dispatcher.BeginInvoke", SourceContract.BodyOf(mirror, "OnDisplaySettingsChanged"));
    }
}
