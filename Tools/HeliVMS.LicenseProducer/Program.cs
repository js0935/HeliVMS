using System.Globalization;
using System.Security.Cryptography;
using HeliVMS.Licensing;
using HeliVMS.Licensing.Crypto;

namespace HeliVMS.LicenseProducer;

/// <summary>
/// 離線授權產生器（§19 / 原 LicenseKeyGenUI 之 CLI 版）。
/// 私鑰不可進入 repo（.secrets/ 已於 .gitignore 排除），此工具於廠商用機執行。
/// 每次簽發／產生金鑰皆寫入簽章式稽核軌跡（§19.6），稽核失敗即中止、不輸出授權碼。
/// </summary>
internal static class Program
{
    private const string OperatorEnvVar = "HELIVMS_ISSUANCE_OPERATOR";

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

        // 印出本機設備碼：簽發給自己、或交付客戶前的自我檢查用（§19.1）。
        if (opts.ContainsKey("device-code"))
        {
            Console.WriteLine(MachineIdProvider.GetDeviceCode());
            Console.Error.WriteLine(
                $"（舊版 MAC 指紋：{MachineIdProvider.GetLegacyFingerprint()}，僅供相容既有授權）");
            return 0;
        }

        var passphraseResult = ResolvePassphrase(opts);
        if (passphraseResult.Error is { } passphraseError)
        {
            Console.Error.WriteLine(passphraseError);
            return 2;
        }

        // 金鑰對產生（§19.6）：產出 private.pem／public.key／嵌入用程式碼片段，並留稽核。
        if (opts.TryGetValue("gen-key", out var keyDir))
        {
            return GenerateKeyPair(opts, keyDir, passphraseResult.Value);
        }

        // 稽核軌跡驗證（§19.6）：以公鑰或私鑰驗簽並檢查雜湊鏈。
        if (opts.TryGetValue("audit-verify", out var auditPath))
        {
            return VerifyAudit(opts, auditPath, passphraseResult.Value);
        }

