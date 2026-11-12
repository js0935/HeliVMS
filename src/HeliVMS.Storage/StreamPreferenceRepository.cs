using System.Globalization;

namespace HeliVMS.Storage;

/// <summary>
/// 每頻道目前採用的碼流（M76，§15.2）持久化。切流決策本身是純函式（<see cref="StreamSwitcher"/>），
/// 但「目前在哪一流」與「上次切換時間」必須跨視窗與跨重啟保存：否則重開後畫面顯示 Main，
/// 實際卻可能是 Sub；維持期（honeymoon）被重置，每開一次窗就會多抖動一次切流。
/// 值存於 app_settings（key = channel.stream.{id} 與 channel.stream.{id}.at），不新增資料表。
/// </summary>
public sealed class StreamPreferenceRepository
{
    private const string KindPrefix = "channel.stream.";
    private const string AtSuffix = ".at";

    private readonly SettingsRepository _settings;

    private readonly SqliteStore _store;

    public StreamPreferenceRepository(SqliteStore store)
    {
        _store = store;
        _settings = new SettingsRepository(store);
    }

    /// <summary>目前碼流；未設定過者一律視為主碼流。</summary>
    public StreamKind GetKind(long channelId)
        => string.Equals(_settings.Get(KindPrefix + channelId), "sub", StringComparison.OrdinalIgnoreCase)
            ? StreamKind.Sub
            : StreamKind.Main;

    /// <summary>上次切換時間（UTC）；從未切換過回 null。</summary>
    public DateTime? GetLastSwitchUtc(long channelId)
    {
        var raw = _settings.Get(KindPrefix + channelId + AtSuffix);
        return DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t)
            ? t.ToUniversalTime()
            : null;
    }

    /// <summary>寫入目前碼流與切換時間。</summary>
    /// <remarks>
    /// <para>
    /// 刻意用<b>原始 UPSERT</b> 而不是 <see cref="SettingsRepository.Set"/>：切流是
    /// 品質協調器的機器決策（可頻繁觸發），後者每寫一次就記一筆 settings.set
    /// 稽核——實測現場 4,583 列（10 月 3,127／11 月 1,430），第二大稽核類別被
    /// 機器行為洗版，真正的「誰改了設定」被淹沒。這與 DiskHealthCoordinator、
    /// 段長修正游標是同一條規則：機器狀態走原始寫入，不記人工稽核。
    /// </para>
    /// <para>
    /// 「何時切的」由 channel.stream.{id}.at 鍵自己回答（值即時間），
    /// 稽核不是必要的可觀測性。
    /// </para>
    /// </remarks>
    public void Save(long channelId, StreamKind kind, DateTime switchedUtc)
    {
        var kindRaw = kind == StreamKind.Sub ? "sub" : "main";
        _store.Execute(
            """
            INSERT INTO app_settings (key, value, updated_at)
            VALUES ($k, $v, $t)
            ON CONFLICT(key) DO UPDATE SET value = $v, updated_at = $t;
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$k", KindPrefix + channelId);
                cmd.Parameters.AddWithValue("$v", kindRaw);
                cmd.Parameters.AddWithValue("$t", SqliteStore.Iso(switchedUtc));
            });
        _store.Execute(
            """
            INSERT INTO app_settings (key, value, updated_at)
            VALUES ($k, $v, $t)
            ON CONFLICT(key) DO UPDATE SET value = $v, updated_at = $t;
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$k", KindPrefix + channelId + AtSuffix);
                cmd.Parameters.AddWithValue("$v", SqliteStore.Iso(switchedUtc));
                cmd.Parameters.AddWithValue("$t", SqliteStore.Iso(switchedUtc));
            });
    }

    /// <summary>刪除頻道的碼流偏好鍵（頻道刪除時呼叫；鍵不得被復用同一 ID 的新頻道繼承）。</summary>
    public static void DeleteForChannel(SqliteStore store, long channelId)
    {
        store.Execute(
            "DELETE FROM app_settings WHERE key = $k OR key = $ka;",
            cmd =>
            {
                cmd.Parameters.AddWithValue("$k", KindPrefix + channelId);
                cmd.Parameters.AddWithValue("$ka", KindPrefix + channelId + AtSuffix);
            });
    }
}
