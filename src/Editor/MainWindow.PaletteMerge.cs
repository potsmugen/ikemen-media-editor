using Avalonia.Interactivity;
using Sff;

namespace Editor;

public partial class MainWindow
{
    /// <summary>Pixel counts per chosen palette, the pixel owners to remap, and sprites that can't follow.</summary>
    private sealed record PaletteMergeUsers(
        Dictionary<Palette, long[]> Weights, List<(Sprite Owner, Palette Source)> Owners, List<string> Blocked, int Sprites);

    /// <summary>Palettes selected in the list, in list order.</summary>
    private List<Palette> SelectedPalettesInOrder()
    {
        if (PaletteList?.SelectedItems is not { } items) return [];
        var chosen = items.OfType<PaletteRow>().Select(r => r.Palette).ToHashSet();
        IEnumerable<Palette> all = _file is { } file ? file.Palettes : _standalonePalettes;
        return all.Where(chosen.Contains).ToList();
    }

    /// <summary>Short label for dialogs: "g,i" for SFF palettes, "S1" for SFFv1's own, "F1"/"T1" for external ones.</summary>
    private static string PaletteKey(SffFile file, Palette? p) =>
        p is null ? "no palette"
        : p.Source == PaletteSource.Embedded
            ? V1PaletteOrdinals(file).TryGetValue(p, out int sn) ? $"S{sn}" : $"{p.Group},{p.Index}"
        : ExternalPaletteOrdinals(file).TryGetValue(p, out int n) ? $"{ExternalGroup(p)}{n}" : p.Describe();

    /// <summary>PaletteKey, or the F/T label when no SFF is open.</summary>
    private string PaletteLabel(Palette p) =>
        _file is { } file ? PaletteKey(file, p)
        : ExternalPaletteOrdinals(_standalonePalettes).TryGetValue(p, out int n) ? $"{ExternalGroup(p)}{n}" : p.Describe();

    private static Sprite PixelOwner(Sprite sprite)
    {
        while (sprite.SharesWith is { } target) sprite = target;
        return sprite;
    }

    /// <summary>
    /// Counts pixels per index for each chosen palette; a sprite on a palette linking to a chosen
    /// one counts toward it. Pixel owners on a chosen palette are remapped and their borrowers
    /// follow. Blocked: borrowers on an unchosen palette, owners not chosen, undecoded sprites.
    /// </summary>
    private static PaletteMergeUsers PlanPaletteMergeUsers(SffFile file, List<Palette> chosen)
    {
        var chosenSet = chosen.ToHashSet();
        Palette? SourceOf(Sprite s) =>
            s.Palette is not { } p ? null
            : chosenSet.Contains(p) ? p
            : p.LinksTo is { } t && chosenSet.Contains(t) ? t : null;

        var weights = new Dictionary<Palette, long[]>();
        var owners = new List<(Sprite, Palette)>();
        var blocked = new List<string>();
        int sprites = 0;
        foreach (var group in file.Sprites.GroupBy(PixelOwner))
        {
            var users = group.ToList();
            if (users.All(s => SourceOf(s) is null)) continue;
            var owner = group.Key;
            var ownerSource = SourceOf(owner);
            if (ownerSource is null)
            {
                foreach (var s in users.Where(s => SourceOf(s) is not null))
                    blocked.Add($"{s.Group},{s.Index}: pixels from {owner.Group},{owner.Index} on palette {PaletteKey(file, owner.Palette)}, which isn't merged");
                continue;
            }
            var outside = users.Where(s => SourceOf(s) is null).ToList();
            if (outside.Count > 0)
            {
                foreach (var s in outside)
                    blocked.Add($"{s.Group},{s.Index}: on palette {PaletteKey(file, s.Palette)}, which isn't merged, but shares pixels with {owner.Group},{owner.Index}");
                continue;
            }
            if (owner.Indexed is not { } pixels)
            {
                if (owner.DecodeError is not null) blocked.Add($"{owner.Group},{owner.Index}: could not be decoded");
                continue; // true color: no indexes to count or remap
            }
            if (!weights.TryGetValue(ownerSource, out var counts)) weights[ownerSource] = counts = new long[256];
            foreach (byte index in pixels) counts[index]++;
            owners.Add((owner, ownerSource));
            sprites += users.Count;
        }
        return new PaletteMergeUsers(weights, owners, blocked, sprites);
    }

