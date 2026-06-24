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
    public string Icon { get; set; } = "";
    public string RtspHint { get; set; } = "";
    public string CameraId { get; set; } = "";

    /// <summary>Child nodes (group nodes only).</summary>
    public ObservableCollection<CameraTreeNode> Children { get; set; } = [];
    public bool IsGroup => Children.Count > 0;
}

/// <summary>Group header node.</summary>
public sealed class CameraTreeGroup : CameraTreeNode { }

/// <summary>Leaf camera node.</summary>
public sealed class CameraTreeItem : CameraTreeNode {
    public bool IsConnected { get; set; }
    public string Tooltip { get; set; } = "";
    public System.Windows.Media.Brush ConnectionColor =>
        IsConnected ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(76, 175, 80))
                    : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(244, 67, 54));
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
                    Icon = "📁"
                };
                groups[groupName] = group;
            }

            group.Children.Add(new CameraTreeItem {
                DisplayName = cam.Name ?? cam.Id,
                RtspHint = cam.RtspUrl,
                CameraId = cam.Id,
                Icon = cam.IsFavorite ? "⭐" : "📷",
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
                    DisplayName = "⭐ 我的最愛",
                    Icon = "⭐"
                };
                foreach (var cam in favorites) {
                    favGroup.Children.Add(new CameraTreeItem {
                        DisplayName = cam.Name ?? cam.Id,
                        RtspHint = cam.RtspUrl,
                        CameraId = cam.Id,
                        Icon = "⭐",
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

    // ─── Search ───

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e) {
        ReloadCameras();
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

    private void OnTreeMouseMove(object sender, MouseEventArgs e) {
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
