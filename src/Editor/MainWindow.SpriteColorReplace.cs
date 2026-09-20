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
/// Two rows of squares, as many pairs as settings.json ColorReplacePairs asks
/// for: top is "Active" (what a column becomes), bottom is "Replace" (what a
/// column looks for). The rows are one long line, so a high setting gives a
/// wide panel that scrolls rather than wrapping — cluttered by choice. Nothing
/// is typed — click a square to arm it, then click either the sprite canvas or
/// a palette swatch to fill it with that pixel's/swatch's index. Only shown
/// while Replace is the active tool, since the squares are meaningless
/// otherwise.
/// </summary>
public partial class MainWindow
{
    /// <summary>Color pairs on offer. Slots 0..N-1 are Active (top row); slot i pairs with
    /// slot i + N (bottom row). Sized from settings.json by BuildReplaceSquares.</summary>
    private int _replacePairCount;
    private int?[] _replaceSlotIndex = [];
    private Button[] _replaceSquares = [];
    private int? _armedReplaceSlot;

    /// <summary>
    /// Fills both rows with one square per side. The squares are built here rather
    /// than declared in XAML so the pair count can come from settings.json, which
    /// ApplyStartupSettings can't reach: it runs before InitializeComponent, and these
    /// rows only exist afterwards.
    /// </summary>
    private void BuildReplaceSquares()
    {
        _replacePairCount = Settings.Checked(nameof(Settings.ColorReplacePairs), _settings.ColorReplacePairs,
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
            (slot < _replacePairCount ? ReplaceActiveRow : ReplaceFromRow).Children.Add(btn);
        }
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
                btn.Background = new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x38));
                btn.Foreground = Brushes.Gray;
                btn.Content = "";
                ToolTip.SetTip(btn, null);
            }
            btn.BorderBrush = _armedReplaceSlot == i ? Brushes.Orange : Brushes.Transparent;
            btn.BorderThickness = new Thickness(_armedReplaceSlot == i ? 2 : 0);
        }

        UpdateReplaceMap();
    }

    /// <summary>Feeds SpriteView.ReplaceMap for the brush-based Replace tool.</summary>
    private void UpdateReplaceMap()
    {
        var map = new Dictionary<byte, byte>();
        for (int col = 0; col < _replacePairCount; col++)
            if (_replaceSlotIndex[col] is int to
                && _replaceSlotIndex[_replacePairCount + col] is int from && from != to)
                map[(byte)from] = (byte)to;
        SpriteCanvas.ReplaceMap = map.Count > 0 ? map : null;
    }
}
