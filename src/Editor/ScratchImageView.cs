using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Sff;

namespace Editor;

/// <summary>
/// Displays an arbitrary reference image — not a game sprite, just a picture
/// to sample colors from — scaled to fit the available width until the mouse
/// wheel zooms it. Clicking it reports the color under the pointer, read from
/// the decoded pixels this already has rather than reading back out of the
/// displayed bitmap. Zoom and pan follow the sprite canvas's mouse buttons:
/// wheel to zoom, middle-drag to pan, middle-click to put the view back,
/// right-click for "Center image in canvas".
/// </summary>
public class ScratchImageView : Control
{
    private const int FitIndex = -1; // _zoomIdx value meaning "still fitted to the panel width"
    private const double ClickSlop = 3; // middle-button travel, in pixels, below which it's a click and not a drag

    private byte[]? _bgra; // one byte per B,G,R,A, row-major — decoded once, kept for picking
    private int _width, _height;
    private WriteableBitmap? _bitmap;

    /// <summary>Index into <see cref="SpriteView.ZoomLevels"/>, or <see cref="FitIndex"/> while fitted.</summary>
    private int _zoomIdx = FitIndex;
    private Point _pan;
    private bool _panning, _panMoved;
    private Point _panFrom;

    /// <summary>Raised with the color under the pointer whenever the image is clicked.</summary>
    public event Action<Rgba>? ColorPicked;

    /// <summary>Raised on a right-click, so the window can offer the same context menu the sprite canvas gets.</summary>
    public event Action? ImageContextRequested;

    /// <summary>Current scale. Fits the panel width until the zoom is stepped away from that.</summary>
    public double Zoom => _zoomIdx == FitIndex ? FitScale : SpriteView.ZoomLevels[_zoomIdx];

    /// <summary>The scale that spreads the image across the panel — the default view, kept as a zoom state of its own.</summary>
    private double FitScale => _width > 0 && Bounds.Width > 0 ? Bounds.Width / _width : 1;

    /// <summary>Backdrop color; kept in step with the app theme (see MainWindow.ApplyTheme).</summary>
    public IBrush Background { get; set; } = new SolidColorBrush(Color.FromRgb(0x2a, 0x2a, 0x2e));

    public ScratchImageView()
    {
        ClipToBounds = true;
        MinHeight = 80;
        // Nearest-neighbor, like SpriteView. The picked color is read from _bgra by
        // integer index rather than from the rendered pixels, so what matters is that
        // the pixel under the pointer is the pixel that gets picked.
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
    }

    public void Load(byte[] bgra, int width, int height)
    {
        _width = width;
        _height = height;
        _bgra = bgra;
        _bitmap?.Dispose();
        _bitmap = _width > 0 && _height > 0 ? MakeBitmap(_bgra, _width, _height) : null;
        // A new image always starts fitted, whatever the last one was left at.
        _zoomIdx = FitIndex;
        _pan = default;
        InvalidateMeasure();
        InvalidateVisual();
    }

    public void Clear()
    {
        _bgra = null;
        _width = _height = 0;
        _bitmap?.Dispose();
        _bitmap = null;
        InvalidateMeasure();
        InvalidateVisual();
    }

    private static WriteableBitmap MakeBitmap(byte[] bgra, int width, int height)
    {
        var bmp = new WriteableBitmap(new PixelSize(width, height),
            new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using var fb = bmp.Lock();
        System.Runtime.InteropServices.Marshal.Copy(bgra, 0, fb.Address, bgra.Length);
        return bmp;
    }

    /// <summary>Steps the zoom the same way the sprite canvas does, leaving the fitted view for the nearest step below it.</summary>
    public void StepZoom(int delta)
    {
        var levels = SpriteView.ZoomLevels;
        if (_width == 0) return;

        if (_zoomIdx == FitIndex)
        {
            // Start one step below the fitted scale, so the first wheel click lands on a
            // real step instead of jumping straight to the next one up.
            double fit = FitScale;
            int below = 0;
            for (int i = 0; i < levels.Length; i++)
                if (levels[i] <= fit) below = i;
            _zoomIdx = below;
            if (_zoomIdx == 0 && delta < 0) return; // already past the smallest step
        }

        int next = Math.Clamp(_zoomIdx + delta, 0, levels.Length - 1);
        if (next == _zoomIdx) return;
        _zoomIdx = next;
        InvalidateMeasure();
        InvalidateVisual();
    }

    /// <summary>Back to the fitted view: the whole image across the panel, with no pan.</summary>
    public void ResetView()
    {
        _zoomIdx = FitIndex;
        _pan = default;
        InvalidateMeasure();
        InvalidateVisual();
    }

    /// <summary>Moves the image into the middle of the panel without changing zoom.</summary>
    public void CenterImage()
    {
        if (_width == 0 || _height == 0) return;
        double z = Zoom;
        _pan = new Point((Bounds.Width - _width * z) / 2, (Bounds.Height - _height * z) / 2);
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (_bitmap is null || _width == 0) return new Size(0, MinHeight);
        double w = double.IsInfinity(availableSize.Width) ? _width : availableSize.Width;
        // Fitted, the image tracks the panel width; a stepped zoom keeps its own scale.
        double z = _zoomIdx == FitIndex && w > 0 ? w / _width : Zoom;
        return new Size(w, Math.Max(_height * z, MinHeight));
    }

    public override void Render(DrawingContext ctx)
    {
        ctx.FillRectangle(Background, new Rect(Bounds.Size));
        if (_bitmap is null || _width == 0) return;
        double z = Zoom;
        ctx.DrawImage(_bitmap, new Rect(0, 0, _width, _height),
            new Rect(_pan.X, _pan.Y, _width * z, _height * z));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_bgra is null || _width == 0 || Bounds.Width <= 0) return;

        var p = e.GetCurrentPoint(this);

        // Middle button, as on the sprite canvas: a click puts the view back, a drag pans it.
        if (p.Properties.IsMiddleButtonPressed)
        {
            _panning = true;
            _panMoved = false;
            _panFrom = p.Position;
            Cursor = new Cursor(StandardCursorType.Hand);
            e.Pointer.Capture(this);
            return;
        }

        if (p.Properties.IsRightButtonPressed)
        {
            ImageContextRequested?.Invoke();
            return;
        }

        if (!p.Properties.IsLeftButtonPressed) return;

        double z = Zoom;
        int x = (int)((p.Position.X - _pan.X) / z), y = (int)((p.Position.Y - _pan.Y) / z);
        if (x < 0 || x >= _width || y < 0 || y >= _height) return;

        int off = (y * _width + x) * 4;
        ColorPicked?.Invoke(new Rgba(_bgra[off + 2], _bgra[off + 1], _bgra[off], _bgra[off + 3]));
    }

    /// <summary>
    /// The tip sits over the very image being sampled from, and Avalonia keeps it up
    /// until the pointer leaves the control, so close it on any movement.
    /// </summary>
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (_panning)
        {
            var p = e.GetPosition(this);
            if (Point.Distance(p, _panFrom) > ClickSlop) _panMoved = true;
            _pan += p - _panFrom;
            _panFrom = p;
            InvalidateVisual();
        }

        if (ToolTip.GetIsOpen(this)) ToolTip.SetIsOpen(this, false);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_panning) return;
        _panning = false;
        Cursor = Cursor.Default;
        e.Pointer.Capture(null);
        // A middle click that never traveled is a reset, the canvas's middle-click behavior.
        if (!_panMoved) ResetView();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        StepZoom(e.Delta.Y > 0 ? 1 : -1);
        e.Handled = true;
    }
}
