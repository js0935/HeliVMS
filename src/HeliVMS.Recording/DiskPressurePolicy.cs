namespace HeliVMS.Recording;

/// <summary>
/// 磁碟空間壓力等級（§11.1 磁碟滿策略三階段）。
///
/// <para>
/// 三階段策略的第一階段是「預警」：在使用率超過 85% 時就要讓維運知道，而不是等到寫不進去
/// 才發現。在此之前系統對磁碟只剩兩個狀態——「還能錄」與「已經爆了」——而爆掉的那一刻
/// 症狀是 ffmpeg 每段都失敗、退避到 5 分鐘一次，畫面照常，log 乾淨。
/// </para>
/// </summary>
public enum DiskPressureLevel
{
    /// <summary>正常。</summary>
    Normal,

    /// <summary>預警：已用超過 <see cref="DiskPressurePolicy.WarnUsedFraction"/>。</summary>
    Warning,

    /// <summary>危急：可用空間已低於寫入門檻（<c>recording.min_free_bytes</c>）。</summary>
    Critical,
}

/// <summary>
/// 磁碟滿策略的純決策（§11.1）。刻意不碰 I/O：把容量與門檻當輸入，等級當輸出，
/// 才能在單元測試裡固定磁碟組態，不必真的把一顆碟灌滿。
/// </summary>
public static class DiskPressurePolicy
{
    /// <summary>預警門檻：已用率達此值即進入 <see cref="DiskPressureLevel.Warning"/>。</summary>
    public const double WarnUsedFraction = 0.85;

    /// <summary>已用率（0–1）；容量未知（≤0）時回 0，不誤報。</summary>
    public static double UsedFraction(long totalBytes, long freeBytes)
    {
        if (totalBytes <= 0)
        {
            return 0;
        }

        var used = totalBytes - Math.Max(0, freeBytes);
        if (used < 0)
        {
            used = 0;
        }

        return (double)used / totalBytes;
    }

    /// <summary>
    /// 單一磁碟的壓力等級。
    /// <paramref name="criticalFreeBytes"/> ≤ 0 表示未設寫入門檻，此時不會進入
    /// <see cref="DiskPressureLevel.Critical"/>（等於停用最後一階段）。
    /// </summary>
    public static DiskPressureLevel Evaluate(long totalBytes, long freeBytes, long criticalFreeBytes)
    {
        if (totalBytes <= 0)
        {
            // 容量未知不代表有問題：寧可回報正常，也不要對一座取不到磁碟資訊的站台反覆誤報。
            return DiskPressureLevel.Normal;
        }

        if (freeBytes <= criticalFreeBytes)
        {
            return DiskPressureLevel.Critical;
        }

        return UsedFraction(totalBytes, freeBytes) >= WarnUsedFraction
            ? DiskPressureLevel.Warning
            : DiskPressureLevel.Normal;
    }

    /// <summary>
    /// 多顆磁碟中<b>最嚴重</b>的等級（供回報／告警）：只要任一顆超過 85% 即為
    /// <see cref="DiskPressureLevel.Warning"/>，任一顆低於門檻即為
    /// <see cref="DiskPressureLevel.Critical"/>。空集合回正常。
    /// </summary>
    public static DiskPressureLevel EvaluateOverall(
        IEnumerable<(long TotalBytes, long FreeBytes)> drives,
        long criticalFreeBytes)
    {
        ArgumentNullException.ThrowIfNull(drives);

        var worst = DiskPressureLevel.Normal;
        foreach (var drive in drives)
        {
            worst = Max(worst, Evaluate(drive.TotalBytes, drive.FreeBytes, criticalFreeBytes));
        }

        return worst;
    }

    /// <summary>
    /// 是否<b>每一顆</b>磁碟都低於寫入門檻——亦即「已經沒有地方可以寫新錄影」。
    ///
    /// <para>
    /// 這才是「停新錄」的判準（§11.1 第三階段）：只要還有一顆磁碟寫得下，錄影就不該被擋。
    /// 與 <see cref="EvaluateOverall"/> 刻意分開是為了不讓多磁碟站台因為某一顆滿了，
    /// 就在還有空間的磁碟上停掉錄影。空集合回 <c>false</c>（沒有磁碟資訊就不擋）。
    /// </para>
    /// </summary>
    public static bool AllCritical(
        IEnumerable<(long TotalBytes, long FreeBytes)> drives,
        long criticalFreeBytes)
    {
        ArgumentNullException.ThrowIfNull(drives);

        var any = false;
        foreach (var drive in drives)
        {
            any = true;
            if (Evaluate(drive.TotalBytes, drive.FreeBytes, criticalFreeBytes) != DiskPressureLevel.Critical)
            {
                return false;
            }
        }

        return any;
    }

    private static DiskPressureLevel Max(DiskPressureLevel a, DiskPressureLevel b) => a >= b ? a : b;
}
