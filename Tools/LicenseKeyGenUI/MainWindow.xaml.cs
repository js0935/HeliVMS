using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Windows;
using HeliVMS.Licensing;
using HeliVMS.Licensing.Crypto;

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
        DeviceCodeBox.Text = MachineIdProvider.GetDeviceCode();
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

            var publicKeyPem = RsaPem.ToPublicPem(rsa);
            PublicKeyCodeBox.Text =
                "public const string Value =\n    \"\"\"\n" +
                string.Concat(publicKeyPem.TrimEnd().Split('\n').Select(l => "    " + l.Trim() + "\n")) +
                "    \"\"\";";

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

            var tierName = SignTypeBox.Text.Trim();
            var tier = LicenseTiers.Find(tierName) ?? LicenseTiers.All[3];

            var maxCameras = int.TryParse(SignMaxBox.Text.Trim(), out var m) ? m : tier.DefaultCameras;
            if (maxCameras is < 1 or > LicenseTiers.MaxCameras)
            {
                MessageBox.Show($"通道數必須介於 1～{LicenseTiers.MaxCameras}", "錯誤", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var expiry = SignExpiryBox.Text.Trim();
            if (string.IsNullOrEmpty(expiry) || expiry == "yyyy-MM-dd 或留空=永久")
                expiry = null;

            var licensee = SignLicenseeBox.Text.Trim();
            if (string.IsNullOrEmpty(licensee)) licensee = "禾秝軟體";

            if (!File.Exists(PrivKeyPath))
            {
                MessageBox.Show("請先產生金鑰對", "錯誤", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var privateKeyBlob = File.ReadAllBytes(PrivKeyPath);
            using var rsa = RSA.Create();
            rsa.ImportPkcs8PrivateKey(privateKeyBlob, out _);

            // 交由產品端授權庫組裝 payload 與簽章，格式不會與產品端漂移（§19.1／§19.2）。
            var payload = new LicensePayload
            {
                Machine = machineId.ToUpperInvariant(),
                IssuedUtc = DateTime.UtcNow,
                ExpiresUtc = string.IsNullOrEmpty(expiry)
                    ? null
                    : DateTime.SpecifyKind(DateTime.Parse(expiry), DateTimeKind.Utc),
                Cameras = maxCameras,
                Features = tier.Features,
                Issuer = licensee,
            };

            var licenseKey = LicenseSerializer.Sign(payload, rsa);

            LicenseKeyBox.Text = licenseKey;
            SignStatusText.Text =
                $"✓ 授權碼已產生（{tier.Name} / {maxCameras} 台 / {(expiry ?? "永久")}）";
            Log($"授權碼已產生：{tier.Name} / {maxCameras} 台");
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

}
