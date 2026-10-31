using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace HeliVMS.App
{
    /// <summary>
    /// 螢幕虛擬鍵盤（觸控模式）：輸入框右側的「⌨」呼叫鈕改為啟動 Windows 內建的
    /// <c>osk.exe</c>，不再內建自製鍵盤面板。
    ///
    /// <para>
    /// 為什麼改用系統小鍵盤：自製面板只能覆蓋 ASCII／數字（版面、IME、各國鍵帽都是
    /// 自己維護的成本），而系統 osk 是無障礙元件，鍵帽齊全、支援 IME、隨 Windows 更新。
    /// 這裡只剩兩件事：把焦點還給輸入框（osk 的按鍵送到目前有焦點的控制項——呼叫鈕
    /// 被點下時焦點會停在按鈕上，不還焦點等於打錯地方），以及把 osk 叫起來。
    /// </para>
    /// <para>
    /// osk 是單一執行個體：已開著時再啟動只會把既有視窗帶到前景，不會開第二份。
    /// 啟用開關（<see cref="Enabled"/>）只控制呼叫鈕的顯示；已開的 osk 屬於系統視窗，
    /// 不由本程式收回。
    /// </para>
    /// </summary>
    public static class Osk
    {
        private static readonly HashSet<Button> _callButtons = new();
        private static bool _enabled = true;

        public static bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled == value) return;
                _enabled = value;
                foreach (var b in _callButtons)
                {
                    b.Visibility = _enabled ? Visibility.Visible : Visibility.Collapsed;
                }
            }
        }

        /// <summary>供輸入框控制範本讀取呼叫按鈕的初始可見性。</summary>
        public static Visibility CallVisibility => _enabled ? Visibility.Visible : Visibility.Collapsed;

        public static void Toggle(TextBox target) => ToggleCore(target);
        public static void Toggle(PasswordBox target) => ToggleCore(target);

        internal static void RegisterCallButton(Button b)
        {
            _callButtons.Add(b);
            b.Visibility = _enabled ? Visibility.Visible : Visibility.Collapsed;
        }

        internal static void UnregisterCallButton(Button b) => _callButtons.Remove(b);

        private static void ToggleCore(Control target)
        {
            if (!_enabled || target is null) return;

            // osk 的按鍵送到「目前有焦點的控制項」。呼叫鈕被點下時 WPF 會把焦點給按鈕，
            // 不先還給輸入框，osk 打的字會落在按鈕上——看起來就是「小鍵盤沒作用」。
            target.Focus();

            try
            {
                Process.Start(new ProcessStartInfo(ResolveOskPath())
                {
                    UseShellExecute = true,
                });
            }
            catch (Exception ex) when (
                ex is Win32Exception
                    or FileNotFoundException)
            {
                // 精簡安裝（IoT／Server Core）可能沒有 osk.exe：安靜略過。
                // 呼叫鈕還在，使用者換實體鍵盤輸入；不值得為它彈錯誤視窗。
            }
        }

        /// <summary>
        /// 解析系統 osk.exe 的路徑。
        /// </summary>
        /// <remarks>
        /// 64 位元程序直接用 System32。32 位元程序在 64 位元 Windows 上會被檔案系統
        /// 轉送到 SysWOW64——那裡<b>沒有</b> osk.exe，得走 <c>Sysnative</c> 這個只存在於
        /// 32 位元程序眼中的虛擬別名。找不到檔案也照樣回傳路徑，讓啟動端統一由
        /// <see cref="System.Diagnostics.ProcessStartInfo"/> 啟動呼叫的例外處理（精簡安裝）。
        /// </remarks>
        internal static string ResolveOskPath()
        {
            var system = Environment.SystemDirectory;
            if (Environment.Is64BitProcess || !Environment.Is64BitOperatingSystem)
            {
                return Path.Combine(system, "osk.exe");
            }

            var windir = Path.GetDirectoryName(system);
            return string.IsNullOrEmpty(windir)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Sysnative", "osk.exe")
                : Path.Combine(windir, "Sysnative", "osk.exe");
        }
    }
}
