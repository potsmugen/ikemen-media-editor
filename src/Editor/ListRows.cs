using System.ComponentModel;
using System.Globalization;
using Air;
using Sff;
using Snd;

namespace Editor;

// Row models the list templates bind to (compiled bindings: x:DataType needs top-level types).

/// <summary>One row's worth of what the ItemTemplate binds to. Every cell that an edit can
/// change raises PropertyChanged, so a row can be refreshed where it lies instead of being
/// rebuilt — see RebuildSpriteTable.</summary>
public sealed class SpriteTableRow(Sprite sprite, int visibleRow, int spriteIndex) : INotifyPropertyChanged
{
    private static readonly PropertyChangedEventArgs GroupTextChanged = new(nameof(GroupText));
    private static readonly PropertyChangedEventArgs NumberTextChanged = new(nameof(NumberText));
    private static readonly PropertyChangedEventArgs XTextChanged = new(nameof(XText));
    private static readonly PropertyChangedEventArgs YTextChanged = new(nameof(YText));
    private static readonly PropertyChangedEventArgs FlagsChanged = new(nameof(Flags));
    private static readonly PropertyChangedEventArgs PaletteTextChanged = new(nameof(PaletteText));
    private static readonly PropertyChangedEventArgs WidthsChanged = new(nameof(Widths));

    public event PropertyChangedEventHandler? PropertyChanged;

    private string _groupText = sprite.Group.ToString(CultureInfo.InvariantCulture);
    private string _numberText = sprite.Index.ToString(CultureInfo.InvariantCulture);
    private string _xText = sprite.AxisX.ToString(CultureInfo.InvariantCulture);
    private string _yText = sprite.AxisY.ToString(CultureInfo.InvariantCulture);
    private string _flags = "";
    private string _paletteText = "";
    private double[] _widths = [];

    public Sprite Sprite { get; } = sprite;
    public int VisibleRow { get; } = visibleRow;
    /// <summary>The sprite's 1-based position in the SFF file: its index, independent of any filter
    /// or sort, so it stays meaningful as the visible set changes. Not the same as Sprite.Index,
    /// the sprite's in-game number within its group.</summary>
    public string Number { get; } = (spriteIndex + 1).ToString(CultureInfo.InvariantCulture);
    public string GroupText { get => _groupText; private set => Set(ref _groupText, value, GroupTextChanged); }
    public string NumberText { get => _numberText; private set => Set(ref _numberText, value, NumberTextChanged); }
    public string XText { get => _xText; private set => Set(ref _xText, value, XTextChanged); }
    public string YText { get => _yText; private set => Set(ref _yText, value, YTextChanged); }
    public string Flags { get => _flags; set => Set(ref _flags, value, FlagsChanged); }
    public string PaletteText { get => _paletteText; set => Set(ref _paletteText, value, PaletteTextChanged); }
    /// <summary>Same array for every row and the header (TextTableWidths).</summary>
    public double[] Widths
    {
        get => _widths;
        set
        {
            // Rebuilt on every edit, but almost always identical; skipping the notification
            // then keeps realized rows from re-reading all seven bindings for nothing.
            bool same = _widths.Length == value.Length;
            for (int i = 0; same && i < value.Length; i++) same = _widths[i] == value[i];
            if (same) return;
            _widths = value;
            PropertyChanged?.Invoke(this, WidthsChanged);
        }
    }

    private void Set(ref string field, string value, PropertyChangedEventArgs args)
    {
        if (field == value) return;
        field = value;
        PropertyChanged?.Invoke(this, args);
    }

