using System.Text.Json;

namespace HeliVMS.Storage;

/// <summary>最近一次錄影心跳的內容。</summary>
public sealed record RecordingHeartbeatRecord(DateTime UpdatedUtc, int ProcessId, int ActiveChannels);

/// <summary>心跳的新鮮度。</summary>
public enum RecordingHeartbeatStatus
{
    /// <summary>仍在容許時距內。</summary>
    Fresh,

    /// <summary>檔案存在但已超過容許時距。</summary>
    Stale,

    /// <summary>檔案不存在（從未寫過或已被刪除）。</summary>
    Missing,
}

/// <summary>
/// 錄影心跳檔（M320，§11.1 看門狗）。
///
/// <para>
/// 錄影行程每個調和週期把「我還活著、現在錄幾路」寫進資料根目錄下的一個小檔案
/// （<c>recording.heartbeat</c>）。看門狗讀這個檔案就能分辨三種狀態：行程活著且在錄
/// （Fresh）、行程卡住或整個死掉（Stale）、從來沒跑過（Missing）。
/// </para>
///
/// <para>
/// 刻意<b>不</b>用資料庫：心跳要能在資料庫被鎖住（正是最需要告警的那種卡住）時仍寫得出來，
/// 也要能被一個不連資料庫的輕量監看程式讀取。寫入失敗一律吞掉——它只是維運訊號，
/// 不該讓錄影因此停擺。
/// </para>
/// </summary>
public static class RecordingHeartbeat
{
    /// <summary>心跳檔名（位於資料根目錄）。</summary>
    public const string FileName = "recording.heartbeat";

    /// <summary>
    /// 超過這個時距沒更新就算過期。調和週期預設 30 秒，取 2 分鐘等於容許連續 3 個週期沒動，
    /// 足以吸收一次正常的資料庫慢查詢，又能在真正卡死時很快告警。
    /// </summary>
    public static readonly TimeSpan DefaultStaleAfter = TimeSpan.FromMinutes(2);

    public static string PathFor(string dataRoot) => Path.Combine(dataRoot, FileName);

    /// <summary>
    /// 寫入心跳（先寫 <c>.tmp</c> 再改名覆蓋，避免看門狗讀到寫一半的內容）。
    /// 任何失敗都吞掉：心跳檔寫不進去不是錄影該停的理由。
    /// </summary>
    public static void Write(string dataRoot, DateTime updatedUtc, int activeChannels, int processId)
    {
        try
        {
            Directory.CreateDirectory(dataRoot);
            var json = JsonSerializer.Serialize(new RecordingHeartbeatRecord(updatedUtc, processId, activeChannels));
            var path = PathFor(dataRoot);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or ArgumentException
                or NotSupportedException or PathTooLongException)
        {
            // 權限／路徑問題；心跳只是訊號，不影響錄影。
        }
    }

    /// <summary>讀取心跳；檔案不存在或內容壞掉一律回 <c>null</c>（視為 Missing）。</summary>
    public static RecordingHeartbeatRecord? TryRead(string dataRoot)
    {
        try
        {
            var path = PathFor(dataRoot);
            if (!File.Exists(path))
            {
                return null;
            }

            return JsonSerializer.Deserialize<RecordingHeartbeatRecord>(File.ReadAllText(path));
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException
                or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// 判定新鮮度。時距是「現在 − 上次更新」，<b>大於</b>門檻才算過期（剛好等於視為新鮮）。
    /// </summary>
    public static RecordingHeartbeatStatus Evaluate(
        RecordingHeartbeatRecord? heartbeat, DateTime nowUtc, TimeSpan staleAfter)
    {
        if (heartbeat is null)
        {
            return RecordingHeartbeatStatus.Missing;
        }

        return nowUtc - heartbeat.UpdatedUtc > staleAfter
            ? RecordingHeartbeatStatus.Stale
            : RecordingHeartbeatStatus.Fresh;
    }

    /// <summary>直接讀檔並判定新鮮度。</summary>
    public static RecordingHeartbeatStatus Evaluate(string dataRoot, DateTime nowUtc, TimeSpan? staleAfter = null)
        => Evaluate(TryRead(dataRoot), nowUtc, staleAfter ?? DefaultStaleAfter);
}
