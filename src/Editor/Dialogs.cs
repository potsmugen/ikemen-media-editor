using System.Globalization;
using System.IO;
using Air;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Sff;

namespace Editor;

/// <summary>Which channels a gradient interpolates. A palette entry's untouched channels keep their own value.</summary>
[Flags]
public enum GradientChannels { None = 0, R = 1, G = 2, B = 4, A = 8 }

/// <summary>How to reorder a frame's own Clsn boxes.</summary>
public enum BoxSortOrder { TopToBottom, BottomToTop, LeftToRight, RightToLeft, SmallestFirst, LargestFirst }

/// <summary>What a color sort orders by.</summary>
public enum ColorSortKey { Brightness, Hue, Index }

/// <summary>What the color sort dialog returned.</summary>
public sealed record ColorReorderChoice(ColorSortKey Key, bool Descending, bool UpdateSprites);

/// <summary>What the user chose when asked about unsaved work.</summary>
public enum SaveChoice { Save, Discard, Cancel }

/// <summary>What the user chose when switching sprites with an uncommitted Paint Mode stroke pending.</summary>
public enum PendingPaintChoice { Commit, Discard, Cancel }

/// <summary>What the user chose when a sprite change would drop an uncommitted palette adjustment.</summary>
public enum PendingAdjustChoice { Commit, Discard, Cancel }

/// <summary>What the user chose when switching sounds with uncommitted sound edits pending.</summary>
public enum PendingSoundEditChoice { Commit, Discard, Cancel }

public enum RecoveryChoice { Recover, Discard, Dismiss }

/// <summary>What the user chose in the sprite link action dialog.</summary>
public enum SpriteShareDialogAction { Unlink, Link }

/// <summary>How the user chose a sprite link target.</summary>
public enum SpriteTargetDialogAction { UseGroupImage, PickFromList }

public sealed record SpriteTargetDialogResult(SpriteTargetDialogAction Action, int Group = 0, int Image = 0);

/// <summary>What the user chose in the palette link action dialog.</summary>
public enum PaletteShareDialogAction { Unlink, Link, Merge }

/// <summary>How the user chose a palette link target.</summary>
public enum PaletteTargetDialogAction { UseGroupIndex, PickFromList }

public sealed record PaletteTargetDialogResult(PaletteTargetDialogAction Action, int Group = 0, int Index = 0);

/// <summary>Which palette a newly imported sprite should use.</summary>
public enum ImportPaletteChoice { Own, Previous, Specific }

public enum SffV1PaletteChoice { Own, Previous }

/// <summary>What to do with duplicate embedded palettes.</summary>
public enum DuplicatePaletteAction { Merge, Link }

public enum DeleteSpritesPaletteChoice { DeleteSpritesAndPalettes, DeleteSpritesOnly, Cancel }

public enum StandalonePaletteChoice { Keep, Discard }
public enum StandalonePaletteOpenChoice { Save, Discard, Cancel }

/// <summary>What to do when a GIF's frames need more colors than one palette holds.</summary>
public enum GifOverflowChoice { TrueColor, Reduce }

/// <summary>How an imported image's pixel indexes land in the chosen palette.</summary>
public enum ImportIndexChoice { Keep, Nearest }

/// <summary>What PromptSpriteImport collected. Group/Number only apply when adding.
/// PaletteGroup/PaletteIndex only apply when Own creates a genuinely new palette
/// (not one reused because it's identical to an existing one).</summary>
public enum BitDepthChoice { EightBit, ThirtyTwoBit }

public sealed record ImportChoices(
    ImportPaletteChoice PaletteChoice, Palette? SpecificPalette,
    ImportIndexChoice IndexChoice, BitDepthChoice BitDepth,
    int Group, int Number, int PaletteGroup, int PaletteIndex)
{
    /// <summary>With nearest matching from an indexed image, its index 0 stays the mask.</summary>
    public bool KeepMask { get; init; } = true;
}

/// <summary>How many of the imported images each palette (by list position) reproduces exactly, per index mode.</summary>
public sealed record ImportPaletteMatches(int[] Keep, int[] Nearest, int Total);

/// <summary>The import dialog's preview: builds the first image under given choices, drawn over the canvas look.</summary>
public sealed record ImportPreview(Func<ImportChoices, Sprite> Build, CanvasLook Look);

/// <summary>How the sprite canvas dimensions should change.</summary>
public sealed record SpriteResizeChoices(bool Relative, int Width, int Height);

/// <summary>How sprite pixels should scale.</summary>
public sealed record SpriteScaleChoices(bool Percentage, double Width, double Height, SpriteRotate.Interpolation Interpolation);

/// <summary>How far to shift the selected sprites' axes.</summary>
public sealed record SpriteOffsetShift(int X, int Y);

/// <summary>Where to place the selected sprites' axes on their canvases.</summary>
public enum SpriteAxisPosition
{
    TopLeft, TopCenter, TopRight,
    MiddleLeft, Center, MiddleRight,
    BottomLeft, BottomCenter, BottomRight,
}

public enum BulkSpriteExportFormat { Png, Pcx, Preserve }

public sealed record BulkSpriteExportOptions(BulkSpriteExportFormat Format, bool IncludePalettes, bool PadCanvas);

/// <summary>
/// Avalonia ships no message box, so these are hand-built modal windows. Kept
/// deliberately plain: a message, some buttons, and a result.
/// </summary>
// Every TaskCompletionSource here uses RunContinuationsAsynchronously: otherwise TrySetResult runs the
// caller inline, before dialog.Close(), and a picker opened next can leave the main window disabled.
/// <summary>What Find Similar Sprites should look for: the level chosen in the dialog.</summary>
public sealed record SimilarSpriteChoices(SimilarityChoice Level, bool RequireSamePalette);

/// <summary>One named level in the Find Similar Sprites dialog: what it is called, what it
/// catches, and the percentage the scan runs at (see Settings.SimilarExactPercent).</summary>
public sealed record SimilarityChoice(string Label, string Detail, int Percent);

public static class Dialogs
{
    /// <summary>
    /// Looks up a themed brush by resource key. Dialogs are short-lived (built,
    /// shown modally, discarded), so a snapshot at creation time is enough —
    /// no need for a live DynamicResource binding.
    /// </summary>
    private static IBrush ThemeBrush(string key) =>
        Avalonia.Application.Current?.Resources.TryGetResource(key, null, out var v) == true && v is IBrush b
            ? b : Brushes.Gray;

    public static Task ShowMessage(Window owner, string title, string message) =>
        Show<object?>(owner, title, message, [("OK", null)]);

    /// <summary>A message with OK (the default) and one extra button; true when the extra one was clicked.</summary>
    public static Task<bool> ShowMessageWithAction(Window owner, string title, string message, string action) =>
        Show(owner, title, message, [("OK", false), (action, true)]);

    /// <summary>
    /// A read-only shortcut reference: each section is a heading over rows of chord and
    /// description. One shared grid gives every section the same chord column, so the two
    /// columns line up whatever the font size or window width — something space-padded
    /// text in a proportional font cannot do.
    /// </summary>
    public static Task ShowShortcuts(Window owner, string title,
        IReadOnlyList<(string Section, (string Chord, string Text)[] Items)> sections)
    {
        var result = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);

