using System.Windows;
using HeliVMS.Licensing;

namespace HeliVMS.App;

/// <summary>
/// 主視窗：M1 殼，頂欄品牌＋工作區佔位＋底欄授權狀態。
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var state = new LicenseManager().ValidateDefault();
        StatusText.Text = state.IsValid
            ? $"禾秝軟體開發團隊 · 已授權（{state.Payload!.Cameras} 路）"
            : $"未授權：{state.Message ?? state.Status.ToString()}";
    }
}