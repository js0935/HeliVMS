using HeliVMS.Recording;
using HeliVMS.Shared;
using HeliVMS.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace HeliVMS.Agent;

public class Program
{
    public static void Main(string[] args)
    {
        Host.CreateDefaultBuilder(args)
            .UseWindowsService()
            .ConfigureServices((ctx, services) =>
            {
                var db = DataRootLayout.ResolveDatabasePath(v => Environment.GetEnvironmentVariable(v));
                var dataRoot = DataRootLayout.ResolveRoot(v => Environment.GetEnvironmentVariable(v));
                // 錄影根目錄與桌面、WebApi 共用同一條規則（見 RecordingStorage）：以設定中心寫進共用
                // 資料庫的 recording.root 為準，未設定時才退回環境變數與「避開系統碟」的推導值。
                // 服務於啟動時讀一次；桌面切換磁碟後，服務下次啟動就會跟著切換。
                services.AddSingleton(sp => new SqliteStore(db));
                services.AddSingleton(sp =>
                {
                    var store = sp.GetRequiredService<SqliteStore>();
                    string? configured = null;
                    try
                    {
                        configured = new SettingsRepository(store).Get(RecordingStorage.RootSettingKey);
                    }
                    catch (Exception)
                    {
                        // 資料庫尚未由桌面／WebApi 初始化時退回推導值，不讓服務因讀不到設定而啟動失敗。
                    }

                    var recordingsRoot = RecordingStorage.ResolveConfiguredRoot(dataRoot, configured);
                    return new RecordingScheduler(store, recordingsRoot);
                });
                services.AddSingleton<IRecordingHost>(sp => new RecordingHostBridge(sp.GetRequiredService<RecordingScheduler>()));
                services.AddHostedService<RecordingWorker>();
            })
            .Build()
            .Run();
    }
}
