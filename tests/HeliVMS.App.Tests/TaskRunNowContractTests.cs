using System.Text.RegularExpressions;

namespace HeliVMS.App.Tests;

/// <summary>
/// 排程任務「立即執行」契約：任務列有執行鈕、重開機先確認、宿主手動觸發與輪詢共用
/// 同一條路（先記後跑）但搶不到閘門時排隊而不是跳過——按了按鈕就該執行。
/// </summary>
public sealed class TaskRunNowContractTests
{
    private const string Code = "src/HeliVMS.App/RulesWindow.xaml.cs";
    private const string Xaml = "src/HeliVMS.App/RulesWindow.xaml";
    private const string Host = "src/HeliVMS.App/Services/MaintenanceTaskHost.cs";
    private const string Main = "src/HeliVMS.App/MainWindow.xaml.cs";
    private const string I18n = "src/HeliVMS.Storage/I18n.cs";

    [Fact]
    public void 任務列有執行鈕且重開機先確認()
    {
        Assert.Equal("OnTaskRunClicked", WiredHandler("TaskRunButton"));

        var body = BodyOf("void OnTaskRunClicked");
        Assert.Contains("MaintenanceTaskKind.Reboot", body);
        Assert.Contains("MessageBox.Show", body);
        Assert.Contains("Tasks.RunRebootConfirm", body);
        Assert.True(
            body.IndexOf("Tasks.RunRebootConfirm", StringComparison.Ordinal)
            < body.IndexOf("await run(", StringComparison.Ordinal),
            "重開機的確認必須在真正觸發之前");
    }

    [Fact]
    public void 主視窗把宿主的立即執行交給規則視窗()
    {
        var body = SourceContract.BodyOf(SourceContract.Read(Main), "OpenRulesWindow");
        Assert.Contains("runTaskNow", body);
        Assert.Contains("RunNowAsync", body);
    }

    [Fact]
    public void 手動執行與輪詢共用同一條先記後跑的路()
    {
        // RunNowAsync 不得自帶第二份執行邏輯：它必須呼叫 RunTaskAsync——
        // 先記後跑、稽核先於動作都在那條路上，兩份實作一定漂移。
        var body = BodyOf("async Task<string?> RunNowAsync");
        Assert.Contains("await RunTaskAsync(task)", body);

        // 手動觸發要排隊等閘門（WaitAsync），不是 TryWait 跳過：按了按鈕就該執行。
        Assert.Contains("_gate.WaitAsync()", body);
    }

    [Fact]
    public void 執行相關文字存在於三種語言()
    {
        var catalogue = SourceContract.Read(I18n);
        foreach (var key in new[] { "Tasks.Run", "Tasks.RunRebootConfirm", "Tasks.RunDone", "Tasks.RunUnavailable" })
        {
            Assert.Equal(3, CountKeys(catalogue, key));
        }
    }

    private static int CountKeys(string catalogue, string key)
    {
        var token = "[\"" + key + "\"]";
        var count = 0;
        var i = 0;
        while ((i = catalogue.IndexOf(token, i, StringComparison.Ordinal)) >= 0)
        {
            count++;
            i += token.Length;
        }

        return count;
    }

    private static string BodyOf(string signature)
    {
        var source = SourceContract.Read(Host);
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        if (start < 0)
        {
            source = SourceContract.Read(Code);
            start = source.IndexOf(signature, StringComparison.Ordinal);
        }

        Assert.True(start >= 0, $"找不到 {signature}");
        return source[start..];
    }

    private static string WiredHandler(string buttonName)
    {
        var xaml = SourceContract.Read(Xaml);
        var at = xaml.IndexOf("x:Name=\"" + buttonName + "\"", StringComparison.Ordinal);
        Assert.True(at >= 0, $"找不到 {buttonName}");
        var match = Regex.Match(xaml[at..], "Click=\"(\\w+)\"");
        Assert.True(match.Success, $"{buttonName} 沒有 Click 接線");
        return match.Groups[1].Value;
    }
}