    /// <summary>Each palette's padding: its most frequent color, if it fills 2+ entries (ties: lowest index).</summary>
    private static Dictionary<Palette, Rgba> DetectPadding(List<Palette> chosen)
    {
        var padOf = new Dictionary<Palette, Rgba>();
        foreach (var p in chosen)
        {
            var counts = new Dictionary<Rgba, int>();
            for (int i = 1; i < Math.Min(p.Colors.Length, 256); i++)
                counts[p.Colors[i]] = counts.GetValueOrDefault(p.Colors[i]) + 1;
            if (counts.Count > 0 && counts.MaxBy(kv => kv.Value) is { Value: >= 2 } top) padOf[p] = top.Key;
        }
        return padOf;
    }

    /// <summary>Padding choices: detected colors, most entries first, then 0,0,0.</summary>
    private static List<Rgba> PaddingChoices(List<Palette> chosen, Dictionary<Palette, Rgba> padOf)
    {
        var choices = padOf.Values.Distinct()
            .OrderByDescending(c => chosen.Sum(p => p.Colors.Skip(1).Take(255).Count(x => x == c))).ToList();
        var black = new Rgba(0, 0, 0, 255);
        if (!choices.Contains(black)) choices.Add(black);
        return choices;
    }

    /// <summary>Whether index i of p is left out: its palette's padding, unless keepUsed and a sprite uses it.</summary>
    private static bool IsPadding(Palette p, int i, Dictionary<Palette, Rgba> padOf, PaletteMergeUsers? users, bool keepUsed) =>
        padOf.TryGetValue(p, out var pad) && p.Colors[i] == pad
        && !(keepUsed && users is not null && users.Weights.TryGetValue(p, out var w) && w[i] > 0);

    private static PaletteMerge.Result BuildPaletteMerge(List<Palette> chosen, PaletteMergeUsers? users,
        Dictionary<Palette, Rgba> padOf, bool keepUsed, Rgba fill) =>
        PaletteMerge.Build(chosen.Select(p => new PaletteMerge.Source(p.Colors,
            users is not null && users.Weights.TryGetValue(p, out var w) ? w : null,
            Enumerable.Range(0, p.Colors.Length).Select(i => i > 0 && IsPadding(p, i, padOf, users, keepUsed)).ToArray())).ToList(), fill);

    /// <summary>Distinct colors kept, plus the mask.</summary>
    private static int PaddedColorCount(List<Palette> chosen, PaletteMergeUsers? users, Dictionary<Palette, Rgba> padOf, bool keepUsed) =>
        chosen.SelectMany(p => Enumerable.Range(1, Math.Max(0, Math.Min(p.Colors.Length, 256) - 1))
            .Where(i => !IsPadding(p, i, padOf, users, keepUsed)).Select(i => p.Colors[i])).Distinct().Count() + 1;

    /// <summary>Colors including the mask, so they compare with a 256-entry palette.</summary>
    private static int MergeColorCount(PaletteMerge.Result result) => result.UniqueColors + 1;

    private static string MergeSummary(PaletteMerge.Result result) =>
        result.Reduced ? $"{MergeColorCount(result)} colors reduced to 256" : $"{MergeColorCount(result)} colors";

    /// <summary>Builds a temp palette from the colors of the selected palettes; nothing else changes.</summary>
    private void OnCombinePalettes(object? sender, RoutedEventArgs e) => _ = CombinePalettesFrom(null);

