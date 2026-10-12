using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace HeliVMS.Licensing;

/// <summary>
/// <c>Tools/LicenseKeyGen</c> 授權金鑰（<c>AAAA-BBBB-CCCC-DDDD</c>）的單一共用實作。
///
/// <para>
/// 這裡是 HMAC 密鑰與位元組格式的<b>唯一權威來源</b>。簽發端
/// <c>Tools/LicenseKeyGen</c> 直接呼叫本類別產生金鑰，產品端
/// <see cref="LicenseManager.Validate"/> 直接呼叫本類別驗證金鑰。兩邊共用同一份程式碼，
/// 是為了讓舊版「簽發端與驗證端各自 hardcode 密鑰、靠註解提醒同步」的做法不會再漂移：
/// 舊 <c>// HMAC secret MUST match ... keep in sync</c> 註解在 repo 裡已經漂移過一次，
/// 導致產品端根本沒有 HMAC 驗證路徑，授權檔只能餵 RSA，於是每張 HMAC 金鑰都顯示
/// 「簽章驗證失敗」。
/// </para>
///
/// <para><b>位元組配置</b>（8 bytes → 16 進位 16 字元 → 以 4-4-4-4 顯示）：
/// <list type="bullet">
///   <item><c>[0]</c> maxCameras（1-255；0 無效）</item>
///   <item><c>[1]</c> typeId（高 4 索引 <see cref="LicenseTiers.All"/>） | licenseeId（低 4 索引）</item>
///   <item><c>[2]</c> yearsSince<see cref="ExpiryEpochYear"/>（0 = 永久）</item>
///   <item><c>[3]</c> month（1-12；<c>[2]</c> = 0 時忽略）</item>
///   <item><c>[4]</c> day（1-31；<c>[2]</c> = 0 時忽略）</item>
///   <item><c>[5]</c> HMAC-SHA256(<c>[0..4]</c> ‖ ASCII(devicePrefix[0..4]))[0]</item>
///   <item><c>[6..7]</c> devicePrefix 前 4 個 16 進位字元解讀成的兩個位元組</item>
/// </list>
/// </para>
///
/// <para><b>安全性質（刻意的取捨，勿當成意外）：</b>
/// 驗證端與簽發端共用對稱密鑰，而產品端必須內嵌這個密鑰才能離線驗證。
/// 取得 <c>HeliVMS.Licensing.dll</c> 的人（反編譯即可）能取出 <see cref="Secret"/>
/// 並自製任意金鑰。相較之下 HELVMS-v2 的 RSA 公鑰驗證無法 forging。
/// 選 HMAC 是因為離線、零相依、金鑰是 19 個字元好口頭傳遞；
/// 若要回復不可偽造性，必須改回非對稱簽章，見 <c>docs/ARCHITECTURE.md</c> §19.5。
/// </para>
/// </summary>
public static class HmacLicenseKey
{
    /// <summary>金鑰位元組長度。</summary>
    public const int ByteLength = 8;

    /// <summary>到期年份的計算基準年；<c>[2]</c> = 0 表示永久授權。</summary>
    public const int ExpiryEpochYear = 2025;

    /// <summary>MAC 驗證失敗的訊息，即使用者看到「金鑰無效」的原因。</summary>
    public const string TamperedMessage = "金鑰無效：簽章驗證失敗（內容遭竄改或金鑰不符）";

    /// <summary>
    /// HMAC-SHA256 密鑰，簽發端與驗證端共用。
    /// </summary>
    /// <remarks>
    /// 這是產品可離線驗證的代價，見本型別 remarks 的「安全性質」。若要輪替，
    /// 所有已發出的金鑰會同時失效，且舊密鑰必須保留一段遷移期才能讓客戶續用。
    /// </remarks>
    public static ReadOnlySpan<byte> Secret =>
    [
        0x1A, 0x2B, 0x3C, 0x4D, 0x5E, 0x6F, 0x70, 0x81,
        0x92, 0xA3, 0xB4, 0xC5, 0xD6, 0xE7, 0xF8, 0x09,
    ];

