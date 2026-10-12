using System.Text;

namespace HeliVMS.Shared;

/// <summary>
/// 有上限的檔案日誌附加寫入。
///
/// 為什麼需要這個：應用程式的崩潰記錄是 <c>File.AppendAllText</c>，沒有任何上限。
/// 正常情況下一個程序崩潰幾次就沒了，但有兩種常見情形會讓它長到失控——
/// 啟動時就失敗的迴圈，或同一個例外每秒重現一次（計時器、事件迴圈）。
/// 沒有上限的結果是 %TEMP% 裡躺著一個幾百 MB 的文字檔：每次寫入都要重新
/// 配置緩衝、會占滿磁碟，而且診斷時真正需要的其實只有「最近發生什麼」。
///
/// 裁掉舊內容而不是刪掉整個檔案，是因為使用者常常是幾天後才去看這個檔案；
/// 只留尾巴仍然看得到最近一次崩潰，而檔案大小從此有上限。
/// </summary>
public static class BoundedFileLog
{
    /// <summary>附加寫入，檔案超過 <paramref name="maxBytes"/> 後只保留最新的尾巴。</summary>
    /// <param name="path">檔案路徑。</param>
    /// <param name="content">要附加的內容。</param>
    /// <param name="maxBytes">檔案大小上限（位元組）。</param>
    /// <param name="onError">
    /// 寫入失敗時的處置。預設忽略——記錄診斷資訊的函式本身不該讓應用程式再崩一次，
    /// 那會把真正的例外蓋掉。傳入可觀察的委派則能測試失敗路徑。
    /// </param>
    public static void Append(
        string path,
        string content,
        long maxBytes,
        Action<Exception>? onError = null)
    {
        try
        {
            File.AppendAllText(path, content);

            if (maxBytes > 0 && new FileInfo(path).Length > maxBytes)
            {
                TrimToTail(path, maxBytes);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            onError?.Invoke(ex);
        }
    }

    /// <summary>只保留檔案最後 <paramref name="maxBytes"/> 位元組。</summary>
    private static void TrimToTail(string path, long maxBytes)
    {
        var length = new FileInfo(path).Length;
        if (length <= maxBytes)
        {
            return;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        stream.Seek(-maxBytes, SeekOrigin.End);
        var buffer = new byte[maxBytes];
        var read = stream.Read(buffer, 0, buffer.Length);

        var text = Encoding.UTF8.GetString(buffer, 0, read);

        // 從中間切開會留下半行亂碼，那是上一則記錄的尾巴，會讓下次讀檔的人（或下一次
        // 追加）以為 log 從這裡開始。丟掉第一行直到換行，之後的內容才是完整記錄。
        var firstNewLine = text.IndexOf('\n');
        if (firstNewLine >= 0 && firstNewLine + 1 < text.Length)
        {
            text = text[(firstNewLine + 1)..];
        }

        File.WriteAllText(path, text);
    }
}