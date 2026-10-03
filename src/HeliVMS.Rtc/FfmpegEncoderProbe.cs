using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;

namespace HeliVMS.Rtc;

/// <summary>編碼器探測的三種結果。</summary>
public enum FfmpegEncoderStatus
{
    /// <summary>編碼器存在。</summary>
    Available,

    /// <summary>
    /// ffmpeg 跑得起來，但沒有這個編碼器。
    /// <para>
    /// 這是<b>唯一</b>值得提前擋掉的情況：minimal 版的 ffmpeg 預設不含 libx264，
    /// 而症狀是「按了開啟、等了 30 秒、看到一段 ffmpeg stderr」。
    /// </para>
    /// </summary>
    Missing,

    /// <summary>
    /// 問不到：ffmpeg 不存在、不是可執行檔，或探測逾時。
    /// <para>
    /// 刻意<b>不</b>當成 <see cref="Missing"/>：那會用「沒有 libx264」蓋掉真正該說的
    /// 「HELIVMS_WHEP_FFMPEG 指向的檔案不存在」。前者要使用者去換 ffmpeg build，
    /// 後者只要改一個環境變數——講錯方向會讓人改錯設定。
    /// </para>
    /// </summary>
    Unavailable,
}

/// <summary>
/// 確認 ffmpeg 真的編得出 H.264，缺編碼器時<b>立刻</b>失敗並說明原因。
///
/// <para>
/// M244 §2 要求：「若伺服器端 ffmpeg 沒有 <c>libx264</c>，啟動時要明確失敗並說明原因，
/// 不能靜默退成沒有畫面。」
/// </para>
///
/// <para>
/// 不做這件事時會發生什麼：ffmpeg 照樣被啟動，輸出
/// <c>Unknown encoder 'libx264'</c> 後以非零碼結束。症狀是「按了開啟即時畫面，
/// 等了 30 秒，然後失敗」——而且錯誤訊息是一整段 ffmpeg stderr，操作員要自己從裡面
/// 找出真正的原因。
/// </para>
///
/// <para>
/// 結果按 <c>ffmpegPath</c> 快取：探測要開一個行程，publisher 每路一條、而且觀看者
/// 進來時就會啟動。同一個 ffmpeg 路徑只需要問一次。
/// </para>
/// </summary>
public sealed class FfmpegEncoderProbe
{
    private static readonly ConcurrentDictionary<string, Task<FfmpegEncoderStatus>> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly string _ffmpegPath;
    private readonly TimeSpan _timeout;
    private readonly Func<CancellationToken, Task<string?>>? _readEncoders;

    public FfmpegEncoderProbe(string ffmpegPath, TimeSpan? timeout = null)
        : this(ffmpegPath, timeout, readEncoders: null)
    {
    }

    /// <summary>
    /// 以注入的「讀取編碼器清單」取代真的啟動行程。
    /// <para>
    /// 這是為了讓 Available／Missing／Unavailable 三種結果都能被<b>確定性</b>驗證：
    /// 真的去跑 <c>cmd.exe</c> 或 <c>findstr.exe</c> 當假 ffmpeg，它們收到
    /// <c>-hide_banner -encoders</c> 會怎麼反應不是我們能控制的，測試不該建立在
    /// 別支程式的行為上。
    /// </para>
    /// </summary>
    internal FfmpegEncoderProbe(
        string ffmpegPath,
        TimeSpan? timeout,
        Func<CancellationToken, Task<string?>>? readEncoders)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ffmpegPath);
        _ffmpegPath = ffmpegPath;
        _timeout = timeout ?? TimeSpan.FromSeconds(10);
        _readEncoders = readEncoders;
    }

    /// <summary>探測逾時；夠跑完一次 <c>ffmpeg -encoders</c> 即可。</summary>
    public TimeSpan Timeout => _timeout;

    /// <summary>查 <paramref name="encoder"/> 是否可用；結果依路徑快取。</summary>
    public Task<FfmpegEncoderStatus> ProbeAsync(string encoder, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(encoder);

        // 注入讀取器時不快取：快取是為了別讓正式環境重複開行程，
        // 而測試的每次呼叫本來就該是獨立的一次詢問。
        if (_readEncoders is not null) return ProbeCoreAsync(encoder, token);

        return Cache.GetOrAdd(Key(encoder), _ => ProbeCoreAsync(encoder, token));
    }

    private async Task<FfmpegEncoderStatus> ProbeCoreAsync(string encoder, CancellationToken token)
    {
        string? output;
        try
        {
            output = await ReadEncodersAsync(token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ObjectDisposedException)
        {
            return FfmpegEncoderStatus.Unavailable;
        }

        // 讀不到輸出＝行程起不來或逾時，與「跑起來但沒這顆編碼器」必須分開。
        if (output is null) return FfmpegEncoderStatus.Unavailable;

        return MentionsEncoder(output, encoder)
            ? FfmpegEncoderStatus.Available
            : FfmpegEncoderStatus.Missing;
    }

    private string Key(string encoder) => $"{_ffmpegPath}\u0000{encoder}";

    private async Task<string?> ReadEncodersAsync(CancellationToken token)
    {
        if (_readEncoders is not null) return await _readEncoders(token).ConfigureAwait(false);

        var psi = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // -encoders 走 stdout；-hide_banner 讓輸出不含版本雜訊，減少誤判。
        foreach (var arg in new[] { "-hide_banner", "-encoders" }) psi.ArgumentList.Add(arg);

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return null;
        }

        if (process is null) return null;

        using (process)
        {
            // 同時讀兩個流：ffmpeg 會把 banner 之類寫到 stderr，先讀完 stdout 再讀 stderr
            // 會在輸出量大時死鎖。
            var stdout = process.StandardOutput.ReadToEndAsync(token);
            var stderr = process.StandardError.ReadToEndAsync(token);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(_timeout);

            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                // 探測逾時不該拖住 publisher 啟動；殺掉行程後當成問不到。
                TryKill(process);
                return null;
            }

            return await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or Win32Exception)
        {
            // 已經結束或無權限；探測失敗照常往下走。
        }
    }

    /// <summary>
    /// 判斷 <c>ffmpeg -encoders</c> 的輸出裡有沒有這個編碼器。
    /// <para>純函式，讓「格式長什麼樣」可以獨立驗證，不必真的跑 ffmpeg。</para>
    /// </summary>
    /// <remarks>
    /// 輸出一行一個編碼器，格式為 <c>V....D libx264   libx264 H.264 ...</c>。
    /// 比對時用「空白包圍的詞」而不是 <c>Contains</c>：<c>libx264</c> 是 <c>libx264rgb</c>
    /// 的前綴，用 <c>Contains</c> 會在只有 RGB 變體時誤判為可用，而那個編碼器輸出的是
    /// 4:4:4，SFU 端與瀏覽器都解不出來——症狀又是「連上了沒有畫面」。
    /// </remarks>
    public static bool MentionsEncoder(string? encodersOutput, string encoder)
    {
        if (string.IsNullOrEmpty(encodersOutput)) return false;

        foreach (var line in encodersOutput.Split('\n'))
        {
            var columns = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            // 至少要有旗標欄與名稱欄；描述欄可有可無。
            if (columns.Length < 2) continue;

            // 精確比對名稱欄。
            if (string.Equals(columns[1], encoder, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    /// <summary>
    /// 清掉快取。測試用：同一個 ffmpeg 路徑在不同設定下會得到不同結果。
    /// </summary>
    internal static void ResetCacheForTests() => Cache.Clear();
}