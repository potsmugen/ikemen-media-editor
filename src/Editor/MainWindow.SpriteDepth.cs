using Avalonia.Controls;
using Sff;

namespace Editor;

/// <summary>Convert Color Depth: indexed to 32-bit with the sprite's own palette, or 32-bit to
/// 8-bit with a palette built from its colors. One undo step for the whole selection</summary>
public partial class MainWindow
{
    private sealed record SpriteDepthState(Sprite Sprite, Palette? Palette, byte[]? Indexed, byte[]? Direct, bool Dirty);

    /// <summary>True color needs SFF 2.0.1.0, so only that version can change color depth</summary>
    private bool CanConvertSpriteDepth() => _file is { } file && SffVersions.SupportsPng(file.Version);

    private MenuItem ConvertSpriteDepthMenuItem(List<Sprite> selected)
    {
        var item = MenuAction("Convert Color Depth", () => _ = ConvertSpriteDepth(selected),
            CanConvertSpriteDepth() && selected.Count > 0);
        // Disabled items still show their tip, so say why it is grayed out
        if (_file is not null && !CanConvertSpriteDepth())
            ToolTip.SetTip(item, "Available in SFF 2.0.1.0 files only");
        return item;
    }

    private static Sprite PixelRoot(Sprite sprite)
    {
        while (sprite.SharesWith is { } target) sprite = target;
        return sprite;
    }

