using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Editor;

/// <summary>
/// A horizontal strip showing one action's frames, sized proportionally to
/// their duration, with a playhead. Clicking jumps to the start of that frame;
/// dragging scrubs to the exact tick under the pointer. A frame
/// with Clsn boxes is filled in that Clsn's color (top/bottom split when it
/// has both), using whichever color — own or the action's default — the
/// frame is actually showing; a holding frame keeps a border in the hold
/// color so that status isn't lost under a Clsn fill. The selected frame
/// keeps its color coding and is marked by an outline around its cell
/// instead of having its fill painted over.
/// Zoom works like WaveformView: the whole action fits until zoomed; the wheel
/// zooms around the cursor, Shift+wheel scrolls. Tick marks show once ticks are
/// wide enough. Holding and zero-length frames take one tick of width.
/// </summary>
public class Timeline : Control
{
    /// <summary>One frame's duration plus the color it should show for each Clsn kind it has (own or inherited), or null if it has none.
    /// Loop marks the frame the action replays from — drawn as a chevron on the cell's left edge.</summary>
    public readonly record struct Frame(int Duration, Color? AttackColor, Color? HurtColor, bool Loop = false);

    /// <summary>Backdrop color; kept in step with the app theme (see MainWindow.ApplyTheme).</summary>
    public IBrush Track { get; set; } = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x1e));
    private static readonly IBrush Cell = new SolidColorBrush(Color.FromRgb(0x3a, 0x3a, 0x48));
    private static readonly IBrush CellAlt = new SolidColorBrush(Color.FromRgb(0x32, 0x32, 0x3e));
    // Selection outline: a black under-stroke with a white core, so it reads
    // on top of every fill the cell can have (Clsn red/blue/teal/orange, hold
    // green, the plain cell shades) without replacing any of them.
    private static readonly IPen SelectedOuter = new Pen(Brushes.Black, 3);
    private static readonly IPen SelectedInner = new Pen(Brushes.White, 1);
    private static readonly Color HoldColor = Color.FromRgb(0x50, 0x90, 0x60);
    private static readonly IBrush Hold = new SolidColorBrush(HoldColor);
    private static readonly IPen HoldBorder = new Pen(new SolidColorBrush(HoldColor), 2);
    private static readonly IPen Playhead = new Pen(Brushes.White, 2);
    // Darkens whatever cell fill it crosses, like the selection outline's black under-stroke.
    private static readonly IPen TickMark = new Pen(new SolidColorBrush(Color.FromArgb(0x70, 0, 0, 0)), 1);
    /// <summary>Loopstart chevron, the theme's Accent (see MainWindow.ApplyTheme), like the frame table's L.</summary>
    public IBrush LoopMarker { get; set; } = new SolidColorBrush(Color.FromRgb(0xc0, 0x70, 0xd8));

    private IReadOnlyList<Frame> _frames = [];
    private int _selected = -1;
    private int _playTick = -1;

    private double? _pixelsPerTick; // null = fit the whole action
    private double _viewStart;      // first visible tick of width, in layout ticks

    private double? _pressX;
    private bool _dragging;

    /// <summary>Deepest zoom, in pixels per tick (settings.json TimelineMaxPixelsPerTick).</summary>
    public double MaxPixelsPerTick { get; set; } = Settings.DefaultTimelineMaxPixelsPerTick;
    /// <summary>Zoom factor per wheel notch (settings.json TimelineZoomStep).</summary>
    public double ZoomStep { get; set; } = Settings.DefaultTimelineZoomStep;
    /// <summary>Shift+wheel scroll per notch, as a fraction of the visible width (settings.json TimelineScrollStep).</summary>
    public double ScrollStep { get; set; } = Settings.DefaultTimelineScrollStep;
    /// <summary>Pixels the pointer must move before a press counts as a drag (settings.json TimelineDragThresholdPixels).</summary>
    public double DragThreshold { get; set; } = Settings.DefaultTimelineDragThresholdPixels;
    /// <summary>Tick marks show from this many pixels per tick (settings.json TimelineTickMarkMinPixels).</summary>
    public double TickMarkMinPixels { get; set; } = Settings.DefaultTimelineTickMarkMinPixels;

    /// <summary>Raised with the frame index the user clicked.</summary>
    public event Action<int>? FrameClicked;
    /// <summary>Raised while dragging with the action tick under the pointer.</summary>
    public event Action<int>? TickScrubbed;
    /// <summary>Raised when zoom, scroll position or the frames change, so a scrollbar can follow.</summary>
    public event Action? ViewChanged;

    public Timeline() { Focusable = true; ClipToBounds = true; }

    /// <summary>Layout ticks: real durations, with holding and zero-length frames one tick wide.</summary>
    public double TotalTicks => Math.Max(1, _frames.Sum(f => Math.Max(f.Duration, 1)));
    private double Width1 => Math.Max(1, Bounds.Width);
    private double FitPixelsPerTick => Width1 / TotalTicks;
    private double PixelsPerTick => _pixelsPerTick ?? FitPixelsPerTick;
    public double VisibleTicks => Width1 / PixelsPerTick;

    /// <summary>First visible layout tick. Setting it scrolls (clamped).</summary>
    public double ViewStart
    {
        get => _viewStart;
        set => SetView(_pixelsPerTick, value);
    }

    /// <summary>New frames keep the zoom and position, clamped; call ResetView for another action.</summary>
    public void Show(IReadOnlyList<Frame> frames, int selected)
    {
        _frames = frames;
        _selected = selected;
        SetView(_pixelsPerTick, _viewStart);
    }

    /// <summary>Back to fitting the whole action.</summary>
    public void ResetView() => SetView(null, 0);

    /// <summary>Moves the playhead to an action tick, or -1 to hide it. A moved playhead is scrolled into view.</summary>
    public void SetPlayTick(int tick)
    {
        bool moved = tick != _playTick;
        _playTick = tick;
        if (moved && tick >= 0)
        {
            double at = LayoutTickOf(tick), visible = VisibleTicks;
            if (at < _viewStart || at >= _viewStart + visible)
                SetView(_pixelsPerTick, at - visible * 0.1);
        }
        InvalidateVisual();
    }

    private double ClampPixelsPerTick(double ppt)
    {
        double fit = FitPixelsPerTick;
        return Math.Clamp(ppt, fit, Math.Max(MaxPixelsPerTick, fit));
    }

    private void SetView(double? ppt, double start)
    {
        if (ppt is { } z)
        {
            z = ClampPixelsPerTick(z);
            ppt = z <= FitPixelsPerTick ? null : z; // zoomed all the way out = fit, which then follows resizes
        }
        _pixelsPerTick = ppt;
        _viewStart = Math.Clamp(start, 0, Math.Max(0, TotalTicks - VisibleTicks));
        InvalidateVisual();
        ViewChanged?.Invoke();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        SetView(_pixelsPerTick, _viewStart);
    }

    private double XOf(double layoutTick) => (layoutTick - _viewStart) * PixelsPerTick;
    private double LayoutTickAt(double x) => _viewStart + x / PixelsPerTick;

    /// <summary>Layout tick where each frame starts, plus one trailing entry for the end.</summary>
    private double[] Starts()
    {
        var starts = new double[_frames.Count + 1];
        double at = 0;
        for (int i = 0; i < _frames.Count; i++)
        {
            starts[i] = at;
            at += Math.Max(_frames[i].Duration, 1); // a holding frame still gets a visible sliver
        }
        starts[^1] = at;
        return starts;
    }

    /// <summary>Action tick to layout tick; a hold that has been reached parks at its start.</summary>
    private double LayoutTickOf(int tick)
    {
        int at = 0;
        double layout = 0;
        foreach (var f in _frames)
        {
            if (f.Duration < 0) return layout;
            if (f.Duration > 0 && tick < at + f.Duration) return layout + (tick - at);
            at += Math.Max(f.Duration, 0);
            layout += Math.Max(f.Duration, 1);
        }
        return layout;
    }

    /// <summary>Frame under a layout tick, clamped to the frames.</summary>
    private int FrameAtLayout(double layoutTick, double[] starts)
    {
        int frame = 0;
        for (int i = 0; i < _frames.Count; i++)
            if (layoutTick >= starts[i]) frame = i;
        return frame;
    }

    /// <summary>Action tick under x: exact inside a frame, the frame's start on a hold or zero-length frame.</summary>
    private int TickAt(double x)
    {
        var starts = Starts();
        double layout = Math.Clamp(LayoutTickAt(x), 0, starts[^1]);
        int frame = FrameAtLayout(layout, starts);
        int at = 0;
        for (int i = 0; i < frame; i++) at += Math.Max(_frames[i].Duration, 0);
        int d = _frames[frame].Duration;
        return d > 0 ? at + Math.Clamp((int)Math.Floor(layout - starts[frame]), 0, d - 1) : at;
    }

    public override void Render(DrawingContext ctx)
    {
        var bounds = new Rect(Bounds.Size);
        ctx.FillRectangle(Track, bounds);
        if (_frames.Count == 0) return;

        var starts = Starts();
        var xs = starts.Select(XOf).ToArray();
        for (int i = 0; i < _frames.Count; i++)
        {
            var frame = _frames[i];
            double x0 = xs[i], x1 = Math.Max(xs[i + 1], x0 + 1);
            if (x1 < 0 || x0 > bounds.Width) continue;
            var r = new Rect(x0, 2, x1 - x0 - 1, bounds.Height - 4);
            bool hold = frame.Duration < 0;

            if (frame.AttackColor is { } ac && frame.HurtColor is { } hc)
            {
                double half = r.Height / 2;
                ctx.FillRectangle(new SolidColorBrush(ac), new Rect(r.X, r.Y, r.Width, half));
                ctx.FillRectangle(new SolidColorBrush(hc), new Rect(r.X, r.Y + half, r.Width, r.Height - half));
            }
            else if (frame.AttackColor is { } a) ctx.FillRectangle(new SolidColorBrush(a), r);
            else if (frame.HurtColor is { } h) ctx.FillRectangle(new SolidColorBrush(h), r);
            else ctx.FillRectangle(hold ? Hold : (i % 2 == 0 ? Cell : CellAlt), r);

            // A holding frame whose fill got taken over by a Clsn color still gets marked, just as a border instead.
            if (hold && (frame.AttackColor is not null || frame.HurtColor is not null))
                ctx.DrawRectangle(HoldBorder, r);

            // The selected frame gets an outline just inside its edge, over
            // whatever fill it has, rather than the fill being replaced.
            if (i == _selected)
            {
                var sr = new Rect(r.X + 2, r.Y + 2, Math.Max(1, r.Width - 4), Math.Max(1, r.Height - 4));
                ctx.DrawRectangle(SelectedOuter, sr);
                ctx.DrawRectangle(SelectedInner, sr);
            }
        }

        if (PixelsPerTick >= TickMarkMinPixels) DrawTickMarks(ctx, bounds, starts);

        // The loop chevron sits on top of cell fills and selection outlines —
        // it names a position in the sequence, which matters even (especially)
        // when that cell is the selected one.
        for (int i = 0; i < _frames.Count; i++)
        {
            if (!_frames[i].Loop) continue;
            double x0 = xs[i];
            if (x0 < -10 || x0 > bounds.Width) continue;
            double size = Math.Min(10, Math.Max(xs[i + 1] - x0 - 2, 4));
            double y = bounds.Height / 2;
            var tri = new StreamGeometry();
            using (var g = tri.Open())
            {
                g.BeginFigure(new Point(x0 + 1, y - size / 2), true);
                g.LineTo(new Point(x0 + 1, y + size / 2), true);
                g.LineTo(new Point(x0 + 1 + size, y), true);
                g.EndFigure(true);
            }
            ctx.DrawGeometry(LoopMarker, null, tri);
        }

        if (_playTick >= 0)
        {
            double x = XOf(LayoutTickOf(_playTick));
            ctx.DrawLine(Playhead, new Point(x, 0), new Point(x, bounds.Height));
        }
    }

    /// <summary>A short mark inside each frame at every tick.</summary>
    private void DrawTickMarks(DrawingContext ctx, Rect bounds, double[] starts)
    {
        double bottom = bounds.Height - 2, len = (bottom - 2) / 4;
        for (int i = 0; i < _frames.Count; i++)
        {
            int d = _frames[i].Duration;
            if (d <= 0 || XOf(starts[i + 1]) < 0 || XOf(starts[i]) > bounds.Width) continue;
            for (int k = 1; k < d; k++) // k = 0 is the cell's own edge
            {
                double x = Math.Round(XOf(starts[i] + k)) + 0.5;
                if (x < 0 || x > bounds.Width) continue;
                ctx.DrawLine(TickMark, new Point(x, bottom - len), new Point(x, bottom));
            }
        }
    }

    /// <summary>Zooms keeping the tick under x where it is.</summary>
    private void ZoomAt(double factor, double x)
    {
        double anchor = LayoutTickAt(x);
        double ppt = ClampPixelsPerTick(PixelsPerTick * factor);
        SetView(ppt, anchor - x / ppt);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_frames.Count == 0) return;
        double dx = e.Delta.X, dy = e.Delta.Y;
        bool shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;
        if (shift || (dx != 0 && dy == 0)) // some platforms turn Shift+wheel into a horizontal delta
            SetView(_pixelsPerTick, _viewStart - (dx != 0 ? dx : dy) * ScrollStep * VisibleTicks);
        else if (dy != 0)
            ZoomAt(Math.Pow(ZoomStep, dy), e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var p = e.GetCurrentPoint(this);
        if (!p.Properties.IsLeftButtonPressed) return; // right-click falls through to the native ContextRequested event
        if (_frames.Count == 0) return;

        _pressX = p.Position.X;
        _dragging = false;
        e.Pointer.Capture(this);
        e.Handled = true;
        FrameClicked?.Invoke(FrameAtLayout(LayoutTickAt(p.Position.X), Starts()));
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_pressX is not { } pressX || _frames.Count == 0) return;
        double x = e.GetPosition(this).X;
        if (!_dragging && Math.Abs(x - pressX) < DragThreshold) return;
        _dragging = true;
        TickScrubbed?.Invoke(TickAt(x));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_pressX is null) return;
        _pressX = null;
        _dragging = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _pressX = null;
        _dragging = false;
    }
}
