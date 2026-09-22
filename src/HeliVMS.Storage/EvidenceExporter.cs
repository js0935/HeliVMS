using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace HeliVMS.Storage;

/// <summary>One exported recording file described by the evidence manifest (M119, section 14.1/14.5).</summary>
public sealed record EvidenceSegment(string FileName, long LengthBytes, string Sha256, long EventCount);

/// <summary>
/// Audit export manifest (M119): the file ledger alongside a video export, mirroring the
/// "稽核匯出工具" P0 ledger (channel/time/AI results/operator) with SHA-256 integrity per
/// segment and for the manifest itself.
/// </summary>
public sealed record EvidenceManifest(
    string FormatVersion,
    string ExportedBy,
    DateTime ExportedUtc,
    string Source,
    DateTime FromUtc,
    DateTime ToUtc,
    IReadOnlyList<EvidenceSegment> Segments);

public sealed record EvidenceExport(string ManifestPath, string ManifestSha256, int SegmentCount);

/// <summary>
/// Builds and verifies evidence-export ledgers. Segment hashes let consumers validate the
/// exported footage end to end; the manifest hash travels with the package so a tampered
/// ledger is detectable before any segment is trusted.
/// </summary>
public static class EvidenceExporter
{
    public const string DefaultFormatVersion = "helivms-evidence/v1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string ComputeSha256(string filePath)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexStringLower(sha.ComputeHash(stream));
    }

    public static string ComputeSha256(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexStringLower(sha.ComputeHash(bytes));
    }

    public static EvidenceManifest BuildManifest(
        string exportedBy,
        string source,
        DateTime fromUtc,
        DateTime toUtc,
        IReadOnlyList<EvidenceSegment> segments) =>
        new(DefaultFormatVersion, exportedBy, DateTime.UtcNow, source, fromUtc, toUtc,
            segments.ToArray());

    /// <summary>
    /// Surveys existing files (hash + size) and writes the full audit package into
    /// <paramref name="directory"/>: <c>manifest.json</c> plus <c>manifest.sha256</c>.
    /// </summary>
    public static EvidenceExport CreateExportFromFiles(
        string directory,
        string exportedBy,
        string source,
        DateTime fromUtc,
        DateTime toUtc,
        IEnumerable<string> filePaths,
        long eventCountPerFile = 0)
    {
        Directory.CreateDirectory(directory);
        var segments = new List<EvidenceSegment>();
        foreach (var path in filePaths)
        {
            var destination = Path.Combine(directory, Path.GetFileName(path));
            if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(path, destination, overwrite: true);
            }

            var info = new FileInfo(destination);
            segments.Add(new EvidenceSegment(
                Path.GetFileName(destination), info.Length, ComputeSha256(destination), eventCountPerFile));
        }

        var manifest = BuildManifest(exportedBy, source, fromUtc, toUtc, segments);
        return WriteExport(directory, manifest);
    }

    public static EvidenceExport WriteExport(string directory, EvidenceManifest manifest)
    {
        Directory.CreateDirectory(directory);
        var json = JsonSerializer.Serialize(manifest, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        var sha = ComputeSha256(bytes);
        var manifestPath = Path.Combine(directory, "manifest.json");
        File.WriteAllBytes(manifestPath, bytes);
        File.WriteAllText(Path.Combine(directory, "manifest.sha256"), sha);
        return new EvidenceExport(manifestPath, sha, manifest.Segments.Count);
    }

    /// <summary>
    /// Re-validates an exported package against its manifest. Returns an empty list when
    /// intact; otherwise one error per tampered/missing segment plus manifest-hash drift.
    /// </summary>
    public static IReadOnlyList<string> Verify(string directory, EvidenceManifest manifest)
    {
        var errors = new List<string>();
        var manifestPath = Path.Combine(directory, "manifest.json");
        var ledger = Path.Combine(directory, "manifest.sha256");

        if (!File.Exists(manifestPath))
        {
            errors.Add("manifest.json missing");
            return errors;
        }

        var jsonBytes = File.ReadAllBytes(manifestPath);
        if (File.Exists(ledger))
        {
            var expected = File.ReadAllText(ledger).Trim();
            if (!string.Equals(ComputeSha256(jsonBytes), expected, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("manifest hash mismatch");
            }
        }

        foreach (var segment in manifest.Segments)
        {
            var path = Path.Combine(directory, segment.FileName);
            if (!File.Exists(path))
            {
                errors.Add($"missing segment {segment.FileName}");
                continue;
            }

            if (!string.Equals(ComputeSha256(path), segment.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"segment hash mismatch {segment.FileName}");
            }
        }

        return errors;
    }
}