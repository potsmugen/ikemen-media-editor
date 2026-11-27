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
            double satGap = Settings.Checked(nameof(Settings.PalettesSection.PaletteShuffleSaturationGapPercent), p.PaletteShuffleSaturationGapPercent,
                Settings.DefaultPaletteShuffleSaturationGapPercent, v => v > 0 && v <= 100);
            double darken = Settings.Checked(nameof(Settings.PalettesSection.PaletteShuffleDarkenPercent), p.PaletteShuffleDarkenPercent,
                Settings.DefaultPaletteShuffleDarkenPercent, v => v >= 0 && v <= 100);
            double brighten = Settings.Checked(nameof(Settings.PalettesSection.PaletteShuffleBrightenPercent), p.PaletteShuffleBrightenPercent,
                Settings.DefaultPaletteShuffleBrightenPercent, v => v >= 0 && v <= 100);
            double saturation = Settings.Checked(nameof(Settings.PalettesSection.PaletteShuffleSaturationPercent), p.PaletteShuffleSaturationPercent,
                Settings.DefaultPaletteShuffleSaturationPercent, v => v >= 0 && v <= 100);
            double grayTint = Settings.Checked(nameof(Settings.PalettesSection.PaletteShuffleGrayTintPercent), p.PaletteShuffleGrayTintPercent,
                Settings.DefaultPaletteShuffleGrayTintPercent, v => v >= 0 && v <= 100);
            double grayTintChance = Settings.Checked(nameof(Settings.PalettesSection.PaletteShuffleGrayTintChancePercent), p.PaletteShuffleGrayTintChancePercent,
                Settings.DefaultPaletteShuffleGrayTintChancePercent, v => v >= 0 && v <= 100);
            double keep = Settings.Checked(nameof(Settings.PalettesSection.PaletteShuffleKeepPercent), p.PaletteShuffleKeepPercent,
                Settings.DefaultPaletteShuffleKeepPercent, v => v >= 0 && v <= 100);
            double keepDark = Settings.Checked(nameof(Settings.PalettesSection.PaletteShuffleKeepDarkPercent), p.PaletteShuffleKeepDarkPercent,
                Settings.DefaultPaletteShuffleKeepDarkPercent, v => v >= 0 && v <= 100);

            var shuffled = ShuffleColors(_shuffleBase, _shuffleRandom, hueGap, Math.Max(neighborGap, hueGap), satGap, darken, brighten, saturation, grayTint, grayTintChance, keep, keepDark, out int groups);
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
    /// Random alternative palette. Colors are grouped (chained) when their hues are within hueGapDegrees and their saturations within satGapPercent
    /// of each other; neighboring palette indexes get the looser neighborGapDegrees for hue. Each group gets one random hue shift over the full circle and one
    /// random lightness change (down by up to darkenPercent, up by up to brightenPercent; see ShiftLightness, which brightens light colors more than dark ones) and one random saturation change of up to
    /// saturationPercent. Saturation is a share of what sRGB allows, so shifted colors stay in gamut. A group is left as it is with keepPercent chance
    /// (keepDarkPercent when its average lightness is under 0.3), but at least one group always changes. Near-grays form their own group: it changes
    /// lightness only, or with grayTintChancePercent chance gets one random hue tinted between a third of and the full grayTintPercent.
    /// Pure black and white stay neutral because sRGB allows no tint there.
    /// </summary>
    internal static Dictionary<int, Rgba> ShuffleColors(Dictionary<int, Rgba> source, Random rng,
        double hueGapDegrees, double neighborGapDegrees, double satGapPercent, double darkenPercent, double brightenPercent, double saturationPercent, double grayTintPercent, double grayTintChancePercent, double keepPercent, double keepDarkPercent, out int groupCount)
    {
        // A color is gray when its chroma is a small share of what sRGB allows at its lightness (so pale tints near white are not)
        // Very low chroma also counts, so a tinted gray ramp isn't split where dark ends inflate the share
        const double GrayShare = 0.05, GrayChroma = 0.012;
        double gap = hueGapDegrees * Math.PI / 180, neighborGap = neighborGapDegrees * Math.PI / 180, darkenRange = darkenPercent / 100, brightenRange = brightenPercent / 100, satRange = saturationPercent / 100, satGap = satGapPercent / 100;
        var result = new Dictionary<int, Rgba>(source);
        var colored = new List<(int Index, double L, double Hue, double Sat, byte A)>();
        var grays = new List<(int Index, double L, byte A)>();

        foreach (var (i, c) in source)
        {
            var (L, a, b) = RgbToOklab(c.R, c.G, c.B);
            double chroma = Math.Sqrt(a * a + b * b), hue = Math.Atan2(b, a);
            double max = MaxChroma(L, hue), share = max <= 1e-6 ? 0 : Math.Clamp(chroma / max, 0, 1);
            if (share < GrayShare || chroma < GrayChroma) { grays.Add((i, L, c.A)); continue; }
            colored.Add((i, L, hue, share, c.A));
        }

        // Groups: linked colors (see the loop below) form one group, chained.
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

        // Two colors link when their hues are close and their saturations are too (hue alone grouped
        // colors that official variants treat differently); neighboring palette indexes get the looser hue gap.
        for (int m = 0; m < count; m++)
            for (int n = m + 1; n < count; n++)
            {
                if (Math.Abs(colored[m].Sat - colored[n].Sat) > satGap) continue;
                double d = HueDistance(colored[m].Hue, colored[n].Hue);
                if (d <= gap || (d <= neighborGap && Math.Abs(colored[m].Index - colored[n].Index) == 1)) Link(m, n);
            }

        var groups = new List<List<(int Index, double L, double Hue, double Sat, byte A)>>();
        var groupOf = new Dictionary<int, int>();
        for (int k = 0; k < count; k++)
        {
            int root = Find(k);
            if (!groupOf.TryGetValue(root, out int g)) { g = groups.Count; groupOf[root] = g; groups.Add([]); }
            groups[g].Add(colored[k]);
        }

        // Which groups stay as they are. Official variants leave about a fifth of the groups alone, near-blacks most often.
        double keepChance = keepPercent / 100, keepDarkChance = keepDarkPercent / 100;
        bool Keeps(double meanL) => rng.NextDouble() < (meanL < 0.3 ? keepDarkChance : keepChance);
        var groupMeanL = groups.Select(g => g.Average(c => c.L)).ToList();
        double grayMeanL = grays.Count > 0 ? grays.Average(g => g.L) : 0;
        var kept = groupMeanL.Select(Keeps).ToList();
        bool grayKept = grays.Count > 0 && Keeps(grayMeanL);
        int totalGroups = groups.Count + (grays.Count > 0 ? 1 : 0);
        if (totalGroups > 0 && kept.All(k => k) && (grays.Count == 0 || grayKept))
        {
            int forced = rng.Next(totalGroups); // everything was kept: one random group changes anyway
            if (forced < groups.Count) kept[forced] = false; else grayKept = false;
        }

        for (int gi = 0; gi < groups.Count; gi++)
        {
            if (kept[gi]) continue;
            var group = groups[gi];
            double shift = rng.NextDouble() * 2 * Math.PI, change = RandomLightnessChange(rng, groupMeanL[gi], darkenRange, brightenRange);
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
            if (!grayKept)
            {
                double hue = rng.NextDouble() * 2 * Math.PI, change = RandomLightnessChange(rng, grayMeanL, darkenRange, brightenRange);
                // Official grays mostly stay neutral; a tinted one is rarely strong, so the strength ranges from a third of the limit up to it
                double tint = rng.NextDouble() < grayTintChancePercent / 100 ? grayTintPercent / 100 * (1.0 / 3 + 2.0 / 3 * rng.NextDouble()) : 0;
                foreach (var (i, L, a) in grays)
                {
                    double l2 = ShiftLightness(L, change);
                    result[i] = FromOklch(l2, tint * MaxChroma(l2, hue), hue, a);
                }
            }
        }
        return result;
    }

    /// <summary>Light groups darken more often and dark ones brighten more often (as in official variants), then a random size up to that direction's limit. Negative darkens.</summary>
    private static double RandomLightnessChange(Random rng, double meanLightness, double darkenRange, double brightenRange)
    {
        double darkenChance = Math.Clamp(meanLightness, 0.3, 0.7);
        double u = rng.NextDouble();
        return rng.NextDouble() < darkenChance ? -u * darkenRange : u * brightenRange;
    }

    /// <summary>Darkening scales lightness down. Brightening is its mirror: it lifts light colors more than dark ones, easing off toward white so nothing clips. Either way two different lightnesses stay different, so a ramp never flattens at the ends.</summary>
    private static double ShiftLightness(double L, double change) => change >= 0 ? L * (1 + change) / (1 + change * L) : L * (1 + change);

    private static Rgba FromOklch(double L, double chroma, double hue, byte alpha)
    {
        var (r, g, b) = OklabToLinear(L, chroma * Math.Cos(hue), chroma * Math.Sin(hue));
        return new Rgba(ClampByte(LinearToSrgb(r) * 255), ClampByte(LinearToSrgb(g) * 255), ClampByte(LinearToSrgb(b) * 255), alpha);
    }
}
