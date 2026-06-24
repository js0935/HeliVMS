using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HeliVMS.Models;

namespace HeliVMS.Controls;

/// <summary>
/// Dynamic multi‑cell video grid that accepts camera drops from an external
/// CameraTreePanel and supports internal drag‑swap + double‑click maximize.
///
/// Usage (parent page):
///   grid.DropCameraRequested += (cameraId, slotIndex) => {
///       var camera = _cameraService.GetById(cameraId);
///       if (camera is not null) grid.AssignSlot(slotIndex, camera);
///   };
///   grid.SetSlotCount(16); // initial layout
/// </summary>
public partial class DynamicCameraGrid : UserControl {

    // ═══════════════════════════════════════════════════
    //  Constants & State
    // ═══════════════════════════════════════════════════
    public const int MaxSlots = 64;

    private readonly VideoPlayer?[] _slots = new VideoPlayer?[MaxSlots];
    private readonly Camera?[] _slotCameras = new Camera?[MaxSlots];
    private int _rows = 1, _cols = 1;
    private int _activeSlotCount;
    private int _maximizedSlot = -1;

    // ─── External drag-drop (from TreeView) ───
    private int _dragOverSlotIndex = -1;

    // ─── Internal drag-swap ───
    private VideoPlayer? _dragSourcePlayer;
    private Point _dragStartPoint;
    private bool _isDragging;

    // ═══════════════════════════════════════════════════
    //  Events
    // ═══════════════════════════════════════════════════

    /// <summary>Raised when an external drag-drop requests a camera placed in a slot.</summary>
    public event Action<string, int>? DropCameraRequested;

    /// <summary>Raised when a camera is assigned to / removed from a slot.</summary>
    public event Action<Camera?, int>? SlotChanged;

    /// <summary>Raised when user right-clicks a slot with a camera.</summary>
    public event Action<string, string>? SlotContextMenuRequested;

    // ═══════════════════════════════════════════════════
    //  Constructor
    // ═══════════════════════════════════════════════════
    public DynamicCameraGrid() {
        InitializeComponent();

        MainGrid.DragEnter += OnDragEnter;
        MainGrid.DragOver  += OnDragOver;
        MainGrid.DragLeave += OnDragLeave;
        MainGrid.Drop      += OnDrop;

        MainGrid.MouseLeftButtonDown += OnMouseLeftButtonDown;
        MainGrid.MouseMove           += OnMouseMove;
        MainGrid.MouseLeftButtonUp   += OnMouseLeftButtonUp;
        this.AddHandler(Control.MouseDoubleClickEvent,
            new MouseButtonEventHandler(OnMouseDoubleClick), handledEventsToo: true);
        this.LostMouseCapture += (_, _) => CancelDrag();
        Unloaded += (_, _) => ClearAll();
    }

    // ═══════════════════════════════════════════════════
    //  Public API
    // ═══════════════════════════════════════════════════

    /// <summary>Set the number of logical slots and rebuild the grid layout.</summary>
    public void SetSlotCount(int count) {
        count = Math.Clamp(count, 1, MaxSlots);
        _activeSlotCount = count;
        _maximizedSlot = -1;
        (_rows, _cols) = CalculateGrid(count);
        RebuildGrid();
        EmptyOverlay.Visibility = Visibility.Collapsed;
    }

    /// <summary>Assign a camera to the given slot (replaces any existing player there).</summary>
    public void AssignSlot(int slotIndex, Camera camera) {
        if (slotIndex < 0 || slotIndex >= _activeSlotCount) return;
        if (_slots[slotIndex]?.Camera?.Id == camera.Id) return;

        RemoveSlot(slotIndex);

        var player = CreatePlayer(camera);
        _slots[slotIndex] = player;
        _slotCameras[slotIndex] = camera;
        PlacePlayerInCell(player, slotIndex);
        SlotChanged?.Invoke(camera, slotIndex);
    }

    /// <summary>Remove whatever is in the given slot (player + placeholder).</summary>
    public void RemoveSlot(int slotIndex) {
        if (slotIndex < 0 || slotIndex >= _activeSlotCount) return;
        RemoveContainerAt(slotIndex);
        if (_slots[slotIndex] is { } player) {
            player.UnloadCamera();
            _slots[slotIndex] = null;
            _slotCameras[slotIndex] = null;
        }
        // Replace with placeholder
        var ph = CreatePlaceholder(slotIndex);
        Grid.SetRow(ph, slotIndex / _cols);
        Grid.SetColumn(ph, slotIndex % _cols);
        MainGrid.Children.Add(ph);
    }

