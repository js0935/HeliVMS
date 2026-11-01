using System.Xml.Linq;

namespace HeliVMS.Devices.Onvif;

/// <summary>
/// ONVIF 事件（PullPoint 訂閱）——攝影機乾接點輸入（DI）的唯一來源。
///
/// <para>
/// 為什麼一定要走事件：ONVIF 沒有「讀取 DI 目前狀態」的操作（<c>GetDigitalInputs</c> 只回
/// 端子清單），狀態變化只以通知送出。少了這一塊，攝影機的 DI 永遠不會進系統——桌面只能
/// 靠人工在 IO 視窗模擬，規則與警報因此空轉。
/// </para>
/// <para>
/// 實作刻意只做 PullPoint 這一條最廣泛支援的路徑（相對 Basic Notification 需要設備主動
/// 推播到本機，現場多半不通）：建立訂閱 → 取回訂閱位址 → 反覆 PullMessages。逾時沒有訊息
/// 是正常情形（回空清單），認證與連線錯誤則照常往外丟，由呼叫端的重試節奏處理。
/// </para>
/// </summary>
public sealed partial class OnvifDeviceService
{
    private static readonly XNamespace Wsnt = "http://docs.oasis-open.org/wsn/b-2";

    /// <summary>WSN 的動作 URI 前綴（Action 標頭用；XNamespace 不能直接轉字串）。</summary>
    private const string WsntUri = "http://docs.oasis-open.org/wsn/b-2/";

    /// <summary>事件服務位址；尚未解析→null（見 <see cref="EnsureEventCapabilityAsync"/>）。</summary>
    public string? EventXAddr { get; private set; }

    /// <summary>是否已知事件服務位址。</summary>
    public bool HasEvents => !string.IsNullOrWhiteSpace(EventXAddr);

    /// <summary>
    /// 解析事件服務位址：GetCapabilities 的 Events 類別優先，找不到就退回設備服務
    ///（多數設備在同一個端點上接受 CreatePullPointSubscription）。
    /// </summary>
    public async Task EnsureEventCapabilityAsync(CancellationToken cancellationToken = default)
    {
        if (EventXAddr is not null)
        {
            return;
        }

        await _capabilityLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (EventXAddr is not null)
            {
                return;
            }

            var xAddr = await TryResolveCapabilityAsync("Events", ["Events", "All"], cancellationToken)
                .ConfigureAwait(false);
            EventXAddr = string.IsNullOrWhiteSpace(xAddr) ? DeviceXAddr : xAddr;
        }
        finally
        {
            _capabilityLock.Release();
        }
    }

    /// <summary>
    /// 建立 PullPoint 訂閱並回傳訂閱位址；設備不支援時擲 <see cref="OnvifException"/>（NotSupported）。
    /// </summary>
    /// <param name="termination">訂閱長度（到期前呼叫端會重新訂閱）。</param>
    /// <param name="cancellationToken">取消權杖。</param>
    public async Task<Uri> CreatePullPointSubscriptionAsync(
        TimeSpan? termination = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureEventCapabilityAsync(cancellationToken).ConfigureAwait(false);
        var life = termination ?? TimeSpan.FromHours(1);

        var body = await PostAsync(
            new XElement(Wsnt + "CreatePullPointSubscription",
                new XElement(Wsnt + "InitialTerminationTime", $"PT{(int)life.TotalSeconds}S")),
            EventXAddr,
            WsntUri + "CreatePullPointSubscription",
            cancellationToken).ConfigureAwait(false);

        var address = DescendantsAnyNs(body, "Address")
            .Select(e => e.Value.Trim())
            .FirstOrDefault(v => v.Length > 0);

        if (string.IsNullOrWhiteSpace(address))
        {
            throw OnvifException.NotSupported("設備未回報事件的訂閱位址（不支援 PullPoint 訂閱）。");
        }

        return new Uri(ResolveUrl(address));
    }

    /// <summary>
    /// 取得訂閱位址上待處理的通知；等待 <paramref name="timeout"/> 後沒有訊息時回空清單
    /// （PullMessages 的正常逾時行為，不是錯誤）。
    /// </summary>
    public async Task<IReadOnlyList<OnvifEventMessage>> PullMessagesAsync(
        Uri subscription,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subscription);

        XElement body;
        try
        {
            body = await PostAsync(
                new XElement(Wsnt + "PullMessages",
                    new XElement(Wsnt + "Timeout", $"PT{Math.Max(1, (int)timeout.TotalSeconds)}S"),
                    new XElement(Wsnt + "MessageLimit", 32)),
                subscription.AbsoluteUri,
                WsntUri + "PullMessages",
                cancellationToken).ConfigureAwait(false);
        }
        catch (OnvifException ex) when (ex.Code == OnvifErrorCode.NotSupported)
        {
            // 訂閱過期或設備以 Fault 表示「這一輪沒有訊息」：視為空，下一輪重新訂閱。
            return [];
        }

        var messages = new List<OnvifEventMessage>();
        foreach (var notification in DescendantsAnyNs(body, "NotificationMessage"))
        {
            var topic = notification.ElementAnyNs("Topic")?.Value.Trim() ?? string.Empty;
            var source = ReadSimpleItems(notification, "Source");
            var data = ReadSimpleItems(notification, "Data");
            messages.Add(new OnvifEventMessage(topic, source, data));
        }

        return messages;
    }

    /// <summary>
    /// 從通知中取出數位輸入的變化：權杖（<c>InputToken</c>）與作動狀態（<c>LogicalState</c>）。
    /// 非數位輸入的事件回 <c>false</c>（呼叫端據此略過，不猜測）。
    /// </summary>
    /// <remarks>
    /// 主題依 ONVIF 規範為 <c>tns1:Device/Trigger/DigitalInput</c>；部分廠牌用
    /// <c>tns1:Device/Trigger/Relay</c> 或自訂前綴，故以「主題含 DigitalInput」判斷，
    /// 狀態則一律讀 <c>LogicalState</c>（true＝作動）。
    /// </remarks>
    public static bool TryParseDigitalInput(OnvifEventMessage message, out string token, out bool active)
    {
        ArgumentNullException.ThrowIfNull(message);
        token = string.Empty;
        active = false;

        if (!message.Topic.Contains("DigitalInput", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!TryGetValue(message.Source, "InputToken", out token)
            && !TryGetValue(message.Data, "InputToken", out token))
        {
            return false;
        }

        if (!TryGetValue(message.Data, "LogicalState", out var state)
            && !TryGetValue(message.Source, "LogicalState", out state))
        {
            return false;
        }

        active = state.Trim().ToLowerInvariant() switch
        {
            "true" or "1" or "active" or "on" => true,
            _ => false,
        };
        return true;
    }

    private static bool TryGetValue(IReadOnlyDictionary<string, string> items, string name, out string value)
    {
        foreach (var (key, item) in items)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                value = item;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private static IReadOnlyDictionary<string, string> ReadSimpleItems(XElement notification, string itemElement)
    {
        var items = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var message = DescendantsAnyNs(notification, "Message").FirstOrDefault();
        var container = message is null ? null : DescendantsAnyNs(message, itemElement).FirstOrDefault();
        if (container is null)
        {
            return items;
        }

        foreach (var item in container.Elements())
        {
            var name = (string?)item.Attribute("Name");
            var value = (string?)item.Attribute("Value");
            if (!string.IsNullOrWhiteSpace(name))
            {
                items[name.Trim()] = (value ?? string.Empty).Trim();
            }
        }

        return items;
    }
}
