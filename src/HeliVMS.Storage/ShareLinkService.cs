using System.IO;
using System.Security.Cryptography;

namespace HeliVMS.Storage;

/// <summary>分享連結種類（M51，§14.7 #4）。</summary>
public static class ShareKind
{
    public const string Segment = "segment";
    public const string Snapshot = "snapshot";
    public const string Evidence = "evidence";

    public static bool IsValid(string kind)
        => kind is Segment or Snapshot or Evidence;
}

/// <summary>拒絕原因（M51）：供 HTTP 狀態碼對映。</summary>
public static class ShareDeny
{
    public const string NotFound = "not-found";
    public const string Revoked = "revoked";
    public const string Expired = "expired";
    public const string Exhausted = "exhausted";
    public const string Password = "password";
    public const string Missing = "missing";
}

/// <summary>存取評估結果（M51，§14.7 #4）。</summary>
public sealed record ShareAccessResult(
    bool Ok,
    string Error,
    string Reason,
    string ResourcePath,
    string Kind,
    string Label)
{
    public static ShareAccessResult Fail(string error, string reason)
        => new(false, error, reason, string.Empty, string.Empty, string.Empty);
}

/// <summary>分享權杖產生（M51）：<see cref="RandomNumberGenerator"/> 32 bytes → base64url。</summary>
public static class ShareToken
{
    public const int ByteLength = 32;

    public static string Create()
        => Base64Url.Encode(RandomNumberGenerator.GetBytes(ByteLength));
}

/// <summary>
/// 外部安全共享服務（M51，§14.7 #4）：建立／評估（撤銷、到期、次數、密碼）／撤銷／清理。
/// </summary>
public sealed class ShareLinkService
{
    private readonly ShareLinkRepository _links;
    private readonly AuditLogRepository _audit;

    public ShareLinkService(SqliteStore store)
    {
        _links = new ShareLinkRepository(store);
        _audit = new AuditLogRepository(store);
    }

    public ShareLinkRecord Create(
        string kind,
        string resourcePath,
        IReadOnlyList<string> allowedRoots,
        DateTime createdUtc,
        string? label = null,
        string? createdBy = null,
        DateTime? expiresUtc = null,
        int maxUses = 0,
        string? password = null)
    {
        if (!ShareKind.IsValid(kind))
        {
            throw new ArgumentException("無效的分享種類", nameof(kind));
        }

        if (string.IsNullOrWhiteSpace(resourcePath))
        {
            throw new ArgumentException("資源路徑不可為空", nameof(resourcePath));
        }

        if (allowedRoots is null || allowedRoots.Count == 0)
        {
            throw new ArgumentException("需設定允許根目錄", nameof(allowedRoots));
        }

        if (maxUses < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxUses), "使用次數上限不可為負");
        }

        if (!File.Exists(resourcePath))
        {
            throw new FileNotFoundException("分享資源不存在", resourcePath);
        }

        if (!allowedRoots.Any(root => SharePath.IsWithinRoot(root, resourcePath)))
        {
            throw new UnauthorizedAccessException("資源路徑不在允許根目錄內");
        }

        var token = ShareToken.Create();
        var passwordHash = string.IsNullOrEmpty(password) ? null : PasswordHasher.Hash(password);
        var id = _links.Add(token, kind, resourcePath, label, passwordHash, createdUtc, createdBy, expiresUtc, maxUses);
        var actor = string.IsNullOrWhiteSpace(createdBy) ? "system" : createdBy;
        _audit.Record(actor, "share.create", AuditCategories.Share,
            targetType: "share", targetId: id, detail: $"kind={kind}", occurredAtUtc: createdUtc);
        return _links.Get(id)!;
    }

    public IReadOnlyList<ShareLinkRecord> List() => _links.List();

    public IReadOnlyList<ShareLinkRecord> ListActive(DateTime nowUtc) => _links.ListActive(nowUtc);

    public ShareLinkRecord? GetByToken(string token) => _links.GetByToken(token);

    public ShareAccessResult Evaluate(string token, DateTime nowUtc, string? password = null)
        => Evaluate(_links.GetByToken(token), nowUtc, password);

    public ShareAccessResult Evaluate(ShareLinkRecord? record, DateTime nowUtc, string? password = null)
    {
        if (record is null)
        {
            return ShareAccessResult.Fail("分享連結不存在", ShareDeny.NotFound);
        }

        if (record.Revoked)
        {
            return ShareAccessResult.Fail("分享連結已撤銷", ShareDeny.Revoked);
        }

        if (record.ExpiresAt is { Length: > 0 } exp && SqliteStore.FromIso(exp) <= nowUtc)
        {
            return ShareAccessResult.Fail("分享連結已過期", ShareDeny.Expired);
        }

        if (record.MaxUses > 0 && record.UseCount >= record.MaxUses)
        {
            return ShareAccessResult.Fail("分享連結已達使用次數上限", ShareDeny.Exhausted);
        }

        if (record.HasPassword &&
            (string.IsNullOrEmpty(password) || !PasswordHasher.Verify(password, record.PasswordHash!)))
        {
            return ShareAccessResult.Fail("密碼錯誤", ShareDeny.Password);
        }

        if (!File.Exists(record.ResourcePath))
        {
            return ShareAccessResult.Fail("分享資源已不存在", ShareDeny.Missing);
        }

        return new ShareAccessResult(true, string.Empty, string.Empty, record.ResourcePath, record.Kind, record.Label ?? string.Empty);
    }

    public void RecordUse(int id, DateTime usedUtc) => _links.IncrementUse(id, usedUtc);

    public void Revoke(int id)
    {
        _links.SetRevoked(id, true);
        _audit.Record("system", "share.revoke", AuditCategories.Share,
            targetType: "share", targetId: id);
    }

    public void Delete(int id)
    {
        _links.Delete(id);
        _audit.Record("system", "share.delete", AuditCategories.Share,
            targetType: "share", targetId: id);
    }

    public int PurgeExpired(DateTime nowUtc) => _links.PurgeExpired(nowUtc);
}
