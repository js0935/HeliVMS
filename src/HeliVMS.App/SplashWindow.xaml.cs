using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Animation;

namespace HeliVMS.App;

/// <summary>
/// 品牌啟動畫面（BRAND.md 第 4 節：≤3 秒、淡出 200ms 無殘影）。
/// </summary>
public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SplashImage.Source = MainWindow.CreateBitmap("splash.png");
    }

    /// <summary>
    /// 淡出後關閉視窗（200ms，避免殘影）。
    /// </summary>
    public async Task FadeOutAsync()
    {
        var tcs = new TaskCompletionSource();
        var anim = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(200));
        anim.Completed += (_, _) => tcs.SetResult();
        BeginAnimation(OpacityProperty, anim);
        await tcs.Task;
        Close();
    }
}