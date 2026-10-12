namespace HeliVMS.Storage.Tests;

/// <summary>
/// 回放遮蔽疊圖投影（M279，§14.7 #5）。區域以來源影像像素儲存，回放顯示框大小不一，
/// 必須等比縮放並裁剪；若縮放算錯，疊圖框與匯出實際遮蔽位置會錯開，操作員會以為「這區沒被遮」。
/// </summary>
public sealed class RedactionOverlayTests
{
    private static RedactionRegion Region(int x, int y, int w, int h, bool filled = true) =>
        new(1, RedactionSources.Clip, 5, 3, new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc),
            x, y, w, h, filled, new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc));

    [Fact]
    public void 投影會依顯示框等比縮放()
    {
        var boxes = RedactionOverlay.Project([Region(100, 50, 200, 100)], 1920, 1080, 960, 540);

        var box = Assert.Single(boxes);
        Assert.Equal(50, box.X, 3);
        Assert.Equal(25, box.Y, 3);
        Assert.Equal(100, box.Width, 3);
        Assert.Equal(50, box.Height, 3);
    }

    [Fact]
    public void 投影保留實心與模糊旗標()
    {
        var boxes = RedactionOverlay.Project(
            [Region(0, 0, 10, 10, filled: false), Region(20, 20, 10, 10, filled: true)],
            100, 100, 100, 100);

        Assert.False(boxes[0].Filled);
        Assert.True(boxes[1].Filled);
    }

    [Fact]
    public void 超出來源範圍的區域會被裁剪到顯示框內()
    {
        var boxes = RedactionOverlay.Project([Region(90, 90, 50, 50)], 100, 100, 100, 100);

        var box = Assert.Single(boxes);
        Assert.Equal(90, box.X, 3);
        Assert.Equal(90, box.Y, 3);
        Assert.Equal(10, box.Width, 3);
        Assert.Equal(10, box.Height, 3);
    }

    [Fact]
    public void 負座標區域會被夾到框內()
    {
        var boxes = RedactionOverlay.Project([Region(-10, -10, 30, 30)], 100, 100, 100, 100);

        var box = Assert.Single(boxes);
        Assert.Equal(0, box.X, 3);
        Assert.Equal(0, box.Y, 3);
        Assert.Equal(20, box.Width, 3);
        Assert.Equal(20, box.Height, 3);
    }

    [Fact]
    public void 完全在框外的區域會被略過()
    {
        Assert.Empty(RedactionOverlay.Project([Region(200, 200, 10, 10)], 100, 100, 100, 100));
    }

    [Fact]
    public void 寬高非正的區域會被略過()
    {
        Assert.Empty(RedactionOverlay.Project([Region(10, 10, 0, 10)], 100, 100, 100, 100));
    }

    [Theory]
    [InlineData(0, 100, 100, 100)]
    [InlineData(100, 0, 100, 100)]
    [InlineData(100, 100, 0, 100)]
    [InlineData(100, 100, 100, 0)]
    public void 來源或目標尺寸非正時回空集合(int sourceWidth, int sourceHeight, double targetWidth, double targetHeight)
    {
        Assert.Empty(RedactionOverlay.Project([Region(10, 10, 10, 10)], sourceWidth, sourceHeight, targetWidth, targetHeight));
    }

    [Fact]
    public void 沒有區域時回空集合()
    {
        Assert.Empty(RedactionOverlay.Project([], 1920, 1080, 960, 540));
    }

    [Fact]
    public void 區域清單為null時擲例外()
    {
        Assert.Throws<ArgumentNullException>(() => RedactionOverlay.Project(null!, 1920, 1080, 960, 540));
    }
}
