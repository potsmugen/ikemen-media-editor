using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Sff;

namespace Editor;

public partial class MainWindow
{
    private sealed class SpritePaletteBatchPlan(
        Sprite sprite, Palette? beforePalette, Palette? sourcePalette,
        bool changesColors, bool canRemap)
    {
        public Sprite Sprite { get; } = sprite;
        public Palette? BeforePalette { get; } = beforePalette;
        public Palette? SourcePalette { get; } = sourcePalette;

        public bool ChangesColors { get; } = changesColors;
        public bool CanRemap { get; } = canRemap;
        public Palette? AfterPalette { get; set; }
        public byte[]? BeforeIndexes { get; } = sprite.Indexed;
        public byte[]? BeforeDirect { get; } = sprite.Direct;
        public byte[]? AfterIndexes { get; set; }
        public bool PixelsDirtyBefore { get; } = sprite.PixelsDirty;

        /// <summary>A true-color sprite with no indexes; matching converts it to indexed.</summary>
        public bool IsDirect => BeforeIndexes is null && BeforeDirect is not null;
    }

    private sealed record PaletteBatchColorChange(
        Palette Palette, Rgba[] Before, Rgba[] After, bool DirtyBefore, bool DirtyAfter);

    private async Task<bool> AssignPaletteToSelectedSprites(Palette chosen, List<Sprite> selected)
    {
        if (_file is not { } file) return false;
        bool v1 = SffVersions.IsV1(file.Version);
        bool move = MovesIntoSff(file, chosen); // one shared palette instead of a copy per sprite
        var skipped = new List<string>();
        var plans = new List<SpritePaletteBatchPlan>();
        int unchanged = 0;

        foreach (var sprite in selected)
        {
            var current = sprite.Palette;
            if (!ReferenceEquals(current, chosen) || chosen.Source == PaletteSource.External)
            {
                if (chosen.Source == PaletteSource.External && v1
                    && (current is null || current.Source != PaletteSource.Embedded || file.PaletteCanBeMadeUnique(sprite)))
                {
                    skipped.Add(SkipPaletteSprite(sprite,
                        "set this sprite's Palette field to Own first"));
                    continue;
                }

                bool changesColors = v1 && current is not null;
                bool canRemap = CanRemapSpriteIndexes(file, sprite, current);
                plans.Add(new(sprite, current, current, changesColors, canRemap));
            }
            else
            {
                unchanged++;
            }
        }

        if (plans.Count == 0)
        {
            ReportPaletteBatch("Use preview palette", 0, unchanged, skipped);
            return false;
        }

        var targetColors = chosen.Colors.ToArray();
        bool droppedAlpha = false;
        if (chosen.Source == PaletteSource.External)
        {
            var largest = SpriteNeedingMostColors(plans.Select(plan => plan.Sprite));
            var prepared = await PreparePaletteForSprite(largest, targetColors, v1);
            if (prepared is null || !ReferenceEquals(_file, file)) return false;
            targetColors = prepared.Value.Colors;
            droppedAlpha = prepared.Value.DroppedAlpha;
        }

        bool allowNearest = plans.Any(plan => plan.CanRemap);
        bool allowKeep = plans.Any(plan => plan.BeforeIndexes is not null) || !allowNearest;
        string? notice = v1 && plans.Any(plan => plan.ChangesColors && plan.SourcePalette is { } palette
            && file.Sprites.Any(other => !ReferenceEquals(other, plan.Sprite) && ReferenceEquals(other.Palette, palette)))
            ? "Other sprites using affected SFFv1 palettes may look different if their indexes are unchanged."
            : null;
        var mapping = await Dialogs.PromptPaletteIndexMapping(this, allowNearest,
            allowNearest ? null : "Nearest-color matching is unavailable for the selected sprites.", notice,
            multiple: true, allowKeep: allowKeep,
            keepDisabledReason: allowKeep ? null : "These sprites have no indexes to keep.");
        if (mapping is null || !ReferenceEquals(_file, file)) return false;

        if (mapping == ImportIndexChoice.Nearest)
        {
            foreach (var plan in plans.Where(plan => !plan.CanRemap).ToList())
            {
                skipped.Add(SkipPaletteSprite(plan.Sprite,
                    "cannot remap linked or borrowed pixels, or an indexed sprite with no palette"));
                plans.Remove(plan);
            }
        }
        else
        {
            // A true-color sprite has no indexes to keep, so Keep leaves it alone.
            foreach (var plan in plans.Where(plan => plan.IsDirect).ToList())
            {
                skipped.Add(SkipPaletteSprite(plan.Sprite, "is true color, so there are no indexes to keep"));
                plans.Remove(plan);
            }
        }
        if (plans.Count == 0)
        {
            ReportPaletteBatch("Use preview palette", 0, unchanged, skipped);
            return false;
        }

        if (!TryBeginBusy()) return false;
        try
        {
            for (int i = 0; i < plans.Count; i++)
            {
                var plan = plans[i];
                await BusyStep("Applying palette", i, plans.Count, $"{plan.Sprite.Group},{plan.Sprite.Index}");
                if (mapping == ImportIndexChoice.Nearest)
                {
                    if (plan.IsDirect)
                        plan.AfterIndexes = ImageImport.MatchToPalette(plan.BeforeDirect!, targetColors);
                    else if (plan.BeforeIndexes is { } indexes)
                    {
                        var remapped = ImageImport.RemapIndexes(indexes, plan.SourcePalette!, targetColors);
                        if (!remapped.AsSpan().SequenceEqual(indexes)) plan.AfterIndexes = remapped;
                    }
                }
                plan.AfterPalette = move
                    ? chosen
                    : chosen.Source == PaletteSource.External && !v1
                    ? NewPaletteForSprite(file, plan.Sprite.Group, 0, targetColors, plans, plan)
                    : plan.ChangesColors ? plan.BeforePalette : chosen;
            }
        }
        finally { EndBusy(); }

        var added = plans.Where(plan => chosen.Source == PaletteSource.External && !v1 && !move)
            .Select(plan => plan.AfterPalette!).ToList();
        var colorChanges = BuildPaletteColorChanges(plans, targetColors, dirtyExternalPalettes: v1);
        // A temp palette never stays behind: SFFv2 moves it in, SFFv1 drops it after copying its colors.
        Action moveIn, moveOut;
        if (move)
        {
            var steps = MoveTempPaletteIntoSff(file, chosen, targetColors, plans[0].Sprite.Group);
            (moveIn, moveOut) = (steps.MoveIn, steps.MoveOut);
        }
        else (moveIn, moveOut) = TempPaletteLeavesList(file, chosen);
        ApplySpritePaletteBatch(file, plans, colorChanges, added,
            "Use preview palette", selected, skipped, unchanged,
            droppedAlpha ? " without alpha" : "",
            moveIn: moveIn, moveOut: moveOut);
        return true;
    }

