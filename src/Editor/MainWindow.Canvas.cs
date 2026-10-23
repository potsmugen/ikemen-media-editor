using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace Editor;

// Canvas background and crosshair (Preferences > Canvas).
public partial class MainWindow
{
    private static readonly FilePickerFileType ImageType =
        new("Images (PNG, JPG, BMP)") { Patterns = ImageFile.Patterns };

    /// <summary>A loaded canvas image, kept while its path stays the same (previews re-apply often). A failed load is kept too, so it's logged once.</summary>
    private sealed class ImageSlot
    {
        public string? Path;
        public Bitmap? Bitmap;
    }

    private readonly ImageSlot _backgroundSlot = new(), _crosshairSlot = new();
    /// <summary>What the canvases currently show; ApplyTheme re-applies it for the new theme color.</summary>
    private Dialogs.CanvasValues? _appliedCanvas;
    /// <summary>The theme's canvas color (set by ApplyTheme), used when the base color is Theme.</summary>
    private Color _themeCanvasColor = Color.FromRgb(0x2a, 0x2a, 0x2e);

    private Dialogs.CanvasLimits CanvasLimits()
    {
        var s = _settings;
        double armMin = Settings.Checked(nameof(s.Canvas.CrosshairArmMin), s.Canvas.CrosshairArmMin,
            Settings.DefaultCrosshairArmMin, v => double.IsFinite(v) && v > 0);
        double armMax = Settings.Checked(nameof(s.Canvas.CrosshairArmMax), s.Canvas.CrosshairArmMax,
            Math.Max(armMin, Settings.DefaultCrosshairArmMax), v => double.IsFinite(v) && v >= armMin);
        double thMin = Settings.Checked(nameof(s.Canvas.CrosshairThicknessMin), s.Canvas.CrosshairThicknessMin,
            Settings.DefaultCrosshairThicknessMin, v => double.IsFinite(v) && v > 0);
        double thMax = Settings.Checked(nameof(s.Canvas.CrosshairThicknessMax), s.Canvas.CrosshairThicknessMax,
            Math.Max(thMin, Settings.DefaultCrosshairThicknessMax), v => double.IsFinite(v) && v >= thMin);
        int offMax = Settings.Checked(nameof(s.Canvas.BackgroundOffsetMax), s.Canvas.BackgroundOffsetMax,
            Settings.DefaultBackgroundOffsetMax, v => v >= 0);
        return new(armMin, armMax, thMin, thMax, offMax);
    }

    private static T CheckedEnum<T>(string name, string? value, T fallback) where T : struct, Enum
    {
        if (Enum.TryParse<T>(value, true, out var v) && Enum.IsDefined(v)) return v;
        CrashLog.Error("Settings", $"{name} = {value} isn't valid; using {fallback}");
        return fallback;
    }

    /// <summary>A #RRGGBB setting; null stays null (theme/auto), nonsense is logged and treated as null.</summary>
    private static Color? CheckedColor(string name, string? value)
    {
        if (value is null) return null;
        if (Color.TryParse(value, out var c)) return Color.FromRgb(c.R, c.G, c.B);
        CrashLog.Error("Settings", $"{name} = {value} isn't valid; using the default");
        return null;
    }

    private Dialogs.CanvasValues CanvasValuesFromSettings(Settings? from = null)
    {
        var s = from ?? _settings;
        var style = s.Canvas.BackgroundStyle is null ? BackgroundStyle.Checkerboard
            : CheckedEnum(nameof(s.Canvas.BackgroundStyle), s.Canvas.BackgroundStyle, BackgroundStyle.Checkerboard);
        return new(style, s.Canvas.BaseColor, s.Canvas.CheckerColor, s.Canvas.CheckerSize,
            s.Canvas.BackgroundImagePath, s.Canvas.BackgroundTile, s.Canvas.BackgroundOffsetX, s.Canvas.BackgroundOffsetY, s.Canvas.BackgroundOpacityPercent,
            s.Canvas.BackgroundFollowZoom, s.Canvas.BackgroundFollowPan,
            string.Equals(s.Canvas.CrosshairMode, "Image", StringComparison.OrdinalIgnoreCase), s.Canvas.CrosshairColor,
            s.Canvas.CrosshairArmLength, s.Canvas.CrosshairThickness, s.Canvas.CrosshairImagePath,
            s.Canvas.CrosshairHotspotX, s.Canvas.CrosshairHotspotY, s.Canvas.CrosshairScaleWithZoom);
    }

    private void StoreCanvasValues(Dialogs.CanvasValues c)
    {
        var s = _settings;
        s.Canvas.BackgroundStyle = c.Style.ToString();
        s.Canvas.BaseColor = c.BaseColor;
        s.Canvas.CheckerColor = c.CheckerColor;
        s.Canvas.CheckerSize = c.CheckerSize;
        s.Canvas.BackgroundImagePath = c.BackgroundPath;
        s.Canvas.BackgroundTile = c.Tile;
        s.Canvas.BackgroundOffsetX = c.OffsetX;
        s.Canvas.BackgroundOffsetY = c.OffsetY;
        s.Canvas.BackgroundOpacityPercent = c.OpacityPercent;
        s.Canvas.BackgroundFollowZoom = c.FollowZoom;
        s.Canvas.BackgroundFollowPan = c.FollowPan;
        s.Canvas.CrosshairMode = c.CrosshairImageStyle ? "Image" : "Lines";
        s.Canvas.CrosshairColor = c.Color;
        s.Canvas.CrosshairArmLength = c.ArmLength;
        s.Canvas.CrosshairThickness = c.Thickness;
        s.Canvas.CrosshairImagePath = c.CrosshairImagePath;
        s.Canvas.CrosshairHotspotX = c.HotspotX;
        s.Canvas.CrosshairHotspotY = c.HotspotY;
        s.Canvas.CrosshairScaleWithZoom = c.ScaleWithZoom;
    }

