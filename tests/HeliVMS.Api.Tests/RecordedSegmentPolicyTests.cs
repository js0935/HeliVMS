using HeliVMS.WebApi;
using Microsoft.Extensions.Configuration;

namespace HeliVMS.Api.Tests;

/// <summary>
/// <see cref="RecordedSegmentPolicy"/>（§14.3 串流）決定遠端能不能讀某個錄影分段檔。
/// 呼叫端只給分段編號、路徑一律取自索引，所以這個政策的每一條邊界都對應一個
/// 「能不能讀到不該讀的檔案」的問題——而它錯的方向永遠是放行。
/// </summary>
public sealed class RecordedSegmentPolicyTests
{
    private static string NewRoot() => Path.Combine(Path.GetTempPath(), $"helivms-seg-{Guid.NewGuid():N}");

    private static IConfiguration Config(params (string Key, string Value)[] entries)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(entries.ToDictionary(e => e.Key, e => (string?)e.Value))
            .Build();

    [Fact]
    public void 沒有根目錄時一律拒絕()
    {
        var policy = new RecordedSegmentPolicy(Array.Empty<string>());

        Assert.Empty(policy.Roots);
        Assert.False(policy.IsRecordedSegmentPath(null));
        Assert.False(policy.IsRecordedSegmentPath("   "));
        Assert.False(policy.IsRecordedSegmentPath(Path.Combine(NewRoot(), "seg.mp4")));
        Assert.Throws<UnauthorizedAccessException>(
            () => policy.EnsureRecordedSegmentPath(Path.Combine(NewRoot(), "seg.mp4")));
        Assert.Contains(RecordedSegmentPolicy.RecordingsRootConfigKey, policy.DenyMessage("x"));
    }

    [Fact]
    public void 明確設定的錄影根目錄優先於資料目錄()
    {
        var explicitRoot = NewRoot();
        var config = Config(
            (RecordedSegmentPolicy.RecordingsRootConfigKey, explicitRoot),
            (RecordedSegmentPolicy.DataRootConfigKey, @"C:\OtherData"));

        var policy = new RecordedSegmentPolicy(config);

        Assert.Single(policy.Roots);
        Assert.Equal(Path.GetFullPath(explicitRoot), policy.Roots[0]);
    }

    [Fact]
    public void 未設定錄影根目錄時使用資料目錄下的recordings()
    {
        var config = Config((RecordedSegmentPolicy.DataRootConfigKey, @"C:\HeliData"));

        var policy = new RecordedSegmentPolicy(config);

        Assert.Single(policy.Roots);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(@"C:\HeliData", RecordedSegmentPolicy.RecordingsDirName)),
            policy.Roots[0]);
    }

    [Fact]
    public void 兩者都未設定時回退到預設資料目錄()
    {
        var policy = new RecordedSegmentPolicy(Config());

        Assert.Single(policy.Roots);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(RecordedSegmentPolicy.DefaultDataRoot, RecordedSegmentPolicy.RecordingsDirName)),
            policy.Roots[0]);
    }

    [Fact]
    public void 只允許真正位於根目錄內的路徑()
    {
        var root = NewRoot();
        var policy = new RecordedSegmentPolicy(new[] { root });

        Assert.True(policy.IsRecordedSegmentPath(root));
        Assert.True(policy.IsRecordedSegmentPath(Path.Combine(root, "ch001", "20260101", "seg.mp4")));

        Assert.False(policy.IsRecordedSegmentPath(root + "-evil"));
        Assert.False(policy.IsRecordedSegmentPath(Path.Combine(root, "..", "escape.mp4")));
        Assert.False(policy.IsRecordedSegmentPath(Path.GetTempPath()));
    }

    [Fact]
    public void 根目錄會正規化並去重()
    {
        var root = NewRoot();
        var policy = new RecordedSegmentPolicy(new[] { root + Path.DirectorySeparatorChar, root.ToUpperInvariant() });

        Assert.Single(policy.Roots);
        Assert.Equal(Path.GetFullPath(root), policy.Roots[0]);
    }

    [Fact]
    public void 不允許的路徑會擲出未授權例外()
    {
        var root = NewRoot();
        var policy = new RecordedSegmentPolicy(new[] { root });

        Assert.Throws<UnauthorizedAccessException>(
            () => policy.EnsureRecordedSegmentPath(Path.Combine(root, "..", "escape.mp4")));
    }
}
