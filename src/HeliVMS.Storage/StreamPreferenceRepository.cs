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

    public StreamPreferenceRepository(SqliteStore store) => _settings = new SettingsRepository(store);

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
    public void Save(long channelId, StreamKind kind, DateTime switchedUtc)
    {
        _settings.Set(KindPrefix + channelId, kind == StreamKind.Sub ? "sub" : "main");
        _settings.Set(KindPrefix + channelId + AtSuffix, SqliteStore.Iso(switchedUtc));
    }
}
