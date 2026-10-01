using System.Buffers.Binary;
using System.Text;

namespace HeliVMS.Api.Tests;

/// <summary>合成的 MP4 box 工具（測試用）：真實錄影檔需要 ffmpeg，這裡用同構的位元組組出 box。</summary>
internal static class Mp4Fixture
{
    public static byte[] Box(string type, int payload, int declaredPayload = -1)
    {
        var size = 8 + (declaredPayload >= 0 ? declaredPayload : payload);
        var buffer = new byte[size];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, (uint)size);
        Encoding.ASCII.GetBytes(type).CopyTo(buffer, 4);
        return buffer;
    }

    public static byte[] Fragment(int payload) => Concat(Box("moof", payload), Box("mdat", payload));

    /// <summary>ftyp＋moov＋一個 moof/mdat 的最小 fMP4。</summary>
    public static byte[] Fmp4() => Concat(Box("ftyp", 8), Box("moov", 16), Fragment(32));

    /// <summary>宣告某個大小、但實際只給 <paramref name="actualBytes"/> 位元組的 box（測試截斷與前綴情境）。</summary>
    public static byte[] BoxWithDeclaredSize(string type, long declaredSize, int actualBytes)
    {
        var buffer = new byte[actualBytes];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, (uint)declaredSize);
        Encoding.ASCII.GetBytes(type).CopyTo(buffer, 4);
        return buffer;
    }

    public static byte[] LargeSizeBox(string type, int payload)
    {
        const int header = 16;
        var buffer = new byte[header + payload];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, 1);
        Encoding.ASCII.GetBytes(type).CopyTo(buffer, 4);
        BinaryPrimitives.WriteUInt64BigEndian(buffer.AsSpan(8), (ulong)buffer.Length);
        return buffer;
    }

    public static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(p => p.Length)];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }

        return result;
    }

    public static byte[] Prefix(byte[] all, int length) => all.AsSpan(0, length).ToArray();
}