    /// <summary>Base is the palette whose indexes and mask win (the dialog starts on it).</summary>
    private async Task CombinePalettesFrom(Palette? basePalette)
    {
        try
        {
            var chosen = SelectedPalettesInOrder();
            if (chosen.Count < 2)
            {
                await Dialogs.ShowMessage(this, "Combine Palettes", "Select two or more palettes in the list (Ctrl or Shift-click).");
                return;
            }
            if (!await SettlePendingColorAdjust()) return;

            var users = _file is { } file ? PlanPaletteMergeUsers(file, chosen) : null;
            var padOf = DetectPadding(chosen);
            var paddings = PaddingChoices(chosen, padOf);
            int count = PaddedColorCount(chosen, users, padOf, keepUsed: false);
            int initial = basePalette is not null && chosen.Contains(basePalette) ? chosen.IndexOf(basePalette) : 0;
            // Over 256 the button itself asks for the reduction.
            if (await Dialogs.PromptMergeOptions(this, "Combine Palettes", count <= 256 ? "Combine" : "Reduce and Combine",
                    chosen.Select(PaletteLabel).ToList(), initial, paddings, $"Colors: {count} of 256") is not { } choice) return;
            var first = chosen[choice.Target];
            chosen = [first, .. chosen.Where(p => !ReferenceEquals(p, first))];
            PaletteMerge.Result result;
            try { result = BuildPaletteMerge(chosen, users, padOf, keepUsed: false, paddings[choice.Padding]); }
            catch (InvalidOperationException ex)
            {
                CrashLog.Error("OnCombinePalettes", ex.Message);
                await Dialogs.ShowMessage(this, "Combine Palettes", "No colors are left.");
                return;
            }
            AddTempPalette(new Palette { Colors = result.Colors }, "combine palettes");
            StatusText.Text = $"Combined {chosen.Count} palettes into a temp palette ({MergeSummary(result)}).";
        }
        catch (Exception ex)
        {
            await Failed("OnCombinePalettes", ex);
        }
    }

    /// <summary>
    /// Merges the selected palettes and remaps their sprites. SFFv2: the target (picked in the dialog,
    /// starting on the right-clicked palette) gets the colors and the other SFF palettes are removed. SFFv1: every chosen SFF
    /// palette gets the colors. The target's indexes win.
    /// </summary>
    private void OnMergePalettes(object? sender, RoutedEventArgs e) => _ = MergePalettesInto(null);

