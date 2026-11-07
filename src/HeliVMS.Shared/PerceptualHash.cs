using System.Numerics;

namespace HeliVMS.Shared;

/// <summary>
/// 感知雜湊（dHash——difference hash）：把灰階影像壓成 64-bit 指紋，
/// 供「以圖搜圖」的近似比對（QCV8001 的 QCJpegSearch 對應功能的演算法核心）。
/// </summary>
/// <remarks>
/// <para>
/// 原理：縮到 9×8 灰階後，逐列比較相鄰兩格的明暗（左 &gt; 右記 1 位），
/// 得 8×8＝64 位。記的是「明暗變化的方向」而不是絕對亮度——整體加亮、減亮、
/// 對比拉伸都不改變指紋。監視畫面的日夜曝光差異大，這正是要的特性：
/// 同一個場景白天與半夜（紅外線）拍到的縮圖，絕對亮度差很多，但紋理方向相近。
/// </para>
/// <para>
/// <b>已知限制（必須在呼叫端與 UI 說明）</b>：低紋理的畫面（全黑、全白、一面牆、
/// 關燈的走廊）幾乎沒有明暗變化，任何兩張低紋理畫面的距離都會很小——
/// 在這種場景「找到相似畫面」等於沒有判別力。所以搜尋結果一律附上距離值
/// （0–64，愈小愈像），由操作員自行判斷，而不是只報「找到 N 筆」。
/// </para>
/// <para>
/// 64 位指紋在 8 位元漢明距離內的隨機碰撞率約 1 百萬分之一量級——
/// 對「在幾千張樣本中找相似」足夠；真正要比對大規模庫時應改用
/// 更長的指紋或二階段驗證（先用本雜湊篩選、再用像素差確認）。
/// </para>
/// </remarks>
public static class PerceptualHash
{
    /// <summary>雜湊用的灰階網格寬（9 欄——每列做 8 次相鄰比較）。</summary>
    public const int GrayColumns = 9;

    /// <summary>雜湊用的灰階網格高。</summary>
    public const int GrayRows = 8;

    /// <summary>9×8 灰階的位元組數。</summary>
    public const int GrayBytes = GrayColumns * GrayRows;

    /// <summary>指紋位元數。</summary>
    public const int Bits = (GrayColumns - 1) * GrayRows;

    /// <summary>
    /// 由 9×8 灰階（列優先、每列 9 bytes）計算 64-bit 指紋。
    /// 每列左格比右格亮記 1 位，從最低位開始填。
    /// </summary>
    public static ulong FromGray9x8(ReadOnlySpan<byte> gray9x8)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(gray9x8.Length, GrayBytes);

        ulong hash = 0;
        var bit = 0;
        for (var y = 0; y < GrayRows; y++)
        {
            var row = y * GrayColumns;
            for (var x = 0; x < GrayColumns - 1; x++)
            {
                if (gray9x8[row + x] > gray9x8[row + x + 1])
                {
                    hash |= 1UL << bit;
                }

                bit++;
            }
        }

        return hash;
    }

    /// <summary>
    /// 由任意尺寸的灰階影像（列優先）計算指紋：先以盒濾波降採樣到 9×8。
    /// 這是「抽出畫面 → 指紋」的正式入口（32×32 的取樣幀、原始查詢圖都走這裡）。
    /// </summary>
    public static ulong FromGrayImage(ReadOnlySpan<byte> gray, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfLessThan(gray.Length, width * height);

        Span<byte> small = stackalloc byte[GrayBytes];
        for (var ty = 0; ty < GrayRows; ty++)
        {
            var y0 = ty * height / GrayRows;
            var y1 = Math.Max(y0 + 1, (ty + 1) * height / GrayRows);
            for (var tx = 0; tx < GrayColumns; tx++)
            {
                var x0 = tx * width / GrayColumns;
                var x1 = Math.Max(x0 + 1, (tx + 1) * width / GrayColumns);
                var sum = 0;
                var count = 0;
                for (var y = y0; y < y1 && y < height; y++)
                {
                    var row = y * width;
                    for (var x = x0; x < x1 && x < width; x++)
                    {
                        sum += gray[row + x];
                        count++;
                    }
                }

                // count==0（來源比 9×8 還小時的角落格）：以 0 填入——低紋理，
                // 本來就沒有判別力，不要讓它汙染整張指紋。
                small[(ty * GrayColumns) + tx] = count > 0 ? (byte)(sum / count) : (byte)0;
            }
        }

        return FromGray9x8(small);
    }

    /// <summary>漢明距離（0＝指紋相同、64＝完全相反）。</summary>
    public static int HammingDistance(ulong a, ulong b) => BitOperations.PopCount(a ^ b);

    /// <summary>相似度 0–1（1＝相同指紋）；僅供顯示，門檻判斷請用 <see cref="HammingDistance"/>。</summary>
    public static double Similarity(ulong a, ulong b) => 1.0 - (HammingDistance(a, b) / (double)Bits);
}
