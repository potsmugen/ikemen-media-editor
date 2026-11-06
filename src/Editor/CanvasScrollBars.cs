using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;

namespace Editor;

/// <summary>Overlay scroll bars that pan a SpriteView; a bar is disabled while the whole image is visible.</summary>
public sealed class CanvasScrollBars : Panel
{
    public static readonly StyledProperty<SpriteView?> TargetProperty =
        AvaloniaProperty.Register<CanvasScrollBars, SpriteView?>(nameof(Target));

    public SpriteView? Target { get => GetValue(TargetProperty); set => SetValue(TargetProperty, value); }

    private readonly ScrollBar _h = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
    private readonly ScrollBar _v = new() { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Right };
    private ScrollBar? _pressed; // a pressed bar stays enabled, or panning the image into view would disable it mid-drag
    private bool _updating;

    static CanvasScrollBars()
    {
        TargetProperty.Changed.AddClassHandler<CanvasScrollBars>((bars, e) =>
        {
            if (e.OldValue is SpriteView old) old.ScrollInfoChanged -= bars.Refresh;
            if (e.NewValue is SpriteView view) view.ScrollInfoChanged += bars.Refresh;
            bars.Refresh();
        });
    }

    public CanvasScrollBars()
    {
        Children.Add(_h);
        Children.Add(_v);
        Hook(_h, true);
        Hook(_v, false);
        // Leave the corner empty where the bars meet.
        _h.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty) _v.Margin = new Thickness(0, 0, 0, _h.Bounds.Height);
        };
        _v.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty) _h.Margin = new Thickness(0, 0, _v.Bounds.Width, 0);
        };
    }

    private void Hook(ScrollBar bar, bool horizontal)
    {
        bar.ValueChanged += (_, e) =>
        {
            if (!_updating) Target?.ScrollTo(horizontal, e.NewValue);
        };
        bar.AddHandler(PointerPressedEvent, (_, _) => _pressed = bar, Avalonia.Interactivity.RoutingStrategies.Bubble, true);
        bar.AddHandler(PointerReleasedEvent, (_, _) =>
        {
            if (_pressed != bar) return;
            _pressed = null;
            Refresh();
        }, Avalonia.Interactivity.RoutingStrategies.Bubble, true);
    }

    private void Refresh()
    {
        Apply(_h, true);
        Apply(_v, false);
    }

    private void Apply(ScrollBar bar, bool horizontal)
    {
        var a = Target?.GetScrollAxis(horizontal) ?? default;
        _updating = true;
        try
        {
            bar.Minimum = a.Min;
            bar.Maximum = a.Max;
            bar.ViewportSize = a.Viewport;
            bar.SmallChange = a.Viewport / 10;
            bar.LargeChange = a.Viewport * 0.9;
            bar.Value = Math.Clamp(a.Value, a.Min, a.Max);
            bar.IsEnabled = a.Needed || _pressed == bar;
        }
        finally { _updating = false; }
    }
}