    private async Task MergePalettesInto(Palette? into)
    {
        try
        {
            if (_file is not { } file) return;
            var chosen = SelectedPalettesInOrder();
            if (chosen.Count < 2)
            {
                await Dialogs.ShowMessage(this, "Merge Palettes", "Select two or more palettes in the list (Ctrl or Shift-click).");
                return;
            }
            var embedded = chosen.Where(p => p.Source == PaletteSource.Embedded).ToList();
            if (embedded.Count == 0)
            {
                CrashLog.Error("OnMergePalettes", "refused: no SFF palette selected");
                await Dialogs.ShowMessage(this, "Cannot Merge Palettes", "Select at least one SFF palette.");
                return;
            }
            bool v1 = SffVersions.IsV1(file.Version);
            // A link can't take the colors (SFFv2), so it can't be the target.
            var candidates = embedded.Where(p => v1 || p.LinksTo is null).ToList();
            if (candidates.Count == 0)
            {
                CrashLog.Error("OnMergePalettes", "refused: every selected SFF palette is a link");
                await Dialogs.ShowMessage(this, "Cannot Merge Palettes", "Every selected SFF palette is a link. Unlink one first.");
                return;
            }
            if (!await SettlePendingColorAdjust() || !ReferenceEquals(_file, file)) return;

            var users = PlanPaletteMergeUsers(file, chosen);
            if (users.Blocked.Count > 0)
            {
                foreach (string item in users.Blocked) CrashLog.Error("Merge palettes", "refused: " + item);
                string shown = string.Join("\n", users.Blocked.Take(10));
                if (users.Blocked.Count > 10) shown += $"\n…and {users.Blocked.Count - 10} more (see crash.log).";
                await Dialogs.ShowMessage(this, "Cannot Merge Palettes", "These sprites can't be remapped:\n" + shown);
                return;
            }
            var padOf = DetectPadding(chosen);
            var paddings = PaddingChoices(chosen, padOf);
            int count = PaddedColorCount(chosen, users, padOf, keepUsed: true);
            if (count > 256)
            {
                string msg = $"These palettes use {count} colors; a palette holds 256.";
                CrashLog.Error("OnMergePalettes", "refused: " + msg);
                await Dialogs.ShowMessage(this, "Cannot Merge Palettes", msg);
                return;
            }
            int initial = into is not null && candidates.Contains(into) ? candidates.IndexOf(into) : 0;
            if (await Dialogs.PromptMergeOptions(this, "Merge Palettes", "Continue", candidates.Select(p => PaletteKey(file, p)).ToList(),
                    initial, paddings, $"Colors: {count} of 256") is not { } choice
                || !ReferenceEquals(_file, file)) return;
            var target = candidates[choice.Target];
            // The target goes first, so its indexes and mask win.
            chosen = [target, .. chosen.Where(p => !ReferenceEquals(p, target))];
            embedded = [target, .. embedded.Where(p => !ReferenceEquals(p, target))];

            PaletteMerge.Result result;
            try { result = BuildPaletteMerge(chosen, users, padOf, keepUsed: true, paddings[choice.Padding]); }
            catch (InvalidOperationException ex)
            {
                CrashLog.Error("OnMergePalettes", ex.Message);
                await Dialogs.ShowMessage(this, "Merge Palettes", "No colors are left.");
                return;
            }
            if (MergeColorCount(result) > 256)
            {
                string msg = $"These palettes use {MergeColorCount(result)} colors; a palette holds 256.";
                CrashLog.Error("OnMergePalettes", "refused: " + msg);
                await Dialogs.ShowMessage(this, "Cannot Merge Palettes", msg);
                return;
            }

            List<Palette> targets = v1 ? embedded : [target];
            List<Palette> dropped = v1 ? [] : embedded.Skip(1).ToList();
            string text = (v1 ? $"Merge {chosen.Count} palettes?" : $"Merge {chosen.Count} palettes into {PaletteKey(file, target)}?")
                + $"\n\nColors: {MergeColorCount(result)} of 256"
                + (v1 ? $"\nPalettes changed: {targets.Count}" : dropped.Count > 0 ? $"\nRemoved: {string.Join(", ", dropped.Select(p => PaletteKey(file, p)))}" : "")
                + $"\nSprites remapped: {users.Sprites}";
            if (!await Dialogs.Confirm(this, "Merge Palettes", text, "Merge")) return;
            if (!ReferenceEquals(_file, file) || chosen.Any(p => !file.Palettes.Contains(p))) return;

            var remapped = new List<(Sprite Owner, byte[] Pixels)>();
            if (!TryBeginBusy()) return;
            try
            {
                var mapOf = chosen.Select((p, i) => (p, i)).ToDictionary(x => x.p, x => result.Maps[x.i]);
                for (int i = 0; i < users.Owners.Count; i++)
                {
                    var (owner, source) = users.Owners[i];
                    if (i % 64 == 0) await BusyStep("Merging palettes", i, users.Owners.Count, $"{owner.Group},{owner.Index}");
                    var map = mapOf[source];
                    var before = owner.Indexed!;
                    var after = new byte[before.Length];
                    for (int k = 0; k < before.Length; k++) after[k] = map[before[k]];
                    if (!after.AsSpan().SequenceEqual(before)) remapped.Add((owner, after));
                }
            }
            finally { EndBusy(); }

            var colorsBefore = targets.Select(p => p.Colors.ToArray()).ToList();
            var colorsAfter = result.Colors;
            var snapshotBefore = file.SnapshotPixelLinks();
            PixelLinkSnapshot? snapshotAfter = null;
            Palette? selected = _paletteOverride >= 0 && _paletteOverride < file.Palettes.Count ? file.Palettes[_paletteOverride] : null;
            Palette? selectedAfter = selected is not null && dropped.Contains(selected) ? target : selected;
            bool editingRemoved = _editingPalette is { } editing && dropped.Contains(editing);

            void ClearRemovedPaletteEditor()
            {
                if (!editingRemoved) return;
                _editingPalette = null;
                _editingView = null;
                _selectedSwatch = -1;
                ColorEditor.IsEnabled = false;
                SyncAdjustCommitButton();
            }

            void ApplyMerge()
            {
                if (snapshotAfter is null)
                {
                    foreach (var (owner, pixels) in remapped)
                    {
                        owner.Indexed = pixels;
                        owner.PixelsDirty = true;
                    }
                    RedirectDroppedPalettes(file, dropped, target, colorsAfter);
                    snapshotAfter = file.SnapshotPixelLinks();
                }
                else snapshotAfter.Restore();
                foreach (var p in targets) p.Colors = (Rgba[])colorsAfter.Clone();
                _paletteOverride = selectedAfter is null ? -1 : file.Palettes.IndexOf(selectedAfter);
                ClearRemovedPaletteEditor();
            }

            void UndoMerge()
            {
                snapshotBefore.Restore();
                for (int i = 0; i < targets.Count; i++) targets[i].Colors = (Rgba[])colorsBefore[i].Clone();
                _paletteOverride = selected is null ? -1 : file.Palettes.IndexOf(selected);
                ClearRemovedPaletteEditor();
            }

            Edit("merge palettes", ApplyMerge, UndoMerge);
            CrashLog.Trace($"[palettes] merged {chosen.Count} palettes into {PaletteDisplayName(file, target)}; "
                + $"{MergeSummary(result)}, remapped {remapped.Count} pixel owner(s), removed {dropped.Count}");
            StatusText.Text = $"Merged {chosen.Count} palettes ({MergeSummary(result)}); remapped {users.Sprites} sprite(s)"
                + (dropped.Count > 0 ? $", removed {dropped.Count} palette(s)." : ".");
        }
        catch (Exception ex)
        {
            await Failed("OnMergePalettes", ex);
        }
    }

