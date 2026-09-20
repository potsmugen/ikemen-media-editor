using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Editor;

/// <summary>
/// Paint Shop Pro-style picker: a box with hue top to bottom and lightness left
/// to right (black, full color, white), a saturation strip (full at the top,
/// gray at the bottom) and an optional alpha strip.
/// </summary>
public sealed class ColorPickerView : Control
{
    /// <summary>Box size (settings.json ColorPickerBoxWidth/Height; set at startup).</summary>
    public static int BoxWidth { get; set; } = Settings.DefaultColorPickerBoxWidth;
    public static int BoxHeight { get; set; } = Settings.DefaultColorPickerBoxHeight;

    private const double StripWidth = 20, Gap = 8, CheckerSize = 5, MarkerRadius = 5;
    private static readonly IPen Border = new Pen(Brushes.Gray, 1);

    private double _h, _s = 1, _l = 0.5;
    private byte _a = 255;
    private WriteableBitmap? _box;
    private double _boxSaturation = -1;
    private enum Part { None, Box, Saturation, Alpha }
    private Part _drag;

    public bool ShowAlpha { get; init; }
    /// <summary>Raised on every move while dragging.</summary>
    public event Action<Color>? ColorChanged;
    /// <summary>Raised when a drag ends (button up or capture lost).</summary>
    public event Action? DragCompleted;

    public Color Color
    {
        get
        {
            var c = HslToRgb(_h, _s, _l);
            return Color.FromArgb(_a, c.R, c.G, c.B);
        }
    }

    /// <summary>Grays, black and white have no hue (and black/white no saturation), so those markers stay put.</summary>
    public void SetColor(Color c)
    {
        var (h, s, l) = RgbToHsl(c);
        if (s > 0 && l > 0 && l < 1) _h = h;
        if (l > 0 && l < 1) _s = s;
        _l = l;
        _a = c.A;
        InvalidateVisual();
    }

    private Rect BoxRect => new(0, 0, BoxWidth, BoxHeight);
    private Rect SaturationRect => new(BoxWidth + Gap, 0, StripWidth, BoxHeight);
    private Rect AlphaRect => new(BoxWidth + Gap * 2 + StripWidth, 0, StripWidth, BoxHeight);

    protected override Size MeasureOverride(Size availableSize) =>
        new(BoxWidth + Gap + StripWidth + (ShowAlpha ? Gap + StripWidth : 0), BoxHeight);

    public override void Render(DrawingContext ctx)
    {
        EnsureBox();
        ctx.DrawImage(_box!, new Rect(0, 0, BoxWidth, BoxHeight), BoxRect);
        ctx.DrawRectangle(Border, BoxRect);

        ctx.FillRectangle(Vertical(HslToRgb(_h, 1, _l), HslToRgb(_h, 0, _l)), SaturationRect);
        ctx.DrawRectangle(Border, SaturationRect);

        if (ShowAlpha)
        {
            var r = AlphaRect;
            for (int y = 0; y * CheckerSize < r.Height; y++)
                for (int x = 0; x * CheckerSize < r.Width; x++)
                    ctx.FillRectangle((x + y) % 2 == 0 ? Brushes.White : Brushes.LightGray,
                        new Rect(r.X + x * CheckerSize, r.Y + y * CheckerSize,
                            Math.Min(CheckerSize, r.Width - x * CheckerSize), Math.Min(CheckerSize, r.Height - y * CheckerSize)));
            var c = HslToRgb(_h, _s, _l);
            ctx.FillRectangle(Vertical(c, Color.FromArgb(0, c.R, c.G, c.B)), r);
            ctx.DrawRectangle(Border, r);
        }

        var p = new Point(_l * BoxWidth, _h / 360 * BoxHeight);
        ctx.DrawEllipse(null, new Pen(Brushes.Black, 3), p, MarkerRadius, MarkerRadius);
        ctx.DrawEllipse(null, new Pen(Brushes.White, 1.5), p, MarkerRadius, MarkerRadius);
        StripMarker(ctx, SaturationRect, (1 - _s) * BoxHeight);
        if (ShowAlpha) StripMarker(ctx, AlphaRect, (1 - _a / 255.0) * BoxHeight);
    }