    private async Task ImportPaletteIntoSelectedSprites(List<Sprite> selected)
    {
        if (_file is not { } file) return;
        bool v1 = SffVersions.IsV1(file.Version);
        var skipped = new List<string>();
        var plans = new List<SpritePaletteBatchPlan>();

        foreach (var sprite in selected)
        {
            var current = sprite.Palette;
            string? reason = sprite.Indexed is null && sprite.Direct is null
                ? "has no pixels"
                : sprite.Indexed is not null && current is null
                    ? "has no palette"
                    : v1 && sprite.SharesWith is not null
                        ? "links to another sprite and cannot carry its own palette"
                        : v1 && (current!.Source != PaletteSource.Embedded || file.PaletteCanBeMadeUnique(sprite))
                            ? "does not carry its own SFFv1 palette"
                            : null;
            if (reason is not null)
            {
                skipped.Add(SkipPaletteSprite(sprite, reason));
                continue;
            }

            bool canRemap = CanRemapSpriteIndexes(file, sprite, current);
            plans.Add(new(sprite, current, current, changesColors: v1, canRemap));
        }

        if (plans.Count == 0)
        {
            ReportPaletteBatch("Import Palette into Sprite", 0, 0, skipped);
            return;
        }

        var files = await PickOpen(new FilePickerOpenOptions
        {
            Title = v1 ? "Import palette into SFFv1 sprites" : "Import palette into SFFv2 sprites",
            AllowMultiple = false,
            FileTypeFilter = [PaletteFilesType],
        });
        if (files.Count == 0) return;
        if (files[0].TryGetLocalPath() is not { } path)
        {
            CrashLog.Error("Import Palette into Sprite", $"{files[0].Name}: selected file has no local path");
            await Dialogs.ShowMessage(this, "Could Not Import Palette", "The selected file has no local path.");
            return;
        }
        if (!ReferenceEquals(_file, file)) return;

        Palette imported;
        try { imported = PaletteFileImport.Read(path); }
        catch (Exception ex)
        {
            CrashLog.Write($"Import Palette into Sprite {path}", ex);
            await Dialogs.ShowMessage(this, "Could Not Import Palette", $"{Path.GetFileName(path)}: {ex.Message}");
            return;
        }

        var largest = SpriteNeedingMostColors(plans.Select(plan => plan.Sprite));
        var prepared = await PreparePaletteForSprite(largest, imported.Colors, v1);
        if (prepared is null || !ReferenceEquals(_file, file)) return;
        var targetColors = prepared.Value.Colors;
        bool droppedAlpha = prepared.Value.DroppedAlpha;

        bool allowNearest = plans.Any(plan => plan.CanRemap);
        bool allowKeep = plans.Any(plan => plan.BeforeIndexes is not null) || !allowNearest;
        string? notice = v1 && plans.Any(plan => plan.ChangesColors && plan.SourcePalette is { } palette
            && file.Sprites.Any(other => !ReferenceEquals(other, plan.Sprite) && ReferenceEquals(other.Palette, palette)))
            ? "Other sprites using affected SFFv1 palettes may look different if their indexes are unchanged."
            : null;
        var mapping = await Dialogs.PromptPaletteIndexMapping(this, allowNearest,
            allowNearest ? null : "Nearest-color matching is unavailable for the selected sprites.", notice,
            multiple: true, allowKeep: allowKeep,
            keepDisabledReason: allowKeep ? null : "These sprites have no indexes to keep.");
        if (mapping is null || !ReferenceEquals(_file, file)) return;

        if (mapping == ImportIndexChoice.Nearest)
        {
            foreach (var plan in plans.Where(plan => !plan.CanRemap).ToList())
            {
                skipped.Add(SkipPaletteSprite(plan.Sprite,
                    "cannot remap linked or borrowed pixels, or an indexed sprite with no palette"));
                plans.Remove(plan);
            }
        }
        else
        {
            // A true-color sprite has no indexes to keep, so Keep leaves it alone.
            foreach (var plan in plans.Where(plan => plan.IsDirect).ToList())
            {
                skipped.Add(SkipPaletteSprite(plan.Sprite, "is true color, so there are no indexes to keep"));
                plans.Remove(plan);
            }
        }
        if (plans.Count == 0)
        {
            ReportPaletteBatch("Import Palette into Sprite", 0, 0, skipped);
            return;
        }

        int directGroup = 0;
        int directIndex = 0;
        if (plans.Any(plan => plan.IsDirect))
        {
            var first = plans.First(plan => plan.IsDirect);
            var key = await Dialogs.PromptNewPaletteKey(this, first.Sprite.Group, NextFreePaletteIndex(first.Sprite.Group), SpriteFieldMax);
            if (key is null || !ReferenceEquals(_file, file)) return;
            (directGroup, directIndex) = key.Value;
        }

        if (!TryBeginBusy()) return;
        try
        {
            for (int i = 0; i < plans.Count; i++)
            {
                var plan = plans[i];
                await BusyStep("Importing palette", i, plans.Count, $"{plan.Sprite.Group},{plan.Sprite.Index}");
                if (mapping == ImportIndexChoice.Nearest)
                {
                    if (plan.IsDirect)
                        plan.AfterIndexes = ImageImport.MatchToPalette(plan.BeforeDirect!, targetColors);
                    else if (plan.BeforeIndexes is { } indexes)
                    {
                        var remapped = ImageImport.RemapIndexes(indexes, plan.SourcePalette!, targetColors);
                        if (!remapped.AsSpan().SequenceEqual(indexes)) plan.AfterIndexes = remapped;
                    }
                }
                if (v1)
                    plan.AfterPalette = plan.BeforePalette;
                else if (plan.IsDirect)
                    plan.AfterPalette = NewPaletteForSprite(file, directGroup, directIndex, targetColors, plans, plan);
                else
                    plan.AfterPalette = NewPaletteForSprite(file, plan.BeforePalette!.Group, 0, targetColors, plans, plan);
            }
        }
        finally { EndBusy(); }

        List<Palette> added = v1 ? [] : plans.Select(plan => plan.AfterPalette!).ToList();
        var colorChanges = BuildPaletteColorChanges(plans, targetColors, dirtyExternalPalettes: false);
        ApplySpritePaletteBatch(file, plans, colorChanges, added,
            "Import Palette into Sprite", selected, skipped, unchanged: 0,
            droppedAlpha ? " without alpha" : "", Path.GetFileName(path));
    }

