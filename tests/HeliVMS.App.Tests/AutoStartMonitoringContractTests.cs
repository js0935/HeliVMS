namespace HeliVMS.App.Tests;

/// <summary>
/// 監控中心開機自動啟動契約（M252）。
/// </summary>
/// <remarks>
/// <para>
/// 桌面端沒有 WPF 執行期測試。監控中心已移除手動「連線」「錄影」按鈕，改由 <c>OnLoaded</c>
/// 觸發 <c>AutoStartMonitoringAsync</c>：連線全部已設定頻道並叫錄影排程器立即對帳全時錄影。
/// 若這條接線斷了，開機後畫面會全黑、也不會錄影，而 log 乾淨得像正常啟動——正是最難從
/// 測試以外發現的那種回歸，所以用來源契約把它釘住。
/// </para>
/// <para>
/// 授權與排程閘門仍留在 <c>RecordingScheduler</c> 內，本方法只是提早觸發第一次對帳，
/// 不繞過任何檢查。
/// </para>
/// </remarks>
public sealed class AutoStartMonitoringContractTests
{
    private const string MainWindow = "src/HeliVMS.App/MainWindow.xaml.cs";

    [Fact]
    public void 主視窗載入時會啟動自動連線與錄影()
    {
        var onLoaded = SourceContract.BodyOf(SourceContract.Read(MainWindow), "OnLoaded");
        Assert.NotEqual(string.Empty, onLoaded);
        Assert.Contains("AutoStartMonitoringAsync();", onLoaded);
    }

    [Fact]
    public void 自動啟動會連線全部頻道並立即對帳錄影排程()
    {
        var body = SourceContract.BodyOf(SourceContract.Read(MainWindow), "AutoStartMonitoringAsync");
        Assert.NotEqual(string.Empty, body);
        Assert.Contains("_manager.ConnectAsync", body);
        Assert.Contains("_channelList.Count", body);
        Assert.Contains("ReconcileAsync", body);
    }

    [Fact]
    public void 沒有已設定頻道時不會嘗試連線()
    {
        var body = SourceContract.BodyOf(SourceContract.Read("src/HeliVMS.App/MainWindow.xaml.cs"), "AutoStartMonitoringAsync");
        Assert.NotEqual(string.Empty, body);
        Assert.Contains("_channelList.Count == 0", body);
    }

    /// <summary>
    /// 子視窗關閉後，頻道清單若有變動就重載並重新連線（新頻道立即出現，不必重啟程式）。
    /// </summary>
    /// <remarks>
    /// 症狀：在設定中心→攝影機／頻道管理新增頻道後，監看中心一直停在「未連線」，要把整個
    /// 程式關掉重開才看得到。原因是頻道在子視窗內建立，子視窗關閉時主視窗只更新了清單卻
    /// 沒有重新連線。修正：<c>OpenChild</c> 的 <c>Closed</c> 改呼叫 <c>ReloadChannelsIfChangedAsync</c>，
    /// 以清單簽章比對，只有真的新增／刪除／修改才重連（避免關閉事件中心等無關視窗也重連，
    /// 造成錄影短暫中斷）。少了這條接線，這個症狀就會復發。
    /// </remarks>
    [Fact]
    public void 子視窗關閉後頻道有變動會自動重新連線()
    {
        var code = SourceContract.Read("src/HeliVMS.App/MainWindow.xaml.cs");

        var openChild = SourceContract.BodyOf(code, "OpenChild");
        Assert.NotEqual(string.Empty, openChild);
        Assert.Contains("ReloadChannelsIfChangedAsync", openChild);

        var reload = SourceContract.BodyOf(code, "ReloadChannelsIfChangedAsync");
        Assert.NotEqual(string.Empty, reload);
        Assert.Contains("RefreshChannelList", reload);
        Assert.Contains("ChannelSignature", reload);
        Assert.Contains("AutoStartMonitoringAsync", reload);
    }

