using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Sff;

namespace Editor;

/// <summary>The canvas background and checkerboard, as Preferences > Canvas set them.</summary>
public sealed record CanvasLook(IBrush Background, IBrush Checker, bool Checkerboard, int CheckerSize)
{
    public static readonly CanvasLook Default = new(new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x28)),
        new SolidColorBrush(Color.FromRgb(0x27, 0x27, 0x30)), true, Settings.DefaultCheckerSize);
}

/// <summary>Shows one sprite's visible pixels scaled to fit, centered over the canvas background.
/// Transparent borders don't count toward the fit. No zoom or pan.</summary>
public sealed class FitPreviewView : Control
{
    private WriteableBitmap? _bitmap;
    private Rect _content; // bounding box of the non-transparent pixels, in bitmap pixels
    private IBrush? _checker;

    public CanvasLook Look { get; set; } = CanvasLook.Default;

    public FitPreviewView()
    {
        ClipToBounds = true;
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
    }

    /// <summary>Shows the sprite with its own palette; null clears the view.</summary>
    public void Show(Sprite? sprite)
    {
        _bitmap?.Dispose();
        _bitmap = null;
        if (sprite is { Width: > 0, Height: > 0, HasPixels: true })
        {
            var bgra = sprite.ToBgra(sprite.Palette);
            _content = VisibleBounds(bgra, sprite.Width, sprite.Height);
            if (_content.Width > 0) _bitmap = ImageFile.ToBitmap(bgra, sprite.Width, sprite.Height);
        }
        InvalidateVisual();
    }

    private static Rect VisibleBounds(byte[] bgra, int width, int height)
    {
        int left = width, top = height, right = -1, bottom = -1;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                if (bgra[(y * width + x) * 4 + 3] == 0) continue;
                if (x < left) left = x;
                if (x > right) right = x;
                if (y < top) top = y;
                if (y > bottom) bottom = y;
            }
        return right < 0 ? default : new Rect(left, top, right - left + 1, bottom - top + 1);
    }

    public override void Render(DrawingContext ctx)
    {
        var full = new Rect(Bounds.Size);
        ctx.FillRectangle(Look.Background, full);
        if (Look.Checkerboard) ctx.FillRectangle(_checker ??= CheckerBrush(Look), full);
        if (_bitmap is null || Bounds.Width <= 0 || Bounds.Height <= 0) return;

        double w = _content.Width, h = _content.Height;
        double scale = Math.Min(Bounds.Width / w, Bounds.Height / h);
        double dw = w * scale, dh = h * scale;
        ctx.DrawImage(_bitmap, _content,
            new Rect((Bounds.Width - dw) / 2, (Bounds.Height - dh) / 2, dw, dh));
    }

    /// <summary>Same tiling as the sprite canvas: two checker squares per 2x2 tile.</summary>
    private static IBrush CheckerBrush(CanvasLook look)
    {
        int size = Math.Clamp(look.CheckerSize, 1, 512);
        var drawing = new DrawingGroup
        {
            Children =
            {
                new GeometryDrawing { Brush = look.Checker, Geometry = new RectangleGeometry(new Rect(0, 0, size, size)) },
                new GeometryDrawing { Brush = look.Checker, Geometry = new RectangleGeometry(new Rect(size, size, size, size)) },
            },
        };
        return new DrawingBrush(drawing)
        {
            TileMode = TileMode.Tile,
            Stretch = Stretch.Fill,
            DestinationRect = new RelativeRect(0, 0, size * 2, size * 2, RelativeUnit.Absolute),
        };
    }
}
