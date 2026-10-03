using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using HeliVMS.Rtc;

namespace HeliVMS.Api.Tests;

/// <summary>
/// WHEP 狀態端點必須<b>據實</b>回報這台主機的 ICE 佈署狀態。
///
/// <para>
/// M244 §7 要求「沒有 TURN 且沒有 <c>PUBLIC_HOST</c> 時，對稱 NAT 會連不上，
/// 此限制須在 UI 明說」。要讓操作員看到這句話，前端就得問得到——但 TURN 與
/// <c>PUBLIC_HOST</c> 都是環境變數，畫面上看不到。前端若只能猜，結果是兩種壞法：
/// 有 TURN 的主機被說成「沒有 TURN」（操作員去設定一個已經有的東西），
/// 或沒 TURN 的主機什麼都不說（回到「遠端偶爾連不上」的原點）。
/// </para>
/// <para>
/// 這組測試盯的是「值真的跟著設定走」。若有人把這些欄位寫成常數，
/// 這個檔案會紅，而症狀在現場是無聲的誤導。
/// </para>
/// </summary>
public sealed class WhepDeploymentStatusTests
{
    private static async Task<JsonElement> StatusAsync(WhepApiFactory factory)
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", WhepApiFactory.Key);

        var channel = factory.SeedChannel(WhepApiFactory.PlaceholderRtsp);
        var response = await client.GetAsync($"/api/stream/{channel}/whep");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static bool Bool(JsonElement status, string name)
    {
        // 用 GetProperty 而非 TryGetProperty：欄位不見就是契約被改動，要紅不要靜默。
        Assert.True(status.TryGetProperty(name, out var value),
            $"狀態回應少了「{name}」；前端靠它判斷要不要提示 NAT 限制。");
        return value.GetBoolean();
    }

    [Fact]
    public async Task 沒設定TURN時必須據實回報沒有()
    {
        using var factory = new WhepApiFactory();

        var status = await StatusAsync(factory);

        Assert.False(Bool(status, "turnConfigured"));
        Assert.False(Bool(status, "publicHostConfigured"));
    }

    [Fact]
    public async Task 設定了TURN時必須據實回報有()
    {
        using var factory = new WhepApiFactory
        {
            TurnSetting = "turn:turn.example.net:3478;user;pass",
        };

        var status = await StatusAsync(factory);

        // 反向的錯誤同樣嚴重：對已經配好 TURN 的主機說「沒 TURN」，
        // 會讓操作員以為設定沒生效而反覆重設。
        Assert.True(Bool(status, "turnConfigured"));
    }

    [Fact]
    public async Task 設定了PublicHost時必須據實回報有()
    {
        using var factory = new WhepApiFactory
        {
            // IP 值：DNS 名稱會被 ParseHost 拒掉並記錄，那是刻意行為。
            PublicHostSetting = "198.51.100.7",
        };

        var status = await StatusAsync(factory);

        Assert.True(Bool(status, "publicHostConfigured"));
    }
}