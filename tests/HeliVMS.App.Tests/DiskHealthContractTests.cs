using HeliVMS.Storage;

namespace HeliVMS.App.Tests;

/// <summary>
/// 錄影磁碟健康（S.M.A.R.T.）的接線契約：桌面每小時迴圈與 Headless Agent 都要問同一件事，
/// 而且設定頁必須把「讀不到」顯示成「未提供」而不是「健康」。
/// </summary>
/// <remarks>
/// <para>
/// 「碟要壞了」與「碟滿了」是兩種都會無聲吃掉錄影的失效，處置相反（換碟 vs 清檔）。這一層的
/// 錯誤幾乎都不會報錯：不問就是不知道、問了顯示錯就是一片綠。等到回放讀不出來才發現，
/// 往往已經少了好幾天的錄影。
/// </para>
/// </remarks>
public sealed class DiskHealthContractTests
{
    private const string MainCode = "src/HeliVMS.App/MainWindow.xaml.cs";
    private const string SettingsCode = "src/HeliVMS.App/SettingsWindow.xaml.cs";
    private const string SettingsXaml = "src/HeliVMS.App/SettingsWindow.xaml";
    private const string AgentCode = "src/HeliVMS.Agent/RetentionWorker.cs";

    private static readonly string[] Keys =
    [
        "Settings.DiskHealthLabel",
        "Settings.DiskHealthHealthy",
        "Settings.DiskHealthWarning",
        "Settings.DiskHealthFailing",
        "Settings.DiskHealthUnknown",
        "Settings.DiskHealthLine",
        "Settings.DiskHealthModel",
        "Settings.DiskHealthTemperature",
        "Settings.DiskHealthEmpty",
        "Main.DiskHealthFailing",
        "Main.DiskHealthWarning",
    ];

    [Fact]
    public void 桌面與Agent的每小時迴圈都會查磁碟健康()
    {
        // 兩個部署模式（桌面／服務）都必須問；只接一邊的症狀是「同一台機器換成服務模式部署
        // 之後就再也沒有人檢查硬碟」。
        Assert.Contains(
            "DiskHealthCoordinator.RunConfigured",
            SourceContract.BodyOf(SourceContract.Read(MainCode), "RunRetentionLoopAsync"));

        Assert.Contains(
            "DiskHealthCoordinator.RunConfigured",
            SourceContract.BodyOf(SourceContract.Read(AgentCode), "RunOnce"));
    }

    [Fact]
    public void 設定頁顯示逐顆健康且讀不到時不是健康()
    {
        Assert.Contains("x:Name=\"DiskHealthText\"", SourceContract.Read(SettingsXaml));

        var source = SourceContract.Read(SettingsCode);
        Assert.Contains("ReloadDiskHealth()", SourceContract.BodyOf(source, "ReloadUsage"));

        var body = SourceContract.BodyOf(source, "ReloadDiskHealth");
        Assert.Contains("DiskHealthCoordinator.ReadConfigured", body);
        Assert.Contains("DiskHealth.LevelKey(volume.Level)", body);

        // 沒設定錄影磁碟時說「—」，不要把空清單印成健康。
        Assert.Contains("Settings.DiskHealthEmpty", body);
    }

    [Fact]
    public void 磁碟健康的文案三語齊備()
    {
        foreach (var lang in I18n.Languages)
        {
            foreach (var key in Keys)
            {
                Assert.NotEqual(key, I18n.Get(lang, key));
            }
        }
    }

    [Fact]
    public void 四個等級各有專屬文案且未知不等於健康()
    {
        var keys = Enum.GetValues<DiskHealthLevel>()
            .Select(DiskHealth.LevelKey)
            .ToList();

        Assert.Equal(4, keys.Count);
        Assert.Equal(4, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.NotEqual(DiskHealth.LevelKey(DiskHealthLevel.Unknown), DiskHealth.LevelKey(DiskHealthLevel.Healthy));
    }
}
