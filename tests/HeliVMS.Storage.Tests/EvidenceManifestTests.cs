using System.IO;
using System.Text;

namespace HeliVMS.Storage.Tests;

/// <summary>M53（§14.7 #5）：證據 manifest 建立與完整性驗證。</summary>
public class EvidenceManifestTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _dir;
    private readonly SqliteStore _store;
    private readonly EvidenceManifestService _service;
    private readonly EvidenceManifestRepository _repo;

    public EvidenceManifestTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-evidence-{Guid.NewGuid():N}.db");
        _dir = Path.Combine(Path.GetTempPath(), $"helivms-evdir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "clip01.mp4"), new string('A', 4096), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(_dir, "snapshot.jpg"), new string('B', 2048), new UTF8Encoding(false));
        Directory.CreateDirectory(Path.Combine(_dir, "sub"));
        File.WriteAllText(Path.Combine(_dir, "sub", "meta.txt"), "metadata", new UTF8Encoding(false));

        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _service = new EvidenceManifestService(_store);
        _repo = new EvidenceManifestRepository(_store);
    }

    public void Dispose()
    {
        _store.Dispose();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }

        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void BuildRaw_IncludesAllFilesRecursively()
    {
        var json = _service.BuildRaw(_dir);
        Assert.Contains("clip01.mp4", json);
        Assert.Contains("snapshot.jpg", json);
        Assert.Contains("sub/meta.txt", json);
        Assert.Contains("sha256", json);
    }

    [Fact]
    public void Build_ContainsSignatureAndSigner()
    {
        var json = _service.Build(_dir);
        Assert.Contains("signature", json);
        Assert.Contains("signer", json);
        var (signed, valid) = _service.VerifySigned(json);
        Assert.True(signed);
        Assert.True(valid);
    }

    [Fact]
    public void Save_WriteManifestFile_AndDbRecord()
    {
        var record = _service.Save(_dir);
        var manifestPath = Path.Combine(_dir, EvidenceManifestService.ManifestFileName);
        Assert.True(File.Exists(manifestPath));
        Assert.True(record.Id > 0);
        Assert.Equal(_dir, record.DirectoryPath);
        Assert.Equal("created", record.Status);
        Assert.Equal(File.ReadAllText(manifestPath), record.ManifestJson);

        var loaded = _repo.GetByDirectory(_dir);
        Assert.NotNull(loaded);
        Assert.Equal(record.Id, loaded.Id);
    }

    [Fact]
    public void Verify_AllFilesOk_OverallOk()
    {
        _service.Save(_dir);
        var result = _service.Verify(_dir);
        Assert.True(result.OverallOk);
        Assert.Equal(3, result.OkCount);
        Assert.Equal(0, result.TamperedCount);
        Assert.Equal(0, result.MissingCount);
        Assert.Equal(0, result.ExtraCount);
    }

    [Fact]
    public void Verify_TamperedFile_Detected()
    {
        _service.Save(_dir);
        File.WriteAllText(Path.Combine(_dir, "snapshot.jpg"), new string('C', 2048), new UTF8Encoding(false));
        var result = _service.Verify(_dir);
        Assert.False(result.OverallOk);
        Assert.Equal(1, result.TamperedCount);
        Assert.Equal(2, result.OkCount);
        Assert.Contains(result.Items, i => i.Status == EvidenceItemStatus.Tampered && i.RelPath == "snapshot.jpg");
    }

    [Fact]
    public void Verify_MissingFile_Detected()
    {
        _service.Save(_dir);
        File.Delete(Path.Combine(_dir, "clip01.mp4"));
        var result = _service.Verify(_dir);
        Assert.False(result.OverallOk);
        Assert.Equal(1, result.MissingCount);
        Assert.Contains(result.Items, i => i.Status == EvidenceItemStatus.Missing && i.RelPath == "clip01.mp4");
    }

    [Fact]
    public void Verify_ExtraFile_Flagged()
    {
        _service.Save(_dir);
        File.WriteAllText(Path.Combine(_dir, "late-add.bin"), "later", new UTF8Encoding(false));
        var result = _service.Verify(_dir);
        Assert.Equal(1, result.ExtraCount);
        Assert.True(result.OverallOk);
        Assert.Contains(result.Items, i => i.Status == EvidenceItemStatus.Extra && i.RelPath == "late-add.bin");
    }

    [Fact]
    public void Verify_ManifestItselfEdited_SignatureRejectedAsTampered()
    {
        _service.Save(_dir);
        var manifestPath = Path.Combine(_dir, EvidenceManifestService.ManifestFileName);
        var text = File.ReadAllText(manifestPath);
        File.WriteAllText(manifestPath, text.Replace("sha256", "sha256x", StringComparison.Ordinal), new UTF8Encoding(false));
        var result = _service.Verify(_dir);
        Assert.False(result.OverallOk);
        Assert.Contains(result.Items, i => i.Status == EvidenceItemStatus.Tampered);
    }

    [Fact]
    public void VerifySigned_SignatureValid_FalseAfterEdit()
    {
        var signed = _service.Build(_dir);
        var (signedPresent, valid) = _service.VerifySigned(signed);
        Assert.True(signedPresent);
        Assert.True(valid);

        var edited = signed.Replace("\"snapshot.jpg\"", "\"snapshot2.jpg\"", StringComparison.Ordinal);
        var (_, editedValid) = _service.VerifySigned(edited);
        Assert.False(editedValid);
    }

    [Fact]
    public void Verify_NoManifest_Throws()
    {
        Assert.Throws<FileNotFoundException>(() => _service.Verify(_dir));
    }

    [Fact]
    public void EmptyDirectory_BuildThrows()
    {
        var empty = Path.Combine(Path.GetTempPath(), $"helivms-evempty-{Guid.NewGuid():N}");
        Directory.CreateDirectory(empty);
        try
        {
            Assert.Throws<ArgumentException>(() => _service.BuildRaw(empty));
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
        }
    }

    [Fact]
    public void Save_Twice_UpsertsSameDirectory()
    {
        _service.Save(_dir);
        var record2 = _service.Save(_dir);
        Assert.Single(_repo.List());
    }

    [Fact]
    public void Repository_RoundTrip_SetVerified_Delete()
    {
        var record = _repo.Upsert(_dir, "{}", "created");
        _repo.SetVerified(record, "verified");
        var loaded = _repo.GetByDirectory(_dir);
        Assert.Equal("verified", loaded!.Status);
        Assert.NotNull(loaded.LastVerifiedAt);
        _repo.Delete(record);
        Assert.Null(_repo.GetByDirectory(_dir));
    }
}