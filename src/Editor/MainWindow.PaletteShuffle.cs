using Avalonia.Interactivity;
using Sff;

namespace Editor;

public partial class MainWindow
{
    /// <summary>The selected colors as they were before the first Shuffle, so each press is a new alternative of the original instead of stacking. Cleared by anything else that changes colors.</summary>
    private Dictionary<int, Rgba>? _shuffleBase;

    private readonly Random _shuffleRandom = new();

    private async void OnShuffleColors(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_editingPalette is not { } pal || _editingView is not { } view) return;
            List<int> indices = AdjustIndices();
            if (indices.Count == 0) { StatusText.Text = "Click a color first."; return; }

            EnsureColorSession();
            _shuffleBase ??= indices.ToDictionary(i => i, i => pal.Colors[i]);

            var p = _settings.Palettes;
            double hueGap = Settings.Checked(nameof(Settings.PalettesSection.PaletteShuffleHueGapDegrees), p.PaletteShuffleHueGapDegrees,
                Settings.DefaultPaletteShuffleHueGapDegrees, v => v > 0 && v <= 180);
            double neighborGap = Settings.Checked(nameof(Settings.PalettesSection.PaletteShuffleNeighborHueGapDegrees), p.PaletteShuffleNeighborHueGapDegrees,
                Settings.DefaultPaletteShuffleNeighborHueGapDegrees, v => v > 0 && v <= 180);
            double darken = Settings.Checked(nameof(Settings.PalettesSection.PaletteShuffleDarkenPercent), p.PaletteShuffleDarkenPercent,
                Settings.DefaultPaletteShuffleDarkenPercent, v => v >= 0 && v <= 100);
            double brighten = Settings.Checked(nameof(Settings.PalettesSection.PaletteShuffleBrightenPercent), p.PaletteShuffleBrightenPercent,
                Settings.DefaultPaletteShuffleBrightenPercent, v => v >= 0 && v <= 100);
            double saturation = Settings.Checked(nameof(Settings.PalettesSection.PaletteShuffleSaturationPercent), p.PaletteShuffleSaturationPercent,
                Settings.DefaultPaletteShuffleSaturationPercent, v => v >= 0 && v <= 100);
            double grayTint = Settings.Checked(nameof(Settings.PalettesSection.PaletteShuffleGrayTintPercent), p.PaletteShuffleGrayTintPercent,
                Settings.DefaultPaletteShuffleGrayTintPercent, v => v >= 0 && v <= 100);

            var shuffled = ShuffleColors(_shuffleBase, _shuffleRandom, hueGap, Math.Max(neighborGap, hueGap), darken, brighten, saturation, grayTint, out int groups);
            foreach (var (i, c) in shuffled)
                if (i < pal.Colors.Length) pal.Colors[i] = c;