        var mono = new FontFamily("Consolas,Menlo,monospace");
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            ColumnSpacing = 16,
            RowSpacing = 4,
            MaxWidth = 560,
        };

        void AddHeading(string text)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var heading = new TextBlock
            {
                Text = text,
                FontWeight = FontWeight.Bold,
                Margin = new Avalonia.Thickness(0, grid.RowDefinitions.Count == 1 ? 0 : 12, 0, 2),
            };
            Grid.SetRow(heading, grid.RowDefinitions.Count - 1);
            Grid.SetColumnSpan(heading, 2);
            grid.Children.Add(heading);
        }

        void AddItem(string chord, string text)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            int row = grid.RowDefinitions.Count - 1;
            var chordText = new TextBlock { Text = chord, FontFamily = mono };
            Grid.SetRow(chordText, row);
            Grid.SetColumn(chordText, 0);
            var description = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(description, row);
            Grid.SetColumn(description, 1);
            grid.Children.Add(chordText);
            grid.Children.Add(description);
        }

        foreach (var (section, items) in sections)
        {
            AddHeading(section);
            foreach (var (chord, text) in items) AddItem(chord, text);
        }

        var ok = new Button { Content = "OK", MinWidth = 90, IsDefault = true };

        var dialog = new Window
        {
            Background = ThemeBrush("ThemePanelBg"),
            Foreground = ThemeBrush("ThemeText"),
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false,
        };

        ok.Click += (_, _) => { result.TrySetResult(null); dialog.Close(); };
        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 18,
            Children =
            {
                new ScrollViewer { Content = grid, MaxHeight = 520, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { ok },
                },
            },
        };
        dialog.Closed += (_, _) => result.TrySetResult(null);
        dialog.Opened += (_, _) => ok.Focus();

        dialog.ShowDialog(owner);
        return result.Task;
    }

    internal static Task<RecoveryChoice> PromptRecovery(Window owner, RecoveryManifest manifest)
    {
        // With several tabs, each line says which tab it restores into.
        bool tabs = manifest.Items.Select(i => i.Tab).Distinct().Count() > 1;
        string lines = string.Join(Environment.NewLine, manifest.Items.OrderBy(i => i.TabOrder).Select(item =>
            (tabs ? $"[{item.TabName ?? (item.TabDef is { } def ? Path.GetFileName(def) : $"Tab {item.TabOrder + 1}")}] " : "")
            + $"{item.Kind.ToUpperInvariant()}: {item.Title}"
            + (item.SourcePath is null ? " (new file)" : $" — {item.SourcePath}")
            + (RecoveryStore.SourceMatches(item) ? "" : " (source changed or missing; recovers separately)")));
        var result = new TaskCompletionSource<RecoveryChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
        var recover = new Button { Content = "Recover", MinWidth = 90, IsDefault = true };
        var discard = new Button { Content = "Discard", MinWidth = 90 };
        var body = new StackPanel { Spacing = 8, Margin = new Avalonia.Thickness(16) };
        body.Children.Add(new TextBlock { Text = $"Unsaved work from {manifest.CapturedAt.LocalDateTime:g}:", TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new TextBlock { Text = lines, TextWrapping = TextWrapping.Wrap });
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { recover, discard } };
        body.Children.Add(row);
        var dialog = new Window
        {
            Background = ThemeBrush("ThemePanelBg"), Foreground = ThemeBrush("ThemeText"),
            Title = "Recover Unsaved Work", SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false, ShowInTaskbar = false,
            Content = body,
        };
        recover.Click += (_, _) => { result.TrySetResult(RecoveryChoice.Recover); dialog.Close(); };
        discard.Click += (_, _) => { result.TrySetResult(RecoveryChoice.Discard); dialog.Close(); };
        dialog.Closed += (_, _) => result.TrySetResult(RecoveryChoice.Dismiss);
        dialog.ShowDialog(owner);
        return result.Task;
    }

    /// <summary>A yes/no question, with the affirmative button labeled by the caller.</summary>
    public static Task<bool> Confirm(Window owner, string title, string message, string affirmative) =>
        Show(owner, title, message, [(affirmative, true), ("Cancel", false)], false);

    public static Task<StandalonePaletteChoice?> PromptKeepStandalonePalettes(Window owner, int count, int unsaved)
    {
        string noun = count == 1 ? "palette" : "palettes";
        string loss = unsaved == 0 ? ""
            : unsaved == 1 ? "\n1 has unsaved changes that Discard loses."
            : $"\n{unsaved} have unsaved changes that Discard loses.";
        return Show<StandalonePaletteChoice?>(owner, "Open Palettes",
            $"Keep {count} open {noun} in the palettes list for the SFF, or discard them?{loss}",
            [("Keep", StandalonePaletteChoice.Keep), ("Discard", StandalonePaletteChoice.Discard), ("Cancel", null)], null);
    }

    public static Task<StandalonePaletteOpenChoice> PromptUnsavedStandalonePalettes(Window owner, int count)
    {
        string noun = count == 1 ? "palette file has" : "palette files have";
        return Show(owner, "Unsaved Palette Changes",
            $"{count} standalone {noun} unsaved changes.",
            [("Save", StandalonePaletteOpenChoice.Save), ("Discard", StandalonePaletteOpenChoice.Discard), ("Cancel", StandalonePaletteOpenChoice.Cancel)],
            StandalonePaletteOpenChoice.Cancel);
    }

    public static Task<DeleteSpritesPaletteChoice> PromptDeleteSpritesAndPalettes(Window owner,
        IReadOnlyList<Palette> palettes, int linkedPaletteCount)
    {
        string list = string.Join(", ", palettes.Select(p => $"{p.Group},{p.Index}"));
        string message = $"After deleting the selected sprites, these palettes will have no remaining sprite users:\n{list}\nDelete these palettes too?";
        if (linkedPaletteCount > 0)
        {
            string noun = linkedPaletteCount == 1 ? "linked palette will be" : "linked palettes will be";
            message += $"\n{linkedPaletteCount} {noun} unlinked with their current colors preserved.";
        }
        return Show(owner, "Delete Sprites and Palettes?", message,
            [("Delete Sprites and Palettes", DeleteSpritesPaletteChoice.DeleteSpritesAndPalettes),
                ("Delete Sprites Only", DeleteSpritesPaletteChoice.DeleteSpritesOnly),
                ("Cancel", DeleteSpritesPaletteChoice.Cancel)],
            DeleteSpritesPaletteChoice.Cancel);
    }

    public static Task<bool> PromptShortPalette(Window owner, int needed, int available) =>
        Show(owner, "Palette Has Fewer Colors", $"This sprite uses color indexes up to {needed - 1}, so it needs {needed} palette entries. The file contains {available}. Reject the import, or pad the missing entries with opaque black?",
            [("Reject Import", false), ("Pad with Opaque Black", true)], false);

    /// <summary>
    /// A GIF repeats frames to hold a pose, and often returns to an earlier
    /// image. This asks what to do with them: drop the repeats, keep every
    /// frame, or stop. Returns null if canceled.
    /// </summary>
    public static Task<bool?> PromptDuplicateGifFrames(Window owner, string name, int total, int duplicates)
    {
        string frames = total == 1 ? "1 frame" : $"{total} frames";
        string repeats = duplicates == 1 ? "1 is a repeat" : $"{duplicates} are repeats";
        return Show<bool?>(owner, "Repeated Frames",
            $"{name} has {frames}, and {repeats} of an earlier frame. Dropping them leaves "
            + $"{total - duplicates}, so the animation may hold a pose for a shorter time. Keep every frame instead?",
            [("Drop Repeats", true), ("Keep Every Frame", false), ("Cancel", null)]);
    }

    /// <summary>
    /// A GIF keeps 256 colors per color table rather than per file, so one
    /// animation's frames can use more colors between them than a palette holds.
    /// This asks what to do: keep every color by bringing the frames in as
    /// 32-bit color, or cut them down to what a palette holds. Returns null if
    /// canceled, which stops the import.
    /// </summary>
    public static Task<GifOverflowChoice?> PromptGifTooManyColors(Window owner, string name, int frames, int colors)
    {
        string many = frames == 1 ? "1 frame" : $"{frames} frames";
        int max = Sff.GifImport.MaxSharedColors;
        return Show<GifOverflowChoice?>(owner, "Too Many Colors for One Palette",
            $"{name} has {many} that use {colors} colors between them, more than the {max} one palette holds. "
            + $"32-bit color keeps every color exactly. Reducing to {max} keeps the frames 8-bit, "
            + "but shifts the colors that do not fit.",
            [("Use 32-bit Color", GifOverflowChoice.TrueColor),
             ($"Reduce to {max} Colors", GifOverflowChoice.Reduce),
             ("Cancel", null)], null);
    }

    public static Task<bool> PromptSffV1AlphaLoss(Window owner) =>
        Show(owner, "SFFv1 Cannot Store Palette Alpha", "SFFv1 stores palette colors as RGB. Alpha from this palette will be lost when saved. Continue without alpha?",
            [("Cancel", false), ("Continue without Alpha", true)], false);

    /// <summary>A single-line text prompt. Returns null if canceled. Null sizes mean several sprites,
    /// which have no single current canvas: the line and the pre-filled values are left out.</summary>
    public static Task<SpriteResizeChoices?> PromptSpriteResize(Window owner, int? currentWidth, int? currentHeight)
    {
        var result = new TaskCompletionSource<SpriteResizeChoices?>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Null sizes mean several sprites; curW/curH are read on the single path only.
        bool single = currentWidth.HasValue && currentHeight.HasValue;
        int curW = currentWidth ?? 0, curH = currentHeight ?? 0;
        var absolute = new RadioButton { Content = "Absolute size", GroupName = "ResizeMode", IsChecked = true };
        var relative = new RadioButton { Content = "Relative change", GroupName = "ResizeMode" };
        var width = new NumericUpDown { Minimum = 1, Maximum = 65535, Increment = 1, FormatString = "0", Width = 100 };
        var height = new NumericUpDown { Minimum = 1, Maximum = 65535, Increment = 1, FormatString = "0", Width = 100 };

        void UpdateMode()
        {
            bool isRelative = relative.IsChecked == true;
            // A relative change is bounded by what the sprite(s) can absorb. One sprite gives an
            // exact range; several share the union of every sprite's, so the exact range is checked
            // per sprite when the resize runs.
            width.Minimum = isRelative ? single ? 1 - curW : -65534 : 1;
            height.Minimum = isRelative ? single ? 1 - curH : -65534 : 1;
            width.Maximum = isRelative ? single ? 65535 - curW : 65534 : 65535;
            height.Maximum = isRelative ? single ? 65535 - curH : 65534 : 65535;
            if (isRelative) { width.Value = 0; height.Value = 0; }
            else if (single) { width.Value = curW; height.Value = curH; }
            else { width.Value = null; height.Value = null; }
        }
        absolute.IsCheckedChanged += (_, _) => UpdateMode();
        relative.IsCheckedChanged += (_, _) => UpdateMode();

        var ok = new Button { Content = "Resize", MinWidth = 90, IsDefault = true };
        var cancel = new Button { Content = "Cancel", MinWidth = 90 };
        var body = new StackPanel { Spacing = 8, Margin = new Avalonia.Thickness(16) };
        if (single) body.Children.Add(new TextBlock { Text = $"Current canvas: {curW} × {curH}" });
        body.Children.Add(absolute);
        body.Children.Add(relative);
        body.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children =
        {
            new TextBlock { Text = "Width", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Width = 55 }, width,
            new TextBlock { Text = "Height", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Width = 55 }, height,
        }});
        body.Children.Add(new TextBlock
        {
            Text = single
                ? "The existing pixels stay centered; the axis moves with them. New indexed pixels use the transparent index."
                : "Each sprite's existing pixels stay centered; the axis moves with them. A relative change applies to each sprite's own size.",
            TextWrapping = TextWrapping.Wrap, MaxWidth = 420,
        });
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        row.Children.Add(ok); row.Children.Add(cancel); body.Children.Add(row);
        var dialog = new Window
        {
            Background = ThemeBrush("ThemePanelBg"), Foreground = ThemeBrush("ThemeText"),
            Title = single ? "Resize sprite canvas" : "Resize sprite canvases", SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false, ShowInTaskbar = false,
            Content = body,
        };
        ok.Click += (_, _) =>
        {
            result.TrySetResult(new SpriteResizeChoices(relative.IsChecked == true,
                (int)(width.Value ?? 0), (int)(height.Value ?? 0)));
            dialog.Close();
        };
        cancel.Click += (_, _) => { result.TrySetResult(null); dialog.Close(); };
        dialog.Closed += (_, _) => result.TrySetResult(null);
        dialog.Opened += (_, _) => width.Focus();
        dialog.ShowDialog(owner);
        return result.Task;
    }

    /// <summary>Collects the output size and sampling method for scaling sprite pixels.</summary>
    public static Task<SpriteScaleChoices?> PromptSpriteScale(Window owner, int? currentWidth, int? currentHeight,
        bool indexed, int initialPercent, int minPercent, int maxPercent)
    {
        var result = new TaskCompletionSource<SpriteScaleChoices?>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool single = currentWidth.HasValue && currentHeight.HasValue;
        var absolute = new RadioButton { Content = "Absolute size", GroupName = "SpriteScaleMode", IsChecked = true };
        var percentage = new RadioButton { Content = "X/Y percentages", GroupName = "SpriteScaleMode" };
        var width = new NumericUpDown { Minimum = 1, Maximum = 65535, Increment = 1, FormatString = "0", Width = 100,
            Value = currentWidth };
        var height = new NumericUpDown { Minimum = 1, Maximum = 65535, Increment = 1, FormatString = "0", Width = 100,
            Value = currentHeight };
        var scaleX = new NumericUpDown { Minimum = minPercent, Maximum = maxPercent, Increment = 1, FormatString = "0", Width = 100,
            Value = initialPercent };
        var scaleY = new NumericUpDown { Minimum = minPercent, Maximum = maxPercent, Increment = 1, FormatString = "0", Width = 100,
            Value = initialPercent };
        var samplings = new[] { SpriteRotate.Interpolation.Nearest, SpriteRotate.Interpolation.Area, SpriteRotate.Interpolation.Bilinear };
        var sampling = new ComboBox { MinWidth = 140 };
        foreach (var mode in samplings)
            sampling.Items.Add(new ComboBoxItem { Content = mode.ToString(), IsEnabled = !indexed || mode != SpriteRotate.Interpolation.Bilinear });
        sampling.SelectedIndex = 0;
        if (indexed) ToolTip.SetTip(sampling, "Bilinear is available for true-color sprites only.");

        var ok = new Button { Content = "Resize", MinWidth = 90, IsDefault = true };
        void UpdateMode()
        {
            bool isPercentage = percentage.IsChecked == true;
            width.IsEnabled = height.IsEnabled = !isPercentage;
            scaleX.IsEnabled = scaleY.IsEnabled = isPercentage;
            ok.IsEnabled = isPercentage || (width.Value.HasValue && height.Value.HasValue);
        }
        absolute.IsCheckedChanged += (_, _) => UpdateMode();
        percentage.IsCheckedChanged += (_, _) => UpdateMode();
        width.ValueChanged += (_, _) => UpdateMode();
        height.ValueChanged += (_, _) => UpdateMode();
        UpdateMode();

        var cancel = new Button { Content = "Cancel", MinWidth = 90, IsCancel = true };
        var body = new StackPanel { Spacing = 8, Margin = new Avalonia.Thickness(16) };
        if (single) body.Children.Add(new TextBlock { Text = $"Current size: {currentWidth} × {currentHeight}" });
        body.Children.Add(absolute);
        body.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children =
        {
            new TextBlock { Text = "Width", VerticalAlignment = VerticalAlignment.Center, Width = 55 }, width,
            new TextBlock { Text = "Height", VerticalAlignment = VerticalAlignment.Center, Width = 55 }, height,
        }});
        body.Children.Add(percentage);
        body.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children =
        {
            new TextBlock { Text = "X %", VerticalAlignment = VerticalAlignment.Center, Width = 55 }, scaleX,
            new TextBlock { Text = "Y %", VerticalAlignment = VerticalAlignment.Center, Width = 55 }, scaleY,
        }});
        body.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children =
        {
            new TextBlock { Text = "Sampling", VerticalAlignment = VerticalAlignment.Center, Width = 55 }, sampling,
        }});
        body.Children.Add(new TextBlock
        {
            Text = single
                ? "Scales pixels and axis coordinates. Indexed sprites keep their palette and indexes."
                : "Percentages apply to each sprite's own dimensions. Indexed sprites keep their palette and indexes.",
            TextWrapping = TextWrapping.Wrap, MaxWidth = 420,
        });
        body.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8, Children = { ok, cancel } });
        var dialog = new Window
        {
            Background = ThemeBrush("ThemePanelBg"), Foreground = ThemeBrush("ThemeText"),
            Title = single ? "Resize sprite" : "Resize sprites", SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false, ShowInTaskbar = false,
            Content = body,
        };
        ok.Click += (_, _) =>
        {
            result.TrySetResult(new(percentage.IsChecked == true,
                percentage.IsChecked == true ? (double)(scaleX.Value ?? initialPercent) : (double)(width.Value ?? 0),
                percentage.IsChecked == true ? (double)(scaleY.Value ?? initialPercent) : (double)(height.Value ?? 0),
                sampling.SelectedIndex >= 0 && sampling.SelectedIndex < samplings.Length
                    ? samplings[sampling.SelectedIndex] : SpriteRotate.Interpolation.Nearest));
            dialog.Close();
        };
        cancel.Click += (_, _) => { result.TrySetResult(null); dialog.Close(); };
        dialog.Closed += (_, _) => result.TrySetResult(null);
        dialog.Opened += (_, _) => { if (percentage.IsChecked == true) scaleX.Focus(); else width.Focus(); };
        dialog.ShowDialog(owner);
        return result.Task;
    }

    /// <summary>Asks where to place the selected sprites' axes.</summary>
    public static Task<SpriteAxisPosition?> PromptSpriteAxisPosition(Window owner)
    {
        var result = new TaskCompletionSource<SpriteAxisPosition?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var body = new StackPanel { Spacing = 8, Margin = new Avalonia.Thickness(16) };
        body.Children.Add(new TextBlock { Text = "Choose where to place the axis in each selected sprite." });
        var dialog = new Window
        {
            Background = ThemeBrush("ThemePanelBg"), Foreground = ThemeBrush("ThemeText"),
            Title = "Set Sprite Axis", SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false, ShowInTaskbar = false,
            Content = body,
        };

        var positions = new[]
        {
            ("Top Left", SpriteAxisPosition.TopLeft),
            ("Top Center", SpriteAxisPosition.TopCenter),
            ("Top Right", SpriteAxisPosition.TopRight),
            ("Middle Left", SpriteAxisPosition.MiddleLeft),
            ("Center", SpriteAxisPosition.Center),
            ("Middle Right", SpriteAxisPosition.MiddleRight),
            ("Bottom Left", SpriteAxisPosition.BottomLeft),
            ("Bottom Center", SpriteAxisPosition.BottomCenter),
            ("Bottom Right", SpriteAxisPosition.BottomRight),
        };
        var positionsGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto"),
            ColumnSpacing = 6,
            RowSpacing = 6,
        };
        for (int rowIndex = 0; rowIndex < 3; rowIndex++)
        {
            for (int columnIndex = 0; columnIndex < 3; columnIndex++)
            {
                var choice = positions[rowIndex * 3 + columnIndex];
                var button = new Button { Content = choice.Item1, MinWidth = 120 };
                button.Click += (_, _) => { result.TrySetResult(choice.Item2); dialog.Close(); };
                Grid.SetRow(button, rowIndex);
                Grid.SetColumn(button, columnIndex);
                positionsGrid.Children.Add(button);
            }
        }
        body.Children.Add(positionsGrid);

        var cancel = new Button { Content = "Cancel", MinWidth = 90 };
        body.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Children = { cancel },
        });
        cancel.Click += (_, _) => { result.TrySetResult(null); dialog.Close(); };
        dialog.Closed += (_, _) => result.TrySetResult(null);
        dialog.ShowDialog(owner);
        return result.Task;
    }

    /// <summary>Asks how far to shift the selected sprites' axes.</summary>
    public static Task<SpriteOffsetShift?> PromptSpriteOffsetShift(
        Window owner, int minX, int maxX, int minY, int maxY)
    {
        var result = new TaskCompletionSource<SpriteOffsetShift?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var x = new NumericUpDown
        {
            Minimum = minX, Maximum = maxX, Increment = 1, FormatString = "0",
            Value = Math.Clamp(0, minX, maxX), Width = 120,
        };
        var y = new NumericUpDown
        {
            Minimum = minY, Maximum = maxY, Increment = 1, FormatString = "0",
            Value = Math.Clamp(0, minY, maxY), Width = 120,
        };
        var ok = new Button { Content = "Apply", MinWidth = 90, IsDefault = true };
        var cancel = new Button { Content = "Cancel", MinWidth = 90 };
        var body = new StackPanel { Spacing = 8, Margin = new Avalonia.Thickness(16) };
        body.Children.Add(new TextBlock { Text = "Shift every selected sprite by the same amount." });
        body.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children =
            {
                new TextBlock { Text = "X", VerticalAlignment = VerticalAlignment.Center, Width = 55 }, x,
                new TextBlock { Text = "Y", VerticalAlignment = VerticalAlignment.Center, Width = 55 }, y,
            },
        });
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8,
            Children = { ok, cancel },
        };
        body.Children.Add(row);
        var dialog = new Window
        {
            Background = ThemeBrush("ThemePanelBg"), Foreground = ThemeBrush("ThemeText"),
            Title = "Shift Sprite Offsets", SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false, ShowInTaskbar = false,
            Content = body,
        };
        ok.Click += (_, _) =>
        {
            result.TrySetResult(new SpriteOffsetShift((int)(x.Value ?? 0), (int)(y.Value ?? 0)));
            dialog.Close();
        };
        cancel.Click += (_, _) => { result.TrySetResult(null); dialog.Close(); };
        dialog.Closed += (_, _) => result.TrySetResult(null);
        dialog.Opened += (_, _) => x.Focus();
        dialog.ShowDialog(owner);
        return result.Task;
    }

    /// <summary>Collects format, palette, and shared-canvas choices for a bulk sprite export.</summary>
    public static Task<BulkSpriteExportOptions?> PromptBulkSpriteExport(Window owner)
    {
        var result = new TaskCompletionSource<BulkSpriteExportOptions?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var format = new ComboBox { Width = 300 };
        format.Items.Add("PNG");
        format.Items.Add("PCX (indexed sprites only)");
        format.Items.Add("Preserve (PCX stays PCX; otherwise PNG)");
        format.SelectedIndex = 0;
        var palettes = new CheckBox { Content = "Include separate palette PNGs", IsChecked = true };
        var pad = new CheckBox { Content = "Pad every sprite to the same canvas size" };

        var ok = new Button { Content = "Export", MinWidth = 90, IsDefault = true };
        var cancel = new Button { Content = "Cancel", MinWidth = 90 };
        var body = new StackPanel { Spacing = 8, Margin = new Avalonia.Thickness(20) };
        body.Children.Add(new TextBlock { Text = "Image format" });
        body.Children.Add(format);
        body.Children.Add(palettes);
        body.Children.Add(pad);
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        row.Children.Add(ok);
        row.Children.Add(cancel);
        body.Children.Add(row);

        var dialog = new Window
        {
            Background = ThemeBrush("ThemePanelBg"), Foreground = ThemeBrush("ThemeText"),
            Title = "Export Sprites", SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false, ShowInTaskbar = false,
            Content = body,
        };
        ok.Click += (_, _) =>
        {
            var selectedFormat = format.SelectedIndex switch
            {
                1 => BulkSpriteExportFormat.Pcx,
                2 => BulkSpriteExportFormat.Preserve,
                _ => BulkSpriteExportFormat.Png,
            };
            result.TrySetResult(new(selectedFormat, palettes.IsChecked == true, pad.IsChecked == true));
            dialog.Close();
        };
        cancel.Click += (_, _) => { result.TrySetResult(null); dialog.Close(); };
        dialog.Closed += (_, _) => result.TrySetResult(null);
        dialog.ShowDialog(owner);
        return result.Task;
    }

    public enum SpriteExportFormat { Png, Pcx }

    public sealed record SpriteExportChoice(SpriteExportFormat Format, bool UsePreviewPalette);

    /// <summary>Export's format, asked before the save dialog, plus whether to color the
    /// image with the preview palette. That checkbox appears only while a preview
    /// override is selected, since without one both answers are the same sprite
    /// palette. Null = canceled.</summary>
    public static Task<SpriteExportChoice?> PromptSpriteFormat(Window owner, SpriteExportFormat initial, string? preview)
    {
        var result = new TaskCompletionSource<SpriteExportChoice?>(TaskCreationOptions.RunContinuationsAsynchronously);

        var png = new RadioButton { GroupName = "SpriteFormat", Content = ".png", IsChecked = initial == SpriteExportFormat.Png };
        var pcx = new RadioButton { GroupName = "SpriteFormat", Content = ".pcx (index 0 stays opaque)", IsChecked = initial == SpriteExportFormat.Pcx };
        CheckBox? previewBox = null;

        var ok = new Button { Content = "Export", MinWidth = 90, IsDefault = true };
        var cancel = new Button { Content = "Cancel", MinWidth = 90, IsCancel = true };
        var body = new StackPanel { Margin = new Avalonia.Thickness(20), Spacing = 8 };
        body.Children.Add(new TextBlock { Text = "Format:" });
        body.Children.Add(png);
        body.Children.Add(pcx);
        if (preview is { } label)
        {
            // Named, so the choice never says which colors it will write.
            previewBox = new CheckBox { Content = $"Use the preview palette ({label})" };
            body.Children.Add(previewBox);
        }
        body.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8, Margin = new Avalonia.Thickness(0, 8, 0, 0), Children = { ok, cancel },
        });

        var dialog = new Window
        {
            Background = ThemeBrush("ThemePanelBg"), Foreground = ThemeBrush("ThemeText"),
            Title = "Export Sprite", SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false, ShowInTaskbar = false,
            Content = body,
        };
        ok.Click += (_, _) =>
        {
            result.TrySetResult(new(pcx.IsChecked == true ? SpriteExportFormat.Pcx : SpriteExportFormat.Png,
                previewBox?.IsChecked == true));
            dialog.Close();
        };
        cancel.Click += (_, _) => { result.TrySetResult(null); dialog.Close(); };
        dialog.Closed += (_, _) => result.TrySetResult(null);

        dialog.ShowDialog(owner);
        return result.Task;
    }

    public sealed record SpriteRotateChoice(double Degrees, bool Clockwise, SpriteRotate.Pivot Pivot,
        SpriteRotate.Interpolation Interpolation);

    /// <summary>Which way to mirror the selection. Both at once is a half turn, which is
    /// cheaper here than as an angle. Nothing ticked disables the button rather than
    /// reporting a no-op edit. Null = canceled.</summary>
    public static Task<(bool Horizontal, bool Vertical)?> PromptSpriteFlip(Window owner)
    {
        var result = new TaskCompletionSource<(bool, bool)?>(TaskCreationOptions.RunContinuationsAsynchronously);

        var horizontal = new CheckBox { Content = "Horizontal", IsChecked = true };
        var vertical = new CheckBox { Content = "Vertical" };
        var ok = new Button { Content = "Flip", MinWidth = 90, IsDefault = true, IsEnabled = true };
        var cancel = new Button { Content = "Cancel", MinWidth = 90, IsCancel = true };
        void Refresh() => ok.IsEnabled = horizontal.IsChecked == true || vertical.IsChecked == true;
        var body = new StackPanel
        {
            Margin = new Avalonia.Thickness(20), Spacing = 10,
            Children =
            {
                new TextBlock { Text = "Direction", Foreground = ThemeBrush("ThemeMutedText") },
                horizontal, vertical,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8, Margin = new Avalonia.Thickness(0, 8, 0, 0), Children = { ok, cancel },
                },
            },
        };
        horizontal.Click += (_, _) => Refresh();
        vertical.Click += (_, _) => Refresh();

        var dialog = new Window
        {
            Background = ThemeBrush("ThemePanelBg"), Foreground = ThemeBrush("ThemeText"),
            Title = "Flip Sprite", SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false, ShowInTaskbar = false,
            Content = body,
        };
        ok.Click += (_, _) =>
        {
            result.TrySetResult((horizontal.IsChecked == true, vertical.IsChecked == true));
            dialog.Close();
        };
        cancel.Click += (_, _) => { result.TrySetResult(null); dialog.Close(); };
        dialog.Closed += (_, _) => result.TrySetResult(null);

        dialog.ShowDialog(owner);
        return result.Task;
    }

    /// <summary>One turn: an angle with a direction, where the sprite lands, and how to
    /// sample it. The canvas is not asked for: the handler fits each sprite to its own turn.
    /// Bilinear is only offered for a true-color sprite, which is what
    /// <paramref name="indexed"/> false means. Null = canceled.</summary>
    public static Task<SpriteRotateChoice?> PromptSpriteRotate(Window owner, bool indexed)
    {
        var result = new TaskCompletionSource<SpriteRotateChoice?>(TaskCreationOptions.RunContinuationsAsynchronously);

        NumericUpDown Number(double value, double min, double max, double increment, string format, double w) => new()
        {
            Value = (decimal)value, Minimum = (decimal)min, Maximum = (decimal)max,
            Increment = (decimal)increment, FormatString = format, Width = w,
        };

        var angle = Number(0, 0, 360, 1, "0.#", 90);
        var left = new RadioButton { GroupName = "RotateDir", Content = "Left" };
        var right = new RadioButton { GroupName = "RotateDir", Content = "Right", IsChecked = true };
        var pivotSprite = new RadioButton { GroupName = "RotatePivot", Content = "Sprite", IsChecked = true };
        var pivotAxis = new RadioButton { GroupName = "RotatePivot", Content = "Axis" };
        var pivotNone = new RadioButton { GroupName = "RotatePivot", Content = "None (keeps the same coordinates)" };
        // All three are always listed, the ones that cannot work for this sprite disabled with
        // the reason, as EnumCombo does for the SprMaker options.
        var samplings = new[] { SpriteRotate.Interpolation.Nearest, SpriteRotate.Interpolation.Area, SpriteRotate.Interpolation.Bilinear };
        var smooth = new ComboBox { MinWidth = 120 };
        foreach (var v in samplings)
            smooth.Items.Add(new ComboBoxItem { Content = v.ToString(), IsEnabled = !indexed || v != SpriteRotate.Interpolation.Bilinear });
        // SelectedIndex set here, not in the initializer: on an empty Items collection it is
        // ignored, which left the combo showing nothing at all.
        smooth.SelectedIndex = 0;
        if (indexed)
            ToolTip.SetTip(smooth, "Available for true-color sprites only.");

        Control Row(string label, Control content)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            Grid.SetColumn(content, 1);
            grid.Children.Add(new TextBlock
            {
                Text = label, Foreground = ThemeBrush("ThemeMutedText"),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Avalonia.Thickness(0, 0, 10, 0),
            });
            grid.Children.Add(content);
            return grid;
        }

        var ok = new Button { Content = "Rotate", MinWidth = 90, IsDefault = true };
        var cancel = new Button { Content = "Cancel", MinWidth = 90, IsCancel = true };
        var body = new StackPanel { Margin = new Avalonia.Thickness(20), Spacing = 10 };
        body.Children.Add(Row("Angle", new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 16,
            Children = { angle, left, right },
        }));
        body.Children.Add(Row("Pivot", new StackPanel { Children = { pivotSprite, pivotAxis, pivotNone } }));
        body.Children.Add(Row("Sampling", smooth));
        body.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8, Children = { ok, cancel },
        });

        var dialog = new Window
        {
            Background = ThemeBrush("ThemePanelBg"), Foreground = ThemeBrush("ThemeText"),
            Title = "Rotate Sprite", SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false, ShowInTaskbar = false,
            Content = body,
        };
        ok.Click += (_, _) =>
        {
            result.TrySetResult(new((double)(angle.Value ?? 0), right.IsChecked == true,
                pivotSprite.IsChecked == true ? SpriteRotate.Pivot.Sprite
                    : pivotAxis.IsChecked == true ? SpriteRotate.Pivot.Axis : SpriteRotate.Pivot.None,
                smooth.SelectedIndex >= 0 && smooth.SelectedIndex < samplings.Length
                    ? samplings[smooth.SelectedIndex] : SpriteRotate.Interpolation.Nearest));
            dialog.Close();
        };
        cancel.Click += (_, _) => { result.TrySetResult(null); dialog.Close(); };
        dialog.Closed += (_, _) => result.TrySetResult(null);

        dialog.ShowDialog(owner);
        return result.Task;
    }

    /// <summary>Asks which SFF version a brand new sprite file should start as — New
    /// has no existing file to infer a version from, unlike Open. Mirrors the three
    /// choices Save As offers. Returns null if canceled, aborting New.</summary>
    /// <summary>Asks for an SFF version: New (title/action default) and Save As (preselects the file's own).</summary>
    public static Task<SffVersion?> PromptSffVersion(Window owner, SffVersion? preselect = null,
        string title = "New sprite file", string prompt = "SFF version for the new file:", string action = "Create")
    {
        var result = new TaskCompletionSource<SffVersion?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var v21 = new RadioButton { Content = "SFF 2.0.1.0 (8, 24 and 32-bit) - recommended", GroupName = "SffVersion", IsChecked = true };
        var v20 = new RadioButton { Content = "SFF 2.0.0.0 (8-bit)", GroupName = "SffVersion" };
        var v1 = new RadioButton { Content = "SFF 1.0.1.0 (8-bit)", GroupName = "SffVersion" };
        if (preselect is { } pv && SffVersions.IsV1(pv)) v1.IsChecked = true;
        else if (preselect is { } pv2 && !SffVersions.SupportsPng(pv2)) v20.IsChecked = true;

        var ok = new Button { Content = action, MinWidth = 90, IsDefault = true };
        var cancel = new Button { Content = "Cancel", MinWidth = 90 };
        var body = new StackPanel { Spacing = 8, Margin = new Avalonia.Thickness(20) };
        body.Children.Add(new TextBlock { Text = prompt });
        body.Children.Add(v21);
        body.Children.Add(v20);
        body.Children.Add(v1);
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        row.Children.Add(ok);
        row.Children.Add(cancel);
        body.Children.Add(row);

        var dialog = new Window
        {
            Background = ThemeBrush("ThemePanelBg"), Foreground = ThemeBrush("ThemeText"),
            Title = title, SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false, ShowInTaskbar = false,
            Content = body,
        };
        ok.Click += (_, _) =>
        {
            var version = v1.IsChecked == true ? SffVersions.V1 : v20.IsChecked == true ? SffVersions.V2 : SffVersions.V2_1;
            result.TrySetResult(version);
            dialog.Close();
        };
        cancel.Click += (_, _) => { result.TrySetResult(null); dialog.Close(); };
        dialog.Closed += (_, _) => result.TrySetResult(null);
        dialog.ShowDialog(owner);
        return result.Task;
    }

    /// <summary>
    /// Save As across SFF versions: lists what the conversion changes and, when
    /// actCount > 0 (v1 → v2), offers to embed the character's external .act
    /// palettes. True = embed, false = save without, null = cancel the save.
    /// </summary>
    public static Task<bool?> PromptSffConversion(Window owner, string title, IReadOnlyList<string> notes,
        int actCount, bool repoints)
    {
        var result = new TaskCompletionSource<bool?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var embed = new CheckBox { Content = $"Embed {(actCount == 1 ? "it" : "them")} in the saved file" };
        var ok = new Button { Content = "Save", MinWidth = 90, IsDefault = true };
        var cancel = new Button { Content = "Cancel", MinWidth = 90, IsCancel = true };
        var body = new StackPanel { Spacing = 8, Margin = new Avalonia.Thickness(20), MaxWidth = 420 };
        TextBlock Text(string t) => new() { Text = t, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        foreach (var n in notes) body.Children.Add(Text(n));
        if (actCount > 0)
        {
            body.Children.Add(Text($"This character has {actCount} external .act palette{(actCount == 1 ? "" : "s")} that the SFF did not store."));
            body.Children.Add(embed);
            body.Children.Add(Text(actCount == 1 ? "Without embedding, it is not saved." : "Without embedding, they are not saved."));
            if (repoints)
                body.Children.Add(Text("Embedding also switches sprites that use the main palette (sprite 0,0's) to the .act at 1,1, even if its colors differ."));
        }
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        row.Children.Add(ok);
        row.Children.Add(cancel);
        body.Children.Add(row);

        var dialog = new Window
        {
            Background = ThemeBrush("ThemePanelBg"), Foreground = ThemeBrush("ThemeText"),
            Title = title, SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false, ShowInTaskbar = false,
            Content = body,
        };
        ok.Click += (_, _) => { result.TrySetResult(actCount > 0 && embed.IsChecked == true); dialog.Close(); };
        cancel.Click += (_, _) => { result.TrySetResult(null); dialog.Close(); };
        dialog.Closed += (_, _) => result.TrySetResult(null);
        dialog.ShowDialog(owner);
        return result.Task;
    }

    /// <summary>Save Like SprMaker, step 1: which tool to save like. True = SprMaker 2.01 (SFFv2), null = cancel.</summary>
    public static Task<bool?> PromptSprMakerTool(Window owner, bool v2)
    {
        var result = new TaskCompletionSource<bool?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var one = new RadioButton { Content = "SprMaker 0.97 (SFFv1)", GroupName = "SprMakerTool", IsChecked = !v2 };
        var two = new RadioButton { Content = "SprMaker 2.01 (SFFv2)", GroupName = "SprMakerTool", IsChecked = v2 };
        var next = new Button { Content = "Next", MinWidth = 90, IsDefault = true };
        var cancel = new Button { Content = "Cancel", MinWidth = 90, IsCancel = true };
        var body = new StackPanel { Spacing = 8, Margin = new Avalonia.Thickness(20) };
        body.Children.Add(new TextBlock { Text = "Save the way this tool builds the file:" });
        body.Children.Add(one);
        body.Children.Add(two);
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        row.Children.Add(next);
        row.Children.Add(cancel);
        body.Children.Add(row);

        var dialog = new Window
        {
            Background = ThemeBrush("ThemePanelBg"), Foreground = ThemeBrush("ThemeText"),
            Title = "Save Like SprMaker", SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false, ShowInTaskbar = false,
            Content = body,
        };
        next.Click += (_, _) => { result.TrySetResult(two.IsChecked == true); dialog.Close(); };
        cancel.Click += (_, _) => { result.TrySetResult(null); dialog.Close(); };
        dialog.Closed += (_, _) => result.TrySetResult(null);
        dialog.ShowDialog(owner);
        return result.Task;
    }

    /// <summary>Save Like SprMaker, step 2 for SprMaker 0.97 (SFFv1). Null = cancel.</summary>
    public static Task<SprMakerOptions?> PromptSprMakerOptions(
        Window owner, SprMakerOptions current, IReadOnlyList<string> notes)
    {
        var crop = OptionBox("Automatically crop images and adjust axes (-c)", true,
            "Trims each image's empty border; the axis moves with it.");
        var link = OptionBox("Eliminate duplicate files (-f)", true,
            "Links sprites whose pixels and palette are identical.");
        var pal = OptionBox("Eliminate duplicate successive palettes (-p)", SprMakerOptions.SupportsLinkDuplicatePalettes,
            "Reuses the previous sprite's palette when the colors match.");
        var summary = OptionBox("Summary mode (-s)", true,
            "The report file holds only the summary and errors.");
        var quiet = OptionBox("Quiet mode (-q)", true, "No report file is written.");
        void Show(SprMakerOptions o)
        {
            crop.IsChecked = o.AutoCrop;
            link.IsChecked = o.LinkDuplicateSprites;
            pal.IsChecked = o.LinkDuplicatePalettes;
            summary.IsChecked = o.SummaryMode;
            quiet.IsChecked = o.QuietMode;
        }
        Show(current);
        return OptionsDialog(owner, "Save Like SprMaker 0.97", notes,
            [crop, link, pal, summary, quiet],
            () => Show(new SprMakerOptions()),
            () => new SprMakerOptions
            {
                AutoCrop = crop.IsChecked == true, LinkDuplicateSprites = link.IsChecked == true,
                LinkDuplicatePalettes = pal.IsChecked == true,
                SummaryMode = summary.IsChecked == true, QuietMode = quiet.IsChecked == true,
            });
    }

    /// <summary>Save Like SprMaker, step 2 for SprMaker 2.01 (SFFv2). Null = cancel.</summary>
    public static Task<SprMaker2Options?> PromptSprMaker2Options(
        Window owner, SprMaker2Options current, IReadOnlyList<string> notes)
    {
        var crop = OptionBox("Automatically crop images and adjust axes (-c)", true,
            "Trims each image's empty border; the axis moves with it.");
        var link = OptionBox("Link duplicate files (-f)", true,
            "Links sprites whose pixels are identical.");
        var detect = OptionBox("Link duplicate successive palettes (-p)", SprMaker2Options.SupportsPaletteDedupe,
            "Links palettes whose colors are identical.");
        var verbose = OptionBox("Verbose mode (-v)", true,
            "The report file also lists every linked, cropped and changed palette line.");
        var veryVerbose = OptionBox("Very verbose mode (-V)", SprMaker2Options.SupportsVeryVerbose,
            "The report file also lists every source sprite's palette, not just the changes.");
        var quiet = OptionBox("Quiet mode (-q, -Q)", true,
            "No report file is written (warnings and errors still reach the status bar).");
        var c5 = EnumCombo(Enum.GetValues<SprMaker2Compress5>(), SprMaker2Options.Supports);
        var c8 = EnumCombo(Enum.GetValues<SprMaker2Compress8>(), SprMaker2Options.Supports);
        var c24 = EnumCombo(Enum.GetValues<SprMaker2Compress24>(), SprMaker2Options.Supports);
        var decompress = OptionBox("Decompress on load", SprMaker2Options.SupportsDecompressOnLoad,
            "Marks sprites to be decompressed once when the file loads.");
        var discard = OptionBox("Discard duplicate palettes", SprMaker2Options.SupportsPaletteDedupe,
            "Drops duplicate palettes instead of linking them.");
        void Show(SprMaker2Options o)
        {
            crop.IsChecked = o.AutoCrop;
            link.IsChecked = o.DetectDuplicateSprites;
            detect.IsChecked = o.DetectDuplicatePalettes;
            verbose.IsChecked = o.VerboseMode;
            veryVerbose.IsChecked = o.VeryVerboseMode;
            quiet.IsChecked = o.QuietMode;
            c5.SelectedIndex = (int)o.Compress5;
            c8.SelectedIndex = (int)o.Compress8;
            c24.SelectedIndex = (int)o.Compress24;
            decompress.IsChecked = o.DecompressOnLoad;
            discard.IsChecked = o.DiscardDuplicatePalettes;
        }
        Show(current);
        Control Row(string label, ComboBox box) => new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children = { new TextBlock { Text = label, Width = 110, VerticalAlignment = VerticalAlignment.Center }, box },
        };
        return OptionsDialog(owner, "Save Like SprMaker 2.01", notes,
            [crop, link, detect, verbose, veryVerbose, quiet,
             Row("5-bit sprites", c5), Row("8-bit sprites", c8), Row("True color", c24), decompress, discard],
            () => Show(new SprMaker2Options()),
            () => new SprMaker2Options
            {
                AutoCrop = crop.IsChecked == true, DetectDuplicateSprites = link.IsChecked == true,
                DetectDuplicatePalettes = detect.IsChecked == true,
                DiscardDuplicatePalettes = discard.IsChecked == true,
                DecompressOnLoad = decompress.IsChecked == true,
                VerboseMode = verbose.IsChecked == true, VeryVerboseMode = veryVerbose.IsChecked == true,
                QuietMode = quiet.IsChecked == true,
                Compress5 = (SprMaker2Compress5)c5.SelectedIndex, Compress8 = (SprMaker2Compress8)c8.SelectedIndex,
                Compress24 = (SprMaker2Compress24)c24.SelectedIndex,
            });
    }

    // An option the writer can't do yet is shown disabled, marked as such.
    private static CheckBox OptionBox(string label, bool supported, string tip)
    {
        var box = new CheckBox { Content = supported ? label : $"{label} (not supported yet)", IsEnabled = supported };
        ToolTip.SetTip(box, tip);
        return box;
    }

    private static ComboBox EnumCombo<T>(T[] values, Func<T, bool> supported) where T : struct, Enum
    {
        var box = new ComboBox { MinWidth = 180 };
        foreach (var v in values)
        {
            bool ok = supported(v);
            var name = v.ToString();
            if (name != "None") name = name.ToUpperInvariant(); // LZ5, RLE8, PNG32
            box.Items.Add(new ComboBoxItem { Content = ok ? name : $"{name} (not supported yet)", IsEnabled = ok });
        }
        return box;
    }

    private static Task<T?> OptionsDialog<T>(Window owner, string title, IReadOnlyList<string> notes,
        Control[] rows, Action reset, Func<T> read) where T : class
    {
        var result = new TaskCompletionSource<T?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resetButton = new Button { Content = "Reset to Defaults" };
        ToolTip.SetTip(resetButton, "Restores the tool's own defaults.");
        var ok = new Button { Content = "Save", MinWidth = 90, IsDefault = true };
        var cancel = new Button { Content = "Cancel", MinWidth = 90, IsCancel = true };
        var body = new StackPanel { Spacing = 8, Margin = new Avalonia.Thickness(20), MaxWidth = 460 };
        foreach (var n in notes)
            body.Children.Add(new TextBlock { Text = n, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        foreach (var r in rows) body.Children.Add(r);
        var buttons = new DockPanel { LastChildFill = false, Margin = new Avalonia.Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(resetButton, Dock.Left);
        buttons.Children.Add(resetButton);
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        right.Children.Add(ok);
        right.Children.Add(cancel);
        DockPanel.SetDock(right, Dock.Right);
        buttons.Children.Add(right);
        body.Children.Add(buttons);

        var dialog = new Window
        {
            Background = ThemeBrush("ThemePanelBg"), Foreground = ThemeBrush("ThemeText"),
            Title = title, SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false, ShowInTaskbar = false,
            Content = body,
        };
        resetButton.Click += (_, _) => reset();
        ok.Click += (_, _) => { result.TrySetResult(read()); dialog.Close(); };
        cancel.Click += (_, _) => { result.TrySetResult(null); dialog.Close(); };
        dialog.Closed += (_, _) => result.TrySetResult(null);
        dialog.ShowDialog(owner);
        return result.Task;
    }

    public static Task<SffV1PaletteChoice?> PromptSffV1PaletteChoice(Window owner,
        bool previousAvailable, bool usePrevious)
    {
        var result = new TaskCompletionSource<SffV1PaletteChoice?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var own = new RadioButton
        {
            GroupName = "SffV1Palette", Content = "Own palette", IsChecked = !usePrevious,
        };
        var previous = new RadioButton
        {
            GroupName = "SffV1Palette", Content = "Use previous sprite's palette",
            IsEnabled = previousAvailable, IsChecked = usePrevious,
        };
        var body = new StackPanel { Spacing = 10, Margin = new Avalonia.Thickness(20), MaxWidth = 420 };
        body.Children.Add(new TextBlock
        {
            Text = "SFFv1 palettes are stored in sprite images. Own makes this sprite carry a copy of its current palette; later sprites that reuse it follow along.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });
        body.Children.Add(own);
        body.Children.Add(previous);
        var ok = new Button { Content = "Apply", MinWidth = 90, IsDefault = true };
        var cancel = new Button { Content = "Cancel", MinWidth = 90 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        body.Children.Add(buttons);

        var dialog = new Window
        {
            Background = ThemeBrush("ThemePanelBg"), Foreground = ThemeBrush("ThemeText"),
            Title = "SFFv1 Palette", SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false, ShowInTaskbar = false,
            Content = body,
        };
        ok.Click += (_, _) =>
        {
            if (own.IsChecked != true && previous.IsChecked != true) return;
            result.TrySetResult(previous.IsChecked == true
                ? SffV1PaletteChoice.Previous
                : SffV1PaletteChoice.Own);
            dialog.Close();
        };
        cancel.Click += (_, _) => { result.TrySetResult(null); dialog.Close(); };
        dialog.Closed += (_, _) => result.TrySetResult(null);
        dialog.ShowDialog(owner);
        return result.Task;
    }

    public static Task<int?> PromptSffV2PaletteChoice(Window owner, IReadOnlyList<string> choices, int selectedIndex)
    {
        var result = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var choice = new ComboBox { ItemsSource = choices, SelectedIndex = selectedIndex, MinWidth = 280 };
        var ok = new Button { Content = "Use Palette", MinWidth = 90, IsDefault = true };
        var cancel = new Button { Content = "Cancel", MinWidth = 90 };
        ok.IsEnabled = choice.SelectedIndex >= 0;
        choice.SelectionChanged += (_, _) => ok.IsEnabled = choice.SelectedIndex >= 0;
        var body = new StackPanel { Spacing = 12, Margin = new Avalonia.Thickness(20) };
        body.Children.Add(new TextBlock { Text = "Choose the palette entry this sprite uses:" });
        body.Children.Add(choice);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        body.Children.Add(buttons);

        var dialog = new Window
        {
            Background = ThemeBrush("ThemePanelBg"), Foreground = ThemeBrush("ThemeText"),
            Title = "SFFv2 Palette", SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false, ShowInTaskbar = false,
            Content = body,
        };
        ok.Click += (_, _) =>
        {
            if (choice.SelectedIndex < 0) return;
            result.TrySetResult(choice.SelectedIndex);
            dialog.Close();
        };
        cancel.Click += (_, _) => { result.TrySetResult(null); dialog.Close(); };
        dialog.Closed += (_, _) => result.TrySetResult(null);
        dialog.ShowDialog(owner);
        return result.Task;
    }

    public static Task<SpriteShareDialogAction?> PromptSpriteSharing(Window owner)
    {
        var result = new TaskCompletionSource<SpriteShareDialogAction?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var unlink = new Button { Content = "Unlink", MinWidth = 90 };
        var link = new Button { Content = "Link", MinWidth = 90, IsDefault = true };
        var cancel = new Button { Content = "Cancel", MinWidth = 90 };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8,
        };
        buttons.Children.Add(unlink);
        buttons.Children.Add(link);
        buttons.Children.Add(cancel);

        var body = new StackPanel { Spacing = 12, Margin = new Avalonia.Thickness(20), MaxWidth = 520 };
        body.Children.Add(new TextBlock { Text = "Choose what to do with this sprite." });
        body.Children.Add(buttons);
        var dialog = new Window
        {
            Background = ThemeBrush("ThemePanelBg"), Foreground = ThemeBrush("ThemeText"),
            Title = "Sprite Linking", SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false, ShowInTaskbar = false,
            Content = body,
        };
        unlink.Click += (_, _) => { result.TrySetResult(SpriteShareDialogAction.Unlink); dialog.Close(); };
        link.Click += (_, _) => { result.TrySetResult(SpriteShareDialogAction.Link); dialog.Close(); };
        cancel.Click += (_, _) => { result.TrySetResult(null); dialog.Close(); };
        dialog.Closed += (_, _) => result.TrySetResult(null);
        dialog.ShowDialog(owner);
        return result.Task;
    }

    public static Task<SpriteTargetDialogResult?> PromptSpriteLinkTarget(Window owner,
        string initialGroup, string initialImage, int maximum, Func<int, int, string?> validateTarget)
    {
        var result = new TaskCompletionSource<SpriteTargetDialogResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var group = new TextBox { Text = initialGroup, MinWidth = 90 };
        var image = new TextBox { Text = initialImage, MinWidth = 90 };
        var chooseTarget = new Button { Content = "Choose Target", MinWidth = 100, IsDefault = true };
        var pickFromList = new Button { Content = "Pick from List", MinWidth = 100 };
        var cancel = new Button { Content = "Cancel", MinWidth = 90 };
        var error = new TextBlock { Foreground = ThemeBrush("ThemeMutedText"), TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var body = new StackPanel { Spacing = 12, Margin = new Avalonia.Thickness(20), MaxWidth = 440 };
        body.Children.Add(new TextBlock
        {
            Text = "Choose a target by Group and Image, or pick one from the list.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });
        var fields = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,8,*,8,Auto"),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
        };
        fields.Children.Add(new TextBlock { Text = "Group" });
        var imageLabel = new TextBlock { Text = "Image" };
        Grid.SetColumn(imageLabel, 2);
        fields.Children.Add(imageLabel);
        Grid.SetRow(group, 1);
        fields.Children.Add(group);
        Grid.SetColumn(image, 2);
        Grid.SetRow(image, 1);
        fields.Children.Add(image);
        Grid.SetColumn(chooseTarget, 4);
        Grid.SetRow(chooseTarget, 1);
        fields.Children.Add(chooseTarget);
        body.Children.Add(fields);
        body.Children.Add(error);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        buttons.Children.Add(pickFromList);
        buttons.Children.Add(cancel);
        body.Children.Add(buttons);

        var dialog = new Window
        {
            Background = ThemeBrush("ThemePanelBg"), Foreground = ThemeBrush("ThemeText"),
            Title = "Sprite Target", SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false, ShowInTaskbar = false,
            Content = body,
        };

        void ChooseTarget()
        {
            if (!int.TryParse(group.Text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int groupValue)
                || !int.TryParse(image.Text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int imageValue))
            {
                error.Text = "Enter a whole number for Group and Image.";
                return;
            }
            if (groupValue < 0 || imageValue < 0 || groupValue > maximum || imageValue > maximum)
            {
                error.Text = maximum == int.MaxValue
                    ? "Group and Image must be whole numbers of 0 or more."
                    : "Group and Image must be whole numbers from 0 to 65535.";
                return;
            }
            if (validateTarget(groupValue, imageValue) is { } validationError)
            {
                error.Text = validationError;
                return;
            }
            result.TrySetResult(new(SpriteTargetDialogAction.UseGroupImage, groupValue, imageValue));
            dialog.Close();
        }

        chooseTarget.Click += (_, _) => ChooseTarget();
        pickFromList.Click += (_, _) => { result.TrySetResult(new(SpriteTargetDialogAction.PickFromList)); dialog.Close(); };
        cancel.Click += (_, _) => { result.TrySetResult(null); dialog.Close(); };
        dialog.Closed += (_, _) => result.TrySetResult(null);
        dialog.ShowDialog(owner);
        return result.Task;
    }

    public static Task<PaletteShareDialogAction?> PromptPaletteSharing(Window owner)
    {
        var result = new TaskCompletionSource<PaletteShareDialogAction?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var unlink = new Button { Content = "Unlink", MinWidth = 90 };
        var link = new Button { Content = "Link", MinWidth = 90, IsDefault = true };
        var merge = new Button { Content = "Merge", MinWidth = 90 };
        var cancel = new Button { Content = "Cancel", MinWidth = 90 };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8,
        };
        buttons.Children.Add(unlink);
        buttons.Children.Add(link);
        buttons.Children.Add(merge);
        buttons.Children.Add(cancel);

        var body = new StackPanel { Spacing = 12, Margin = new Avalonia.Thickness(20), MaxWidth = 520 };
        body.Children.Add(new TextBlock { Text = "Choose what to do with this palette." });
        body.Children.Add(buttons);
        var dialog = new Window
        {
            Background = ThemeBrush("ThemePanelBg"), Foreground = ThemeBrush("ThemeText"),
            Title = "Palette Linking and Merging", SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false, ShowInTaskbar = false,
            Content = body,
        };

        unlink.Click += (_, _) => { result.TrySetResult(PaletteShareDialogAction.Unlink); dialog.Close(); };
        link.Click += (_, _) => { result.TrySetResult(PaletteShareDialogAction.Link); dialog.Close(); };
        merge.Click += (_, _) => { result.TrySetResult(PaletteShareDialogAction.Merge); dialog.Close(); };
        cancel.Click += (_, _) => { result.TrySetResult(null); dialog.Close(); };
        dialog.Closed += (_, _) => result.TrySetResult(null);
        dialog.ShowDialog(owner);
        return result.Task;
    }

    public static Task<PaletteTargetDialogResult?> PromptPaletteLinkTarget(Window owner,
        string initialGroup, string initialIndex, int maximum, Func<int, int, string?> validateTarget)
    {
        var result = new TaskCompletionSource<PaletteTargetDialogResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var group = new TextBox { Text = initialGroup, MinWidth = 90 };
        var index = new TextBox { Text = initialIndex, MinWidth = 90 };
        var usePair = new Button { Content = "Choose Target", MinWidth = 100, IsDefault = true };
        var pickFromList = new Button { Content = "Pick from List", MinWidth = 100 };
        var cancel = new Button { Content = "Cancel", MinWidth = 90 };
        var error = new TextBlock { Foreground = ThemeBrush("ThemeMutedText"), TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var body = new StackPanel { Spacing = 12, Margin = new Avalonia.Thickness(20), MaxWidth = 440 };
        body.Children.Add(new TextBlock
        {
            Text = "Choose a target by Group and Index, or pick one from the list.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });
        var fields = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,8,*,8,Auto"),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
        };
        fields.Children.Add(new TextBlock { Text = "Group" });
        var indexLabel = new TextBlock { Text = "Index" };
        Grid.SetColumn(indexLabel, 2);
        fields.Children.Add(indexLabel);
        Grid.SetRow(group, 1);
        fields.Children.Add(group);
        Grid.SetColumn(index, 2);
        Grid.SetRow(index, 1);
        fields.Children.Add(index);
        Grid.SetColumn(usePair, 4);
        Grid.SetRow(usePair, 1);
        fields.Children.Add(usePair);
        body.Children.Add(fields);
        body.Children.Add(error);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        buttons.Children.Add(pickFromList);
        buttons.Children.Add(cancel);
        body.Children.Add(buttons);

        var dialog = new Window
        {
            Background = ThemeBrush("ThemePanelBg"), Foreground = ThemeBrush("ThemeText"),
            Title = "Palette Target", SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false, ShowInTaskbar = false,
            Content = body,
        };

        void ChooseTarget()
        {
            if (!int.TryParse(group.Text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int groupValue)
                || !int.TryParse(index.Text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int indexValue))
            {
                error.Text = "Enter a whole number for Group and Index.";
                return;
            }
            if (groupValue < 0 || indexValue < 0 || groupValue > maximum || indexValue > maximum)
            {
                error.Text = maximum == int.MaxValue
                    ? "Group and Index must be whole numbers of 0 or more."
                    : "Group and Index must be whole numbers from 0 to 65535.";
                return;
            }
            if (validateTarget(groupValue, indexValue) is { } validationError)
            {
                error.Text = validationError;
                return;
            }
            result.TrySetResult(new(PaletteTargetDialogAction.UseGroupIndex, groupValue, indexValue));
            dialog.Close();
        }

        usePair.Click += (_, _) => ChooseTarget();
        pickFromList.Click += (_, _) => { result.TrySetResult(new(PaletteTargetDialogAction.PickFromList)); dialog.Close(); };
        cancel.Click += (_, _) => { result.TrySetResult(null); dialog.Close(); };
        dialog.Closed += (_, _) => result.TrySetResult(null);
        dialog.ShowDialog(owner);
        return result.Task;
    }

    /// <summary>Asks for the Group and Index a new palette should get.</summary>
    public static Task<(int Group, int Index)?> PromptNewPaletteKey(Window owner,
        int initialGroup, int initialIndex, int maximum)
    {
        var result = new TaskCompletionSource<(int Group, int Index)?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var group = new NumericUpDown { Minimum = 0, Maximum = maximum, Increment = 1, FormatString = "0", Value = initialGroup, Width = 100 };
        var index = new NumericUpDown { Minimum = 0, Maximum = maximum, Increment = 1, FormatString = "0", Value = initialIndex, Width = 100 };
        var ok = new Button { Content = "Continue", MinWidth = 90, IsDefault = true };
        var cancel = new Button { Content = "Cancel", MinWidth = 90 };
        var body = new StackPanel { Spacing = 12, Margin = new Avalonia.Thickness(20), MaxWidth = 440 };
        body.Children.Add(new TextBlock
        {
            Text = "Choose the Group and Index for the new palette.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });
        body.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 14,
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center,
                    Children = { new TextBlock { Text = "Group", VerticalAlignment = VerticalAlignment.Center }, group },
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center,
                    Children = { new TextBlock { Text = "Index", VerticalAlignment = VerticalAlignment.Center }, index },
                },
            },
        });
        body.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8, Margin = new Avalonia.Thickness(0, 8, 0, 0), Children = { ok, cancel },
        });

        var dialog = new Window
        {
            Background = ThemeBrush("ThemePanelBg"), Foreground = ThemeBrush("ThemeText"),
            Title = "New Palette Key", SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false, ShowInTaskbar = false,
            Content = body,
        };
        ok.Click += (_, _) =>
        {
            result.TrySetResult(((int)(group.Value ?? initialGroup), (int)(index.Value ?? initialIndex)));
            dialog.Close();
        };
        cancel.Click += (_, _) => { result.TrySetResult(null); dialog.Close(); };
        dialog.Closed += (_, _) => result.TrySetResult(null);
        dialog.ShowDialog(owner);
        return result.Task;
    }

    public static Task<string?> PromptText(Window owner, string title, string label, string initial = "")
    {
        var result = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var box = new TextBox { Text = initial, MinWidth = 260 };

        var dialog = new Window
        {
            Background = ThemeBrush("ThemePanelBg"),
            Foreground = ThemeBrush("ThemeText"),
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false,
        };

        var ok = new Button { Content = "OK", MinWidth = 90, IsDefault = true };
        var cancel = new Button { Content = "Cancel", MinWidth = 90 };
        ok.Click += (_, _) => { result.TrySetResult(box.Text); dialog.Close(); };
        cancel.Click += (_, _) => { result.TrySetResult(null); dialog.Close(); };
        box.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { result.TrySetResult(box.Text); dialog.Close(); } };

        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = label },
                box,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8, Children = { ok, cancel },
                },
            },
        };
        dialog.Closed += (_, _) => result.TrySetResult(null);
        dialog.Opened += (_, _) => box.Focus();

        dialog.ShowDialog(owner);
        return result.Task;
    }

    /// <summary>
    /// PromptText with a Preview button (left of OK/Cancel) and an error line. validate
    /// returns an error for unusable text, or null; OK with an error keeps the dialog
    /// open. preview returns an error to show, or null; Preview stays clickable while
    /// one runs (the caller restarts it), and only the latest run's message is shown.
    /// The error line clears on the next Preview or OK. onClosed runs however the dialog closes (e.g. to stop a
    /// preview that's still playing). initial null hides the text box (the text is then "");
    /// extra, if given, goes under the box (e.g. radio buttons). Returns the accepted text,
    /// or null if canceled.
    /// </summary>
    public static Task<string?> PromptTextWithPreview(Window owner, string title, string label, string? initial,
        Func<string, string?> validate, Func<string, Task<string?>> preview, Action onClosed, Control? extra = null,
        Func<string, string?>? warn = null)
    {
        var result = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var box = new TextBox { Text = initial ?? "", MinWidth = 260, IsVisible = initial is not null };
        var error = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.Parse("#ff9090")), TextWrapping = TextWrapping.Wrap,
            MaxWidth = 420, IsVisible = false,
        };

        var dialog = new Window
        {
            Background = ThemeBrush("ThemePanelBg"),
            Foreground = ThemeBrush("ThemeText"),
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false,
        };

        // Amber, not the error red: a warning never blocks, it only makes OK wait
        // one press so it can be read (see warnedText).
        var warning = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.Parse("#e0a040")), TextWrapping = TextWrapping.Wrap,
            MaxWidth = 420, IsVisible = false,
        };
        string? warnedText = null; // the value the visible warning is about

        void ShowError(string? message)
        {
            error.Text = message ?? "";
            error.IsVisible = message is not null;
            if (message is null) return;
            warning.IsVisible = false; // a stale warning next to an error would only confuse
            warnedText = null;
        }

        // Returns true when a warning for text is newly shown (i.e. not yet seen).
        bool ShowWarning(string text)
        {
            string? message = null;
            try { message = warn?.Invoke(text); }
            catch (Exception ex) { CrashLog.Write("PromptTextWithPreview.warn", ex); }
            warning.Text = message ?? "";
            warning.IsVisible = message is not null;
            bool fresh = message is not null && warnedText != text.Trim();
            warnedText = message is null ? null : text.Trim();
            return fresh;
        }

        void Accept()
        {
            string text = box.Text ?? "";
            ShowError(validate(text));
            if (error.IsVisible) return;
            if (ShowWarning(text)) return; // first OK on a warned value only shows the warning
            result.TrySetResult(text);
            dialog.Close();
        }

        var previewButton = new Button { Content = "Preview", MinWidth = 90 };
        var ok = new Button { Content = "OK", MinWidth = 90, IsDefault = true };
        var cancel = new Button { Content = "Cancel", MinWidth = 90 };
        int previewRun = 0;
        previewButton.Click += async (_, _) =>
        {
            int run = ++previewRun;
            ShowError(null);
            string? message;
            try
            {
                message = await preview(box.Text ?? "");
            }
            catch (Exception ex)
            {
                CrashLog.Write("PromptTextWithPreview", ex);
                message = $"Couldn't preview: {ex.Message}";
            }
            if (run != previewRun) return; // an older, restarted run doesn't overwrite a newer one
            ShowError(message);
            if (message is null) ShowWarning(box.Text ?? "");
        };
        ok.Click += (_, _) => Accept();
        cancel.Click += (_, _) => { result.TrySetResult(null); dialog.Close(); };
        box.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) Accept(); };

        var buttons = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        Grid.SetColumn(previewButton, 0);
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { ok, cancel } };
        Grid.SetColumn(right, 2);
        buttons.Children.Add(previewButton);
        buttons.Children.Add(right);

        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 12,
            Children = { new TextBlock { Text = label, MaxWidth = 420, TextWrapping = TextWrapping.Wrap }, box },
        };
        var body = (StackPanel)dialog.Content;
        if (extra is not null) body.Children.Add(extra);
        body.Children.Add(warning);
        body.Children.Add(error);
        body.Children.Add(buttons);
        dialog.Closed += (_, _) =>
        {
            result.TrySetResult(null);
            try { onClosed(); }
            catch (Exception ex) { CrashLog.Write("PromptTextWithPreview.Closed", ex); }
        };
        dialog.Opened += (_, _) => { if (box.IsVisible) box.Focus(); else ok.Focus(); };

        dialog.ShowDialog(owner);
        return result.Task;
    }

    /// <summary>Asks how close a sprite has to be before Find Similar Sprites counts it. The levels
    /// are named rather than a percentage, because what the number means (a share of the two
    /// pictures' combined area) is not something to pick by eye, while the names are.
    /// Returns null if canceled.</summary>
    public static Task<SimilarSpriteChoices?> PromptSimilarSprites(
        Window owner, IReadOnlyList<SimilarityChoice> levels, int selected,
        bool initialRequirePalette, string spriteKey)
    {
        var result = new TaskCompletionSource<SimilarSpriteChoices?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var buttons = new List<RadioButton>();
        int chosen = Math.Clamp(selected, 0, levels.Count - 1);
        foreach (var level in levels)
        {
            // The name alone ("Similar") doesn't say what it catches, so each option carries
            // its own line about that rather than hiding it in a tooltip.
            var button = new RadioButton
            {
                GroupName = "Similarity",
                IsChecked = buttons.Count == chosen,
                Content = new StackPanel
                {
                    Spacing = 2, Children =
                    {
                        new TextBlock { Text = level.Label, FontWeight = FontWeight.SemiBold },
                        new TextBlock { Text = level.Detail, Foreground = ThemeBrush("ThemeMutedText") },
                    },
                },
            };
            buttons.Add(button);
        }
        var palette = new CheckBox { Content = "Require the same palette", IsChecked = initialRequirePalette };
        ToolTip.SetTip(palette, "Leaves out sprites whose colors come from a different palette.");

        var ok = new Button { Content = "Find", MinWidth = 90, IsDefault = true };
        var cancel = new Button { Content = "Cancel", MinWidth = 90 };

        var dialog = new Window
        {
            Background = ThemeBrush("ThemePanelBg"),
            Foreground = ThemeBrush("ThemeText"),
            Title = "Find Similar Sprites",
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false,
        };

        void Accept()
        {
            int at = buttons.FindIndex(b => b.IsChecked == true);
            if (at < 0) return;
            result.TrySetResult(new SimilarSpriteChoices(levels[at], palette.IsChecked == true));
            dialog.Close();
        }
        ok.Click += (_, _) => Accept();
        cancel.Click += (_, _) => { result.TrySetResult(null); dialog.Close(); };

        var options = new StackPanel { Spacing = 8 };
        foreach (var b in buttons) options.Children.Add(b);
        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 12,
            MaxWidth = 420,
            Children =
            {
                new TextBlock { Text = $"How much of sprite {spriteKey} should match?" },
                options,
                palette,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8, Children = { ok, cancel },
                },
            },
        };
        dialog.Closed += (_, _) => result.TrySetResult(null);
        dialog.Opened += (_, _) => buttons[chosen].Focus();

        dialog.ShowDialog(owner);
        return result.Task;
    }

    /// <summary>A multi-line text editor, for editing an action as raw .air text. Returns null if canceled.</summary>
    /// <summary>
    /// A long report beside the owner (not modal): resizable, wrapped, scrolling vertically, with Close.
    /// A line with a Jump is underlined with a hand cursor; one click runs it. Each action becomes a button
    /// before Close, showing its tip; clicking it runs the action and closes the report.
    /// </summary>
    public static Window ShowReport(Window owner, string title, IReadOnlyList<(string Text, Action? Jump)> lines,
        IReadOnlyList<(string Label, string Tip, Action Click)>? actions = null)
    {
        var mono = new FontFamily("Consolas,Menlo,monospace");
        var panel = new StackPanel { Margin = new Avalonia.Thickness(4) };
        foreach (var (text, jump) in lines)
        {
            var line = new TextBlock
            {
                Text = text.Length == 0 ? " " : text, FontFamily = mono, Foreground = ThemeBrush("ThemeText"),
                TextWrapping = TextWrapping.Wrap,
            };
            if (jump is not null)
            {
                line.TextDecorations = TextDecorations.Underline;
                line.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
                line.Background = Brushes.Transparent; // the whole line is hit-testable, not just the glyphs
                line.Tapped += (_, _) =>
                {
                    try { jump(); }
                    catch (Exception ex) { CrashLog.Write("ShowReport jump", ex); }
                };
            }
            panel.Children.Add(line);
        }
        var scroller = new ScrollViewer
        {
            Content = panel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, // lines wrap to the window width
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = ThemeBrush("ThemeListBg"),
        };

        var dialog = new Window
        {
            Background = ThemeBrush("ThemePanelBg"),
            Foreground = ThemeBrush("ThemeText"),
            Title = title,
            Width = 640,
            Height = 520,
            MinWidth = 320,
            MinHeight = 200,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = true,
            ShowInTaskbar = false,
        };

        var close = new Button { Content = "Close", MinWidth = 90, IsCancel = true };
        close.Click += (_, _) => dialog.Close();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8, Margin = new Avalonia.Thickness(0, 12, 0, 0),
        };
        foreach (var (label, tip, click) in actions ?? Array.Empty<(string Label, string Tip, Action Click)>())
        {
            var b = new Button { Content = label, MinWidth = 90 };
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) => { click(); dialog.Close(); };
            buttons.Children.Add(b);
        }
        buttons.Children.Add(close);
        DockPanel.SetDock(buttons, Dock.Bottom);
        dialog.Content = new DockPanel { Margin = new Avalonia.Thickness(20), Children = { buttons, scroller } };
        dialog.Show(owner); // stays open beside the editor, so the jumps can be followed one after another
        return dialog;
    }

    /// <summary>
    /// Asks how to bring images in as sprites: which palette they should use,
    /// how their indexes map onto it, and — when adding rather than replacing
    /// — the group and first number (one more per image). PreviousPalette is
    /// what "the previous sprite's palette" refers to; null disables that
    /// option. ImagesIndexed enables "keep the image's indexes", which only
    /// means something for indexed images. paletteGroup/paletteIndex seed the
    /// Group/Index a genuinely new "own palette" gets, defaulting to the
    /// sprite's own group and the next free palette number in it — only shown
    /// while Own is selected. Defaults come from initial, which the caller
    /// derives from the images rather than the previous import. Returns null
    /// if canceled. framesGif notes that the images are a GIF's frames, which
    /// decode to color and so have no indexes to keep. matches adds match
    /// counts to the palette labels; preview shows the first image as it would import.
    /// </summary>
    public static Task<ImportChoices?> PromptSpriteImport(
        Window owner, string title, int imageCount, bool trueColorAvailable,
        IReadOnlyList<Palette> palettes, Palette? previousPalette, Palette? specificInitial,
        bool askPlacement, int group, int number, int paletteGroup, int paletteIndex,
        bool imagesIndexed, ImportChoices initial, IReadOnlyList<string>? paletteLabels = null,
        bool framesGif = false, ImportPaletteMatches? matches = null, ImportPreview? preview = null)
    {
        var result = new TaskCompletionSource<ImportChoices?>(TaskCreationOptions.RunContinuationsAsynchronously);

        var own = new RadioButton
        {
            GroupName = "ImportPalette", Content = "The image's own palette",
        };
        ToolTip.SetTip(own, "Keeps the image's own palette.");
        var previous = new RadioButton
        {
            GroupName = "ImportPalette", Content = "The previous sprite's palette", IsEnabled = previousPalette is not null,
        };
        var specific = new RadioButton
        {
            GroupName = "ImportPalette", Content = "A specific palette:", IsEnabled = palettes.Count > 0,
        };
        var baseLabels = paletteLabels ?? palettes.Select((p, i) => $"{i + 1}. {p.Describe()}").ToList();
        var paletteBox = new ComboBox { IsEnabled = palettes.Count > 0, MinWidth = 180 };
        int specificAt = -1;
        for (int i = 0; i < palettes.Count && specificAt < 0; i++)
            if (ReferenceEquals(palettes[i], specificInitial)) specificAt = i;

        var keep = new RadioButton
        {
            GroupName = "ImportIndex", Content = "Keep the image's indexes", IsEnabled = imagesIndexed,
        };
        ToolTip.SetTip(keep, imagesIndexed
            ? "Keeps pixel indexes, using the chosen palette."
            : framesGif ? "A GIF's frames have no palette indexes to keep."
            : "These images have no palette indexes.");
        var nearest = new RadioButton
        {
            GroupName = "ImportIndex", Content = "Match the nearest color",
        };
        ToolTip.SetTip(nearest, "Uses the closest color in the chosen palette.");
        var keepMask = new CheckBox
        {
            Content = "Keep mask", IsChecked = initial.KeepMask, Margin = new Avalonia.Thickness(20, 0, 0, 0),
        };
        ToolTip.SetTip(keepMask, "Pixels at the image's index 0 stay transparent.");

        var bitDepth = new ComboBox { MinWidth = 220 };
        bitDepth.Items.Add(new ComboBoxItem { Content = "8-bit color" });
        bitDepth.Items.Add(new ComboBoxItem
        {
            Content = trueColorAvailable ? "32-bit color" : "32-bit color (needs SFF 2.0.1.0)",
            IsEnabled = trueColorAvailable,
        });
        bitDepth.SelectedIndex = initial.BitDepth == BitDepthChoice.ThirtyTwoBit && trueColorAvailable ? 1 : 0;
        ToolTip.SetTip(bitDepth, "Chooses indexed or 32-bit color storage.");

        var groupBox = new NumericUpDown { Minimum = 0, Maximum = 65535, Increment = 1, FormatString = "0", Value = group, Width = 100 };
        var numberBox = new NumericUpDown { Minimum = 0, Maximum = 65535, Increment = 1, FormatString = "0", Value = number, Width = 100 };
        var palGroupBox = new NumericUpDown { Minimum = 0, Maximum = 65535, Increment = 1, FormatString = "0", Value = initial.PaletteGroup != 0 || initial.PaletteIndex != 0 ? initial.PaletteGroup : paletteGroup, Width = 100 };
        var palIndexBox = new NumericUpDown { Minimum = 0, Maximum = 65535, Increment = 1, FormatString = "0", Value = initial.PaletteGroup != 0 || initial.PaletteIndex != 0 ? initial.PaletteIndex : paletteIndex, Width = 100 };

        (initial.PaletteChoice == ImportPaletteChoice.Previous && previousPalette is not null ? previous
            : initial.PaletteChoice == ImportPaletteChoice.Specific && palettes.Count > 0 ? specific
            : own).IsChecked = true;
        (initial.IndexChoice == ImportIndexChoice.Keep && imagesIndexed ? keep : nearest).IsChecked = true;

        // Labels count matches for the index mode in use, so they follow the Indexes radios.
        string Suffix(int i, bool keepMode)
        {
            if (matches is null || i >= matches.Keep.Length) return "";
            int m = keepMode ? matches.Keep[i] : matches.Nearest[i];
            return m == 0 ? "" : matches.Total == 1 ? " — match"
                : m == matches.Total ? $" — matches all {m}" : $" — matches {m} of {matches.Total}";
        }
        void Relabel()
        {
            int at = paletteBox.SelectedIndex;
            bool keepMode = keep.IsChecked == true;
            paletteBox.ItemsSource = baseLabels.Select((label, i) => label + Suffix(i, keepMode)).ToList();
            paletteBox.SelectedIndex = at;
        }
        paletteBox.SelectedIndex = -1;
        Relabel();
        paletteBox.SelectedIndex = Math.Max(0, specificAt);
        keep.IsCheckedChanged += (_, _) => Relabel();
        void UpdateEnabled()
        {
            bool tc = bitDepth.SelectedIndex == 1 && trueColorAvailable;
            bool ownSelected = own.IsChecked == true;
            // With the image's own palette there is only one possible mapping
            // (matching against itself is exact), so both index options gray out.
            own.IsEnabled = !tc;
            previous.IsEnabled = !tc && previousPalette is not null;
            specific.IsEnabled = !tc && palettes.Count > 0;
            paletteBox.IsEnabled = !tc && specific.IsChecked == true;
            keep.IsEnabled = !tc && !ownSelected && imagesIndexed;
            nearest.IsEnabled = !tc && !ownSelected;
            keepMask.IsEnabled = nearest.IsEnabled && nearest.IsChecked == true && imagesIndexed;
            // Only matters when Own actually creates a new palette object.
            palGroupBox.IsEnabled = !tc && ownSelected;
            palIndexBox.IsEnabled = !tc && ownSelected;
        }
        foreach (var c in new ToggleButton[] { own, previous, specific, keep, nearest })
            c.IsCheckedChanged += (_, _) => UpdateEnabled();
        bitDepth.SelectionChanged += (_, _) => UpdateEnabled();
        UpdateEnabled();

        var ok = new Button { Content = "Import", MinWidth = 90, IsDefault = true };
        var cancel = new Button { Content = "Cancel", MinWidth = 90 };

        var dialog = new Window
        {
            Background = ThemeBrush("ThemePanelBg"),
            Foreground = ThemeBrush("ThemeText"),
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false,
        };

        ImportChoices Current()
        {
            var palChoice = specific.IsChecked == true && palettes.Count > 0 ? ImportPaletteChoice.Specific
                : previous.IsEnabled && previous.IsChecked == true ? ImportPaletteChoice.Previous
                : ImportPaletteChoice.Own;
            // GIF frames carry no indexes to keep, so the disabled option must
            // not be read back as if it had been chosen.
            return new ImportChoices(
                palChoice,
                palettes.Count > 0 ? palettes[Math.Max(0, paletteBox.SelectedIndex)] : null,
                keep.IsEnabled && keep.IsChecked == true ? ImportIndexChoice.Keep : ImportIndexChoice.Nearest,
                bitDepth.SelectedIndex == 1 ? BitDepthChoice.ThirtyTwoBit : BitDepthChoice.EightBit,
                (int)(groupBox.Value ?? group),
                (int)(numberBox.Value ?? number),
                (int)(palGroupBox.Value ?? paletteGroup),
                (int)(palIndexBox.Value ?? paletteIndex))
            {
                KeepMask = keepMask.IsChecked == true,
            };
        }
        ok.Click += (_, _) =>
        {
            result.TrySetResult(Current());
            dialog.Close();
        };
        cancel.Click += (_, _) => { result.TrySetResult(null); dialog.Close(); };

        // Sections top to bottom: where the sprites go, then how their colors come in.
        TextBlock Section(string text) => new() { Text = text, Foreground = ThemeBrush("ThemeMutedText"), FontWeight = FontWeight.Bold };
        var body = new StackPanel { Margin = new Avalonia.Thickness(20), Spacing = 10 };
        if (imageCount > 1) body.Children.Add(new TextBlock { Text = framesGif ? $"{imageCount} frames" : $"{imageCount} images" });
        if (askPlacement)
        {
            body.Children.Add(Section("Sprite"));
            body.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 14,
                Children =
                {
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center,
                        Children = { new TextBlock { Text = "Group", VerticalAlignment = VerticalAlignment.Center }, groupBox },
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center,
                        Children = { new TextBlock { Text = imageCount > 1 ? "First number" : "Number", VerticalAlignment = VerticalAlignment.Center }, numberBox },
                    },
                },
            });
            if (imageCount > 1)
                body.Children.Add(new TextBlock { Text = "Numbered upward from here, skipping numbers already taken.", Foreground = ThemeBrush("ThemeMutedText") });
        }
        body.Children.Add(Section("Color depth"));
        body.Children.Add(bitDepth);
        body.Children.Add(Section("Palette"));
        body.Children.Add(own);
        body.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 14, Margin = new Avalonia.Thickness(20, 0, 0, 0),
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center,
                    Children = { new TextBlock { Text = "New palette group", VerticalAlignment = VerticalAlignment.Center }, palGroupBox },
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center,
                    Children = { new TextBlock { Text = "Index", VerticalAlignment = VerticalAlignment.Center }, palIndexBox },
                },
            },
        });
        body.Children.Add(previous);
        body.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6,
            Children = { specific, paletteBox },
        });
        body.Children.Add(Section("Indexes"));
        body.Children.Add(keep);
        body.Children.Add(nearest);
        body.Children.Add(keepMask);
        if (framesGif)
            body.Children.Add(new TextBlock
            {
                Text = "A GIF's frames have no palette of their own, so they cannot keep its indexes.",
                Foreground = ThemeBrush("ThemeMutedText"),
            });
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8, Margin = new Avalonia.Thickness(0, 8, 0, 0), Children = { ok, cancel },
        };

        if (preview is null)
        {
            body.Children.Add(buttons);
            dialog.Content = body;
        }
        else
        {
            buttons.Margin = new Avalonia.Thickness(20, 0, 20, 20);
            dialog.Content = new StackPanel
            {
                Children =
                {
                    new StackPanel { Orientation = Orientation.Horizontal, Children = { body, ImportPreviewPanel(dialog, preview, Current, Section,
                        refresh => {
                            foreach (var c in new ToggleButton[] { own, previous, specific, keep, nearest, keepMask })
                                c.IsCheckedChanged += (_, _) => refresh();
                            paletteBox.SelectionChanged += (_, _) => refresh();
                            bitDepth.SelectionChanged += (_, _) => refresh();
                        }) } },
                    buttons,
                },
            };
        }
        dialog.Closed += (_, _) => result.TrySetResult(null);

        dialog.ShowDialog(owner);
        return result.Task;
    }

    /// <summary>The import dialog's preview pane. It rebuilds off the UI thread on each
    /// change; a build overtaken by a newer one is dropped.</summary>
    private static Control ImportPreviewPanel(Window dialog, ImportPreview preview, Func<ImportChoices> current,
        Func<string, TextBlock> section, Action<Action> hookChanges)
    {
        var view = new FitPreviewView { Look = preview.Look };
        var error = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Avalonia.Thickness(8), IsVisible = false,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };

        int generation = 0;
        async void Refresh()
        {
            try
            {
                int mine = ++generation;
                var choices = current();
                Sprite? sprite = null;
                string? failure = null;
                try { sprite = await Task.Run(() => preview.Build(choices)); }
                catch (Exception ex)
                {
                    failure = ex.Message;
                    CrashLog.Error("ImportPreview", ex.Message);
                }
                if (mine != generation) return;
                view.Show(sprite);
                error.Text = failure ?? "";
                error.IsVisible = failure is not null;
            }
            catch (Exception ex) { CrashLog.Write("ImportPreview", ex); }
        }
        hookChanges(Refresh);
        dialog.Opened += (_, _) => Refresh();

        var heading = section("Preview");
        heading.Margin = new Avalonia.Thickness(0, 0, 0, 10);
        // Square: the pane takes the options column's height, and its width follows.
        var pane = new Grid { Children = { view, error } };
        pane.SizeChanged += (_, e) => { if (e.HeightChanged) pane.Width = e.NewSize.Height; };
        Grid.SetRow(pane, 1);
        return new Grid
        {
            Margin = new Avalonia.Thickness(0, 20, 20, 20),
            RowDefinitions = new RowDefinitions("Auto,*"),
            Children = { heading, pane },
        };
    }

    /// <summary>What the sheet dialog collected: a background, size floor, and excluded cells.</summary>
    public sealed record SheetDetectChoices(Rgba? Background, int MinimumSize, IReadOnlyList<Sff.SheetCell> Excluded);

    /// <summary>
    /// Reports what sheet detection found and lets its background and size floor
    /// be corrected before importing. Detected cells can also be excluded from
    /// the import. Returns null if canceled.
    /// </summary>
    public static Task<SheetDetectChoices?> PromptSheetDetect(
        Window owner, string name, byte[] bytes, Sff.SheetLayout layout, int found, int floor)
    {
        var result = new TaskCompletionSource<SheetDetectChoices?>(TaskCreationOptions.RunContinuationsAsynchronously);

        var detected = new TextBlock { Foreground = ThemeBrush("ThemeMutedText") };
        var foundBox = new NumericUpDown { Minimum = 0, Maximum = 4096, Increment = 1, FormatString = "0", Value = floor, Width = 100 };
        var foundLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center };

        // The picker shows two choices when detection had to guess: the most
        // common color, or the first top-left pixel. The label explains each
        // choice; the raw value alone is useless.
        var background = new ComboBox { MinWidth = 190 };
        if (layout.AssumedBackground && layout.Background is { } guessed)
        {
            var firstPixel = Sff.SheetImport.FirstPixelColor(bytes);
            var choices = new[]
            {
                new { Label = $"Most common color ({Describe(guessed)})", Value = (Rgba?)guessed },
                new { Label = $"First pixel ({Describe(firstPixel)})", Value = (Rgba?)firstPixel },
            };
            background.ItemsSource = choices;
            background.DisplayMemberBinding = new Avalonia.Data.Binding("Label");
            background.SelectedIndex = 0;
        }

        HashSet<Sff.SheetCell> excluded = new();
        Bitmap? previewBitmap = null;
        try
        {
            var decoded = ImageImport.DecodeToBgra(bytes);
            using var ms = new MemoryStream(PngExport.Encode(decoded.Bgra, decoded.Width, decoded.Height));
            previewBitmap = new Bitmap(ms);
        }
        catch (Exception ex)
        {
            CrashLog.Error("PromptSheetDetect", $"could not render preview for {name}: {ex.Message}");
        }
        var previewCanvas = new Canvas();
        var previewContent = new Canvas();
        var previewImage = new Image { Stretch = Stretch.None, IsHitTestVisible = false };
        double previewZoom = 1;
        if (previewBitmap != null)
        {
            previewImage.Source = previewBitmap;
            previewCanvas.Width = previewBitmap.PixelSize.Width;
            previewCanvas.Height = previewBitmap.PixelSize.Height;
            previewContent.Width = previewBitmap.PixelSize.Width;
            previewContent.Height = previewBitmap.PixelSize.Height;
            Canvas.SetLeft(previewImage, 0);
            Canvas.SetTop(previewImage, 0);
            previewContent.Children.Add(previewImage);
            previewCanvas.Children.Add(previewContent);
            previewCanvas.ClipToBounds = true;
        }

        void Update()
        {
            int size = (int)(foundBox.Value ?? floor);
            var picked = background.SelectedIndex >= 0 && background.ItemCount > 0
                ? (background.SelectedItem as dynamic)?.Value as Rgba?
                : null;
            var newLayout = LayoutWith(bytes, picked, size);
            int again = newLayout?.Count ?? 0;
            foundLabel.Text = again == 0
                ? $"{found} sprite{(found == 1 ? "" : "s")}"
                : $"{again} sprite{(again == 1 ? "" : "s")} (was {found})";
            detected.Text = layout.AssumedBackground
                ? $"Treating {Describe(picked ?? layout.Background!.Value)} as the background."
                : "The sheet carries its own transparency.";
            if (previewCanvas != null)
            {
                var old = previewContent.Children.OfType<Border>().ToList();
                foreach (var r in old) previewContent.Children.Remove(r);
                if (newLayout != null)
                {
                    var currentBg = picked ?? layout.Background;
                    var activeBrush = ContrastBrush(currentBg);
                    foreach (var cell in newLayout.Cells)
                    {
                        bool isExcluded = excluded.Contains(cell);
                        var rect = new Border
                        {
                            Width = cell.Width,
                            Height = cell.Height,
                            BorderBrush = isExcluded ? Brushes.Gray : activeBrush,
                            BorderThickness = new Avalonia.Thickness(2),
                            Background = Brushes.Transparent,
                            Opacity = isExcluded ? 0.35 : 1
                        };
                        ToolTip.SetTip(rect, "Click to exclude or restore this sprite.");
                        Canvas.SetLeft(rect, cell.X);
                        Canvas.SetTop(rect, cell.Y);
                        rect.PointerPressed += (_, e) =>
                        {
                            if (excluded.Contains(cell))
                            {
                                excluded.Remove(cell);
                                rect.BorderBrush = activeBrush;
                                rect.Opacity = 1;
                            }
                            else
                            {
                                excluded.Add(cell);
                                rect.BorderBrush = Brushes.Gray;
                                rect.Opacity = 0.35;
                            }
                            e.Handled = true;
                        };
                        previewContent.Children.Add(rect);
                    }
                }
            }
        }

        foundBox.ValueChanged += (_, _) => Update();
        background.SelectionChanged += (_, _) => Update();

        var ok = new Button { Content = "Continue", MinWidth = 90, IsDefault = true };
        var cancel = new Button { Content = "Cancel", MinWidth = 90 };

        var dialog = new Window
        {
            Background = ThemeBrush("ThemePanelBg"),
            Foreground = ThemeBrush("ThemeText"),
            Title = "Sprites in Sheet",
            Width = 900,
            Height = 700,
            MinWidth = 500,
            MinHeight = 420,
            MaxWidth = 1400,
            MaxHeight = 1000,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = true,
            ShowInTaskbar = false,
        };

        ok.Click += (_, _) =>
        {
            var picked = background.SelectedIndex >= 0 && background.ItemCount > 0
                ? (background.SelectedItem as dynamic)?.Value as Rgba?
                : null;
            result.TrySetResult(new SheetDetectChoices(picked, (int)(foundBox.Value ?? floor), excluded.ToList()));
            dialog.Close();
        };
        cancel.Click += (_, _) => { result.TrySetResult(null); dialog.Close(); };

        var body = new Grid
        {
            Margin = new Avalonia.Thickness(20),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto,Auto,Auto,Auto"),
        };
        body.RowSpacing = 10;
        body.Children.Add(new TextBlock { Text = name });
        Grid.SetRow(foundLabel, 1);
        body.Children.Add(foundLabel);
        Grid.SetRow(detected, 2);
        body.Children.Add(detected);
        if (previewCanvas != null && previewBitmap != null)
        {
            var preview = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = ThemeBrush("ThemeListBg"),
                Content = previewCanvas,
            };
            preview.AddHandler(Avalonia.Input.InputElement.PointerWheelChangedEvent, (_, e) =>
            {
                if (e.Delta.Y == 0) return;
                var levels = SpriteView.ZoomLevels;
                int current = Array.IndexOf(levels, previewZoom);
                if (current < 0) current = Array.FindIndex(levels, z => z >= previewZoom);
                if (current < 0) current = levels.Length - 1;
                int next = Math.Clamp(current + (e.Delta.Y > 0 ? 1 : -1), 0, levels.Length - 1);
                if (next != current)
                {
                    previewZoom = levels[next];
                    previewContent.RenderTransform = new ScaleTransform(previewZoom, previewZoom);
                    previewContent.RenderTransformOrigin = new Avalonia.RelativePoint(0, 0, Avalonia.RelativeUnit.Relative);
                    previewCanvas.Width = previewBitmap.PixelSize.Width * previewZoom;
                    previewCanvas.Height = previewBitmap.PixelSize.Height * previewZoom;
                }
                e.Handled = true;
            }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            Grid.SetRow(preview, 3);
            body.Children.Add(preview);
        }
        var minimumSize = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = "Ignore sprites smaller than", VerticalAlignment = VerticalAlignment.Center },
                foundBox,
                new TextBlock { Text = "pixels", VerticalAlignment = VerticalAlignment.Center },
            },
        };
        Grid.SetRow(minimumSize, 4);
        body.Children.Add(minimumSize);
        var minimumNote = new TextBlock
        {
            Text = "Raising this drops the stray pixels a keyed sheet leaves behind; lowering it keeps sprites too small to be real.",
            Foreground = ThemeBrush("ThemeMutedText"),
        };
        Grid.SetRow(minimumNote, 5);
        body.Children.Add(minimumNote);
        if (layout.AssumedBackground)
        {
            var backgroundRow = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = "Background", VerticalAlignment = VerticalAlignment.Center },
                    background,
                },
            };
            Grid.SetRow(backgroundRow, 6);
            body.Children.Add(backgroundRow);
        }
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8, Margin = new Avalonia.Thickness(0, 8, 0, 0), Children = { ok, cancel },
        };
        Grid.SetRow(buttons, 7);
        body.Children.Add(buttons);

        dialog.Content = body;
        dialog.Closed += (_, _) =>
        {
            result.TrySetResult(null);
            previewBitmap?.Dispose();
        };

        Update();
        dialog.ShowDialog(owner);
        return result.Task;
    }

    /// <summary>Names a color for the status line and the sheet dialog.</summary>
    private static string Describe(Rgba c) => Sff.SheetImport.Describe(c);

    /// <summary>Chooses a visible outline color for the sheet preview.</summary>
    private static Brush ContrastBrush(Rgba? bg)
    {
        if (bg is null) return new SolidColorBrush(Colors.Cyan);
        var c = bg.Value;
        return new SolidColorBrush(Color.FromRgb((byte)(255 - c.R), (byte)(255 - c.G), (byte)(255 - c.B)));
    }

    /// <summary>Redetects a sheet to preview another size floor or background.</summary>
    private static SheetLayout? LayoutWith(byte[] bytes, Rgba? background, int minimumSize)
    {
        int before = Sff.SheetImport.MinimumSize;
        try
        {
            Sff.SheetImport.MinimumSize = minimumSize;
            return Sff.SheetImport.Detect(bytes, background);
        }
        catch (Exception ex)
        {
            CrashLog.Error("PromptSheetDetect", $"layout with floor of {minimumSize} failed: {ex.Message}");
            return null;
        }
        finally
        {
            Sff.SheetImport.MinimumSize = before;
        }
    }

    public static Task<int?> PromptPaletteDepth(Window owner, int currentColors, IReadOnlyList<int> availableColors)
    {
        var result = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var depths = Sff.PaletteDepth.SupportedColorCounts;
        var choices = depths.Select((count, bits) => new ComboBoxItem
        {
            Content = $"{bits + 1}-bit ({count} colors)",
            Tag = count,
            IsEnabled = availableColors.Contains(count),
        }).ToArray();
        var depth = new ComboBox { ItemsSource = choices, MinWidth = 220 };
        var apply = new Button { Content = "Convert", MinWidth = 90, IsDefault = true };
        int initial = Array.IndexOf(depths, currentColors);
        if (initial < 0 || !availableColors.Contains(currentColors))
            initial = availableColors.Contains(256) ? Array.IndexOf(depths, 256)
                : Array.FindIndex(depths, availableColors.Contains);
        depth.SelectedIndex = initial;
        var note = new TextBlock { Foreground = ThemeBrush("ThemeMutedText"), TextWrapping = TextWrapping.Wrap };
        void Update()
        {
            int selectedColors = depth.SelectedItem is ComboBoxItem { Tag: int count } ? count : 0;
            bool enabled = selectedColors > 0 && availableColors.Contains(selectedColors);
            note.Text = enabled
                ? selectedColors > currentColors
                    ? "New entries are filled with opaque black."
                    : "Existing colors and indexes are preserved."
                : "A sprite using this palette has indexes that exceed this depth, or is undecoded.";
            apply.IsEnabled = enabled;
        }
        var cancel = new Button { Content = "Cancel", MinWidth = 90 };
        depth.SelectionChanged += (_, _) => Update();
        var body = new StackPanel { Spacing = 12, Margin = new Avalonia.Thickness(20), MaxWidth = 440 };
        body.Children.Add(new TextBlock { Text = "Choose the indexed depth for this palette." });
        body.Children.Add(depth);
        body.Children.Add(note);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8,
            Children = { apply, cancel },
        };
        body.Children.Add(buttons);
        var dialog = new Window
        {
            Background = ThemeBrush("ThemePanelBg"), Foreground = ThemeBrush("ThemeText"),
            Title = "Convert Palette Depth", SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false, ShowInTaskbar = false,
            Content = body,
        };
        apply.Click += (_, _) =>
        {
            if (depth.SelectedItem is not ComboBoxItem { Tag: int count } || !availableColors.Contains(count)) return;
            result.TrySetResult(count);
            dialog.Close();
        };
        cancel.Click += (_, _) => { result.TrySetResult(null); dialog.Close(); };
        dialog.Closed += (_, _) => result.TrySetResult(null);
        Update();
        dialog.ShowDialog(owner);
        return result.Task;
    }

    public static Task<TooManyColorsChoice?> PromptTooManyColors(
        Window owner, string name, int colors, bool trueColorAvailable)
    {
        int max = Sff.GifImport.MaxSharedColors;
        string message = $"{name} has {colors} colors, more than the {max} one palette holds. "
            + $"Reducing to {max} keeps it 8-bit but shifts colors that do not fit. ";
        var buttons = new List<(string Label, TooManyColorsChoice? Value)>
        {
            ($"Reduce to {max} Colors", TooManyColorsChoice.Reduce),
        };
        if (trueColorAvailable)
        {
            message += "32-bit color keeps every color exactly.";
            buttons.Add(("Use 32-bit Color", TooManyColorsChoice.TrueColor));
        }
        else message += "This SFF version does not support 32-bit color.";
        buttons.Add(("Cancel", null));
        return Show<TooManyColorsChoice?>(owner, "Too Many Colors for One Palette",
            message, buttons.ToArray(), null);
    }

    public enum TooManyColorsChoice { Reduce, TrueColor }

    public static Task<bool> ConfirmMedianCut(Window owner, IReadOnlyList<string> images)
    {
        ArgumentNullException.ThrowIfNull(images);
        string message = images.Count == 1
            ? $"{images[0]} has more than 256 colors.\nIt will be reduced to 256 colors using frequency-weighted median cut."
            : $"{images.Count} images have more than 256 colors.\nThey will be reduced to 256 colors using frequency-weighted median cut.";
        return Confirm(owner, "Reduce Image Colors", message, "Continue");
    }

    /// <summary>Chooses whether this sprite keeps its indexes or maps them to a palette by color.</summary>
    public static Task<ImportIndexChoice?> PromptPaletteIndexMapping(
        Window owner, bool allowNearest, string? nearestDisabledReason = null, string? notice = null,
        bool multiple = false, bool allowKeep = true, string? keepDisabledReason = null)
    {
        var result = new TaskCompletionSource<ImportIndexChoice?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var keep = new RadioButton
        {
            GroupName = "PaletteIndexMapping", Content = "Keep indexes",
            IsChecked = allowKeep, IsEnabled = allowKeep,
        };
        ToolTip.SetTip(keep, allowKeep
            ? multiple ? "Leaves each selected sprite's pixel indexes unchanged."
                : "Leaves this sprite's pixel indexes unchanged."
            : keepDisabledReason ?? "These sprites have no indexes to keep.");
        var nearest = new RadioButton
        {
            GroupName = "PaletteIndexMapping", Content = "Match the nearest color",
            IsChecked = !allowKeep, IsEnabled = allowNearest,
        };
        ToolTip.SetTip(nearest, allowNearest
            ? multiple ? "Maps each selected sprite's visible colors to the closest palette colors."
                : "Maps each visible color to its closest color in the palette."
            : nearestDisabledReason ?? "Nearest-color matching is unavailable for this sprite.");

        var dialog = new Window
        {
            Background = ThemeBrush("ThemePanelBg"),
            Foreground = ThemeBrush("ThemeText"),
            Title = "Palette Index Mapping",
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false,
        };
        var apply = new Button { Content = "Continue", MinWidth = 90, IsDefault = true };
        var cancel = new Button { Content = "Cancel", MinWidth = 90 };
        apply.Click += (_, _) =>
        {
            result.TrySetResult(nearest.IsChecked == true ? ImportIndexChoice.Nearest : ImportIndexChoice.Keep);
            dialog.Close();
        };
        cancel.Click += (_, _) => { result.TrySetResult(null); dialog.Close(); };

        var body = new StackPanel { Margin = new Avalonia.Thickness(20), Spacing = 10 };
        body.Children.Add(new TextBlock
        {
            Text = !allowKeep
                ? multiple
                    ? "The selected sprites are true color. Matching them to the palette converts them to indexed pixels."
                    : "This sprite is true color. Matching it to the palette converts it to indexed pixels."
                : multiple
                    ? "Choose how the selected sprites' existing pixel indexes map to the palette."
                    : "Choose how this sprite's existing pixel indexes map to the palette.",
            MaxWidth = 340, TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(keep);
        body.Children.Add(nearest);
        if (!string.IsNullOrWhiteSpace(notice))
            body.Children.Add(new TextBlock
            {
                Text = notice, Foreground = ThemeBrush("ThemeMutedText"),
                MaxWidth = 340, TextWrapping = TextWrapping.Wrap,
            });
        body.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8, Margin = new Avalonia.Thickness(0, 8, 0, 0), Children = { apply, cancel },
        });

        dialog.Content = body;
        dialog.Closed += (_, _) => result.TrySetResult(null);
        dialog.ShowDialog(owner);
        return result.Task;
    }

    /// <summary>Where a .def's files open when the current tab already has files. Null if canceled.</summary>
    public static Task<bool?> AskOpenInNewTab(Window owner, string fileName) =>
        Show<bool?>(owner, "Open Project",
            $"This tab already has files open. Open {fileName} in a new tab, or close them and open it here?",
            [("Open in New Tab", true), ("Open in Current Tab", false), ("Cancel", null)]);

    /// <summary>Loading a session while tabs are in use: true appends its tabs, false replaces them, null cancels.</summary>
    public static Task<bool?> AskReplaceOrAppendTabs(Window owner, string fileName) =>
        Show<bool?>(owner, "Load Session",
            $"Some tabs already have files open. Replace them with the tabs in {fileName}, or add those after them?",
            [("Replace", false), ("Append", true), ("Cancel", null)]);

    /// <summary>Closing a project tab, or quitting, with unsaved work in it.</summary>
    public static Task<SaveChoice> AskToSaveTab(Window owner, string tabName, string what) =>
        Show(owner, "Unsaved Changes",
            $"{tabName} has unsaved changes to the {what}.",
            [("Save", SaveChoice.Save), ("Discard", SaveChoice.Discard), ("Cancel", SaveChoice.Cancel)],
            SaveChoice.Cancel);

    public static Task<SaveChoice> AskToSave(Window owner, string fileName) =>
        Show(owner, "Unsaved Changes",
            $"{fileName} has changes that have not been saved.",
            [("Save", SaveChoice.Save), ("Discard", SaveChoice.Discard), ("Cancel", SaveChoice.Cancel)],
            SaveChoice.Cancel);

    public static Task<PendingPaintChoice> AskAboutPendingPaint(Window owner) =>
        Show(owner, "Uncommitted Paint Changes",
            "This sprite has paint strokes that haven't been committed yet.",
            [("Commit", PendingPaintChoice.Commit), ("Discard", PendingPaintChoice.Discard), ("Cancel", PendingPaintChoice.Cancel)],
            PendingPaintChoice.Cancel);

    public static Task<PendingAdjustChoice> AskAboutPendingColorAdjust(Window owner) =>
        Show(owner, "Uncommitted Color Adjustment",
            "This palette has a color adjustment that hasn't been committed yet.",
            [("Commit", PendingAdjustChoice.Commit), ("Discard", PendingAdjustChoice.Discard), ("Cancel", PendingAdjustChoice.Cancel)],
            PendingAdjustChoice.Cancel);

    public static Task<PendingSoundEditChoice> AskAboutPendingSoundEdits(Window owner) =>
        Show(owner, "Uncommitted Sound Changes",
            "This sound has edits that haven't been committed yet.",
            [("Commit", PendingSoundEditChoice.Commit), ("Discard", PendingSoundEditChoice.Discard), ("Cancel", PendingSoundEditChoice.Cancel)],
            PendingSoundEditChoice.Cancel);

    public static Task<bool> AskFindDuplicatesAgain(Window owner) =>
        Show(owner, "Duplicate Palettes Changed",
            "The duplicate palettes changed while the report was open.",
            [("Find Again", true), ("Cancel", false)],
            false);

    public static Task<bool> AskOpenSpriteFile(Window owner) =>
        Show(owner, "Sprite File Not Open",
            "This frame's sprite is not in the loaded .sff.",
            [("Open File", true), ("Cancel", false)],
            false);

    /// <summary>Which channel(s) to linearly interpolate for a gradient. Returns null if canceled or nothing is checked.</summary>
    public static Task<GradientChannels?> PromptGradientChannels(Window owner, int colorCount, bool alphaEnabled = true)
    {
        var result = new TaskCompletionSource<GradientChannels?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rBox = new CheckBox { Content = "R", IsChecked = true };
        var gBox = new CheckBox { Content = "G", IsChecked = true };
        var bBox = new CheckBox { Content = "B", IsChecked = true };
        var aBox = new CheckBox { Content = "A" };
        if (!alphaEnabled)
        {
            aBox.IsEnabled = false;
            ToolTip.SetTip(aBox, "SFFv1 stores RGB only.");
        }

        var dialog = new Window
        {
            Background = ThemeBrush("ThemePanelBg"),
            Foreground = ThemeBrush("ThemeText"),
            Title = "Create Gradient",
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false,
        };

        var ok = new Button { Content = "Create", MinWidth = 90, IsDefault = true };
        var cancel = new Button { Content = "Cancel", MinWidth = 90 };
        ok.Click += (_, _) =>
        {
            var ch = GradientChannels.None;
            if (rBox.IsChecked == true) ch |= GradientChannels.R;
            if (gBox.IsChecked == true) ch |= GradientChannels.G;
            if (bBox.IsChecked == true) ch |= GradientChannels.B;
            if (aBox.IsChecked == true) ch |= GradientChannels.A;
            result.TrySetResult(ch == GradientChannels.None ? null : ch);
            dialog.Close();
        };
        cancel.Click += (_, _) => { result.TrySetResult(null); dialog.Close(); };

        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = $"Interpolate between the first and last of the {colorCount} selected colors, channel by channel:",
                    MaxWidth = 320, TextWrapping = TextWrapping.Wrap,
                },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, Children = { rBox, gBox, bBox, aBox } },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8, Children = { ok, cancel },
                },
            },
        };
        dialog.Closed += (_, _) => result.TrySetResult(null);

        dialog.ShowDialog(owner);
        return result.Task;
    }

    /// <summary>Export GIF's choices: whole-number scale and whether Clsn boxes are drawn.</summary>
    public readonly record struct GifExportChoices(int Scale, bool Boxes);

    /// <summary>Asks Export GIF's scale (1 to maxScale) and whether to draw Clsn boxes. Null if canceled.</summary>
    public static Task<GifExportChoices?> PromptGifExport(Window owner, int initialScale, int maxScale)
    {
        var result = new TaskCompletionSource<GifExportChoices?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var scale = new NumericUpDown
        {
            Value = Math.Clamp(initialScale, 1, maxScale), Minimum = 1, Maximum = maxScale, Increment = 1,
            FormatString = "0", Width = 120,
        };
        var boxes = new CheckBox { Content = "Draw Clsn boxes", IsChecked = false };

        var dialog = new Window
        {
            Background = ThemeBrush("ThemePanelBg"),
            Foreground = ThemeBrush("ThemeText"),
            Title = "Export GIF",
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false,
        };

        var ok = new Button { Content = "Export", MinWidth = 90, IsDefault = true };
        var cancel = new Button { Content = "Cancel", MinWidth = 90 };
        ok.Click += (_, _) =>
        {
            int s = scale.Value is { } v ? (int)v : Math.Clamp(initialScale, 1, maxScale);
            result.TrySetResult(new GifExportChoices(s, boxes.IsChecked == true));
            dialog.Close();
        };
        cancel.Click += (_, _) => { result.TrySetResult(null); dialog.Close(); };

        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 8,
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8,
                    Children = { new TextBlock { Text = $"Scale (1-{maxScale})", VerticalAlignment = VerticalAlignment.Center }, scale },
                },
                boxes,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8, Margin = new Avalonia.Thickness(0, 8, 0, 0), Children = { ok, cancel },
                },
            },
        };
        dialog.Closed += (_, _) => result.TrySetResult(null);

        dialog.ShowDialog(owner);
        return result.Task;
    }

    /// <summary>Asks which way to reorder a frame's own Clsn boxes. Returns null if canceled.</summary>
    public static Task<BoxSortOrder?> PromptBoxSortOrder(Window owner,
        string prompt = "Reorder this frame's own Clsn1/Clsn2 boxes by:")
    {
        var result = new TaskCompletionSource<BoxSortOrder?>(TaskCreationOptions.RunContinuationsAsynchronously);

        var top = new RadioButton { GroupName = "SortOrder", Content = "Top to bottom", IsChecked = true };
        var bottom = new RadioButton { GroupName = "SortOrder", Content = "Bottom to top" };
        var left = new RadioButton { GroupName = "SortOrder", Content = "Leftmost to rightmost" };
        var right = new RadioButton { GroupName = "SortOrder", Content = "Rightmost to leftmost" };
        var small = new RadioButton { GroupName = "SortOrder", Content = "Smallest area first" };
        var large = new RadioButton { GroupName = "SortOrder", Content = "Largest area first" };

        var dialog = new Window
        {
            Background = ThemeBrush("ThemePanelBg"),
            Foreground = ThemeBrush("ThemeText"),
            Title = "Sort Boxes",
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false,
        };

        var ok = new Button { Content = "Sort", MinWidth = 90, IsDefault = true };
        var cancel = new Button { Content = "Cancel", MinWidth = 90 };
        ok.Click += (_, _) =>
        {
            var order = top.IsChecked == true ? BoxSortOrder.TopToBottom
                : bottom.IsChecked == true ? BoxSortOrder.BottomToTop
                : left.IsChecked == true ? BoxSortOrder.LeftToRight
                : right.IsChecked == true ? BoxSortOrder.RightToLeft
                : small.IsChecked == true ? BoxSortOrder.SmallestFirst
                : BoxSortOrder.LargestFirst;
            result.TrySetResult(order);
            dialog.Close();
        };
        cancel.Click += (_, _) => { result.TrySetResult(null); dialog.Close(); };

        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = prompt },
                top, bottom, left, right, small, large,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8, Margin = new Avalonia.Thickness(0, 8, 0, 0), Children = { ok, cancel },
                },
            },
        };
        dialog.Closed += (_, _) => result.TrySetResult(null);

        dialog.ShowDialog(owner);
        return result.Task;
    }

    /// <summary>Asks how to sort the selected colors, and whether sprites using the palette follow. Returns null if canceled.</summary>
    public static Task<ColorReorderChoice?> PromptColorSort(Window owner, bool canUpdateSprites, string? disabledReason)
    {
        var result = new TaskCompletionSource<ColorReorderChoice?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var brightness = new RadioButton { GroupName = "ColorSortKey", Content = "Brightness", IsChecked = true };
        var hue = new RadioButton { GroupName = "ColorSortKey", Content = "Hue" };
        var index = new RadioButton { GroupName = "ColorSortKey", Content = "Index" };
        var ascending = new RadioButton { GroupName = "ColorSortOrder", Content = "Ascending", IsChecked = true };
        var descending = new RadioButton { GroupName = "ColorSortOrder", Content = "Descending" };
        var update = new CheckBox { Content = "Update sprites that use this palette", IsEnabled = canUpdateSprites };

        var dialog = new Window
        {
            Background = ThemeBrush("ThemePanelBg"),
            Foreground = ThemeBrush("ThemeText"),
            Title = "Sort Colors",
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false,
        };

        var ok = new Button { Content = "Sort", MinWidth = 90, IsDefault = true };
        var cancel = new Button { Content = "Cancel", MinWidth = 90 };
        ok.Click += (_, _) =>
        {
            var key = brightness.IsChecked == true ? ColorSortKey.Brightness
                : hue.IsChecked == true ? ColorSortKey.Hue
                : ColorSortKey.Index;
            result.TrySetResult(new ColorReorderChoice(key, descending.IsChecked == true, update.IsChecked == true));
            dialog.Close();
        };
        cancel.Click += (_, _) => { result.TrySetResult(null); dialog.Close(); };
        dialog.Closed += (_, _) => result.TrySetResult(null);

        var panel = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "Sort the selected colors by:" },
                index, brightness, hue,
                new TextBlock { Text = "Order:" },
                ascending, descending,
                update,
            },
        };
        if (!canUpdateSprites && disabledReason is not null) panel.Children.Add(new TextBlock { Text = disabledReason });
        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8, Margin = new Avalonia.Thickness(0, 8, 0, 0), Children = { ok, cancel },
        });
        dialog.Content = panel;

        dialog.ShowDialog(owner);
        return result.Task;
    }

    /// <summary>Asks whether to swap two colors and update sprites using the palette. Returns null if canceled.</summary>
    /// <summary>"Based on" and "Padding" dropdowns for Combine and Merge. Returns the chosen indexes; null on Cancel.</summary>
    public static Task<(int Target, int Padding)?> PromptMergeOptions(Window owner, string title, string affirmative,
        IReadOnlyList<string> targets, int targetIndex, IReadOnlyList<Rgba> paddings, string countText)
    {
        var result = new TaskCompletionSource<(int, int)?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ok = new Button { Content = affirmative, MinWidth = 90, IsDefault = true };
        var cancel = new Button { Content = "Cancel", MinWidth = 90 };
        var target = new ComboBox { ItemsSource = targets, SelectedIndex = targetIndex, MinWidth = 160 };
        // Data items with a template: a control item would show in the closed box as an untinted copy.
        var padding = new ComboBox
        {
            MinWidth = 160, ItemsSource = paddings, SelectedIndex = 0,
            ItemTemplate = new FuncDataTemplate<Rgba>((c, _) => new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 8,
                Children =
                {
                    new Border
                    {
                        Width = 14, Height = 14, BorderThickness = new Avalonia.Thickness(1),
                        BorderBrush = ThemeBrush("ThemeMutedText"),
                        Background = new SolidColorBrush(Color.FromArgb(c.A, c.R, c.G, c.B)),
                    },
                    new TextBlock { Text = c.A == 255 ? $"{c.R},{c.G},{c.B}" : $"{c.R},{c.G},{c.B},{c.A}" },
                },
            }),
        };
        var body = new StackPanel { Spacing = 12, Margin = new Avalonia.Thickness(20), MaxWidth = 440 };
        body.Children.Add(new StackPanel { Spacing = 4, Children = { new TextBlock { Text = "Based on" }, target } });
        body.Children.Add(new StackPanel { Spacing = 4, Children = { new TextBlock { Text = "Padding" }, padding } });
        body.Children.Add(new TextBlock { Text = countText, Foreground = ThemeBrush("ThemeMutedText") });
        body.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8,
            Children = { ok, cancel },
        });
        var dialog = new Window
        {
            Background = ThemeBrush("ThemePanelBg"), Foreground = ThemeBrush("ThemeText"),
            Title = title, SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false, ShowInTaskbar = false,
            Content = body,
        };
        ok.Click += (_, _) =>
        {
            result.TrySetResult((Math.Max(0, target.SelectedIndex), Math.Max(0, padding.SelectedIndex)));
            dialog.Close();
        };
        cancel.Click += (_, _) => { result.TrySetResult(null); dialog.Close(); };
        dialog.Closed += (_, _) => result.TrySetResult(null);
        dialog.ShowDialog(owner);
        return result.Task;
    }

    public static Task<bool?> PromptColorSwap(Window owner, int first, int second, bool canUpdateSprites, string? disabledReason)
    {
        var result = new TaskCompletionSource<bool?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var update = new CheckBox { Content = "Update sprites that use this palette", IsEnabled = canUpdateSprites };

        var dialog = new Window
        {
            Background = ThemeBrush("ThemePanelBg"),
            Foreground = ThemeBrush("ThemeText"),
            Title = "Swap Colors",
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false,
        };

        var ok = new Button { Content = "Swap", MinWidth = 90, IsDefault = true };
        var cancel = new Button { Content = "Cancel", MinWidth = 90 };
        ok.Click += (_, _) => { result.TrySetResult(update.IsChecked == true); dialog.Close(); };
        cancel.Click += (_, _) => { result.TrySetResult(null); dialog.Close(); };
        dialog.Closed += (_, _) => result.TrySetResult(null);

        var panel = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 8,
            Children = { new TextBlock { Text = $"Swap colors {first} and {second}." }, update },
        };
        if (!canUpdateSprites && disabledReason is not null) panel.Children.Add(new TextBlock { Text = disabledReason });
        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8, Margin = new Avalonia.Thickness(0, 8, 0, 0), Children = { ok, cancel },
        });
        dialog.Content = panel;

        dialog.ShowDialog(owner);
        return result.Task;
    }

    /// <summary>
    /// Modal progress window for a long job: a bar, "label N of M" and Cancel. It blocks the
    /// owner while open. Cancel or closing it cancels Token; the job calls Close when done.
    /// </summary>
    public sealed class ProgressWindow
    {
        private readonly Window _dialog;
        private readonly ProgressBar _bar;
        private readonly TextBlock _text;
        private readonly string _label;
        private readonly int _total;
        private readonly CancellationTokenSource _cancel = new();
        private bool _finished, _closed;

        public CancellationToken Token => _cancel.Token;

        public ProgressWindow(Window owner, string title, string label, int total)
        {
            _label = label;
            _total = Math.Max(1, total);
            _bar = new ProgressBar { Minimum = 0, Maximum = _total, Value = 0, MinWidth = 320 };
            _text = new TextBlock();
            var cancel = new Button { Content = "Cancel", MinWidth = 90, HorizontalAlignment = HorizontalAlignment.Right };
            cancel.Click += (_, _) => _dialog!.Close();
            _dialog = new Window
            {
                Background = ThemeBrush("ThemePanelBg"),
                Foreground = ThemeBrush("ThemeText"),
                Title = title,
                SizeToContent = SizeToContent.WidthAndHeight,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false,
                ShowInTaskbar = false,
                Content = new StackPanel
                {
                    Margin = new Avalonia.Thickness(20), Spacing = 10,
                    Children = { _text, _bar, cancel },
                },
            };
            _dialog.Closed += (_, _) => { _closed = true; if (!_finished) _cancel.Cancel(); };
            Report(0);
            _dialog.ShowDialog(owner);
        }

        public void Report(int done)
        {
            _bar.Value = Math.Clamp(done, 0, _total);
            _text.Text = $"{_label} {Math.Min(done + 1, _total)} of {_total}";
        }

        public void Close()
        {
            _finished = true;
            if (!_closed) _dialog.Close();
        }
    }

    /// <summary>How many boxes Auto Clsn2 should use, and their layout.</summary>
    public readonly record struct AutoBoxCount(int Boxes, CoverLayout Layout, bool SkipWithoutClsn2 = false, ClsnKind Kind = ClsnKind.Hurt);

    /// <summary>
    /// Asks which kind (Auto Clsn only; For All is always Clsn2) and how many boxes cover the sprite. Returns
    /// null if canceled or the count is not a positive number. maxBoxes is
    /// the user's own ceiling from settings.json — above it auto-generated
    /// boxes stop being usable, but the sky is the limit if someone insists.
    /// </summary>
    public static Task<AutoBoxCount?> PromptAutoBoxCount(Window owner, int initialBoxes, CoverLayout initialLayout, int maxBoxes,
        bool forAll = false)
    {
        // For All only: leave frames that have no Clsn2 of their own alone.
        var skip = new CheckBox { Content = "Skip frames without Clsn2", IsChecked = false, IsVisible = forAll };
        var result = new TaskCompletionSource<AutoBoxCount?>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Opens on Clsn2 every time; the kind isn't remembered.
        var kind = new ComboBox { ItemsSource = new[] { "Clsn1", "Clsn2" }, SelectedIndex = 1, HorizontalAlignment = HorizontalAlignment.Stretch };
        var box = new TextBox { Text = initialBoxes.ToString(CultureInfo.InvariantCulture), MinWidth = 260 };
        // Same order as CoverLayout.
        var layout = new ComboBox { ItemsSource = new[] { "Stacked", "Side by side", "Auto" }, SelectedIndex = (int)initialLayout,
            HorizontalAlignment = HorizontalAlignment.Stretch };

        var dialog = new Window
        {
            Background = ThemeBrush("ThemePanelBg"),
            Foreground = ThemeBrush("ThemeText"),
            Title = forAll ? "Auto Clsn2 For All" : "Auto Clsn",
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false,
        };

        void Accept()
        {
            if (!int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) return;
            n = Math.Clamp(n, 1, Math.Max(1, maxBoxes));
            box.Text = n.ToString(CultureInfo.InvariantCulture); // reflect a clamped value
            var chosen = forAll || kind.SelectedIndex != 0 ? ClsnKind.Hurt : ClsnKind.Attack;
            result.TrySetResult(new AutoBoxCount(n, (CoverLayout)Math.Max(0, layout.SelectedIndex), skip.IsChecked == true, chosen));
            dialog.Close();
        }

        var ok = new Button { Content = "Generate", MinWidth = 90, IsDefault = true };
        var cancel = new Button { Content = "Cancel", MinWidth = 90 };
        ok.Click += (_, _) => Accept();
        cancel.Click += (_, _) => { result.TrySetResult(null); dialog.Close(); };
        box.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) Accept(); };

        // Each label sits above its control.
        var kindLabel = new TextBlock { Text = "Kind", IsVisible = !forAll };
        kind.IsVisible = !forAll;
        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 6,
            Children =
            {
                kindLabel, kind,
                new TextBlock { Text = "Boxes" }, box,
                new TextBlock { Text = "Layout" }, layout,
                skip,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8, Margin = new Avalonia.Thickness(0, 10, 0, 0), Children = { ok, cancel },
                },
            },
        };
        dialog.Closed += (_, _) => result.TrySetResult(null);
        dialog.Opened += (_, _) => box.Focus();

        dialog.ShowDialog(owner);
        return result.Task;
    }

    /// <summary>
    /// Blend mode picker: a radio per mode AirParser recognizes without a
    /// numeric Source/Destination, plus A/S/SA's own "Custom alpha/dest"
    /// checkbox for the forms that take one (Add1 is a fixed A1 = 255/128,
    /// not a separate custom case — it's reachable via the checkbox too, but
    /// kept as its own radio since it's common enough to deserve one click).
    /// The checkbox defaults off, and the fields default to 256/256 when
    /// first turned on — leaving it off writes the plain "A"/"S"/"SA" tag.
    /// Returns null if canceled.
    /// </summary>
    public static Task<Blend?> PromptBlend(Window owner, Blend initial)
    {
        var result = new TaskCompletionSource<Blend?>(TaskCreationOptions.RunContinuationsAsynchronously);

        var noneR = new RadioButton { GroupName = "BlendKind", Content = "None" };
        var addR = new RadioButton { GroupName = "BlendKind", Content = "A (Add)" };
        var add1R = new RadioButton { GroupName = "BlendKind", Content = "A1 (Add1)" };
        var subR = new RadioButton { GroupName = "BlendKind", Content = "S (Sub)" };
        var subAddR = new RadioButton { GroupName = "BlendKind", Content = "SA (SubAdd)" };

        var selected = initial.Mode switch
        {
            BlendMode.Add when initial.Source == 255 && initial.Destination == 128 => add1R,
            BlendMode.Add => addR, // covers both plain "A" (255/255) and a custom "AS..." value
            BlendMode.Subtract => subR,
            BlendMode.SubAdd => subAddR,
            _ => noneR,
        };
        selected.IsChecked = true;

        var customCheck = new CheckBox { Content = "Custom alpha/dest" };
        var srcBox = new NumericUpDown { Minimum = 0, Maximum = 256, Increment = 1, Width = 90 };
        var dstBox = new NumericUpDown { Minimum = 0, Maximum = 256, Increment = 1, Width = 90 };

        // The initial selection shows its actual values if it already has custom
        // ones; every other radio starts uncustomised (256/256, boxes disabled)
        // until the user opts in via the checkbox.
        bool startsCustom = (selected == addR || selected == subR || selected == subAddR)
                             && (initial.Source != 255 || initial.Destination != 255 || initial.HasExplicitAlpha);
        customCheck.IsChecked = startsCustom;
        srcBox.Value = startsCustom ? initial.Source : 256;
        dstBox.Value = startsCustom ? initial.Destination : 256;

        void SyncAlphaSection()
        {
            bool section = addR.IsChecked == true || subR.IsChecked == true || subAddR.IsChecked == true;
            customCheck.IsEnabled = section;
            bool custom = section && customCheck.IsChecked == true;
            srcBox.IsEnabled = custom;
            dstBox.IsEnabled = custom;
        }
        SyncAlphaSection();

        customCheck.Click += (_, _) =>
        {
            if (customCheck.IsChecked == true) { srcBox.Value = 256; dstBox.Value = 256; }
            SyncAlphaSection();
        };

        // Switching to a different custom-alpha radio starts it fresh (off, 256/256)
        // rather than carrying over whatever the previous radio's fields held.
        var current = selected;
        foreach (var r in new[] { noneR, addR, add1R, subR, subAddR })
            r.Click += (_, _) =>
            {
                if (r != current && (r == addR || r == subR || r == subAddR))
                {
                    customCheck.IsChecked = false;
                    srcBox.Value = 256;
                    dstBox.Value = 256;
                }
                current = r;
                SyncAlphaSection();
            };

        var blendDialog = new Window
        {
            Background = ThemeBrush("ThemePanelBg"),
            Foreground = ThemeBrush("ThemeText"),
            Title = "Blend",
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false,
        };

        void Accept()
        {
            bool custom = customCheck.IsChecked == true;
            int src = custom ? (int)(srcBox.Value ?? 256) : 255;
            int dst = custom ? (int)(dstBox.Value ?? 256) : 255;
            var blend = noneR.IsChecked == true ? Blend.None
                : add1R.IsChecked == true ? new Blend(BlendMode.Add, 255, 128)
                : addR.IsChecked == true ? new Blend(BlendMode.Add, src, dst, custom ? $"AS{src}D{dst}" : "")
                : subR.IsChecked == true ? new Blend(BlendMode.Subtract, src, dst, custom ? $"SS{src}D{dst}" : "")
                : new Blend(BlendMode.SubAdd, src, dst, custom ? $"SAS{src}D{dst}" : ""); // subAddR, the only case left
            // Custom alpha is always written out in full (e.g. AS255D255), even when it equals the default.
            result.TrySetResult(blend);
            blendDialog.Close();
        }

        var ok = new Button { Content = "OK", MinWidth = 90, IsDefault = true };
        var cancel = new Button { Content = "Cancel", MinWidth = 90 };
        ok.Click += (_, _) => Accept();
        cancel.Click += (_, _) => { result.TrySetResult(null); blendDialog.Close(); };

        blendDialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "Blend mode for this frame:" },
                noneR, addR, add1R, subR, subAddR,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Avalonia.Thickness(20, 4, 0, 0),
                    Children = { customCheck },
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Avalonia.Thickness(20, 0, 0, 0),
                    Children =
                    {
                        new TextBlock { Text = "Source", VerticalAlignment = VerticalAlignment.Center, Width = 50 }, srcBox,
                        new TextBlock { Text = "Dest.", VerticalAlignment = VerticalAlignment.Center, Width = 40 }, dstBox,
                    },
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8, Margin = new Avalonia.Thickness(0, 8, 0, 0), Children = { ok, cancel },
                },
            },
        };
        blendDialog.Closed += (_, _) => result.TrySetResult(null);

        blendDialog.ShowDialog(owner);
        return result.Task;
    }

    public enum PaletteExportFormat { Act, Pal, Png }

    /// <summary>Companion format for Export Palette; .rgba is always written. Null = canceled.</summary>
    public static Task<PaletteExportFormat?> PromptPaletteFormat(Window owner, PaletteExportFormat initial)
    {
        var result = new TaskCompletionSource<PaletteExportFormat?>(TaskCreationOptions.RunContinuationsAsynchronously);

        var act = new RadioButton { GroupName = "PalFormat", Content = ".act (MUGEN order)", IsChecked = initial == PaletteExportFormat.Act };
        var pal = new RadioButton { GroupName = "PalFormat", Content = ".pal (JASC-PAL)", IsChecked = initial == PaletteExportFormat.Pal };
        var png = new RadioButton { GroupName = "PalFormat", Content = ".png (indexed, visual reference)", IsChecked = initial == PaletteExportFormat.Png };

        var dialog = new Window
        {
            Background = ThemeBrush("ThemePanelBg"),
            Foreground = ThemeBrush("ThemeText"),
            Title = "Export Palette",
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false,
        };

        var ok = new Button { Content = "OK", MinWidth = 90, IsDefault = true };
        var cancel = new Button { Content = "Cancel", MinWidth = 90, IsCancel = true };
        ok.Click += (_, _) =>
        {
            result.TrySetResult(png.IsChecked == true ? PaletteExportFormat.Png
                : pal.IsChecked == true ? PaletteExportFormat.Pal : PaletteExportFormat.Act);
            dialog.Close();
        };
        cancel.Click += (_, _) => { result.TrySetResult(null); dialog.Close(); };

        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "Also export as:" },
                act, pal, png,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8, Margin = new Avalonia.Thickness(0, 8, 0, 0), Children = { ok, cancel },
                },
            },
        };
        dialog.Closed += (_, _) => result.TrySetResult(null);

        dialog.ShowDialog(owner);
        return result.Task;
    }

    /// <summary>A frame's Loopstart and the Interpolate lines written above it.</summary>
    /// <summary>Null = mixed across several frames.</summary>
    public sealed record SpecialFlags(bool? LoopStart, bool? Offset, bool? Scale, bool? Angle, bool? Blend);

    /// <summary>Special Flags: checkboxes for the frames' Loopstart and Interpolate lines. Null = canceled.</summary>
    public static Task<SpecialFlags?> PromptSpecialFlags(Window owner, string heading, SpecialFlags initial, bool loopEnabled)
    {
        var result = new TaskCompletionSource<SpecialFlags?>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Loopstart on frame 1 is the same as none, so it can't be set there. A mixed flag can be set back to mixed.
        static CheckBox Flag(string text, bool? value, bool enabled = true) =>
            new() { Content = text, IsChecked = value, IsThreeState = value is null, IsEnabled = enabled };
        var loop = Flag("Loopstart", initial.LoopStart, loopEnabled);
        var offset = Flag("Interpolate Offset", initial.Offset);
        var scale = Flag("Interpolate Scale", initial.Scale);
        var angle = Flag("Interpolate Angle", initial.Angle);
        var blend = Flag("Interpolate Blend", initial.Blend);

        var dialog = new Window
        {
            Background = ThemeBrush("ThemePanelBg"),
            Foreground = ThemeBrush("ThemeText"),
            Title = "Special Flags",
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false,
        };

        var ok = new Button { Content = "OK", MinWidth = 90, IsDefault = true };
        var cancel = new Button { Content = "Cancel", MinWidth = 90, IsCancel = true };
        ok.Click += (_, _) =>
        {
            result.TrySetResult(new SpecialFlags(loop.IsChecked, offset.IsChecked, scale.IsChecked, angle.IsChecked, blend.IsChecked));
            dialog.Close();
        };
        cancel.Click += (_, _) => { result.TrySetResult(null); dialog.Close(); };

        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = heading },
                loop, offset, scale, angle, blend,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8, Margin = new Avalonia.Thickness(0, 8, 0, 0), Children = { ok, cancel },
                },
            },
        };
        dialog.Closed += (_, _) => result.TrySetResult(null);

        dialog.ShowDialog(owner);
        return result.Task;
    }

    public sealed record PreferencesValues(bool SwitchTabOnOpen, bool SyncSprite, string ThemeName, int BackupCount,
        double InfiniteAnimationTimeout, double SilenceThreshold, bool DebugMode,
        double FontScalePercent, double ButtonScalePercent, double ButtonGap, double PanelSeparatorWidth,
        double PanelContentInset, CanvasValues Canvas);

    /// <summary>Preferences > Canvas. Colors are #RRGGBB; null BaseColor = theme, null CheckerColor = auto from the base.</summary>
    public sealed record CanvasValues(BackgroundStyle Style, string? BaseColor, string? CheckerColor,
        int CheckerSize,
        string? BackgroundPath, bool Tile, int OffsetX, int OffsetY, double OpacityPercent,
        bool FollowZoom, bool FollowPan,
        bool CrosshairImageStyle, string Color, double ArmLength, double Thickness,
        string? CrosshairImagePath, int HotspotX, int HotspotY, bool ScaleWithZoom);

    /// <summary>Ranges the Canvas tab offers (settings.json).</summary>
    public sealed record CanvasLimits(double ArmMin, double ArmMax, double ThicknessMin, double ThicknessMax, int OffsetMax);

    /// <summary>
    /// Settings > Preferences. Visual changes go to preview as they're made
    /// (Debug mode excepted); OK returns the values, Cancel or closing returns
    /// null and the caller reverts. An emptied number box keeps its initial value.
    /// Each tab's Reset to Defaults fills that tab from defaults (still undone by
    /// Cancel); the Background tab keeps its chosen Style.
    /// </summary>
    public static Task<PreferencesValues?> PromptPreferences(Window owner, PreferencesValues initial, PreferencesValues defaults,
        IReadOnlyList<string> themeNames,
        double minScale, double maxScale, CanvasLimits limits, Func<Window, string, string?, Task<string?>> pickImage,
        Func<Color> themeCanvasColor, Action<PreferencesValues> preview)
    {
        var result = new TaskCompletionSource<PreferencesValues?>(TaskCreationOptions.RunContinuationsAsynchronously);

        var switchTab = new CheckBox { Content = "Switch to the relevant tab when opening a file", IsChecked = initial.SwitchTabOnOpen };
        var syncSprite = new CheckBox { Content = "Same sprite on the Sprites, Animations and Palettes tabs", IsChecked = initial.SyncSprite };
        ToolTip.SetTip(syncSprite, "Keeps the sprite in sync when switching tabs.");
        var theme = new ComboBox
        {
            ItemsSource = themeNames.Select(n => n == Themes.Fallback.Name ? n + " (default)" : n).ToList(),
            MinWidth = 180,
        };
        int themeAt = themeNames.ToList().FindIndex(n => n.Equals(initial.ThemeName, StringComparison.OrdinalIgnoreCase));
        theme.SelectedIndex = themeAt >= 0 ? themeAt : Math.Max(0, themeNames.ToList().IndexOf(Themes.Fallback.Name));

        NumericUpDown Number(double value, double min, double increment, string format, double? max = null)
        {
            var n = new NumericUpDown
            {
                Value = (decimal)value, Minimum = (decimal)min, Increment = (decimal)increment,
                FormatString = format, Width = 120,
            };
            if (max is { } m) n.Maximum = (decimal)m;
            return n;
        }
        var backupCount = Number(initial.BackupCount, 0, 1, "0", Settings.DefaultBackupCountMax);
        ToolTip.SetTip(backupCount, "Keeps this many previous versions when saving over a file.");
        var debug = new CheckBox { Content = "Debug mode", IsChecked = initial.DebugMode };
        ToolTip.SetTip(debug, "Lifts limits such as the group/number and axis ranges, for testing the engine.");
        var fontScale = Number(initial.FontScalePercent, minScale, 5, "0", maxScale);
        var buttonScale = Number(initial.ButtonScalePercent, minScale, 5, "0", maxScale);
        var buttonGap = Number(initial.ButtonGap, 0, 1, "0", Settings.DefaultButtonGapMax);
        ToolTip.SetTip(buttonGap, "Space between buttons.");
        var panelSeparatorWidth = Number(initial.PanelSeparatorWidth, 1, 1, "0", Settings.DefaultPanelSeparatorWidthMax);
        ToolTip.SetTip(panelSeparatorWidth, "Sets the visible thickness of panel separators.");
        var panelContentInset = Number(initial.PanelContentInset, 0, 1, "0", Settings.DefaultPanelContentInsetMax);
        ToolTip.SetTip(panelContentInset, "Sets the standard inset inside panels.");

        TextBlock Section(string text)
        {
            var t = new TextBlock { Text = text, FontWeight = FontWeight.Bold, Margin = new Avalonia.Thickness(0, 6, 0, 0) };
            t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable("ThemeMutedText")); // follows the theme preview
            return t;
        }
        Grid Row(string label, Control control)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("230,Auto") };
            var t = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(control, 1);
            g.Children.Add(t);
            g.Children.Add(control);
            return g;
        }

        var dialog = new Window
        {
            Title = "Preferences",
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false,
        };
        dialog.Bind(Window.BackgroundProperty, dialog.GetResourceObservable("ThemePanelBg"));
        dialog.Bind(Window.ForegroundProperty, dialog.GetResourceObservable("ThemeText"));

        bool ready = false;
        void Changed() { if (ready) preview(Current()); }

        // Pre-assigned: Changed (and so Current) is handed to BuildCanvasTabs before it returns.
        Func<CanvasValues> canvasValues = () => initial.Canvas;
        Control backgroundTab;
        Control crosshairTab;
        Action<CanvasValues> setBackground;
        Action<CanvasValues> setCrosshair;
        (backgroundTab, crosshairTab, canvasValues, setBackground, setCrosshair) = BuildCanvasTabs(
            dialog, initial.Canvas, limits, pickImage, themeCanvasColor, Changed, Number, Section, Row);

        PreferencesValues Current()
        {
            int ti = theme.SelectedIndex;
            return new PreferencesValues(
                switchTab.IsChecked == true,
                syncSprite.IsChecked == true,
                ti >= 0 && ti < themeNames.Count ? themeNames[ti] : initial.ThemeName,
                (int)(backupCount.Value ?? initial.BackupCount),
                // Not offered on this tab: settings.json only. Nothing here edits them,
                // so they pass through untouched.
                initial.InfiniteAnimationTimeout,
                initial.SilenceThreshold,
                debug.IsChecked == true,
                fontScale.Value is { } fs ? (double)fs : initial.FontScalePercent,
                buttonScale.Value is { } bs ? (double)bs : initial.ButtonScalePercent,
                buttonGap.Value is { } bg ? (double)bg : initial.ButtonGap,
                panelSeparatorWidth.Value is { } psm ? (double)psm : initial.PanelSeparatorWidth,
                panelContentInset.Value is { } pci ? (double)pci : initial.PanelContentInset,
                canvasValues());
        }

        theme.SelectionChanged += (_, _) => Changed();
        fontScale.ValueChanged += (_, _) => Changed();
        buttonScale.ValueChanged += (_, _) => Changed();
        buttonGap.ValueChanged += (_, _) => Changed();
        panelSeparatorWidth.ValueChanged += (_, _) => Changed();
        panelContentInset.ValueChanged += (_, _) => Changed();

        var ok = new Button { Content = "OK", MinWidth = 90, IsDefault = true };
        var cancel = new Button { Content = "Cancel", MinWidth = 90 };
        ok.Click += (_, _) => { result.TrySetResult(Current()); dialog.Close(); };
        cancel.Click += (_, _) => { result.TrySetResult(null); dialog.Close(); };

        // Fills controls without a preview per control, then previews once.
        void ResetTab(Action fill)
        {
            ready = false;
            try { fill(); }
            finally { ready = true; }
            Changed();
        }
        Button ResetButton(Action fill)
        {
            var b = new Button { Content = "Reset to Defaults", Margin = new Avalonia.Thickness(0, 10, 0, 0) };
            ToolTip.SetTip(b, "Puts this tab back to its defaults. Cancel still undoes it.");
            b.Click += (_, _) => ResetTab(fill);
            return b;
        }
        var resetGeneral = ResetButton(() =>
        {
            var d = defaults;
            switchTab.IsChecked = d.SwitchTabOnOpen;
            syncSprite.IsChecked = d.SyncSprite;
            backupCount.Value = d.BackupCount;
            debug.IsChecked = d.DebugMode;
        });
        var resetAppearance = ResetButton(() =>
        {
            var d = defaults;
            int at = themeNames.ToList().FindIndex(n => n.Equals(d.ThemeName, StringComparison.OrdinalIgnoreCase));
            if (at >= 0) theme.SelectedIndex = at;
            fontScale.Value = (decimal)Math.Clamp(d.FontScalePercent, minScale, maxScale);
            buttonScale.Value = (decimal)Math.Clamp(d.ButtonScalePercent, minScale, maxScale);
            buttonGap.Value = (decimal)Math.Clamp(d.ButtonGap, 0, Settings.DefaultButtonGapMax);
            panelSeparatorWidth.Value = (decimal)Math.Clamp(d.PanelSeparatorWidth, 1, Settings.DefaultPanelSeparatorWidthMax);
            panelContentInset.Value = (decimal)Math.Clamp(d.PanelContentInset, 0, Settings.DefaultPanelContentInsetMax);
        });
        var resetBackground = ResetButton(() => setBackground(defaults.Canvas));
        var resetCrosshair = ResetButton(() => setCrosshair(defaults.Canvas));

        var general = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Section("Files"),
                switchTab,
                syncSprite,
                Section("Saving"),
                Row($"Backup versions (0-{Settings.DefaultBackupCountMax})", backupCount),
                Section("Debug"),
                debug,
                resetGeneral,
            },
        };
        var appearance = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Section("Interface"),
                Row("Theme", theme),
                Row($"Font size % ({minScale:0}-{maxScale:0})", fontScale),
                Row($"Button size % ({minScale:0}-{maxScale:0})", buttonScale),
                Row($"Button gap (0-{Settings.DefaultButtonGapMax:0})", buttonGap),
                Row($"Panel separator width (1-{Settings.DefaultPanelSeparatorWidthMax:0})", panelSeparatorWidth),
                Row($"Panel content inset (0-{Settings.DefaultPanelContentInsetMax:0})", panelContentInset),
                resetAppearance,
            },
        };

        TabItem Tab(string header, Control content)
        {
            var t = new TabItem { Header = header, Content = content }; // look comes from App.axaml
            return t;
        }

        var backgroundPage = new StackPanel { Children = { backgroundTab, resetBackground } };
        var crosshairPage = new StackPanel { Children = { crosshairTab, resetCrosshair } };
        var pages = new Control[] { general, appearance, backgroundPage, crosshairPage };
        // Size every tab to the largest. The pages must be measured in the dialog's
        // visual tree: on their own their control templates and styles are not
        // applied, so they report a smaller size and the tabs clip. A Grid stacks
        // them in one cell, so its desired size is the largest page.
        var measureHost = new Grid();
        foreach (var page in pages) measureHost.Children.Add(page);
        dialog.Content = measureHost;
        measureHost.Measure(Avalonia.Size.Infinity);
        double pageWidth = measureHost.DesiredSize.Width;
        double pageHeight = measureHost.DesiredSize.Height;
        foreach (var page in pages) measureHost.Children.Remove(page);
        foreach (var page in pages)
        {
            page.Width = pageWidth;
            page.Height = pageHeight;
        }
        var preferenceTabs = new TabControl
        {
            MinWidth = pageWidth,
            Items =
            {
                Tab("General", general),
                Tab("Appearance", appearance),
                Tab("Background", backgroundPage),
                Tab("Crosshair", crosshairPage),
            },
        };

        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 8,
            Children =
            {
                preferenceTabs,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8, Margin = new Avalonia.Thickness(0, 10, 0, 0), Children = { ok, cancel },
                },
            },
        };
        dialog.Closed += (_, _) => result.TrySetResult(null);

        ready = true;
        dialog.ShowDialog(owner);
        return result.Task;
    }

    /// <summary>A #RRGGBB box and a swatch button that opens the color picker. An invalid hex keeps the last good color.</summary>
    private sealed class ColorPicker
    {
        private readonly TextBox _hex = new() { Width = 90 };
        private readonly Border _swatch = new()
        {
            Width = 22, Height = 22, BorderBrush = Brushes.Gray, BorderThickness = new Avalonia.Thickness(1),
        };
        private readonly Button _swatchButton = new() { Padding = new Avalonia.Thickness(2), VerticalAlignment = VerticalAlignment.Center };
        private bool _syncing;

        public StackPanel View { get; }
        public Color Color { get; private set; }
        /// <summary>Raised on user edits only, not on Set.</summary>
        public event Action? Changed;

        public ColorPicker(Color initial)
        {
            View = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _swatchButton, _hex } };
            _swatchButton.Content = _swatch;
            ToolTip.SetTip(_swatchButton, "Opens the color picker.");
            _swatchButton.Click += (_, _) =>
            {
                var start = Color;
                ColorPickerFlyout.Open(_swatchButton, start, alpha: false,
                    c => { Show(c, updateHex: true); Changed?.Invoke(); },
                    () => { },
                    () => { Show(start, updateHex: true); Changed?.Invoke(); });
            };
            Set(initial);
            _hex.TextChanged += (_, _) =>
            {
                if (_syncing) return;
                var t = (_hex.Text ?? "").Trim();
                if (!t.StartsWith('#')) t = "#" + t;
                if (t.Length != 7 || !Color.TryParse(t, out var c)) return;
                Show(c, updateHex: false);
                Changed?.Invoke();
            };
            _hex.LostFocus += (_, _) => Show(Color, updateHex: true);
        }

        public void Set(Color c) => Show(c, updateHex: true);

        private void Show(Color c, bool updateHex)
        {
            _syncing = true;
            Color = Color.FromRgb(c.R, c.G, c.B);
            if (updateHex) _hex.Text = Hex(Color);
            _swatch.Background = new SolidColorBrush(Color);
            _syncing = false;
        }
    }

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>
    /// Preferences > Canvas. The Background tab shows only the rows its style
    /// uses: the checker rows only for Checkerboard, the image rows only for
    /// Image. Fields that still don't apply dim: a color picker while it's on
    /// Theme/Auto, image fields until an image is chosen, arm
    /// length for the Image crosshair, the crosshair image fields for Lines.
    /// Crosshair color and thickness stay live in both styles (the guides use them).
    /// </summary>
    private static (Control BackgroundTab, Control CrosshairTab, Func<CanvasValues> Values,
        Action<CanvasValues> SetBackground, Action<CanvasValues> SetCrosshair) BuildCanvasTabs(Window dialog, CanvasValues init,
        CanvasLimits limits, Func<Window, string, string?, Task<string?>> pickImage, Func<Color> themeColor, Action changed,
        Func<double, double, double, string, double?, NumericUpDown> number,
        Func<string, TextBlock> section, Func<string, Control, Grid> row)
    {
        Color? Parse(string? s) => s is not null && Color.TryParse(s, out var c) ? Color.FromRgb(c.R, c.G, c.B) : null;
        TextBlock NameBlock() => new() { VerticalAlignment = VerticalAlignment.Center, MinWidth = 140, MaxWidth = 220, TextTrimming = TextTrimming.CharacterEllipsis };
        TextBlock Label(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Avalonia.Thickness(6, 0, 0, 0) };
        StackPanel Inline(params Control[] cs)
        {
            var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            foreach (var c in cs) p.Children.Add(c);
            return p;
        }

        // ---- background ----
        var flatR = new RadioButton { GroupName = "BgStyle", Content = "Flat", IsChecked = init.Style == BackgroundStyle.Flat };
        var checkerR = new RadioButton { GroupName = "BgStyle", Content = "Checkerboard", IsChecked = init.Style == BackgroundStyle.Checkerboard };
        var imageR = new RadioButton { GroupName = "BgStyle", Content = "Image", IsChecked = init.Style == BackgroundStyle.Image };
        ToolTip.SetTip(imageR, "Shows Flat when there is no image.");
        BackgroundStyle SelectedStyle() =>
            imageR.IsChecked == true ? BackgroundStyle.Image
            : checkerR.IsChecked == true ? BackgroundStyle.Checkerboard
            : BackgroundStyle.Flat;

        var baseMode = new ComboBox { ItemsSource = new[] { "Theme", "Custom" }, SelectedIndex = init.BaseColor is null ? 0 : 1, MinWidth = 100 };
        var basePicker = new ColorPicker(Parse(init.BaseColor) ?? themeColor());
        bool baseTouched = init.BaseColor is not null;
        Color EffectiveBase() => baseMode.SelectedIndex == 1 ? basePicker.Color : themeColor();

        var checkerMode = new ComboBox { ItemsSource = new[] { "Auto", "Custom" }, SelectedIndex = init.CheckerColor is null ? 0 : 1, MinWidth = 100 };
        var checkerPicker = new ColorPicker(Parse(init.CheckerColor) ?? CanvasColors.CheckerFor(EffectiveBase()));
        bool checkerTouched = init.CheckerColor is not null;
        var checkerSize = number(Math.Clamp(init.CheckerSize, Settings.CheckerSizeMin, Settings.CheckerSizeMax),
            Settings.CheckerSizeMin, 1, "0", Settings.CheckerSizeMax);
        ToolTip.SetTip(checkerSize, "In screen pixels; the checkerboard keeps this size as the canvas zooms.");

        string? bgPath = init.BackgroundPath;
        // Browse opens in the shipped patterns folder the first time; afterward it follows the
        // remembered folder, and Reset to Defaults (SetBackground) puts it back there.
        string? bgStartFolder = bgPath is null ? CanvasPatterns.Folder : null;
        var bgName = NameBlock();
        var bgBrowse = new Button { Content = "Browse" };
        var bgClear = new Button { Content = "Clear" };
        var tile = new CheckBox { Content = "Tile", IsChecked = init.Tile };
        ToolTip.SetTip(tile, "Repeats the image to fill the canvas.");
        var offX = number(init.OffsetX, -limits.OffsetMax, 1, "0", limits.OffsetMax);
        var offY = number(init.OffsetY, -limits.OffsetMax, 1, "0", limits.OffsetMax);
        ToolTip.SetTip(offX, "Moves the image in pixels at 100% zoom.");
        ToolTip.SetTip(offY, "Moves the image in pixels at 100% zoom.");
        var opacity = new Slider
        {
            Minimum = 0, Maximum = 100, Value = Math.Clamp(init.OpacityPercent, 0, 100),
            SmallChange = 1, LargeChange = 10, TickFrequency = 1, IsSnapToTickEnabled = true, Width = 160,
        };
        var opacityText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MinWidth = 30, Text = $"{opacity.Value:0}" };
        var followZoom = new CheckBox { Content = "Scale with zoom", IsChecked = init.FollowZoom };
        ToolTip.SetTip(followZoom, "Scales the image with the zoom.");
        var followPan = new CheckBox { Content = "Follow pan", IsChecked = init.FollowPan };
        ToolTip.SetTip(followPan, "Moves the image with the view when panning.");

        // Rows are shown only for the styles they apply to; SyncBackground toggles them.
        var checkerColorRow = row("Checker color", Inline(checkerMode, checkerPicker.View));
        var checkerSizeRow = row("Checker size", checkerSize);
        var imageRow = row("Image", Inline(bgName, bgBrowse, bgClear));
        var offsetRow = row("Offset", Inline(Label("X"), offX, Label("Y"), offY));
        var opacityRow = row("Opacity %", Inline(opacity, opacityText));
        var imageBoolRow = Inline(tile, followZoom, followPan);

        void SyncBackground()
        {
            var current = SelectedStyle();
            bool checker = current == BackgroundStyle.Checkerboard;
            bool image = current == BackgroundStyle.Image;

            basePicker.View.IsEnabled = baseMode.SelectedIndex == 1;
            checkerColorRow.IsVisible = checkerSizeRow.IsVisible = checker;
            checkerPicker.View.IsEnabled = checkerMode.SelectedIndex == 1;

            imageRow.IsVisible = offsetRow.IsVisible = opacityRow.IsVisible = imageBoolRow.IsVisible = image;
            bgName.Text = bgPath is null ? "None" : Path.GetFileName(bgPath);
            ToolTip.SetTip(bgName, bgPath);
            bgClear.IsEnabled = bgPath is not null;
            bool live = bgPath is not null;
            foreach (var c in new Control[] { tile, offX, offY, opacity, opacityText, followZoom, followPan }) c.IsEnabled = live;
        }

        flatR.IsCheckedChanged += (_, _) => { SyncBackground(); changed(); };
        checkerR.IsCheckedChanged += (_, _) => { SyncBackground(); changed(); };
        imageR.IsCheckedChanged += (_, _) => { SyncBackground(); changed(); };
        baseMode.SelectionChanged += (_, _) =>
        {
            // First switch to Custom starts from the current theme color.
            if (baseMode.SelectedIndex == 1 && !baseTouched) { basePicker.Set(themeColor()); baseTouched = true; }
            SyncBackground(); changed();
        };
        basePicker.Changed += () => { baseTouched = true; changed(); };
        checkerMode.SelectionChanged += (_, _) =>
        {
            if (checkerMode.SelectedIndex == 1 && !checkerTouched) { checkerPicker.Set(CanvasColors.CheckerFor(EffectiveBase())); checkerTouched = true; }
            SyncBackground(); changed();
        };
        checkerPicker.Changed += () => { checkerTouched = true; changed(); };
        checkerSize.ValueChanged += (_, _) => changed();
        tile.IsCheckedChanged += (_, _) => changed();
        offX.ValueChanged += (_, _) => changed();
        offY.ValueChanged += (_, _) => changed();
        opacity.PropertyChanged += (_, e) =>
        {
            if (e.Property != Slider.ValueProperty) return;
            opacityText.Text = $"{opacity.Value:0}";
            changed();
        };
        followZoom.IsCheckedChanged += (_, _) => changed();
        followPan.IsCheckedChanged += (_, _) => changed();
        bgClear.Click += (_, _) => { bgPath = null; SyncBackground(); changed(); };
        bgBrowse.Click += async (_, _) =>
        {
            try
            {
                if (await pickImage(dialog, "Choose background image", bgStartFolder) is not { } p) return;
                ImageFile.Load(p).Dispose(); // reject unreadable files now, not at the next start
                bgPath = p;
                bgStartFolder = null; // later browses use the remembered folder
                SyncBackground();
                changed();
            }
            catch (Exception ex)
            {
                CrashLog.Write("Preferences.BackgroundBrowse", ex);
                await ShowMessage(dialog, "Could Not Open Image", ex.Message);
            }
        };

        // ---- crosshair ----
        var chLines = new RadioButton { GroupName = "CrosshairStyle", Content = "Lines", IsChecked = !init.CrosshairImageStyle };
        var chImage = new RadioButton { GroupName = "CrosshairStyle", Content = "Image", IsChecked = init.CrosshairImageStyle };
        var chPicker = new ColorPicker(Parse(init.Color) ?? CrosshairStyle.DefaultColor);
        ToolTip.SetTip(chPicker.View, "Also used by the axis guides (Ctrl+G).");
        var arm = number(Math.Clamp(init.ArmLength, limits.ArmMin, limits.ArmMax), limits.ArmMin, 1, "0.##", limits.ArmMax);
        var thick = number(Math.Clamp(init.Thickness, limits.ThicknessMin, limits.ThicknessMax), limits.ThicknessMin, 1, "0.##", limits.ThicknessMax);
        ToolTip.SetTip(thick, "Also used by the axis guides (Ctrl+G).");

        string? chPath = init.CrosshairImagePath;
        var chName = NameBlock();
        var chBrowse = new Button { Content = "Browse" };
        var chClear = new Button { Content = "Clear" };
        var hotX = number(Math.Max(0, init.HotspotX), 0, 1, "0", null);
        var hotY = number(Math.Max(0, init.HotspotY), 0, 1, "0", null);
        ToolTip.SetTip(hotX, "The image pixel placed on the axis.");
        ToolTip.SetTip(hotY, "The image pixel placed on the axis.");
        void SetHotspotRange(int w, int h)
        {
            hotX.Maximum = Math.Max(0, w - 1);
            hotY.Maximum = Math.Max(0, h - 1);
        }
        if (chPath is not null)
        {
            try { using var bmp = ImageFile.Load(chPath); SetHotspotRange(bmp.PixelSize.Width, bmp.PixelSize.Height); }
            catch (Exception ex) { CrashLog.Write("Preferences.CrosshairImage", ex); }
        }
        var scale = new CheckBox { Content = "Scale with zoom", IsChecked = init.ScaleWithZoom };
        ToolTip.SetTip(scale, "Scales the crosshair with the zoom.");

        void SyncCrosshair()
        {
            chName.Text = chPath is null ? "None" : Path.GetFileName(chPath);
            ToolTip.SetTip(chName, chPath);
            bool image = chImage.IsChecked == true;
            arm.IsEnabled = !image;
            chName.IsEnabled = chBrowse.IsEnabled = image;
            chClear.IsEnabled = image && chPath is not null;
            hotX.IsEnabled = hotY.IsEnabled = image && chPath is not null;
        }
        chLines.IsCheckedChanged += (_, _) => { SyncCrosshair(); changed(); };
        chImage.IsCheckedChanged += (_, _) => { SyncCrosshair(); changed(); };
        chPicker.Changed += changed;
        arm.ValueChanged += (_, _) => changed();
        thick.ValueChanged += (_, _) => changed();
        hotX.ValueChanged += (_, _) => changed();
        hotY.ValueChanged += (_, _) => changed();
        scale.IsCheckedChanged += (_, _) => changed();
        chClear.Click += (_, _) => { chPath = null; SyncCrosshair(); changed(); };
        chBrowse.Click += async (_, _) =>
        {
            try
            {
                if (await pickImage(dialog, "Choose crosshair image", null) is not { } p) return;
                int w, h;
                using (var bmp = ImageFile.Load(p)) (w, h) = (bmp.PixelSize.Width, bmp.PixelSize.Height);
                chPath = p;
                SetHotspotRange(w, h);
                hotX.Value = w / 2; // new image: hotspot starts at its center
                hotY.Value = h / 2;
                SyncCrosshair();
                changed();
            }
            catch (Exception ex)
            {
                CrashLog.Write("Preferences.CrosshairBrowse", ex);
                await ShowMessage(dialog, "Could Not Open Image", ex.Message);
            }
        };

        SyncBackground();
        SyncCrosshair();

        var backgroundTab = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                section("Style"),
                Inline(flatR, checkerR, imageR),
                section("Settings"),
                row("Base color", Inline(baseMode, basePicker.View)),
                checkerColorRow,
                checkerSizeRow,
                imageRow,
                offsetRow,
                opacityRow,
                imageBoolRow,
            },
        };
        var crosshairTab = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                section("Style"),
                Inline(chLines, chImage),
                section("Crosshair"),
                row("Color", chPicker.View),
                row($"Arm length ({limits.ArmMin:0.##}-{limits.ArmMax:0.##})",
                    Inline(arm, Label($"Thickness ({limits.ThicknessMin:0.##}-{limits.ThicknessMax:0.##})"), thick)),
                row("Image", Inline(chName, chBrowse, chClear)),
                row("Hotspot", Inline(Label("X"), hotX, Label("Y"), hotY)),
                scale,
            },
        };

        CanvasValues Values() => new(
            SelectedStyle(),
            baseMode.SelectedIndex == 1 ? Hex(basePicker.Color) : null,
            checkerMode.SelectedIndex == 1 ? Hex(checkerPicker.Color) : null,
            checkerSize.Value is { } cs ? (int)cs : init.CheckerSize,
            bgPath, tile.IsChecked == true,
            offX.Value is { } ox ? (int)ox : init.OffsetX,
            offY.Value is { } oy ? (int)oy : init.OffsetY,
            opacity.Value, followZoom.IsChecked == true, followPan.IsChecked == true,
            chImage.IsChecked == true, Hex(chPicker.Color),
            arm.Value is { } a ? (double)a : init.ArmLength,
            thick.Value is { } t ? (double)t : init.Thickness,
            chPath,
            hotX.Value is { } hx ? (int)hx : init.HotspotX,
            hotY.Value is { } hy ? (int)hy : init.HotspotY,
            scale.IsChecked == true);

        void SetBackground(CanvasValues v)
        {
            // Reset to Defaults keeps the chosen Style and fills in the rest.
            baseTouched = v.BaseColor is not null;
            checkerTouched = v.CheckerColor is not null;
            baseMode.SelectedIndex = v.BaseColor is null ? 0 : 1;
            basePicker.Set(Parse(v.BaseColor) ?? themeColor());
            checkerMode.SelectedIndex = v.CheckerColor is null ? 0 : 1;
            checkerPicker.Set(Parse(v.CheckerColor) ?? CanvasColors.CheckerFor(EffectiveBase()));
            checkerSize.Value = Math.Clamp(v.CheckerSize, Settings.CheckerSizeMin, Settings.CheckerSizeMax);
            bgPath = v.BackgroundPath;
            bgStartFolder = CanvasPatterns.Folder; // Reset to Defaults returns to the shipped patterns
            tile.IsChecked = v.Tile;
            offX.Value = v.OffsetX;
            offY.Value = v.OffsetY;
            opacity.Value = Math.Clamp(v.OpacityPercent, 0, 100);
            followZoom.IsChecked = v.FollowZoom;
            followPan.IsChecked = v.FollowPan;
            SyncBackground();
        }

        void SetCrosshair(CanvasValues v)
        {
            chImage.IsChecked = v.CrosshairImageStyle;
            chLines.IsChecked = !v.CrosshairImageStyle;
            chPicker.Set(Parse(v.Color) ?? CrosshairStyle.DefaultColor);
            arm.Value = (decimal)Math.Clamp(v.ArmLength, limits.ArmMin, limits.ArmMax);
            thick.Value = (decimal)Math.Clamp(v.Thickness, limits.ThicknessMin, limits.ThicknessMax);
            chPath = v.CrosshairImagePath;
            hotX.Value = Math.Max(0, v.HotspotX);
            hotY.Value = Math.Max(0, v.HotspotY);
            scale.IsChecked = v.ScaleWithZoom;
            SyncCrosshair();
        }

        return (backgroundTab, crosshairTab, Values, SetBackground, SetCrosshair);
    }

    private static Task<T> Show<T>(Window owner, string title, string message,
        (string Label, T Value)[] buttons, T canceled = default!)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };

        var dialog = new Window
        {
            Background = ThemeBrush("ThemePanelBg"),
            Foreground = ThemeBrush("ThemeText"),
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false,
        };

        foreach (var (label, value) in buttons)
        {
            var b = new Button { Content = label, MinWidth = 90, IsDefault = buttons[0].Label == label };
            b.Click += (_, _) => { result.TrySetResult(value); dialog.Close(); };
            row.Children.Add(b);
        }

        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 18,
            Children =
            {
                new TextBlock { Text = message, MaxWidth = 420, TextWrapping = TextWrapping.Wrap },
                row,
            },
        };
        dialog.Closed += (_, _) => result.TrySetResult(canceled);

        dialog.ShowDialog(owner);
        return result.Task;
    }
}
