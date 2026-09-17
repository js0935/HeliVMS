using System.Security.Cryptography;
using Xunit;

namespace HeliVMS.Storage.Tests;

public sealed class ExportVerifierTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"helivms-ver-{Guid.NewGuid():N}");

    public ExportVerifierTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string WriteFile(string name, int size)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, Enumerable.Range(0, size).Select(i => (byte)(i % 251)).ToArray());
        return path;
    }

    private static string ShaOf(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }

    [Fact]
    public void Verify_MatchingHash_Valid()
    {
        var path = WriteFile("a.mp4", 4096);
        var sha = ShaOf(path);

        var report = ExportVerifier.Verify(path, sha);

        Assert.True(report.Valid);
        Assert.True(report.HashMatches);
        Assert.Equal(4096, report.SizeBytes);
    }

    [Fact]
    public void Verify_WrongHash_Invalid()
    {
        var path = WriteFile("b.mp4", 4096);

        var report = ExportVerifier.Verify(path, "DEADBEEF");

        Assert.False(report.Valid);
        Assert.False(report.HashMatches);
    }

    [Fact]
    public void Verify_NoExpectedHash_ValidByExistence()
    {
        var path = WriteFile("c.mp4", 2048);

        var report = ExportVerifier.Verify(path);

        Assert.True(report.Valid);
        Assert.True(report.HashMatches);
        Assert.Equal(2048, report.SizeBytes);
    }

    [Fact]
    public void Verify_MissingFile_Invalid()
    {
        var report = ExportVerifier.Verify(Path.Combine(_dir, "none.mp4"), "X");

        Assert.False(report.Valid);
        Assert.Equal(string.Empty, report.Sha256);
        Assert.Equal(0, report.SizeBytes);
    }

    [Fact]
    public void Verify_HashIsLowercaseHex()
    {
        var path = WriteFile("d.mp4", 1024);

        var report = ExportVerifier.Verify(path);

        Assert.Equal(64, report.Sha256.Length);
        Assert.All(report.Sha256, c => Assert.True(Uri.IsHexDigit(c)));
        Assert.Equal(report.Sha256, report.Sha256.ToLowerInvariant());
    }

    [Fact]
    public void Verify_FfprobeSummary_Optional()
    {
        var path = WriteFile("e.mp4", 5000);

        var report = ExportVerifier.Verify(path, useFfprobe: true);

        Assert.True(report.Valid);
        if (report.FfprobeSummary is not null)
        {
            Assert.Contains("容器", report.FfprobeSummary);
        }
    }
}