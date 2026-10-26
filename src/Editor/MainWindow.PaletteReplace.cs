using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Sff;

namespace Editor;

/// <summary>
/// Palettes > Replace: loads a palette file over the active palette. SFF palettes take the new
/// colors in place (key, sprites and linked palettes stay); imported, temp and standalone
/// palettes are swapped for the new file at the same spot.
/// </summary>
public partial class MainWindow
{
    private async void OnReplacePalette(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (PreviewOverridePalette() is not { } target)
            {
                StatusText.Text = "Choose a palette in the list first.";
                return;
            }
            if (!await SettlePendingColorAdjust()) return;
            var files = await PickOpen(new FilePickerOpenOptions
            {
                Title = $"Replace Palette {PaletteLabel(target)}",
                FileTypeFilter = [PaletteFilesType],
            });
            if (files.Count == 0) return;
            if (files[0].TryGetLocalPath() is not { } path)
            {
                CrashLog.Error("OnReplacePalette", $"{files[0].Name}: selected file has no local path");
                await Dialogs.ShowMessage(this, "Cannot Replace Palette", $"{files[0].Name}: selected file has no local path");
                return;
            }
            Palette loaded;
            try { loaded = PaletteFileImport.Read(path); }
            catch (Exception ex)
            {
                CrashLog.Write($"OnReplacePalette {path}", ex);
                await Dialogs.ShowMessage(this, "Cannot Replace Palette", $"{Path.GetFileName(path)}: {ex.Message}");
                return;
            }
            if (!ReferenceEquals(PreviewOverridePalette(), target)) return; // the selection moved while picking

            if (_file is null) await ReplaceStandalonePalette(target, loaded, path);
            else if (target.Source == PaletteSource.External) SwapExternalPalette(_file, target, loaded, path);
            else await ReplaceEmbeddedPaletteColors(_file, target, loaded.Colors, Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            await Failed("OnReplacePalette", ex);
        }
    }

    /// <summary>No SFF open: closes the palette document and opens the file in its place.</summary>
    private async Task ReplaceStandalonePalette(Palette old, Palette loaded, string path)
    {
        int at = _standalonePalettes.IndexOf(old);
        if (at < 0) return;
        if (_standalonePalettes.FirstOrDefault(p => !ReferenceEquals(p, old) && p.SourcePath is not null && PathsEqual(p.SourcePath, path)) is { } open)
        {
            await Dialogs.ShowMessage(this, "Cannot Replace Palette", $"{Path.GetFileName(path)} is already open as {PaletteLabel(open)}.");
            return;
        }
        if (old.Dirty && !await Dialogs.Confirm(this, "Close Palette File",
                $"{Path.GetFileName(old.SourcePath) ?? "New palette"} has unsaved changes.", "Discard")) return;
        if (_file is not null || !ReferenceEquals(_standalonePalettes.ElementAtOrDefault(at), old)) return;

        string label = PaletteLabel(old);
        loaded.SourcePath = path;
        loaded.Source = PaletteSource.External;
        (loaded.Group, loaded.Index) = (old.Group, old.Index);
        loaded.Dirty = false;
        _standalonePalettes[at] = loaded;
        _undo.MarkSaved(StandalonePaletteKindFor(loaded));
        _standalonePaletteOverride = at;
        ShowStandalonePalette(loaded);
        CrashLog.Trace($"[palettes] replaced standalone {label} with {path}");
        StatusText.Text = $"Replaced {label} with {Path.GetFileName(path)}.";
    }

    /// <summary>Imported or temp palette beside an SFF: the file takes its place, number and sprites.</summary>
    private void SwapExternalPalette(SffFile file, Palette old, Palette loaded, string path)
    {
        int at = file.Palettes.IndexOf(old);
        if (at < 0) return;
        string label = PaletteDisplayName(file, old);
        loaded.SourcePath = path;
        loaded.Source = PaletteSource.External;
        (loaded.Group, loaded.Index) = (old.Group, old.Index);
        loaded.Dirty = false;
        var users = file.Sprites.Where(s => ReferenceEquals(s.Palette, old)).ToList();
        Edit("replace palette",
            () =>
            {
                file.Palettes[at] = loaded;
                foreach (var s in users) s.Palette = loaded;
                RetargetPaletteEditor(old, loaded);
                SelectPalette(loaded);
            },
            () =>
            {
                file.Palettes[at] = old;
                foreach (var s in users) s.Palette = old;
                RetargetPaletteEditor(loaded, old);
                SelectPalette(old);
            }, kind: ExternalPaletteKind);
        CrashLog.Trace($"[palettes] replaced {label} with {path}");
        StatusText.Text = $"Replaced {label} with {Path.GetFileName(path)}.";
    }

