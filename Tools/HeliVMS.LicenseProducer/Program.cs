using System.Globalization;
using HeliVMS.Licensing;
using HeliVMS.Licensing.Crypto;

namespace HeliVMS.LicenseProducer;

/// <summary>
/// 離線授權產生器（§19 / 原 LicenseKeyGenUI 之 CLI 版）。
/// 私鑰不可進入 repo（.secrets/ 已於 .gitignore 排除），此工具於廠商用機執行。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        var opts = ParseArgs(args);

        if (opts.Count == 0 || opts.ContainsKey("help") || opts.ContainsKey("h"))
        {
            PrintUsage();
            return opts.ContainsKey("h") || opts.ContainsKey("help") ? 0 : 1;
        }

        if (opts.ContainsKey("list-tiers"))
        {
            PrintTiers();
            return 0;
        }

        if (!opts.TryGetValue("private", out var privatePemPath) || !File.Exists(privatePemPath))
        {
            Console.Error.WriteLine("缺少參數 --private <私鑰 PEM 路徑>");
            return 2;
        }

        // 等級決定通道數與功能旗標；--cameras／--features 可再覆寫（§19.3）。
        var tierName = opts.TryGetValue("tier", out var tierText) ? tierText.Trim() : string.Empty;
        var tier = LicenseTiers.Find(tierName);
        if (tierName.Length > 0 && tier is null)
        {
            Console.Error.WriteLine($"未知等級「{tierName}」。可用等級：{TierNames()}");
            return 2;
        }

        var cameras = tier?.DefaultCameras ?? 0;
        if (opts.TryGetValue("cameras", out var cameraText))
        {
            if (!int.TryParse(cameraText, out cameras))
            {
                Console.Error.WriteLine("參數 --cameras 必須是整數");
                return 2;
            }
        }

        if (cameras < 1)
        {
            Console.Error.WriteLine("請指定 --tier 或 --cameras");
            return 2;
        }

        if (cameras > LicenseTiers.MaxCameras)
        {
            Console.Error.WriteLine($"通道數不可超過 {LicenseTiers.MaxCameras}（§19.3）");
            return 2;
        }

        var machine = opts.TryGetValue("machine", out var machineText) ? machineText.Trim() : string.Empty;
        if (machine.Length > 0 &&
            (machine.Length != 32 || machine.Any(c => !Uri.IsHexDigit(c))))
        {
            Console.Error.WriteLine("--machine 必須是 32 碼十六進位設備碼（§19.1）");
            return 2;
        }

        DateTime? expires = null;
        if (opts.TryGetValue("expire", out var expireText) &&
            DateTime.TryParse(expireText, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var expiry))
        {
            expires = expiry.ToUniversalTime();
        }

        var features = opts.TryGetValue("features", out var featuresText)
            ? featuresText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : tier?.Features ?? [LicenseTiers.FeatureCore];

        try
        {
            using var privateKey = RsaPem.ParsePrivateKey(File.ReadAllText(privatePemPath));

            var payload = new LicensePayload
            {
                Id = opts.TryGetValue("id", out var id) ? id : Guid.NewGuid().ToString("N"),
                Machine = machine.ToUpperInvariant(),
                IssuedUtc = DateTime.UtcNow,
                ExpiresUtc = expires,
                Cameras = cameras,
                Features = features,
                Issuer = opts.TryGetValue("issuer", out var issuer) && issuer.Length > 0
                    ? issuer
                    : "禾秝軟體開發團隊",
            };

            var token = LicenseSerializer.Sign(payload, privateKey);

            Console.WriteLine(token);

            Console.Error.WriteLine(
                $"等級 {(tier?.Name ?? "自訂")} / {cameras} 路 / 功能 {string.Join(',', features)} / " +
                $"到期 {(expires is null ? "永久" : expires.Value.ToString("u"))}");

            if (opts.TryGetValue("out", out var outPath))
            {
                File.WriteAllText(outPath, token);
                Console.Error.WriteLine($"已寫入：{Path.GetFullPath(outPath)}");
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("產生失敗：" + ex.Message);
            return 3;
        }
    }

    private static string TierNames() => string.Join('、', LicenseTiers.All.Select(t => t.Name));

    private static Dictionary<string, string> ParseArgs(string[] args)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var key = arg[2..];
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                map[key] = args[++i];
            }
            else
            {
                map[key] = string.Empty;
            }
        }

        return map;
    }

    private static void PrintTiers()
    {
        Console.WriteLine($"{"等級",-10}{"建議通道",10}  功能");
        Console.WriteLine(new string('-', 72));
        foreach (var tier in LicenseTiers.All)
        {
            var cameras = tier.DefaultCameras >= LicenseTiers.MaxCameras
                ? "≤1024"
                : tier.DefaultCameras.ToString(CultureInfo.InvariantCulture);
            Console.WriteLine(
                $"{PadDisplay(tier.Name, 10)}{PadDisplay(cameras, 10)}  {string.Join(',', tier.Features)}");
        }
    }

    /// <summary>
    /// 以終端顯示寬度靠左填滿：CJK 全形字元佔 2 格，直接用格式化的字元數會錯位。
    /// </summary>
    private static string PadDisplay(string value, int width)
    {
        var display = 0;
        foreach (var c in value)
        {
            display += c >= 0x1100 && c <= 0x9FFF ? 2 : 1;
        }

        return value + new string(' ', Math.Max(0, width - display));
    }

    private static void PrintUsage()
    {
        Console.WriteLine(
            """
            HeliVMS.LicenseProducer — 離線授權產生器

            用法：
              dotnet run -c Release -- --private <私鑰PEM> [--tier <等級>]
                  [--cameras <通道數>] [--features core,ai,gis]
                  [--expire <到期日，UTC>] [--machine <32碼設備碼>]
                  [--id <授權ID>] [--issuer <發行者>] [--out <輸出檔>]
                  [--list-tiers]

            等級（--list-tiers 可列出矩陣）：
              基本版 / 標準版 / 專業版 / 進階版 / 企業版 / 客製版
              等級會帶出建議通道數與功能旗標；--cameras、--features 可覆寫。

            範例：
              dotnet run -c Release -- --private .secrets\test-private.pem
                  --tier 進階版 --expire 2027-09-13 --out .secrets\license.lic

              dotnet run -c Release -- --private .secrets\test-private.pem
                  --tier 客製版 --cameras 512 --features core,ai,gis,remote

            注意：
              - 私鑰永不進入 git（.secrets/ 已排除）
              - 授權碼格式為 HELVMS-v2.{payload}.{signature}（§19.1）
              - 機器指紋可於產品端以 MachineIdProvider.GetDeviceCode() 取得；
                留空 --machine 代表不綁定機器
            """);
    }
}