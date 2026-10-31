namespace HeliVMS.Recording;

/// <summary>
/// 「每小時合併」的共用設定（<c>recording.hourly_merge</c>）。
///
/// <para>
/// 動機：錄影以 60–600 秒一段落盤，每台每天約 288 個檔（預設 300 秒）。備份／同步、
/// SMB 瀏覽、防毒掃描都在掃這些小檔。超過一小時前的段已經穩定（不再被寫入），把
/// 「同一個小時資料夾裡的完整小時」重新封裝成單一檔案，每天每台 288 檔 → 24 檔。
/// </para>
/// <para>
/// 預設關閉：合併會改寫磁碟上的檔案與索引列，屬於資料重整；讓操作員自己決定要不要。
/// </para>
/// </summary>
public static class HourlyMergePolicy
{
    /// <summary>設定鍵；值為 "1"（啟用）或 "0"（停用）。</summary>
    public const string SettingKey = "recording.hourly_merge";

    /// <summary>預設停用：合併會重寫檔案，交給操作員開。</summary>
    public const bool DefaultEnabled = false;

    /// <summary>小時結束後至少要經過這麼久才會被合併：合併中的小時絕不能還在被錄。</summary>
    public const double MinAgeHours = 1.0;

    /// <summary>單輪最多合併幾個小時：remux 以磁碟速度進行，限制輪次成本可預期。</summary>
    public const int MaxHoursPerSweep = 4;

    /// <summary>往前找多久的小時：超過這個距離的歷史視為已被處理或不值得再掃。</summary>
    public const int MaxLookbackDays = 14;

    /// <summary>從共用設定讀取；未設定或無法解析時回 <see cref="DefaultEnabled"/>。</summary>
    public static bool Read(HeliVMS.Storage.SqliteStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        var raw = new HeliVMS.Storage.SettingsRepository(store).Get(SettingKey);
        return raw switch
        {
            "1" or "true" or "True" => true,
            "0" or "false" or "False" => false,
            _ => DefaultEnabled,
        };
    }
}
