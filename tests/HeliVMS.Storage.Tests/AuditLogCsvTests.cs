using HeliVMS.Storage;

namespace HeliVMS.Storage.Tests;

/// <summary>
/// 稽核 CSV 匯出格式（M109／§14.1）。
///
/// <para>
/// WebApi 的 <c>/api/audit/export.csv</c> 與桌面端稽核視窗共用 <see cref="AuditLogCsv.Render"/>，
/// 這裡釘住的是「稽核證據」的兩件事：欄位順序不可變（外部對帳以此解析），
/// 以及含逗號／引號／換行的明細必須依 RFC 4180 跳脫（否則一筆多欄的 detail 會把整份 CSV 的欄位數打亂）。
/// </para>
/// </summary>
public sealed class AuditLogCsvTests
{
    private static AuditLogEntry Entry(
        string actor,
        string action,
        string category,
        string? detail = null,
        long? targetId = null,
        string? targetType = null) =>
        new(1, new DateTime(2026, 1, 2, 3, 4, 5, 678, DateTimeKind.Utc), actor, action, category, targetType, targetId, detail);

    [Fact]
    public void 標頭欄位順序固定()
    {
        var csv = AuditLogCsv.Render([]);

        Assert.Equal("id,occurred_at,actor,action,category,target_type,target_id,detail\n", csv);
    }

    [Fact]
    public void 一般明細逐欄輸出()
    {
        var csv = AuditLogCsv.Render([Entry("alice", "login", AuditCategories.Auth, "ok", 7, "user")]);

        var line = Assert.Single(csv.TrimEnd('\n').Split('\n').Skip(1));
        Assert.Equal("1,2026-01-02T03:04:05.678Z,alice,login,auth,user,7,ok", line);
    }

    [Fact]
    public void 空欄位輸出空字串以維持欄位數()
    {
        var csv = AuditLogCsv.Render([Entry("bob", "export", AuditCategories.Export)]);

        var line = Assert.Single(csv.TrimEnd('\n').Split('\n').Skip(1));
        Assert.Equal("1,2026-01-02T03:04:05.678Z,bob,export,export,,,", line);
    }

    [Theory]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("line1\nline2", "\"line1\nline2\"")]
    [InlineData("plain", "plain")]
    public void 特殊字元依RFC4180跳脫(string input, string expected)
    {
        Assert.Equal(expected, AuditLogCsv.Escape(input));
    }

    [Fact]
    public void 明細含逗號不會增加欄位數()
    {
        var csv = AuditLogCsv.Render([
            Entry("carol", "share", AuditCategories.Share, "url=https://x, expires=fri"),
        ]);

        var line = Assert.Single(csv.TrimEnd('\n').Split('\n').Skip(1));
        // 8 欄：即使 detail 內含逗號，跳脫後仍必須只有 7 個分隔逗號可被切割。
        Assert.Equal("1,2026-01-02T03:04:05.678Z,carol,share,share,,,\"url=https://x, expires=fri\"", line);
    }
}