    /// <summary>刪除頻道後不會殘留已刪除攝影機的畫面。</summary>
    /// <remarks>
    /// 症狀：在頻道管理刪除一台攝影機後，監控中心該格仍顯示它最後一張影格（像還在連）。
    /// 重新連線只對「還存在的頻道」重建工作階段與重畫格位，多出來的格子不會被重畫，
    /// 最後一張影格就凍在 <c>Image.Source</c> 上，因此必須明確清除。
    /// </remarks>
    [Fact]
    public void 頻道刪除後不會殘留已刪除攝影機的畫面()
    {
        var code = SourceContract.Read(MainWindow);

        var reload = SourceContract.BodyOf(code, "ReloadChannelsIfChangedAsync");
        Assert.NotEqual(string.Empty, reload);
        Assert.Contains("ClearCellsBeyondChannels();", reload);

        var clear = SourceContract.BodyOf(code, "ClearCellsBeyondChannels");
        Assert.NotEqual(string.Empty, clear);
        // 以目前頻道數為界，清掉其後的格位（頻道刪光時即全部）。
        Assert.Contains("_channelList.Count", clear);
        Assert.Contains("ApplyCellState(", clear);
        Assert.Contains("null", clear);
    }

    /// <summary>沒有即時影格時，依「未指派」或「已指派但斷線」顯示不同佔位圖。</summary>
    /// <remarks>
    /// 症狀：監看牆上「這格還沒配頻道」和「配了但攝影機掛了」原本都是同樣的黑底加「未連線」，
    /// 操作員只能逐格讀文字。改為未指派顯示 <c>Disable.png</c>、已指派但沒有畫面顯示
    /// <c>Invisible.png</c>；斷線時還必須清掉最後一張影格，否則佔位圖根本不會出現。
    /// </remarks>
    [Fact]
    public void 沒有畫面時會依未指派或斷線顯示不同佔位圖()
    {
        var code = SourceContract.Read(MainWindow);

        var placeholder = SourceContract.BodyOf(code, "ShowCellPlaceholder");
        Assert.NotEqual(string.Empty, placeholder);
        // 未指派頻道（_cellChannel 為 null）＝Disable；已指派但無影格＝Invisible。
        Assert.Contains("_cellChannel[cell] is null ? NoChannelImage() : DisconnectedImage()", placeholder);
        Assert.Contains("_bitmap[cell]", placeholder);

        // 兩種佔位圖各自載入對應的 PNG。
        Assert.Contains("LoadPlaceholder(\"Disable.png\")", code);
        Assert.Contains("LoadPlaceholder(\"Invisible.png\")", code);

        // 非即時狀態要清掉最後一張影格，否則畫面凍在斷線前那一刻、佔位圖不會出現。
        var apply = SourceContract.BodyOf(code, "ApplyCellState");
        Assert.NotEqual(string.Empty, apply);
        Assert.Contains("ShowCellPlaceholder(cell)", apply);
        Assert.Contains("state != RtspState.Streaming", apply);
        Assert.Contains("_bitmap[cell] = null", apply);
        // 未指派頻道的空格不出「未連線」文字（Disable 佔位圖已說明空格）。
        Assert.Contains("_cellChannel[cell] is null", apply);
        Assert.Contains("SetCellStatus(cell, string.Empty", apply);
    }

    [Fact]
    public void 佔位圖會隨建置複製到輸出()
    {
        // 少了這兩筆 Content，本機執行時 CreateBitmap 找不到檔（佔位圖消失），
        // 而且因為是「檔案不存在」而非編譯錯誤，只會在畫面上看得出來。
        var csproj = SourceContract.Read("src/HeliVMS.App/HeliVMS.App.csproj");
        Assert.Contains(@"Assets\Disable.png", csproj);
        Assert.Contains(@"Assets\Invisible.png", csproj);
    }

    /// <summary>監控中心載入時只把「仍沿用裝置名稱」的頻道補成格位預設名，其餘名稱不動。</summary>
    /// <remarks>
    /// 症狀：ONVIF 精靈等來源帶進來的是裝置名稱，監看牆上滿是認不出機位的名字。只補「從未命名過、
    /// 名稱仍等於裝置名稱」的頻道，補過一次就屬於該攝影機。刻意不把既有的「攝影機NN」視為可自動命名：
    /// 否則拖曳調好順序後，下次載入又依格位重新編號，把操作員排好的順序改回去。
    /// </remarks>
    [Fact]
    public void 監控中心載入時只把沿用的裝置名稱補成格位預設名()
    {
        var code = SourceContract.Read(MainWindow);

        var refresh = SourceContract.BodyOf(code, "RefreshChannelList");
        Assert.NotEqual(string.Empty, refresh);
        Assert.Contains("NormalizeDefaultChannelNames", refresh);

        var normalize = SourceContract.BodyOf(code, "NormalizeDefaultChannelNames");
        Assert.NotEqual(string.Empty, normalize);
        Assert.Contains("ChannelNaming.ReassignDefaultNames", normalize);
        Assert.Contains("IsDefaultChannelName", normalize);
        Assert.Contains("RenameChannel", normalize);

        // 只有「名稱仍等於裝置名稱」才算自動命名；既有的「攝影機NN」不可再被重新編號。
        var isDefault = SourceContract.BodyOf(code, "IsDefaultChannelName");
        Assert.NotEqual(string.Empty, isDefault);
        Assert.Contains("ChannelNaming.CarriesDeviceName", isDefault);
        Assert.Contains("DeviceId", isDefault);
        Assert.DoesNotContain("MatchesDefault", isDefault);

        // 改名要真的寫回資料庫。
        var rename = SourceContract.BodyOf(code, "RenameChannel");
        Assert.NotEqual(string.Empty, rename);
        Assert.Contains("_channels", rename);
        Assert.Contains("Update", rename);
    }