    private void RemoveContainerAt(int slotIndex) {
        for (int i = MainGrid.Children.Count - 1; i >= 0; i--) {
            if (MainGrid.Children[i] is Grid container
                && Grid.GetRow(container) == slotIndex / _cols
                && Grid.GetColumn(container) == slotIndex % _cols) {
                MainGrid.Children.RemoveAt(i);
                return;
            }
        }
    }

    /// <summary>Get the camera at a slot, or null if empty.</summary>
    public Camera? GetCameraAt(int slotIndex)
        => (uint)slotIndex < _activeSlotCount ? _slotCameras[slotIndex] : null;

    /// <summary>Expose internal slot cameras for close-button lookup.</summary>
    public Camera?[] GetSlotCameras() => _slotCameras;

    /// <summary>Return all non-null VideoPlayers currently active in the grid.</summary>
    public List<VideoPlayer> GetActiveSlots() {
        var result = new List<VideoPlayer>(_activeSlotCount);
        for (int i = 0; i < _activeSlotCount; i++) {
            if (_slots[i] is { } p) result.Add(p);
        }
        return result;
    }

    /// <summary>Clear all slots.</summary>
    public void ClearAll() {
        for (int i = 0; i < _activeSlotCount; i++) {
            if (_slots[i] is { } p) {
                p.UnloadCamera();
                _slots[i] = null;
                _slotCameras[i] = null;
            }
        }
        _selectionBorder = null;
        _selectedSlot = -1;
        MainGrid.Children.Clear();
        _maximizedSlot = -1;
        EmptyOverlay.Visibility = _activeSlotCount == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Load a full set of cameras into the grid (replaces existing).</summary>
    public void LoadCameras(IList<Camera?> cameras, bool useSubStream = false) {
        ClearAll();
        var count = Math.Min(cameras.Count, MaxSlots);
        if (count == 0) { EmptyOverlay.Visibility = Visibility.Visible; return; }

        // Auto-fit grid size to camera count
        SetSlotCount(count);

        for (int i = 0; i < count; i++) {
            var cam = cameras[i];
            if (cam is not null && cam.IsEnabled && cam.IsVisible)
                AssignSlot(i, cam);
        }
    }

    // ═══════════════════════════════════════════════════
    //  Layout Helpers
    // ═══════════════════════════════════════════════════

    private static (int rows, int cols) CalculateGrid(int n) => n switch {
        1  => (1, 1),  2  => (1, 2),  3  => (2, 2),  4  => (2, 2),
        5  => (2, 3),  6  => (2, 3),  7  => (3, 3),  8  => (3, 3),
        9  => (3, 3),  10 => (3, 4),  11 => (3, 4),  12 => (3, 4),
        13 => (4, 4),  14 => (4, 4),  15 => (4, 4),  16 => (4, 4),
        17 => (4, 5),  18 => (4, 5),  19 => (4, 5),  20 => (4, 5),
        21 => (5, 5),  22 => (5, 5),  23 => (5, 5),  24 => (5, 5),
        25 => (5, 5),  26 => (5, 6),  27 => (5, 6),  30 => (5, 6),
        36 => (6, 6),  42 => (6, 7),  49 => (7, 7),  56 => (7, 8),
        64 => (8, 8),  _  => (4, 4)
    };

    private void RebuildGrid() {
        MainGrid.RowDefinitions.Clear();
        MainGrid.ColumnDefinitions.Clear();
        for (int c = 0; c < _cols; c++)
            MainGrid.ColumnDefinitions.Add(new ColumnDefinition());
        for (int r = 0; r < _rows; r++)
            MainGrid.RowDefinitions.Add(new RowDefinition());

        MainGrid.Children.Clear();
        for (int i = 0; i < _activeSlotCount; i++) {
            if (_slots[i] is { } player)
                PlacePlayerInCell(player, i);
            else
                AddPlaceholder(i);
        }
    }

    private void PlacePlayerInCell(VideoPlayer player, int slotIndex) {
        var cam = _slotCameras[slotIndex];
        var container = new Grid();
        container.MouseDown += (_, args) => {
            if (args.ChangedButton == MouseButton.Left)
                SelectSlot(slotIndex);
        };
        if (cam is not null) {
            var ctx = new ContextMenu();
            var camId = cam.Id;
            ctx.Items.Add(new MenuItem { Header = "移除攝影機", Tag = camId });
            ctx.Items.Add(new MenuItem { Header = "開始錄影", Tag = camId });
            ctx.Items.Add(new MenuItem { Header = "停止錄影", Tag = camId });
            ctx.Items.Add(new Separator());
            ctx.Items.Add(new MenuItem { Header = "PTZ 控制", Tag = camId });
            foreach (MenuItem item in ctx.Items)
                item.Click += (s, _) => {
                    if (s is MenuItem mi)
                        SlotContextMenuRequested?.Invoke(camId, (string)mi.Header);
                };
            container.ContextMenu = ctx;
        }
        Grid.SetRow(container, slotIndex / _cols);
        Grid.SetColumn(container, slotIndex % _cols);
        container.Children.Add(player);
        if (cam is not null) {
            var label = new TextBlock {
                Text = cam.Name,
                FontSize = _cols >= 5 ? 9 : 11,
                Foreground = new SolidColorBrush(Color.FromArgb(200, 0xFF, 0xFF, 0xFF)),
                Background = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)),
                Padding = new Thickness(4, 1, 4, 1),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
                IsHitTestVisible = false,
            };
            container.Children.Add(label);
            if (cam.HasPTZ && _cols < 6) {
                var ptzBadge = new Border {
                    Background = new SolidColorBrush(Color.FromArgb(180, 0x21, 0x96, 0xF3)),
                    CornerRadius = new CornerRadius(2),
                    Padding = new Thickness(3, 0, 3, 0),
                    Margin = new Thickness(4, 2, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top,
                    Child = new TextBlock {
                        Text = "PTZ",
                        FontSize = 8,
                        Foreground = Brushes.White,
                    }
                };
                container.Children.Add(ptzBadge);
            }
        }
        if (!MainGrid.Children.Contains(container))
            MainGrid.Children.Add(container);
    }

