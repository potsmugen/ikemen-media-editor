using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Editor;

/// <summary>
/// Monospace .air editor with a translucent band behind the current frame's
/// lines. A stock TextBox has no line-background API, so the band is an overlay
/// positioned from the inner TextPresenter's caret coordinates.
/// </summary>
public sealed class ActionTextView : UserControl
{
    private readonly TextBox _box;
    private readonly Border _band;
    private int _highlightStart = -1;
    private int _highlightLength;
    private bool _suppressCaret;

    public ActionTextView()
    {
        _band = new Border
        {
            IsHitTestVisible = false,
            IsVisible = false,
            Background = new SolidColorBrush(Color.FromArgb(0x55, 0x3a, 0x7a, 0xd0)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xc0, 0x5a, 0x9a, 0xf0)),
            BorderThickness = new Thickness(3, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };

        _box = new TextBox
        {
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = Dialogs.MonoFont,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Watermark = "Select an action to edit its .air text",
        };
        _box.Bind(TextBox.FontSizeProperty, _box.GetResourceObservable("UiFont14")); // follows Preferences' font size
        // No UpdateBand here: the presenter's layout still holds the old text until the
        // next layout pass (LayoutUpdated below redraws the band then).
        _box.TextChanged += (_, _) => TextEdited?.Invoke();
        _box.LostFocus += (_, _) => EditFinished?.Invoke();
        _box.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.CaretIndexProperty && !_suppressCaret)
                CaretMoved?.Invoke(CaretIndex);
        };
        _box.LayoutUpdated += (_, _) => UpdateBand();

        ClipToBounds = true;
        Content = new Grid
        {
            Children =
            {
                _box,
                new Canvas { IsHitTestVisible = false, Children = { _band } },
            },
        };
    }

    public event Action? TextEdited;
    public event Action? EditFinished;
    public event Action<int>? CaretMoved;

    public string Text
    {
        get => _box.Text ?? "";
        set => _box.Text = value;
    }

    public int CaretIndex
    {
        get => _box.CaretIndex;
        set
        {
            _suppressCaret = true;
            _box.CaretIndex = Math.Clamp(value, 0, Text.Length);
            _suppressCaret = false;
        }
    }

    /// <summary>Character span of the current frame, or start &lt; 0 to hide the band.</summary>
    public void SetHighlight(int start, int length, bool moveCaret)
    {
        _highlightStart = start;
        _highlightLength = length;
        if (moveCaret && start >= 0)
        {
            CaretIndex = start;
        }
        UpdateBand();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ForegroundProperty)
            _box.Foreground = Foreground;
        else if (change.Property == BackgroundProperty)
            _box.Background = Brushes.Transparent;
    }

    private void UpdateBand()
    {
        if (_highlightStart < 0 || _highlightLength <= 0 || GetPresenter() is not { } presenter)
        {
            _band.IsVisible = false;
            return;
        }

        // Clamp to the text the layout was built from (it can lag Text), and drop
        // trailing line breaks so the range never ends on the next line.
        var layout = presenter.TextLayout;
        var lines = layout.TextLines;
        int laidOut = Math.Min(Text.Length, lines.Sum(l => l.Length));
        int start = Math.Clamp(_highlightStart, 0, laidOut);
        int length = Math.Clamp(_highlightLength, 0, laidOut - start);
        while (length > 0 && Text[start + length - 1] is '\r' or '\n') length--;
        if (length <= 0 || lines.Count == 0)
        {
            _band.IsVisible = false;
            return;
        }

        // Top and bottom from the line list itself. Not HitTestTextRange: Avalonia 11.3's
        // version can step onto the next line with 0 characters left and throw from
        // TextLineImpl.GetTextBounds (textLength must be non-zero).
        int first = layout.GetLineIndexFromCharacterIndex(start, false);
        int last = layout.GetLineIndexFromCharacterIndex(start + length - 1, false);
        double top = 0;
        for (int k = 0; k < first; k++) top += lines[k].Height;
        double bottom = top;
        for (int k = first; k <= last && k < lines.Count; k++) bottom += lines[k].Height;

        var origin = presenter.TranslatePoint(new Point(0, top), this);
        if (origin is null)
        {
            _band.IsVisible = false;
            return;
        }

        Canvas.SetLeft(_band, 0);
        Canvas.SetTop(_band, origin.Value.Y);
        _band.Width = Math.Max(0, Bounds.Width);
        _band.Height = Math.Max(0, bottom - top);
        _band.IsVisible = _band.Height > 0;
    }

    /// <summary>Named GetPresenter, not Presenter, to avoid hiding ContentControl.Presenter (CS0108).</summary>
    private TextPresenter? GetPresenter()
    {
        _box.ApplyTemplate();
        return _box.GetVisualDescendants().OfType<TextPresenter>().FirstOrDefault();
    }
}
