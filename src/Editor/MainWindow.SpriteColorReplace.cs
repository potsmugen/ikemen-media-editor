using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Editor;

/// <summary>
/// The Sprites tab's color replacer: feeds SpriteView.ReplaceMap for the
/// brush-based Replace tool (see MainWindow.SpritePaint.cs) — remaps palette
/// indices in the current sprite's own pixels, not the Palettes tab's RGBA
/// table, which changes what a color looks like everywhere it's used.
///
/// One Old/New pair of squares per color, as many as settings.json ColorReplacePairs
/// asks for, laid out two pairs per line: Old is what a pair looks for, New is what
/// it becomes. A high setting gives a tall panel that scrolls. Nothing
/// is typed — click a square to arm it, then click either the sprite canvas or
/// a palette swatch to fill it with that pixel's/swatch's index. Only shown
/// while Replace is the active tool, since the squares are meaningless
/// otherwise.
/// </summary>
public partial class MainWindow
{
    /// <summary>Color pairs on offer. Slots 0..N-1 are New; slot i pairs with Old slot i + N.
    /// Sized from settings.json by BuildReplaceSquares.</summary>
    private int _replacePairCount;
    private int?[] _replaceSlotIndex = [];
    private Button[] _replaceSquares = [];
    private int? _armedReplaceSlot;
    /// <summary>New colors several pairs share; a Shift-stroke leaves them alone.</summary>
    private List<byte> _inverseSkipped = [];

