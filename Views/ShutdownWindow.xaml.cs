using System.Windows;

namespace HeliVMS.Views;

public partial class ShutdownWindow : Window
{
    public ShutdownWindow()
    {
        InitializeComponent();
    }

    public void UpdateStatus(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => UpdateStatus(message));
            return;
        }
        StatusText.Text = message;
    }

    public void AppendStatus(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => AppendStatus(message));
            return;
        }
        if (string.IsNullOrWhiteSpace(StatusText.Text))
            StatusText.Text = message;
        else
            StatusText.Text += "\n" + message;
    }
}