    private bool CanRemapSpriteIndexes(SffFile file, Sprite sprite, Palette? current) =>
        sprite.SharesWith is null
        && !file.Sprites.Any(other => ReferenceEquals(other.SharesWith, sprite))
        && (sprite.Direct is not null || (sprite.Indexed is not null && current is not null));

    private static Sprite SpriteNeedingMostColors(IEnumerable<Sprite> sprites) =>
        sprites.OrderByDescending(sprite => sprite.Indexed is { Length: > 0 } indexes
            ? indexes.Max(index => (int)index) + 1 : 0).First();

    private static Palette NewPaletteForSprite(SffFile file, int group, int preferredIndex, Rgba[] colors,
        IEnumerable<SpritePaletteBatchPlan> reservedPlans, SpritePaletteBatchPlan currentPlan)
    {
        var used = file.Palettes.Where(palette => palette.Group == group).Select(palette => palette.Index).ToHashSet();
        foreach (var plan in reservedPlans)
            if (!ReferenceEquals(plan, currentPlan) && plan.AfterPalette is { } palette && palette.Group == group)
                used.Add(palette.Index);
        int index = preferredIndex;
        while (used.Contains(index)) index++;
        return new Palette
        {
            Group = group,
            Index = index,
            Colors = (Rgba[])colors.Clone(),
            Source = PaletteSource.Embedded,
        };
    }

