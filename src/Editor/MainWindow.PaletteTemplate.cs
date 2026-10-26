using System.IO;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Sff;

namespace Editor;

public partial class MainWindow
{
    private const int MaxPaletteTemplateSpacing = 256, MaxPaletteTemplateSwatchSize = 64;

    /// <summary>The template's sprites for one palette, the line saying where they came from, and sprite 0,0's piece if any.</summary>
    private sealed record PaletteTemplateSource(List<PaletteTemplate.Piece> Pieces, string Note, int? Stance = null);

    /// <summary>Exports sprites that show every index a palette's sprites use, plus its swatch grid, as one indexed PNG.</summary>
    private async void OnCreatePaletteTemplate(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_file is not { } file || file.Sprites.Count == 0)
            {
                StatusText.Text = "Open a sprite file with sprites first.";
                return;
            }
            var palettes = file.Palettes.ToList();
            if (palettes.Count == 0)
            {
                StatusText.Text = "This sprite file has no palettes.";
                return;
            }

            int count = Settings.Checked(nameof(_settings.Sprites.PaletteTemplateSprites), _settings.Sprites.PaletteTemplateSprites,
                Settings.DefaultPaletteTemplateSprites, v => v is >= 1 and <= 1000);
            int padding = Settings.Checked(nameof(_settings.Sprites.PaletteTemplatePaddingPixels), _settings.Sprites.PaletteTemplatePaddingPixels,
                Settings.DefaultPaletteTemplatePaddingPixels, v => v is >= 0 and <= MaxPaletteTemplateSpacing);
            int cell = Settings.Checked(nameof(_settings.Sprites.PaletteTemplateSwatchSize), _settings.Sprites.PaletteTemplateSwatchSize,
                Settings.DefaultPaletteTemplateSwatchSize, v => v is >= 1 and <= MaxPaletteTemplateSwatchSize);

            var sources = new Dictionary<Palette, PaletteTemplateSource>();
            PaletteTemplateSource SourceOf(Palette p) =>
                sources.TryGetValue(p, out var s) ? s : sources[p] = PaletteTemplateSprites(file, p);

            (PaletteTemplate.Sheet? Sheet, PaletteTemplatePreview Preview) Make(PaletteTemplateChoice c)
            {
                var pal = palettes[c.Palette];
                var source = SourceOf(pal);
                if (source.Pieces.Count == 0) return (null, new PaletteTemplatePreview(null, 0, 0, 0, source.Note));
                var pick = PaletteTemplate.Choose(source.Pieces, c.Count, c.Seed, source.Stance);
                // Choose takes sprite 0,0 first, so it leads the sheet too.
                int? lead = source.Stance is { } s && pick.Chosen.Count > 0 && pick.Chosen[0] == s ? 0 : null;
                var sheet = PaletteTemplate.Build(pick.Chosen.Select(i => source.Pieces[i]).ToList(), c.Spacing, c.SwatchSize, lead);
                string coverage = $"Indexes covered: {pick.Covered} of {pick.Used} used.";
                if (pick.Covered < pick.Used)
                    coverage += $" {pick.NeededForAll} sprite{(pick.NeededForAll == 1 ? "" : "s")} cover all.";
                return (sheet, new PaletteTemplatePreview(PaletteTemplate.ToBgra(sheet, pal.Colors), sheet.Width, sheet.Height,
                    source.Pieces.Count, source.Note + "\n" + coverage));
            }

            var ordinals = ExternalPaletteOrdinals(file);
            var carriers = V1PaletteCarriers(file);
            var names = palettes.Select(p => PaletteDropdownName(file, p, ordinals, carriers)).ToList();
            int initial = ActivePalette() is { } active && palettes.IndexOf(active) is >= 0 and var at ? at : 0;
            if (await Dialogs.PromptPaletteTemplate(this, names, initial, count, padding, MaxPaletteTemplateSpacing,
                    cell, MaxPaletteTemplateSwatchSize, c => Make(c).Preview) is not { } choice
                || !ReferenceEquals(_file, file) || !file.Palettes.Contains(palettes[choice.Palette]))
                return;

