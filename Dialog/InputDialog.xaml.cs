// ============================================================
// HeliVMS - 智慧監控管理系統
// 禾秝軟體開發團隊
// 代碼設計：洪俊士
// 版本：V1.0.0
// ============================================================

using System.Windows;

namespace HeliVMS.Dialog;

public partial class InputDialog : Window {
    public string? Value { get; private set; }

    public InputDialog(string title, string prompt, string defaultValue = "") {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        InputBox.Text = defaultValue;
        InputBox.SelectAll();
        Loaded += (_, _) => InputBox.Focus();
    }

    private void OkButton_Click(object sender, RoutedEventArgs e) {
        Value = InputBox.Text.Trim();
        DialogResult = !string.IsNullOrEmpty(Value);
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) {
        DialogResult = false;
        Close();
    }
}