    private static List<PaletteBatchColorChange> BuildPaletteColorChanges(
        IEnumerable<SpritePaletteBatchPlan> plans, Rgba[] colors, bool dirtyExternalPalettes)
    {
        var changes = new List<PaletteBatchColorChange>();
        var seen = new HashSet<Palette>();
        foreach (var plan in plans)
        {
            if (!plan.ChangesColors || plan.BeforePalette is not { } palette || !seen.Add(palette)) continue;
            changes.Add(new(palette, palette.Colors.ToArray(), (Rgba[])colors.Clone(),
                palette.Dirty, dirtyExternalPalettes && palette.Source == PaletteSource.External || palette.Dirty));
        }
        return changes;
    }

    private void ApplySpritePaletteBatch(SffFile file, List<SpritePaletteBatchPlan> plans,
        List<PaletteBatchColorChange> colorChanges, List<Palette> added, string action,
        List<Sprite> selected, List<string> skipped, int unchanged, string alphaNote, string? sourceName = null,
        Action? moveIn = null, Action? moveOut = null)
    {
        if (plans.Count == 0) return;
        var beforeSprites = plans.Select(plan => (
            Sprite: plan.Sprite, Palette: plan.BeforePalette, Indexed: plan.BeforeIndexes,
            Direct: plan.BeforeDirect, Dirty: plan.PixelsDirtyBefore)).ToList();
        var afterSprites = plans.Select(plan => (
            Sprite: plan.Sprite, Palette: plan.AfterPalette, Indexed: plan.AfterIndexes,
            Dirty: plan.AfterIndexes is null ? plan.PixelsDirtyBefore : true)).ToList();

        Edit($"{action.ToLowerInvariant()} for {plans.Count} sprites",
            () =>
            {
                moveIn?.Invoke();
                foreach (var change in colorChanges)
                {
                    change.Palette.Colors = change.After;
                    change.Palette.Dirty = change.DirtyAfter;
                }
                foreach (var state in afterSprites)
                {
                    state.Sprite.Palette = state.Palette;
                    if (state.Indexed is not null)
                    {
                        state.Sprite.Indexed = state.Indexed;
                        state.Sprite.Direct = null;
                    }
                    state.Sprite.PixelsDirty = state.Dirty;
                }
                foreach (var palette in added) file.Palettes.Add(palette);
            },
            () =>
            {
                foreach (var palette in added) file.Palettes.Remove(palette);
                foreach (var change in colorChanges)
                {
                    change.Palette.Colors = change.Before;
                    change.Palette.Dirty = change.DirtyBefore;
                }
                foreach (var state in beforeSprites)
                {
                    state.Sprite.Palette = state.Palette;
                    state.Sprite.Indexed = state.Indexed;
                    state.Sprite.Direct = state.Direct;
                    state.Sprite.PixelsDirty = state.Dirty;
                }
                moveOut?.Invoke();
            });

        RestoreSpriteSelection(selected);
        int applied = plans.Count;
        string summary = sourceName is null
            ? $"Applied preview palette{alphaNote} to {applied} sprite(s)."
            : $"Imported {sourceName}{alphaNote} for {applied} sprite(s).";
        ReportPaletteBatch(action, applied, unchanged, skipped, summary);
    }

