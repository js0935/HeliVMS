using System.IO;
using System.Threading.Tasks;
using System.Windows;
using HeliVMS.Storage;

namespace HeliVMS.App;

/// <summary>
/// 應用程式進入點：顯示啟動畫面 → 1200ms → 需要時登入（M42）→ 開啟主視窗並淡出啟動畫面。
/// 任何啟動例外寫入 %TEMP%\helivms-startup.log 後重新擲出。
/// </summary>
public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            var splash = new SplashWindow();
            splash.Show();

            await Task.Delay(1200);

            if (!PerformLogin())
            {
                Shutdown(1);
                return;
            }

            var main = new MainWindow();
            main.Show();
            await splash.FadeOutAsync();
        }
        catch (Exception ex)
        {
            File.WriteAllText(
                Path.Combine(Path.GetTempPath(), "helivms-startup.log"),
                $"{DateTime.UtcNow:O}\n{ex}\n");
            throw;
        }
    }

    /// <summary>
    /// 依 <c>auth.enabled</c> 決定是否要求登入（M42）。未啟用時視為 admin、直接放行。
    /// </summary>
    private static bool PerformLogin()
    {
        var dbPath = Path.Combine(HeliVMS.App.MainWindow.ResolveDataRoot(), "index.db");
        using var store = new SqliteStore(dbPath);
        store.Initialize();

        var auth = new AuthService(store);
        if (!auth.IsAuthEnabled)
        {
            return true;
        }

        return new LoginWindow(auth).ShowDialog() == true;
    }
}