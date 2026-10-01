using System.Globalization;
using HeliVMS.Shared.Models;

namespace HeliVmsVerify;

/// <summary>
/// 離線驗證匯出簽章收據的命令列工具（§14.3(2)／M240）。
/// </summary>
/// <remarks>
/// 為什麼要獨立工具：匯出檔會離開這台 VMS（交給警方、律師、客戶）。驗證方手上只有
/// <c>影片檔 ＋ .receipt.json</c>，不該也不需要資料庫、金鑰或這套系統的執行權限。
/// 因此整條驗證路徑只依賴 BCL 與 <see cref="HeliVMS.Shared"/>。
/// </remarks>
public static class VerifyCli
{
    public const string Usage =
        "用法：HeliVmsVerify <匯出檔路徑> [--signer <64 位金鑰指紋>]\n"
        + "  驗證匯出檔旁的 .receipt.json：重算 SHA-256、驗簽、比對選用的簽署者指紋。\n"
        + "  不帶 --signer 只能證明「收據由收據內那把金鑰簽署」；金鑰身分須另行比對。\n"
        + "  結束碼：0=有效、1=驗證失敗、2=用法錯誤。";

    /// <summary>執行驗證；回傳行程結束碼。</summary>
    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "/?")
        {
            (args.Length == 0 ? error : output).WriteLine(Usage);
            return args.Length == 0 ? 2 : 0;
        }

        string? expectedSigner = null;
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--signer":
                    if (i + 1 >= args.Length)
                    {
                        error.WriteLine("--signer 需要參數。");
                        error.WriteLine(Usage);
                        return 2;
                    }

                    expectedSigner = args[++i];
                    break;
                default:
                    error.WriteLine($"不明的參數：{args[i]}");
                    error.WriteLine(Usage);
                    return 2;
            }
        }

        var report = ExportReceiptCodec.Verify(args[0], expectedSigner);
        Write(output, report);
        return report.Valid ? 0 : 1;
    }

    private static void Write(TextWriter output, ExportReceiptReport report)
    {
        output.WriteLine($"匯出檔：{report.ClipPath}");
        output.WriteLine($"收據　：{(report.ReceiptPath ?? "-")}");
        output.WriteLine($"檔案存在：{YesNo(report.FileExists)}");
        output.WriteLine($"雜湊相符：{YesNo(report.HashMatches)}");
        output.WriteLine($"簽章有效：{YesNo(report.SignatureValid)}");
        output.WriteLine($"簽署者　：{report.Signer ?? "-"}");
        output.WriteLine($"金鑰已比對：{(report.SelfAssertedKey ? "否（金鑰由收據自行宣稱）" : "是")}");
        output.WriteLine($"結論　：{(report.Valid ? "有效" : "無效")}（{report.Detail ?? "-"}）");
    }

    private static string YesNo(bool value)
        => value ? "是" : string.Create(CultureInfo.InvariantCulture, $"否");
}