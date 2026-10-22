using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Sff;

namespace Editor;

public partial class MainWindow
{
    private static readonly FilePickerFileType PngPcxType =
        new("PNG or PCX images") { Patterns = ["*.png", "*.pcx"] };

    /// <summary>Builds one palette from the colors of picked images.</summary>
    private async void OnCreatePaletteFromImages(object? sender, RoutedEventArgs e)
    {
        try
        {
            var files = await PickOpen(new FilePickerOpenOptions
            {
                Title = "Create Palette from Images",
                AllowMultiple = true,
                FileTypeFilter = [PngPcxType],
            });
            if (files.Count == 0) return;
            if (_file is null && !await SettlePendingColorAdjust()) return;

            var builder = new PaletteFromImages();
            var errors = new List<string>();
            int read = 0;
            foreach (var f in files)
            {
                if (f.TryGetLocalPath() is not { } path)
                {
                    CrashLog.Error("OnCreatePaletteFromImages", $"{f.Name}: selected file has no local path");
                    errors.Add($"{f.Name}: selected file has no local path");
                    continue;
                }
                try
                {
                    builder.Add(File.ReadAllBytes(path));
                    read++;
                }
                catch (Exception ex)
                {
                    CrashLog.Write($"OnCreatePaletteFromImages {path}", ex);
                    errors.Add($"{Path.GetFileName(path)}: {ex.Message}");
                }
            }

            if (builder.ColorCount == 0)
            {
                if (read > 0) CrashLog.Error("OnCreatePaletteFromImages", "the picked images have no visible pixels");
                errors.Insert(0, read > 0 ? "The picked images have no visible pixels." : "No image could be read.");
                await Dialogs.ShowMessage(this, "Create Palette from Images", string.Join("\n", errors));
                return;
            }

            int sourceColors = builder.ColorCount;
            var (pal, reduced) = builder.Build();
            AddTempPalette(pal, "create palette from images");

            string from = read == 1 ? "1 image" : $"{read} images";
            StatusText.Text = reduced
                ? $"Created a temp palette from {from}; {sourceColors} colors reduced to 255."
                : $"Created a temp palette from {from} ({sourceColors} colors).";
            if (errors.Count > 0)
                await Dialogs.ShowMessage(this, "Some Images Could Not Be Read", string.Join("\n", errors));
        }
        catch (Exception ex)
        {
            await Failed("OnCreatePaletteFromImages", ex);
        }
    }

    /// <summary>
    /// Adds a temp palette (no file yet). With an SFF it joins the list as one undo step and
    /// becomes an SFF palette only when applied to sprites; without one it is a standalone palette.
    /// </summary>
    /// <param name="after">Duplicate puts the copy right under its source; null appends.</param>
    private void AddTempPalette(Palette pal, string undoName, Palette? after = null)
    {
        pal.Source = PaletteSource.External;
        pal.SourcePath = null;
        pal.Group = 1;
        pal.Dirty = true; // never saved
        if (_file is { } file)
        {
            var taken = file.Palettes.Where(p => p.Group == 1).Select(p => p.Index).ToHashSet();
            pal.Index = Enumerable.Range(1, int.MaxValue - 1).First(i => !taken.Contains(i));
            int at = after is not null && file.Palettes.IndexOf(after) is var i and >= 0 ? i + 1 : file.Palettes.Count;
            Edit(undoName,
                () => { file.Palettes.Insert(Math.Min(at, file.Palettes.Count), pal); SelectPalette(pal); },
                () => { file.Palettes.Remove(pal); _paletteOverride = -1; });
        }
        else
        {
            pal.Index = _standalonePalettes.Count + 1;
            AddStandalonePalette(pal, after);
        }
    }

    /// <summary>Adds a standalone palette and selects it for editing.</summary>
    private void AddStandalonePalette(Palette palette, Palette? after = null)
    {
        int at = after is not null && _standalonePalettes.IndexOf(after) is var i and >= 0 ? i + 1 : _standalonePalettes.Count;
        _standalonePalettes.Insert(at, palette);
        _standalonePaletteOverride = at;
        PaletteFilterBox.Text = "";
        RebuildPaletteList();
        _editingPalette = palette;
        _editingView = BigSwatches;
        _selectedSwatch = 0;
        ColorEditor.IsEnabled = true;
        ASlider.IsEnabled = CanEditPaletteAlpha(palette);
        var color = palette.Colors[0];
        _suppressEvents = true;
        RSlider.Value = color.R; GSlider.Value = color.G; BSlider.Value = color.B; ASlider.Value = color.A;
        _suppressEvents = false;
        ResetColorAdjustBase();
        RefreshPaletteTab();
        UpdateTitle();
    }
}
