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

    private const string DefaultPassword = "hr22619219";

    public LoginWindow()
    {
        InitializeComponent();
        EnsurePasswordFile();
        PasswordInput.Focus();
    }

    private static void EnsurePasswordFile()
    {
        if (!File.Exists(PasswordFilePath))
            File.WriteAllText(PasswordFilePath, HashPassword(DefaultPassword));
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
