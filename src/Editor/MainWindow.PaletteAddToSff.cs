using Avalonia.Controls;
using Avalonia.Interactivity;
using Sff;

namespace Editor;

/// <summary>
/// Add to SFF: moves an external palette (temp, or imported from a file) into an SFF v2 at a
/// chosen group and number, e.g. a 1,n alt palette that no sprite uses. A taken key asks to
/// replace that palette's colors; it keeps its key, sprites and the palettes linked to it.
/// Several at once take the next free numbers from the chosen key, skipping taken ones.
/// </summary>
public partial class MainWindow
{
    private async void OnAddPaletteToSff(object? sender, RoutedEventArgs e) => await AddPalettesToSff(SelectedPalettesInOrder());

    private static string AddToSffTip(int count) => count > 1
        ? "Moves the selected palettes into the SFF, numbered up from a group and number you choose."
        : "Moves this palette into the SFF at a group and number you choose.";

    /// <summary>The palette list's right-click entry; dimmed when it can't run.</summary>
    private MenuItem AddToSffMenuItem(List<Palette> palettes)
    {
        var item = MenuAction("Add to SFF", () => _ = AddPalettesToSff(palettes), AddToSffBlocker(palettes) is null);
        ToolTip.SetTip(item, AddToSffTip(palettes.Count));
        return item;
    }

    /// <summary>Palettes menu: same state as the right-click entry, for the selected palettes.</summary>
    private void SyncAddToSffMenuItem()
    {
        var palettes = SelectedPalettesInOrder();
        AddPaletteToSffMenuItem.IsEnabled = AddToSffBlocker(palettes) is null;
        ToolTip.SetTip(AddPaletteToSffMenuItem, AddToSffTip(palettes.Count));
    }

    /// <summary>Why Add to SFF is unavailable for these palettes, or null when it can run.</summary>
    private string? AddToSffBlocker(IReadOnlyList<Palette> palettes) =>
        palettes.Count == 0 ? "Select a palette." : palettes.Select(p => AddToSffBlocker(p)).FirstOrDefault(b => b is not null);

    /// <summary>One palette asks for its key (a taken key offers to replace); several go to AddManyPalettesToSff.</summary>
    private async Task AddPalettesToSff(List<Palette> palettes)
    {
        if (palettes.Count == 1) await AddPaletteToSff(palettes[0]);
        else if (palettes.Count > 1) await AddManyPalettesToSff(palettes);
    }

    /// <summary>Why Add to SFF is unavailable for this palette, or null when it can run.</summary>
    private string? AddToSffBlocker(Palette? palette) =>
        _file is not { } file ? "Open a sprite file first."
        : SffVersions.IsV1(file.Version) ? "SFF v1 keeps palettes in its sprites; apply it to sprites instead."
        : palette is not { Source: PaletteSource.External } ? "Available for imported and temp palettes."
        : null;

    private async Task AddPaletteToSff(Palette palette)
    {
        try
        {
            if (AddToSffBlocker(palette) is not null || _file is not { } file || !file.Palettes.Contains(palette))
            {
                CrashLog.Error("AddPaletteToSff", AddToSffBlocker(palette) ?? "palette is not in the list");
                return;
            }
            if (HasPendingColorAdjust() && ReferenceEquals(_editingPalette, palette))
            {
                var pending = await Dialogs.AskAboutPendingColorAdjust(this);
                if (pending == PendingAdjustChoice.Cancel) return;
                if (pending == PendingAdjustChoice.Commit) CommitPendingColorAdjust();
                else DiscardPendingColorAdjust();
            }

            var characterIndexes = file.Palettes.Where(p => p.Source == PaletteSource.Embedded && p.Group == 1)
                .Select(p => p.Index).ToHashSet();
            int nextFree = 1;
            while (characterIndexes.Contains(nextFree)) nextFree++;
            if (await Dialogs.PromptNewPaletteKey(this, 1, nextFree, SpriteFieldMax) is not { } key) return;
            var (group, index) = key;
            if (!ReferenceEquals(_file, file) || !file.Palettes.Contains(palette) || palette.Source != PaletteSource.External) return;

            string name = PaletteDisplayName(file, palette);
            var existing = file.Palettes.FirstOrDefault(p => p.Source == PaletteSource.Embedded && p.Group == group && p.Index == index);
            if (existing is null) MovePaletteIntoSff(file, palette, group, index, name);
            else await ReplaceSffPaletteColors(file, palette, existing, name);
        }
        catch (Exception ex)
        {
            await Failed("AddPaletteToSff", ex);
        }
    }

