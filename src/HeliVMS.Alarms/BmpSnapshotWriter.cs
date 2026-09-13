using System.Text;
using HeliVMS.Shared.Models;

namespace HeliVMS.Alarms;

/// <summary>將 BGR24 全彩幀寫成 24-bit 無壓縮 BMP（零外部依賴，Windows 相片檢視器等可直接開啟）。</summary>
public static class BmpSnapshotWriter
{
    public static void Save(VideoFrame frame, string path, bool overwrite = true)
    {
        var stride = frame.Width * 3;
        var paddedRow = ((stride + 3) / 4) * 4;
        var pixelBytes = paddedRow * frame.Height;
        var headerSize = 14 + 40;
        var fileSize = headerSize + pixelBytes;

        using var fs = new FileStream(path, overwrite ? FileMode.Create : FileMode.CreateNew);
        using var w = new BinaryWriter(fs, Encoding.ASCII, leaveOpen: false);

        // 檔案標頭
        w.Write((byte)'B');
        w.Write((byte)'M');
        w.Write(fileSize);
        w.Write(0);
        w.Write(headerSize);

        // DIB 標頭（BITMAPINFOHEADER）
        w.Write(40);             // biSize
        w.Write(frame.Width);
        w.Write(frame.Height);   // 正數＝由下而上，底部第一列先寫
        w.Write((short)1);       // biPlanes
        w.Write((short)24);      // biBitCount
        w.Write(0);              // biCompression（BI_RGB）
        w.Write(pixelBytes);
        w.Write(2835);           // X ppm（72dpi）
        w.Write(2835);
        w.Write(0);              // biClrUsed
        w.Write(0);

        // 像素：底部行優先（BMP 儲存由下而上）
        var bgr = frame.Pixels;
        var rowBuf = new byte[paddedRow];
        for (var y = frame.Height - 1; y >= 0; y--)
        {
            Buffer.BlockCopy(bgr, y * stride, rowBuf, 0, stride);
            w.Write(rowBuf, 0, paddedRow);
        }
    }
}