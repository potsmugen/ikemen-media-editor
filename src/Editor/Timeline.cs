using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Editor;

/// <summary>
/// A horizontal strip showing one action's frames, sized proportionally to
/// their duration, with a playhead. Clicking jumps to that frame. A frame
/// with Clsn boxes is filled in that Clsn's color (top/bottom split when it
/// has both), using whichever color — own or the action's default — the
/// frame is actually showing; a holding frame keeps a border in the hold
/// color so that status isn't lost under a Clsn fill. The selected frame
/// keeps its color coding and is marked by an outline around its cell
/// instead of having its fill painted over.
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
    // Distinct from every cell hue (Clsn red/blue, defaults orange/teal, hold
    // green) and from the white playhead — the chevron is a fixed landmark,
    // the playhead a moving line, and they can sit on the same cell.
    private static readonly IBrush LoopMarker = new SolidColorBrush(Color.FromRgb(0xc0, 0x70, 0xd8));

    private IReadOnlyList<Frame> _frames = [];
    private int _selected = -1;
    private int _playTick = -1;

    /// <summary>Raised with the frame index the user clicked.</summary>
    public event Action<int>? FrameClicked;

    public Timeline() { Focusable = true; ClipToBounds = true; }

    public void Show(IReadOnlyList<Frame> frames, int selected)
    {
        _frames = frames;
        _selected = selected;
        InvalidateVisual();
    }

    /// <summary>Moves the playhead to a tick position during playback, or -1 to hide it.</summary>
    public void SetPlayTick(int tick)
    {
        _playTick = tick;
        InvalidateVisual();
    }

    private double TotalTicks => Math.Max(1, _frames.Where(f => f.Duration > 0).Sum(f => f.Duration));

    /// <summary>The screen x where each frame starts, plus one trailing entry for the end.</summary>
    private double[] Boundaries()
    {
        var xs = new double[_frames.Count + 1];
        double w = Bounds.Width, total = TotalTicks, at = 0;
        for (int i = 0; i < _frames.Count; i++)
        {
            xs[i] = at / total * w;
            at += Math.Max(_frames[i].Duration, 1); // a holding frame still gets a visible sliver
        }
        xs[^1] = w;
        return xs;
    }

    public override void Render(DrawingContext ctx)
    {
        var bounds = new Rect(Bounds.Size);
        ctx.FillRectangle(Track, bounds);
        if (_frames.Count == 0) return;

        var xs = Boundaries();
        for (int i = 0; i < _frames.Count; i++)
        {
            var frame = _frames[i];
            double x0 = xs[i], x1 = Math.Max(xs[i + 1], x0 + 1);
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

        // The loop chevron sits on top of cell fills and selection outlines —
        // it names a position in the sequence, which matters even (especially)
        // when that cell is the selected one.
        for (int i = 0; i < _frames.Count; i++)
        {
            if (!_frames[i].Loop) continue;
            double x0 = xs[i];
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
            double x = _playTick / TotalTicks * bounds.Width;
            ctx.DrawLine(Playhead, new Point(x, 0), new Point(x, bounds.Height));
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var p = e.GetCurrentPoint(this);
        if (!p.Properties.IsLeftButtonPressed) return; // right-click falls through to the native ContextRequested event
        if (_frames.Count == 0) return;

        var xs = Boundaries();
        double x = p.Position.X;
        int frame = 0;
        for (int i = 0; i < _frames.Count; i++)
            if (x >= xs[i]) frame = i;
        FrameClicked?.Invoke(frame);
    }
}
