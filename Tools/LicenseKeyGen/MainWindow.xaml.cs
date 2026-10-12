using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HeliVMS.Licensing;

namespace LicenseKeyGen;

public partial class MainWindow : Window
{
    /// <summary>
    /// 等級名稱，順序即 <c>HeliVMS.Licensing.LicenseTiers.All</c> 的索引，
    /// 也就是金鑰 <c>[1]</c> 的高 4 個 bit。產品端據此推出功能旗標。
    /// </summary>
    private static readonly string[] LicenseTypeNames =
        ["基本版", "標準版", "專業版", "進階版", "企業版", "客製版"];

    public MainWindow()
    {
        InitializeComponent();
        RefreshDeviceCode();
    }

    private void RefreshDeviceCode()
    {
        DeviceCodeBox.Text = ComputeDeviceCode();
    }

    private void RefreshDeviceCode_Click(object sender, RoutedEventArgs e)
    {
        RefreshDeviceCode();
    }

    private void CopyDeviceCode_Click(object sender, RoutedEventArgs e)
    {
        CopyText(DeviceCodeBox.Text, "設備碼已複製");
    }

    private void GenerateKey_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            GenerateBtn.IsEnabled = false;
            GenerateBtn.Content = "產生中...";
            LicenseKeyBox.Text = "";
            StatusText.Text = "";

            var deviceCode = DeviceCodeBox.Text.Trim();
            if (deviceCode.Length < 8 || deviceCode[..8].Any(c => !Uri.IsHexDigit(c)))
            {
                MessageBox.Show("設備碼必須是至少 8 碼十六進位字串", "錯誤",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

var maxCameras = int.TryParse(MaxCamerasBox.Text.Trim(), out var m) ? m : 64;

            var typeItem = LicenseTypeBox.SelectedItem as ComboBoxItem;
            var typeText = typeItem?.Content?.ToString() ?? "進階版 (64)";
            var typeId = LicenseTypeNames.ToList().IndexOf(
                typeText.Contains('(') ? typeText[..typeText.IndexOf('(')].Trim() : typeText);
            if (typeId < 0) typeId = 3;

            var licenseeText = LicenseeBox.Text.Trim();
            if (string.IsNullOrEmpty(licenseeText)) licenseeText = "禾秝公司";
            var licenseeId = licenseeText == "禾秝公司" ? 1 : 0;

            // 位元組配置與 HMAC 全部交給 HeliVMS.Licensing 的共用實作，這裡不再自帶一份
            // 密鑰。舊版兩邊各 hardcode 一次密鑰、只靠註解提醒同步，實際上已經漂移過：
            // 產品端根本沒有 HMAC 驗證路徑，於是每一張這裡產出的金鑰在產品端都顯示
            // 「簽章驗證失敗」。共用程式碼之後這類問題不可能再發生。
            if (!HmacLicenseKey.TryCreate(
                    maxCameras,
                    typeId,
                    licenseeId,
                    ExpiryPicker.SelectedDate is { } picked
                        ? DateOnly.FromDateTime(picked)
                        : null,
                    deviceCode,
                    out var licenseKey,
                    out var createError))
            {
                MessageBox.Show(createError, "錯誤",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var expiryLabel = ExpiryPicker.SelectedDate is { } expiryDate
                ? $"{expiryDate:yyyy-MM-dd}"
                : "永久";
            LicenseKeyBox.Text = licenseKey;
            StatusText.Text = $"✓ 已產生（{LicenseTypeNames[typeId]} / {maxCameras} 台 / {expiryLabel}）";
            StatusText.Foreground = TryFindResource("SuccessBrush") as System.Windows.Media.Brush;
            Log($"授權碼已產生：{licenseKey}");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"產生失敗：{ex.Message}", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = $"✗ 失敗：{ex.Message}";
            StatusText.Foreground = TryFindResource("ErrorBrush") as System.Windows.Media.Brush;
        }
        finally
        {
            GenerateBtn.IsEnabled = true;
            GenerateBtn.Content = "產生授權碼";
        }
    }

    private void CopyLicenseKey_Click(object sender, RoutedEventArgs e)
    {
        CopyText(LicenseKeyBox.Text, "授權碼已複製");
    }

    private void CopyText(string text, string msg)
    {
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            Clipboard.SetText(text);
            StatusText.Text = $"✓ {msg}";
            StatusText.Foreground = TryFindResource("SuccessBrush") as System.Windows.Media.Brush;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"✗ 複製失敗：{ex.Message}";
            StatusText.Foreground = TryFindResource("ErrorBrush") as System.Windows.Media.Brush;
        }
    }

    /// <summary>
    /// 預填本機機器碼，直接沿用產品端的 <see cref="MachineIdProvider"/>。
    ///
    /// 舊版這裡自己重寫了一份 WMI 計算，已經與產品端漂移：產品端會濾掉
    /// <c>SerialNumber == "None"</c> 並在 WMI 少回值時補 <c>Environment.MachineName</c>，
    /// 這裡兩者都沒有，於是同一台機器兩邊算出不同的碼，簽出來的權限立刻失效。
    /// 預填值仍然只是方便；替客戶簽發時要把客戶回報的機器碼貼進來覆蓋它。
    /// </summary>
    private static string ComputeDeviceCode() => MachineIdProvider.GetDeviceCode();

    private void ChangePassword_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ChangePasswordDialog();
        dialog.ShowDialog();
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private static void Log(string msg) => Debug.WriteLine($"[LicenseKeyGen] {msg}");
}
