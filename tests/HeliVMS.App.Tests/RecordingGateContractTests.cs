namespace HeliVMS.App.Tests;

/// <summary>
/// 錄影閘門入口契約（M237／§19.4「合併檢查，避免遠程繞過」）。
///
/// <para>
/// 錄影是唯一會持續寫磁碟的動作，授權的「通道上限」只落在
/// <c>LicenseService.CheckRecording</c>。今天呼叫點剛好只有兩個（排程與人工），
/// 兩處都問了閘門——但這是「目前剛好」，不是「結構上不可能漏」：
/// 將來加一條事件觸發錄影、或讓 WebApi 直接開錄，都能在
/// <c>SegmentRecorder</c> 建構處多一行就繞過上限。
/// </para>
///
/// <para>
/// 與 <c>LicenseEntryPointContractTests</c> 同一個形狀：桌面沒有 WPF 執行期測試，
/// 所以用契約把「唯一真正開始寫分段的地方」綁死在閘門後面。
/// </para>
/// </summary>
public sealed class RecordingGateContractTests
{
    /// <summary>（檔案, 方法）→ 建立錄影器前必須呼叫的閘門。</summary>
    private static readonly (string File, string Method)[] Sites =
    [
        ("src/HeliVMS.Recording/RecordingScheduler.cs", "ReconcileAsync"),
        ("src/HeliVMS.App/Services/ChannelSession.cs", "SetRecordingAsync"),
    ];

    public static TheoryData<string, string> RecordingStartSites
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var (file, method) in Sites)
            {
                data.Add(file, method);
            }

            return data;
        }
    }

    private const string GateCall = "CheckRecording";
    private const string Creation = "new SegmentRecorder(";

    [Theory]
    [MemberData(nameof(RecordingStartSites))]
    public void 錄影起點必須先問錄影閘門(string file, string method)
    {
        var body = SourceContract.BodyOf(SourceContract.Read(file), method);
        Assert.NotEqual(string.Empty, body);

        var gate = body.IndexOf(GateCall + "(", StringComparison.Ordinal);
        var create = body.IndexOf(Creation, StringComparison.Ordinal);

        Assert.True(gate >= 0, $"{file} 的 {method} 沒有問錄影閘門（{GateCall}）");
        Assert.True(create >= 0, $"{file} 的 {method} 找不到建立錄影器的位置，契約表可能已漂移");
        Assert.True(
            gate < create,
            $"{file} 的 {method} 先建立錄影器才問閘門：上限在開始寫檔之後才檢查等於沒檢查");
    }

    [Theory]
    [MemberData(nameof(RecordingStartSites))]
    public void 閘門結果必須真的被採納(string file, string method)
    {
        var body = SourceContract.BodyOf(SourceContract.Read(file), method);

        // 只呼叫不判斷等於沒擋：必須有一個依 Allowed 分支的敘述擋在建立之前。
        Assert.Matches(@"\w+\.Allowed", body);
        Assert.Matches(@"!\s*\w+\.Allowed", body);
    }

    [Theory]
    [MemberData(nameof(RecordingStartSites))]
    public void 閘門要帶來源_稽核才分得出人工與排程(string file, string method)
    {
        // CheckRecording 的稽核以「頻道＋理由＋來源」去重；不帶來源就會被混算成同一筆。
        var body = SourceContract.BodyOf(SourceContract.Read(file), method);

        Assert.Matches(@"RecordingGateSources\.\w+", body);
    }

    [Fact]
    public void 每個錄影器建立點都要列入契約()
    {
        // 新增一條錄影路徑卻沒問閘門，等於通道上限被繞過。逐一比對建立點與契約表。
        var declared = Sites.Select(site => SiteKey(site.File, site.Method)).ToHashSet(StringComparer.Ordinal);

        var undeclared = RecorderCreations()
            .Where(site => !declared.Contains(SiteKey(site.File, site.Method)))
            .Select(site => SiteKey(site.File, site.Method))
            .OrderBy(site => site, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            undeclared.Count == 0,
            "這些地方建立 SegmentRecorder 但未列入 RecordingStartSites（必須先問錄影閘門）："
            + string.Join("、", undeclared));
    }

    [Fact]
    public void 契約表涵蓋所有實際的錄影器建立點()
    {
        // 反向：契約表列了不存在的建立點，等於這條檢查在自我滿足。
        var actual = RecorderCreations()
            .Select(site => SiteKey(site.File, site.Method))
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(actual);
        foreach (var (file, method) in Sites)
        {
            Assert.Contains(SiteKey(file, method), actual);
        }
    }

    private static string SiteKey(string file, string method) => $"{file.Replace('\\', '/')}::{method}";

    /// <summary>整個 <c>src</c> 底下所有 <c>new SegmentRecorder(</c> 的所在檔案與其外層方法。</summary>
    private static List<(string File, string Method)> RecorderCreations()
    {
        var sites = new List<(string, string)>();
        var root = Path.Combine(SourceContract.RepoRoot(), "src");

        foreach (var path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            var source = File.ReadAllText(path);
            for (var at = source.IndexOf(Creation, StringComparison.Ordinal); at >= 0; at = source.IndexOf(Creation, at + 1, StringComparison.Ordinal))
            {
                sites.Add((Path.GetRelativePath(SourceContract.RepoRoot(), path), SourceContract.EnclosingMethod(source, at)));
            }
        }

        return sites;
    }
}
