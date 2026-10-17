namespace HeliVMS.Recording.Tests;

/// <summary>
/// Headless Agent 磁碟滿策略的接線契約（M319，§11.1）。
///
/// <para>
/// Agent 沒有測試專案，而這條路若漏接，症狀是「服務模式部署磁碟將滿不回報、滿了照樣開新錄影」——
/// 以 Windows 服務（無桌面、無 WebApi 主機）模式部署時沒有任何行為測試場域能發現。
/// 故以原始碼契約釘住：<c>RetentionWorker</c> 要呼叫 <c>DiskPressureCoordinator.RunConfigured</c>
/// 回報壓力等級，且 <c>Program.cs</c> 建立的排程器要接上停新錄閘門 <c>ShouldStopNewRecording</c>。
/// 斷言錨在行首（<c>^\s*</c>），因為單用 <c>Contains</c> 會被把該行註解掉蒙混過關。
/// </para>
/// </summary>
public sealed class AgentDiskPressureContractTests
{
    private const string ProgramCode = "src/HeliVMS.Agent/Program.cs";
    private const string WorkerCode = "src/HeliVMS.Agent/RetentionWorker.cs";

    /// <summary>Agent 保留迴圈每圈要回報磁碟壓力等級（桌面與 Agent 共用同一入口）。</summary>
    [Fact]
    public void Agent保留迴圈會回報磁碟壓力()
    {
        var worker = Read(WorkerCode);
        Assert.Matches(@"(?m)^\s*var level = DiskPressureCoordinator\.RunConfigured\(", worker);
    }

    /// <summary>Agent 的排程器要接上「停新錄」閘門，否則磁碟滿了仍會一直開新錄影。</summary>
    [Fact]
    public void Agent排程接上停新錄閘門()
    {
        var program = Read(ProgramCode);
        Assert.Matches(
            @"(?m)^\s*diskFull: \(\) => DiskPressureCoordinator\.ShouldStopNewRecording\(",
            program);
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
