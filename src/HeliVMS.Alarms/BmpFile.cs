using System.Text;
using HeliVMS.Shared.Models;

namespace HeliVMS.Alarms;

/// <summary>BGR24 像素影像（未含 padding；與 <see cref="VideoFrame"/> 同 layout）。</summary>
public readonly record struct BgrImage(int Width, int Height, byte[] Pixels);

/// <summary>
/// 24-bit 無壓縮 BMP 之讀取／寫入（M63 影片摘要）。寫入與 <see cref="BmpSnapshotWriter"/>
/// 同位元組格式；讀取支援正向（由下而上）與負向（自上而下）高度、含 row padding 與 DIB 標頭。
/// 零外部依賴。
/// </summary>
public static class BmpFile
{
    public static void Save(byte[] bgr, int width, int height, string path)
    {
        if (bgr.Length != width * height * 3)
        {
            throw new ArgumentException("像素緩衝區大小與寬高不符。", nameof(bgr));
        }

        var stride = width * 3;
        var paddedRow = ((stride + 3) / 4) * 4;
        var pixelBytes = paddedRow * height;
        var headerSize = 14 + 40;
        var fileSize = headerSize + pixelBytes;

        using var fs = new FileStream(path, FileMode.Create);
        using var w = new BinaryWriter(fs, Encoding.ASCII, leaveOpen: false);

        w.Write((byte)'B');
        w.Write((byte)'M');
        w.Write(fileSize);
        w.Write(0);
        w.Write(headerSize);

        w.Write(40);
        w.Write(width);
        w.Write(height);
        w.Write((short)1);
        w.Write((short)24);
        w.Write(0);
        w.Write(pixelBytes);
        w.Write(2835);
        w.Write(2835);
        w.Write(0);
        w.Write(0);

        var rowBuf = new byte[paddedRow];
        for (var y = height - 1; y >= 0; y--)
        {
            Buffer.BlockCopy(bgr, y * stride, rowBuf, 0, stride);
            w.Write(rowBuf, 0, paddedRow);
        }
    }

    /// <summary>讀取 24-bit BMP 並回傳 BGR24 像素（不含 padding，Top-left 起始）。</summary>
    public static BgrImage Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 54 || bytes[0] != 'B' || bytes[1] != 'M')
        {
            throw new InvalidDataException("非 24-bit BMP 檔案。");
        }

        var dataOffset = BitConverter.ToInt32(bytes, 10);
        var dibSize = BitConverter.ToInt32(bytes, 14);
        if (dibSize < 40)
        {
            throw new InvalidDataException("不支援的 DIB 標頭。");
        }

        var width = BitConverter.ToInt32(bytes, 18);
        var heightRaw = BitConverter.ToInt32(bytes, 22);
        var planes = BitConverter.ToUInt16(bytes, 26);
        var bitCount = BitConverter.ToUInt16(bytes, 28);
        var compression = BitConverter.ToUInt32(bytes, 30);
        if (planes != 1 || bitCount != 24 || compression != 0 || width <= 0 || heightRaw == 0)
        {
            throw new InvalidDataException("僅支援 24-bit BI_RGB BMP。");
        }

        var height = Math.Abs(heightRaw);
        var bottomUp = heightRaw > 0;
        var stride = width * 3;
        var paddedRow = ((stride + 3) / 4) * 4;
        var rowStart = dataOffset;
        var pixels = new byte[width * height * 3];

        for (var y = 0; y < height; y++)
        {
            var fileRow = bottomUp ? (height - 1 - y) : y;
            var src = rowStart + fileRow * paddedRow;
            Buffer.BlockCopy(bytes, src, pixels, y * stride, stride);
        }

        return new BgrImage(width, height, pixels);
    }
}