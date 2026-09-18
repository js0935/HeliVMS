using System.IO;

namespace HeliVMS.Storage.Tests;

/// <summary>M51（§14.7 #4）：外部安全共享連結服務與路徑邊界測試。</summary>
public class ShareLinkTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _rootA;
    private readonly string _rootB;
    private readonly string _fileA;
    private readonly SqliteStore _store;
    private readonly ShareLinkService _service;

    public ShareLinkTests()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), $"helivms-share-{Guid.NewGuid():N}");
        _rootA = Path.Combine(baseDir, "in");
        _rootB = Path.Combine(baseDir, "out");
        Directory.CreateDirectory(_rootA);
        Directory.CreateDirectory(_rootB);
        _fileA = Path.Combine(_rootA, "clip.mp4");
        File.WriteAllBytes(_fileA, new byte[] { 1, 2, 3, 4 });

        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-share-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _service = new ShareLinkService(_store);
    }

    public void Dispose()
    {
        _store.Dispose();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }

        try
        {
            Directory.Delete(Path.GetDirectoryName(_rootA)!, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Create_ReturnsTokenAndPersists()
    {
        var record = _service.Create(ShareKind.Segment, _fileA, new[] { _rootA }, DateTime.UtcNow, label: "剪輯");

        Assert.True(record.Token.Length >= 40);
        Assert.Equal(ShareKind.Segment, record.Kind);
        Assert.Equal(_fileA, record.ResourcePath);
        Assert.Equal("剪輯", record.Label);
        Assert.Equal(0, record.UseCount);
        Assert.False(record.Revoked);
        Assert.False(record.HasPassword);

        Assert.NotNull(_service.GetByToken(record.Token));
    }

    [Fact]
    public void Create_TokensAreUnique()
    {
        var a = _service.Create(ShareKind.Segment, _fileA, new[] { _rootA }, DateTime.UtcNow);
        var b = _service.Create(ShareKind.Segment, _fileA, new[] { _rootA }, DateTime.UtcNow);

        Assert.NotEqual(a.Token, b.Token);
    }

    [Fact]
    public void ShareToken_HasEnoughEntropy()
    {
        var tokens = Enumerable.Range(0, 50).Select(_ => ShareToken.Create()).ToHashSet();

        Assert.Equal(50, tokens.Count);
        Assert.All(tokens, t => Assert.True(t.Length >= 40));
    }

    [Fact]
    public void Create_PathOutsideAllowedRoots_Throws()
    {
        Assert.Throws<UnauthorizedAccessException>(
            () => _service.Create(ShareKind.Segment, _fileA, new[] { _rootB }, DateTime.UtcNow));
    }

    [Fact]
    public void Create_MissingFile_Throws()
    {
        var ghost = Path.Combine(_rootA, "ghost.mp4");

        Assert.Throws<FileNotFoundException>(
            () => _service.Create(ShareKind.Segment, ghost, new[] { _rootA }, DateTime.UtcNow));
    }

    [Fact]
    public void Create_InvalidKind_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => _service.Create("live", _fileA, new[] { _rootA }, DateTime.UtcNow));
    }

    [Fact]
    public void Create_NoAllowedRoots_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => _service.Create(ShareKind.Segment, _fileA, Array.Empty<string>(), DateTime.UtcNow));
    }

    [Fact]
    public void Create_NegativeMaxUses_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => _service.Create(ShareKind.Segment, _fileA, new[] { _rootA }, DateTime.UtcNow, maxUses: -1));
    }

    [Fact]
    public void Evaluate_ValidLink_Succeeds()
    {
        var record = _service.Create(ShareKind.Snapshot, _fileA, new[] { _rootA }, DateTime.UtcNow);

        var result = _service.Evaluate(record.Token, DateTime.UtcNow);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(_fileA, result.ResourcePath);
        Assert.Equal(ShareKind.Snapshot, result.Kind);
    }

    [Fact]
    public void Evaluate_UnknownToken_NotFound()
    {
        var result = _service.Evaluate("does-not-exist", DateTime.UtcNow);

        Assert.False(result.Ok);
        Assert.Equal(ShareDeny.NotFound, result.Reason);
    }

    [Fact]
    public void Evaluate_Revoked_IsDenied()
    {
        var record = _service.Create(ShareKind.Segment, _fileA, new[] { _rootA }, DateTime.UtcNow);
        _service.Revoke(record.Id);

        var result = _service.Evaluate(record.Token, DateTime.UtcNow);

        Assert.False(result.Ok);
        Assert.Equal(ShareDeny.Revoked, result.Reason);
    }

    [Fact]
    public void Evaluate_Expired_IsDenied()
    {
        var now = DateTime.UtcNow;
        var record = _service.Create(ShareKind.Segment, _fileA, new[] { _rootA }, now, expiresUtc: now.AddMinutes(10));

        Assert.True(_service.Evaluate(record.Token, now.AddMinutes(5)).Ok);

        var expired = _service.Evaluate(record.Token, now.AddMinutes(11));
        Assert.False(expired.Ok);
        Assert.Equal(ShareDeny.Expired, expired.Reason);
    }

    [Fact]
    public void Evaluate_MaxUsesExhausted_IsDenied()
    {
        var record = _service.Create(ShareKind.Segment, _fileA, new[] { _rootA }, DateTime.UtcNow, maxUses: 2);

        _service.RecordUse(record.Id, DateTime.UtcNow);
        _service.RecordUse(record.Id, DateTime.UtcNow);

        var result = _service.Evaluate(record.Token, DateTime.UtcNow);
        Assert.False(result.Ok);
        Assert.Equal(ShareDeny.Exhausted, result.Reason);
    }

    [Fact]
    public void Evaluate_PasswordRequired()
    {
        var record = _service.Create(ShareKind.Segment, _fileA, new[] { _rootA }, DateTime.UtcNow, password: "s3cret");

        var missing = _service.Evaluate(record.Token, DateTime.UtcNow);
        Assert.False(missing.Ok);
        Assert.Equal(ShareDeny.Password, missing.Reason);

        var wrong = _service.Evaluate(record.Token, DateTime.UtcNow, "nope");
        Assert.False(wrong.Ok);
        Assert.Equal(ShareDeny.Password, wrong.Reason);

        var correct = _service.Evaluate(record.Token, DateTime.UtcNow, "s3cret");
        Assert.True(correct.Ok, correct.Error);
    }

    [Fact]
    public void Evaluate_ResourceRemoved_IsDenied()
    {
        var record = _service.Create(ShareKind.Segment, _fileA, new[] { _rootA }, DateTime.UtcNow);
        File.Delete(_fileA);

        var result = _service.Evaluate(record.Token, DateTime.UtcNow);

        Assert.False(result.Ok);
        Assert.Equal(ShareDeny.Missing, result.Reason);

        File.WriteAllBytes(_fileA, new byte[] { 1, 2, 3, 4 });
    }

    [Fact]
    public void RecordUse_IncrementsCountAndTimestamp()
    {
        var record = _service.Create(ShareKind.Segment, _fileA, new[] { _rootA }, DateTime.UtcNow);
        var used = DateTime.UtcNow;

        _service.RecordUse(record.Id, used);

        var updated = _service.GetByToken(record.Token)!;
        Assert.Equal(1, updated.UseCount);
        Assert.NotNull(updated.LastUsedAt);
    }

    [Fact]
    public void ListActive_ExcludesRevokedAndExpired()
    {
        var now = DateTime.UtcNow;
        var active = _service.Create(ShareKind.Segment, _fileA, new[] { _rootA }, now);
        var revoked = _service.Create(ShareKind.Segment, _fileA, new[] { _rootA }, now);
        _service.Create(ShareKind.Segment, _fileA, new[] { _rootA }, now, expiresUtc: now.AddMinutes(-1));
        _service.Revoke(revoked.Id);

        var list = _service.ListActive(now);

        Assert.Equal(new[] { active.Id }, list.Select(x => x.Id));
    }

    [Fact]
    public void PurgeExpired_RemovesOnlyExpired()
    {
        var now = DateTime.UtcNow;
        _service.Create(ShareKind.Segment, _fileA, new[] { _rootA }, now);
        _service.Create(ShareKind.Segment, _fileA, new[] { _rootA }, now, expiresUtc: now.AddMinutes(-5));
        _service.Create(ShareKind.Segment, _fileA, new[] { _rootA }, now, expiresUtc: now.AddMinutes(-1));

        var removed = _service.PurgeExpired(now);

        Assert.Equal(2, removed);
        Assert.Single(_service.List());
    }

    [Theory]
    [InlineData(@"C:\data", @"C:\data\clip.mp4", true)]
    [InlineData(@"C:\data", @"C:\data", true)]
    [InlineData(@"C:\data", @"C:\data-other\clip.mp4", false)]
    [InlineData(@"C:\data", @"C:\other\clip.mp4", false)]
    [InlineData(@"C:\data", @"C:\data\..\secret.mp4", false)]
    [InlineData(@"C:\data\", @"C:\data\sub\clip.mp4", true)]
    public void SharePath_EnforcesRootBoundary(string root, string path, bool expected)
    {
        Assert.Equal(expected, SharePath.IsWithinRoot(root, path));
    }

    [Fact]
    public void SharePath_EmptyInputs_AreRejected()
    {
        Assert.False(SharePath.IsWithinRoot(string.Empty, @"C:\data\x.mp4"));
        Assert.False(SharePath.IsWithinRoot(@"C:\data", string.Empty));
    }
}
