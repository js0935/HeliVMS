using HeliVMS.Storage;

namespace HeliVMS.Storage.Tests;

/// <summary>
/// 磁碟健康的判斷與 S.M.A.R.T. 解析（<see cref="DiskHealth"/>）。
/// </summary>
/// <remarks>
/// <para>
/// 這一支回答的是「碟要壞了」，與 <c>DiskPressurePolicy</c> 的「碟滿了」是兩件事——兩者都會讓
/// 錄影無聲消失，但處置完全相反（換碟 vs 清檔）。
/// </para>
/// <para>
/// 最貴的錯誤是把<b>未知講成健康</b>：不是管理員、控制器不外露 S.M.A.R.T.、RAID/USB 橋接後面
/// 看不到實體碟，畫面都會一片綠，而硬碟已經在重新配置磁區。所以「未知」與「健康」被逐一釘開。
/// </para>
/// </remarks>
public class DiskHealthTests
{
    [Fact]
    public void 沒有取樣時是未知而不是健康()
    {
        Assert.Equal(DiskHealthLevel.Unknown, DiskHealth.Evaluate(null));
        Assert.Equal(DiskHealthLevel.Unknown, DiskHealth.Evaluate(new SmartAttributes(null, null, null, null, null)));
    }

    [Fact]
    public void 控制器說沒有預測故障時算健康()
    {
        // 只拿得到 FailurePredictStatus（常見於非管理員或控制器不外露屬性）時，
        // 「沒有預測故障」仍然是作業系統給的健康聲明。
        Assert.Equal(DiskHealthLevel.Healthy, DiskHealth.Evaluate(new SmartAttributes(false, null, null, null, null)));
    }

    [Fact]
    public void 預測故障直接判定即將故障()
    {
        var sample = new SmartAttributes(
            PredictFailure: true,
            ReallocatedSectors: 0,
            PendingSectors: 0,
            TemperatureCelsius: 30,
            PowerOnHours: 1000);

        Assert.Equal(DiskHealthLevel.Failing, DiskHealth.Evaluate(sample));
    }

    [Fact]
    public void 有待重新配置磁區視為即將故障()
    {
        // 待重新配置＝讀取失敗、等重映射；資料已經在流失，不是「有點壞」。
        var sample = new SmartAttributes(false, ReallocatedSectors: 0, PendingSectors: 2, TemperatureCelsius: 33, PowerOnHours: 500);

        Assert.Equal(DiskHealthLevel.Failing, DiskHealth.Evaluate(sample));
    }

    [Fact]
    public void 已重新配置磁區或高溫是警告()
    {
        var reallocated = new SmartAttributes(false, ReallocatedSectors: 8, PendingSectors: 0, TemperatureCelsius: 35, PowerOnHours: 9000);
        var hot = new SmartAttributes(false, ReallocatedSectors: 0, PendingSectors: 0, TemperatureCelsius: DiskHealth.WarningTemperatureCelsius, PowerOnHours: 9000);

        Assert.Equal(DiskHealthLevel.Warning, DiskHealth.Evaluate(reallocated));
        Assert.Equal(DiskHealthLevel.Warning, DiskHealth.Evaluate(hot));
    }

    [Fact]
    public void 一切正常時才是健康()
    {
        var sample = new SmartAttributes(false, ReallocatedSectors: 0, PendingSectors: 0, TemperatureCelsius: 34, PowerOnHours: 12000);

        Assert.Equal(DiskHealthLevel.Healthy, DiskHealth.Evaluate(sample));
    }

    [Fact]
    public void 取最嚴重的一顆且未知不掩蓋已知()
    {
        Assert.Equal(
            DiskHealthLevel.Failing,
            DiskHealth.Worst([DiskHealthLevel.Unknown, DiskHealthLevel.Healthy, DiskHealthLevel.Failing]));

        Assert.Equal(
            DiskHealthLevel.Warning,
            DiskHealth.Worst([DiskHealthLevel.Unknown, DiskHealthLevel.Healthy, DiskHealthLevel.Warning]));

        // 全部未知才是未知（一顆讀不到不該讓整站的結論變成未知）。
        Assert.Equal(DiskHealthLevel.Unknown, DiskHealth.Worst([DiskHealthLevel.Unknown, DiskHealthLevel.Unknown]));
        Assert.Equal(DiskHealthLevel.Healthy, DiskHealth.Worst([DiskHealthLevel.Unknown, DiskHealthLevel.Healthy]));
    }

