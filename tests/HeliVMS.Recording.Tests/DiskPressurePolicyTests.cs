namespace HeliVMS.Recording.Tests;

/// <summary>
/// 磁碟滿策略的等級判定（M319，§11.1）。
///
/// <para>
/// 這是純函式，測試把容量與門檻當輸入固定住，不必真的把磁碟灌滿——灌滿一顆碟來測這種邏輯
/// 既不實際，也無法測到「剛好 85%」這種邊界。門檻錯了不會有任何錯誤訊息，只會是「磁碟爆了
/// 卻沒預警」或「明明還有空間卻被停錄」。
/// </para>
/// </summary>
public sealed class DiskPressurePolicyTests
{
    private const long GiB = 1024L * 1024 * 1024;

    /// <summary>容量未知時回正常，不對取不到磁碟資訊的站台反覆誤報。</summary>
    [Fact]
    public void 容量未知時回報正常()
        => Assert.Equal(DiskPressureLevel.Normal, DiskPressurePolicy.Evaluate(0, 0, 2 * GiB));

    /// <summary>已用剛好 85% 即進入預警（門檻是「達」不是「超過」）。</summary>
    [Fact]
    public void 已用達85即為預警()
        => Assert.Equal(DiskPressureLevel.Warning, DiskPressurePolicy.Evaluate(100, 15, 0));

    /// <summary>已用未達 85% 仍為正常——預警不能提前到處都是。</summary>
    [Fact]
    public void 已用未達85仍為正常()
        => Assert.Equal(DiskPressureLevel.Normal, DiskPressurePolicy.Evaluate(100, 16, 0));

    /// <summary>可用空間低於寫入門檻即為危急，優先於 85% 的判斷。</summary>
    [Fact]
    public void 可用空間低於寫入門檻時為危急()
        => Assert.Equal(DiskPressureLevel.Critical, DiskPressurePolicy.Evaluate(100 * GiB, 1 * GiB, 2 * GiB));

    /// <summary>可用空間剛好等於門檻即算危急（寫入門檻是「保留至少這麼多」，等於時已無緩衝）。</summary>
    [Fact]
    public void 可用空間等於門檻時為危急()
        => Assert.Equal(DiskPressureLevel.Critical, DiskPressurePolicy.Evaluate(100 * GiB, 2 * GiB, 2 * GiB));

    /// <summary>
    /// 門檻設 0 時，只有完全寫滿（可用 0）才算危急：這是否定第三階段停新錄的既有手段
    /// （<c>recording.min_free_bytes</c> 設 0），不能讓它變成「一律危急」。
    /// </summary>
    [Fact]
    public void 門檻設零時只有完全寫滿才危急()
    {
        // 門檻 0、可用 1GiB：遠超過 85% 故為預警，但「還有空間可寫」故不是危急。
        Assert.Equal(DiskPressureLevel.Warning, DiskPressurePolicy.Evaluate(100 * GiB, 1 * GiB, 0));
        Assert.Equal(DiskPressureLevel.Critical, DiskPressurePolicy.Evaluate(100 * GiB, 0, 0));
    }

    /// <summary>多顆磁碟回報最嚴重的那顆：任一顆超過 85% 就要預警。</summary>
    [Fact]
    public void 多顆磁碟回報最嚴重的一顆()
    {
        var drives = new (long, long)[] { (100 * GiB, 90 * GiB), (100 * GiB, 1 * GiB) };
        Assert.Equal(DiskPressureLevel.Warning, DiskPressurePolicy.EvaluateOverall(drives, 0));
    }

    /// <summary>空集合回正常（沒有磁碟資訊不誤報）。</summary>
    [Fact]
    public void 沒有磁碟時回報正常()
        => Assert.Equal(DiskPressureLevel.Normal, DiskPressurePolicy.EvaluateOverall([], 2 * GiB));

    /// <summary>
    /// 只要還有一顆磁碟高於門檻，就不算全部危急——多磁碟站台不該因某一顆滿了就停掉全部錄影。
    /// </summary>
    [Fact]
    public void 只有部分磁碟危急時不視為全面危急()
    {
        var drives = new (long, long)[] { (100 * GiB, 1 * GiB), (100 * GiB, 50 * GiB) };
        Assert.False(DiskPressurePolicy.AllCritical(drives, 2 * GiB));
    }

    /// <summary>每一顆都低於門檻時才算全面危急（停新錄的判準）。</summary>
    [Fact]
    public void 全部磁碟都低於門檻時為全面危急()
    {
        var drives = new (long, long)[] { (100 * GiB, 1 * GiB), (100 * GiB, 0) };
        Assert.True(DiskPressurePolicy.AllCritical(drives, 2 * GiB));
    }

    /// <summary>空集合不算全面危急（沒有磁碟資訊不擋錄影）。</summary>
    [Fact]
    public void 沒有磁碟時不算全面危急()
        => Assert.False(DiskPressurePolicy.AllCritical([], 2 * GiB));
}
