namespace HeliVMS.Shared.Models;

/// <summary>
/// 錄影排程（M10 §錄影排程）：頻道在星期遮罩（bit0＝週日…bit6＝週六）與每日時段內自動錄影。
/// 時間以「本地分鐘」表示（0..1439）；<see cref="EndMinute"/> 小於 <see cref="StartMinute"/> 表示跨越午夜。
/// </summary>
public sealed record RecordingScheduleRecord
{
    public long Id { get; init; }

    public int ChannelId { get; init; }

    /// <summary>星期遮罩：bit 依 <see cref="DayOfWeek"/>（Sun=0 … Sat=6）。</summary>
    public int DaysMask { get; init; }

    /// <summary>每日開始分鐘（本地）。</summary>
    public int StartMinute { get; init; }

    /// <summary>每日結束分鐘（本地；小於開始分鐘＝跨午夜）。</summary>
    public int EndMinute { get; init; }

    public bool Enabled { get; init; }

    /// <summary>該排程於本地時刻是否處於錄影時段（含跨午夜與星期遮罩）。</summary>
    public bool IsActiveAt(DateTime localNow)
    {
        if (!Enabled)
        {
            return false;
        }

        var cur = localNow.Hour * 60 + localNow.Minute;
        var dayBit = 1 << (int)localNow.DayOfWeek;
        if (StartMinute <= EndMinute)
        {
            return (dayBit & DaysMask) != 0 && cur >= StartMinute && cur <= EndMinute;
        }

        if (cur >= StartMinute)
        {
            return (dayBit & DaysMask) != 0;
        }

        var yesterday = (int)localNow.DayOfWeek > 0 ? (int)localNow.DayOfWeek - 1 : 6;
        return cur <= EndMinute && ((1 << yesterday) & DaysMask) != 0;
    }
}