            if (ReferenceEquals(pal, _scratchPalette)) view.InvalidateVisual();
            else RefreshPaletteViews(pal);
            if (_selectedSwatch >= 0 && _selectedSwatch < pal.Colors.Length && indices.Contains(_selectedSwatch))
            {
                _suppressEvents = true;
                var c = pal.Colors[_selectedSwatch];
                RSlider.Value = c.R; GSlider.Value = c.G; BSlider.Value = c.B; ASlider.Value = c.A;
                _suppressEvents = false;
            }
            RebaseAfterColorOp(keepShuffleBase: true);
            StatusText.Text = $"Shuffled {indices.Count} color(s) in {groups} group(s).";
        }
        catch (Exception ex)
        {
            await Failed("OnShuffleColors", ex);
        }
    }

    /// <summary>
    /// Random alternative palette. Colors are grouped by hue (a new group starts wherever the sorted hues
    /// jump by more than hueGapDegrees, or by more than neighborGapDegrees between neighboring palette indexes); each group gets one random hue shift over the full circle and one
    /// random lightness change (down by up to darkenPercent, up by up to brightenPercent; see ShiftLightness) and one random saturation change of up to
    /// saturationPercent. Saturation is a share of what sRGB allows, so shifted colors stay in gamut. Near-grays form their own group: one random hue, tinted by grayTintPercent.
    /// Pure black and white stay neutral because sRGB allows no tint there.
    /// </summary>
    private static Dictionary<int, Rgba> ShuffleColors(Dictionary<int, Rgba> source, Random rng,
        double hueGapDegrees, double neighborGapDegrees, double darkenPercent, double brightenPercent, double saturationPercent, double grayTintPercent, out int groupCount)
    {
        // A color is gray when its chroma is a small share of what sRGB allows at its lightness (so pale tints near white are not).
        const double GrayShare = 0.05;
        double gap = hueGapDegrees * Math.PI / 180, neighborGap = neighborGapDegrees * Math.PI / 180, darkenRange = darkenPercent / 100, brightenRange = brightenPercent / 100, satRange = saturationPercent / 100;
        var result = new Dictionary<int, Rgba>(source);
        var colored = new List<(int Index, double L, double Hue, double Sat, byte A)>();
        var grays = new List<(int Index, double L, byte A)>();

        foreach (var (i, c) in source)
        {
            var (L, a, b) = RgbToOklab(c.R, c.G, c.B);
            double chroma = Math.Sqrt(a * a + b * b), hue = Math.Atan2(b, a);
            double max = MaxChroma(L, hue), share = max <= 1e-6 ? 0 : Math.Clamp(chroma / max, 0, 1);
            if (share < GrayShare) { grays.Add((i, L, c.A)); continue; }
            colored.Add((i, L, hue, share, c.A));
        }

        // Hue groups. Colors whose hues are within gap of each other (sorted around the circle, seam included)
        // are linked; so are colors at neighboring palette indexes within the looser neighborGap, because
        // a palette ramp usually sits side by side. Linked colors form one group.
        int count = colored.Count;
        var parent = Enumerable.Range(0, count).ToArray();
        int Find(int x)
        {
            while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
            return x;
        }
        void Link(int x, int y) => parent[Find(x)] = Find(y);
        static double HueDistance(double x, double y)
        {
            double d = Math.Abs(x - y) % (2 * Math.PI);
            return d > Math.PI ? 2 * Math.PI - d : d;
        }

        int[] byHue = Enumerable.Range(0, count).OrderBy(k => colored[k].Hue).ToArray();
        for (int t = 0; t < count && count > 1; t++)
        {
            int first = byHue[t], second = byHue[(t + 1) % count];
            if (HueDistance(colored[first].Hue, colored[second].Hue) <= gap) Link(first, second);
        }
        var byIndex = new Dictionary<int, int>();
        for (int k = 0; k < count; k++) byIndex[colored[k].Index] = k;
        for (int k = 0; k < count; k++)
            if (byIndex.TryGetValue(colored[k].Index + 1, out int next) && HueDistance(colored[k].Hue, colored[next].Hue) <= neighborGap)
                Link(k, next);

        var groups = new List<List<(int Index, double L, double Hue, double Sat, byte A)>>();
        var groupOf = new Dictionary<int, int>();
        for (int k = 0; k < count; k++)
        {
            int root = Find(k);
            if (!groupOf.TryGetValue(root, out int g)) { g = groups.Count; groupOf[root] = g; groups.Add([]); }
            groups[g].Add(colored[k]);
        }

        foreach (var group in groups)
        {
            double shift = rng.NextDouble() * 2 * Math.PI, change = RandomLightnessChange(rng, darkenRange, brightenRange);
            double satFactor = 1 + (rng.NextDouble() * 2 - 1) * satRange;
            foreach (var (i, L, hue, sat, a) in group)
            {
                double l2 = ShiftLightness(L, change), h2 = hue + shift;
                result[i] = FromOklch(l2, Math.Clamp(sat * satFactor, 0, 1) * MaxChroma(l2, h2), h2, a);
            }
        }

        groupCount = groups.Count;
        if (grays.Count > 0)
        {
            groupCount++;
            double hue = rng.NextDouble() * 2 * Math.PI, change = RandomLightnessChange(rng, darkenRange, brightenRange);
            foreach (var (i, L, a) in grays)
            {
                double l2 = ShiftLightness(L, change);
                result[i] = FromOklch(l2, grayTintPercent / 100 * MaxChroma(l2, hue), hue, a);
            }
        }
        return result;
    }

    /// <summary>Up or down with equal odds, then a random size up to that direction's limit. Negative darkens.</summary>
    private static double RandomLightnessChange(Random rng, double darkenRange, double brightenRange)
    {
        double u = rng.NextDouble() * 2 - 1;
        return u >= 0 ? u * brightenRange : u * darkenRange;
    }

    /// <summary>Darkening scales lightness down; brightening closes that share of the gap to white. Either way two different lightnesses stay different, so a ramp never flattens at the ends.</summary>
    private static double ShiftLightness(double L, double change) => change >= 0 ? L + (1 - L) * change : L * (1 + change);

    private static Rgba FromOklch(double L, double chroma, double hue, byte alpha)
    {
        var (r, g, b) = OklabToLinear(L, chroma * Math.Cos(hue), chroma * Math.Sin(hue));
        return new Rgba(ClampByte(LinearToSrgb(r) * 255), ClampByte(LinearToSrgb(g) * 255), ClampByte(LinearToSrgb(b) * 255), alpha);
    }
}
