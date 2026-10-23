using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Sff;

namespace Editor;

public partial class MainWindow
{
    // ---- single and bulk export / import -----------------------------------

    /// <summary>Last Export format this session; pre-selected next time.</summary>
    private Dialogs.SpriteExportFormat _lastSpriteExportFormat = Dialogs.SpriteExportFormat.Png;

    /// <summary>Exports the active sprite as .png or .pcx, asked before the save dialog.
    /// The sprite's own palette is written unless the preview palette is asked for,
    /// matching Export All, which passes each sprite's own palette too.</summary>
    private async void OnExportSprite(object? sender, RoutedEventArgs e)
    {
        if (_current is not { } sprite) return;
        try
        {
            // PCX stores indexed pixels only, and a true-color sprite's pixels ignore
            // any palette, so it goes straight to the save dialog as PNG instead of
            // into a prompt with nothing to choose.
            var choice = new Dialogs.SpriteExportChoice(Dialogs.SpriteExportFormat.Png, false);
            if (sprite.Indexed is not null)
            {
                if (await Dialogs.PromptSpriteFormat(this, _lastSpriteExportFormat, PreviewOverridePalette() is { } preview ? PaletteLabel(preview) : null) is not { } c) return;
                choice = c;
                _lastSpriteExportFormat = c.Format; // the format carries over; the palette tick starts fresh each time
            }
            var format = choice.Format;

            var (ext, type) = format == Dialogs.SpriteExportFormat.Pcx ? ("pcx", PcxType) : ("png", PngType);
            var file = await PickSave(new FilePickerSaveOptions
            {
                Title = "Export Sprite",
                SuggestedFileName = $"{sprite.Group}_{sprite.Index}.{ext}",
                DefaultExtension = ext,
                FileTypeChoices = [type],
            });
            if (file?.TryGetLocalPath() is not { } path) return;

            var palette = choice.UsePreviewPalette ? ActivePalette() : sprite.Palette;
            File.WriteAllBytes(path, format == Dialogs.SpriteExportFormat.Pcx
                ? PcxExport.Encode(sprite, palette) : PngExport.Encode(sprite, palette, _showMask));
            StatusText.Text = $"Wrote {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            await Failed("OnExportSprite", ex);
        }
    }