    private int _selectedSlot = -1;
    private Border? _selectionBorder;

    public int SelectedSlot => _selectedSlot;

    public event Action<int>? SlotSelected;

    private void SelectSlot(int slotIndex) {
        if (_selectionBorder is not null) {
            MainGrid.Children.Remove(_selectionBorder);
            _selectionBorder = null;
        }
        _selectedSlot = slotIndex;
        if (slotIndex < 0) return;
        _selectionBorder = new Border {
            BorderBrush = new SolidColorBrush(Color.FromArgb(200, 0x21, 0x96, 0xF3)),
            BorderThickness = new Thickness(2),
            IsHitTestVisible = false,
        };
        Grid.SetRow(_selectionBorder, slotIndex / _cols);
        Grid.SetColumn(_selectionBorder, slotIndex % _cols);
        MainGrid.Children.Add(_selectionBorder);
        SlotSelected?.Invoke(slotIndex);
    }

    // ═══════════════════════════════════════════════════
    //  Placeholders (empty slots)
    // ═══════════════════════════════════════════════════

    private static readonly Brush PlaceholderBg
        = new SolidColorBrush(Color.FromArgb(0x08, 0xFF, 0xFF, 0xFF));
    private static readonly Brush PlaceholderBorder
        = new SolidColorBrush(Color.FromArgb(0x40, 0x88, 0x88, 0x88));
    private static readonly Brush DropHighlightBg
        = new SolidColorBrush(Color.FromArgb(0x30, 0x41, 0x69, 0xE1));

