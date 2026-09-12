using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;

namespace LicenseKeyGenUI;

public partial class MainWindow : Window
{
    private readonly string _appDir;
    private const string PrivateKeyFile = "private.key";
    private const string PublicKeyFile = "public.key";

    public MainWindow()
    {
        InitializeComponent();
        _appDir = AppDomain.CurrentDomain.BaseDirectory;
        LoadKeyPaths();
        RefreshDeviceCode();
    }

    private void LoadKeyPaths()
    {
        var privPath = Path.Combine(_appDir, PrivateKeyFile);
        var pubPath = Path.Combine(_appDir, PublicKeyFile);

        PrivateKeyPathBox.Text = File.Exists(privPath) ? privPath : "（尚未產生）";
        PublicKeyPathBox.Text = File.Exists(pubPath) ? pubPath : "（尚未產生）";

        if (File.Exists(privPath) && File.Exists(pubPath))
        {
            KeyStatusText.Text = "✓ 金鑰對已就緒";
            GenerateKeyBtn.Content = "重新產生金鑰對";
        }
        else
        {
            KeyStatusText.Text = "尚未產生金鑰對，請先產生";
            SignBtn.IsEnabled = false;
        }
    }

    private string PrivKeyPath => Path.Combine(_appDir, PrivateKeyFile);
    private string PubKeyPath => Path.Combine(_appDir, PublicKeyFile);

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
        CopyToClipboard(DeviceCodeBox.Text, "設備碼已複製");
    }

    private void GenerateKeyPair_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            GenerateKeyBtn.IsEnabled = false;
            GenerateKeyBtn.Content = "產生中...";

            using var rsa = RSA.Create(2048);

            var privateKey = rsa.ExportPkcs8PrivateKey();
            File.WriteAllBytes(PrivKeyPath, privateKey);

            var publicKey = rsa.ExportSubjectPublicKeyInfo();
            File.WriteAllBytes(PubKeyPath, publicKey);

            PrivateKeyPathBox.Text = PrivKeyPath;
            PublicKeyPathBox.Text = PubKeyPath;

            var publicKeyB64 = Convert.ToBase64String(publicKey);
            PublicKeyCodeBox.Text = $"""
                private static readonly byte[] EmbeddedPublicKey = Convert.FromBase64String(
                    "{publicKeyB64}");
                """;

            KeyStatusText.Text = "✓ 金鑰對產生成功";
            GenerateKeyBtn.Content = "重新產生金鑰對";
            SignBtn.IsEnabled = true;

            Log("金鑰對已產生");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"產生金鑰失敗：{ex.Message}", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
            KeyStatusText.Text = $"✗ 失敗：{ex.Message}";
        }
        finally
        {
            GenerateKeyBtn.IsEnabled = true;
        }
    }

    private void CopyPublicKeyCode_Click(object sender, RoutedEventArgs e)
    {
        CopyToClipboard(PublicKeyCodeBox.Text, "EmbeddedPublicKey 程式碼已複製");
    }

    private void SignLicense_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SignBtn.IsEnabled = false;
            SignBtn.Content = "簽署中...";
            LicenseKeyBox.Text = "";
            SignStatusText.Text = "";

            var machineId = SignMachineIdBox.Text.Trim();
            if (machineId.Length != 32 || machineId.Any(c => !Uri.IsHexDigit(c)))
            {
                MessageBox.Show("設備碼必須是 32 碼十六進位字串", "錯誤", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var maxCameras = int.TryParse(SignMaxBox.Text.Trim(), out var m) ? m : 64;

            var expiry = SignExpiryBox.Text.Trim();
            if (string.IsNullOrEmpty(expiry) || expiry == "yyyy-MM-dd 或留空=永久")
                expiry = null;

            var licenseType = SignTypeBox.Text.Trim();
            if (string.IsNullOrEmpty(licenseType)) licenseType = "進階版";

            var licensee = SignLicenseeBox.Text.Trim();
            if (string.IsNullOrEmpty(licensee)) licensee = "禾秝軟體";

            if (!File.Exists(PrivKeyPath))
            {
                MessageBox.Show("請先產生金鑰對", "錯誤", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var privateKeyBlob = File.ReadAllBytes(PrivKeyPath);
            using var rsa = RSA.Create(2048);
            rsa.ImportPkcs8PrivateKey(privateKeyBlob, out _);

            var payloadObj = new Dictionary<string, object?>
            {
                ["mid"] = machineId,
                ["max"] = maxCameras,
                ["exp"] = expiry,
                ["type"] = licenseType,
                ["lic"] = licensee
            };

            var payloadJson = JsonSerializer.Serialize(payloadObj, new JsonSerializerOptions
            {
                DictionaryKeyPolicy = null,
                WriteIndented = false
            });

            var payloadBytes = Encoding.UTF8.GetBytes(payloadJson);
            var signature = rsa.SignData(payloadBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

            var payloadB64 = Base64UrlEncode(payloadBytes);
            var sigB64 = Base64UrlEncode(signature);
            var licenseKey = $"HELVMS-{payloadB64}.{sigB64}";

            LicenseKeyBox.Text = licenseKey;
            SignStatusText.Text = $"✓ 授權碼已產生（{licenseType} / {maxCameras} 台 / {(expiry ?? "永久")}）";
            Log($"授權碼已產生：{licenseType} / {maxCameras} 台");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"簽署失敗：{ex.Message}", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
            SignStatusText.Text = $"✗ 失敗：{ex.Message}";
        }
        finally
        {
            SignBtn.IsEnabled = true;
            SignBtn.Content = "產生授權碼";
        }
    }

    private void CopyLicenseKey_Click(object sender, RoutedEventArgs e)
    {
        CopyToClipboard(LicenseKeyBox.Text, "授權碼已複製");
    }

    private void CopyToClipboard(string text, string successMessage)
    {
        try
        {
            if (string.IsNullOrEmpty(text)) return;
            Clipboard.SetText(text);
            SignStatusText.Text = $"✓ {successMessage}";
        }
        catch (Exception ex)
        {
            SignStatusText.Text = $"✗ 複製失敗：{ex.Message}";
        }
    }

    private void Log(string message)
    {
        Debug.WriteLine($"[LicenseKeyGen] {message}");
    }

    private static string ComputeDeviceCode()
    {
        try
        {
            var parts = new List<string>();

            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT ProcessorId FROM Win32_Processor");
                foreach (var obj in searcher.Get())
                {
                    var val = obj["ProcessorId"]?.ToString()?.Trim();
                    if (!string.IsNullOrEmpty(val))
                        parts.Add(val);
                }
            }
            catch { }

            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT SerialNumber FROM Win32_BaseBoard");
                foreach (var obj in searcher.Get())
                {
                    var val = obj["SerialNumber"]?.ToString()?.Trim();
                    if (!string.IsNullOrEmpty(val) &&
                        !val.Equals("To be filled by O.E.M.", StringComparison.OrdinalIgnoreCase) &&
                        !val.Equals("Default string", StringComparison.OrdinalIgnoreCase))
                        parts.Add(val);
                }
            }
            catch { }

            if (parts.Count < 2)
            {
                try
                {
                    var drive = DriveInfo.GetDrives()
                        .FirstOrDefault(d => d.IsReady && d.DriveType == DriveType.Fixed);
                    if (drive != null)
                    {
                        var volumeId = drive.RootDirectory.FullName;
                        var hash = Convert.ToHexString(
                            SHA256.HashData(Encoding.UTF8.GetBytes(volumeId)));
                        parts.Add(hash[..16]);
                    }
                }
                catch { }
            }

            if (parts.Count == 0)
                return "UNKNOWN";

            var raw = string.Join("|", parts);
            var finalHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
            return finalHash[..32];
        }
        catch
        {
            return "UNKNOWN";
        }
    }

    private static string Base64UrlEncode(byte[] data)
    {
        return Convert.ToBase64String(data)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
