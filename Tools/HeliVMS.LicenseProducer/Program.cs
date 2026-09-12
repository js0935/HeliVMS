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

        if (!opts.TryGetValue("private", out var privatePemPath) || !File.Exists(privatePemPath))
        {
            Console.Error.WriteLine("缺少參數 --private <私鑰 PEM 路徑>");
            return 2;
        }

        if (!opts.TryGetValue("cameras", out var cameraText) ||
            !int.TryParse(cameraText, out var cameras))
        {
            Console.Error.WriteLine("缺少/無效參數 --cameras");
            return 2;
        }

        DateTime? expires = null;
        if (opts.TryGetValue("expire", out var expireText) &&
            DateTime.TryParse(expireText, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var expiry))
        {
            expires = expiry.ToUniversalTime();
        }

        try
        {
            using var privateKey = RsaPem.ParsePrivateKey(File.ReadAllText(privatePemPath));

            var payload = new LicensePayload
            {
                Id = opts.TryGetValue("id", out var id) ? id : Guid.NewGuid().ToString("N"),
                Machine = opts.TryGetValue("machine", out var machine) ? machine : string.Empty,
                IssuedUtc = DateTime.UtcNow,
                ExpiresUtc = expires,
                Cameras = cameras,
                Features = opts.TryGetValue("features", out var features)
                    ? features.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    : ["core"],
                Issuer = "禾秝軟體開發團隊",
            };

            var token = LicenseSerializer.Sign(payload, privateKey);

            Console.WriteLine(token);

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

    private static void PrintUsage()
    {
        Console.WriteLine(
            """
            HeliVMS.LicenseProducer — 離線授權產生器

            用法：
              dotnet run -c Release -- --private <私鑰PEM> --cameras <通道數>
                  [--expire <到期日，UTC>] [--machine <機器指紋>] [--features core,ai,gis]
                  [--id <授權ID>] [--out <輸出檔>]

            範例：
              dotnet run -c Release -- --private .secrets\test-private.pem --cameras 32
                  --expire 2027-09-13 --features core,ai,gis --out .secrets\license.lic

            注意：
              - 私鑰永不進入 git（.secrets/ 已排除）
              - 機器指紋可於產品端以 LicenseManager.MachineIdProvider.GetFingerprint() 取得
            """);
    }
}