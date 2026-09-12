using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LicenseKeyGen;

public partial class MainWindow : Window
{
    // HMAC secret MUST match HeliVMS.Services.LicenseService — keep in sync
    private static readonly byte[] HmacSecret =
        [0x1A, 0x2B, 0x3C, 0x4D, 0x5E, 0x6F, 0x70, 0x81,
         0x92, 0xA3, 0xB4, 0xC5, 0xD6, 0xE7, 0xF8, 0x09];

    private static readonly string[] LicenseTypeNames =
        ["基本版", "標準版", "專業版", "進階版", "企業版", "客製版"];

    // Byte layout (8 bytes → 16 hex chars = AAAA-BBBB-CCCC-DDDD):
    //   [0] maxCameras (0-255, 0=invalid)
    //   [1] typeId(7-4) | licenseeId(3-0)
    //   [2] yearsSince2025 (0=永久, 1-255)
    //   [3] month (1-12; ignored when [2]=0)
    //   [4] day (1-31; ignored when [2]=0)
    //   [5] HMAC-SHA256(data[0..4] + ASCII(devicePrefix[0..4]))[0]
    //   [6..7] devicePrefix hex[0..3] as raw bytes

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
            if (maxCameras < 1 || maxCameras > 255)
            {
                MessageBox.Show("最大攝影機數需介於 1 ~ 255", "錯誤",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            int expiryYears = 0;
            if (ExpiryPicker.SelectedDate.HasValue)
            {
                var years = ExpiryPicker.SelectedDate.Value.Year - 2025;
                if (years < 0 || years > 255)
                {
                    MessageBox.Show("到期年份超出範圍（2025 ~ 2280）", "錯誤",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                expiryYears = years;
            }

            var typeItem = LicenseTypeBox.SelectedItem as ComboBoxItem;
            var typeText = typeItem?.Content?.ToString() ?? "進階版 (64)";
            var typeId = LicenseTypeNames.ToList().IndexOf(
                typeText.Contains('(') ? typeText[..typeText.IndexOf('(')].Trim() : typeText);
            if (typeId < 0) typeId = 3;

            var licenseeText = LicenseeBox.Text.Trim();
            if (string.IsNullOrEmpty(licenseeText)) licenseeText = "禾秝公司";
            var licenseeId = licenseeText == "禾秝公司" ? 1 : 0;

            var devicePrefix = deviceCode[..8];

            var data = new byte[5];
            data[0] = (byte)(maxCameras & 0xFF);
            data[1] = (byte)(((typeId & 0x0F) << 4) | (licenseeId & 0x0F));
            data[2] = (byte)(expiryYears & 0xFF);

            if (ExpiryPicker.SelectedDate.HasValue)
            {
                data[3] = (byte)ExpiryPicker.SelectedDate.Value.Month;
                data[4] = (byte)ExpiryPicker.SelectedDate.Value.Day;
            }

            var hmacInput = data.Concat(Encoding.ASCII.GetBytes(devicePrefix[..4])).ToArray();
            using var hmac = new HMACSHA256(HmacSecret);
            var hmacResult = hmac.ComputeHash(hmacInput);

            var keyBytes = new byte[8];
            keyBytes[0] = data[0];
            keyBytes[1] = data[1];
            keyBytes[2] = data[2];
            keyBytes[3] = data[3];
            keyBytes[4] = data[4];
            keyBytes[5] = hmacResult[0];
            keyBytes[6] = Convert.ToByte(devicePrefix[..2], 16);
            keyBytes[7] = Convert.ToByte(devicePrefix[2..4], 16);

            var hex = Convert.ToHexString(keyBytes);
            var licenseKey = $"{hex[..4]}-{hex[4..8]}-{hex[8..12]}-{hex[12..]}";

            var expiryLabel = expiryYears > 0
                ? $"{ExpiryPicker.SelectedDate:yyyy-MM-dd}"
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

    private static string ComputeDeviceCode()
    {
        try
        {
            var parts = new List<string>();

            try
            {
                using var s = new ManagementObjectSearcher("SELECT ProcessorId FROM Win32_Processor");
                foreach (var o in s.Get())
                {
                    var v = o["ProcessorId"]?.ToString()?.Trim();
                    if (!string.IsNullOrEmpty(v)) parts.Add(v);
                }
            }
            catch { }

            try
            {
                using var s = new ManagementObjectSearcher("SELECT SerialNumber FROM Win32_BaseBoard");
                foreach (var o in s.Get())
                {
                    var v = o["SerialNumber"]?.ToString()?.Trim();
                    if (!string.IsNullOrEmpty(v) &&
                        !v.Equals("To be filled by O.E.M.", StringComparison.OrdinalIgnoreCase) &&
                        !v.Equals("Default string", StringComparison.OrdinalIgnoreCase))
                        parts.Add(v);
                }
            }
            catch { }

            if (parts.Count < 2)
            {
                try
                {
                    var d = DriveInfo.GetDrives().FirstOrDefault(x => x.IsReady && x.DriveType == DriveType.Fixed);
                    if (d != null)
                        parts.Add(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(d.RootDirectory.FullName)))[..16]);
                }
                catch { }
            }

            if (parts.Count == 0) return "UNKNOWN";
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", parts))))[..32];
        }
        catch { return "UNKNOWN"; }
    }

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
