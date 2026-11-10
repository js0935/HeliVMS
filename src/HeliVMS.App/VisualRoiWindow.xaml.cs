using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using HeliVMS.Shared;

namespace HeliVMS.App;

/// <summary>
/// 框選區域選擇（QC_Live 的 <c>SetRegion</c> 對齊）：在樣本圖上拖出一個矩形，
/// 交給以圖搜索只比對框內區域。
/// </summary>
/// <remarks>
/// <para>
/// 為什麼要一個獨立視窗：框選必須在同一個顯示比例下操作（操作員用眼睛對齊畫面裡的門、車道），
/// 而事件中心是清單頁、沒有可拖曳的影像區。獨立視窗也讓「框選」與「選檔」兩個動作分開，
/// 不會因為換了樣本圖就沿用上一個框（框還畫在舊圖的座標上，看起來像框錯了地方）。
/// </para>
/// <para>
/// 影像用 <c>Stretch="Uniform"</c> 置中（有黑邊），所以畫面上的點必須先換算回<b>影像像素</b>
/// 才能交給 <see cref="VisualHash.FromPixels"/>；直接把顯示座標當像素用，在非原生解析度
/// 或非等比例視窗下會框到完全不同的一塊。
/// </para>
/// </remarks>
public partial class VisualRoiWindow : Window
{
    private readonly int _imageWidth;
    private readonly int _imageHeight;
    private VisualHash.Roi? _roi;
    private Point? _dragStart;
    private Point? _dragCurrent;

    /// <summary>操作員按下「套用」後的框（null＝清除框選，改搜整張圖）。</summary>
    public VisualHash.Roi? Result { get; private set; }

    /// <summary>true＝按了套用／清除；false＝取消或關窗（呼叫端不得變更既有框選）。</summary>
    public bool Applied { get; private set; }

    public VisualRoiWindow(string sampleImagePath, VisualHash.Roi? initialRoi = null)
    {
        ArgumentNullException.ThrowIfNull(sampleImagePath);
        _roi = initialRoi;

        InitializeComponent();
        ApplyI18n();

        try
        {
            var frame = BitmapFrame.Create(
                new Uri(Path.GetFullPath(sampleImagePath)),
                BitmapCreateOptions.None,
                BitmapCacheOption.OnLoad);
            _imageWidth = frame.PixelWidth;
            _imageHeight = frame.PixelHeight;
            SampleImage.Source = frame;
        }
        catch (Exception)
        {
            // 解不開的圖（損毀／不是圖片）在這裡就要說出來；否則操作員拖了半天才發現
            // 搜不到東西，而症狀看起來像「框選搜索沒有用」。
            _imageWidth = 0;
            _imageHeight = 0;
            HintText.Text = Localizer.T("VisualRoi.LoadFailed");
        }

        UpdateRoiInfo();
    }

    private void ApplyI18n()
    {
        Title = Localizer.T("VisualRoi.Title");
        HeaderTitle.Text = Localizer.T("VisualRoi.Title");
        HeaderCloseButton.ToolTip = Localizer.T("VisualRoi.Close");
        HintText.Text = Localizer.T("VisualRoi.Hint");
        ClearButton.Content = Localizer.T("VisualRoi.Clear");
        CancelButton.Content = Localizer.T("VisualRoi.Cancel");
        ApplyButton.Content = Localizer.T("VisualRoi.Apply");
    }

    /// <summary>顯示座標 → 影像像素（含 Uniform 置中的黑邊換算）。</summary>
    private bool TryToImagePoint(Point display, out double x, out double y)
    {
        x = 0;
        y = 0;
        if (!TryRenderedRect(out var scale, out var offsetX, out var offsetY))
        {
            return false;
        }

        x = (display.X - offsetX) / scale;
        y = (display.Y - offsetY) / scale;
        return true;
    }