    public void UpdateAxes(int x, int y)
    {
        XText = x.ToString(CultureInfo.InvariantCulture);
        YText = y.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Re-reads every cell this row shows from its sprite, raising only what changed.</summary>
    public void Refresh(string flags, string paletteText)
    {
        GroupText = Sprite.Group.ToString(CultureInfo.InvariantCulture);
        NumberText = Sprite.Index.ToString(CultureInfo.InvariantCulture);
        UpdateAxes(Sprite.AxisX, Sprite.AxisY);
        Flags = flags;
        PaletteText = paletteText;
    }
}

/// <summary>One row of the Palettes tab's table: group, index, source ("sff" or
/// import format, plain text), color count and linked palette — same virtualizing-ListBox
/// model as the sprite table.</summary>
internal sealed class PaletteRow : INotifyPropertyChanged
{
    /// <summary>An empty name tells the binding layer every property on this row changed.
    /// Cheaper than tracking a dozen cells that rarely change one at a time.</summary>
    private static readonly PropertyChangedEventArgs AllChanged = new(string.Empty);

    public event PropertyChangedEventHandler? PropertyChanged;

    public required Palette Palette { get; init; }
    public required string Number { get; init; }
    public string NumberText => Number + (Palette.Dirty ? "*" : "");
    public required string GroupText { get; set; }
    public required string IndexText { get; set; }
    public required string GroupToolTip { get; set; }
    public required string IndexToolTip { get; set; }
    public required string SourceText { get; set; }
    public required string ColorCountText { get; set; }
    public required string LinkText { get; set; }
    public required string LinkToolTip { get; set; }
    public required string Kind { get; set; }
    /// <summary>Same array for every row and the header (TextTableWidths).</summary>
    public double[] Widths { get; set; } = [];

    /// <summary>Copies every cell from a freshly built row of the same palette. Number and
    /// Palette are left alone: both follow the row's place in the list, so a changed row set
    /// needs a rebuild rather than a refresh.</summary>
    public void CopyFrom(PaletteRow from)
    {
        GroupText = from.GroupText;
        IndexText = from.IndexText;
        GroupToolTip = from.GroupToolTip;
        IndexToolTip = from.IndexToolTip;
        SourceText = from.SourceText;
        ColorCountText = from.ColorCountText;
        LinkText = from.LinkText;
        LinkToolTip = from.LinkToolTip;
        Kind = from.Kind;
        Widths = from.Widths;
        PropertyChanged?.Invoke(this, AllChanged);
    }
}

/// <summary>One row of the action list: a raw number, and a comment that is
/// the action's real comment when it has one, or a bracketed reserved-action
/// suggestion for a standalone .air or a character definition. The suggestion is never written to
/// the file on its own — double-clicking it is what injects it, and only
/// when there is no comment there to overwrite. Stages and other .defs get no suggestions.</summary>
internal sealed class ActionRow
{
    public required AnimationAction Action { get; init; }
    public string NumberText => Action.Number.ToString(CultureInfo.InvariantCulture);
    /// <summary>The same for every row (see ActionNumberWidth).</summary>
    public double NumberWidth { get; init; }
    public string? SuggestedName { get; init; }
    public string CommentText => Action.Comments.Count > 0
        ? Action.Comments[0]
        : SuggestedName is { } s ? $"[{s}]" : "";
}

/// <summary>One row of the frame list's table: sprite, duration and flip flags in their own columns instead of one packed string.</summary>
internal sealed class FrameRow
{
    public required string Number { get; init; }
    public required string Sprite { get; init; }
    public required string Duration { get; init; }
    public required string Flags { get; init; }
}

/// <summary>One row's worth of what the ItemTemplate binds to — one row per frame, so row == frame.</summary>
public sealed class FrameTableRow(int frameRow)
{
    public int FrameRow { get; } = frameRow;
    public string Number { get; set; } = (frameRow + 1).ToString(CultureInfo.InvariantCulture);
    /// <summary>The Loopstart frame: tagged "L" in the Frame cell.</summary>
    public bool IsLoopFrame { get; set; }
    public string GroupText { get; set; } = "";
    public string IndexText { get; set; } = "";
    public string OffsetXText { get; set; } = "";
    public string OffsetYText { get; set; } = "";
    public string DurationText { get; set; } = "";
    public bool FlipH { get; set; }
    public bool FlipV { get; set; }
    public string BlendText { get; set; } = "";
    public string ScaleXText { get; set; } = "";
    public string ScaleYText { get; set; } = "";
    public string AngleText { get; set; } = "";
    // Interpolate markers, shown in the cells they affect (the mark sits on the frame being moved toward).
    public bool InterpOffset { get; set; }
    public bool InterpScale { get; set; }
    public bool InterpAngle { get; set; }
    public bool InterpBlend { get; set; }
    // Last row only: the last frame interpolates toward the loop start (drawn at its bottom edge).
    public bool WrapOffset { get; set; }
    public bool WrapScale { get; set; }
    public bool WrapAngle { get; set; }
    public bool WrapBlend { get; set; }
    /// <summary>Column widths, the same array for every row and the header (see FrameTableWidths).</summary>
    public double[] Widths { get; set; } = [];
}

/// <summary>One row of the sound list's table: Group and Number (double-click to edit), plus duration and rate as their own columns.</summary>
public sealed class SoundRow(SoundEntry sound, int number) : INotifyPropertyChanged
{
    private static readonly PropertyChangedEventArgs GroupTextChanged = new(nameof(GroupText));
    private static readonly PropertyChangedEventArgs NumberTextChanged = new(nameof(NumberText));
    private static readonly PropertyChangedEventArgs DurationTextChanged = new(nameof(DurationText));
    private static readonly PropertyChangedEventArgs RateTextChanged = new(nameof(RateText));
    private static readonly PropertyChangedEventArgs WidthsChanged = new(nameof(Widths));

