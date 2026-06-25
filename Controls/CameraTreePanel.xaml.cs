using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HeliVMS.Models;
using HeliVMS.Services;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace HeliVMS.Controls;

// ═══════════════════════════════════════════════════════
//  Tree Item Models
// ═══════════════════════════════════════════════════════

/// <summary>Base node in the camera tree.</summary>
public abstract class CameraTreeNode : INotifyPropertyChanged {
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Notify(string prop) => PropertyChanged?.Invoke(this, new(prop));

    string _displayName = "";
    public string DisplayName { get => _displayName; set { _displayName = value; Notify(nameof(DisplayName)); } }
    public string RtspHint { get; set; } = "";
    public string CameraId { get; set; } = "";
    public Geometry IconGeometry { get; set; } = Geometry.Parse("M12,2 C6.48,2 2,6.48 2,12 C2,17.52 6.48,22 12,22 C17.52,22 22,17.52 22,12 C22,6.48 17.52,2 12,2 Z");

    /// <summary>Child nodes (group nodes only).</summary>
    public ObservableCollection<CameraTreeNode> Children { get; set; } = [];
    public bool IsGroup => Children.Count > 0;
}

/// <summary>Group header node.</summary>
public sealed class CameraTreeGroup : CameraTreeNode { }

/// <summary>Leaf camera node.</summary>
public sealed class CameraTreeItem : CameraTreeNode {
    bool _isConnected;
    public bool IsConnected {
        get => _isConnected;
        set { _isConnected = value; Notify(nameof(IsConnected)); Notify(nameof(ConnectionColor)); }
    }
    public string Tooltip { get; set; } = "";
    public Brush ConnectionColor => IsConnected
        ? (Application.Current?.TryFindResource("SuccessBrush") as Brush) ?? Brushes.LimeGreen
        : (Application.Current?.TryFindResource("ErrorBrush") as Brush) ?? Brushes.Red;
}

// ═══════════════════════════════════════════════════════
//  CameraTreePanel Control
// ═══════════════════════════════════════════════════════

/// <summary>
/// Collapsible camera tree with search, group-by-group display,
/// and drag-source support for the DynamicCameraGrid.
/// </summary>
public partial class CameraTreePanel : UserControl {
    private readonly ICameraService _cameraService;
    private readonly string _statePath;
    private readonly HashSet<string> _expandedGroups = [];
    private bool _isCollapsed;
    private Point _dragStartPoint;

    /// <summary>Raised when the panel collapse state changes.</summary>
    public event Action<bool>? CollapseChanged;

    /// <summary>Raised when a camera action is requested from the context menu.</summary>
    public event Action<string, string>? CameraAction; // (cameraId, action)

    public CameraTreePanel() {
        InitializeComponent();
        _cameraService = App.Services.GetRequiredService<ICameraService>();
        _statePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "tree_state.json");
        _cameraService.CamerasChanged += () => Dispatcher.Invoke(ReloadCameras);
        Loaded += (_, _) => { LoadExpandedState(); ReloadCameras(); };

