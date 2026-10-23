using Air;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Sff;

namespace Editor;

/// <summary>Export GIF on the Animations playback bar: one pass of the current action as a looping animated GIF.</summary>
public partial class MainWindow
{
    private static readonly FilePickerFileType GifType = new("GIF images") { Patterns = ["*.gif"] };

    /// <summary>One sprite placed on the GIF canvas: world = M·(pixel − axis) + offset, M = [A C; B D].</summary>
    private readonly record struct GifPlacement(Sprite Sprite, byte[] Bgra, double A, double B, double C, double D, double Ox, double Oy);

    private async void OnExportGif(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_action is not { Elements.Count: > 0 } action || _file is null)
            {
                StatusText.Text = "Choose an action with frames first.";
                return;
            }

            int scaleMax = Settings.Checked(nameof(Settings.AnimationsSection.GifScaleMax), _settings.Animations.GifScaleMax, Settings.DefaultGifScaleMax, v => v >= 1);
            int scaleDefault = Settings.Checked(nameof(Settings.AnimationsSection.GifScale), _settings.Animations.GifScale, Settings.DefaultGifScale, v => v >= 1 && v <= scaleMax);
            int maxSide = Settings.Checked(nameof(Settings.AnimationsSection.GifMaxSide), _settings.Animations.GifMaxSide, Settings.DefaultGifMaxSide, v => v is >= 1 and <= 65535);
            if (await Dialogs.PromptGifExport(this, scaleDefault, scaleMax) is not { } choice) return;
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
                StatusText.Text = $"Can't export: the GIF would be {width}x{height} pixels (up to {maxSide} a side).";
                return;
            }

            var file = await PickSave(new FilePickerSaveOptions
            {
                Title = "Export Animation as GIF",
                SuggestedFileName = $"action{action.Number}.gif",
                DefaultExtension = "gif",
                FileTypeChoices = [GifType],
            });
            if (file?.TryGetLocalPath() is not { } path) return;

            // 60 ticks a second; GIF delays are in 1/100 s and players stretch anything under 2,
            // so each delay is at least 2 and later ones catch up to keep the total time right.
            var frames = new List<GifFrame>();
            double target = 0;
            int emitted = 0;
            for (int i = 0; i < runs.Count; i++)
            {
                target += runs[i].Ticks * 100.0 / 60;
                int delay = Math.Max(2, (int)Math.Round(target - emitted));
                emitted += delay;
                var pixels = RenderGifFrame(placements[i], left, top, (int)width, (int)height, scale);
                DrawGifBoxes(pixels, boxes[i], left, top, (int)width, (int)height, scale);
                frames.Add(new GifFrame(pixels, delay));
            }

            var bytes = await Task.Run(() => Gif.Encode((int)width, (int)height, frames));
            Backups.Make(path, _settings.Files.BackupCount);
            await File.WriteAllBytesAsync(path, bytes);
            CrashLog.Trace($"[anim] gif action {action.Number}: {frames.Count} frames, {width}x{height}, x{scale}, boxes {choice.Boxes}, {emitted} cs");
            StatusText.Text = $"Wrote {Path.GetFileName(path)}: {frames.Count} frames, {width}x{height}, {emitted / 100.0:0.##}s.";
        }
        catch (Exception ex)
        {
            await Failed("OnExportGif", ex);
        }
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

    /// <summary>Draws one frame onto a transparent canvas, nearest-neighbor, scale output pixels per world pixel. Blends draw as normal: a GIF has no additive mode.</summary>
    private static byte[] RenderGifFrame(GifPlacement? placement, int left, int top, int width, int height, int scale)
    {
        var dst = new byte[width * height * 4];
        if (placement is not { } p) return dst;
        double det = p.A * p.D - p.C * p.B;
        int w = p.Sprite.Width, h = p.Sprite.Height;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                double dx = left + (x + 0.5) / scale - p.Ox, dy = top + (y + 0.5) / scale - p.Oy;
                int px = (int)Math.Floor((p.D * dx - p.C * dy) / det + p.Sprite.AxisX);
                int py = (int)Math.Floor((-p.B * dx + p.A * dy) / det + p.Sprite.AxisY);
                if (px < 0 || py < 0 || px >= w || py >= h) continue;
                Array.Copy(p.Bgra, (py * w + px) * 4, dst, (y * width + x) * 4, 4);
            }
        return dst;
    }

    /// <summary>The frame's boxes in the plain Clsn1/Clsn2 colors, whether own or inherited (no default colors).</summary>
    private static List<BoxOverlay> GifBoxes(AnimationElement element)
    {
        var boxes = BoxesFor(element);
        foreach (var b in boxes) b.Color = b.Tag is ClsnKind.Attack ? AttackColor : HurtColor;
        return boxes;
    }

    /// <summary>Draws each box as a 1 px outline in its canvas color, in list order (Clsn2 then Clsn1 on top).</summary>
    private static void DrawGifBoxes(byte[] dst, List<BoxOverlay> boxes, int left, int top, int width, int height, int scale)
    {
        void Put(int x, int y, Color c)
        {
            if (x < 0 || y < 0 || x >= width || y >= height) return;
            int i = (y * width + x) * 4;
            (dst[i], dst[i + 1], dst[i + 2], dst[i + 3]) = (c.B, c.G, c.R, 255);
        }
        foreach (var b in boxes)
        {
            int x1 = (Math.Min(b.X1, b.X2) - left) * scale, x2 = (Math.Max(b.X1, b.X2) - left) * scale - 1;
            int y1 = (Math.Min(b.Y1, b.Y2) - top) * scale, y2 = (Math.Max(b.Y1, b.Y2) - top) * scale - 1;
            if (x2 < x1 || y2 < y1) continue;
            for (int x = x1; x <= x2; x++) { Put(x, y1, b.Color); Put(x, y2, b.Color); }
            for (int y = y1; y <= y2; y++) { Put(x1, y, b.Color); Put(x2, y, b.Color); }
        }
    }
}
