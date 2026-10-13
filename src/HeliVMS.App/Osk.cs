using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using HeliVMS.Shared;

namespace HeliVMS.App
{
    /// <summary>
    /// 螢幕虛擬鍵盤（觸控模式）。
    /// 由輸入框右側的「⌨」呼叫按鈕呼出，不隨焦點自動彈出；僅支援 ASCII／數字與常用符號。
    /// </summary>
    public static class Osk
    {
        private static readonly Dictionary<Window, OskPanel> _panels = new();
        private static readonly HashSet<Button> _callButtons = new();
        private static bool _enabled = true;

        public static bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled == value) return;
                _enabled = value;
                if (!_enabled)
                {
                    foreach (var p in _panels.Values) p.Hide();
                }
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
            var host = Window.GetWindow(target);
            if (host is null) return;
            if (!_panels.TryGetValue(host, out var panel))
            {
                panel = new OskPanel(host);
                _panels[host] = panel;
                host.Closed += (_, _) => Drop(host);
            }

            if (panel.IsVisible && panel.Target == target)
            {
                panel.Hide();
                return;
            }

            panel.Target = target;
            panel.Show();
        }

        private static void Drop(Window host)
        {
            if (_panels.TryGetValue(host, out var panel))
            {
                _panels.Remove(host);
                panel.Close();
            }
        }

        internal static void AppendInput(Control target, string ch)
        {
            switch (target)
            {
                case TextBox tb:
                    if (ch == "\b")
                    {
                        if (tb.CaretIndex > 0)
                        {
                            tb.Text = tb.Text.Remove(tb.CaretIndex - 1, 1);
                            tb.CaretIndex = Math.Max(0, tb.CaretIndex - 1);
                        }
                    }
                    else if (ch.Length > 0)
                    {
                        tb.Text = tb.Text.Insert(tb.CaretIndex, ch);
                        tb.CaretIndex += ch.Length;
                    }
                    break;
                case PasswordBox pb:
                    if (ch == "\b")
                    {
                        if (pb.Password.Length > 0)
                        {
                            pb.Password = pb.Password.Substring(0, pb.Password.Length - 1);
                        }
                    }
                    else if (ch.Length > 0)
                    {
                        pb.Password += ch;
                    }
                    break;
            }
        }
    }

    /// <summary>深色螢幕虛擬鍵盤視窗；對齊宿主視窗底部置中，不搶焦點。</summary>
    internal sealed class OskPanel : Window
    {
        public Control? Target { get; set; }

        private readonly Window _host;

        private bool _shift;

        /// <summary>
        /// 讓 OSK 視窗永不取得啟動（activation）。只靠 <see cref="Window.ShowActivated"/>＝false
        /// 不夠：那僅影響第一次 Show，之後點按鍵仍會把 OSK 設為作用中視窗，宿主隨即
        /// Deactivated 而把鍵盤收起來——症狀就是「按一個字母鍵盤就消失」。
        /// WS_EX_NOACTIVATE 讓點擊只送滑鼠輸入、不轉移焦點。
        /// </summary>
        private const int GwlExStyle = -20;
        private const int WsExNoActivate = 0x08000000;
        private const int WsExToolWindow = 0x00000080;

        [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
        private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        private static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
            => IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : new IntPtr(GetWindowLong32(hWnd, nIndex));

        private static void SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr value)
        {
            if (IntPtr.Size == 8)
            {
                SetWindowLongPtr64(hWnd, nIndex, value);
            }
            else
            {
                SetWindowLong32(hWnd, nIndex, value.ToInt32());
            }
        }

        public OskPanel(Window host)
        {
            _host = host;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            SizeToContent = SizeToContent.WidthAndHeight;
            Content = BuildContent();
            Owner = host;
            host.LocationChanged += (_, _) => Realign();
            host.SizeChanged += (_, _) => Realign();
            host.StateChanged += OnHostState;
            host.Deactivated += (_, _) => Hide();
            ContentRendered += (_, _) => Realign();
            Focusable = false;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var handle = new WindowInteropHelper(this).Handle;
            var exStyle = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
            SetWindowLongPtr(handle, GwlExStyle, new IntPtr(exStyle | WsExNoActivate | WsExToolWindow));
        }

        private Border BuildContent()
        {
            var shell = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x0E, 0x1B, 0x2E)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x4A, 0x73)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8),
                Margin = new Thickness(4),
                Effect = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 2, Opacity = 0.5 }
            };
            // 拖曳只認外框本身。原本在 PreviewMouseLeftButtonDown 裡用型別判斷原始來源
            // 是不是 Button，這是錯的：點按鈕時原始來源是範本內的 Border／AccessText，
            // 永遠不會是 Button，於是每個按鍵都誤觸 DragMove 的模態迴圈並吃掉 Click——
            // 症狀就是「叫得出來卻打不出字」。改用氣泡階段的 MouseLeftButtonDown：按鈕會把它
            // 標記為已處理、不冒泡到外框，剩下的空隙才由外框接手，並以參照相等確認點在外框上。
            shell.MouseLeftButtonDown += (_, e) =>
            {
                if (ReferenceEquals(e.OriginalSource, shell))
                {
                    TryDragMove();
                }
            };

            var board = new StackPanel();
            foreach (var row in OnScreenKeyboardLayout.Rows(_shift))
            {
                var line = new StackPanel { Orientation = Orientation.Horizontal };
                foreach (var key in row)
                {
                    line.Children.Add(MakeKey(key));
                }
                board.Children.Add(line);
            }

            shell.Child = board;
            return shell;
        }

        private void TryDragMove()
        {
            if (Mouse.LeftButton != MouseButtonState.Pressed)
            {
                return;
            }

            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
            }
        }

        private static void Place(Grid grid, Button b, int r, int c, int span = 1, int _ = 1)
        {
            Grid.SetRow(b, r);
            Grid.SetColumn(b, c);
            Grid.SetColumnSpan(b, span);
            grid.Children.Add(b);
        }

        private Button MakeKey(string id)
        {
            var fill = id switch
            {
                OnScreenKeyboardLayout.CollapseKey => Color.FromRgb(0x7A, 0x3E, 0x2E),
                OnScreenKeyboardLayout.ShiftKey when _shift => Color.FromRgb(0x26, 0x60, 0xAA),
                _ => Color.FromRgb(0x1A, 0x2C, 0x49),
            };

            var b = new Button
            {
                Content = LabelFor(id),
                Width = id switch
                {
                    OnScreenKeyboardLayout.SpaceKey => 152,
                    OnScreenKeyboardLayout.ShiftKey => 44,
                    OnScreenKeyboardLayout.BackspaceKey
                        or OnScreenKeyboardLayout.ClearKey
                        or OnScreenKeyboardLayout.CollapseKey => 76,
                    _ => 38,
                },
                Height = 34,
                Margin = new Thickness(1.5),
                Padding = new Thickness(0),
                Foreground = new SolidColorBrush(Color.FromRgb(0xEB, 0xF3, 0xFF)),
                Background = new SolidColorBrush(fill),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x4A, 0x73)),
                Focusable = false,
                IsTabStop = false,
                Cursor = Cursors.Hand,
                FontSize = 14,
                ToolTip = id == OnScreenKeyboardLayout.ShiftKey ? Localizer.T("Osk.Shift") : null
            };
            AutomationProperties.SetAutomationId(b, "OskKey_" + id);
            b.Tag = id;
            b.Style = null;
            b.Click += OnKeyClicked;
            return b;
        }

        private static string LabelFor(string id) => id switch
        {
            OnScreenKeyboardLayout.SpaceKey => Localizer.T("Osk.Space"),
            OnScreenKeyboardLayout.ClearKey => Localizer.T("Osk.Clear"),
            OnScreenKeyboardLayout.CollapseKey => Localizer.T("Osk.Collapse"),
            OnScreenKeyboardLayout.BackspaceKey => Localizer.T("Osk.Backspace"),
            OnScreenKeyboardLayout.ShiftKey => "⇧",
            _ => id,
        };

        private void OnKeyClicked(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string id })
            {
                return;
            }

            // 大小寫與收起不依賴 Target：即使沒有目標輸入框（理論上不會發生）也應正常反應。
            switch (id)
            {
                case OnScreenKeyboardLayout.ShiftKey:
                    _shift = !_shift;
                    Content = BuildContent();
                    return;
                case OnScreenKeyboardLayout.CollapseKey:
                    Hide();
                    return;
            }

            if (Target is null)
            {
                return;
            }

            switch (id)
            {
                case OnScreenKeyboardLayout.SpaceKey:
                    Osk.AppendInput(Target, " ");
                    break;
                case OnScreenKeyboardLayout.ClearKey:
                    ClearTarget();
                    break;
                case OnScreenKeyboardLayout.BackspaceKey:
                    Osk.AppendInput(Target, "\b");
                    break;
                default:
                    if (id.Length == 1)
                    {
                        Osk.AppendInput(Target, id);
                    }

                    break;
            }
        }

        private void ClearTarget()
        {
            switch (Target)
            {
                case TextBox t:
                    t.Clear();
                    break;
                case PasswordBox p:
                    p.Clear();
                    break;
            }
        }

        private void OnHostState(object? sender, EventArgs e)
        {
            if (_host.WindowState == WindowState.Minimized) Hide();
            else if (_host.WindowState == WindowState.Normal) Realign();
        }

        private void Realign()
        {
            if (!IsVisible || _host.WindowState == WindowState.Minimized) return;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (!IsVisible || !_host.IsVisible) return;
                var w = ActualWidth;
                var h = ActualHeight;
                if (w <= 0 || h <= 0) return;
                var work = SystemParameters.WorkArea;
                Left = Math.Max(work.Left, Math.Min(_host.Left + (_host.ActualWidth - w) / 2, work.Right - w));
                var altBelow = _host.Top + _host.ActualHeight + 2;
                Top = altBelow + h <= work.Bottom
                    ? altBelow
                    : Math.Max(work.Top, _host.Top + _host.ActualHeight - h - 6);
            }));
        }

        public new void Show()
        {
            if (IsVisible) return;
            base.Show();
            Realign();
        }
    }
}