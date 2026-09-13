using System.IO;
using System.Threading.Tasks;
using System.Windows;

namespace HeliVMS.App;

/// <summary>
/// 應用程式進入點：顯示啟動畫面 → 1200ms → 開啟主視窗並淡出啟動畫面。
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
}