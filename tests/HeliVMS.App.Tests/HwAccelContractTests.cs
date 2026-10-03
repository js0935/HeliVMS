using System.Text.RegularExpressions;

namespace HeliVMS.App.Tests;

/// <summary>
/// 監看硬體解碼 opt-in 契約（§3.3 決策 D4）。
///
/// <para>
/// 參數組裝的正確性由 <c>HeliVMS.Media.Tests</c> 的純函式測試釘住；這份契約只確認桌面端
/// 真的有把設定接起來：設定 UI → <c>decode.hwaccel</c> → <c>ChannelSession</c> 讀取 →
/// <c>RtspClient</c> 使用。任一段沒接，功能會「邏輯正確但永遠不會啟用」，沒有既有測試會變紅。
/// </para>
/// </summary>
public sealed class HwAccelContractTests
{
    private const string SettingsXaml = "src/HeliVMS.App/SettingsWindow.xaml";
    private const string SettingsCode = "src/HeliVMS.App/SettingsWindow.xaml.cs";
    private const string SessionCode = "src/HeliVMS.App/Services/ChannelSession.cs";
    private const string ClientCode = "src/HeliVMS.Media/RtspClient.cs";

    [Fact]
    public void 設定視窗有硬體解碼選項且含停用與自動()
    {
        var xaml = SourceContract.Read(SettingsXaml);

        Assert.Contains("x:Name=\"HwAccelCombo\"", xaml);
        Assert.Equal("OnHwAccelChanged", WiredSelectionChanged(SettingsXaml, "HwAccelCombo"));

        foreach (var value in new[] { "off", "auto", "d3d11va", "cuda", "dxva2" })
        {
            Assert.Contains($"Tag=\"{value}\"", xaml);
        }
    }

    [Fact]
    public void 切換硬體解碼會寫回設定鍵()
    {
        var code = SourceContract.Read(SettingsCode);

        Assert.Contains("\"decode.hwaccel\"", code);

        var handler = SourceContract.BodyOf(code, "OnHwAccelChanged");
        Assert.Contains("HwAccelCombo.SelectedItem", handler);
        Assert.Contains("_settings.Set", handler);

        var reload = SourceContract.BodyOf(code, "ReloadHwAccel");
        Assert.Contains("_settings.Get", reload);
        Assert.Contains("HwAccelCombo", reload);
    }

    [Fact]
    public void 頻道連線建立時讀取硬體解碼設定()
    {
        var code = SourceContract.Read(SessionCode);

        // 兩個建立點（建構子與切換碼流）都必須帶 hwAccel，只驗存在會漏掉其中一處。
        var constructed = Regex.Matches(code, @"new RtspClient\(").Count;
        var wired = Regex.Matches(code, @"new RtspClient\([^;]*hwAccel:\s*ReadHwAccel\(\)", RegexOptions.Singleline).Count;
        Assert.Equal(2, constructed);
        Assert.Equal(constructed, wired);

        Assert.Contains("\"decode.hwaccel\"", code);

        var read = SourceContract.BodyOf(code, "ReadHwAccel");
        Assert.Contains("SettingsRepository", read);
        Assert.Contains(".Get(", read);
    }

    [Fact]
    public void RtspClient以純函式組裝解碼參數()
    {
        // StreamFramesAsync 必須經由 BuildDecodeArguments 組參數，否則測試釘住的
        // 參數（硬體加速、bgr24、-vsync 0）可能與實際下給 ffmpeg 的不同。
        var code = SourceContract.Read(ClientCode);
        var stream = SourceContract.BodyOf(code, "StreamFramesAsync");

        Assert.Contains("BuildDecodeArguments", stream);
        Assert.DoesNotContain("psi.ArgumentList.Add(\"-hwaccel\")", stream);
    }

    private static string WiredSelectionChanged(string relativePath, string element)
    {
        var xaml = SourceContract.Read(relativePath);
        var tag = Regex.Match(
            xaml,
            $@"<[A-Za-z][^<>]*?x:Name=""{Regex.Escape(element)}""[^<>]*?>",
            RegexOptions.Singleline).Value;

        Assert.NotEqual(string.Empty, tag);
        var handler = Regex.Match(tag, @"\bSelectionChanged=""(\w+)""");
        return handler.Success ? handler.Groups[1].Value : string.Empty;
    }
}
