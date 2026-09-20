using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Sff;

namespace Editor;

/// <summary>What fills the canvas behind the sprite. Image draws over the Flat color.</summary>
public enum BackgroundStyle { Flat, Checkerboard, Image }

/// <summary>Background image drawn under the sprite on the Sprites and Animations canvases.</summary>
public sealed class CanvasBackground
{
    public required Bitmap Image { get; init; }
    /// <summary>Tiles the image across the canvas instead of drawing it once, centered on the axis.</summary>
    public bool Tile { get; init; }
    /// <summary>Moves the image right/down, in pixels at 100% zoom.</summary>
    public Point Offset { get; init; }
    public double Opacity { get; init; } = 1;
    /// <summary>Scales with the sprite's zoom (around the axis) instead of staying at 100%.</summary>
    public bool FollowZoom { get; init; } = true;
    /// <summary>Moves with the axis when panning instead of staying fixed to the view.</summary>
    public bool FollowPan { get; init; } = true;
}

public static class CanvasColors
{
    /// <summary>Second checker square: a slight brightness shift from the base, lighter on dark bases, darker on light ones.</summary>
    public static Color CheckerFor(Color bg)
    {
        double luminance = 0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B;
        int delta = luminance > 128 ? -14 : 14;
        byte Shift(byte v) => (byte)Math.Clamp(v + delta, 0, 255);
        return Color.FromRgb(Shift(bg.R), Shift(bg.G), Shift(bg.B));
    }
}

/// <summary>How the axis is marked: colored lines, or an image whose hotspot sits on the axis.</summary>
public sealed class CrosshairStyle
{
    public static readonly Color DefaultColor = Color.FromRgb(0xe0, 0xe0, 0xe0);
    public static readonly CrosshairStyle Default = new();

    public Color Color { get; init; } = DefaultColor;
    public double ArmLength { get; init; } = Settings.DefaultCrosshairArmLength;
    public double Thickness { get; init; } = Settings.DefaultCrosshairThickness;
    /// <summary>Set = Image style; null = Lines.</summary>
    public Bitmap? Image { get; init; }
    /// <summary>Image pixel placed on the axis.</summary>
    public PixelPoint Hotspot { get; init; }
    public bool ScaleWithZoom { get; init; }
}

/// <summary>Where the example canvas backgrounds ship beside the exe (see the copy rule in Editor.csproj). Browsing for one starts here.</summary>
public static class CanvasPatterns
{
    public static string Folder { get; } = Path.Combine(AppContext.BaseDirectory, "backgrounds");
}

/// <summary>Image loading shared by the canvas background, crosshair and reference image.</summary>
public static class ImageFile
{
    public static readonly string[] Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp"];

    /// <summary>Straight-alpha BGRA. PNG goes through our own codec; JPG/BMP through Avalonia.</summary>
    public static (byte[] Bgra, int Width, int Height) LoadBgra(string path)
    {
        var data = File.ReadAllBytes(path);
        if (data.Length >= 8 && data[0] == 0x89 && data[1] == (byte)'P' && data[2] == (byte)'N' && data[3] == (byte)'G')
            return PngExport.DecodeToBgra(data);

        using var ms = new MemoryStream(data);
        using var bmp = new Bitmap(ms);
        int w = bmp.PixelSize.Width, h = bmp.PixelSize.Height;
        var buf = new byte[w * h * 4];
        var pin = GCHandle.Alloc(buf, GCHandleType.Pinned);
        try { bmp.CopyPixels(new PixelRect(0, 0, w, h), pin.AddrOfPinnedObject(), buf.Length, w * 4); }
        finally { pin.Free(); }

        var format = bmp.Format;
        bool rgba = format == PixelFormats.Rgba8888;
        if (!rgba && format != PixelFormats.Bgra8888)
            throw new NotSupportedException($"Unsupported decoded pixel format {format}.");
        bool premul = bmp.AlphaFormat == AlphaFormat.Premul;
        for (int i = 0; i + 3 < buf.Length; i += 4)
        {
            if (rgba) (buf[i], buf[i + 2]) = (buf[i + 2], buf[i]);
            byte a = buf[i + 3];
            if (premul && a is > 0 and < 255)
                for (int k = 0; k < 3; k++) buf[i + k] = (byte)Math.Min(255, buf[i + k] * 255 / a);
        }
        return (buf, w, h);
    }

    public static WriteableBitmap ToBitmap(byte[] bgra, int width, int height)
    {
        var bmp = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using var fb = bmp.Lock();
        for (int y = 0; y < height; y++)
            Marshal.Copy(bgra, y * width * 4, fb.Address + y * fb.RowBytes, width * 4);
        return bmp;
    }

    public static WriteableBitmap Load(string path)
    {
        var (bgra, w, h) = LoadBgra(path);
        if (w <= 0 || h <= 0) throw new InvalidDataException("The image has no pixels.");
        return ToBitmap(bgra, w, h);
    }
}
