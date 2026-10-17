namespace HeliVMS.Recording.Tests;

using System.Text.RegularExpressions;

/// <summary>
/// Headless Agent 完整性自我檢查迴圈的接線契約（M318）。
///
/// <para>
/// 與 <see cref="AgentRetentionContractTests"/> 同理：Agent 沒有測試專案，而這條迴圈若被移除，
/// 症狀是「服務模式部署下損壞／遺失的錄影永遠不會被發現」——磁碟掉線、外部誤刪都不會留下任何訊息，
/// 直到真的要調閱時才發現是空的，而現場早已過去。故以原始碼契約釘住：<c>Program.cs</c> 要註冊
/// <c>IntegrityWorker</c>，且該 worker 要走共用協調器 <c>MediaIntegrityCoordinator.RunConfigured</c>
/// （與桌面同一條路徑），而不是自己另寫一套。
/// </para>
/// <para>斷言錨在行首（<c>^\s*</c>），因為單用 <c>Contains</c> 會被把該行註解掉蒙混過關。</para>
/// </summary>
public sealed class AgentIntegrityContractTests
{
    private const string ProgramCode = "src/HeliVMS.Agent/Program.cs";
    private const string WorkerCode = "src/HeliVMS.Agent/IntegrityWorker.cs";

    [Fact]
    public void Agent會註冊完整性檢查背景服務()
    {
        var program = Read(ProgramCode);
        Assert.Matches(@"(?m)^\s*services\.AddHostedService<IntegrityWorker>\(\);", program);
    }

    [Fact]
    public void Agent完整性迴圈走共用協調器()
    {
        var worker = Read(WorkerCode);
        Assert.Matches(@"(?m)^\s*var report = MediaIntegrityCoordinator\.RunConfigured\(", worker);
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
