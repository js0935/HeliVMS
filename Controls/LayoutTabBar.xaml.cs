using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HeliVMS.Models;

namespace HeliVMS.Controls;

public partial class LayoutTabBar : UserControl {
    private readonly List<TabItemData> _tabs = [];
    private TabItemData? _selectedTab;
    private bool _isRenaming;
    private TabItemData? _dragTab;
    private Point _dragStart;

    public LayoutTab? CurrentTab => _selectedTab?.Layout;
    public IReadOnlyList<LayoutTab> Tabs => _tabs.Select(t => t.Layout).ToList().AsReadOnly();

    public event EventHandler<LayoutTab>? TabSelected;
    public event EventHandler<LayoutTab>? TabAdded;
    public event EventHandler<string>? TabRenamed;
    public event EventHandler? TabsReordered;

    public LayoutTabBar() {
        InitializeComponent();
    }

    public void LoadTabs(IEnumerable<LayoutTab> tabs) {
        _tabs.Clear();
        TabStrip.Children.Clear();
        _selectedTab = null;
        foreach (var tab in tabs)
            AddTabInternal(tab);
        if (_tabs.Count > 0)
            SelectTab(_tabs[0].Layout.Id);
    }

    public void AddTab(LayoutTab tab) {
        AddTabInternal(tab);
        SelectTab(tab.Id);
    }

    private TabItemData AddTabInternal(LayoutTab tab) {
        var data = new TabItemData { Layout = tab };
        _tabs.Add(data);
        data.UI = BuildTabUI(data);
        TabStrip.Children.Add(data.UI);
        return data;
    }

    public void CloseTab(string id) {
        var idx = _tabs.FindIndex(t => t.Layout.Id == id);
        if (idx < 0 || _tabs.Count <= 1) return;
        var data = _tabs[idx];
        _tabs.RemoveAt(idx);
        TabStrip.Children.Remove(data.UI);
        if (_selectedTab?.Layout.Id == id) {
            var next = _tabs.Count > idx ? _tabs[idx] : _tabs[^1];
            SelectTab(next.Layout.Id);
        }
    }

    public void SelectTab(string id) {
        var data = _tabs.FirstOrDefault(t => t.Layout.Id == id);
        if (data is null) return;
        if (_selectedTab is not null) {
            var prevBorder = (Border)_selectedTab.UI;
            prevBorder.Background = Brushes.Transparent;
            prevBorder.BorderThickness = new Thickness(0, 0, 1, 0);
        }
        _selectedTab = data;
        var border = (Border)data.UI;
        border.Background = new SolidColorBrush(Color.FromArgb(25, 0x21, 0x96, 0xF3));
        border.BorderThickness = new Thickness(0, 0, 0, 2);
        border.BorderBrush = new SolidColorBrush(Color.FromArgb(220, 0x21, 0x96, 0xF3));
        TabSelected?.Invoke(this, data.Layout);
    }

    public void MarkDirty(string id, bool dirty = true) {
        var data = _tabs.FirstOrDefault(t => t.Layout.Id == id);
        if (data is null) return;
        data.Dirty = dirty;
        UpdateTabLabel(data);
    }

    public void MarkClean(string id) => MarkDirty(id, false);

    private void UpdateTabLabel(TabItemData data) {
        var border = (Border)data.UI;
        var stack = (StackPanel)((Border)border.Child).Child;
        var nameText = (TextBlock)stack.Children[0];
        nameText.Text = data.Layout.Name;
        var dirtyText = (TextBlock)stack.Children[1];
        dirtyText.Visibility = data.Dirty ? Visibility.Visible : Visibility.Collapsed;
    }

    private Border BuildTabUI(TabItemData data) {
        var nameText = new TextBlock {
            Text = data.Layout.Name,
            FontSize = 12,
            Foreground = GetResource<Brush>("TextBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        var dirtyText = new TextBlock {
            Text = " *",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07)),
            Visibility = Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Center
        };
        var closeIcon = new System.Windows.Shapes.Path {
            Data = TryFindResource("IconClose") as Geometry ?? Geometry.Parse("M0,0"),
            Fill = GetResource<Brush>("SecondaryTextBrush"),
            Width = 10, Height = 10, Stretch = Stretch.Uniform
        };
        var closeBtn = new Button {
            Content = closeIcon,
            Width = 18, Height = 18,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            ToolTip = "關閉分頁",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
        };
        closeBtn.Click += (_, _) => CloseTab(data.Layout.Id);

        var innerStack = new StackPanel { Orientation = Orientation.Horizontal };
        innerStack.Children.Add(nameText);
        innerStack.Children.Add(dirtyText);
        innerStack.Children.Add(closeBtn);

        var innerBorder = new Border { Child = innerStack };