    /// <summary>格位改派給別的頻道時，先把上一台留下的畫面清掉。</summary>
    /// <remarks>
    /// 新頻道的第一張影格可能數百毫秒後才到，甚至連不上；不清掉舊畫面時，
    /// 監看牆會停在被刪除／被換掉那台的影像，操作員看不出畫面對應到哪一台。
    /// </remarks>
    [Fact]
    public void 格位改派給別的頻道時會先清掉上一台的畫面()
    {
        var body = SourceContract.BodyOf(SourceContract.Read(MainWindow), "OnCellMappingChanged");
        Assert.NotEqual(string.Empty, body);
        Assert.Contains("_bitmap[cell] = null", body);
        Assert.Contains("SetCellSource(cell, null)", body);
    }

    /// <summary>改派格位後既有工作階段不會再補送狀態，收到影格時必須把卡住的「未連線」補回。</summary>
    /// <remarks>
    /// 症狀：把非第一格的攝影機放大（或拖曳換位）會改派格位，管理器重用既有 session、不再發
    /// 狀態事件，<c>OnCellMappingChanged</c> 把 <c>_cellStates</c> 設成 Stopped 後就沒人改回來。
    /// 狀態文字只在 Streaming 時更新，於是畫面明明在動卻一直寫「未連線」，log 乾淨得像正常。
    /// 收到影格＝此刻真的有畫面，是狀態的地面真相，必須以此把狀態補回。
    /// </remarks>
    [Fact]
    public void 改派格位後收到影格會把未連線的狀態補回串流中()
    {
        var body = SourceContract.BodyOf(SourceContract.Read(MainWindow), "RenderCellFrame");
        Assert.NotEqual(string.Empty, body);

        // 收到影格＝地面真相：不是 Streaming 就補成 Streaming，否則文字永遠卡在「未連線」。
        Assert.Contains("_cellStates[cell] = RtspState.Streaming", body);
        // 文字更新不可再只限 Streaming，那正是卡住不動的原因。
        Assert.DoesNotContain("if (_cellStates[cell] == RtspState.Streaming)", body);
        Assert.Contains("RenderCellStatusText(cell)", body);
    }

    /// <summary>按住畫面左鍵拖到別格，放開即調整攝影機順序。</summary>
    /// <remarks>
    /// 症狀：使用者要把常看的攝影機排到第一格，卻只能進設定中心改「指定格位」再等全部重連。
    /// 少了任一滑鼠手勢的接線，拖曳會完全沒反應且沒有任何錯誤訊息——只能靠人眼發現。
    /// </remarks>
    [Fact]
    public void 按住畫面拖曳到別格會調整攝影機順序()
    {
        var code = SourceContract.Read(MainWindow);

        // 每格都要接上左鍵按下／移動／放開三個手勢（比對呼叫語法，註解提到方法名不算數）。
        var rebuild = SourceContract.BodyOf(code, "RebuildCells");
        Assert.NotEqual(string.Empty, rebuild);
        Assert.Contains("OnCellMouseDown(cellIndex", rebuild);
        Assert.Contains("OnCellMouseMove(cellIndex", rebuild);
        Assert.Contains("OnCellMouseUp(cellIndex", rebuild);

        // 放開到別格＝搬移：重排、把新格位寫回資料庫、再重讀（名稱跟著格位）。
        var reorder = SourceContract.BodyOf(code, "ReorderCells");
        Assert.NotEqual(string.Empty, reorder);
        Assert.Contains("ChannelLayout.Move", reorder);
        Assert.Contains("UpdateChannelDisplayCell", reorder);
        Assert.Contains("ApplyCellOrderAsync", reorder);

        // 單純點擊（沒有拖曳）仍要走原本的選取／放大，不能被拖曳搶走。
        var up = SourceContract.BodyOf(code, "OnCellMouseUp");
        Assert.NotEqual(string.Empty, up);
        Assert.Contains("OnCellClicked", up);

        // 拖曳後只改派既有 session 的格位；用 ConnectAsync 會拆掉再重建，等於中斷錄影。
        var apply = SourceContract.BodyOf(code, "ApplyCellOrderAsync");
        Assert.NotEqual(string.Empty, apply);
        Assert.Contains("ReconcileAsync", apply);
        Assert.DoesNotContain("ConnectAsync", apply);
    }