    private Border CreatePlaceholder(int slotIndex) {
        return new Border {
            Tag = slotIndex,
            Background = PlaceholderBg,
            BorderBrush = PlaceholderBorder,
            BorderThickness = new Thickness(1, 1, 0, 0),
            Child = new TextBlock {
                Text = "+",
                FontSize = 28,
                Foreground = new SolidColorBrush(Color.FromArgb(0x60, 0x88, 0x88, 0x88)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "拖曳攝影機至此"
            }
        };
    }

    private void AddPlaceholder(int slotIndex) {
        var ph = CreatePlaceholder(slotIndex);
        Grid.SetRow(ph, slotIndex / _cols);
        Grid.SetColumn(ph, slotIndex % _cols);
        MainGrid.Children.Add(ph);
    }

    private void RemoveAllPlaceholders() {
        for (int i = MainGrid.Children.Count - 1; i >= 0; i--)
            if (MainGrid.Children[i] is Border { Tag: int })
                MainGrid.Children.RemoveAt(i);
    }

    // ═══════════════════════════════════════════════════
    //  External Drag & Drop (from CameraTreePanel)
    // ═══════════════════════════════════════════════════

    private void OnDragEnter(object sender, DragEventArgs e) {
        e.Effects = e.Data.GetDataPresent("CameraId") ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDragOver(object sender, DragEventArgs e) {
        if (!e.Data.GetDataPresent("CameraId")) { e.Effects = DragDropEffects.None; return; }
        e.Effects = DragDropEffects.Copy;

        int idx = HitTestSlot(e.GetPosition(MainGrid));
        if (idx != _dragOverSlotIndex) {
            ClearDragHighlight();
            _dragOverSlotIndex = idx;
            if (idx >= 0) HighlightSlot(idx);
        }
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e) {
        ClearDragHighlight();
        _dragOverSlotIndex = -1;
    }

    private void OnDrop(object sender, DragEventArgs e) {
        ClearDragHighlight();
        _dragOverSlotIndex = -1;

        if (!e.Data.GetDataPresent("CameraId")) return;
        var cameraId = e.Data.GetData("CameraId") as string;
        if (cameraId is null) return;

        int slotIndex = HitTestSlot(e.GetPosition(MainGrid));
        if (slotIndex >= 0 && _maximizedSlot < 0)
            DropCameraRequested?.Invoke(cameraId, slotIndex);
        e.Handled = true;
    }

    private int HitTestSlot(Point pos) {
        if (_maximizedSlot >= 0) return -1;
        double cw = MainGrid.ActualWidth / _cols;
        double ch = MainGrid.ActualHeight / _rows;
        int c = (int)(pos.X / cw);
        int r = (int)(pos.Y / ch);
        if ((uint)c >= _cols || (uint)r >= _rows) return -1;
        int idx = r * _cols + c;
        return idx < _activeSlotCount ? idx : -1;
    }

    private void HighlightSlot(int idx) {
        foreach (var child in MainGrid.Children)
            if (child is Border { Tag: int tag } border && tag == idx)
                border.Background = DropHighlightBg;
    }

    private void ClearDragHighlight() {
        foreach (var child in MainGrid.Children)
            if (child is Border { Tag: int } border)
                border.Background = PlaceholderBg;
    }

    // ═══════════════════════════════════════════════════
    //  Internal Drag‑Swap
    // ═══════════════════════════════════════════════════

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e) {
        if (e.ClickCount > 1) return;
        var player = FindPlayerAt(e.GetPosition(MainGrid));
        if (player?.Camera is null) return;
        _dragSourcePlayer = player;
        _dragStartPoint = e.GetPosition(MainGrid);
        _isDragging = false;
        Mouse.Capture(this);
    }

    private void OnMouseMove(object sender, MouseEventArgs e) {
        if (_dragSourcePlayer is null) return;
        var pos = e.GetPosition(MainGrid);

        if (e.LeftButton != MouseButtonState.Pressed) {
            CancelDrag();
            return;
        }

        if (!_isDragging) {
            var dx = pos.X - _dragStartPoint.X;
            var dy = pos.Y - _dragStartPoint.Y;
            var thresh = SystemParameters.MinimumHorizontalDragDistance;
            if (Math.Abs(dx) < thresh && Math.Abs(dy) < thresh) return;
            _isDragging = true;
        }
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e) {
        if (_isDragging && _dragSourcePlayer is not null)
            PerformSwap(e.GetPosition(MainGrid));
        CancelDrag();
    }

    private void PerformSwap(Point dropPos) {
        var target = FindPlayerAt(dropPos);
        if (target is null || target == _dragSourcePlayer || target.Camera is null) return;
        var srcId = _dragSourcePlayer!.Camera?.Id;
        if (srcId is null) return;

        int srcIdx = IndexOfId(_slotCameras, srcId);
        int tgtIdx = IndexOfId(_slotCameras, target.Camera.Id);
        if (srcIdx < 0 || tgtIdx < 0) return;

        // Swap in the arrays
        (_slots[srcIdx], _slots[tgtIdx]) = (_slots[tgtIdx], _slots[srcIdx]);
        (_slotCameras[srcIdx], _slotCameras[tgtIdx]) = (_slotCameras[tgtIdx], _slotCameras[srcIdx]);

        // Reposition in Grid
        PlacePlayerInCell(_slots[srcIdx]!, srcIdx);
        PlacePlayerInCell(_slots[tgtIdx]!, tgtIdx);
    }

    private void CancelDrag() {
        _dragSourcePlayer = null;
        _isDragging = false;
        if (Mouse.Captured == this) Mouse.Capture(null);
    }

    private static int IndexOfId(Camera?[] arr, string id) {
        for (int i = 0; i < arr.Length; i++)
            if (arr[i]?.Id == id) return i;
        return -1;
    }

    private VideoPlayer? FindPlayerAt(Point position) {
        var hit = MainGrid.InputHitTest(position) as DependencyObject;
        while (hit is not null && hit is not VideoPlayer)
            hit = VisualTreeHelper.GetParent(hit);
        return hit as VideoPlayer;
    }

    // ═══════════════════════════════════════════════════
    //  Double‑Click Maximize / Restore
    // ═══════════════════════════════════════════════════

    private void OnMouseDoubleClick(object sender, MouseButtonEventArgs e) {
        int idx = HitTestSlot(e.GetPosition(MainGrid));
        if (idx < 0 || _slots[idx] is null) return;

        if (_maximizedSlot == idx)
            RestoreLayout();
        else if (_maximizedSlot < 0)
            MaximizeSlot(idx);
    }

    private void MaximizeSlot(int idx) {
        _maximizedSlot = idx;
        var target = _slots[idx]!;

        foreach (var child in MainGrid.Children) {
            if (child is VideoPlayer vp) {
                if (vp == target) {
                    Grid.SetRow(vp, 0);
                    Grid.SetColumn(vp, 0);
                    Grid.SetRowSpan(vp, _rows);
                    Grid.SetColumnSpan(vp, _cols);
                    vp.IsMaximized = true;
                } else {
                    vp.Visibility = Visibility.Collapsed;
                    vp.SuspendVideo();
                }
            } else if (child is Border border) {
                border.Visibility = Visibility.Collapsed;
            }
        }

        if (_slotCameras[idx] is not null)
            target.SwitchToMainStream();
    }

    private void RestoreLayout() {
        if (_maximizedSlot < 0) return;
        _maximizedSlot = -1;

        for (int i = 0; i < _activeSlotCount; i++) {
            var player = _slots[i];
            if (player is not null) {
                Grid.SetRowSpan(player, 1);
                Grid.SetColumnSpan(player, 1);
                PlacePlayerInCell(player, i);
                player.Visibility = Visibility.Visible;
                player.IsMaximized = false;
                player.ResumeVideo();
            }
        }

        // Re-show placeholders
        RemoveAllPlaceholders();
        for (int i = 0; i < _activeSlotCount; i++)
            if (_slots[i] is null) AddPlaceholder(i);
    }

    // ═══════════════════════════════════════════════════
    //  Player Factory
    // ═══════════════════════════════════════════════════

    private VideoPlayer CreatePlayer(Camera camera) {
        var player = new VideoPlayer();
        player.SetFullBleed();
        player.LoadCamera(camera, useSubStream: _activeSlotCount > 1);
        player.Selected += OnPlayerSelected;
        player.MaximizeRequested += cam => {
            if (cam is null) return;
            int idx = Array.IndexOf(_slotCameras, cam);
            if (idx >= 0)
                MaximizeSlot(idx);
        };
        return player;
    }

    private void OnPlayerSelected(VideoPlayer selected) {
        for (int i = 0; i < _activeSlotCount; i++) {
            if (_slots[i] is { } p && p != selected)
                p.IsSelected = false;
        }
    }
}
