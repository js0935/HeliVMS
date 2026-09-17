using System.IO.Compression;
using System.Security.Cryptography;
using Xunit;

namespace HeliVMS.Storage.Tests;

public sealed class EvidencePackageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"helivms-evid-{Guid.NewGuid():N}");

    public EvidencePackageTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string CreateSampleFile(string name, int size)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, Enumerable.Range(0, size).Select(i => (byte)(i % 251)).ToArray());
        return path;
    }

    [Fact]
    public void Roundtrip_NoPassword_ValidWithMatchingHashes()
    {
        var mp4 = CreateSampleFile("sample.mp4", 4096);
        var bmp = CreateSampleFile("snapshot.bmp", 512);
        var note = CreateSampleFile("meta.sha256", 180);
        var manifest = EvidencePackager.BuildManifest("demo", [mp4, bmp, note]);
        var outPath = Path.Combine(_dir, "evidence.evp");

        EvidencePackager.Create(outPath, manifest);

        var result = EvidencePackager.Verify(outPath);
        Assert.True(result.Valid, string.Join("; ", result.Failures));
        Assert.Equal(manifest.BundleId, result.BundleId);
        Assert.False(result.Expired);
        Assert.Equal(3, result.Items.Count);
        Assert.Equal(manifest.Items[0].Sha256, result.Items[0].Sha256);
        Assert.Equal(manifest.Items[1].Sha256, result.Items[1].Sha256);
    }

    [Fact]
    public void WithPassword_Correct_Valid()
    {
        var mp4 = CreateSampleFile("clip.mp4", 2048);
        var manifest = EvidencePackager.BuildManifest("demo", [mp4]);
        var outPath = Path.Combine(_dir, "evidence.evp");

        EvidencePackager.Create(outPath, manifest, "s3cret");

        Assert.True(EvidencePackager.Verify(outPath, "s3cret").Valid);
    }

    [Fact]
    public void WithPassword_WrongPassword_Invalid()
    {
        var mp4 = CreateSampleFile("clip.mp4", 2048);
        var manifest = EvidencePackager.BuildManifest("demo", [mp4]);
        var outPath = Path.Combine(_dir, "evidence.evp");
        EvidencePackager.Create(outPath, manifest, "s3cret");

        var result = EvidencePackager.Verify(outPath, "wrong");
        Assert.False(result.Valid);
        Assert.Contains(result.Failures, f => f.Contains("密碼錯誤"));
    }

    [Fact]
    public void WithPassword_MissingPassword_Rejected()
    {
        var mp4 = CreateSampleFile("clip.mp4", 2048);
        var manifest = EvidencePackager.BuildManifest("demo", [mp4]);
        var outPath = Path.Combine(_dir, "evidence.evp");
        EvidencePackager.Create(outPath, manifest, "s3cret");

        var result = EvidencePackager.Verify(outPath);
        Assert.False(result.Valid);
        Assert.Contains(result.Failures, f => f.Contains("需提供密碼"));
    }

    [Fact]
    public void NoPassword_WithPasswordParam_StillValid()
    {
        var mp4 = CreateSampleFile("clip.mp4", 2048);
        var manifest = EvidencePackager.BuildManifest("demo", [mp4]);
        var outPath = Path.Combine(_dir, "evidence.evp");
        EvidencePackager.Create(outPath, manifest);

        Assert.True(EvidencePackager.Verify(outPath, "ignored-pw").Valid);
    }

    [Fact]
    public void TamperedItem_Detected()
    {
        var mp4 = CreateSampleFile("sample.mp4", 4096);
        var manifest = EvidencePackager.BuildManifest("demo", [mp4]);
        var outPath = Path.Combine(_dir, "evidence.evp");
        EvidencePackager.Create(outPath, manifest);

        using (var zip = new ZipArchive(File.Open(outPath, FileMode.Open, FileAccess.ReadWrite), ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry("sample.mp4")!;
            using var es = entry.Open();
            es.SetLength(1);
            es.Position = 0;
            es.WriteByte(0xFF);
        }

        var result = EvidencePackager.Verify(outPath);
        Assert.False(result.Valid);
        Assert.Contains(result.Failures, f => f.Contains("SHA-256 不符：sample.mp4"));
    }

    [Fact]
    public void ExpiredBundle_Flagged()
    {
        var mp4 = CreateSampleFile("clip.mp4", 2048);
        var manifest = EvidencePackager.BuildManifest("demo", [mp4]) with
        {
            ExpiresUtc = DateTime.UtcNow.AddMinutes(-5),
        };
        var outPath = Path.Combine(_dir, "evidence.evp");
        EvidencePackager.Create(outPath, manifest);

        var result = EvidencePackager.Verify(outPath);
        Assert.True(result.Valid);
        Assert.True(result.Expired);
    }

    [Fact]
    public void Kind_DetectedByExtension()
    {
        var mp4 = CreateSampleFile("a.mp4", 8);
        var bmp = CreateSampleFile("b.bmp", 8);
        var sha = CreateSampleFile("c.sha256", 8);
        var manifest = EvidencePackager.BuildManifest("demo", [mp4, bmp, sha]);

        Assert.Equal("video", manifest.Items[0].Kind);
        Assert.Equal("snapshot", manifest.Items[1].Kind);
        Assert.Equal("hash", manifest.Items[2].Kind);
    }

    [Fact]
    public void DuplicateFilenames_Flattened()
    {
        var a = CreateSampleFile("same.mp4", 8);
        CreateSampleFile("same2.mp4", 12);
        var b = Path.Combine(_dir, "same2.mp4");
        var manifest = EvidencePackager.BuildManifest("demo", [a, b]);

        Assert.Equal(2, manifest.Items.Count);
        Assert.Contains("same", manifest.Items[1].RelativePath);
        Assert.NotEqual(manifest.Items[0].RelativePath, manifest.Items[1].RelativePath);
    }

    [Fact]
    public void EmptyFiles_Rejected()
    {
        Assert.Throws<ArgumentException>(() => EvidencePackager.BuildManifest("demo", []));
    }

    [Fact]
    public void MissingSource_Rejected()
    {
        Assert.Throws<FileNotFoundException>(
            () => EvidencePackager.BuildManifest("demo", [Path.Combine(_dir, "nope.mp4")]));
    }

    [Fact]
    public void NonPackageFile_Invalid()
    {
        var junk = CreateSampleFile("random.bin", 300);
        var result = EvidencePackager.Verify(junk);
        Assert.False(result.Valid);
    }

    [Fact]
    public void Create_ReturnsBundleSha256()
    {
        var mp4 = CreateSampleFile("clip.mp4", 1024);
        var manifest = EvidencePackager.BuildManifest("demo", [mp4]);
        var outPath = Path.Combine(_dir, "evidence.evp");

        var sha = EvidencePackager.Create(outPath, manifest);
        Assert.Equal(64, sha.Length);

        using var fs = File.OpenRead(outPath);
        using var shaAlg = SHA256.Create();
        Assert.Equal(Convert.ToHexStringLower(shaAlg.ComputeHash(fs)), sha);
    }
}