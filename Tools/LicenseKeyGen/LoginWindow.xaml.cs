using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Input;

namespace LicenseKeyGen;

public partial class LoginWindow : Window
{
    private static readonly string PasswordFilePath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "password.dat");

    /// <summary>
    /// 首次啟動（尚無 <c>password.dat</c>）時用來建立初始密碼的環境變數名稱。
    /// </summary>
    private const string BootstrapPasswordVariable = "HELIVMS_KEYGEN_PASSWORD";

    public LoginWindow()
    {
        InitializeComponent();

        if (!TryEnsurePasswordFile())
        {
            // 刻意不內建預設密碼：本工具會簽發可解鎖產品的授權金鑰，原始碼又隨公開
            // repository 流通，任何寫死的密碼等於把金鑰簽發權一起公開。缺檔且未設定
            // 環境變數時就擋在登入前，而不是用一個全網皆知的密碼放行。
            ErrorText.Text = "找不到密碼檔，請設定環境變數 "
                + BootstrapPasswordVariable + " 後重新啟動";
            PasswordInput.IsEnabled = false;
            LoginBtn.IsEnabled = false;
            return;
        }

        PasswordInput.Focus();
    }

    /// <summary>
    /// 建立初始密碼檔；已有 <c>password.dat</c> 時不讀環境變數。
    /// 回傳 false 代表無法取得可用的密碼（呼叫端必須擋下登入）。
    /// </summary>
    private static bool TryEnsurePasswordFile()
    {
        if (File.Exists(PasswordFilePath))
            return true;

        var bootstrap = Environment.GetEnvironmentVariable(BootstrapPasswordVariable);
        if (string.IsNullOrWhiteSpace(bootstrap))
            return false;

        File.WriteAllText(PasswordFilePath, HashPassword(bootstrap), Encoding.UTF8);
        return true;
    }

    private static bool VerifyPassword(string input)
    {
        if (!File.Exists(PasswordFilePath)) return false;
        var stored = File.ReadAllText(PasswordFilePath, Encoding.UTF8).Trim();
        return stored == HashPassword(input);
    }

    internal static void ChangePassword(string newPassword)
    {
        File.WriteAllText(PasswordFilePath, HashPassword(newPassword), Encoding.UTF8);
    }

    private static string HashPassword(string pw)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(pw));
        return Convert.ToHexString(bytes);
    }

    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Login_Click(object sender, RoutedEventArgs e)
    {
        AttemptLogin();
    }

    private void PasswordInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            AttemptLogin();
    }

    private void AttemptLogin()
    {
        var pw = PasswordInput.Password;
        if (string.IsNullOrEmpty(pw))
        {
            ErrorText.Text = "請輸入密碼";
            return;
        }

        if (VerifyPassword(pw))
        {
            DialogResult = true;
            Close();
        }
        else
        {
            ErrorText.Text = "密碼錯誤";
            PasswordInput.Clear();
            PasswordInput.Focus();
        }
    }
}