    /// <summary>One prompt for the group and starting number; each palette takes the next free
    /// number from there, in list order, in one undo step.</summary>
    private async Task AddManyPalettesToSff(List<Palette> palettes)
    {
        try
        {
            if (AddToSffBlocker(palettes) is not null || _file is not { } file || !palettes.All(file.Palettes.Contains))
            {
                CrashLog.Error("AddManyPalettesToSff", AddToSffBlocker(palettes) ?? "a palette is not in the list");
                return;
            }
            if (HasPendingColorAdjust() && _editingPalette is { } editing && palettes.Contains(editing))
            {
                var pending = await Dialogs.AskAboutPendingColorAdjust(this);
                if (pending == PendingAdjustChoice.Cancel) return;
                if (pending == PendingAdjustChoice.Commit) CommitPendingColorAdjust();
                else DiscardPendingColorAdjust();
            }

            var characterIndexes = file.Palettes.Where(p => p.Source == PaletteSource.Embedded && p.Group == 1)
                .Select(p => p.Index).ToHashSet();
            int nextFree = 1;
            while (characterIndexes.Contains(nextFree)) nextFree++;
            if (await Dialogs.PromptNewPaletteKey(this, 1, nextFree, SpriteFieldMax,
                    "Choose the Group and starting Index. Each palette takes the next free number from there.") is not { } key) return;
            var (group, start) = key;
            if (!ReferenceEquals(_file, file)
                || !palettes.All(p => file.Palettes.Contains(p) && p.Source == PaletteSource.External)) return;

            var taken = file.Palettes.Where(p => p.Source == PaletteSource.Embedded && p.Group == group)
                .Select(p => p.Index).ToHashSet();
            var indexes = new List<int>();
            for (long next = start; indexes.Count < palettes.Count && next <= SpriteFieldMax; next++)
                if (!taken.Contains((int)next)) indexes.Add((int)next);
            if (indexes.Count < palettes.Count)
            {
                CrashLog.Error("AddManyPalettesToSff", $"refused: group {group} has {indexes.Count} free numbers from {start}, {palettes.Count} needed");
                await Dialogs.ShowMessage(this, "Cannot Add Palettes",
                    $"Group {group} has only {indexes.Count} free numbers from {start} for {palettes.Count} palettes.");
                return;
            }

            var before = palettes.Select(p => (p.Group, p.Index, p.SourcePath, p.Dirty)).ToList();
            Edit("add palettes to sff",
                () =>
                {
                    for (int i = 0; i < palettes.Count; i++)
                    {
                        palettes[i].Source = PaletteSource.Embedded;
                        (palettes[i].Group, palettes[i].Index, palettes[i].SourcePath, palettes[i].Dirty) = (group, indexes[i], null, false);
                    }
                    SelectPalette(palettes[0]);
                },
                () =>
                {
                    for (int i = 0; i < palettes.Count; i++)
                    {
                        palettes[i].Source = PaletteSource.External;
                        (palettes[i].Group, palettes[i].Index, palettes[i].SourcePath, palettes[i].Dirty) = before[i];
                    }
                    SelectPalette(palettes[0]);
                });
            CrashLog.Trace($"[palettes] added {palettes.Count} palettes to the SFF in group {group}: {string.Join(", ", indexes)}");
            StatusText.Text = $"Added {palettes.Count} palettes to the SFF in group {group}: {string.Join(", ", indexes)}.";
        }
        catch (Exception ex)
        {
            await Failed("AddManyPalettesToSff", ex);
        }
    }

    private void MovePaletteIntoSff(SffFile file, Palette palette, int group, int index, string name)
    {
        var before = (palette.Group, palette.Index, palette.SourcePath, palette.Dirty);
        Edit("add palette to sff",
            () =>
            {
                palette.Source = PaletteSource.Embedded;
                (palette.Group, palette.Index, palette.SourcePath, palette.Dirty) = (group, index, null, false);
                SelectPalette(palette);
            },
            () =>
            {
                palette.Source = PaletteSource.External;
                (palette.Group, palette.Index, palette.SourcePath, palette.Dirty) = before;
                SelectPalette(palette);
            });
        CrashLog.Trace($"[palettes] added {name} to the SFF as {group},{index}");
        StatusText.Text = $"Added {name} to the SFF as palette {group},{index}.";
    }

    private async Task ReplaceSffPaletteColors(SffFile file, Palette palette, Palette existing, string name)
    {
        string key = $"{existing.Group},{existing.Index}";
        if (existing.Colors.Length != palette.Colors.Length)
        {
            CrashLog.Error("AddPaletteToSff", $"refused: {name} has {palette.Colors.Length} colors, {key} has {existing.Colors.Length}");
            await Dialogs.ShowMessage(this, "Cannot Replace Palette",
                $"{name} has {palette.Colors.Length} colors and palette {key} has {existing.Colors.Length}. Pick another number.");
            return;
        }
        int users = file.Sprites.Count(s => ReferenceEquals(s.Palette, existing));
        int linkers = file.Palettes.Count(p => ReferenceEquals(p.LinksTo, existing));
        var notes = new List<string> { $"Palette {key} already exists. Replace its colors with {name}?" };
        if (existing.LinksTo is { } target) notes.Add($"It links to {PaletteKey(file, target)} and gets its own colors.");
        if (users > 0) notes.Add($"{users} sprite{(users == 1 ? " uses" : "s use")} it.");
        if (linkers > 0) notes.Add($"{linkers} linked palette{(linkers == 1 ? " follows" : "s follow")}.");
        if (!await Dialogs.Confirm(this, "Replace Palette", string.Join(" ", notes), "Replace")) return;
        if (!ReferenceEquals(_file, file) || !file.Palettes.Contains(palette) || !file.Palettes.Contains(existing)) return;

        var state = PaletteLinkState.Capture(existing);
        var colors = palette.Colors.ToArray();
        int at = file.Palettes.IndexOf(palette);
        bool wasEditing = ReferenceEquals(_editingPalette, palette);
        Edit("replace palette colors",
            () =>
            {
                existing.LinksTo = null; // set first, or Colors would write through to the link target
                existing.Colors = colors.ToArray();
                file.Palettes.Remove(palette);
                if (wasEditing) _editingPalette = existing;
                SelectPalette(existing);
            },
            () =>
            {
                state.Restore(existing);
                file.Palettes.Insert(Math.Min(at, file.Palettes.Count), palette);
                if (wasEditing) _editingPalette = palette;
                SelectPalette(palette);
            });
        CrashLog.Trace($"[palettes] replaced {key}'s colors with {name}");
        StatusText.Text = $"Replaced the colors of palette {key} with {name}.";
    }
}
