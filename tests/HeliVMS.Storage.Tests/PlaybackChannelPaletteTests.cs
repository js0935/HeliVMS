using HeliVMS.Storage;

namespace HeliVMS.Storage.Tests;

/// <summary>
/// 通道色帶列的配色索引（§8.5「色帶按通道橫向排列」）。
/// 索引錯了不會 crash，只會讓兩台攝影機拿到同一條色帶——多路比對時誤判成同一台，比沒色帶更糟。
/// </summary>
public class PlaybackChannelPaletteTests
{
    [Fact]
    public void 同一通道永遠得到同一個色盤位置()
    {
        // 色帶列與牆邊框要對得上：同一台在兩處不同色，操作員會把色帶讀到隔壁畫面。
        Assert.Equal(PlaybackChannelPalette.IndexFor(3), PlaybackChannelPalette.IndexFor(3));
    }

    [Fact]
    public void 相鄰通道不會同色()
    {
        for (var id = 1; id < 16; id++)
        {
            Assert.NotEqual(
                PlaybackChannelPalette.IndexFor(id),
                PlaybackChannelPalette.IndexFor(id + 1));
        }
    }

    [Theory]
    [InlineData(-3)]
    [InlineData(0)]
    [InlineData(9)]
    [InlineData(100)]
    public void 任意通道ID都有色盤位置(int channelId)
    {
        Assert.InRange(PlaybackChannelPalette.IndexFor(channelId), 0, PlaybackChannelPalette.Size - 1);
    }
}
