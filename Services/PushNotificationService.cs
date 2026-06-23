using System.Windows;
using System.Windows.Media;

namespace HeliVMS.Services;

public sealed class PushNotificationService : IPushNotificationService {
    private readonly INotificationService _toast;

    public PushNotificationService(INotificationService toast) {
        _toast = toast;
    }

    public void ShowToast(string title, string message, string? cameraId = null) {
        var display = string.IsNullOrEmpty(cameraId) ? message : $"[{cameraId}] {message}";
        _toast.Show($"{title}: {display}", "INFO");

        try {
            var main = Application.Current.MainWindow;
            if (main is { WindowState: WindowState.Minimized } || !main.IsFocused) {
                main.FlashWindow();
            }
        } catch { }
    }
}

internal static class FlashWindowHelper {
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool FlashWindow(IntPtr hwnd, bool bInvert);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct FLASHWINFO {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    private const uint FLASHW_ALL = 3;
    private const uint FLASHW_TIMERNOFG = 12;

    public static void FlashWindow(this Window window) {
        try {
            var helper = new System.Windows.Interop.WindowInteropHelper(window);
            var info = new FLASHWINFO {
                cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(FLASHWINFO)),
                hwnd = helper.Handle,
                dwFlags = FLASHW_ALL | FLASHW_TIMERNOFG,
                uCount = 5,
                dwTimeout = 0,
            };
            FlashWindowEx(ref info);
        } catch { }
    }
}