    /// <summary>Image picker for the Preferences dialog, parented to it. When it exists, <paramref name="startFolder"/> opens the picker there.</summary>
    private async Task<string?> PickImageFor(Window owner, string title, string? startFolder)
    {
        IStorageFolder? start = startFolder is not null && Directory.Exists(startFolder)
            ? await owner.StorageProvider.TryGetFolderFromPathAsync(startFolder)
            : null;
        var files = await PickOpen(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [ImageType],
            SuggestedStartLocation = start,
        }, owner);
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    /// <summary>The slot's image for path, loading it on a path change. A missing or unreadable file is logged once and reported in problems each time.</summary>
    private static Bitmap? UseImage(ImageSlot slot, string? path, string what, List<string> problems)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (!string.Equals(slot.Path, path, StringComparison.Ordinal))
        {
            slot.Bitmap?.Dispose();
            slot.Bitmap = null;
            slot.Path = path;
            if (!File.Exists(path))
                CrashLog.Error("Canvas", $"{what} not found: {path}");
            else
            {
                try { slot.Bitmap = ImageFile.Load(path); }
                catch (Exception ex) { CrashLog.Write($"Canvas.{what}", ex); }
            }
        }
        if (slot.Bitmap is null) problems.Add($"{what} could not be loaded ({Path.GetFileName(path)})");
        return slot.Bitmap;
    }

    /// <summary>The current canvas background, for previews outside the main window.</summary>
    private CanvasLook _canvasLook = CanvasLook.Default;

    /// <summary>Hands the background and crosshair to the canvases. Startup, Preferences preview/OK/revert, and theme changes.</summary>
    private void ApplyCanvasDecor(Dialogs.CanvasValues v)
    {
        _appliedCanvas = v;
        var limits = CanvasLimits();
        var problems = new List<string>();

        var baseColor = CheckedColor(nameof(Settings.CanvasSection.BaseColor), v.BaseColor) ?? _themeCanvasColor;
        var checkerColor = CheckedColor(nameof(Settings.CanvasSection.CheckerColor), v.CheckerColor) ?? CanvasColors.CheckerFor(baseColor);

        var bgImage = v.Style == BackgroundStyle.Image
            ? UseImage(_backgroundSlot, v.BackgroundPath, "Background image", problems) : null; // null = Flat
        CanvasBackground? background = bgImage is null ? null : new CanvasBackground
        {
            Image = bgImage,
            Tile = v.Tile,
            Offset = new Point(Math.Clamp(v.OffsetX, -limits.OffsetMax, limits.OffsetMax),
                Math.Clamp(v.OffsetY, -limits.OffsetMax, limits.OffsetMax)),
            Opacity = Settings.Checked(nameof(Settings.CanvasSection.BackgroundOpacityPercent), v.OpacityPercent,
                Settings.DefaultBackgroundOpacityPercent, o => o is >= 0 and <= 100) / 100,
            FollowZoom = v.FollowZoom,
            FollowPan = v.FollowPan,
        };

        var chImage = v.CrosshairImageStyle
            ? UseImage(_crosshairSlot, v.CrosshairImagePath, "Crosshair image", problems) : null; // null = Lines
        var crosshair = new CrosshairStyle
        {
            Color = CheckedColor(nameof(Settings.CanvasSection.CrosshairColor), v.Color) ?? CrosshairStyle.DefaultColor,
            ArmLength = Settings.Checked(nameof(Settings.CanvasSection.CrosshairArmLength), v.ArmLength,
                Math.Clamp(Settings.DefaultCrosshairArmLength, limits.ArmMin, limits.ArmMax), a => a >= limits.ArmMin && a <= limits.ArmMax),
            Thickness = Settings.Checked(nameof(Settings.CanvasSection.CrosshairThickness), v.Thickness,
                Math.Clamp(Settings.DefaultCrosshairThickness, limits.ThicknessMin, limits.ThicknessMax), t => t >= limits.ThicknessMin && t <= limits.ThicknessMax),
            Image = chImage,
            Hotspot = chImage is null ? default : new PixelPoint(
                Math.Clamp(v.HotspotX, 0, chImage.PixelSize.Width - 1),
                Math.Clamp(v.HotspotY, 0, chImage.PixelSize.Height - 1)),
            ScaleWithZoom = v.ScaleWithZoom,
        };

        var baseBrush = new SolidColorBrush(baseColor);
        var checkerBrush = new SolidColorBrush(checkerColor);
        var checkerSize = Settings.Checked(nameof(Settings.CanvasSection.CheckerSize), v.CheckerSize,
            Settings.DefaultCheckerSize, c => c >= Settings.CheckerSizeMin && c <= Settings.CheckerSizeMax);
        _canvasLook = new CanvasLook(baseBrush, checkerBrush, v.Style == BackgroundStyle.Checkerboard, checkerSize);
        foreach (var c in AllCanvases())
        {
            c.Background = baseBrush;
            c.CheckerDark = checkerBrush;
            c.Checkerboard = v.Style == BackgroundStyle.Checkerboard;
            c.CheckerSize = checkerSize;
            c.BackgroundImage = background;
            c.Crosshair = crosshair;
            c.InvalidateVisual();
        }
        if (PalettePreview is not null) PalettePreview.ShowCrosshair = false;

        if (problems.Count > 0)
        {
            // Posted so startup's own status updates don't overwrite it.
            var msg = string.Join("; ", problems) + " - see Preferences > Canvas.";
            Dispatcher.UIThread.Post(() => StatusText.Text = msg, DispatcherPriority.Background);
        }
    }
}