    /// <summary>
    /// Fills both rows with one square per side. The squares are built here rather
    /// than declared in XAML so the pair count can come from settings.json, which
    /// ApplyStartupSettings can't reach: it runs before InitializeComponent, and these
    /// rows only exist afterwards.
    /// </summary>
    private void BuildReplaceSquares()
    {
        _replacePairCount = Settings.Checked(nameof(Settings.SpritesSection.ColorReplacePairs), _settings.Sprites.ColorReplacePairs,
            Settings.DefaultColorReplacePairs, v => v is >= 1 and <= 64);
        _replaceSlotIndex = new int?[_replacePairCount * 2];
        _replaceSquares = new Button[_replaceSlotIndex.Length];

        for (int slot = 0; slot < _replaceSlotIndex.Length; slot++)
        {
            var btn = new Button
            {
                Width = 26,
                Height = 26,
                Padding = new Thickness(0),
                Tag = slot.ToString(CultureInfo.InvariantCulture),
            };
            btn.Click += OnReplaceSquareClicked;
            _replaceSquares[slot] = btn;
        }
        // Old on the left, matching the header labels' 26px width and 4px gap.
        for (int pair = 0; pair < _replacePairCount; pair++)
            ReplacePairsGrid.Children.Add(new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 4,
                Margin = new Thickness(0, 0, 12, 4),
                Children = { _replaceSquares[_replacePairCount + pair], _replaceSquares[pair] },
            });
    }

    /// <summary>Called from OnSelectPaintTool: the panel only makes sense while Replace is the active tool.</summary>
    private void UpdateColorReplacerVisibility()
    {
        ColorReplacerPanel.IsVisible = SpriteCanvas.PaintMode && SpriteCanvas.ActiveTool == PaintTool.Replace;
        if (!ColorReplacerPanel.IsVisible && _armedReplaceSlot is not null)
        {
            _armedReplaceSlot = null;
            SpriteCanvas.ArmedForExternalPick = false;
        }
    }

    private void OnReplaceSquareClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tagStr } || !int.TryParse(tagStr, out int slot)) return;

        // Clicking the already-armed square again cancels it; clicking a different one just moves the arming.
        _armedReplaceSlot = _armedReplaceSlot == slot ? null : slot;
        SpriteCanvas.ArmedForExternalPick = _armedReplaceSlot is not null;
        RefreshReplaceSquares();
        StatusText.Text = _armedReplaceSlot is not null
            ? "Click a color on the sprite, or a palette swatch, to fill this slot."
            : "";
    }

    /// <summary>Called from OnPaintColorPicked (sprite click) and OnSwatchClicked (palette click) when a slot is armed.</summary>
    private void SetReplaceSlot(int slot, int paletteIndex)
    {
        _replaceSlotIndex[slot] = paletteIndex;
        _armedReplaceSlot = null;
        SpriteCanvas.ArmedForExternalPick = false;
        RefreshReplaceSquares();
        StatusText.Text = $"Set to index {paletteIndex}.";
    }

    private void OnClearReplaceSquares(object? sender, RoutedEventArgs e)
    {
        Array.Clear(_replaceSlotIndex);
        _armedReplaceSlot = null;
        SpriteCanvas.ArmedForExternalPick = false;
        RefreshReplaceSquares();
    }

    /// <summary>Swaps Old and New in every pair.</summary>
    private void OnSwapReplaceSquares(object? sender, RoutedEventArgs e)
    {
        for (int pair = 0; pair < _replacePairCount; pair++)
            (_replaceSlotIndex[pair], _replaceSlotIndex[_replacePairCount + pair]) =
                (_replaceSlotIndex[_replacePairCount + pair], _replaceSlotIndex[pair]);
        _armedReplaceSlot = null;
        SpriteCanvas.ArmedForExternalPick = false;
        RefreshReplaceSquares();
    }

    private void OnInverseReplaceStarted()
    {
        if (_inverseSkipped.Count == 0) return;
        string list = string.Join(", ", _inverseSkipped.Select(i => i.ToString(CultureInfo.InvariantCulture)));
        StatusText.Text = $"Skipped {list}: more than one pair turns into {(_inverseSkipped.Count == 1 ? "it" : "each")}.";
    }

    private static IBrush ThemeResource(string key) =>
        Application.Current?.Resources.TryGetResource(key, null, out var v) == true && v is IBrush b ? b : Brushes.Gray;

    /// <summary>Paints each square with its assigned color (or a neutral empty look) and highlights whichever is armed.</summary>
    private void RefreshReplaceSquares()
    {
        var pal = ActivePalette();
        for (int i = 0; i < _replaceSquares.Length; i++)
        {
            var btn = _replaceSquares[i];

            if (_replaceSlotIndex[i] is int ix && pal is not null && ix >= 0 && ix < pal.Colors.Length)
            {
                var c = pal.Colors[ix];
                btn.Background = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));
                double luma = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
                btn.Foreground = luma > 140 ? Brushes.Black : Brushes.White;
                btn.Content = ix.ToString(CultureInfo.InvariantCulture);
                ToolTip.SetTip(btn, $"Index {ix}: RGB {c.R}, {c.G}, {c.B}");
            }
            else
            {
                // Theme Field with a Separator outline, so empty squares read on dark and light panels alike.
                btn.Background = ThemeResource("ThemeFieldBg");
                btn.Content = "";
                ToolTip.SetTip(btn, null);
            }
            bool empty = btn.Content is "";
            btn.BorderBrush = _armedReplaceSlot == i ? ThemeResource("ThemeAccent") : empty ? ThemeResource("ThemeSeparator") : Brushes.Transparent;
            btn.BorderThickness = new Thickness(_armedReplaceSlot == i ? 2 : empty ? 1 : 0);
        }

        UpdateReplaceMap();
    }

    /// <summary>Feeds SpriteView.ReplaceMap and, for Shift-strokes, InverseReplaceMap (New → Old,
    /// leaving out a New that several pairs share, since it can't tell which Old it came from).</summary>
    private void UpdateReplaceMap()
    {
        var map = new Dictionary<byte, byte>();
        var inverse = new Dictionary<byte, byte>();
        var shared = new SortedSet<byte>();
        for (int col = 0; col < _replacePairCount; col++)
            if (_replaceSlotIndex[col] is int to
                && _replaceSlotIndex[_replacePairCount + col] is int from && from != to)
            {
                map[(byte)from] = (byte)to;
                if (!inverse.TryAdd((byte)to, (byte)from) && inverse[(byte)to] != from) shared.Add((byte)to);
            }
        foreach (var b in shared) inverse.Remove(b);
        _inverseSkipped = [.. shared];
        SpriteCanvas.ReplaceMap = map.Count > 0 ? map : null;
        SpriteCanvas.InverseReplaceMap = inverse.Count > 0 ? inverse : null;
    }
}
