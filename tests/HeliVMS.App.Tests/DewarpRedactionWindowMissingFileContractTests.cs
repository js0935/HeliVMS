namespace HeliVMS.App.Tests;

/// <summary>
/// 矯正窗與遮蔽窗「預設範圍／預覽」都要以磁碟上還存在的段為準。
///
/// <para>
/// 症狀：使用者把錄影從磁碟刪掉之後，索引要到下一輪對帳才會標成 missing。在那之前打開矯正／
/// 遮蔽窗，預設頻道與預設時間範圍是從 <c>ListFinal</c> 挑出來的——第一個 final 段可能就是
/// 已經被刪掉的那個，於是預覽一按就失敗，或預設範圍整段落空。兩窗的預覽也必須走
/// <c>ListPlayableByRange</c> 並剔除缺檔，而不是把不存在的路徑交給 ffmpeg。
/// </para>
/// </summary>
public sealed class DewarpRedactionWindowMissingFileContractTests
{
    private const string DewarpCode = "src/HeliVMS.App/DewarpWindow.xaml.cs";
    private const string RedactionCode = "src/HeliVMS.App/RedactionWindow.xaml.cs";

    [Theory]
    [InlineData(DewarpCode)]
    [InlineData(RedactionCode)]
    public void 預設頻道與範圍排除不存在於磁碟的段(string path)
    {
        var source = SourceContract.Read(path);

        // 預設頻道挑選與預設範圍都要先過 File.Exists，否則會挑到已刪除的段。
        Assert.Contains("File.Exists(s.FilePath)", source);
    }

    [Fact]
    public void 矯正預覽走可播放範圍而非全部狀態()
    {
        var source = SourceContract.Read(DewarpCode);
        var handler = SourceContract.BodyOf(source, "OnPreviewClicked");

        Assert.Contains("ListPlayableByRange", handler);
        Assert.Contains("File.Exists", handler);
        Assert.DoesNotContain("ListByRange(", handler);
    }
}