    private async Task ConvertSpriteDepth(List<Sprite> selected)
    {
        try
        {
            if (_file is not { } file || selected.Count == 0 || !CanConvertSpriteDepth()) return;
            // The undo snapshot takes the pixels, so settle a pending paint session first
            if (!await ResolvePendingPaintBeforeSwitch() || !ReferenceEquals(_file, file)) return;

            int direct = selected.Count(s => s.Indexed is null && s.Direct is not null);
            int indexed = selected.Count(s => s.Indexed is not null);
            var choice = await Dialogs.PromptSpriteDepth(this, selected.Count,
                initialTrueColor: indexed >= direct, paletteChoice: direct > 1);
            if (choice is null || !ReferenceEquals(_file, file)) return;

            var skipped = new List<string>();
            var targets = new List<Sprite>();
            var linkedSelected = new List<Sprite>();
            int unchanged = 0;
            foreach (var sprite in selected)
            {
                bool isIndexed = sprite.Indexed is not null;
                if (!isIndexed && sprite.Direct is null)
                    skipped.Add(SkipPaletteSprite(sprite, "has no pixels to convert"));
                else if (choice.TrueColor != isIndexed) unchanged++;
                else if (sprite.SharesWith is not null) linkedSelected.Add(sprite);
                else if (isIndexed && sprite.Palette is null)
                    skipped.Add(SkipPaletteSprite(sprite, "has no palette to take its colors from"));
                else targets.Add(sprite);
            }
            var targetSet = targets.ToHashSet();
            // A selected link follows its converted original; only one whose original stays is skipped
            foreach (var sprite in linkedSelected.Where(s => !targetSet.Contains(PixelRoot(s))))
                skipped.Add(SkipPaletteSprite(sprite, "is linked; unlink it to convert its own pixels"));

            if (targets.Count == 0)
            {
                ReportSpriteDepth(skipped, "No sprites converted.", unchanged);
                return;
            }

            var linkers = file.Sprites.Where(s => s.SharesWith is not null && targetSet.Contains(PixelRoot(s))).ToList();
            int outside = linkers.Count(s => !selected.Contains(s));
            if (outside > 0)
            {
                string whose = targets.Count == 1 ? "this sprite" : "the selected sprites";
                if (!await Dialogs.Confirm(this, "Linked Sprites Will Change",
                        $"Converting {whose} also converts {outside} linked sprite{(outside == 1 ? "" : "s")}.", "Continue")
                    || !ReferenceEquals(_file, file))
                    return;
            }

            // 8-bit: the new palettes are numbered from one key, prefilled with the first sprite's group
            (int Group, int Index) key = default;
            if (!choice.TrueColor)
            {
                var first = targets.MinBy(s => file.PositionOf(s))!;
                if (await PromptNewPaletteKeyFor(file, first.Group) is not { } picked) return;
                key = picked;
            }

            var before = targets.Select(s => new SpriteDepthState(s, s.Palette, s.Indexed, s.Direct, s.PixelsDirty)).ToList();
            var after = new List<SpriteDepthState>();
            var added = new List<Palette>();
            int reduced = 0;
            if (!TryBeginBusy()) return;
            try
            {
                if (choice.TrueColor)
                {
                    for (int i = 0; i < targets.Count; i++)
                    {
                        var s = targets[i];
                        await BusyStep("Converting sprites", i, targets.Count, $"{s.Group},{s.Index}");
                        after.Add(new(s, null, null, SpriteDepth.ToTrueColor(s, s.Palette!), true));
                    }
                }
                else
                {
                    var used = file.Palettes.Where(p => p.Group == key.Group).Select(p => p.Index).ToHashSet();
                    int nextIndex = key.Index;
                    Palette NewPalette(Rgba[] colors)
                    {
                        while (used.Contains(nextIndex)) nextIndex++;
                        used.Add(nextIndex);
                        var palette = new Palette
                        {
                            Group = key.Group, Index = nextIndex, Colors = colors, Source = PaletteSource.Embedded,
                        };
                        added.Add(palette);
                        return palette;
                    }
                    List<List<Sprite>> groups = choice.SharedPalette ? new() { targets } : targets.Select(s => new List<Sprite> { s }).ToList();
                    for (int i = 0; i < groups.Count; i++)
                    {
                        var group = groups[i];
                        await BusyStep("Converting sprites", i, groups.Count, $"{group[0].Group},{group[0].Index}");
                        var result = SpriteDepth.ToIndexed(group.Select(s => s.Direct!).ToList());
                        if (result.Reduced) reduced++;
                        var palette = NewPalette(result.Palette.Colors);
                        for (int j = 0; j < group.Count; j++)
                            after.Add(new(group[j], palette, result.Indexes[j], null, true));
                    }
                }
            }
            finally { EndBusy(); }

            // Links read their original's pixels, so they take its new palette (none for 32-bit)
            var afterPalette = after.ToDictionary(s => s.Sprite, s => s.Palette);
            var linkBefore = linkers.Select(s => (Sprite: s, Palette: s.Palette)).ToList();
            var linkAfter = linkers.Select(s => (Sprite: s, Palette: afterPalette[PixelRoot(s)])).ToList();

            void Apply(List<SpriteDepthState> states)
            {
                foreach (var state in states)
                {
                    state.Sprite.Palette = state.Palette;
                    state.Sprite.Indexed = state.Indexed;
                    state.Sprite.Direct = state.Direct;
                    state.Sprite.PixelsDirty = state.Dirty;
                }
            }
            Edit(targets.Count == 1 ? "convert color depth" : "convert color depths",
                () =>
                {
                    foreach (var palette in added) file.Palettes.Add(palette);
                    Apply(after);
                    foreach (var (s, p) in linkAfter) s.Palette = p;
                },
                () =>
                {
                    foreach (var (s, p) in linkBefore) s.Palette = p;
                    Apply(before);
                    foreach (var palette in added) file.Palettes.Remove(palette);
                });
            RestoreSpriteSelection(selected);

            CrashLog.Trace($"[sprites] converted {targets.Count} sprite(s) to {(choice.TrueColor ? "32-bit" : "8-bit")}; "
                + $"{linkers.Count} link(s) followed, {added.Count} palette(s) added, {reduced} reduced");
            string summary = choice.TrueColor
                ? $"Converted {targets.Count} sprite(s) to 32-bit color."
                : $"Converted {targets.Count} sprite(s) to 8-bit with {added.Count} new palette(s)."
                    + (reduced > 0 ? $" Colors were reduced in {reduced} of them." : "");
            ReportSpriteDepth(skipped, summary, unchanged);
        }
        catch (Exception ex)
        {
            await Failed("ConvertSpriteDepth", ex);
        }
    }

    private void ReportSpriteDepth(List<string> skipped, string summary, int unchanged)
    {
        foreach (string item in skipped) CrashLog.Error("Convert color depth", item);
        ShowSkipped("Sprites Skipped", skipped);
        StatusText.Text = summary
            + (unchanged > 0 ? $" {unchanged} already were." : "")
            + (skipped.Count > 0 ? $" Skipped {skipped.Count} (see errors.log)." : "");
    }
}
