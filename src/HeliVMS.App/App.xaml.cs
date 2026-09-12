using System.Threading.Tasks;
using System.Windows;

namespace HeliVMS.App;

/// <summary>
/// 應用程式進入點：顯示品牌啟動畫面 → 開啟主視窗。
/// </summary>
public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var splash = new SplashWindow();
        splash.Show();

        await Task.Delay(1200);

        var main = new MainWindow();
        main.Show();
        await splash.FadeOutAsync();
    }
}