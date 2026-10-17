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
                // 錄影單一擁有者仲裁（M321，§11.1 進程分離）：服務為優先角色，會搶下桌面持有的租約。
                // 服務關閉後不需主動 Release：同級搶佔已讓下次啟動立即取回，不必等租約逾時。
                services.AddSingleton(sp => RecordingOwnership.CreateService(sp.GetRequiredService<SqliteStore>()));
                services.AddSingleton(sp =>
                {
                    var store = sp.GetRequiredService<SqliteStore>();
                    var ownership = sp.GetRequiredService<RecordingOwnershipCoordinator>();
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
                    // 磁碟滿策略第三階段（§11.1）：可用空間低於寫入門檻時停開新錄影；
                    // 每次調和即時重算，設定中心切換磁碟後下一個週期即依新根判斷。
                    return new RecordingScheduler(
                        store,
                        recordingsRoot,
                        diskFull: () => DiskPressureCoordinator.ShouldStopNewRecording(store, recordingsRoot),
                        // 錄影心跳寫進資料根目錄（M320）；看門狗 WatchdogWorker 讀同一份。
                        heartbeatRoot: dataRoot,
                        // 每輪調和續約；非持有者不錄影（M321）。同級搶佔讓服務重啟後立即取回。
                        ownsRecording: () => ownership.AcquireOrRenew(DateTime.UtcNow));
                });
                services.AddSingleton<IRecordingHost>(sp => new RecordingHostBridge(sp.GetRequiredService<RecordingScheduler>()));
                services.AddHostedService<RecordingWorker>();
                // M310：服務以 LocalSystem 執行時預設持有校時權限，代桌面校正系統時鐘。
                services.AddHostedService<NtpSyncWorker>();
                // M316：Headless Agent 過去只錄不刪；補上與桌面／WebApi 共用的保留清理迴圈。
                services.AddHostedService<RetentionWorker>();
                // M318：定期以 ffprobe 抽查已封檔區段，把損壞／遺失寫進稽核（§11.1 寫盤保護）。
                services.AddHostedService<IntegrityWorker>();
                // M320：監看錄影心跳，停擺時告警並（選配）重新拉起錄影程式（§11.1 看門狗）。
                services.AddHostedService<WatchdogWorker>();
            })
            .Build()
            .Run();
    }
}