    [Fact]
    public void 解析ATA屬性區取得重新配置待配置溫度與通電時數()
    {
        var blob = BuildAtaBlob(new Dictionary<byte, int>
        {
            [5] = 8,        // 重新配置磁區
            [9] = 12345,    // 通電時數
            [194] = 36,     // 溫度（°C，取最低位元組）
            [197] = 2,      // 待重新配置
        });

        var parsed = DiskHealth.ParseAtaSmart(blob);

        Assert.NotNull(parsed);
        Assert.Equal(8, parsed!.Value.ReallocatedSectors);
        Assert.Equal(12345, parsed.Value.PowerOnHours);
        Assert.Equal(36, parsed.Value.TemperatureCelsius);
        Assert.Equal(2, parsed.Value.PendingSectors);

        // 解析出來的東西要真的影響判定（否則解析對了也沒用）。
        Assert.Equal(DiskHealthLevel.Failing, DiskHealth.Evaluate(parsed));
    }

    [Fact]
    public void 長度不足或全為空槽時回未知而不是零()
    {
        Assert.Null(DiskHealth.ParseAtaSmart(null));
        Assert.Null(DiskHealth.ParseAtaSmart(new byte[10]));
        Assert.Null(DiskHealth.ParseAtaSmart(new byte[512]));

        // 長度夠但一項都沒解析到：也要回 null（回一個全 0 的物件會被讀成「一切正常」）。
        var attributes = DiskHealth.ParseAtaSmart(new byte[512]);
        Assert.Null(attributes);
    }

    [Fact]
    public void 控制器的故障預測會併進解析結果()
    {
        var blob = BuildAtaBlob(new Dictionary<byte, int> { [194] = 40 });
        var parsed = DiskHealth.ParseAtaSmart(blob);
        Assert.NotNull(parsed);

        var merged = DiskHealth.WithPredictFailure(parsed!.Value, predictFailure: true);

        Assert.True(merged.PredictFailure);
        Assert.Equal(40, merged.TemperatureCelsius);
        Assert.Equal(DiskHealthLevel.Failing, DiskHealth.Evaluate(merged));

        // 沒併進來時維持原值（null＝這項讀不到）。
        Assert.Null(parsed.Value.PredictFailure);
    }

    [Fact]
    public void 等級都對應到一個三語文案鍵()
    {
        foreach (var level in Enum.GetValues<DiskHealthLevel>())
        {
            var key = DiskHealth.LevelKey(level);
            Assert.StartsWith("Settings.DiskHealth", key, StringComparison.Ordinal);
        }

        // 四個等級各一個鍵（漏一個就會有兩個等級顯示同一句話）。
        var keys = Enum.GetValues<DiskHealthLevel>().Select(DiskHealth.LevelKey).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(4, keys.Count);
    }

    /// <summary>
    /// 造一個符合 ATA 版型的屬性區：前 2 bytes 版本，之後 30 項 × 12 bytes
    /// （id, flags×2, 目前值, 最差值, 原始值×6 小端）。
    /// </summary>
    private static byte[] BuildAtaBlob(IReadOnlyDictionary<byte, int> attributes)
    {
        var blob = new byte[512];
        blob[0] = 0x10;   // 版本（不影響解析）

        var slot = 0;
        foreach (var (id, raw) in attributes)
        {
            var offset = 2 + (slot * 12);
            blob[offset] = id;
            blob[offset + 3] = 100;   // 目前值
            blob[offset + 4] = 100;   // 最差值
            blob[offset + 8] = (byte)(raw & 0xFF);
            blob[offset + 9] = (byte)((raw >> 8) & 0xFF);
            blob[offset + 10] = (byte)((raw >> 16) & 0xFF);
            blob[offset + 11] = (byte)((raw >> 24) & 0xFF);
            slot++;
        }

        return blob;
    }
}
