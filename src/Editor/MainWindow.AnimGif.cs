using Air;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Sff;
using SkiaSharp;

namespace Editor;

/// <summary>Export Animation on the Animations playback bar: one pass of the current action as a looping animated WebP or GIF.</summary>
public partial class MainWindow
{
    private static readonly FilePickerFileType GifType = new("GIF images") { Patterns = ["*.gif"] };
    private static readonly FilePickerFileType WebPType = new("WebP images") { Patterns = ["*.webp"] };
    private Dialogs.AnimationExportFormat _lastAnimExportFormat = Dialogs.AnimationExportFormat.WebP;
    private bool _lastAnimExportBackground;

    /// <summary>One sprite placed on the GIF canvas: world = M·(pixel − axis) + offset, M = [A C; B D].</summary>
    private readonly record struct GifPlacement(Sprite Sprite, byte[] Bgra, double A, double B, double C, double D, double Ox, double Oy);

    private async void OnExportAnimation(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_action is not { Elements.Count: > 0 } action || _file is null)
            {
                StatusText.Text = "Choose an action with frames first.";
                return;
            }

            int scaleMax = Settings.Checked(nameof(Settings.AnimationsSection.AnimExportScaleMax), _settings.Animations.AnimExportScaleMax, Settings.DefaultAnimExportScaleMax, v => v >= 1);
            int scaleDefault = Settings.Checked(nameof(Settings.AnimationsSection.AnimExportScale), _settings.Animations.AnimExportScale, Settings.DefaultAnimExportScale, v => v >= 1 && v <= scaleMax);
            int maxSide = Settings.Checked(nameof(Settings.AnimationsSection.AnimExportMaxSide), _settings.Animations.AnimExportMaxSide, Settings.DefaultAnimExportMaxSide, v => v is >= 1 and <= 65535);
            if (await Dialogs.PromptAnimationExport(this, _lastAnimExportFormat, _lastAnimExportBackground, scaleDefault, scaleMax) is not { } choice) return;
            _lastAnimExportFormat = choice.Format;
            _lastAnimExportBackground = choice.Background;
            bool webp = choice.Format == Dialogs.AnimationExportFormat.WebP;
            if (webp) maxSide = Math.Min(maxSide, WebP.MaxSide);
            int scale = Math.Clamp(choice.Scale, 1, scaleMax);

            var runs = GifRuns(action);
            var placements = runs.Select(r => Place(action.Elements[r.Element], r.State)).ToList();
            var boxes = runs.Select(r => choice.Boxes ? GifBoxes(action.Elements[r.Element]) : new List<BoxOverlay>()).ToList();

            // Canvas in world pixels (axis at 0,0): every transformed sprite, plus the boxes when drawn.
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            void Include(double x, double y)
            {
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }
            foreach (var p in placements)
            {
                if (p is not { } pl) continue;
                foreach (var (cx, cy) in new[] { (0, 0), (pl.Sprite.Width, 0), (0, pl.Sprite.Height), (pl.Sprite.Width, pl.Sprite.Height) })
                {
                    var (wx, wy) = ToWorld(pl, cx, cy);
                    Include(wx, wy);
                }
            }
            foreach (var b in boxes.SelectMany(list => list))
            {
                Include(Math.Min(b.X1, b.X2), Math.Min(b.Y1, b.Y2));
                Include(Math.Max(b.X1, b.X2), Math.Max(b.Y1, b.Y2));
            }
            if (minX > maxX)
            {
                StatusText.Text = "Nothing to export: none of this action's sprites are in the loaded .sff.";
                return;
            }
            int left = (int)Math.Floor(minX), top = (int)Math.Floor(minY);
            int worldW = Math.Max(1, (int)Math.Ceiling(maxX) - left), worldH = Math.Max(1, (int)Math.Ceiling(maxY) - top);
            long width = (long)worldW * scale, height = (long)worldH * scale;
            if (width > maxSide || height > maxSide)
            {
                StatusText.Text = $"Can't export: the {(webp ? "WebP" : "GIF")} would be {width}x{height} pixels (up to {maxSide} a side).";
                return;
            }

            var file = await PickSave(new FilePickerSaveOptions
            {
                Title = webp ? "Export Animation as WebP" : "Export Animation as GIF",
                SuggestedFileName = $"action{action.Number}.{(webp ? "webp" : "gif")}",
                DefaultExtension = webp ? "webp" : "gif",
                FileTypeChoices = [webp ? WebPType : GifType],
            });
            if (file?.TryGetLocalPath() is not { } path) return;