    private void RestoreSpriteSelection(IEnumerable<Sprite> selected)
    {
        var file = _file;
        if (file is null) return;
        var rows = selected.Select(sprite => file.PositionOf(sprite))
            .Where(index => index >= 0).Select(index => _visible.IndexOf(index)).Where(row => row >= 0).ToList();
        ApplySpriteRowSelection(rows, suppressEvents: true);
        _spriteSelectionAnchor = _current is null ? -1 : _visible.IndexOf(file.PositionOf(_current));
        RefreshAssignPaletteButton();
    }

    private static string SkipPaletteSprite(Sprite sprite, string reason) =>
        $"skipped sprite {sprite.Group},{sprite.Index}: {reason}";

    private void ReportPaletteBatch(string action, int applied, int unchanged, List<string> skipped, string? summary = null)
    {
        foreach (string item in skipped) CrashLog.Error(action, item);
        ShowSkipped("Sprites Skipped", skipped);
        if (summary is not null)
            StatusText.Text = summary + (skipped.Count > 0 ? $" Skipped {skipped.Count} (see crash.log)." : "");
        else if (applied > 0)
            StatusText.Text = $"Applied to {applied} sprite(s)"
                + (unchanged > 0 ? $"; {unchanged} already matched" : "")
                + (skipped.Count > 0 ? $"; skipped {skipped.Count} (see crash.log)" : ".");
        else if (skipped.Count > 0)
            StatusText.Text = $"No sprites changed; skipped {skipped.Count} (see crash.log).";
        else if (unchanged > 0)
            StatusText.Text = "All selected sprites already use this palette.";
        else
            StatusText.Text = "No selected sprites could be changed.";
    }
}
