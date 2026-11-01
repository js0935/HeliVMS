namespace HeliVMS.App.Tests;

/// <summary>
/// Modbus 接點與攝影機乾接點共用同一套連動規則引擎的接線契約。
/// </summary>
/// <remarks>
/// 之前的缺口：io_rules 只吃到攝影機乾接點（CameraDiMonitor 餵入），Modbus 通道走自己的
/// 語意——同一個現場兩種來源、兩套行為，操作員在警報中心設定的連動對 Modbus 完全沒效。
/// 這條契約釘住三件事：Modbus 通道在監視啟動時同步登記為接點（io_ports.io_channel_id）、
/// DI 穩定變化餵入同一個引擎、DO 輸出依接點來源路由（Modbus 線圈 / 攝影機繼電器）。
/// </remarks>
public sealed class ModbusIoRuleContractTests
{
    private const string MonitorHost = "src/HeliVMS.App/Services/IoMonitorHost.cs";
    private const string Monitor = "src/HeliVMS.Alarms/IoDeviceMonitor.cs";
    private const string Router = "src/HeliVMS.Alarms/RoutingIoOutputWriter.cs";
    private const string Ports = "src/HeliVMS.Storage/IoPortRepository.cs";
    private const string Main = "src/HeliVMS.App/MainWindow.xaml.cs";

    [Fact]
    public void Modbus通道在監視啟動時同步登記為接點()
    {
        var source = SourceContract.Read(MonitorHost);

        // 同步放在 RefreshAndStart（啟動與每次 IO 設定變更都會跑），設定頁不必再接一次。
        Assert.Contains("SyncPortsForChannels()", SourceContract.BodyOf(source, "RefreshAndStart"));
        var sync = SourceContract.BodyOf(source, "SyncPortsForChannels");
        Assert.Contains("EnsureForIoChannel(", sync);

        // 通道被刪除時接點與其規則一起清掉，規則才不會指向不存在的硬體。
        Assert.Contains("DeleteByIoChannel(", sync);
    }

    [Fact]
    public void Modbus的DI變化會餵入連動規則引擎()
    {
        var source = SourceContract.Read(Monitor);

        Assert.Contains("IoRuleHost? _ruleHost", source);
        Assert.Contains("FeedRuleEngine(", SourceContract.BodyOf(source, "ApplyDebounce"));
        Assert.Contains("await _ruleHost.FeedAsync(id, active)", SourceContract.BodyOf(source, "FeedRuleEngine"));
    }

    [Fact]
    public void DO輸出依接點來源路由()
    {
        // 路由器：io_channel_id 有值 → Modbus 線圈；沒有 → 攝影機繼電器。
        var router = SourceContract.BodyOf(SourceContract.Read(Router), "WriteAsync");
        Assert.Contains("IoChannelId is not null", router);
        Assert.Contains("_modbusWriter.WriteAsync", router);
        Assert.Contains("_cameraWriter.WriteAsync", router);

        // 兩個宿主都用同一個路由器（互相補上對方的寫入器）。
        Assert.Contains("new RoutingIoOutputWriter(", SourceContract.Read(MonitorHost));
        Assert.Contains("new RoutingIoOutputWriter(", SourceContract.Read("src/HeliVMS.App/Services/CameraDiHost.cs"));
        Assert.Contains("SetCameraWriter(", SourceContract.Read(MonitorHost));
        Assert.Contains("SetCameraWriter(", SourceContract.Read(Main));
    }

    [Fact]
    public void 攝影機與Modbus的接點在規則編輯器都能選()
    {
        var source = SourceContract.Read("src/HeliVMS.App/RulesWindow.xaml.cs");

        // 規則編輯器的接點來源是 io_ports（Modbus 同步後會出現在清單），不是各自硬體的清單。
        Assert.Contains("_ioPorts.List()", SourceContract.BodyOf(source, "ReloadIoRuleChoices"));
        Assert.Contains("p.Kind == IoPortKind.Di", SourceContract.BodyOf(source, "ReloadIoRuleChoices"));
        Assert.Contains("p.Kind == IoPortKind.Do", SourceContract.BodyOf(source, "ReloadIoRuleChoices"));
    }
}