    /// <summary>
    /// Removes the dropped palettes, pointing their sprites at the target. Palettes linking to a dropped
    /// one relink to the target when after it; earlier ones (links may only point back) get their own copy.
    /// </summary>
    private static void RedirectDroppedPalettes(SffFile file, List<Palette> dropped, Palette target, Rgba[] colors)
    {
        var droppedSet = dropped.ToHashSet();
        int targetAt = file.Palettes.IndexOf(target);
        foreach (var sprite in file.Sprites)
            if (sprite.Palette is { } p && droppedSet.Contains(p)) sprite.Palette = target;
        foreach (var p in file.Palettes)
        {
            if (droppedSet.Contains(p) || p.LinksTo is not { } linked || !droppedSet.Contains(linked)) continue;
            if (file.Palettes.IndexOf(p) > targetAt) p.LinksTo = target;
            else
            {
                p.LinksTo = null;
                p.Colors = (Rgba[])colors.Clone();
            }
        }
        file.Palettes.RemoveAll(droppedSet.Contains);
    }

    /// <summary>Dims the merge commands until two palettes are selected.</summary>
    private void SyncPaletteMergeMenu()
    {
        int count = PaletteList?.SelectedItems?.Count ?? 0;
        CombinePalettesMenuItem.IsEnabled = count >= 2;
        MergePalettesMenuItem.IsEnabled = count >= 2 && _file is not null;
    }

    private void OnPalettesMenuOpened(object? sender, RoutedEventArgs e)
    {
        SyncPaletteMergeMenu();
        SyncAddToSffMenuItem();
    }
}