    private async void OnExportAllSprites(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (await Dialogs.PromptBulkSpriteExport(this) is not { } options) return;
            if (_file is { } sff
                && ExportableSprites(sff, options.Format).Count == 0
                && (!options.IncludePalettes
                    || !sff.Palettes.Any(p => p.Source != PaletteSource.External)))
            {
                await Dialogs.ShowMessage(this, "Nothing to Export",
                    "There are no exportable sprites or palettes with the selected options.");
                return;
            }
            await ExportAllSpritesAndPalettes(options);
        }
        catch (Exception ex)
        {
            await Failed("OnExportAllSprites", ex);
        }
    }


    private static List<Sprite> ExportableSprites(SffFile file, BulkSpriteExportFormat format) =>
        file.Sprites.Where(s => s.DecodeError is null && s.HasPixels
            && (!UsesPcx(s, format) || s.Indexed is not null)).ToList();

    private static List<string> LogSkippedSprites(string source, IEnumerable<Sprite> skipped, BulkSpriteExportFormat format)
    {
        var items = new List<string>();
        foreach (var s in skipped)
        {
            string reason = s.DecodeError ?? (!s.HasPixels ? "no pixels" : UsesPcx(s, format) ? "true-color" : "not exportable");
            string item = $"skipped sprite {s.Group},{s.Index}: {reason}";
            CrashLog.Error(source, item);
            items.Add(item);
        }
        return items;
    }

    private static bool UsesPcx(Sprite sprite, BulkSpriteExportFormat format) => format switch
    {
        BulkSpriteExportFormat.Pcx => true,
        BulkSpriteExportFormat.Preserve => SourceFormat(sprite) == SpriteFormat.Pcx,
        _ => false,
    };

    private static SpriteFormat SourceFormat(Sprite sprite)
    {
        while (sprite.SharesWith is { } target) sprite = target;
        return sprite.SrcFormat;
    }

    private static (byte[] Bytes, string Extension) EncodeExportSprite(Sprite sprite,
        BulkSpriteExportFormat format, Palette? palette, bool showMask, bool pad,
        (int Width, int Height, int AxisX, int AxisY) canvas)
    {
        if (UsesPcx(sprite, format))
            return (pad ? EncodePcxPadded(sprite, palette, canvas) : PcxExport.Encode(sprite, palette), "pcx");
        return (pad ? EncodePadded(sprite, palette, showMask, canvas) : PngExport.Encode(sprite, palette, showMask), "png");
    }

    /// <summary>
    /// One shared canvas size for a batch of sprites, anchored at each sprite's own
    /// AxisX/AxisY rather than its top-left corner: Width/Height fit the widest
    /// left/right/top/bottom extent any sprite in the batch has around its own axis
    /// point, and AxisX/AxisY here is where every sprite's axis point ends up once
    /// padded into this canvas — the same pixel position in every exported image.
    /// </summary>
    private static (int Width, int Height, int AxisX, int AxisY) CommonCanvas(IEnumerable<Sprite> sprites)
    {
        int maxLeft = 0, maxRight = 0, maxTop = 0, maxBottom = 0;
        foreach (var s in sprites)
        {
            maxLeft = Math.Max(maxLeft, s.AxisX);
            maxRight = Math.Max(maxRight, s.Width - s.AxisX);
            maxTop = Math.Max(maxTop, s.AxisY);
            maxBottom = Math.Max(maxBottom, s.Height - s.AxisY);
        }
        return (maxLeft + maxRight, maxTop + maxBottom, maxLeft, maxTop);
    }

    /// <summary>
    /// Encodes one sprite into the shared canvas CommonCanvas computed for its batch,
    /// its own pixels placed so its axis point lands at (canvas.AxisX, canvas.AxisY) —
    /// the same spot in every sprite's padded image. Because canvas is the max extent
    /// around every sprite's own axis point, the offset is always >= 0 and the sprite
    /// always fits entirely inside the canvas — no clipping needed. Padding is fully
    /// transparent: index 0 for an indexed sprite (this app's mask-color convention,
    /// same as PngExport.Encode(Sprite,...)), zero-alpha BGRA for true-color.
    /// </summary>
    private static byte[] EncodePcxPadded(Sprite s, Palette? palette, (int Width, int Height, int AxisX, int AxisY) canvas)
    {
        var indices = s.Indexed ?? throw new InvalidDataException("PCX stores indexed sprites only.");
        var pal = palette ?? s.Palette
            ?? throw new InvalidDataException("the sprite has no palette to write into the PCX.");
        int offsetX = canvas.AxisX - s.AxisX;
        int offsetY = canvas.AxisY - s.AxisY;
        var padded = new byte[canvas.Width * canvas.Height]; // index 0 fills the padding
        for (int y = 0; y < s.Height; y++)
            Array.Copy(indices, y * s.Width, padded, (y + offsetY) * canvas.Width + offsetX, s.Width);
        return PcxExport.EncodeIndexed(padded, canvas.Width, canvas.Height, pal);
    }

    private static byte[] EncodePadded(Sprite s, Palette? palette, bool showMask, (int Width, int Height, int AxisX, int AxisY) canvas)
    {
        int offsetX = canvas.AxisX - s.AxisX;
        int offsetY = canvas.AxisY - s.AxisY;

        if (s.Indexed is { } indices && (palette ?? s.Palette) is { } pal)
        {
            var padded = new byte[canvas.Width * canvas.Height]; // 0 = mask/transparent index
            for (int y = 0; y < s.Height; y++)
                Array.Copy(indices, y * s.Width, padded, (y + offsetY) * canvas.Width + offsetX, s.Width);

            var colors = (Rgba[])pal.Colors.Clone();
            if (colors.Length > Sprite.TransparentIndex)
                colors[Sprite.TransparentIndex] = colors[Sprite.TransparentIndex] with { A = showMask ? (byte)255 : (byte)0 };
            return PngExport.EncodeIndexed(padded, canvas.Width, canvas.Height, colors);
        }

        var bgra = s.ToBgra(palette, showMask);
        var paddedBgra = new byte[canvas.Width * canvas.Height * 4]; // all-zero = transparent
        for (int y = 0; y < s.Height; y++)
            Array.Copy(bgra, y * s.Width * 4, paddedBgra, ((y + offsetY) * canvas.Width + offsetX) * 4, s.Width * 4);
        return PngExport.Encode(paddedBgra, canvas.Width, canvas.Height);
    }

    /// <summary>Writes each palette as an indexed PNG with its RGBA colors in the embedded palette.</summary>
    private static List<ExportManifest.Entry> ExportAllPalettes(IEnumerable<Palette> palettes, string dir, string prefix)
    {
        var entries = new List<ExportManifest.Entry>();
        foreach (var p in palettes)
        {
            string name = $"{prefix}_palette_{p.Group}_{p.Index}.png";
            File.WriteAllBytes(Path.Combine(dir, name), PalettePngFile.Write(p, 1));
            entries.Add(new("palette", p.Group, p.Index, name));
        }
        return entries;
    }

    /// <summary>
    /// Replaces a palette's Colors in place — same object reference, so every sprite
    /// pointing at it stays linked — rather than OnImportPaletteFiles' append-a-new-palette
    /// behavior, which would leave sprites still pointing at the stale one. Returns
    /// the before/after color arrays so a bulk import can fold many of these into
    /// one undo step instead of one per palette.
    /// </summary>
    private static (Rgba[] Before, Rgba[] After) ReplacePaletteColors(Palette pal, Rgba[] newColors)
    {
        var before = (Rgba[])pal.Colors.Clone();
        pal.Colors = newColors;
        return (before, newColors);
    }

    /// <summary>Imports an export manifest (.txt): entries matching an existing Group,Index are
    /// overwritten (after a confirm), the rest are added. One composite undo step.</summary>
    private async void OnImportAllSpritesAndPalettes(object? sender, RoutedEventArgs e)
    {
        if (_file is null || _busy) return;
        bool busy = false;
        SffFile? transactionFile = null;
        List<Sprite>? transactionSprites = null;
        List<Palette>? transactionPalettes = null;
        Dictionary<Sprite, SpriteSnapshot>? transactionSpriteStates = null;
        Dictionary<Palette, Rgba[]>? transactionPaletteColors = null;
        try
        {
            var picked = await PickOpen(new FilePickerOpenOptions
            {
                Title = "Import Sprites and Palettes - Choose the Manifest .txt",
                FileTypeFilter = [TxtType],
            });
            if (picked.Count == 0 || picked[0].TryGetLocalPath() is not { } txtPath) return;
            if (Path.GetDirectoryName(txtPath) is not { } dir) return;

            var (entries, errors) = ExportManifest.Read(File.ReadAllText(txtPath));
            var unexpectedTypes = entries
                .Where(x => x.Type != "sprite" && x.Type != "palette")
                .Select(x => x.Type).Distinct().ToList();
            var duplicateKeys = entries.GroupBy(x => (x.Type, x.Group, x.Index)).Where(g => g.Count() > 1).ToList();
            if (duplicateKeys.Count > 0)
            {
                var keys = string.Join(", ", duplicateKeys.Select(g => $"{g.Key.Type} {g.Key.Group},{g.Key.Index}"));
                CrashLog.Error("ImportAllSpritesAndPalettes", $"{txtPath}: duplicate entries: {keys}");
                await Dialogs.ShowMessage(this, "Duplicate Manifest Entries", keys);
                return;
            }
            if (unexpectedTypes.Count > 0)
            {
                LogBulkProblems("ImportAllSpritesAndPalettes", errors, []);
                CrashLog.Error("ImportAllSpritesAndPalettes", $"{txtPath}: unexpected entry types: {string.Join(", ", unexpectedTypes)}");
                await Dialogs.ShowMessage(this, "Not a Sprite Export", "This manifest contains entries that aren't sprites or palettes.");
                return;
            }

            var file = _file;
            var spriteChanges = new List<(Sprite Sprite, SpriteSnapshot Before, SpriteSnapshot After)>();
            var newSpriteStates = new List<(Sprite Sprite, SpriteSnapshot After)>();
            var paletteChanges = new List<(Palette Palette, Rgba[] Before, Rgba[] After)>();
            Palette? FindPalette(int g, int i) => file.Palettes.FirstOrDefault(p => p.Group == g && p.Index == i);

            int oldSprites = entries.Count(x => x.Type == "sprite" && file.Find(x.Group, x.Index) is not null);
            int oldPalettes = entries.Count(x => x.Type == "palette" && FindPalette(x.Group, x.Index) is not null);
            if (oldSprites + oldPalettes > 0 && !await Dialogs.Confirm(this, "Import",
                    $"{oldSprites} sprite(s) and {oldPalettes} palette(s) in this manifest already exist in the open file " +
                    "and will be overwritten. Entries that don't exist yet will be added.", "Overwrite"))
                return;

            var spritesBefore = file.Sprites.ToList();
            var palettesBefore = file.Palettes.ToList();
            var spriteStatesBefore = spritesBefore.ToDictionary(s => s, SpriteSnapshot.Of);
            var paletteColorsBefore = palettesBefore.ToDictionary(p => p, p => (Rgba[])p.Colors.Clone());
            transactionFile = file;
            transactionSprites = spritesBefore;
            transactionPalettes = palettesBefore;
            transactionSpriteStates = spriteStatesBefore;
            transactionPaletteColors = paletteColorsBefore;
            var stagedPalettes = new Dictionary<(int Group, int Index), Rgba[]>();
            var stagedPaletteEntries = new List<(int Group, int Index)>();
            var stagedPaletteObjects = new Dictionary<(int Group, int Index), Palette>();
            var stagedNewSprites = new List<Sprite>();
            var stagedNewPalettes = new List<Palette>();
            int addedSprites = 0, addedPalettes = 0;

            CrashLog.Trace($"[bulk] import {entries.Count} entries from {txtPath} into {file.Sprites.Count} sprites, {file.Palettes.Count} palettes");
            busy = TryBeginBusy();
            if (!busy) return;
            var ordered = entries.Where(x => x.Type == "palette").Concat(entries.Where(x => x.Type != "palette")).ToList();
            for (int n = 0; n < ordered.Count; n++)
            {
                var entry = ordered[n];
                await BusyStep("Importing", n, ordered.Count, $"{entry.Type} {entry.Group},{entry.Index} {entry.File}");
                if (!ReferenceEquals(_file, file)) { StatusText.Text = "Import stopped \u2014 the file was closed."; return; }
                string path = Path.Combine(dir, entry.File);
                if (!File.Exists(path)) { errors.Add($"{entry.File}: file not found"); continue; }

                if (entry.Type == "palette")
                {
                    var key = (entry.Group, entry.Index);
                    stagedPalettes[key] = PaletteFileImport.Read(path).Colors;
                    stagedPaletteEntries.Add(key);
                    continue;
                }

                var original = file.Find(entry.Group, entry.Index);
                bool isNew = original is null;
                var sprite = original ?? new Sprite { Group = entry.Group, Index = entry.Index };
                Palette? usePal = null;
                if (entry.Palette is { } pk)
                {
                    usePal = file.Palettes.FirstOrDefault(p => p.Group == pk.Group && p.Index == pk.Index);
                    if (usePal is null)
                    {
                        var colors = stagedPalettes.GetValueOrDefault(pk);
                        if (colors is not null)
                        {
                            if (!stagedPaletteObjects.TryGetValue(pk, out usePal))
                            {
                                usePal = new Palette { Group = pk.Group, Index = pk.Index, Colors = colors };
                                stagedPaletteObjects.Add(pk, usePal);
                                stagedNewPalettes.Add(usePal);
                            }
                        }
                    }
                    if (usePal is null)
                    {
                        errors.Add($"{entry.File}: palette {pk.Group},{pk.Index} isn't in this file or the manifest");
                        continue;
                    }
                }
                var (before, after, err) = ReplaceSpriteHeadless(sprite, File.ReadAllBytes(path), usePal);
                var palettesAfterReplace = file.Palettes.ToList();
                RestoreSpriteSnapshot(sprite, before);
                file.Palettes.Clear();
                file.Palettes.AddRange(palettesBefore);
                if (err is not null)
                {
                    errors.Add($"{entry.File}: {err}");
                    continue;
                }
                if (before.Shares is { } shared)
                {
                    bool same = after.Width == before.Width && after.Height == before.Height
                        && after.Indexed.AsSpan().SequenceEqual(before.Indexed) && after.Direct.AsSpan().SequenceEqual(before.Direct);
                    if (!same)
                    {
                        errors.Add($"{entry.File}: {sprite.Group},{sprite.Index} is linked to {shared.Group},{shared.Index}; unlink it first to import a different image");
                        CrashLog.Error("ImportAllSpritesAndPalettes", $"refused {sprite.Group},{sprite.Index}: linked to {shared.Group},{shared.Index}");
                        continue;
                    }
                    after = before with { Palette = after.Palette };
                }
                if (entry.Axis is { } ax) after = after with { AxisX = ax.X, AxisY = ax.Y };
                foreach (var created in palettesAfterReplace.Where(p => !palettesBefore.Contains(p)))
                {
                    int index = 0;
                    while (file.Palettes.Any(p => p.Group == created.Group && p.Index == index)
                        || stagedPaletteObjects.ContainsKey((created.Group, index))
                        || stagedPalettes.ContainsKey((created.Group, index))) index++;
                    created.Index = index;
                    var key = (created.Group, created.Index);
                    stagedPaletteObjects[key] = created;
                    stagedNewPalettes.Add(created);
                    if (ReferenceEquals(after.Palette, created)) after = after with { Palette = created };
                }
                if (isNew)
                {
                    stagedNewSprites.Add(sprite);
                    newSpriteStates.Add((sprite, after));
                    RestoreSpriteSnapshot(sprite, before);
                    addedSprites++;
                }
                else spriteChanges.Add((sprite, before, after));
            }
            foreach (var key in stagedPaletteEntries)
            {
                var colors = stagedPalettes[key];
                var pal = file.Palettes.FirstOrDefault(p => p.Group == key.Group && p.Index == key.Index);
                if (pal is null)
                {
                    if (!stagedPaletteObjects.TryGetValue(key, out pal))
                    {
                        pal = new Palette { Group = key.Group, Index = key.Index };
                        stagedPaletteObjects.Add(key, pal);
                        stagedNewPalettes.Add(pal);
                    }
                    pal.Colors = colors;
                    addedPalettes++;
                }
                else paletteChanges.Add((pal, (Rgba[])pal.Colors.Clone(), colors));
            }
            var newPaletteOrder = entries.Where(x => x.Type == "palette")
                .Select(x => stagedPaletteObjects.GetValueOrDefault((x.Group, x.Index)))
                .Where(p => p is not null).Cast<Palette>()
                .Concat(stagedNewPalettes).Distinct().ToList();
            foreach (var sprite in stagedNewSprites) file.Sprites.Add(sprite);
            foreach (var palette in newPaletteOrder) file.Palettes.Add(palette);
            foreach (var (sprite, after) in newSpriteStates) RestoreSpriteSnapshot(sprite, after);
            foreach (var (sprite, _, after) in spriteChanges) RestoreSpriteSnapshot(sprite, after);
            foreach (var (palette, _, after) in paletteChanges) palette.Colors = after;
            LogBulkProblems("ImportAllSpritesAndPalettes", errors, []);
            CrashLog.Trace($"[bulk] import scanned: sprites {spriteChanges.Count} replaced/{addedSprites} added, palettes {paletteChanges.Count} replaced/{addedPalettes} added, {errors.Count} errors");

            var spritesAfter = file.Sprites.ToList();
            var palettesAfter = file.Palettes.ToList();
            if (spriteChanges.Count == 0 && paletteChanges.Count == 0 && addedSprites == 0 && addedPalettes == 0
                && spritesAfter.Count == spritesBefore.Count && palettesAfter.Count == palettesBefore.Count)
            {
                StatusText.Text = "Nothing imported.";
                ShowSkipped("Some Entries Could Not Be Imported", errors);
                return;
            }

            void ApplyLists(bool useAfter)
            {
                file.Sprites.Clear();
                file.Sprites.AddRange(useAfter ? spritesAfter : spritesBefore);
                file.Palettes.Clear();
                file.Palettes.AddRange(useAfter ? palettesAfter : palettesBefore);
            }
            void ApplySprites(bool useAfter)
            {
                foreach (var (sprite, before, after) in spriteChanges)
                    RestoreSpriteSnapshot(sprite, useAfter ? after : before);
            }
            void ApplyPalettes(bool useAfter)
            {
                foreach (var (pal, before, after) in paletteChanges)
                {
                    pal.Colors = useAfter ? after : before;
                    RefreshPaletteViews(pal);
                }
            }
            // Data's already at "after" from the scan above; run it once more through
            // the same closure the undo/redo will use, so every place that shows
            // palette colors picks up the change (RefreshPaletteViews rebuilds the
            // canvases; the scan loop's ReplacePaletteColors call only touched the data).
            ApplyPalettes(true);

            string name = $"import {spriteChanges.Count + addedSprites} sprite(s), {paletteChanges.Count + addedPalettes} palette(s)";
            _undo.Record(name,
                () => { ApplyLists(false); ApplySprites(false); ApplyPalettes(false); AfterEdit(); RebuildPaletteList(); },
                () => { ApplyLists(true); ApplySprites(true); ApplyPalettes(true); AfterEdit(); RebuildPaletteList(); });

            MarkDirty();
            AfterEdit();
            RebuildPaletteList();

            string summary = $"Sprites: {spriteChanges.Count} replaced, {addedSprites} added. " +
                             $"Palettes: {paletteChanges.Count} replaced, {addedPalettes} added";
            if (errors.Count > 0) summary += $"; {errors.Count} error(s)";
            StatusText.Text = summary;
            ShowSkipped("Some Entries Could Not Be Imported", errors);
        }
        catch (Exception ex)
        {
            if (transactionFile is not null && transactionSprites is not null && transactionPalettes is not null
                && transactionSpriteStates is not null && transactionPaletteColors is not null)
            {
                transactionFile.Sprites.Clear();
                transactionFile.Sprites.AddRange(transactionSprites);
                transactionFile.Palettes.Clear();
                transactionFile.Palettes.AddRange(transactionPalettes);
                foreach (var (sprite, snapshot) in transactionSpriteStates) RestoreSpriteSnapshot(sprite, snapshot);
                foreach (var (palette, colors) in transactionPaletteColors) palette.Colors = colors;
                if (ReferenceEquals(_file, transactionFile))
                {
                    AfterEdit();
                    RebuildPaletteList();
                }
            }
            CrashLog.Write("OnImportAllSpritesAndPalettes", ex);
            await Dialogs.ShowMessage(this, "Could Not Import", $"{ex.Message}\n\nDetails were written to {CrashLog.Path}");
        }
        finally
        {
            if (busy) EndBusy();
        }
    }

    /// <summary>Exports sprites and a manifest .txt, optionally including separate palette PNGs.</summary>
    private async Task ExportAllSpritesAndPalettes(BulkSpriteExportOptions options)
    {
        if (_file is null || _busy) return;
        string suggested = _path is null ? "export" : Path.GetFileNameWithoutExtension(_path);
        var file = await PickSave(new FilePickerSaveOptions
        {
            Title = "Export Sprites - Choose the Manifest .txt",
            SuggestedFileName = $"{suggested}.txt",
            DefaultExtension = "txt",
            FileTypeChoices = [TxtType],
        });
        if (file?.TryGetLocalPath() is not { } txtPath) return;
        if (Path.GetDirectoryName(txtPath) is not { } dir) return;
        string prefix = SanitizeFileName(Path.GetFileNameWithoutExtension(txtPath));

        var sff = _file;
        var eligible = ExportableSprites(sff, options.Format);
        int skipped = sff.Sprites.Count - eligible.Count;
        var skippedItems = LogSkippedSprites("ExportAllSpritesAndPalettes", sff.Sprites.Except(eligible), options.Format);
        var duplicateSpriteKeys = eligible.GroupBy(s => (s.Group, s.Index)).Where(g => g.Count() > 1).ToList();
        var embeddedPalettes = options.IncludePalettes ? sff.Palettes.Where(p => p.Source != PaletteSource.External).ToList() : [];
        var duplicatePaletteKeys = embeddedPalettes.GroupBy(p => (p.Group, p.Index)).Where(g => g.Count() > 1).ToList();
        if (duplicateSpriteKeys.Count > 0 || duplicatePaletteKeys.Count > 0)
        {
            var conflicts = duplicateSpriteKeys.Select(g => $"sprite {g.Key.Group},{g.Key.Index}")
                .Concat(duplicatePaletteKeys.Select(g => $"palette {g.Key.Group},{g.Key.Index}"));
            var message = string.Join(", ", conflicts);
            CrashLog.Error("ExportAllSpritesAndPalettes", $"Cannot export duplicate keys: {message}");
            await Dialogs.ShowMessage(this, "Cannot Export Duplicate Keys", message);
            return;
        }
        var canvas = options.PadCanvas ? CommonCanvas(eligible) : default;
        CrashLog.Trace($"[bulk] export {eligible.Count} sprites to {dir}; format {options.Format}; padded={options.PadCanvas}");

        if (!TryBeginBusy()) return;
        try
        {
            int written = 0;
            var entries = new List<ExportManifest.Entry>();
            var usedPalettes = new List<Palette>();
            var seen = new HashSet<Palette>();
            for (int n = 0; n < eligible.Count; n++)
            {
                var s = eligible[n];
                await BusyStep("Exporting sprite", n, eligible.Count, $"{s.Group},{s.Index} {s.Width}x{s.Height}");
                if (!ReferenceEquals(_file, sff)) { StatusText.Text = "Export stopped \u2014 the file was closed."; return; }
                var (bytes, extension) = EncodeExportSprite(s, options.Format, s.Palette, _showMask, options.PadCanvas, canvas);
                string name = $"{prefix}_{s.Group}_{s.Index}.{extension}";
                File.WriteAllBytes(Path.Combine(dir, name), bytes);
                entries.Add(new("sprite", s.Group, s.Index, name,
                    Axis: options.PadCanvas ? (canvas.AxisX, canvas.AxisY) : (s.AxisX, s.AxisY),
                    Palette: options.IncludePalettes && s.Palette is { Source: not PaletteSource.External } sp
                        ? (sp.Group, sp.Index) : null));
                if (options.IncludePalettes && s.Palette is { Source: not PaletteSource.External } pal && seen.Add(pal))
                    usedPalettes.Add(pal);
                written++;
            }
            // Every palette the SFF itself holds, but never external character .acts.
            var allPalettes = options.IncludePalettes
                ? sff.Palettes.Where(p => p.Source != PaletteSource.External)
                    .Concat(usedPalettes.Except(sff.Palettes)).ToList()
                : [];
            if (options.IncludePalettes)
                entries.InsertRange(0, ExportAllPalettes(allPalettes, dir, prefix));
            File.WriteAllText(txtPath, ExportManifest.Write(entries));
            CrashLog.Trace($"[bulk] export done: {written} sprites, {allPalettes.Count} palettes");

            StatusText.Text = options.IncludePalettes
                ? $"Exported {written} sprites and {allPalettes.Count} palettes to {dir}"
                    + (skipped > 0 ? $", skipped {skipped} (see crash.log)" : "")
                : $"Exported {written} sprites to {dir}"
                    + (skipped > 0 ? $", skipped {skipped} (see crash.log)" : "");
            ShowSkipped("Sprites Skipped", skippedItems);
        }
        catch (Exception ex)
        {
            CrashLog.Write("ExportAllSpritesAndPalettes", ex);
            await Dialogs.ShowMessage(this, "Export Failed", $"Export failed: {ex.Message} (see crash.log)");
        }
        finally
        {
            EndBusy();
        }
    }

    /// <summary>Strips characters that can't appear in a file name — a user typing a
    /// name for an export might type anything — so the prefix always makes a valid
    /// path. Shared by both round-trip exports (sprites+palettes, sounds).</summary>
    private static string SanitizeFileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name;
    }

    /// <summary>Logs skipped/failed items from a bulk operation to crash.log.</summary>
    private static void LogBulkProblems(string source, IEnumerable<string> errors, IEnumerable<string> unmatched)
    {
        foreach (var err in errors) CrashLog.Error(source, err);
        var um = unmatched.ToList();
        if (um.Count > 0) CrashLog.Error(source, "no matching item in the open file for: " + string.Join(", ", um));
    }
}
