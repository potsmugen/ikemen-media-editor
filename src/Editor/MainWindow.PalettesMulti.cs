using Avalonia.Interactivity;
using Sff;

namespace Editor;

/// <summary>
/// Palette list actions on a multi-selection. Delete and Convert Depth share one path for one
/// or many; Duplicate, Export, Add to SFF and Save Original File get a many-palette variant.
/// </summary>
public partial class MainWindow
{
    /// <summary>The selected palettes, in list order, when more than one row is selected.</summary>
    private List<Palette>? MultiSelectedPalettes() =>
        SelectedPalettesInOrder() is { Count: > 1 } many ? many : null;

    // ---- delete -----------------------------------------------------------

    private async void OnDeletePalette(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_file is null)
            {
                var closing = MultiSelectedPalettes()
                    ?? (_standalonePaletteOverride >= 0 && _standalonePaletteOverride < _standalonePalettes.Count
                        ? new List<Palette> { _standalonePalettes[_standalonePaletteOverride] } : null);
                if (closing is not null) await CloseStandalonePalettes(closing);
                return;
            }
            var doomed = MultiSelectedPalettes()
                ?? (_paletteOverride >= 0 && _paletteOverride < _file.Palettes.Count
                    ? new List<Palette> { _file.Palettes[_paletteOverride] } : null);
            if (doomed is null)
            {
                StatusText.Text = "Choose a palette in the list first; the sprite's own cannot be deleted from here.";
                return;
            }
            await DeletePalettes(_file, doomed);
        }
        catch (Exception ex)
        {
            await Failed("OnDeletePalette", ex);
        }
    }

    /// <summary>No SFF open: closes the palette files, asking once if any have unsaved changes.</summary>
    private async Task CloseStandalonePalettes(List<Palette> closing)
    {
        var dirty = closing.Where(p => p.Dirty).ToList();
        if (dirty.Count > 0)
        {
            bool one = closing.Count == 1;
            string message = one
                ? $"{Path.GetFileName(closing[0].SourcePath) ?? "New palette"} has unsaved changes."
                : $"{dirty.Count} of the selected palette files have unsaved changes.";
            if (!await Dialogs.Confirm(this, one ? "Close Palette File" : "Close Palette Files", message, "Discard")) return;
        }
        int first = closing.Select(p => _standalonePalettes.IndexOf(p)).Where(i => i >= 0).DefaultIfEmpty(-1).Min();
        if (first < 0) return;
        _standalonePalettes.RemoveAll(closing.Contains);
        _standalonePaletteOverride = Math.Min(first, _standalonePalettes.Count - 1);
        _editingPalette = null;
        _editingView = null;
        _selectedSwatch = -1;
        ColorEditor.IsEnabled = false;
        ASlider.IsEnabled = false;
        RebuildPaletteList();
        RefreshPaletteTab();
        UpdateTitle();
    }

    /// <summary>
    /// Deletes SFF-list palettes in one undo step. A sprite on a deleted palette moves to the first
    /// surviving palette along its links, else the first surviving palette in the list. Surviving
    /// palettes that link to a deleted one are unlinked with their current colors kept.
    /// </summary>
    private async Task DeletePalettes(SffFile file, List<Palette> doomed)
    {
        var doomedSet = new HashSet<Palette>(doomed, ReferenceEqualityComparer.Instance);
        bool one = doomed.Count == 1;
        string noun = one ? "this palette" : "these palettes";

        Palette? FallbackFor(Palette pal)
        {
            var seen = new HashSet<Palette>(ReferenceEqualityComparer.Instance);
            for (var target = pal.LinksTo; target is not null && seen.Add(target); target = target.LinksTo)
                if (!doomedSet.Contains(target)) return target;
            return file.Palettes.FirstOrDefault(p => !doomedSet.Contains(p));
        }

        var linkers = new List<Palette>();
        var moves = new List<(Sprite Sprite, Palette From, Palette To)>();
        bool Plan()
        {
            linkers = file.Palettes.Where(p => !doomedSet.Contains(p) && p.LinksTo is { } t && doomedSet.Contains(t)).ToList();
            moves = [];
            foreach (var pal in doomed)
            {
                var users = file.Sprites.Where(s => ReferenceEquals(s.Palette, pal)).ToList();
                if (users.Count == 0) continue;
                var fallback = FallbackFor(pal);
                if (fallback is null || !file.Palettes.Contains(fallback))
                {
                    ShowPopup(one ? "Delete Palette" : "Delete Palettes",
                        $"Can't delete palette {PaletteLabel(pal)}: sprites use it and no valid fallback palette is available.");
                    string target = fallback is null ? "none" : $"{fallback.Group},{fallback.Index} is not in the palette table";
                    CrashLog.Error("OnDeletePalette", $"refused: sprites use {pal.Group},{pal.Index}, and fallback target is {target}");
                    return false;
                }
                Palette to = fallback;
                moves.AddRange(users.Select(s => (s, pal, to)));
            }
            return true;
        }

        if (!Plan()) return;
        if (HasPendingColorAdjust() && _editingPalette is { Source: PaletteSource.Embedded } editingPalette
            && (doomedSet.Contains(editingPalette) || linkers.Contains(editingPalette)))
        {
            var pending = await Dialogs.AskAboutPendingColorAdjust(this);
            if (pending == PendingAdjustChoice.Cancel) return;
            if (!ReferenceEquals(_file, file) || !doomed.All(file.Palettes.Contains)) return;
            if (pending == PendingAdjustChoice.Commit) CommitPendingColorAdjust();
            else DiscardPendingColorAdjust();
        }
        if (!ReferenceEquals(_file, file) || !doomed.All(file.Palettes.Contains) || !Plan()) return;

        if (linkers.Count > 0 || moves.Count > 0)
        {
            string FallbackName(Palette p) => p.Source == PaletteSource.External
                ? PaletteDisplayName(file, p)
                : $"palette {PaletteKey(file, p)}";
            var impact = new List<string>();
            if (linkers.Count == 1)
                impact.Add($"1 palette links to {noun}. It will be unlinked with its current colors preserved.");
            else if (linkers.Count > 1)
                impact.Add($"{linkers.Count} palettes link to {noun}. They will be unlinked with their current colors preserved.");
            if (moves.Count > 0)
            {
                string targets = string.Join(", ", moves.Select(m => m.To).Distinct().Select(FallbackName));
                impact.Add(moves.Count == 1
                    ? $"1 sprite uses {noun} and will be reassigned to {targets}."
                    : $"{moves.Count} sprites use {noun} and will be reassigned to {targets}.");
                impact.Add("Their displayed colors may change.");
            }
            if (!await Dialogs.Confirm(this, one ? "Delete Palette?" : $"Delete {doomed.Count} Palettes?",
                    string.Join(" ", impact), "Delete"))
                return;
        }

        if (!ReferenceEquals(_file, file) || !doomed.All(file.Palettes.Contains)) return;
        // Ascending positions, so reinserting in this order restores the list exactly.
        var positions = doomed.Select(p => (Palette: p, At: file.Palettes.IndexOf(p))).OrderBy(x => x.At).ToList();
        var selected = _paletteOverride >= 0 && _paletteOverride < file.Palettes.Count ? file.Palettes[_paletteOverride] : null;
        var reselect = selected is not null && doomedSet.Contains(selected) ? selected : doomed[0];
        var editedPalette = _editingPalette;
        bool editingPaletteRemoved = editedPalette is not null && doomedSet.Contains(editedPalette);
        var editingView = _editingView;
        int editingSwatch = _selectedSwatch;
        bool editorWasEnabled = ColorEditor.IsEnabled;
        var linkStates = linkers.Select(PaletteLinkState.Capture).ToList();

        void ClearRemovedPaletteEditor()
        {
            if (!editingPaletteRemoved) return;
            _editingPalette = null;
            _editingView = null;
            _selectedSwatch = -1;
            ColorEditor.IsEnabled = false;
            ASlider.IsEnabled = false;
            ResetColorAdjustBase();
        }

        void RestoreRemovedPaletteEditor()
        {
            if (!editingPaletteRemoved || editedPalette is null || editingView is null
                || editingSwatch < 0 || editingSwatch >= editedPalette.Colors.Length) return;
            _editingPalette = editedPalette;
            _editingView = editingView;
            _selectedSwatch = editingSwatch;
            var color = editedPalette.Colors[editingSwatch];
            SwatchInfo.Text = $"Index {editingSwatch}: RGB {color.R}, {color.G}, {color.B}, A {color.A}";
            _suppressEvents = true;
            RSlider.Value = color.R; GSlider.Value = color.G; BSlider.Value = color.B; ASlider.Value = color.A;
            _suppressEvents = false;
            ColorEditor.IsEnabled = editorWasEnabled;
            ASlider.IsEnabled = editorWasEnabled && CanEditPaletteAlpha(editedPalette);
            SpriteCanvas.PaintColorIndex = editingSwatch;
            ResetColorAdjustBase();
        }

        Edit(one ? "delete palette" : "delete palettes",
            () =>
            {
                for (int i = 0; i < linkers.Count; i++)
                    new PaletteLinkState(null, linkStates[i].Colors).Restore(linkers[i]);
                foreach (var move in moves) move.Sprite.Palette = move.To;
                file.Palettes.RemoveAll(doomedSet.Contains);
                _paletteOverride = -1;
                ClearRemovedPaletteEditor();
            },
            () =>
            {
                foreach (var (pal, at) in positions) file.Palettes.Insert(Math.Min(at, file.Palettes.Count), pal);
                for (int i = 0; i < linkers.Count; i++)
                    linkStates[i].Restore(linkers[i]);
                foreach (var move in moves) move.Sprite.Palette = move.From;
                _paletteOverride = file.Palettes.IndexOf(reselect);
                RestoreRemovedPaletteEditor();
            });
        CrashLog.Trace($"[palettes] deleted {string.Join(" ", doomed.Select(p => $"{p.Group},{p.Index}"))}; "
            + $"unlinked {linkers.Count} palette(s) with copied colors");
    }

    // ---- convert depth ----------------------------------------------------

    /// <summary>Depths every palette is already at or can convert to, where at least one converts.</summary>
    private static int[] DepthsForAll(SffFile file, IReadOnlyCollection<Palette> palettes) =>
        PaletteDepth.SupportedColorCounts.Where(colors =>
            palettes.All(p => p.Colors.Length == colors || PaletteDepth.CanConvert(file, p, colors))
            && palettes.Any(p => PaletteDepth.CanConvert(file, p, colors))).ToArray();

    /// <summary>Converts one or more palettes to a depth in one undo step; ones already at it are skipped.</summary>
    private async Task ConvertPaletteDepth(List<Palette> palettes)
    {
        try
        {
            if (_file is not { } file || palettes.Count == 0 || !palettes.All(file.Palettes.Contains)) return;
            bool one = palettes.Count == 1;
            var available = DepthsForAll(file, palettes);
            if (available.Length == 0)
            {
                await Dialogs.ShowMessage(this, "Convert Depth", one
                    ? "No different supported depth fits this palette's sprites."
                    : "No supported depth fits the sprites of every selected palette.");
                return;
            }

            var colors = await Dialogs.PromptPaletteDepth(this, palettes.Min(p => p.Colors.Length), available, palettes.Count);
            if (colors is null || !ReferenceEquals(_file, file) || !palettes.All(file.Palettes.Contains)) return;

            if (HasPendingColorAdjust() && _editingPalette is { } editing && palettes.Contains(editing))
            {
                var pending = await Dialogs.AskAboutPendingColorAdjust(this);
                if (pending == PendingAdjustChoice.Cancel) return;
                if (!ReferenceEquals(_file, file) || !palettes.All(file.Palettes.Contains)) return;
                if (pending == PendingAdjustChoice.Commit) CommitPendingColorAdjust();
                else DiscardPendingColorAdjust();
            }

            var changing = palettes.Where(p => p.Colors.Length != colors.Value).ToList();
            if (changing.Count == 0 || !changing.All(p => PaletteDepth.CanConvert(file, p, colors.Value)))
            {
                await Dialogs.ShowMessage(this, "Convert Depth", one
                    ? "This palette no longer fits the selected depth."
                    : "A selected palette no longer fits the selected depth.");
                return;
            }

            var before = changing.Select(p => (Rgba[])p.Colors.Clone()).ToList();
            var after = changing.Select(p => PaletteDepth.Convert(p, colors.Value)).ToList();

            void Apply(List<Rgba[]> values)
            {
                for (int i = 0; i < changing.Count; i++)
                {
                    var palette = changing[i];
                    palette.Colors = (Rgba[])values[i].Clone();
                    if (ReferenceEquals(_editingPalette, palette) && _selectedSwatch >= values[i].Length)
                    {
                        _editingPalette = null;
                        _editingView = null;
                        _selectedSwatch = -1;
                        ColorEditor.IsEnabled = false;
                        ASlider.IsEnabled = false;
                        ResetColorAdjustBase();
                    }
                }
            }
            Edit(one ? "convert palette depth" : "convert palette depths", () => Apply(after), () => Apply(before));
            int bits = Array.IndexOf(PaletteDepth.SupportedColorCounts, colors.Value) + 1;
            int skipped = palettes.Count - changing.Count;
            StatusText.Text = one
                ? $"Converted palette {PaletteLabel(palettes[0])} to {bits}-bit indexed depth."
                : $"Converted {changing.Count} palettes to {bits}-bit indexed depth."
                    + (skipped > 0 ? $" {skipped} already were." : "");
        }
        catch (Exception ex)
        {
            await Failed("ConvertPaletteDepth", ex);
        }
    }

    // ---- duplicate --------------------------------------------------------

    /// <summary>Copies each palette as a temp palette right under its source, in one undo step with
    /// an SFF open. The copies stay selected.</summary>
    private async Task DuplicatePalettes(List<Palette> many)
    {
        if (_file is not { } file)
        {
            if (!await SettlePendingColorAdjust()) return;
            foreach (var source in many)
                AddTempPalette(new Palette { Colors = (Rgba[])source.Colors.Clone() }, "duplicate palette", after: source);
            StatusText.Text = $"Duplicated {many.Count} palettes as temp palettes.";
            return;
        }

        var taken = file.Palettes.Where(p => p.Group == 1).Select(p => p.Index).ToHashSet();
        var copies = new List<(Palette Source, Palette Copy)>();
        foreach (var source in many)
        {
            var copy = new Palette
            {
                Colors = (Rgba[])source.Colors.Clone(),
                Source = PaletteSource.External,
                SourcePath = null,
                Group = 1,
                Dirty = true, // never saved
            };
            copy.Index = Enumerable.Range(1, int.MaxValue - 1).First(i => !taken.Contains(i));
            taken.Add(copy.Index);
            copies.Add((source, copy));
        }
        Edit("duplicate palettes",
            () =>
            {
                foreach (var (source, copy) in copies)
                {
                    int at = file.Palettes.IndexOf(source) is var i and >= 0 ? i + 1 : file.Palettes.Count;
                    file.Palettes.Insert(at, copy);
                }
                _paletteOverride = file.Palettes.IndexOf(copies[0].Copy);
            },
            () =>
            {
                foreach (var (_, copy) in copies) file.Palettes.Remove(copy);
                _paletteOverride = -1;
            }, kind: ExternalPaletteKind);
        SelectPaletteRows(copies.Select(c => c.Copy).ToList(), copies[0].Copy);
        UpdatePaletteActionButtons();
        StatusText.Text = $"Duplicated {many.Count} palettes as temp palettes.";
    }

    // ---- export -----------------------------------------------------------

    /// <summary>Exports every palette to one folder in one format, named like a single export.</summary>
    private async Task ExportPalettesMany(List<Palette> many)
    {
        try
        {
            if (await Dialogs.PromptPaletteFormat(this, _lastExportFormat) is not { } format) return;
            _lastExportFormat = format;
            string ext = PaletteExportType(format).Ext;
            if (await PickFolder("Export Palettes") is not { } dir) return;

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var files = new List<(Palette Palette, string Path)>();
            foreach (var pal in many)
            {
                string stem = PaletteExportName(pal), name = stem;
                for (int n = 2; !names.Add(name); n++) name = $"{stem} ({n})";
                files.Add((pal, Path.Combine(dir, $"{name}.{ext}")));
            }

            var losses = files
                .Select(f => StandalonePaletteLoss(f.Palette, f.Path, null, actTrailer: format == Dialogs.PaletteExportFormat.ActForward))
                .OfType<string>().ToList();
            if (losses.Count > 0 && !await Dialogs.Confirm(this, "Palette Export Loses Information",
                    $"{losses.Count} of {files.Count} palettes lose information. {string.Join(" ", losses.Distinct())} RGBA keeps everything.",
                    "Export"))
                return;

            int existing = files.Count(f => File.Exists(f.Path));
            if (existing > 0 && !await Dialogs.Confirm(this, "Export Palettes",
                    $"{existing} of these files already exist in that folder and will be overwritten.", "Overwrite"))
                return;

            foreach (var (pal, path) in files) WritePaletteExport(pal, path, format);
            CrashLog.Trace($"[palettes] exported {files.Count} selected palettes to {dir}");
            StatusText.Text = $"Exported {files.Count} palettes to {dir}.";
        }
        catch (Exception ex)
        {
            CrashLog.Write("ExportPalettesMany", ex);
            await Dialogs.ShowMessage(this, "Could Not Export the Palettes", ex.Message);
        }
    }

    // ---- save original files ----------------------------------------------

    /// <summary>Saves each palette that has its own file; the others (embedded, temp, file gone) are skipped.</summary>
    private async Task SavePaletteFiles(List<Palette> many)
    {
        var savable = many.Where(p => (_file is null || p.Source == PaletteSource.External)
            && p.SourcePath is { } path && File.Exists(path)).ToList();
        int saved = 0;
        foreach (var pal in savable)
            if (await SavePaletteDocument(pal)) saved++;
        RefreshPaletteTab();
        if (_file is not null) RefreshSprite();
        int skipped = many.Count - savable.Count;
        StatusText.Text = $"Saved {saved} palette file{(saved == 1 ? "" : "s")}."
            + (skipped > 0 ? $" Skipped {skipped} without a palette file." : "");
    }
}