    public event PropertyChangedEventHandler? PropertyChanged;

    private static string Duration(SoundEntry s) =>
        s.Problem is not null ? "unreadable" : $"{s.Duration.TotalSeconds:0.00}s";

    internal static string RateTextFor(SoundEntry s) =>
        s.Problem is not null ? "" : $"{s.SampleRate / 1000.0:0.#}kHz";

    private string _groupText = sound.Group.ToString(CultureInfo.InvariantCulture);
    private string _numberText = sound.Index.ToString(CultureInfo.InvariantCulture);
    private string _durationText = Duration(sound);
    private string _rateText = RateTextFor(sound);
    private double[] _widths = [];

    public SoundEntry Sound { get; } = sound;
    /// <summary>Row position, not the sound's own Number field. It follows the row's place
    /// in the visible list, so a changed row set needs a rebuild rather than a refresh.</summary>
    public string Number { get; } = number.ToString(CultureInfo.InvariantCulture);
    public string GroupText { get => _groupText; private set => Set(ref _groupText, value, GroupTextChanged); }
    public string NumberText { get => _numberText; private set => Set(ref _numberText, value, NumberTextChanged); }
    public string DurationText { get => _durationText; private set => Set(ref _durationText, value, DurationTextChanged); }
    public string RateText { get => _rateText; private set => Set(ref _rateText, value, RateTextChanged); }
    /// <summary>Same array for every row and the header (TextTableWidths).</summary>
    public double[] Widths
    {
        get => _widths;
        set
        {
            // Rebuilt on every edit, but almost always identical; skipping the notification
            // then keeps realized rows from re-reading every binding for nothing.
            bool same = _widths.Length == value.Length;
            for (int i = 0; same && i < value.Length; i++) same = _widths[i] == value[i];
            if (same) return;
            _widths = value;
            PropertyChanged?.Invoke(this, WidthsChanged);
        }
    }

    private void Set(ref string field, string value, PropertyChangedEventArgs args)
    {
        if (field == value) return;
        field = value;
        PropertyChanged?.Invoke(this, args);
    }

    /// <summary>Re-reads every cell this row shows from its sound, raising only what changed.</summary>
    public void Refresh()
    {
        GroupText = Sound.Group.ToString(CultureInfo.InvariantCulture);
        NumberText = Sound.Index.ToString(CultureInfo.InvariantCulture);
        DurationText = Duration(Sound);
        RateText = RateTextFor(Sound);
    }
}
