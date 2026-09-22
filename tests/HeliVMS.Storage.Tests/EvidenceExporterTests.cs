using System.Security.Cryptography;
using System.Text.Json;
using HeliVMS.Storage;
using Xunit;

namespace HeliVMS.Storage.Tests;

public sealed class EvidenceExporterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"helivms-evidence-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static string TempFile(string name)
    {
        var path = Path.Combine(Path.GetTempPath(), name);
        File.WriteAllBytes(path, Enumerable.Range(0, 4096).Select(i => (byte)(i % 251)).ToArray());
        return path;
    }

    [Fact]
    public void ComputeSha256_MatchesDotNetReference()
    {
        var bytes = "evidence-bytes"u8.ToArray();

        var hash = EvidenceExporter.ComputeSha256(bytes);

        Assert.Equal(
            BitConverter.ToString(SHA256.HashData(bytes)).Replace("-", "").ToLowerInvariant(),
            hash);
        Assert.Equal(64, hash.Length);
    }

    [Fact]
    public void CreateExportFromFiles_WritesManifestAndSelfLedger()
    {
        var file = TempFile("ev-a.mp4");
        try
        {
            var export = EvidenceExporter.CreateExportFromFiles(
                _dir, "operator-a", "cam-01", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 1, 1, 1, 0, 0, DateTimeKind.Utc), new[] { file }, 3);

            Assert.Equal(1, export.SegmentCount);
            Assert.True(File.Exists(export.ManifestPath));
            Assert.True(File.Exists(Path.Combine(_dir, "manifest.sha256")));
            Assert.Equal(File.ReadAllText(Path.Combine(_dir, "manifest.sha256")).Trim(), export.ManifestSha256);
            Assert.Equal(64, export.ManifestSha256.Length);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Manifest_RoundTripsThroughJson()
    {
        var manifest = EvidenceExporter.BuildManifest(
            "operator-a", "cam-01", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 1, 1, 0, 0, DateTimeKind.Utc),
            new[] { new EvidenceSegment("b.mp4", 10, "abc", 0) });

        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var restored = JsonSerializer.Deserialize<EvidenceManifest>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(restored);
        Assert.Equal(manifest.Source, restored.Source);
        Assert.Equal(manifest.ExportedBy, restored.ExportedBy);
        Assert.Equal(manifest.Segments.Count, restored.Segments.Count);
        Assert.Equal(manifest.Segments[0].Sha256, restored.Segments[0].Sha256);
    }

    [Fact]
    public void Verify_AcceptsIntactExport()
    {
        var file = TempFile("ev-b.mp4");
        try
        {
            var export = EvidenceExporter.CreateExportFromFiles(
                _dir, "op", "cam-01", DateTime.UtcNow.AddHours(-2), DateTime.UtcNow, new[] { file }, 5);
            var manifest = JsonSerializer.Deserialize<EvidenceManifest>(
                File.ReadAllText(export.ManifestPath), new JsonSerializerOptions(JsonSerializerDefaults.Web));

            Assert.Empty(EvidenceExporter.Verify(_dir, manifest!));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Verify_DetectsTamperedSegment()
    {
        var file = TempFile("ev-c.mp4");
        try
        {
            var export = EvidenceExporter.CreateExportFromFiles(
                _dir, "op", "cam-01", DateTime.UtcNow.AddHours(-2), DateTime.UtcNow, new[] { file }, 5);
            var manifest = JsonSerializer.Deserialize<EvidenceManifest>(
                File.ReadAllText(export.ManifestPath), new JsonSerializerOptions(JsonSerializerDefaults.Web));

            var victim = Path.Combine(_dir, "ev-c.mp4");
            using (var stream = File.OpenWrite(victim))
            {
                stream.Position = 0;
                stream.WriteByte(255);
            }

            var errors = EvidenceExporter.Verify(_dir, manifest!);
            Assert.Contains(errors, e => e.Contains("segment hash mismatch", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Verify_DetectsMissingSegment()
    {
        var file = TempFile("ev-d.mp4");
        try
        {
            var export = EvidenceExporter.CreateExportFromFiles(
                _dir, "op", "cam-01", DateTime.UtcNow.AddHours(-2), DateTime.UtcNow, new[] { file }, 5);
            var manifest = JsonSerializer.Deserialize<EvidenceManifest>(
                File.ReadAllText(export.ManifestPath), new JsonSerializerOptions(JsonSerializerDefaults.Web));

            File.Delete(Path.Combine(_dir, "ev-d.mp4"));

            var errors = EvidenceExporter.Verify(_dir, manifest!);
            Assert.Contains(errors, e => e.Contains("missing segment", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Verify_DetectsManifestHashChange()
    {
        var file = TempFile("ev-e.mp4");
        try
        {
            var export = EvidenceExporter.CreateExportFromFiles(
                _dir, "op", "cam-01", DateTime.UtcNow.AddHours(-2), DateTime.UtcNow, new[] { file }, 5);
            var manifest = JsonSerializer.Deserialize<EvidenceManifest>(
                File.ReadAllText(export.ManifestPath), new JsonSerializerOptions(JsonSerializerDefaults.Web));

            File.WriteAllText(export.ManifestPath, File.ReadAllText(export.ManifestPath) + " ");

            var errors = EvidenceExporter.Verify(_dir, manifest!);
            Assert.Contains(errors, e => e.Contains("manifest hash mismatch", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(file);
        }
    }
}