    private bool TryRenderedRect(out double scale, out double offsetX, out double offsetY)
    {
        scale = 0;
        offsetX = 0;
        offsetY = 0;
        if (_imageWidth <= 0 || _imageHeight <= 0
            || Overlay.ActualWidth <= 0 || Overlay.ActualHeight <= 0)
        {
            return false;
        }

        scale = Math.Min(Overlay.ActualWidth / _imageWidth, Overlay.ActualHeight / _imageHeight);
        if (scale <= 0)
        {
            return false;
        }

        offsetX = (Overlay.ActualWidth - (_imageWidth * scale)) / 2;
        offsetY = (Overlay.ActualHeight - (_imageHeight * scale)) / 2;
        return true;
    }

    private void OnOverlayMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_imageWidth <= 0 || _imageHeight <= 0 || !TryToImagePoint(e.GetPosition(Overlay), out var x, out var y))
        {
            return;
        }

        _dragStart = new Point(x, y);
        _dragCurrent = _dragStart;
        Overlay.CaptureMouse();
        RenderSelection();
    }

    private void OnOverlayMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is null || !TryToImagePoint(e.GetPosition(Overlay), out var x, out var y))
        {
            return;
        }

        _dragCurrent = new Point(x, y);
        RenderSelection();
    }

    private void OnOverlayMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragStart is not { } start)
        {
            return;
        }

        Overlay.ReleaseMouseCapture();
        if (!TryToImagePoint(e.GetPosition(Overlay), out var x, out var y))
        {
            y = start.Y;
            x = start.X;
        }

        // 兩個角落都保留：拖曳方向任意，換算與「太小」的判斷交給共用層，
        // 讓框選的定義與 VisualHash 的裁切口徑永遠是同一份。
        var roi = VisualHash.FromPixels(
            start.X, start.Y, x - start.X, y - start.Y, _imageWidth, _imageHeight);
        _roi = roi;
        _dragStart = null;
        _dragCurrent = null;

        if (roi is null)
        {
            HintText.Text = Localizer.T("VisualRoi.TooSmall");
        }

        RenderSelection();
        UpdateRoiInfo();
    }

    private void OnOverlaySizeChanged(object sender, SizeChangedEventArgs e) => RenderSelection();

    /// <summary>把目前的框（或拖曳中的框）畫到畫面上。</summary>
    private void RenderSelection()
    {
        var region = _dragStart is { } start && _dragCurrent is { } end
            ? VisualHash.FromPixels(start.X, start.Y, end.X - start.X, end.Y - start.Y, _imageWidth, _imageHeight)
            : _roi;

        if (region is null || !TryRenderedRect(out var scale, out var offsetX, out var offsetY))
        {
            Selection.Visibility = Visibility.Collapsed;
            return;
        }

        var (px, py, pw, ph) = VisualHash.PixelRect(region, _imageWidth, _imageHeight);
        Canvas.SetLeft(Selection, offsetX + (px * scale));
        Canvas.SetTop(Selection, offsetY + (py * scale));
        Selection.Width = pw * scale;
        Selection.Height = ph * scale;
        Selection.Visibility = Visibility.Visible;
    }

    private void UpdateRoiInfo()
    {
        ApplyButton.IsEnabled = _roi is not null;
        if (_roi is not { } roi)
        {
            RoiInfoText.Text = Localizer.T("VisualRoi.NoRegion");
            return;
        }

        RoiInfoText.Text = string.Format(
            Localizer.T("VisualRoi.RegionFormat"),
            Math.Round(roi.X * 100),
            Math.Round(roi.Y * 100),
            Math.Round(roi.W * 100),
            Math.Round(roi.H * 100));
    }

    private void OnApplyClicked(object sender, RoutedEventArgs e)
    {
        if (_roi is null)
        {
            return;
        }

        Result = _roi;
        Applied = true;
        Close();
    }

    private void OnClearClicked(object sender, RoutedEventArgs e)
    {
        // 清空框選＝回到整張圖搜索；這是一個明確的決定，所以要記成 Applied。
        Result = null;
        Applied = true;
        Close();
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }
}