    private static IBrush Vertical(Color top, Color bottom) => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(top, 0), new GradientStop(bottom, 1) },
    };

    private static void StripMarker(DrawingContext ctx, Rect strip, double y)
    {
        var r = new Rect(strip.X - 2, strip.Y + y - 2, strip.Width + 4, 4);
        ctx.DrawRectangle(new Pen(Brushes.White, 1), r);
        ctx.DrawRectangle(new Pen(Brushes.Black, 1), r.Inflate(1));
    }

    /// <summary>Redraws the box bitmap only when the saturation it was drawn at changes.</summary>
    private void EnsureBox()
    {
        if (_box is not null && _boxSaturation == _s) return;
        _boxSaturation = _s;
        _box ??= new WriteableBitmap(new PixelSize(BoxWidth, BoxHeight), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using var fb = _box.Lock();
        var row = new byte[BoxWidth * 4];
        for (int y = 0; y < BoxHeight; y++)
        {
            double h = (y + 0.5) / BoxHeight * 360;
            for (int x = 0; x < BoxWidth; x++)
            {
                var c = HslToRgb(h, _s, (x + 0.5) / BoxWidth);
                row[x * 4] = c.B; row[x * 4 + 1] = c.G; row[x * 4 + 2] = c.R; row[x * 4 + 3] = 255;
            }
            System.Runtime.InteropServices.Marshal.Copy(row, 0, fb.Address + y * fb.RowBytes, row.Length);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var p = e.GetPosition(this);
        _drag = BoxRect.Contains(p) ? Part.Box
            : SaturationRect.Contains(p) ? Part.Saturation
            : ShowAlpha && AlphaRect.Contains(p) ? Part.Alpha
            : Part.None;
        if (_drag == Part.None) return;
        e.Pointer.Capture(this);
        DragTo(p);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_drag != Part.None) DragTo(e.GetPosition(this));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag == Part.None) return;
        _drag = Part.None;
        e.Pointer.Capture(null);
        DragCompleted?.Invoke();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_drag == Part.None) return;
        _drag = Part.None;
        DragCompleted?.Invoke();
    }

    private void DragTo(Point p)
    {
        double fy = Math.Clamp(p.Y / BoxHeight, 0, 1);
        switch (_drag)
        {
            case Part.Box:
                _l = Math.Clamp(p.X / BoxWidth, 0, 1);
                _h = fy * 360;
                break;
            case Part.Saturation:
                _s = 1 - fy;
                break;
            case Part.Alpha:
                _a = (byte)Math.Round((1 - fy) * 255);
                break;
        }
        InvalidateVisual();
        ColorChanged?.Invoke(Color);
    }

    public static Color HslToRgb(double h, double s, double l)
    {
        h = (h % 360 + 360) % 360;
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        double m = l - c / 2;
        var (r, g, b) = h < 60 ? (c, x, 0.0) : h < 120 ? (x, c, 0.0) : h < 180 ? (0.0, c, x)
            : h < 240 ? (0.0, x, c) : h < 300 ? (x, 0.0, c) : (c, 0.0, x);
        byte B(double v) => (byte)Math.Clamp(Math.Round((v + m) * 255), 0, 255);
        return Color.FromRgb(B(r), B(g), B(b));
    }

    public static (double H, double S, double L) RgbToHsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2, d = max - min;
        if (d == 0) return (0, 0, l);
        double s = Math.Min(1, d / (1 - Math.Abs(2 * l - 1)));
        double h = max == r ? 60 * ((g - b) / d % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        if (h < 0) h += 360;
        return (h, s, l);
    }
}

/// <summary>
/// Opens a ColorPickerView in a drop-down under an anchor, with OK and Cancel.
/// Clicking outside does nothing; Enter = OK, Esc = Cancel.
/// </summary>
public static class ColorPickerFlyout
{
    /// <param name="changed">Every move while dragging (live preview).</param>
    /// <param name="ok">OK: keep the current color.</param>
    /// <param name="cancel">Cancel: the caller restores the start color.</param>
    public static void Open(Control anchor, Color start, bool alpha, Action<Color> changed, Action ok, Action cancel)
    {
        var picker = new ColorPickerView { ShowAlpha = alpha };
        picker.SetColor(start);
        picker.ColorChanged += changed;

        var okButton = new Button { Content = "OK", MinWidth = 80, IsDefault = true };
        var cancelButton = new Button { Content = "Cancel", MinWidth = 80 };
        var content = new StackPanel
        {
            Spacing = 8, Focusable = true,
            Children =
            {
                picker,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center,
                    Children = { okButton, cancelButton },
                },
            },
        };

        var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedLeft, Content = content };
        bool closing = false;
        void Close(Action result)
        {
            if (closing) return;
            closing = true;
            result();
            flyout.Hide();
        }
        flyout.Closing += (_, e) => { if (!closing) e.Cancel = true; }; // outside clicks don't close it
        okButton.Click += (_, _) => Close(ok);
        cancelButton.Click += (_, _) => Close(cancel);
        content.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter) { Close(ok); e.Handled = true; }
            else if (e.Key == Key.Escape) { Close(cancel); e.Handled = true; }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        flyout.Opened += (_, _) => content.Focus();
        flyout.ShowAt(anchor);
    }
}
