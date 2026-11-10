namespace HeliVMS.App.Tests;

/// <summary>
/// 第二螢幕鏡像牆（QC_Live 的多螢幕監看對齊，MVP）的接線契約。
/// 釘四件事：鏡像窗<b>不另開解碼、不重連</b>（幀從主牆的 RenderCellFrame 轉發——
/// 重連是 F11 全黑的舊傷，任何人在鏡像路徑上叫 ConnectAsync 都要被擋）、
/// 關窗必須解除轉發（否則每開一次留一個 幀接收者）、格位跟著頁簽重設、
/// 邊框窗自帶出口（✕＋Esc，與全 App 的無框窗同一規則）。
/// </summary>
public sealed class WallMirrorContractTests
{
    private const string MirrorCode = "src/HeliVMS.App/WallMirrorWindow.xaml.cs";
    private const string MirrorXaml = "src/HeliVMS.App/WallMirrorWindow.xaml";
    private const string MainCode = "src/HeliVMS.App/MainWindow.xaml.cs";

    [Fact]
    public void 鏡像窗自帶出口且格位固定四格()
    {
        var xaml = SourceContract.Read(MirrorXaml);
        var source = SourceContract.Read(MirrorCode);

        Assert.Contains("WindowStyle=\"None\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"MirrorGrid\"", xaml);
        Assert.Matches(
            @"<Button[^>]*x:Name=""CloseButton""[^>]*Click=""OnCloseClicked""",
            xaml);
        Assert.Contains("Key.Escape", SourceContract.BodyOf(source, "OnWindowKeyDown"));
        Assert.Equal("public const int CellCount = 4;", source.Trim().Contains("public const int CellCount = 4;")
            ? "public const int CellCount = 4;" : string.Empty);
    }

    [Fact]
    public void 幀從主牆轉發且鏡像不開新解碼()
    {
        var main = SourceContract.Read(MainCode);
        var render = SourceContract.BodyOf(main, "RenderCellFrame");
        Assert.Contains("mirror.RenderFrame(mirrorChannel, frame)", render);

        var mirror = SourceContract.Read(MirrorCode);
        // 鏡像窗自己 WritePixels（同一份已解碼影格），絕不碰 RTSP/ConnectAsync。
        Assert.Contains("public void RenderFrame(int channelId, VideoFrame frame)", mirror);
        Assert.DoesNotContain("ConnectAsync", mirror);
        Assert.DoesNotContain("RtspClient", mirror);
    }

    [Fact]
    public void 關窗解除轉發且頁簽切換重設格位()
    {
        var main = SourceContract.Read(MainCode);

        var toggle = SourceContract.BodyOf(main, "OnMirrorToggleClicked");
        Assert.Contains("mirror.Shutdown()", toggle);
        Assert.Contains("mirror.SetChannels(WallChannels())", toggle);

        var rebuild = SourceContract.BodyOf(main, "RebuildCells");
        Assert.Contains("_mirror?.SetChannels(WallChannels())", rebuild);
    }
}
