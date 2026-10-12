namespace HeliVMS.Licensing.Tests;

/// <summary>
/// <c>HmacLicenseKey</c>（<c>AAAA-BBBB-CCCC-DDDD</c>，Tools/LicenseKeyGen 產製）的行為測試。
///
/// 這些測試全部把 <c>deviceCode</c> 當參數注入，不碰 <see cref="MachineIdProvider"/>：
/// 機器碼在 CI 上每台 runner 都不同，依賴它等於讓測試只在某台機器上通過。
/// </summary>
public class HmacLicenseKeyTests
{
    /// <summary>
    /// 固定的 32 碼機器碼，避免測試結果依賴執行機器。
    /// </summary>
    private const string DeviceCode = "C2BA8089136FC38F6750F47464C88B44";

    private const string OtherDeviceCode = "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF";

    private static readonly DateTime NowUtc = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);

    private static string Create(
        int cameras = 32,
        int tier = 3,
        int licensee = 0,
        DateOnly? expires = null,
        string deviceCode = DeviceCode)
    {
        Assert.True(
            HmacLicenseKey.TryCreate(cameras, tier, licensee, expires, deviceCode, out var key, out var error),
            error);
        return key;
    }

    [Fact]
    public void 簽發的金鑰格式為四組四碼十六進位()
    {
        var key = Create();

        Assert.Equal(19, key.Length);
        var groups = key.Split('-');
        Assert.Equal(4, groups.Length);
        Assert.All(groups, g => Assert.Equal(4, g.Length));
        Assert.All(groups, g => Assert.All(g, c => Assert.True(Uri.IsHexDigit(c))));
    }

    [Fact]
    public void 簽發的金鑰可通過驗證並帶出通道數與等級功能()
    {
        var key = Create(cameras: 64, tier: LicenseTiers.All.Count - 1);

        var state = HmacLicenseKey.Validate(key, DeviceCode, NowUtc);

        Assert.Equal(LicenseStatus.Valid, state.Status);
        Assert.Null(state.Message);
        Assert.NotNull(state.Payload);
        Assert.Equal(64, state.Payload.Cameras);
        Assert.Equal(LicenseTiers.All[^1].Features, state.Payload.Features);
        Assert.True(state.IsValid);
    }

    [Fact]
    public void 永久授權沒有到期時間且任何時間都有效()
    {
        var key = Create(expires: null);

        Assert.Null(HmacLicenseKey.Validate(key, DeviceCode, NowUtc).Payload?.ExpiresUtc);
        Assert.Equal(
            LicenseStatus.Valid,
            HmacLicenseKey.Validate(key, DeviceCode, NowUtc.AddYears(50)).Status);
    }

    [Fact]
    public void 到期日當天結束前仍然有效()
    {
        var key = Create(expires: new DateOnly(2027, 6, 30));

        var state = HmacLicenseKey.Validate(key, DeviceCode, new DateTime(2027, 6, 30, 23, 59, 0, DateTimeKind.Utc));

        Assert.Equal(LicenseStatus.Valid, state.Status);
        Assert.Equal(
            new DateTime(2027, 6, 30, 23, 59, 59, DateTimeKind.Utc),
            state.Payload?.ExpiresUtc);
    }

    [Fact]
    public void 過了到期日之後授權視為過期()
    {
        var key = Create(expires: new DateOnly(2027, 6, 30));

        var state = HmacLicenseKey.Validate(key, DeviceCode, new DateTime(2027, 7, 1, 0, 0, 1, DateTimeKind.Utc));

        Assert.Equal(LicenseStatus.Expired, state.Status);
        Assert.NotNull(state.Message);
    }

    /// <summary>
    /// 這條是整個改動的目標症狀：過去產品端沒有 HMAC 路徑，所有 HMAC 金鑰都落到
    /// RSA 驗證而顯示「簽章驗證失敗」，等於簽發出去的金鑰完全不能用。
    /// </summary>
    [Fact]
    public void 產品端驗證LicenseKeyGen金鑰不會回報簽章驗證失敗()
    {
        var key = Create();

        var state = new LicenseManager().Validate(key, NowUtc);

        // deviceCode 由 MachineIdProvider 決定，所以只斷言不是簽章失敗／不是無效格式，
        // 機器綁定是否通過留給上面的注入式測試。
        Assert.NotEqual(LicenseStatus.Tampered, state.Status);
        Assert.NotEqual(LicenseStatus.Invalid, state.Status);
    }

    [Fact]
    public void 竄改通道數會被MAC擋下而不是安靜取得更多路數()
    {
        var key = Create(cameras: 8);
        var bytes = Convert.FromHexString(key.Replace("-", string.Empty));
        bytes[0] = 200;
        var tampered = HmacLicenseKey.Format(bytes);

        var state = HmacLicenseKey.Validate(tampered, DeviceCode, NowUtc);

        Assert.Equal(LicenseStatus.Tampered, state.Status);
        Assert.Equal(HmacLicenseKey.TamperedMessage, state.Message);
    }

    [Theory]
    [InlineData(1)]  // typeId
    [InlineData(2)]  // yearsSince2025
    [InlineData(3)]  // month
    [InlineData(4)]  // day
    [InlineData(5)]  // mac
    public void 任何一個受保護的位元組被改動都會被判定為遭竄改(int index)
    {
        var key = Create(cameras: 32, tier: 3, expires: new DateOnly(2027, 6, 30));
        var bytes = Convert.FromHexString(key.Replace("-", string.Empty));
        bytes[index] ^= 0xFF;

        var state = HmacLicenseKey.Validate(HmacLicenseKey.Format(bytes), DeviceCode, NowUtc);

        Assert.Equal(LicenseStatus.Tampered, state.Status);
    }

    [Fact]
    public void 別台機器的金鑰在本機會被判定為機器不符()
    {
        var key = Create(deviceCode: OtherDeviceCode);

        var state = HmacLicenseKey.Validate(key, DeviceCode, NowUtc);

        Assert.Equal(LicenseStatus.MachineMismatch, state.Status);
    }

    [Fact]
    public void 機器碼取不到時一律拒絕而不是放行()
    {
        var key = Create();

        // WMI 被停用時 MachineIdProvider 會回 "UNKNOWN"，此時若放行等於讓任何
        // 機器取得任意授權，所以必須 fail-closed。
        Assert.Equal(
            LicenseStatus.Invalid,
            HmacLicenseKey.Validate(key, "UNKNOWN", NowUtc).Status);
        Assert.Equal(
            LicenseStatus.Invalid,
            HmacLicenseKey.Validate(key, string.Empty, NowUtc).Status);
        Assert.Equal(
            LicenseStatus.Invalid,
            HmacLicenseKey.Validate(key, "ZZZZ8089136FC38F6750F47464C88B44", NowUtc).Status);
    }

    [Fact]
    public void 空白金鑰回報未提供而非簽章失敗()
    {
        Assert.Equal(LicenseStatus.NotPresent, HmacLicenseKey.Validate(null, DeviceCode, NowUtc).Status);
        Assert.Equal(LicenseStatus.NotPresent, HmacLicenseKey.Validate("   ", DeviceCode, NowUtc).Status);
    }

    [Theory]
    [InlineData("HELVMS-v2.abc.def")]
    [InlineData("NOT-A-KEY")]
    [InlineData("AAAA-BBBB-CCCC")]
    [InlineData("AAAA-BBBB-CCCC-DDDD-EEEE")]
    [InlineData("GGGG-BBBB-CCCC-DDDD")]
    public void 非本格式的字串不會被當成HMAC金鑰(string token)
    {
        Assert.False(HmacLicenseKey.IsKeyFormat(token));
    }

    [Fact]
    public void 分隔線可省略或帶空白仍能解碼()
    {
        var key = Create();
        var compact = key.Replace("-", string.Empty);

        Assert.Equal(LicenseStatus.Valid, HmacLicenseKey.Validate(compact, DeviceCode, NowUtc).Status);
        Assert.Equal(LicenseStatus.Valid, HmacLicenseKey.Validate(key.ToLowerInvariant(), DeviceCode, NowUtc).Status);
        Assert.Equal(LicenseStatus.Valid, HmacLicenseKey.Validate($"  {key}  ", DeviceCode, NowUtc).Status);
    }

    [Fact]
    public void 授權編號由金鑰推導因此每次驗證都相同()
    {
        // 用 GUID 當授權編號會讓每次重啟都換一個 license_id，稽核紀錄因此斷裂。
        var key = Create();

        var first = HmacLicenseKey.Validate(key, DeviceCode, NowUtc).Payload?.Id;
        var second = HmacLicenseKey.Validate(key, DeviceCode, NowUtc.AddDays(1)).Payload?.Id;

        Assert.Equal(first, second);
        Assert.NotNull(first);
    }

    [Fact]
    public void 等級索引超出產品支援範圍時拒絕而不是退到較低等級()
    {
        // MAC 通過但 typeId = 15，表示簽發端比產品端新。默默退到某個等級會讓客戶
        // 拿到比預期低的權限卻不知情。
        var bytes = Convert.FromHexString(Create().Replace("-", string.Empty));
        bytes[1] = 0xF0;
        bytes[5] = HmacLicenseKey.ComputeMac(bytes.AsSpan(0, 5), DeviceCode[..4]);
        bytes[6] = Convert.ToByte(DeviceCode[..2], 16);
        bytes[7] = Convert.ToByte(DeviceCode[2..4], 16);

        var state = HmacLicenseKey.Validate(HmacLicenseKey.Format(bytes), DeviceCode, NowUtc);

        Assert.Equal(LicenseStatus.Invalid, state.Status);
        Assert.Contains("更新產品版本", state.Message);
    }

    [Fact]
    public void 通道數為零的金鑰會被拒絕()
    {
        var bytes = Convert.FromHexString(Create().Replace("-", string.Empty));
        bytes[0] = 0;
        bytes[5] = HmacLicenseKey.ComputeMac(bytes.AsSpan(0, 5), DeviceCode[..4]);
        bytes[6] = Convert.ToByte(DeviceCode[..2], 16);
        bytes[7] = Convert.ToByte(DeviceCode[2..4], 16);

        Assert.Equal(
            LicenseStatus.Invalid,
            HmacLicenseKey.Validate(HmacLicenseKey.Format(bytes), DeviceCode, NowUtc).Status);
    }

    [Fact]
    public void 簽發時拒絕超出範圍的參數()
    {
        Assert.False(HmacLicenseKey.TryCreate(0, 0, 0, null, DeviceCode, out _, out var tooFew));
        Assert.Contains("通道數", tooFew);

        Assert.False(HmacLicenseKey.TryCreate(256, 0, 0, null, DeviceCode, out _, out var tooMany));
        Assert.Contains("通道數", tooMany);

        Assert.False(HmacLicenseKey.TryCreate(8, LicenseTiers.All.Count, 0, null, DeviceCode, out _, out var badTier));
        Assert.Contains("等級", badTier);

        Assert.False(HmacLicenseKey.TryCreate(8, 0, 0, new DateOnly(2024, 12, 31), DeviceCode, out _, out var pastYear));
        Assert.Contains("到期年份", pastYear);
    }

    [Fact]
    public void 到期日為二月三十日會被夾成該月最後一天()
    {
        // 簽發端不該產生這種日期，但手改位元組再加上合法 MAC 是做得到的，
        // 驗證端若直接 new DateTime() 會丟例外而不是回一個明確狀態。
        var bytes = Convert.FromHexString(Create().Replace("-", string.Empty));
        bytes[2] = 2;   // 2027
        bytes[3] = 2;   // February
        bytes[4] = 30;  // 不存在
        bytes[5] = HmacLicenseKey.ComputeMac(bytes.AsSpan(0, 5), DeviceCode[..4]);
        bytes[6] = Convert.ToByte(DeviceCode[..2], 16);
        bytes[7] = Convert.ToByte(DeviceCode[2..4], 16);

        var state = HmacLicenseKey.Validate(HmacLicenseKey.Format(bytes), DeviceCode, NowUtc);

        Assert.Equal(LicenseStatus.Valid, state.Status);
        Assert.Equal(new DateTime(2027, 2, 28, 23, 59, 59, DateTimeKind.Utc), state.Payload?.ExpiresUtc);
    }

    /// <summary>
    /// 把 <see cref="HmacLicenseKey"/> 的位元組配置釘住。
    /// 若有人調整欄位順序或 MAC 輸入，這裡會先壞掉；這是刻意的——舊版就是靠註解
    /// 「keep in sync」而實際漂移過。
    /// </summary>
    [Fact]
    public void 位元組配置維持不變()
    {
        var key = Create(cameras: 0x2A, tier: 3, licensee: 1, expires: new DateOnly(2027, 6, 30));
        var bytes = Convert.FromHexString(key.Replace("-", string.Empty));

        Assert.Equal(0x2A, bytes[0]);                       // [0] maxCameras
        Assert.Equal(0x31, bytes[1]);                       // [1] typeId=3 | licenseeId=1
        Assert.Equal(2027 - HmacLicenseKey.ExpiryEpochYear, bytes[2]); // [2] yearsSince2025
        Assert.Equal(6, bytes[3]);                          // [3] month
        Assert.Equal(30, bytes[4]);                         // [4] day
        Assert.Equal(Convert.ToByte(DeviceCode[..2], 16), bytes[6]); // [6..7] device prefix
        Assert.Equal(Convert.ToByte(DeviceCode[2..4], 16), bytes[7]);
    }

    /// <summary>
    /// 簽發端與驗證端共用同一份程式碼，所以「改掉 HMAC 密鑰」不會造成兩邊不一致——
    /// 它會安靜地讓<b>所有已簽發出去的金鑰同時失效</b>，而現場沒有任何編譯錯誤可指認。
    /// 把簽發結果釘成 golden vector，密鑰一被改就立刻紅燈。
    /// </summary>
    [Fact]
    public void 簽發結果維持不變()
    {
        Assert.Equal(
            "2030-0206-1E8A-C2BA",
            Create(cameras: 32, tier: 3, licensee: 0, expires: new DateOnly(2027, 6, 30)));
    }
}