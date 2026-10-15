using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace HeliVMS.Api.Tests;

/// <summary>
/// 前端靜態檔的快取行為。
/// </summary>
/// <remarks>
/// <para>
/// SPA 是伺服器直接從原始碼目錄供應的檔案，而 <c>StaticFileMiddleware</c> 預設只送 ETag／
/// Last-Modified、不送 <c>Cache-Control</c>。瀏覽器這時改用啟發式新鮮度（約「檔案年齡的 10%」）
/// 自行判斷「這份還很新」，於是把已經修好的 JS 繼續用舊快取蓋掉。
/// </para>
/// <para>
/// 現場症狀是「明明修好了，瀏覽器還在跑舊版」，而且很難從畫面看出是快取：例如瀏覽器拿舊的
/// <c>live.js</c>（<c>sendonly</c>）去協商，就得到 <c>Incompatible receive direction</c>，
/// 但伺服器與磁碟上的原始碼其實都是對的。這支測試把「每次都要回伺服器驗證」釘住，
/// 避免有人日後把表頭拿掉而不自覺——這是行為測試，不是掃原始碼。
/// </para>
/// </remarks>
public sealed class SpaCacheControlTests : IDisposable
{
    /// <summary>測試用內容根：只需要有 <c>index.html</c> 與一個靜態檔，足以走完兩條供應路徑。</summary>
    private readonly string _webRoot = Path.Combine(Path.GetTempPath(), $"helivms-spa-{Guid.NewGuid():N}");

    public SpaCacheControlTests()
    {
        Directory.CreateDirectory(_webRoot);
        File.WriteAllText(Path.Combine(_webRoot, "index.html"), "<!doctype html><meta charset=\"utf-8\"><title>t</title>");
        File.WriteAllText(Path.Combine(_webRoot, "app.js"), "export const ready = true;");
    }

    [Fact]
    public async Task 前端檔案每次都要回伺服器驗證才不會被舊快取蓋掉()
    {
        using var factory = new SpaWebFactory(_webRoot);
        using var client = factory.CreateClient();

        // /app.js 與 /index.html 是實體檔案，走 StaticFileMiddleware；
        // /live/deep/link 這種不存在於磁碟的深層連結走 MapFallback 送 index.html。
        // 兩條供應路徑都必須帶 no-cache，否則任何一條都可能讓修好的前端被瀏覽器續用。
        foreach (var path in new[] { "/app.js", "/index.html", "/live/deep/link" })
        {
            var response = await client.GetAsync(path);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(
                response.Headers.CacheControl?.NoCache is true,
                $"{path} 應該帶 Cache-Control: no-cache，否則修好的前端會被瀏覽器舊快取蓋掉。");
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_webRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>把 SPA 根目錄指到測試自建的目錄，其餘設定比照一般 API factory 隔離。</summary>
    private sealed class SpaWebFactory : WebApplicationFactory<Program>
    {
        private readonly string _root;

        public SpaWebFactory(string root) => _root = root;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("HELIVMS_WEB", _root);
            builder.UseSetting("HELIVMS_DB", Path.Combine(_root, "spa-test.db"));
            builder.UseSetting("HELIVMS_LICENSE", Path.Combine(_root, "absent.lic"));
        }
    }
}
