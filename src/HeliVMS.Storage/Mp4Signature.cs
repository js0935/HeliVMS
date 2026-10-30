namespace HeliVMS.Storage;

/// <summary>
/// 「這個檔案看起來是 ffmpeg 寫出來的 MP4」的最低成本判定：第一個 box 的型別是不是 <c>ftyp</c>。
/// </summary>
/// <remarks>
/// <para>
/// 只看 12 bytes，不呼叫 ffprobe：呼叫端是啟動路徑與磁碟掃描，每個檔案叫一次外部行程會讓開機
/// 變慢，而且 ffprobe 對半截檔本來就只會回報錯誤——那正是這裡要判定的對象。
/// </para>
/// <para>
/// 為什麼共用同一份實作：錄影救援（<c>TemporarySegmentRecovery</c>）用它決定要不要把
/// <c>.tmp</c> 改名成正式檔，磁碟匯入（<c>RecordingDiskImporter</c>）用它決定要不要把
/// tmp／corrupt 的列復活。兩邊各寫一份，遲早會有一邊把垃圾檔放進回放清單，
/// 使用者點下去才發現播不出來。
/// </para>
/// <para>
/// 片段寫入（fMP4，<c>frag_keyframe+empty_moov</c>）的檔案每個 fragment 都自我完整，
/// 所以「開頭是 ftyp」的檔案即使被砍掉尾端，前面幾秒仍然播得出來。
/// </para>
/// </remarks>
public static class Mp4Signature
{
    /// <summary>ISO BMFF 的檔案簽章：第一個 box 型別固定在位移 4（前 4 bytes 是長度）。</summary>
    private static readonly byte[] Ftyp = "ftyp"u8.ToArray();

    /// <summary>第一個 box 是不是 <c>ftyp</c>；讀不到或不足 12 bytes 一律視為不是。</summary>
    public static bool LooksLikeMp4(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var header = new byte[12];
            var read = 0;
            while (read < header.Length)
            {
                var n = stream.Read(header, read, header.Length - read);
                if (n <= 0)
                {
                    break;
                }

                read += n;
            }

            return read >= 12 && header.AsSpan(4, 4).SequenceEqual(Ftyp);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