            // 60 ticks a second. WebP durations are in ms; GIF delays are in 1/100 s and players
            // stretch anything under 2, so GIF delays are at least 2. Later ones catch up to keep the total time right.
            int unitsPerSecond = webp ? 1000 : 100, minDelay = webp ? 1 : 2;
            var background = choice.Background ? ExportBackground((int)width, (int)height, left, top, scale) : null;
            var pixels = new List<byte[]>();
            var delays = new List<int>();
            double target = 0;
            int emitted = 0;
            for (int i = 0; i < runs.Count; i++)
            {
                target += runs[i].Ticks * (double)unitsPerSecond / 60;
                int delay = Math.Max(minDelay, (int)Math.Round(target - emitted));
                emitted += delay;
                var frame = background is null
                    ? RenderGifFrame(placements[i], left, top, (int)width, (int)height, scale)
                    : RenderBlendedFrame(background, placements[i], runs[i].State.Blend, left, top, (int)width, (int)height, scale);
                DrawGifBoxes(frame, boxes[i], left, top, (int)width, (int)height, scale);
                pixels.Add(frame);
                delays.Add(delay);
            }

            var bytes = await Task.Run(() => webp
                ? WebP.Encode((int)width, (int)height, pixels.Select((p, i) => new WebPFrame(p, delays[i])).ToList(), EncodeWebPStill)
                : Gif.Encode((int)width, (int)height, pixels.Select((p, i) => new GifFrame(p, delays[i])).ToList()));
            await File.WriteAllBytesAsync(path, bytes);
            CrashLog.Trace($"[anim] {(webp ? "webp" : "gif")} action {action.Number}: {pixels.Count} frames, {width}x{height}, x{scale}, boxes {choice.Boxes}, background {choice.Background}, {emitted}/{unitsPerSecond} s, {bytes.Length} bytes");
            StatusText.Text = $"Wrote {Path.GetFileName(path)}: {pixels.Count} frames, {width}x{height}, {emitted / (double)unitsPerSecond:0.##}s.";
        }
        catch (Exception ex)
        {
            await Failed("OnExportAnimation", ex);
        }
    }

    /// <summary>One lossless still WebP from straight BGRA, by libwebp inside Skia; Sff.WebP wraps the frames.</summary>
    private static byte[] EncodeWebPStill(byte[] bgra, int width, int height)
    {
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        var pin = System.Runtime.InteropServices.GCHandle.Alloc(bgra, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            using var pixmap = new SKPixmap(info, pin.AddrOfPinnedObject(), width * 4);
            using var data = pixmap.Encode(new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossless, 100));
            return data?.ToArray() ?? throw new InvalidOperationException($"Skia couldn't encode a {width}x{height} WebP frame.");
        }
        finally { pin.Free(); }
    }

    /// <summary>One pass of the action as runs of identical drawn frames; a final hold (-1) gets the playback dwell.</summary>
    private List<(int Element, DrawnState State, int Ticks)> GifRuns(AnimationAction action)
    {
        int finite = action.Elements.TakeWhile(el => !el.ShowsForever).Sum(el => Math.Max(el.Duration, 0));
        int length = finite + (action.LoopTime is null ? InfiniteAnimationTimeoutTicks : 0);
        var runs = new List<(int Element, DrawnState State, int Ticks)>();
        for (int t = 0; t < Math.Max(1, length); t++)
        {
            int i = Math.Max(0, action.PositionAt(t).Index);
            var state = action.StateAt(t);
            if (runs.Count > 0 && runs[^1].Element == i && runs[^1].State == state)
                runs[^1] = runs[^1] with { Ticks = runs[^1].Ticks + 1 };
            else
                runs.Add((i, state, 1));
        }
        return runs;
    }

    /// <summary>Where a frame's sprite lands: same flip/scale/angle maths as SpriteView.ElementTransform. Null if there's nothing to draw.</summary>
    private GifPlacement? Place(AnimationElement element, DrawnState state)
    {
        if (_file?.Find(element.Group, element.Index) is not { Width: > 0, Height: > 0, HasPixels: true } sprite) return null;
        double sx = state.ScaleX * (element.FlipH ? -1 : 1), sy = state.ScaleY * (element.FlipV ? -1 : 1);
        if (sx == 0 || sy == 0) return null;
        double rad = -state.Angle * Math.PI / 180.0; // engine angle is counter-clockwise, screen Y points down
        double cos = Math.Cos(rad), sin = Math.Sin(rad);
        return new GifPlacement(sprite, sprite.ToBgra(sprite.Palette, false),
            cos * sx, sin * sx, -sin * sy, cos * sy, state.OffsetX, state.OffsetY);
    }

    private static (double X, double Y) ToWorld(GifPlacement p, double px, double py)
    {
        double lx = px - p.Sprite.AxisX, ly = py - p.Sprite.AxisY;
        return (p.A * lx + p.C * ly + p.Ox, p.B * lx + p.D * ly + p.Oy);
    }

    /// <summary>Calls draw(output pixel, sprite pixel) for each output pixel the sprite covers, nearest-neighbor, scale output pixels per world pixel.</summary>
    private static void ForEachSpritePixel(GifPlacement p, int left, int top, int width, int height, int scale, Action<int, int> draw)
    {
        double det = p.A * p.D - p.C * p.B;
        int w = p.Sprite.Width, h = p.Sprite.Height;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                double dx = left + (x + 0.5) / scale - p.Ox, dy = top + (y + 0.5) / scale - p.Oy;
                int px = (int)Math.Floor((p.D * dx - p.C * dy) / det + p.Sprite.AxisX);
                int py = (int)Math.Floor((-p.B * dx + p.A * dy) / det + p.Sprite.AxisY);
                if (px < 0 || py < 0 || px >= w || py >= h) continue;
                draw(y * width + x, py * w + px);
            }
    }

    /// <summary>Draws one frame onto a transparent canvas. Blends draw as normal: there's no backdrop to blend with.</summary>
    private static byte[] RenderGifFrame(GifPlacement? placement, int left, int top, int width, int height, int scale)
    {
        var dst = new byte[width * height * 4];
        if (placement is { } p)
            ForEachSpritePixel(p, left, top, width, height, scale, (o, s) => Array.Copy(p.Bgra, s * 4, dst, o * 4, 4));
        return dst;
    }

    /// <summary>Draws one frame over the opaque background with the canvas's blend math (SpriteView.DrawBlended
    /// with Skia's Plus and Multiply), so the export looks like the canvas. Subtract stays the canvas's stand-in.</summary>
    private static byte[] RenderBlendedFrame(byte[] background, GifPlacement? placement, Blend blend, int left, int top, int width, int height, int scale)
    {
        var dst = (byte[])background.Clone();
        if (placement is not { } p) return dst;
        double srcW = Math.Clamp(blend.Source / 255.0, 0, 1), dstW = Math.Clamp(blend.Destination / 255.0, 0, 1);
        var mode = blend.Mode;
        var src = p.Bgra;
        ForEachSpritePixel(p, left, top, width, height, scale, (o, s) =>
        {
            double a = src[s * 4 + 3] / 255.0;
            if (a == 0) return;
            double gray = (src[s * 4] + src[s * 4 + 1] + src[s * 4 + 2]) / 3 / 255.0; // integer average, as SpriteView.Gray
            for (int k = 0; k < 3; k++)
            {
                double d = dst[o * 4 + k], c = src[s * 4 + k];
                d = mode switch
                {
                    // Silhouette pre-scale d*(1 - a*(1-D)), then Plus adds c*a*S.
                    BlendMode.Add => d * (1 - a * (1 - dstW)) + c * a * srcW,
                    // Same pre-scale, then Multiply by the inverted color at a*S: d*(1 - a*S*c).
                    BlendMode.Subtract => d * (1 - a * (1 - dstW)) * (1 - a * srcW * c / 255),
                    // Multiply by the gray inverted copy at a*D, then Plus adds c*a*S.
                    BlendMode.SubAdd => d * (1 - a * dstW * gray) + c * a * srcW,
                    _ => c * a + d * (1 - a),
                };
                dst[o * 4 + k] = (byte)Math.Clamp(Math.Round(d), 0, 255);
            }
        });
        return dst;
    }

    /// <summary>The Animations canvas background as it shows at zoom = scale with no pan: base color, then the
    /// checkerboard (from the axis when it follows zoom or pan, else from the top-left) or the image. Always opaque.</summary>
    private byte[] ExportBackground(int width, int height, int left, int top, int scale)
    {
        var dst = new byte[width * height * 4];
        void Over(int i, byte b, byte g, byte r, double a)
        {
            dst[i] = (byte)Math.Round(b * a + dst[i] * (1 - a));
            dst[i + 1] = (byte)Math.Round(g * a + dst[i + 1] * (1 - a));
            dst[i + 2] = (byte)Math.Round(r * a + dst[i + 2] * (1 - a));
        }

        var baseColor = (AnimCanvas.Background as ISolidColorBrush)?.Color ?? Colors.Black;
        for (int i = 0; i < dst.Length; i += 4)
            (dst[i], dst[i + 1], dst[i + 2], dst[i + 3]) = (baseColor.B, baseColor.G, baseColor.R, 255);

        if (AnimCanvas.Checkerboard && AnimCanvas.CheckerDark is ISolidColorBrush dark)
        {
            // As SpriteView at zoom = scale with no pan, where the canvas center is the axis.
            bool follow = AnimCanvas.CheckerFollowZoom || AnimCanvas.CheckerFollowPan;
            int size = Math.Clamp(AnimCanvas.CheckerSize, 1, 512) * (AnimCanvas.CheckerFollowZoom ? scale : 1);
            int ax = follow ? -left * scale : 0, ay = follow ? -top * scale : 0;
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    if (((int)Math.Floor((x - ax) / (double)size) + (int)Math.Floor((y - ay) / (double)size)) % 2 == 0)
                        Over((y * width + x) * 4, dark.Color.B, dark.Color.G, dark.Color.R, dark.Color.A / 255.0 * dark.Opacity);
        }

        if (AnimCanvas.BackgroundImage is { Image: WriteableBitmap img } bg)
        {
            int iw = img.PixelSize.Width, ih = img.PixelSize.Height;
            var pixels = new byte[iw * ih * 4];
            using (var fb = img.Lock())
                for (int y = 0; y < ih; y++)
                    System.Runtime.InteropServices.Marshal.Copy(fb.Address + y * fb.RowBytes, pixels, y * iw * 4, iw * 4);

            // Same layout as SpriteView.DrawBackgroundImage: centered on the axis, offset at 100%, then zoomed.
            double z = bg.FollowZoom ? scale : 1;
            double rx = -left * scale + (bg.Offset.X - Math.Floor(iw / 2.0)) * z;
            double ry = -top * scale + (bg.Offset.Y - Math.Floor(ih / 2.0)) * z;
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int u = (int)Math.Floor((x + 0.5 - rx) / z), v = (int)Math.Floor((y + 0.5 - ry) / z);
                    if (bg.Tile) { u = ((u % iw) + iw) % iw; v = ((v % ih) + ih) % ih; }
                    else if (u < 0 || v < 0 || u >= iw || v >= ih) continue;
                    int s = (v * iw + u) * 4;
                    Over((y * width + x) * 4, pixels[s], pixels[s + 1], pixels[s + 2], pixels[s + 3] / 255.0 * bg.Opacity);
                }
        }
        return dst;
    }

    /// <summary>The frame's boxes in the plain Clsn1/Clsn2 colors, whether own or inherited (no default colors).</summary>
    private static List<BoxOverlay> GifBoxes(AnimationElement element)
    {
        var boxes = BoxesFor(element);
        foreach (var b in boxes) b.Color = ClsnStyle.Current.For(b.Tag is ClsnKind.Attack ? ClsnKind.Attack : ClsnKind.Hurt, own: true);
        return boxes;
    }

    /// <summary>Draws each box as an outline of the Clsn line thickness inside its edges, in list order (Clsn2 then Clsn1 on top).</summary>
    private static void DrawGifBoxes(byte[] dst, List<BoxOverlay> boxes, int left, int top, int width, int height, int scale)
    {
        void Put(int x, int y, Color c)
        {
            if (x < 0 || y < 0 || x >= width || y >= height) return;
            int i = (y * width + x) * 4;
            (dst[i], dst[i + 1], dst[i + 2], dst[i + 3]) = (c.B, c.G, c.R, 255);
        }
        int thickness = Math.Max(1, (int)(ClsnStyle.Current.LineThickness * (ClsnStyle.Current.ScaleWithZoom ? scale : 1)));
        foreach (var b in boxes)
        {
            int x1 = (Math.Min(b.X1, b.X2) - left) * scale, x2 = (Math.Max(b.X1, b.X2) - left) * scale - 1;
            int y1 = (Math.Min(b.Y1, b.Y2) - top) * scale, y2 = (Math.Max(b.Y1, b.Y2) - top) * scale - 1;
            if (x2 < x1 || y2 < y1) continue;
            // Each pass is one pixel further in, stopping where the sides meet.
            for (int k = 0; k < thickness && x1 + k <= x2 - k && y1 + k <= y2 - k; k++)
            {
                for (int x = x1 + k; x <= x2 - k; x++) { Put(x, y1 + k, b.Color); Put(x, y2 - k, b.Color); }
                for (int y = y1 + k; y <= y2 - k; y++) { Put(x1 + k, y, b.Color); Put(x2 - k, y, b.Color); }
            }
        }
    }
}
