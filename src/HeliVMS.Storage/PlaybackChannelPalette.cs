namespace HeliVMS.Storage;

/// <summary>
/// 通道色帶列（§8.5）的通道配色索引：同一通道在色帶列與回放牆邊框永遠落在同一個色位，
/// 操作員不用讀名字就能把色帶對回畫面。純算數、無 WPF；顏色本身定義在 WPF 端。
/// </summary>
public static class PlaybackChannelPalette
{
    /// <summary>色盤大小：8 色足夠 1/2/4 路牆一眼分辨；超過時迴圈重用（同色不同排不影響判讀）。</summary>
    public const int Size = 8;

    /// <summary>通道 ID → 色盤索引（0..<see cref="Size"/>）。ID 相鄰時索引相鄰，兩台不會意外同色。</summary>
    public static int IndexFor(int channelId)
    {
        // 測試可能塞 0 或負值；先取絕對值再取模，任何 ID 都有確定位置。
        var id = Math.Abs(channelId);
        return id % Size;
    }
}
