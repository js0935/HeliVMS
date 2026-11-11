namespace HeliVMS.App.Tests;

/// <summary>
/// 書籤要畫在時間軸上（QCV8001 的 timeline 有 bookmark 標記）。
/// </summary>
/// <remarks>
/// <para>
/// 書籤是操作員自己標的「這裡要注意」，但長期只有清單、沒有時間軸刻度：標完之後只能靠清單
/// 逐筆點，看不到自己標過哪些位置，也沒得沿著時間軸掃過去。機器記的事件（白）與人標的書籤（黃）
/// 必須分得出來——顏色一樣就等於「我標過哪裡」被事件淹掉。
/// </para>
/// </remarks>
public sealed class PlaybackBookmarkTimelineContractTests
{
    private const string Code = "src/HeliVMS.App/PlaybackWindow.xaml.cs";

    [Fact]
    public void 色帶列會畫出書籤刻度且與事件分色()
    {
        var bands = SourceContract.BodyOf(SourceContract.Read(Code), "RenderChannelBands");

        Assert.NotEqual(string.Empty, bands);
        Assert.Contains("_bookmarks", bands);
        Assert.Contains("s_bookmarkMarkFill", bands);

        // 事件刻度仍然是白的：兩者同色就分不出「機器記的」與「我標的」。
        Assert.Contains("Brushes.White", bands);
    }

    [Fact]
    public void 書籤刻度與事件同排且只在焦點台那排()
    {
        // _bookmarks 只載焦點頻道的書籤（ReloadBookmarks），所以畫在焦點台那一排才有意義；
        // 畫到別的排會是空的，而「空的刻度」比沒有刻度更難查。
        var bands = SourceContract.BodyOf(SourceContract.Read(Code), "RenderChannelBands");
        var focusBranch = bands.IndexOf("IsFocus", StringComparison.Ordinal);
        var marks = bands.IndexOf("foreach (var bookmark in _bookmarks)", StringComparison.Ordinal);

        Assert.True(focusBranch >= 0, "找不到焦點台分支");
        Assert.True(marks > focusBranch, "書籤刻度要跟事件刻度同一排（焦點台），不是每一排都畫");
    }

    [Fact]
    public void 書籤的增改刪都會重畫時間軸()
    {
        var source = SourceContract.Read(Code);

        // 只更新清單不重畫，時間軸上會留著已經刪掉的刻度——比沒有刻度更糟。
        Assert.Contains("RenderBlocks()", SourceContract.BodyOf(source, "OnAddBookmarkClicked"));
        Assert.Contains("RenderBlocks()", SourceContract.BodyOf(source, "OnRenameBookmarkClicked"));
        Assert.Contains("RenderBlocks()", SourceContract.BodyOf(source, "OnDeleteBookmarkClicked"));
    }
}
