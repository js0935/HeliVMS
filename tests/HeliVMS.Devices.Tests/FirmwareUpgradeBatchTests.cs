using System.Net;
using HeliVMS.Devices.Onvif;

namespace HeliVMS.Devices.Tests;

/// <summary>
/// 批次韌體升級編排（§7.4「批次檢查版本 → 推送映像 → 升級／重啟驗證」）。
///
/// <para>
/// 批次升級的風險不在單一命令，而在「一次對多台設備下不可逆命令」。這裡釘住三件事：
/// 一台失敗不會讓其餘設備停在半路、每台命令都送到它自己的端點、以及取消後不再動下一台。
/// 升級前的舊版讀取順序是稽核「從哪一版升上去」的唯一依據，也一併釘住。
/// </para>
/// </summary>
public class FirmwareUpgradeBatchTests
{
    private const string EmptyBody = """
        <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"><s:Body /></s:Envelope>
        """;

    private const string RebootResponse = """
        <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope">
          <s:Body>
            <td:SystemRebootResponse xmlns:td="http://www.onvif.org/ver10/device/wsdl">
              <td:Message>Rebooting now</td:Message>
            </td:SystemRebootResponse>
          </s:Body>
        </s:Envelope>
        """;

    [Fact]
    public async Task 批次檢查會逐台回報各自的韌體版本()
    {
        var handlers = new Dictionary<int, HttpMessageHandler>
        {
            [1] = new RecordingHandler(DeviceSoap),
            [2] = new RecordingHandler(DeviceSoap),
        };

        var results = await Batch(handlers).CheckAsync([Target(1), Target(2)]);

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.True(r.Success));
        Assert.All(results, r => Assert.Equal("V5.5.800", r.Version));
    }

    [Fact]
    public async Task 檢查單台失敗仍會繼續其餘設備()
    {
        var handlers = new Dictionary<int, HttpMessageHandler>
        {
            [1] = new ScriptedHandler((_, _) => OnvifTestHttp.Status(HttpStatusCode.ServiceUnavailable)),
            [2] = new RecordingHandler(DeviceSoap),
        };

        var results = await Batch(handlers).CheckAsync([Target(1), Target(2)]);

        Assert.Equal(2, results.Count);
        Assert.False(results[0].Success);
        Assert.NotNull(results[0].Error);
        Assert.True(results[1].Success);
    }

    [Fact]
    public async Task 批次升級會先讀舊版再送升級且命令送到各設備自己的端點()
    {
        var handler = new RecordingHandler(DeviceSoap);
        var handlers = new Dictionary<int, HttpMessageHandler> { [7] = handler };

        var results = await Batch(handlers).UpgradeAsync([Target(7)], new byte[] { 1, 2, 3 }, rebootAfter: true);

        var result = Assert.Single(results);
        Assert.True(result.Success);
        Assert.Equal("V5.5.800", result.PreviousVersion);
        Assert.True(result.Rebooted);

        // 一定要先讀到舊版，稽核才知道「從哪一版升上去」。
        var infoIndex = handler.Requests.FindIndex(r => r.Body.Contains("GetDeviceInformation", StringComparison.Ordinal));
        var upgradeIndex = handler.Requests.FindIndex(r => r.Body.Contains("StartFirmwareUpgrade", StringComparison.Ordinal));
        var rebootIndex = handler.Requests.FindIndex(r => r.Body.Contains("SystemReboot", StringComparison.Ordinal));
        Assert.True(infoIndex >= 0 && upgradeIndex > infoIndex && rebootIndex > upgradeIndex,
            $"命令順序應為 讀版本({infoIndex}) → 升級({upgradeIndex}) → 重啟({rebootIndex})");

        Assert.All(handler.Requests, r => Assert.Equal("http://10.0.0.7/onvif/device_service", r.Url));
        // 韌體內容 {1,2,3} 以 base64 內嵌於 Upload（Convert.ToBase64String == "AQID"）。
        Assert.Contains("AQID", handler.Requests[upgradeIndex].Body);
    }

    [Fact]
    public async Task 不重啟時不會送SystemReboot()
    {
        var handler = new RecordingHandler(DeviceSoap);
        var handlers = new Dictionary<int, HttpMessageHandler> { [1] = handler };

        var result = Assert.Single(
            await Batch(handlers).UpgradeAsync([Target(1)], new byte[] { 1, 2, 3 }, rebootAfter: false));

        Assert.True(result.Success);
        Assert.False(result.Rebooted);
        Assert.DoesNotContain(handler.Requests, r => r.Body.Contains("SystemReboot", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 升級失敗的設備不會被標記為已重啟且不繼續下重啟()
    {
        var handler = new ScriptedHandler((_, _) => OnvifTestHttp.Status(HttpStatusCode.ServiceUnavailable));
        var handlers = new Dictionary<int, HttpMessageHandler> { [1] = handler };

        var result = Assert.Single(
            await Batch(handlers).UpgradeAsync([Target(1)], new byte[] { 1, 2, 3 }, rebootAfter: true));

        Assert.False(result.Success);
        Assert.False(result.Rebooted);
        Assert.NotNull(result.Error);
        // 讀舊版就失敗（503；唯讀路徑允許重試，故可能不只一次呼叫），此時不可送出任何破壞性命令。
        Assert.DoesNotContain(handler.Requests, r => r.Body.Contains("StartFirmwareUpgrade", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Requests, r => r.Body.Contains("SystemReboot", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 批次升級單台失敗不會中止其餘設備()
    {
        var handlers = new Dictionary<int, HttpMessageHandler>
        {
            [1] = new ScriptedHandler((_, _) => OnvifTestHttp.Status(HttpStatusCode.ServiceUnavailable)),
            [2] = new RecordingHandler(DeviceSoap),
        };

        var results = await Batch(handlers).UpgradeAsync([Target(1), Target(2)], new byte[] { 1, 2, 3 }, rebootAfter: false);

        Assert.Equal(2, results.Count);
        Assert.False(results[0].Success);
        Assert.True(results[1].Success);
        // 第二台確實被下過升級命令（不是被第一台的失敗吃掉）。
        Assert.Contains(
            ((RecordingHandler)handlers[2]).Requests,
            r => r.Body.Contains("StartFirmwareUpgrade", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 空韌體會在任何命令送出前被拒絕()
    {
        var handler = new RecordingHandler(DeviceSoap);
        var handlers = new Dictionary<int, HttpMessageHandler> { [1] = handler };

        await Assert.ThrowsAsync<ArgumentException>(
            () => Batch(handlers).UpgradeAsync([Target(1)], Array.Empty<byte>(), rebootAfter: false));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task 過大韌體會在任何命令送出前被拒絕()
    {
        var handler = new RecordingHandler(DeviceSoap);
        var handlers = new Dictionary<int, HttpMessageHandler> { [1] = handler };
        var tooBig = new byte[OnvifDeviceService.MaxFirmwareBytes + 1];

        await Assert.ThrowsAsync<ArgumentException>(
            () => Batch(handlers).UpgradeAsync([Target(1)], tooBig, rebootAfter: false));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task 取消後不會再動下一台()
    {
        var handler = new RecordingHandler(DeviceSoap);
        var handlers = new Dictionary<int, HttpMessageHandler> { [1] = handler };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Batch(handlers).UpgradeAsync([Target(1)], new byte[] { 1 }, false, null, cts.Token));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task 進度回報涵蓋每一台()
    {
        var handlers = new Dictionary<int, HttpMessageHandler>
        {
            [1] = new RecordingHandler(DeviceSoap),
            [2] = new ScriptedHandler((_, _) => OnvifTestHttp.Status(HttpStatusCode.ServiceUnavailable)),
        };
        var progress = new ListProgress<FirmwareUpgradeResult>();

        var results = await Batch(handlers).UpgradeAsync(
            [Target(1), Target(2)], new byte[] { 1 }, rebootAfter: false, progress);

        Assert.Equal(results.Count, progress.Items.Count);
        Assert.Equal(new[] { 1, 2 }, progress.Items.Select(r => r.Target.DeviceId).ToArray());
    }

    private static FirmwareUpgradeBatch Batch(IDictionary<int, HttpMessageHandler> handlers) =>
        new(target => new OnvifDeviceService(
            target.DeviceXAddr, target.Username, target.Password, handlers[target.DeviceId]));

    private static FirmwareUpgradeTarget Target(int id) =>
        new(id, $"cam{id}", $"http://10.0.0.{id}/onvif/device_service", null, null);

    private static string DeviceSoap(string _, string body)
    {
        if (body.Contains("GetDeviceInformation", StringComparison.Ordinal))
        {
            return OnvifTestHttp.DeviceInfoXml;
        }

        if (body.Contains("GetSystemDateAndTime", StringComparison.Ordinal))
        {
            return OnvifTestHttp.SystemDateAndTime(DateTimeOffset.UtcNow);
        }

        if (body.Contains("SystemReboot", StringComparison.Ordinal))
        {
            return RebootResponse;
        }

        return EmptyBody;
    }

    /// <summary>同步收集進度，避免 <see cref="Progress{T}"/> 把回呼丟到執行緒池造成競態。</summary>
    private sealed class ListProgress<T> : IProgress<T>
    {
        public List<T> Items { get; } = [];

        public void Report(T value) => Items.Add(value);
    }
}