        CameraTreeView.SelectedItemChanged += (_, e) => {
            if (e.NewValue is CameraTreeItem item)
                ToolTip = $"{item.DisplayName}\n{item.RtspHint}";
        };
        CameraTreeView.AddHandler(TreeViewItem.ExpandedEvent, new RoutedEventHandler((_, e) => {
            if (e.OriginalSource is TreeViewItem tvi && tvi.DataContext is CameraTreeGroup g)
                SaveGroupState(g.DisplayName, true);
        }));
        CameraTreeView.AddHandler(TreeViewItem.CollapsedEvent, new RoutedEventHandler((_, e) => {
            if (e.OriginalSource is TreeViewItem tvi && tvi.DataContext is CameraTreeGroup g)
                SaveGroupState(g.DisplayName, false);
        }));
    }

    // ─── Public API ───

    /// <summary>Switch resource panel to a specific tab index (0=Resources, 1=Layouts, 2=Bookmarks).</summary>
    public void SwitchTab(int tabIndex) {
        CameraTreeView.Visibility = tabIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        LayoutsPanel.Visibility = tabIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        BookmarksPanel.Visibility = tabIndex == 2 ? Visibility.Visible : Visibility.Collapsed;

        var accent = TryFindResource("PrimaryBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.DodgerBlue;
        var text = TryFindResource("TextBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.White;
        var dim = TryFindResource("SecondaryTextBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Gray;

        TabResources.BorderThickness = tabIndex == 0 ? new Thickness(0, 0, 0, 2) : new Thickness(0);
        TabResources.BorderBrush = tabIndex == 0 ? accent : System.Windows.Media.Brushes.Transparent;
        TabResources.Foreground = tabIndex == 0 ? text : dim;

        TabLayouts.BorderThickness = tabIndex == 1 ? new Thickness(0, 0, 0, 2) : new Thickness(0);
        TabLayouts.BorderBrush = tabIndex == 1 ? accent : System.Windows.Media.Brushes.Transparent;
        TabLayouts.Foreground = tabIndex == 1 ? text : dim;

        TabBookmarks.BorderThickness = tabIndex == 2 ? new Thickness(0, 0, 0, 2) : new Thickness(0);
        TabBookmarks.BorderBrush = tabIndex == 2 ? accent : System.Windows.Media.Brushes.Transparent;
        TabBookmarks.Foreground = tabIndex == 2 ? text : dim;

        SearchBox.Visibility = tabIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void TabResources_Click(object sender, RoutedEventArgs e) => SwitchTab(0);
    private void TabLayouts_Click(object sender, RoutedEventArgs e) => SwitchTab(1);
    private void TabBookmarks_Click(object sender, RoutedEventArgs e) => SwitchTab(2);

    /// <summary>Reload camera list from the service and rebuild the tree.</summary>
    public void ReloadCameras() {
        var allCameras = _cameraService.GetAllCameras();
        var filter = SearchBox.Text?.Trim() ?? "";
        var roots = BuildTree(allCameras, filter);
        CameraTreeView.ItemsSource = roots;
        Dispatcher.BeginInvoke(new Action(RestoreExpandedState), System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>Focus the search text box.</summary>
    public void FocusSearch() {
        SearchBox?.Focus();
        SearchBox?.SelectAll();
    }

    /// <summary>Collapse or expand the panel.</summary>
    public bool IsCollapsed {
        get => _isCollapsed;
        set {
            _isCollapsed = value;
            CollapseChanged?.Invoke(value);
        }
    }

    // ─── Group State Persistence ───

    private void LoadExpandedState() {
        try {
            if (File.Exists(_statePath)) {
                var data = JsonSerializer.Deserialize<string[]>(File.ReadAllText(_statePath));
                if (data is not null) { _expandedGroups.Clear(); foreach (var g in data) _expandedGroups.Add(g); }
            }
        } catch { _expandedGroups.Clear(); }
    }

    private void SaveExpandedState() {
        try {
            var dir = Path.GetDirectoryName(_statePath);
            if (dir is not null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_statePath, JsonSerializer.Serialize(_expandedGroups.ToArray()));
        } catch { }
    }

    private void SaveGroupState(string name, bool expanded) {
        if (expanded) _expandedGroups.Add(name); else _expandedGroups.Remove(name);
        SaveExpandedState();
    }

    private void RestoreExpandedState() {
        foreach (var item in CameraTreeView.Items) {
            if (item is CameraTreeGroup group && _expandedGroups.Contains(group.DisplayName)) {
                if (CameraTreeView.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem tvi)
                    tvi.IsExpanded = true;
            }
        }
    }

    // ─── Tree Building ───

    private List<CameraTreeNode> BuildTree(IList<Camera> cameras, string filter) {
        var groups = new Dictionary<string, CameraTreeGroup>(StringComparer.OrdinalIgnoreCase);

        foreach (var cam in cameras) {
            if (!cam.IsEnabled) continue;
            if (!string.IsNullOrEmpty(filter) &&
                (cam.Name is null || !cam.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))) continue;

            var groupName = !string.IsNullOrWhiteSpace(cam.Group) ? cam.Group : "未分組";
            if (!groups.TryGetValue(groupName, out var group)) {
                group = new CameraTreeGroup {
                    DisplayName = groupName,
                    IconGeometry = Geometry.Parse("M2,6 L2,18 C2,19.1 2.9,20 4,20 L20,20 C21.1,20 22,19.1 22,18 L22,8 C22,6.9 21.1,6 20,6 L12,6 L10,4 L4,4 C2.9,4 2,4.9 2,6 Z")
                };
                groups[groupName] = group;
            }

            group.Children.Add(new CameraTreeItem {
                DisplayName = cam.Name ?? cam.Id,
                RtspHint = cam.RtspUrl,
                CameraId = cam.Id,
                IconGeometry = cam.IsFavorite
                    ? Geometry.Parse("M12,17.27 L18.18,21 L16.54,13.97 L22,9.24 L14.81,8.63 L12,2 L9.19,8.63 L2,9.24 L7.46,13.97 L5.82,21 Z")
                    : Geometry.Parse("M3,7 L11,7 L13,5 L15,7 L21,7 L21,19 L3,19 Z M8,13 A4,4 0 1,1 16,13 A4,4 0 1,1 8,13"),
                IsConnected = cam.IsConnected,
                Tooltip = $"ID: {cam.Id}\nIP: {cam.IpAddress}\nRTSP: {cam.RtspUrl}\n狀態: {(cam.IsConnected ? "在線" : "離線")}\n群組: {cam.Group ?? "未分組"}"
            });
        }

        var roots = new List<CameraTreeNode>(groups.Values);

        // Prepend favorites group if any cameras favorited
        var favorites = cameras.Where(c => c.IsEnabled && c.IsFavorite).ToList();
        if (favorites.Count > 0) {
            if (!string.IsNullOrEmpty(filter))
                favorites = favorites.Where(c =>
                    c.Name is not null && c.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
            if (favorites.Count > 0) {
                var favGroup = new CameraTreeGroup {
                    DisplayName = "我的最愛",
                    IconGeometry = Geometry.Parse("M12,17.27 L18.18,21 L16.54,13.97 L22,9.24 L14.81,8.63 L12,2 L9.19,8.63 L2,9.24 L7.46,13.97 L5.82,21 Z")
                };
                foreach (var cam in favorites) {
                    favGroup.Children.Add(new CameraTreeItem {
                        DisplayName = cam.Name ?? cam.Id,
                        RtspHint = cam.RtspUrl,
                        CameraId = cam.Id,
                        IconGeometry = Geometry.Parse("M12,17.27 L18.18,21 L16.54,13.97 L22,9.24 L14.81,8.63 L12,2 L9.19,8.63 L2,9.24 L7.46,13.97 L5.82,21 Z"),
                        IsConnected = cam.IsConnected,
                        Tooltip = $"ID: {cam.Id}\nIP: {cam.IpAddress}\nRTSP: {cam.RtspUrl}\n狀態: {(cam.IsConnected ? "在線" : "離線")}\n群組: {cam.Group ?? "未分組"}"
                    });
                }
                roots.Insert(0, favGroup);
            }
        }

        // If only one group, flatten it
        if (roots.Count == 1 && roots[0] is CameraTreeGroup single) {
            if (single.DisplayName == "未分組")
                return [.. single.Children];
        }

        return roots;
    }

    private void FireCameraAction(string? cameraId, string action) {
        if (cameraId is not null) CameraAction?.Invoke(cameraId, action);
    }

    private void OnPlayCamera(object sender, RoutedEventArgs e) {
        if (sender is MenuItem mi) FireCameraAction(mi.Tag as string, "play");
    }

    private void OnOpenNewTab(object sender, RoutedEventArgs e) {
        if (sender is MenuItem mi) FireCameraAction(mi.Tag as string, "open_new_tab");
    }

    private void OnToggleRecording(object sender, RoutedEventArgs e) {
        if (sender is MenuItem mi && mi.Tag is string id) {
            var recording = App.Services.GetRequiredService<IRecordingService>().IsRecording(id);
            FireCameraAction(id, recording ? "stop_recording" : "start_recording");
        }
    }

    private void OnOpenPtz(object sender, RoutedEventArgs e) {
        if (sender is MenuItem mi) FireCameraAction(mi.Tag as string, "ptz");
    }

    private void OnToggleFavorite(object sender, RoutedEventArgs e) {
        if (sender is MenuItem mi && mi.Tag is string cameraId) {
            var cam = _cameraService.GetAllCameras().FirstOrDefault(c => c.Id == cameraId);
            if (cam is not null) {
                cam.IsFavorite = !cam.IsFavorite;
                _cameraService.UpdateCamera(cam);
                ReloadCameras();
            }
        }
    }

    private void OnSnapshot(object sender, RoutedEventArgs e) {
        if (sender is MenuItem mi) FireCameraAction(mi.Tag as string, "snapshot");
    }

    private void OnCameraSettings(object sender, RoutedEventArgs e) {
        if (sender is MenuItem mi) FireCameraAction(mi.Tag as string, "camera_settings");
    }

    private void OnRemoveCamera(object sender, RoutedEventArgs e) {
        if (sender is MenuItem mi && mi.Tag is string cameraId) {
            var cam = _cameraService.GetAllCameras().FirstOrDefault(c => c.Id == cameraId);
            if (cam is not null) {
                var result = MessageBox.Show($"確定要移除攝影機「{cam.Name}」？\n此操作無法復原。",
                    "移除攝影機", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result == MessageBoxResult.Yes) {
                    _cameraService.DeleteCamera(cameraId);
                    ReloadCameras();
                }
            }
        }
    }

    // ─── Search ───

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e) {
        SearchClearBtn.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Collapsed : Visibility.Visible;
        ReloadCameras();
    }

    private void SearchToggle_Click(object sender, RoutedEventArgs e) {
        SearchBar.Visibility = SearchBar.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        if (SearchBar.Visibility == Visibility.Visible)
            SearchBox.Focus();
    }

    private void SearchClear_Click(object sender, RoutedEventArgs e) {
        SearchBox.Text = "";
        SearchBar.Visibility = Visibility.Collapsed;
    }

    // ─── Drag Source ───

    private void OnTreePreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) {
        if (e.OriginalSource is DependencyObject dep) {
            var treeItem = FindParentTreeItem(dep);
            if (treeItem?.DataContext is CameraTreeItem { CameraId: { } id }) {
                _dragStartPoint = e.GetPosition(this);
                CameraTreeView.Tag = id;
            }
        }
    }

    private void OnTreePreviewMouseMove(object sender, MouseEventArgs e) {

        if (CameraTreeView.Tag is not string cameraId) return;

        var pos = e.GetPosition(this);
        var dx = pos.X - _dragStartPoint.X;
        var dy = pos.Y - _dragStartPoint.Y;
        var thresh = SystemParameters.MinimumHorizontalDragDistance;

        if (Math.Abs(dx) < thresh && Math.Abs(dy) < thresh) return;

        CameraTreeView.Tag = null; // clear so we don't re-enter

        var data = new DataObject("CameraId", cameraId);
        DragDrop.DoDragDrop(CameraTreeView, data, DragDropEffects.Copy);
    }

    private static TreeViewItem? FindParentTreeItem(DependencyObject? source) {
        while (source is not null && source is not TreeViewItem)
            source = VisualTreeHelper.GetParent(source);
        return source as TreeViewItem;
    }
}
