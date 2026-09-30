namespace HeliVMS.Storage;

/// <summary>錄影閘門的觸發來源（寫入稽核供追查是哪條路徑嘗試開錄）。</summary>
public static class RecordingGateSources
{
    /// <summary>人工按下「錄影」。</summary>
    public const string Manual = "manual";

    /// <summary>排程器依 recording_schedule 啟動。</summary>
    public const string Schedule = "schedule";
}