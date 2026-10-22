using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace Editor;

/// <summary>
/// Column widths for the sprite, palette, and sound tables, set on the header and every row's cells.
/// SharedSizeGroup did not keep virtualized rows aligned with their header.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// For tables whose cells are a Border (padding 4 a side, right border 1) around one TextBlock at UiFont13.
    /// Each column fits its bold header text (read from the header's own TextBlocks) and its widest cell text.
    /// Sets the header's Borders too. floors: optional per-column text that must fit (live-edited values).
    /// Every column is sized to its header and cell content.
    /// </summary>
    private double[] TextTableWidths(Grid header, IReadOnlyCollection<string[]> rows, string[]? floors = null)
    {
        double size = this.TryFindResource("UiFont13", out var v) && v is double d ? d : 13;
        double Measure(string s, FontWeight weight) => s.Length == 0 ? 0 : new TextLayout(s, new Typeface(FontFamily, weight: weight), size, null).Width;

        var cells = header.Children.OfType<Border>().ToList();
        var widths = new double[cells.Count == 0 ? 0 : cells.Max(b => Grid.GetColumn(b)) + 1];
        foreach (var b in cells)
            if (b.Child is TextBlock t) widths[Grid.GetColumn(b)] = Measure(t.Text ?? "", FontWeight.Bold);
        var charWidths = new Dictionary<char, double>();
        double Estimate(string s) => s.Sum(ch => charWidths.TryGetValue(ch, out var w) ? w : (charWidths[ch] = Measure(ch.ToString(), FontWeight.Normal)));
        for (int c = 0; c < widths.Length; c++)
        {
            // Rank by summed glyph widths, then lay out only the widest few: thousands of rows would be slow one by one.
            var texts = rows.Select(r => c < r.Length ? r[c] : "").Append(floors is not null && c < floors.Length ? floors[c] : "");
            foreach (var s in texts.Distinct().OrderByDescending(Estimate).Take(20))
                widths[c] = Math.Max(widths[c], Measure(s, FontWeight.Normal));
            widths[c] = Math.Ceiling(widths[c]) + 8 + 1; // padding 4+4, right border 1
        }
        foreach (var b in cells) b.Width = widths[Grid.GetColumn(b)];
        return widths;
    }
}
