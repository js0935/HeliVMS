using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;

namespace HeliVMS.Storage;

/// <summary>匯出產物完整性驗證報告（§14.3(2) 匯出即驗證）。</summary>
public sealed record VerificationReport(
    string OutputPath,
    string Sha256,
    long SizeBytes,
    bool HashMatches,
    string? FfprobeSummary,
    bool Valid);

/// <summary>
/// 匯出檔案驗證：重算 SHA-256 對比（防竄改）＋可選 ffprobe 完整性摘要。
/// ffprobe 不可用時 Valid 只以「檔案存在＋hash 比對」判定。
/// </summary>
public static class ExportVerifier
{
    public static VerificationReport Verify(string outputPath, string? expectedSha = null, bool useFfprobe = true)
    {
        var exists = File.Exists(outputPath);
        string sha256 = string.Empty;
        long size = 0;
        if (exists)
        {
            using var stream = File.OpenRead(outputPath);
            sha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            size = stream.Length;
        }

        var hashMatches = exists && (expectedSha is null
            || string.Equals(sha256, expectedSha, StringComparison.OrdinalIgnoreCase));

        string? ffprobe = null;
        if (exists && useFfprobe)
        {
            ffprobe = TryFfprobe(outputPath);
        }

        var valid = exists && hashMatches;
        return new VerificationReport(outputPath, sha256, size, hashMatches, ffprobe, valid);
    }

    /// <summary>以 ffprobe 讀取容器與時長摘要；失敗回傳 null（不影響 Valid）。</summary>
    private static string? TryFfprobe(string path)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ffprobe",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-v");
            psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-show_entries");
            psi.ArgumentList.Add("format=duration:format=format_name");
            psi.ArgumentList.Add("-of");
            psi.ArgumentList.Add("default=noprint_wrappers=1");
            psi.ArgumentList.Add(path);

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return null;
            }

            var stdout = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(10_000);

            var duration = "？";
            var container = "";
            foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var kv = line.Split('=', 2);
                if (kv.Length != 2)
                {
                    continue;
                }

                if (kv[0] == "duration" && double.TryParse(kv[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                {
                    duration = d.ToString("0.##", CultureInfo.InvariantCulture);
                }
                else if (kv[0] == "format_name")
                {
                    container = kv[1];
                }
            }

            return $"容器 {container}、時長 {duration} 秒";
        }
        catch
        {
            return null;
        }
    }
}