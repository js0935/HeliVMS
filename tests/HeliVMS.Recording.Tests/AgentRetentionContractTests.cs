namespace HeliVMS.Recording.Tests;

using System.Text.RegularExpressions;

/// <summary>
/// Headless Agent 保留迴圈的接線契約（M316）。
///
/// Agent 沒有測試專案，而這條迴圈若被移除，症狀是「服務模式部署磁碟無上限成長、log 全乾淨」——
/// 沒有行為測試場域能發現（桌面／WebApi 各自有自己的路徑，服務模式兩者都不在）。
/// 故以原始碼契約釘住：<c>Program.cs</c> 要註冊 <c>RetentionWorker</c>，
/// 且該 worker 要走共用協調器 <c>RetentionCoordinator.RunConfigured</c>（共用設定與清理路徑），
/// 而不是自己另寫一套讀設定／清理的邏輯（那正是過去 Agent 完全沒有清理的成因）。
/// 這是「真縫隙缺測試」時的替代手段，非樂見的常態。
///
/// 註冊與呼叫的斷言都錨在行首（<c>^\s*</c>），因為單用 <c>Contains</c> 會被
/// 把該行註解掉（<c>// services.AddHostedService&lt;RetentionWorker&gt;();</c>）蒙混過關。
/// </summary>
public sealed class AgentRetentionContractTests
{
    private const string ProgramCode = "src/HeliVMS.Agent/Program.cs";
    private const string WorkerCode = "src/HeliVMS.Agent/RetentionWorker.cs";

    [Fact]
    public void Agent會註冊保留清理背景服務()
    {
        var program = Read(ProgramCode);
        Assert.Matches(@"(?m)^\s*services\.AddHostedService<RetentionWorker>\(\);", program);
    }

    [Fact]
    public void Agent保留迴圈走共用協調器並每圈從設定重推根目錄()
    {
        var worker = Read(WorkerCode);
        Assert.Matches(@"(?m)^\s*var sweep = RetentionCoordinator\.RunConfigured\(", worker);
        Assert.Matches(@"(?m)^\s*var dataRoot = DataRootLayout\.ResolveRoot\(", worker);
    }

    private static string Read(string relativePath)
        => File.ReadAllText(Path.Combine(RepoRoot(), relativePath));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HeliVMS.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException("自測試輸出往上找不到 HeliVMS.slnx，無法驗證 Agent 契約。");
    }
}