        if (!opts.TryGetValue("private", out var privatePemPath) || !File.Exists(privatePemPath))
        {
            Console.Error.WriteLine("缺少參數 --private <私鑰路徑>");
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
            using var privateKey = PrivateKeyVault.Open(File.ReadAllText(privatePemPath), passphraseResult.Value);

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

            // 先留痕再輸出：稽核寫不進去就不給授權碼（fail-closed，§19.6）。
            var auditFile = ResolveAuditPath(opts, privatePemPath);
            var entry = IssuanceAuditTrail.Append(
                auditFile,
                new IssuanceAuditDraft(
                    IssuanceActions.Issue,
                    ResolveOperator(opts),
                    LicenseId: payload.Id,
                    Tier: tier?.Name ?? "自訂",
                    Cameras: cameras,
                    Features: features,
                    Machine: payload.Machine.Length > 0 ? payload.Machine : null,
                    ExpiresUtc: expires,
                    Issuer: payload.Issuer,
                    TokenSha256: IssuanceAuditTrail.Fingerprint(
                        System.Text.Encoding.UTF8.GetBytes(token))),
                privateKey);

            Console.WriteLine(token);

            Console.Error.WriteLine(
                $"等級 {(tier?.Name ?? "自訂")} / {cameras} 路 / 功能 {string.Join(',', features)} / " +
                $"到期 {(expires is null ? "永久" : expires.Value.ToString("u"))}");
            Console.Error.WriteLine(
                $"稽核：{Path.GetFullPath(auditFile)}（seq={entry.Seq}，操作者 {entry.Operator}）");

            if (opts.TryGetValue("out", out var outPath))
            {
                File.WriteAllText(outPath, token);
                Console.Error.WriteLine($"已寫入：{Path.GetFullPath(outPath)}");
            }

            return 0;
        }
        catch (PrivateKeyAccessException ex)
        {
            Console.Error.WriteLine("私鑰載入失敗：" + ex.Message);
            return 4;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("產生失敗：" + ex.Message);
            return 3;
        }
    }

    private static int GenerateKeyPair(Dictionary<string, string> opts, string directory, string? passphrase)
    {
        var keySize = 2048;
        if (opts.TryGetValue("key-size", out var keySizeText))
        {
            if (!int.TryParse(keySizeText, out keySize))
            {
                Console.Error.WriteLine("參數 --key-size 必須是整數");
                return 2;
            }
        }

        if (Array.IndexOf(KeyPairFactory.AllowedKeySizes, keySize) < 0)
        {
            Console.Error.WriteLine(
                $"--key-size 僅支援 {string.Join('/', KeyPairFactory.AllowedKeySizes)}（§19.6）");
            return 2;
        }

        var mode = KeyProtectionMode.Plain;
        if (opts.TryGetValue("protection", out var protectionText) && protectionText.Length > 0)
        {
            if (!TryParseProtection(protectionText, out mode))
            {
                Console.Error.WriteLine("參數 --protection 僅支援 plain／passphrase／dpapi");
                return 2;
            }
        }
        else if (passphrase is null)
        {
            // 未指定口令且未要求保護模式時不預設輸出明文私鑰，避免簽發金鑰裸奔。
            Console.Error.WriteLine("注意：未提供口令，將輸出明文私鑰；離開簽發機前請重新以口令加密。");
        }

        var iterations = PrivateKeyVault.DefaultIterations;
        if (opts.TryGetValue("iterations", out var iterText) && !int.TryParse(iterText, out iterations))
        {
            Console.Error.WriteLine("參數 --iterations 必須是整數");
            return 2;
        }

        try
        {
            Directory.CreateDirectory(directory);
            var pair = KeyPairFactory.Generate(keySize, mode, passphrase, iterations);
            KeyPairFactory.WriteTo(pair, directory);

            var auditFile = ResolveAuditPath(opts, Path.Combine(directory, KeyPairFactory.PrivateFileName));

            // 金鑰產生需要簽發私鑰簽稽核；此時尚未持有，故以明文載入剛產生的私鑰（僅存在於記憶體）。
            using var signer = PrivateKeyVault.Open(pair.PrivateKeyText, passphrase);
            var entry = IssuanceAuditTrail.Append(
                auditFile,
                new IssuanceAuditDraft(
                    IssuanceActions.GenerateKey,
                    ResolveOperator(opts),
                    LicenseId: null,
                    Tier: null,
                    Cameras: null,
                    Machine: null,
                    Issuer: null,
                    TokenSha256: null,
                    PublicKeySha256: pair.PublicKeySha256),
                signer);

            Console.Error.WriteLine($"已產生 {pair.KeySizeBits}-bit 金鑰對（保護模式 {pair.Mode}）：");
            Console.Error.WriteLine($"  私鑰：{Path.GetFullPath(Path.Combine(directory, KeyPairFactory.PrivateFileName))}");
            Console.Error.WriteLine($"  公鑰：{Path.GetFullPath(Path.Combine(directory, KeyPairFactory.PublicFileName))}");
            Console.Error.WriteLine($"  嵌入片段：{Path.GetFullPath(Path.Combine(directory, KeyPairFactory.SnippetFileName))}");
            Console.Error.WriteLine($"  公鑰指紋：{pair.PublicKeySha256}");
            Console.Error.WriteLine($"  稽核：{Path.GetFullPath(auditFile)}（seq={entry.Seq}，操作者 {entry.Operator}）");
            Console.Error.WriteLine("  換正式金鑰時把片段貼入 src/HeliVMS.Licensing/Crypto/EmbeddedPublicKey.cs 的 Value。");
            return 0;
        }
        catch (PrivateKeyAccessException ex)
        {
            Console.Error.WriteLine("產生金鑰失敗：" + ex.Message);
            return 4;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("產生金鑰失敗：" + ex.Message);
            return 3;
        }
    }

    private static int VerifyAudit(Dictionary<string, string> opts, string auditPath, string? passphrase)
    {
        if (!File.Exists(auditPath))
        {
            Console.Error.WriteLine($"稽核檔不存在：{auditPath}");
            return 2;
        }

        try
        {
            using var publicKey = LoadPublicKey(opts, passphrase);
            var problems = IssuanceAuditTrail.Verify(auditPath, publicKey);
            var entries = IssuanceAuditTrail.Read(auditPath);

            Console.Error.WriteLine($"稽核檔：{Path.GetFullPath(auditPath)}（{entries.Count} 筆）");
            if (problems.Count == 0)
            {
                foreach (var e in entries)
                {
                    Console.Error.WriteLine(
                        $"  #{e.Seq} {e.AtUtc:u} {e.Action} 操作者={e.Operator}" +
                        (e.LicenseId is null ? string.Empty : $" 授權={e.LicenseId}") +
                        (e.Tier is null ? string.Empty : $" 等級={e.Tier}") +
                        (e.Machine is null ? string.Empty : $" 設備={e.Machine}"));
                }

                Console.Error.WriteLine("驗證通過：全部項目簽章有效且雜湊鏈連續（無竄改、刪除或重排）。");
                return 0;
            }

            foreach (var problem in problems)
            {
                Console.Error.WriteLine("  ✗ " + problem);
            }

            return 5;
        }
        catch (PrivateKeyAccessException ex)
        {
            Console.Error.WriteLine("驗證失敗：" + ex.Message);
            return 4;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("驗證失敗：" + ex.Message);
            return 3;
        }
    }

    private static RSA LoadPublicKey(Dictionary<string, string> opts, string? passphrase)
    {
        if (opts.TryGetValue("public", out var publicPath) && File.Exists(publicPath))
        {
            return RsaPem.ParsePublicKey(File.ReadAllText(publicPath));
        }

        if (opts.TryGetValue("private", out var privatePath) && File.Exists(privatePath))
        {
            var key = PrivateKeyVault.Open(File.ReadAllText(privatePath), passphrase);
            try
            {
                return RsaPem.ParsePublicKey(RsaPem.ToPublicPem(key));
            }
            finally
            {
                key.Dispose();
            }
        }

        throw new PrivateKeyAccessException("請以 --public <公鑰 PEM> 或 --private <私鑰路徑> 指定驗證用金鑰。");
    }

    private static bool TryParseProtection(string text, out KeyProtectionMode mode)
    {
        switch (text.ToLowerInvariant())
        {
            case "plain":
            case "none":
                mode = KeyProtectionMode.Plain;
                return true;
            case "passphrase":
                mode = KeyProtectionMode.Passphrase;
                return true;
            case "dpapi":
                mode = KeyProtectionMode.Dpapi;
                return true;
            default:
                mode = KeyProtectionMode.Plain;
                return false;
        }
    }

    /// <summary>
    /// 稽核軌跡預設與私鑰同目錄（簽發機上的機密與紀錄同處且一併備份／封存）；
    /// 可用 --audit-log 覆寫。
    /// </summary>
    private static string ResolveAuditPath(Dictionary<string, string> opts, string privateKeyPath)
    {
        if (opts.TryGetValue("audit-log", out var auditPath) && auditPath.Length > 0)
        {
            return auditPath;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(privateKeyPath));
        return string.IsNullOrEmpty(directory)
            ? IssuanceAuditTrail.DefaultFileName
            : Path.Combine(directory, IssuanceAuditTrail.DefaultFileName);
    }

    /// <summary>
    /// 操作者識別（§19.6：誰簽了哪張）。優先 --operator，其次 HELIVMS_ISSUANCE_OPERATOR，
    /// 最後退回 OS 帳戶（真實簽發者登入列 P3 雲端強化，故仍以機器＋帳戶名留下可追跡的識別）。
    /// </summary>
    private static string ResolveOperator(Dictionary<string, string> opts)
    {
        if (opts.TryGetValue("operator", out var op) && op.Trim().Length > 0)
        {
            return op.Trim();
        }

        var fromEnv = Environment.GetEnvironmentVariable(OperatorEnvVar);
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return fromEnv.Trim();
        }

        return $"{Environment.MachineName}\\{Environment.UserName}";
    }

    private static (string? Value, string? Error) ResolvePassphrase(Dictionary<string, string> opts)
    {
        var sources = new[] { "passphrase-env", "passphrase-file", "prompt-passphrase" }
            .Count(k => opts.ContainsKey(k));
        if (sources > 1)
        {
            return (null, "口令來源僅可擇一：--passphrase-env／--passphrase-file／--prompt-passphrase。");
        }

        if (opts.TryGetValue("passphrase-env", out var envName) && envName.Length > 0)
        {
            var value = Environment.GetEnvironmentVariable(envName.Trim());
            if (string.IsNullOrEmpty(value))
            {
                return (null, $"環境變數 {envName} 未設定或為空。");
            }

            return (value, null);
        }

        if (opts.TryGetValue("passphrase-file", out var filePath) && filePath.Length > 0)
        {
            if (!File.Exists(filePath))
            {
                return (null, $"口令檔不存在：{filePath}");
            }

            return (File.ReadAllText(filePath).Trim(), null);
        }

        if (opts.ContainsKey("prompt-passphrase"))
        {
            Console.Error.Write("請輸入私鑰口令：");
            var buffer = new System.Text.StringBuilder();
            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter)
                {
                    break;
                }

                if (key.Key == ConsoleKey.Backspace)
                {
                    if (buffer.Length > 0)
                    {
                        buffer.Length--;
                    }

                    continue;
                }

                if (!char.IsControl(key.KeyChar))
                {
                    buffer.Append(key.KeyChar);
                }
            }

            Console.Error.WriteLine();
            if (buffer.Length == 0)
            {
                return (null, "口令不可為空。");
            }

            return (buffer.ToString(), null);
        }

        return (null, null);
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
              dotnet run -c Release -- --private <私鑰路徑> [--tier <等級>]
                  [--cameras <通道數>] [--features core,ai,gis]
                  [--expire <到期日，UTC>] [--machine <32碼設備碼>]
                  [--id <授權ID>] [--issuer <發行者>] [--out <輸出檔>]
                  [--operator <操作者>] [--audit-log <稽核檔>]
                  [--passphrase-env <變數名> | --passphrase-file <檔> | --prompt-passphrase]
                  [--list-tiers] [--device-code]

              dotnet run -c Release -- --gen-key <輸出目錄> [--key-size 3072]
                  [--protection passphrase|dpapi|plain] [--iterations <PBKDF2次數>]
                  [--operator <操作者>] [--audit-log <稽核檔>] [口令來源]

              dotnet run -c Release -- --audit-verify <稽核檔> (--public <公鑰PEM> | --private <私鑰路徑>)

            等級（--list-tiers 可列出矩陣）：
              基本版 / 標準版 / 專業版 / 進階版 / 企業版 / 客製版
              等級會帶出建議通道數與功能旗標；--cameras、--features 可覆寫。

            稽核與金鑰安全（§19.6）：
              - 每次簽發與產生金鑰皆寫入 append-only 稽核軌跡（預設與私鑰同目錄
                issuance-audit.jsonl），每列以簽發私鑰簽章並以雜湊鏈串接；
                稽核寫入失敗即中止、不輸出授權碼
              - 操作者取自 --operator > HELIVMS_ISSUANCE_OPERATOR > 機器\帳戶
              - --audit-verify 可驗出竄改、刪除或重排（需簽發公鑰）
              - 口令來源僅可擇一；--passphrase-env／--prompt-passphrase 不會把口令留在
                命令列歷史

            範例：
              dotnet run -c Release -- --private .secrets\test-private.pem
                  --tier 進階版 --expire 2027-09-13 --out .secrets\license.lic

              dotnet run -c Release -- --private .secrets\test-private.pem
                  --tier 客製版 --cameras 512 --features core,ai,gis,remote

              dotnet run -c Release -- --gen-key .secrets\prod --key-size 3072
                  --protection passphrase --prompt-passphrase

            注意：
              - 私鑰永不進入 git（.secrets/ 已排除）
              - 授權碼格式為 HELVMS-v2.{payload}.{signature}（§19.1）
              - 綁定機器的授權需帶 --machine <32碼設備碼>；
                客戶端設備碼見其「設定 → 授權 → 本機機器指紋」，
                或用 --device-code 取得本機設備碼
              - --machine 留空代表不綁定機器（可在任一機器使用）
            """);
    }
}