using Avalonia.Controls;
using Avalonia.Interactivity;
using Sff;

namespace Editor;

/// <summary>
/// Add to SFF: moves an external palette (temp, or imported from a file) into an SFFv2 at a
/// chosen group and number, e.g. a 1,n alt palette that no sprite uses. A taken key asks to
/// replace that palette's colors; it keeps its key, sprites and the palettes linked to it.
/// </summary>
public partial class MainWindow
{
    private async void OnAddPaletteToSff(object? sender, RoutedEventArgs e)
    {
        if (PaletteList?.SelectedItems?.Count == 1 && (PaletteList.SelectedItem as PaletteRow)?.Palette is { } palette)
            await AddPaletteToSff(palette);
    }

    private const string AddToSffTip = "Moves this palette into the SFF at a group and number you choose.";

    /// <summary>The palette list's right-click entry; dimmed with the reason when it can't run.</summary>
    private MenuItem AddToSffMenuItem(Palette? palette)
    {
        string? blocker = palette is null ? "Select one palette." : AddToSffBlocker(palette);
        var item = MenuAction("Add to SFF", () => { if (palette is not null) _ = AddPaletteToSff(palette); }, blocker is null);
        ToolTip.SetTip(item, blocker ?? AddToSffTip);
        return item;
    }

    /// <summary>Palettes menu: same state as the right-click entry, for the selected palette.</summary>
    private void SyncAddToSffMenuItem()
    {
        var palette = PaletteList?.SelectedItems?.Count == 1 ? (PaletteList.SelectedItem as PaletteRow)?.Palette : null;
        string? blocker = palette is null ? "Select one palette." : AddToSffBlocker(palette);
        AddPaletteToSffMenuItem.IsEnabled = blocker is null;
        ToolTip.SetTip(AddPaletteToSffMenuItem, blocker ?? AddToSffTip);
    }

    /// <summary>Why Add to SFF is unavailable for this palette, or null when it can run.</summary>
    private string? AddToSffBlocker(Palette? palette) =>
        _file is not { } file ? "Open a sprite file first."
        : SffVersions.IsV1(file.Version) ? "SFFv1 keeps palettes in its sprites; apply it to sprites instead."
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