            var chosen = palettes[choice.Palette];
            if (Make(choice).Sheet is not { } built) return;
            var target = await PickSave(new FilePickerSaveOptions
            {
                Title = "Export Palette Template",
                SuggestedFileName = PaletteTemplateFileName(chosen),
                DefaultExtension = "png",
                FileTypeChoices = [PngType],
            });
            if (target?.TryGetLocalPath() is not { } path) return;

            Backups.Make(path, _settings.Files.BackupCount);
            File.WriteAllBytes(path, PaletteTemplate.Encode(built, chosen.Colors));
            CrashLog.Trace($"[palettes] template for {PaletteKey(file, chosen)}: {choice.Count} sprites, spacing {choice.Spacing}, swatch {choice.SwatchSize}, seed {choice.Seed}, {built.Width}x{built.Height}");
            StatusText.Text = $"Wrote {Path.GetFileName(path)}.";
        }
        catch (Exception ex)
        {
            await Failed(nameof(OnCreatePaletteTemplate), ex);
        }
    }

    /// <summary>
    /// Indexed, decoded sprites whose palette is p or shares its colors through a link, one per bitmap.
    /// A palette no sprite uses (external, temp or an unused alt) recolors the sprites of 1,1 (SFF v1: S1).
    /// Sprite 0,0 is kept whenever it is among them.
    /// </summary>
    private static PaletteTemplateSource PaletteTemplateSprites(SffFile file, Palette p)
    {
        static Palette Root(Palette x) => x.LinksTo ?? x;
        List<Sprite> UsersOf(Palette x) =>
            file.Sprites.Where(s => s.Palette is { } sp && ReferenceEquals(Root(sp), Root(x))).ToList();

        string key = PaletteDisplayName(file, p);
        var users = p.Source == PaletteSource.Embedded ? UsersOf(p) : new List<Sprite>();
        string note;
        if (users.Count > 0) note = $"Sprites using {key}: {users.Count}.";
        else
        {
            var stand = SffVersions.IsV1(file.Version)
                ? file.Palettes.FirstOrDefault(x => x.Source == PaletteSource.Embedded)
                : file.Palettes.FirstOrDefault(x => x.Source == PaletteSource.Embedded && x.Group == 1 && x.Index == 1);
            string standKey = stand is null ? (SffVersions.IsV1(file.Version) ? "S1" : "1,1") : PaletteKey(file, stand);
            users = stand is null ? new List<Sprite>() : UsersOf(stand);
            if (users.Count == 0)
                return new PaletteTemplateSource([], $"No sprite uses {key} or {standKey}, so there is nothing to show it on.");
            note = $"No sprite uses {key}; showing it on the {users.Count} sprites of {standKey}.";
        }

        // Sprite 0,0 (the standing pose) is always picked when it is one of them.
        var stanceOwner = users.FirstOrDefault(s => s.Group == 0 && s.Index == 0) is { } stance ? PixelOwner(stance) : null;
        int? stanceAt = null;
        var pieces = new List<PaletteTemplate.Piece>();
        foreach (var owner in users.Select(PixelOwner).Distinct())
            if (owner.DecodeError is null && owner.Indexed is { } pixels
                && PaletteTemplate.Trim(pixels, owner.Width, owner.Height) is { } piece)
            {
                if (ReferenceEquals(owner, stanceOwner)) stanceAt = pieces.Count;
                pieces.Add(piece);
            }
        if (pieces.Count == 0)
            return new PaletteTemplateSource([], note + " None of them has visible indexed pixels.");
        return new PaletteTemplateSource(pieces, note, stanceAt);
    }

    private static string PaletteTemplateFileName(Palette p) =>
        (p.Source == PaletteSource.External
            ? p.SourcePath is { } path ? Path.GetFileNameWithoutExtension(path) : "palette"
            : p.IsCharacterPalette ? $"pal{p.Index}" : $"{p.Group}_{p.Index}") + "_template.png";
}
