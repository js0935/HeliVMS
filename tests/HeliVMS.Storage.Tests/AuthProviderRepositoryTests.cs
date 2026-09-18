using Microsoft.Data.Sqlite;

namespace HeliVMS.Storage.Tests;

/// <summary>M50（§14.7 #1）：企業身份提供者存取測試。</summary>
public class AuthProviderRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly AuthProviderRepository _repo;

    public AuthProviderRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-sso-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _repo = new AuthProviderRepository(_store);
    }

    public void Dispose()
    {
        _store.Dispose();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    [Fact]
    public void Add_ThenGet_PersistsAllFields()
    {
        var id = _repo.Add("Corp AD", "ldap", """{"Host":"dc"}""");

        var record = _repo.Get(id);
        Assert.NotNull(record);
        Assert.Equal("Corp AD", record!.Name);
        Assert.Equal("ldap", record.Kind);
        Assert.True(record.Enabled);
        Assert.Equal("""{"Host":"dc"}""", record.ConfigJson);
        Assert.False(string.IsNullOrEmpty(record.CreatedAt));
    }

    [Fact]
    public void Add_DisabledProvider_PersistsFlag()
    {
        var id = _repo.Add("IdP", "oidc", "{}", enabled: false);

        Assert.False(_repo.Get(id)!.Enabled);
    }

    [Fact]
    public void Add_DuplicateName_Throws()
    {
        _repo.Add("Corp", "oidc", "{}");

        Assert.Throws<SqliteException>(() => _repo.Add("corp", "ldap", "{}"));
    }

    [Fact]
    public void Add_InvalidKind_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _repo.Add("X", "saml", "{}"));
    }

    [Fact]
    public void List_OrdersByName()
    {
        _repo.Add("Zeta", "oidc", "{}");
        _repo.Add("Alpha", "oidc", "{}");

        var names = _repo.List().Select(p => p.Name);
        Assert.Equal(new[] { "Alpha", "Zeta" }, names);
    }

    [Fact]
    public void ListEnabled_FiltersByKindAndEnabled()
    {
        _repo.Add("Oidc On", "oidc", "{}");
        _repo.Add("Oidc Off", "oidc", "{}", enabled: false);
        _repo.Add("Ldap On", "ldap", "{}");

        var oidc = _repo.ListEnabled("oidc");
        Assert.Single(oidc);
        Assert.Equal("Oidc On", oidc[0].Name);

        Assert.Single(_repo.ListEnabled("ldap"));
    }

    [Fact]
    public void SetEnabled_UpdatesFlag()
    {
        var id = _repo.Add("Corp", "oidc", "{}");

        _repo.SetEnabled(id, false);
        Assert.False(_repo.Get(id)!.Enabled);

        _repo.SetEnabled(id, true);
        Assert.True(_repo.Get(id)!.Enabled);
    }

    [Fact]
    public void GetByName_IsCaseInsensitive()
    {
        _repo.Add("Corp IdP", "oidc", "{}");

        Assert.NotNull(_repo.GetByName("corp idp"));
        Assert.Null(_repo.GetByName("missing"));
    }

    [Fact]
    public void Delete_RemovesProvider()
    {
        var id = _repo.Add("Corp", "oidc", "{}");

        _repo.Delete(id);

        Assert.Null(_repo.Get(id));
        Assert.Empty(_repo.List());
    }
}
