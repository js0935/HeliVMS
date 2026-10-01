using System.Buffers.Binary;
using System.Text;

namespace HeliVMS.WebApi;

/// <summary>頂層 MP4 box：型別與在檔案中的位置（§14.3 串流）。</summary>
public readonly record struct Mp4Box(string Type, long Offset, long Size)
{
    public long End => Offset + Size;
}

/// <summary>fMP4 分段檔的初始化／媒體切分（§14.3 串流）。</summary>
/// <remarks>
/// 錄影器以 <c>-movflags frag_keyframe+empty_moov+default_base_moof+faststart</c> 寫出
/// fMP4，每個檔案自帶 ftyp＋空 moov。HLS 的 fMP4 形態要求「整串只有一個初始化段」
/// （EXT-X-MAP），其後接一串 moof/mdat，因此要從每個錄影檔裡把 ftyp/moov 抽出來當
/// 初始化段、把 moof/mdat 留作媒體段。
/// </remarks>
public static class Fmp4Splitter
{
    public const int HeaderSize = 8;

    private const long LargeSizeMarker = 1;

    /// <summary>讀出頂層 box 清單；格式不合法時擲 <see cref="FormatException"/>。</summary>
    public static IReadOnlyList<Mp4Box> ReadTopLevel(ReadOnlySpan<byte> data)
    {
        var boxes = new List<Mp4Box>();
        var offset = 0;
        while (offset < data.Length)
        {
            boxes.Add(ReadBox(data, ref offset));
        }

        return boxes;
    }

    /// <summary>
    /// 初始化段長度：只走檔頭連續的 ftyp／moov，遇到第一個非初始化 box（moof/mdat…）即停，
    /// 因此可以只讀檔頭前綴，不必把整個錄影檔讀進記憶體。
    /// </summary>
    public static int InitLength(ReadOnlySpan<byte> data)
    {
        var offset = 0;
        var hasMoov = false;
        while (offset < data.Length)
        {
            ReadHeader(data, offset, out var type, out var size, out _);
            if (type is not ("ftyp" or "moov"))
            {
                if (!hasMoov)
                {
                    throw new FormatException("檔案開頭沒有 moov，不是可串流的 fMP4");
                }

                return offset;
            }

            if (offset + size > data.Length)
            {
                throw new FormatException($"MP4 box 超出檔案範圍（type={type} offset={offset} size={size}）");
            }

            hasMoov |= type == "moov";
            offset = checked((int)(offset + size));
        }

        throw new FormatException("檔案只有初始化段，沒有 moof/mdat 媒體段");
    }

    /// <summary>
    /// 切出初始化段（檔頭連續的 ftyp／moov）與其後的媒體段起點。
    /// 沒有 moov（不是 fMP4）或沒有 moof（沒有可播放片段）時擲 <see cref="FormatException"/>。
    /// </summary>
    public static Fmp4Parts Split(ReadOnlySpan<byte> data) => new(InitLength(data));

    private static Mp4Box ReadBox(ReadOnlySpan<byte> data, ref int offset)
    {
        ReadHeader(data, offset, out var type, out var size, out _);

        if (offset + size > data.Length)
        {
            throw new FormatException($"MP4 box 超出檔案範圍（type={type} offset={offset} size={size}）");
        }

        var box = new Mp4Box(type, offset, size);
        offset = checked((int)(offset + size));
        return box;
    }

    /// <summary>只讀 box 標頭，不驗證內容是否都在緩衝區內（媒體段常常只讀到檔頭）。</summary>
    private static void ReadHeader(ReadOnlySpan<byte> data, int offset, out string type, out long size, out int header)
    {
        var remaining = data.Length - offset;
        if (remaining < HeaderSize)
        {
            throw new FormatException($"MP4 box 標頭不足（offset {offset}）");
        }

        size = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
        type = Encoding.ASCII.GetString(data.Slice(offset + 4, 4));
        header = HeaderSize;

        if (size == LargeSizeMarker)
        {
            if (remaining < 16)
            {
                throw new FormatException($"MP4 box 大小欄位不足（offset {offset}）");
            }

            size = (long)BinaryPrimitives.ReadUInt64BigEndian(data.Slice(offset + 8, 8));
            header = 16;
        }
        else if (size == 0)
        {
            size = remaining;
        }

        if (size < header)
        {
            throw new FormatException($"MP4 box 大小不合理（type={type} size={size} offset={offset}）");
        }
    }
}

/// <summary>初始化段長度；媒體段自該長度起至檔尾。</summary>
public readonly record struct Fmp4Parts(int InitLength)
{
    public ReadOnlySpan<byte> Init(ReadOnlySpan<byte> data) => data[..InitLength];

    public ReadOnlySpan<byte> Media(ReadOnlySpan<byte> data) => data[InitLength..];
}
