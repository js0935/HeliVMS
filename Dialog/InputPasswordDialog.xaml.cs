using System.Windows;

namespace HeliVMS.Dialog;

public partial class InputPasswordDialog : Window
{
    public string? Password { get; private set; }

    public InputPasswordDialog(string prompt = "請輸入密碼")
    {
        InitializeComponent();
        PromptText.Text = prompt;
        Loaded += (_, _) =>
        {
            Activate();
            PasswordBox.Focus();
        };
    }

    public static string? Show(string prompt, string title)
    {
        var dlg = new InputPasswordDialog(prompt) { Title = title };
        dlg.Owner = Application.Current.Windows.Count > 0
            ? Application.Current.Windows[0] : null;
        return dlg.ShowDialog() == true ? dlg.Password : null;
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        Password = PasswordBox.Password;
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void PasswordBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
            ConfirmButton_Click(sender, e);
    }
}
