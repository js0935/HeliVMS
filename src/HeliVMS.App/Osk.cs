using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

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
            ContentRendered += (_, _) => Realign();
            Focusable = false;
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
            shell.PreviewMouseLeftButtonDown += OnShellDragStart;

            var rows = new List<string[]>
            {
                new[] { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0" },
                new[] { "Q", "W", "E", "R", "T", "Y", "U", "I", "O", "P" },
                new[] { "A", "S", "D", "F", "G", "H", "J", "K", "L", "退格" },
                new[] { "Z", "X", "C", "V", "B", "N", "M", ".", "@", "_" },
                new[] { "空格", "清空", "收起" }
            };

            var board = new StackPanel();
            for (var r = 0; r < rows.Count; r++)
            {
                var line = new StackPanel { Orientation = Orientation.Horizontal };
                foreach (var label in rows[r])
                {
                    line.Children.Add(MakeKey(label));
                }
                board.Children.Add(line);
            }

            shell.Child = board;
            return shell;
        }

        private void OnShellDragStart(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is Button)
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

        private Button MakeKey(string label)
        {
            var accent = label == "收起";
            var b = new Button
            {
                Content = label,
                Width = label == "空格" ? 152 : label is "退格" or "清空" or "收起" ? 76 : 38,
                Height = 34,
                Margin = new Thickness(1.5),
                Padding = new Thickness(0),
                Foreground = new SolidColorBrush(Color.FromRgb(0xEB, 0xF3, 0xFF)),
                Background = new SolidColorBrush(accent
                    ? Color.FromRgb(0x7A, 0x3E, 0x2E)
                    : Color.FromRgb(0x1A, 0x2C, 0x49)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x4A, 0x73)),
                Focusable = false,
                IsTabStop = false,
                Cursor = Cursors.Hand,
                FontSize = 14
            };
            AutomationProperties.SetAutomationId(b, "OskKey_" + label);
            b.Style = null;
            b.Click += OnKeyClicked;
            return b;
        }

        private void OnKeyClicked(object sender, RoutedEventArgs e)
        {
            if (sender is not Button b || Target is null) return;
            switch (b.Content as string)
            {
                case "空格": Osk.AppendInput(Target, " "); break;
                case "清空":
                    if (Target is TextBox t) t.Clear();
                    else if (Target is PasswordBox p) p.Clear();
                    break;
                case "退格": Osk.AppendInput(Target, "\b"); break;
                case "收起": Hide(); break;
                default:
                    if (b.Content is string s && s.Length == 1) Osk.AppendInput(Target, s);
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