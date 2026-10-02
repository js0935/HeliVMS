using HeliVMS.Shared;

namespace HeliVMS.Rtc;

/// <summary>
/// 累積外部行程的 stderr，並在寫入前<b>逐行遮蔽憑證</b>。
/// <para>
/// 為什麼值得獨立成一個型別：ffmpeg 會把自己的輸入位址回顯在 stderr 上，形如
/// <c>Input #0, rtsp, from 'rtsp://user:pass@host/stream'</c>。這裡的位址含有 RTSP 帳密，
/// 而它終究會出現在 API 回應或維運記錄裡。遮蔽一旦被「為了好讀」而移除，代價是明文
/// 密碼進了備份與 log。
/// </para>
/// <para>
/// 抽成獨立型別的唯一目的是<b>讓它可測</b>：<c>LivePublisher.Start</c> 需要真的執行
/// ffmpeg，測試環境未必有；把「遮蔽」這一步獨立出來，就能直接對它做單元測試，
/// 而不必啟動任何外部行程。這是 repo 既有的手法（參考 <c>RtspStreamResolver</c>）。
/// </para>
/// </summary>
public sealed class RedactingErrorBuffer
{
    /// <summary>保留的最大行數；超出時丟棄最舊的行。</summary>
    public const int MaxLines = 20;

    private const int MaxLineLength = 2000;

    private readonly Lock _gate = new();
    private readonly Queue<string> _lines = new();

    /// <summary>加入一行輸出，並先做憑證遮蔽。</summary>
    public void Append(string? line)
    {
        if (line is null) return;

        // 先遮蔽再截斷：反過來的話，被截掉的尾巴可能正好是密碼的一截。
        var scrubbed = RtspUri.RedactText(line);
        if (scrubbed.Length > MaxLineLength)
        {
            scrubbed = scrubbed[..MaxLineLength] + "…";
        }

        lock (_gate)
        {
            _lines.Enqueue(scrubbed);
            while (_lines.Count > MaxLines)
            {
                _lines.Dequeue();
            }
        }
    }

    /// <summary>取得目前累積的內容（已遮蔽）。</summary>
    public string Text
    {
        get
        {
            lock (_gate) return string.Join('\n', _lines).Trim();
        }
    }

    /// <summary>清空內容。</summary>
    public void Clear()
    {
        lock (_gate) _lines.Clear();
    }
}
