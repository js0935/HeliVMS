using HeliVMS.WebApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace HeliVMS.Api.Tests;

/// <summary>
/// 主機組成的行為測試（M305）。
/// </summary>
/// <remarks>
/// <para>
/// 桌面程式要在行程內啟停 Web 服務，而桌面本身已經有一條保留清理迴圈
/// （<c>MainWindow.RunRetentionLoopAsync</c>）。內嵌主機若照獨立主機一樣掛上
/// <see cref="RetentionService"/>，同一份資料庫與同一個 recordings 目錄就會被兩個清理器
/// 同時掃、同時刪檔——這種問題在單元測試之外只會在正式環境咬人，而且症狀只是「偶爾少幾段
/// 錄影」，log 看起來一切正常。因此這裡直接驗證旗標真的改變了背景服務的組成。
/// </para>
/// <para>
/// 這是行為測試、不是掃原始碼：即使有人把 <c>if</c> 拿掉、旗標變成裝飾品，這支測試仍會紅。
/// </para>
/// </remarks>
public sealed class HostCompositionTests
{
    [Fact]
    public void 內嵌主機不掛背景清理而獨立主機才掛()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"helivms-m305-{Guid.NewGuid():N}.db");
        try
        {
            Assert.DoesNotContain(
                nameof(RetentionService),
                HostedServiceNames(new WebApiHostOptions { IncludeHostedServices = false }, dbPath));
            Assert.DoesNotContain(
                nameof(LiveStreamMaintenance),
                HostedServiceNames(new WebApiHostOptions { IncludeHostedServices = false }, dbPath));

            // 索引對帳服務同理：內嵌主機（桌面）已有自己的維護迴圈會對帳，掛上去只會讓兩個
            // 對帳器同時掃同一份索引；但獨立 Web 部署沒有桌面，索引不會自己修，所以必須掛。
            Assert.DoesNotContain(
                nameof(SegmentReconcileService),
                HostedServiceNames(new WebApiHostOptions { IncludeHostedServices = false }, dbPath));

            var standalone = HostedServiceNames(new WebApiHostOptions { IncludeHostedServices = true }, dbPath);
            Assert.Contains(nameof(RetentionService), standalone);
            Assert.Contains(nameof(LiveStreamMaintenance), standalone);
            Assert.Contains(nameof(SegmentReconcileService), standalone);
        }
        finally
        {
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                try
                {
                    File.Delete(dbPath + suffix);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    private static HashSet<string> HostedServiceNames(WebApiHostOptions options, string dbPath)
    {
        // 只建立（build）主機、不啟動：背景服務會被建構出來，但不會真的開始跑迴圈或綁連接埠。
        using var app = WebApiHost.Create([$"--HELIVMS_DB={dbPath}"], options);
        return app.Services.GetServices<IHostedService>()
            .Select(service => service.GetType().Name)
            .ToHashSet(StringComparer.Ordinal);
    }
}
