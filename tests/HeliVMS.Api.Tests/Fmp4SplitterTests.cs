using HeliVMS.WebApi;

namespace HeliVMS.Api.Tests;

/// <summary>
/// M239（§14.3 串流 P0）：fMP4 分段檔的初始化／媒體切分。
/// 錄影器寫的是 <c>frag_keyframe+empty_moov+default_base_moof+faststart</c>，
/// 每個檔案自帶 ftyp＋moov；HLS 要求整串只有一個初始化段，所以這裡是關鍵轉換。
/// </summary>
public sealed class Fmp4SplitterTests
{
    [Fact]
    public void InitLength_StopsAtFirstMediaBox()
    {
        var ftyp = Mp4Fixture.Box("ftyp", 8);
        var moov = Mp4Fixture.Box("moov", 16);
        var file = Mp4Fixture.Concat(ftyp, moov, Mp4Fixture.Fragment(32), Mp4Fixture.Fragment(32));

        Assert.Equal(ftyp.Length + moov.Length, Fmp4Splitter.InitLength(file));
    }

    [Fact]
    public void Split_MediaIsRemainderAfterInit()
    {
        var init = Mp4Fixture.Concat(Mp4Fixture.Box("ftyp", 8), Mp4Fixture.Box("moov", 16));
        var first = Mp4Fixture.Fragment(24);
        var second = Mp4Fixture.Fragment(24);
        var file = Mp4Fixture.Concat(init, first, second);

        var parts = Fmp4Splitter.Split(file);

        Assert.Equal(init, parts.Init(file).ToArray());
        Assert.Equal(Mp4Fixture.Concat(first, second), parts.Media(file).ToArray());
        Assert.Equal(file, Mp4Fixture.Concat(parts.Init(file).ToArray(), parts.Media(file).ToArray()));
    }

    [Fact]
    public void InitLength_WorksOnPrefixWithoutWholeFile()
    {
        var init = Mp4Fixture.Concat(Mp4Fixture.Box("ftyp", 8), Mp4Fixture.Box("moov", 16));
        var mdatHeader = Mp4Fixture.BoxWithDeclaredSize("mdat", 4 * 1024 * 1024, 64);
        var prefix = Mp4Fixture.Concat(init, mdatHeader);

        Assert.Equal(init.Length, Fmp4Splitter.InitLength(prefix));
    }

    [Fact]
    public void InitLength_Reads64BitLargeSizeHeader()
    {
        var ftyp = Mp4Fixture.Box("ftyp", 8);
        var moov = Mp4Fixture.LargeSizeBox("moov", 16);
        var file = Mp4Fixture.Concat(ftyp, moov, Mp4Fixture.Fragment(16));

        Assert.Equal(ftyp.Length + moov.Length, Fmp4Splitter.InitLength(file));
    }

    [Fact]
    public void InitLength_RejectsFileWithoutLeadingMoov()
    {
        var file = Mp4Fixture.Concat(Mp4Fixture.Box("ftyp", 8), Mp4Fixture.Box("mdat", 16), Mp4Fixture.Box("moov", 16));

        var ex = Assert.Throws<FormatException>(() => Fmp4Splitter.InitLength(file));
        Assert.Contains("moov", ex.Message);
    }

    [Fact]
    public void InitLength_RejectsInitOnlyFile()
    {
        var file = Mp4Fixture.Concat(Mp4Fixture.Box("ftyp", 8), Mp4Fixture.Box("moov", 16));

        var ex = Assert.Throws<FormatException>(() => Fmp4Splitter.InitLength(file));
        Assert.Contains("媒體段", ex.Message);
    }

    [Fact]
    public void InitLength_RejectsTruncatedBoxHeader()
    {
        var ex = Assert.Throws<FormatException>(() => Fmp4Splitter.InitLength([0, 0, 0]));
        Assert.Contains("標頭不足", ex.Message);
    }

    [Fact]
    public void InitLength_RejectsTruncatedMoov()
    {
        var file = Mp4Fixture.Concat(
            Mp4Fixture.Box("ftyp", 8),
            Mp4Fixture.BoxWithDeclaredSize("moov", 4096, 8),
            Mp4Fixture.Box("mdat", 16));

        var ex = Assert.Throws<FormatException>(() => Fmp4Splitter.InitLength(file));
        Assert.Contains("超出檔案範圍", ex.Message);
    }


    [Fact]
    public void ReadTopLevel_ListsEveryBox()
    {
        var file = Mp4Fixture.Concat(
            Mp4Fixture.Box("ftyp", 8),
            Mp4Fixture.Box("moov", 16),
            Mp4Fixture.Fragment(16),
            Mp4Fixture.Fragment(16));

        var boxes = Fmp4Splitter.ReadTopLevel(file);

        Assert.Equal(["ftyp", "moov", "moof", "mdat", "moof", "mdat"], boxes.Select(b => b.Type));
        Assert.Equal(file.Length, boxes[^1].End);
    }

    [Fact]
    public void ReadTopLevel_RejectsSizeSmallerThanHeader()
    {
        var file = Mp4Fixture.Box("ftyp", 0, declaredPayload: 0);
        file[3] = 4;

        var ex = Assert.Throws<FormatException>(() => Fmp4Splitter.ReadTopLevel(file));
        Assert.Contains("大小不合理", ex.Message);
    }
}
