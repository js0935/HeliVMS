using System.Windows;
using System.Windows.Input;

namespace LicenseKeyGen;

public partial class ChangePasswordDialog : Window
{
    public ChangePasswordDialog()
    {
        InitializeComponent();
        Owner = Application.Current.MainWindow;
        OldPasswordBox.Focus();
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

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        var oldPw = OldPasswordBox.Password;
        var newPw = NewPasswordBox.Password;
        var confirmPw = ConfirmPasswordBox.Password;

        if (string.IsNullOrEmpty(oldPw) || string.IsNullOrEmpty(newPw))
        {
            StatusText.Text = "請填寫所有欄位";
            return;
        }

        if (newPw.Length < 4)
        {
            StatusText.Text = "新密碼至少 4 碼";
            return;
        }

        if (newPw != confirmPw)
        {
            StatusText.Text = "新密碼與確認密碼不符";
            return;
        }

        var storedHash = System.IO.File.ReadAllText(
            System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "password.dat"),
            System.Text.Encoding.UTF8).Trim();

        var oldHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(oldPw)));

        if (storedHash != oldHash)
        {
            StatusText.Text = "目前密碼錯誤";
            return;
        }

        LoginWindow.ChangePassword(newPw);
        MessageBox.Show("密碼已更新", "完成", MessageBoxButton.OK, MessageBoxImage.Information);
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
