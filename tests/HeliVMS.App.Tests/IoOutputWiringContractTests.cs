namespace HeliVMS.App.Tests;

/// <summary>
/// 乾接點輸出（DO）在桌面端的接線契約。
/// </summary>
/// <remarks>
/// 實際缺口：ONVIF 的 <c>SetRelayOutputState</c> 已完成，卻只有 Web API（<c>/api/io</c>）
/// 在用；桌面 <c>IoWindow</c> 建立的 <c>IoRuleHost</c> 沒有注入輸出寫入器，於是
/// <c>NullIoOutputWriter</c> 接手——DI 觸發後 alarm_events 有寫、引擎狀態有變，
/// 現場的閘門與警燈完全不動，而畫面上一切正常，log 也乾淨。
/// 這條契約釘住桌面確實把 ONVIF 寫入器接上去，且裝置連線走同一條憑證解析。
/// </remarks>
public sealed class IoOutputWiringContractTests
{
    private const string Code = "src/HeliVMS.App/IoWindow.xaml.cs";

    [Fact]
    public void 桌面IO視窗以ONVIF寫入器驅動繼電器()
    {
        var source = SourceContract.Read(Code);

        // 建立真正的寫入器並注入規則宿主（少了注入就是回到空寫入器）。
        Assert.Contains("new OnvifIoOutputWriter(", source);
        Assert.Contains("_host = new IoRuleHost(_ports, _rules, new AlarmEventRepository(store), _outputWriter)", source);

        // 繼電器列舉與切換都要走 ONVIF 服務。
        Assert.Contains("GetRelayOutputsAsync(", source);
        Assert.Contains("SetRelayOutputStateAsync(", source);

        // 憑證解析與 Web API gateway 同一條路徑：SafeUnprotect（舊資料可能是明文）。
        // 位址與憑證只剩 SettingsIoCameraContractTests 釘住的共用建立點一份。
        Assert.Contains("OnvifServiceFactory.Create(", source);

        // 失敗要讓操作員看得到（DO 失敗不再靜默）。
        Assert.Contains("OnvifIoOutputWriter", source);
        Assert.Contains("Io.OutputFailed", source);
        Assert.Contains("_outputWriter.Failed +=", source);
    }
}
