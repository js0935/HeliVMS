namespace HeliVMS.Recording.Tests;

/// <summary>
/// Headless Agent 錄影看門狗的接線契約（M320，§11.1）。
///
/// <para>
/// Agent 沒有測試專案。這條路若漏接，症狀是「以服務模式部署時錄影停了完全沒人知道」——
/// 看門狗邏輯本身在 <see cref="RecordingWatchdogTests"/> 有行為測試，但把它註冊進服務、讓它讀到
/// 重啟設定、以及排程器把心跳寫到與看門狗相同的資料根目錄，這三條線只能靠原始碼契約釘住。
/// 斷言錨在行首（<c>^\s*</c>），因為單用 <c>Contains</c> 會被把該行註解掉蒙混過關。
/// </para>
/// </summary>
public sealed class AgentWatchdogContractTests
{
    private const string ProgramCode = "src/HeliVMS.Agent/Program.cs";
    private const string WorkerCode = "src/HeliVMS.Agent/WatchdogWorker.cs";

    /// <summary>看門狗必須註冊為 hosted service，否則服務模式根本不會跑它。</summary>
    [Fact]
    public void Agent註冊看門狗背景服務()
    {
        var program = Read(ProgramCode);
        Assert.Matches(@"(?m)^\s*services\.AddHostedService<WatchdogWorker>\(\);", program);
    }

    /// <summary>Agent 的排程器要與看門狗寫到同一份心跳（資料根目錄），否則看門狗永遠看到 Missing。</summary>
    [Fact]
    public void Agent排程器寫入資料根目錄心跳()
    {
        var program = Read(ProgramCode);
        Assert.Matches(@"(?m)^\s*heartbeatRoot: dataRoot", program);
        Assert.Matches(@"(?m)^\s*ownsRecording:", program);
    }

    /// <summary>看門狗要用 <c>RecordingWatchdogMonitor</c>（含轉折去重與稽核），不是各寫各的。</summary>
    [Fact]
    public void 看門狗使用監看器並讀取重啟設定()
    {
        var worker = Read(WorkerCode);
        Assert.Matches(@"(?m)^\s*_monitor = new RecordingWatchdogMonitor\(", worker);
        Assert.Contains("RecordingWatchdogMonitor.RestartExeKey", worker);
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