    /// <summary>SFF palette: new colors in place. v1 pads to 256; v2 takes the file's count and
    /// refuses to shrink below an index its sprites use. A link becomes its own colors.</summary>
    private async Task ReplaceEmbeddedPaletteColors(SffFile file, Palette pal, Rgba[] colors, string fileName)
    {
        string key = PaletteKey(file, pal);
        if (SffVersions.IsV1(file.Version) && colors.Length < 256)
        {
            var padded = new Rgba[256];
            Array.Copy(colors, padded, colors.Length);
            Array.Fill(padded, new Rgba(0, 0, 0, 255), colors.Length, 256 - colors.Length);
            colors = padded;
        }
        // Shrinking: sprites reading these colors (its own and linked palettes') must fit and be decoded.
        var tooHigh = colors.Length >= pal.Colors.Length ? new List<Sprite>() : file.Sprites
            .Where(s => ReferenceEquals(s.Palette, pal) || ReferenceEquals(s.Palette?.LinksTo, pal))
            .Where(s => s.Indexed is { } indices ? indices.Any(i => i >= colors.Length) : s.DecodeError is not null)
            .ToList<Sprite>();
        if (tooHigh.Count > 0)
        {
            string names = string.Join(", ", tooHigh.Take(8).Select(s => $"{s.Group},{s.Index}")) + (tooHigh.Count > 8 ? ", …" : "");
            CrashLog.Error("OnReplacePalette", $"refused: {fileName} has {colors.Length} colors; sprites over it or undecoded: {names}");
            await Dialogs.ShowMessage(this, "Cannot Replace Palette",
                $"{fileName} has {colors.Length} colors, but sprites using palette {key} need more or couldn't be checked: {names}.");
            return;
        }

        var state = PaletteLinkState.Capture(pal);
        var linkedTo = pal.LinksTo;
        int oldCount = pal.Colors.Length;
        var after = colors.ToArray();
        Edit("replace palette",
            () =>
            {
                pal.LinksTo = null; // first, or Colors would write through to the link target
                pal.Colors = after.ToArray();
                RetargetPaletteEditor(pal, pal);
                SelectPalette(pal);
            },
            () =>
            {
                state.Restore(pal);
                RetargetPaletteEditor(pal, pal);
                SelectPalette(pal);
            });
        var notes = new List<string>();
        if (after.Length != oldCount) notes.Add($"{oldCount} to {after.Length} colors");
        if (linkedTo is not null) notes.Add($"no longer linked to {PaletteKey(file, linkedTo)}");
        CrashLog.Trace($"[palettes] replaced {key}'s colors with {fileName} ({oldCount} -> {after.Length})");
        StatusText.Text = $"Replaced palette {key} with {fileName}" + (notes.Count > 0 ? $" ({string.Join(", ", notes)})." : ".");
    }

    /// <summary>Moves the color editor from one palette to another, closing it when its swatch no longer exists.</summary>
    private void RetargetPaletteEditor(Palette from, Palette to)
    {
        if (!ReferenceEquals(_editingPalette, from)) return;
        if (_selectedSwatch < to.Colors.Length)
        {
            _editingPalette = to;
            ResetColorAdjustBase();
            return;
        }
        _editingPalette = null;
        _editingView = null;
        _selectedSwatch = -1;
        ColorEditor.IsEnabled = false;
        ASlider.IsEnabled = false;
        ResetColorAdjustBase();
    }

    /// <summary>Selects a standalone palette in the list and opens it in the color editor.</summary>
    private void ShowStandalonePalette(Palette palette)
    {
        PaletteFilterBox.Text = "";
        RebuildPaletteList();
        _suppressEvents = true;
        if (PaletteList.ItemsSource is IEnumerable<PaletteRow> rows)
            PaletteList.SelectedIndex = rows.ToList().FindIndex(row => ReferenceEquals(row.Palette, palette));
        _suppressEvents = false;
        _editingPalette = palette;
        _editingView = BigSwatches;
        _selectedSwatch = palette.Colors.Length > 0 ? 0 : -1;
        ColorEditor.IsEnabled = true;
        ASlider.IsEnabled = CanEditPaletteAlpha(palette);
        if (_selectedSwatch >= 0)
        {
            var color = palette.Colors[_selectedSwatch];
            _suppressEvents = true;
            RSlider.Value = color.R; GSlider.Value = color.G; BSlider.Value = color.B; ASlider.Value = color.A;
            _suppressEvents = false;
        }
        ResetColorAdjustBase();
        RefreshPaletteTab();
        UpdateTitle();
    }
}