        var border = new Border {
            Child = innerBorder,
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(0),
            Cursor = Cursors.Hand,
            BorderThickness = new Thickness(0),
            MinWidth = 50
        };
        border.MouseDown += (_, args) => {
            if (args.ChangedButton == MouseButton.Left) {
                SelectTab(data.Layout.Id);
                _dragTab = data;
                _dragStart = args.GetPosition(TabStrip);
                if (args.ClickCount == 2)
                    BeginRename(data);
            }
        };
        border.MouseMove += (_, args) => {
            if (_dragTab is null || _dragTab != data || args.LeftButton != MouseButtonState.Pressed) return;
            var pos = args.GetPosition(TabStrip);
            if (Math.Abs(pos.X - _dragStart.X) < 20) return;
            var srcIdx = _tabs.IndexOf(data);
            if (srcIdx < 0) return;
            var tgtIdx = srcIdx;
            var cumulative = 0.0;
            for (int i = 0; i < _tabs.Count; i++) {
                var w = ((Border)_tabs[i].UI).ActualWidth;
                cumulative += w / 2;
                if (pos.X < cumulative) { tgtIdx = i; break; }
                cumulative += w / 2;
            }
            if (tgtIdx != srcIdx && tgtIdx >= 0 && tgtIdx < _tabs.Count) {
                _tabs.RemoveAt(srcIdx);
                _tabs.Insert(tgtIdx, data);
                TabStrip.Children.RemoveAt(srcIdx);
                TabStrip.Children.Insert(tgtIdx, data.UI);
                _dragTab = null;
                TabsReordered?.Invoke(this, EventArgs.Empty);
            }
        };
        border.MouseUp += (_, _) => _dragTab = null;
        border.ContextMenu = BuildTabContextMenu(data);
        return border;
    }

    private void BeginRename(TabItemData data) {
        if (_isRenaming) return;
        _isRenaming = true;

        var border = (Border)data.UI;
        var innerBorder = (Border)border.Child;
        var stack = (StackPanel)innerBorder.Child;

        var tb = new TextBox {
            Text = data.Layout.Name,
            FontSize = 12,
            Width = 100,
            Background = new SolidColorBrush(Color.FromArgb(60, 0, 0, 0)),
            Foreground = new SolidColorBrush(Colors.White),
            BorderThickness = new Thickness(0),
            CaretBrush = new SolidColorBrush(Colors.White),
        };
        tb.LostFocus += (_, _) => CommitRename(data, tb);
        tb.KeyDown += (_, e) => {
            if (e.Key == Key.Enter) CommitRename(data, tb);
            else if (e.Key == Key.Escape) { _isRenaming = false; UpdateTabLabel(data); }
        };

        stack.Children[0] = tb;
        tb.Focus();
        tb.SelectAll();
    }

    private void CommitRename(TabItemData data, TextBox tb) {
        _isRenaming = false;
        var name = tb.Text.Trim();
        if (!string.IsNullOrEmpty(name) && name != data.Layout.Name) {
            data.Layout.Name = name;
            TabRenamed?.Invoke(this, data.Layout.Id);
        }
        UpdateTabLabel(data);
    }

    private void BtnAddTab_Click(object sender, RoutedEventArgs e) {
        var tab = new LayoutTab { Name = $"佈局 {_tabs.Count + 1}" };
        AddTab(tab);
        TabAdded?.Invoke(this, tab);
    }

    private ContextMenu BuildTabContextMenu(TabItemData data) {
        var menu = new ContextMenu();
        Application.Current.TryFindResource("SurfaceBrush");

        var renameItem = new MenuItem { Header = "重新命名" };
        renameItem.Icon = MakeIcon("IconBookmark");
        renameItem.Click += (_, _) => BeginRename(data);
        menu.Items.Add(renameItem);

        var dupItem = new MenuItem { Header = "複製" };
        dupItem.Icon = MakeIcon("IconSave");
        dupItem.Click += (_, _) => DuplicateTab(data);
        menu.Items.Add(dupItem);

        menu.Items.Add(new Separator());

        var closeItem = new MenuItem { Header = "關閉" };
        closeItem.Icon = MakeIcon("IconClose");
        closeItem.Click += (_, _) => CloseTab(data.Layout.Id);
        menu.Items.Add(closeItem);

        var closeOthersItem = new MenuItem { Header = "關閉其他分頁" };
        closeOthersItem.Icon = MakeIcon("IconClose");
        closeOthersItem.Click += (_, _) => CloseOtherTabs(data.Layout.Id);
        menu.Items.Add(closeOthersItem);

        return menu;
    }

    private static System.Windows.Shapes.Path MakeIcon(string key) {
        var icon = new System.Windows.Shapes.Path {
            Data = Application.Current.TryFindResource(key) as Geometry ?? Geometry.Parse("M0,0"),
            Fill = (Brush)Application.Current.FindResource("TextBrush"),
            Width = 14, Height = 14, Stretch = System.Windows.Media.Stretch.Uniform
        };
        return icon;
    }

    private void DuplicateTab(TabItemData data) {
        var tab = new LayoutTab {
            Name = $"{data.Layout.Name} (複製)",
        };
        AddTab(tab);
        TabAdded?.Invoke(this, tab);
    }

    private void CloseOtherTabs(string id) {
        var keep = _tabs.FirstOrDefault(t => t.Layout.Id == id);
        if (keep is null) return;
        _tabs.Clear();
        TabStrip.Children.Clear();
        _tabs.Add(keep);
        TabStrip.Children.Add(keep.UI);
        SelectTab(keep.Layout.Id);
    }

    private static T GetResource<T>(string key) where T : class {
        try { return (T)Application.Current.FindResource(key); }
        catch { return null!; }
    }

    private class TabItemData {
        public LayoutTab Layout { get; set; } = new();
        public Border UI { get; set; } = new();
        public bool Dirty { get; set; }
    }
}