    /// <summary>
    /// 判斷字串是否為本格式的授權金鑰（而非 HELVMS-v2 的 <c>HELVMS-v2.{payload}.{sig}</c>）。
    /// </summary>
    /// <remarks>
    /// 刻意比 <see cref="TryDecode"/> 嚴格：分派時必須先確定格式，才能決定走
    /// HMAC 還是 RSA。放寬會讓格式判斷與解碼混在一起，產生「看起來像金鑰其實不是」
    /// 的分歧路徑。
    /// </remarks>
    public static bool IsKeyFormat([NotNullWhen(true)] string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        var trimmed = key.Trim();
        if (trimmed.StartsWith(LicenseSerializer.Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var groups = trimmed.Split('-', StringSplitOptions.RemoveEmptyEntries);
        return groups.Length == 4
            && groups.All(g => g.Length == 4 && g.All(Uri.IsHexDigit));
    }

    /// <summary>
    /// 把 <c>AAAA-BBBB-CCCC-DDDD</c> 解成 8 個位元組。分隔線可省略或含空白，方便使用者貼上。
    /// </summary>
    public static bool TryDecode(string? key, out byte[] bytes, [NotNullWhen(false)] out string? error)
    {
        bytes = [];
        error = null;

        if (string.IsNullOrWhiteSpace(key))
        {
            error = "未提供授權金鑰";
            return false;
        }

        var hex = new string([.. key.Where(c => c != '-' && !char.IsWhiteSpace(c))]).ToUpperInvariant();
        if (hex.Length != ByteLength * 2)
        {
            error = $"授權金鑰必須是 4 組、共 16 個 16 進位字元（例 AAAA-BBBB-CCCC-DDDD），實際收到 {hex.Length} 個";
            return false;
        }

        foreach (var c in hex)
        {
            if (!Uri.IsHexDigit(c))
            {
                error = "授權金鑰含有非 16 進位字元";
                return false;
            }
        }

        bytes = Convert.FromHexString(hex);
        return true;
    }

    /// <summary>
    /// 依 8 個位元組產生顯示用金鑰字串（<c>AAAA-BBBB-CCCC-DDDD</c>）。
    /// </summary>
    public static string Format(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != ByteLength)
        {
            throw new ArgumentException($"授權金鑰必須剛好 {ByteLength} 個位元組", nameof(bytes));
        }

        var hex = Convert.ToHexString(bytes);
        return $"{hex[..4]}-{hex[4..8]}-{hex[8..12]}-{hex[12..]}";
    }

    /// <summary>
    /// 計算 MAC：<c>HMAC-SHA256(header ‖ ASCII(devicePrefix[0..4]))[0]</c>。
    /// </summary>
    /// <param name="header">金鑰前 5 個位元組（maxCameras、typeId/licenseeId、years、month、day）。</param>
    /// <param name="devicePrefix4">機器碼前 4 個字元，以 ASCII 參與 MAC。</param>
    public static byte ComputeMac(ReadOnlySpan<byte> header, string devicePrefix4)
    {
        ArgumentNullException.ThrowIfNull(devicePrefix4);

        var take = Math.Min(4, devicePrefix4.Length);
        var input = new byte[header.Length + take];
        header.CopyTo(input);
        Encoding.ASCII.GetBytes(devicePrefix4, 0, take, input, header.Length);

        return HMACSHA256.HashData(Secret, input)[0];
    }

    /// <summary>
    /// 由簽發端參數組出金鑰位元組。
    /// </summary>
    /// <param name="maxCameras">通道數上限（1-255）。</param>
    /// <param name="tierIndex"><see cref="LicenseTiers.All"/> 的索引。</param>
    /// <param name="licenseeId">0 = 外部授權客戶、1 = 內部授權。</param>
    /// <param name="expiresOn">到期日；<c>null</c> 為永久授權。</param>
    /// <param name="deviceCode">目標機器碼（<see cref="MachineIdProvider.GetDeviceCode"/>）。</param>
    /// <param name="key">產出的 <c>AAAA-BBBB-CCCC-DDDD</c> 金鑰。</param>
    /// <param name="error">失敗原因；成功時為 null。</param>
    public static bool TryCreate(
        int maxCameras,
        int tierIndex,
        int licenseeId,
        DateOnly? expiresOn,
        string deviceCode,
        [NotNullWhen(true)] out string? key,
        [NotNullWhen(false)] out string? error)
    {
        key = null;
        error = null;

        if (maxCameras is < 1 or > 255)
        {
            error = $"通道數上限必須介於 1 ~ 255，收到 {maxCameras}";
            return false;
        }

        if (tierIndex < 0 || tierIndex >= LicenseTiers.All.Count)
        {
            error = $"授權等級索引必須介於 0 ~ {LicenseTiers.All.Count - 1}，收到 {tierIndex}";
            return false;
        }

        if (licenseeId is < 0 or > 15)
        {
            error = $"授權對象索引必須介於 0 ~ 15，收到 {licenseeId}";
            return false;
        }

        if (!TryReadDevicePrefix(deviceCode, out var prefix, out error))
        {
            return false;
        }

        var years = 0;
        var month = 0;
        var day = 0;
        if (expiresOn is { } expiry)
        {
            years = expiry.Year - ExpiryEpochYear;
            if (years is < 1 or > 255)
            {
                error = $"到期年份必須介於 {ExpiryEpochYear + 1} ~ {ExpiryEpochYear + 255}，收到 {expiry.Year}";
                return false;
            }

            month = expiry.Month;
            day = expiry.Day;
        }

        var bytes = new byte[ByteLength];
        bytes[0] = (byte)maxCameras;
        bytes[1] = (byte)((tierIndex << 4) | licenseeId);
        bytes[2] = (byte)years;
        bytes[3] = (byte)month;
        bytes[4] = (byte)day;
        bytes[5] = ComputeMac(bytes.AsSpan(0, 5), prefix);

        var hexPrefix = prefix.ToUpperInvariant();
        bytes[6] = Convert.ToByte(hexPrefix[..2], 16);
        bytes[7] = Convert.ToByte(hexPrefix[2..4], 16);

        key = Format(bytes);
        return true;
    }

    /// <summary>
    /// 驗證金鑰並轉成 <see cref="LicenseState"/>。
    /// </summary>
    /// <param name="key">使用者輸入的授權金鑰。</param>
    /// <param name="deviceCode">本機機器碼。</param>
    /// <param name="nowUtc">判斷到期用的現在時間。</param>
    /// <remarks>
    /// <paramref name="deviceCode"/> 由呼叫端注入而非本類別自行取用，這樣測試才能
    /// 驗證「別台機器的金鑰被拒」而不必真的搬動機器碼。
    /// </remarks>
    public static LicenseState Validate(string? key, string deviceCode, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return new LicenseState(LicenseStatus.NotPresent, null, "未找到授權檔");
        }

        if (!TryDecode(key, out var bytes, out var decodeError))
        {
            return new LicenseState(LicenseStatus.Invalid, null, decodeError);
        }

        // 機器碼取不到就無法確認金鑰屬於本機，一律 fail-closed。放行等於讓任何
        // WMI 失效的機器（例如 WMI 服務被停用）取得任意授權。
        if (!TryReadDevicePrefix(deviceCode, out var prefix, out var deviceError))
        {
            return new LicenseState(LicenseStatus.Invalid, null, deviceError);
        }

        var hexPrefix = prefix.ToUpperInvariant();
        if (!TryHexByte(hexPrefix.AsSpan(0, 2), out var localHigh)
            || !TryHexByte(hexPrefix.AsSpan(2, 2), out var localLow))
        {
            return new LicenseState(LicenseStatus.Invalid, null, "本機機器碼不是有效的 16 進位字串");
        }

        if (bytes[6] != localHigh || bytes[7] != localLow)
        {
            return new LicenseState(
                LicenseStatus.MachineMismatch,
                null,
                $"授權與本機不符（本機機器碼 {deviceCode}）");
        }

        // 先驗 MAC 再解讀內容：未驗證的位元組不能拿來決定權限。
        if (ComputeMac(bytes.AsSpan(0, 5), prefix) != bytes[5])
        {
            return new LicenseState(LicenseStatus.Tampered, null, TamperedMessage);
        }

        var maxCameras = bytes[0];
        if (maxCameras == 0)
        {
            return new LicenseState(LicenseStatus.Invalid, null, "授權通道數無效");
        }

        var tierIndex = (bytes[1] >> 4) & 0x0F;
        if (tierIndex >= LicenseTiers.All.Count)
        {
            // MAC 已通過卻超出等級範圍，代表簽發端版本比產品端新。fail-closed 並說清楚，
            // 不要默默退到某個等級，否則新等级的客戶會拿到較低的權限而不知情。
            return new LicenseState(
                LicenseStatus.Invalid,
                null,
                $"授權等級索引 {tierIndex} 超出產品支援範圍 0 ~ {LicenseTiers.All.Count - 1}，請更新產品版本");
        }

        DateTime? expiresUtc = null;
        var years = bytes[2];
        if (years > 0)
        {
            var year = ExpiryEpochYear + years;
            var month = (int)Math.Clamp((int)bytes[3], 1, 12);
            var day = (int)Math.Clamp((int)bytes[4], 1, DateTime.DaysInMonth(year, month));
            expiresUtc = new DateTime(year, month, day, 23, 59, 59, DateTimeKind.Utc);
        }

        var tier = LicenseTiers.All[tierIndex];
        var payload = new LicensePayload
        {
            // HMAC 格式沒有授權編號欄位。用金鑰本身當穩定識別碼，而不是每次驗證都
            // 產生新的 GUID，否則 license_id 每次重啟都變，稽核紀錄會斷成一堆碎片。
            Id = $"{LicenseSerializer.Prefix}-hmac-{Convert.ToHexString(bytes)}",
            // 記整組機器碼而不是只留綁定用的 4 碼前綴：授權清單要能讓人看出這張金鑰
            // 是發給哪台機器的，而 4 碼前綴在 65,536 台機器上就會碰撞。
            Machine = deviceCode.Trim().ToUpperInvariant(),
            // 格式不記錄發行日期。填一個編造出來的日期會在授權清單裡顯示成假資料，
            // 這裡誠實地標記為紀元起點；到期判斷只用 ExpiresUtc。
            IssuedUtc = DateTime.UnixEpoch,
            ExpiresUtc = expiresUtc,
            Cameras = maxCameras,
            Features = tier.Features,
        };

        if (expiresUtc is { } expiry && nowUtc > expiry)
        {
            return new LicenseState(
                LicenseStatus.Expired,
                payload,
                $"授權已於 {expiry:u} 到期");
        }

        return new LicenseState(LicenseStatus.Valid, payload, null);
    }

    /// <summary>
    /// 取機器碼前 4 個字元作為 MAC 與綁定依據。
    /// </summary>
    private static bool TryReadDevicePrefix(
        string deviceCode,
        [NotNullWhen(true)] out string? prefix,
        [NotNullWhen(false)] out string? error)
    {
        prefix = null;
        error = null;

        if (string.IsNullOrWhiteSpace(deviceCode))
        {
            error = "無法取得本機機器碼，無法驗證授權";
            return false;
        }

        var normalized = deviceCode.Trim().ToUpperInvariant();
        if (normalized.Length < 4 || !normalized[..4].All(Uri.IsHexDigit))
        {
            error = "本機機器碼不是有效的 16 進位字串，無法驗證授權";
            return false;
        }

        prefix = normalized[..4];
        return true;
    }

    private static bool TryHexByte(ReadOnlySpan<char> hex, out byte value)
    {
        value = 0;
        return hex.Length == 2 && byte.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out value);
    }
}