    /// <summary>拖曳調順序後可按「復原」退回上一個排序。</summary>
    /// <remarks>
    /// 症狀：操作員拖錯一格只能再憑記憶拖回去，沒有「原本長怎樣」可對照，越補越亂。
    /// 復原若只改畫面、沒把格位寫回資料庫，重開程式又會跑掉；若用 ConnectAsync 重新連線
    /// 則會中斷錄影。所以它必須寫回 display_cell、重讀清單，再用 ReconcileAsync 改派。
    /// </remarks>
    [Fact]
    public void 復原會還原上一次拖曳調整的攝影機順序()
    {
        var xaml = SourceContract.Read("src/HeliVMS.App/MainWindow.xaml");
        Assert.Contains("x:Name=\"LayoutItemUndo\"", xaml);

        var code = SourceContract.Read(MainWindow);

        // 調整前先留下快照，復原才有東西可回。
        var reorder = SourceContract.BodyOf(code, "ReorderCells");
        Assert.NotEqual(string.Empty, reorder);
        Assert.Contains("_cellOrderHistory.Push(CaptureCellOrder())", reorder);

        // 復原：把快照格位寫回資料庫、重讀清單，再改派既有 session（不重連）。
        var undo = SourceContract.BodyOf(code, "UndoCellOrder");
        Assert.NotEqual(string.Empty, undo);
        Assert.Contains("_cellOrderHistory.Pop()", undo);
        Assert.Contains("UpdateChannelDisplayCell(", undo);
        Assert.Contains("RefreshChannelList()", undo);
        Assert.Contains("ApplyCellOrderAsync()", undo);
        Assert.DoesNotContain("ConnectAsync", undo);

        // 「復原」是佈局選單裡的項目、不是一種佈局：選到它時要先把下拉還原成上一個佈局索引。
        var layout = SourceContract.BodyOf(code, "OnLayoutChanged");
        Assert.NotEqual(string.Empty, layout);
        Assert.Contains("ReferenceEquals(LayoutCombo.SelectedItem, LayoutItemUndo)", layout);
        Assert.Contains("UndoCellOrder()", layout);
    }

    /// <summary>佈局記憶以格數還原，選項清單增減（含中間插入）不會改變已記下的分割。</summary>
    /// <remarks>
    /// 症狀：佈局選單為了貼合 16:9 在 4 與 9 之間插入了 6（並一路加到 64），若記憶仍存「索引」，
    /// 同一個索引會因前面多插了選項而對應到別的分割——記下 36 分割（舊索引 5）重開變成 16 分割。
    /// 索引位移不會報錯、log 也乾淨，只能靠操作員發現排列變了。故改存格數，還原時對到最接近的格數；
    /// 舊檔只有索引時才退回以索引解讀。
    /// </remarks>
    [Fact]
    public void 佈局記憶以格數還原所以插入選項不會改變已記下的分割()
    {
        var code = SourceContract.Read(MainWindow);

        // 存檔要寫格數，不能只寫索引。
        var save = SourceContract.BodyOf(code, "SaveLayoutPreference");
        Assert.NotEqual(string.Empty, save);
        Assert.Contains("cells = CurrentCellCount()", save);

        // 還原優先用格數對到最接近的選項；沒有格數（舊檔）才退回索引。
        var apply = SourceContract.BodyOf(code, "ApplyLayoutPreference");
        Assert.NotEqual(string.Empty, apply);
        Assert.Contains("LayoutIndexForCells", apply);
        Assert.Contains("TryGetProperty(\"cells\"", apply);
        Assert.Contains("TryGetProperty(\"layout\"", apply);

        // 對應邏輯要以格數差距挑最接近者，否則插入選項後仍會挑錯。
        var pick = SourceContract.BodyOf(code, "LayoutIndexForCells");
        Assert.NotEqual(string.Empty, pick);
        Assert.Contains("LayoutCellCounts", pick);
        Assert.Contains("Math.Abs", pick);
    }
}
