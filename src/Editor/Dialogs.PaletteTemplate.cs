using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Editor;

/// <summary>One Create Palette Template preview: the sheet as BGRA (null when there is nothing to show),
/// how many sprites the palette offers, and the lines under the controls.</summary>
public sealed record PaletteTemplatePreview(byte[]? Bgra, int Width, int Height, int Sprites, string Info);

/// <summary>What Create Palette Template exports: palette position in the dropdown, sprite count, spacing and
/// swatch cell size in pixels, and shuffle seed.</summary>
public sealed record PaletteTemplateChoice(int Palette, int Count, int Spacing, int SwatchSize, int Seed);

public static partial class Dialogs
{
    /// <summary>Palette dropdown, sprite count, spacing, swatch size and Shuffle over a live preview; build(choice) makes each one.</summary>
    public static Task<PaletteTemplateChoice?> PromptPaletteTemplate(Window owner, IReadOnlyList<string> palettes,
        int paletteIndex, int count, int spacing, int maxSpacing, int swatchSize, int maxSwatchSize,
        Func<PaletteTemplateChoice, PaletteTemplatePreview> build)
    {
        var result = new TaskCompletionSource<PaletteTemplateChoice?>(TaskCreationOptions.RunContinuationsAsynchronously);
        int seed = Random.Shared.Next();
        bool updating = false;

        var palette = new ComboBox { ItemsSource = palettes, SelectedIndex = paletteIndex, MinWidth = 220 };
        var sprites = new NumericUpDown { Minimum = 1, Maximum = Math.Max(1, count), Increment = 1, FormatString = "0", Width = 100, Value = count };
        var spacingBox = new NumericUpDown { Minimum = 0, Maximum = maxSpacing, Increment = 1, FormatString = "0", Width = 100, Value = spacing };
        ToolTip.SetTip(spacingBox, "Pixels between sprites and around the sheet.");
        var swatchBox = new NumericUpDown { Minimum = 1, Maximum = maxSwatchSize, Increment = 1, FormatString = "0", Width = 100, Value = swatchSize };
        ToolTip.SetTip(swatchBox, "Pixels per palette entry in the swatch grid.");
        var shuffle = new Button { Content = "Shuffle" };
        ToolTip.SetTip(shuffle, "Picks a different set of sprites.");
        var info = new TextBlock { Foreground = ThemeBrush("ThemeMutedText"), TextWrapping = TextWrapping.Wrap };
        var image = new Image { Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapInterpolationMode(image, Avalonia.Media.Imaging.BitmapInterpolationMode.None);
        var preview = new Border
        {
            Width = 560, Height = 360, Background = ThemeBrush("ThemeListBg"),
            BorderBrush = ThemeBrush("ThemeSeparator"), BorderThickness = new Avalonia.Thickness(1),
            Padding = new Avalonia.Thickness(4), Child = image,
        };
        var export = new Button { Content = "Export", MinWidth = 90, IsDefault = true };
        var cancel = new Button { Content = "Cancel", MinWidth = 90 };

        void Update()
        {
            if (updating) return;
            updating = true;
            try
            {
                int requested = (int)(sprites.Value ?? 1);
                var p = build(Current());
                sprites.Maximum = Math.Max(1, p.Sprites);
                sprites.Value = Math.Clamp(requested, 1, Math.Max(1, p.Sprites));
                info.Text = p.Info;
                image.Source = p.Bgra is null ? null : ImageFile.ToBitmap(p.Bgra, p.Width, p.Height);
                export.IsEnabled = shuffle.IsEnabled = sprites.IsEnabled = spacingBox.IsEnabled = swatchBox.IsEnabled = p.Bgra is not null;
            }
            catch (Exception ex)
            {
                CrashLog.Write("PromptPaletteTemplate", ex);
                info.Text = ex.Message;
                image.Source = null;
                export.IsEnabled = false;
            }
            finally { updating = false; }
        }

        PaletteTemplateChoice Current() => new(Math.Max(0, palette.SelectedIndex), (int)(sprites.Value ?? 1),
            (int)(spacingBox.Value ?? 0), (int)(swatchBox.Value ?? 1), seed);

        palette.SelectionChanged += (_, _) => Update();
        spacingBox.ValueChanged += (_, _) => Update();
        swatchBox.ValueChanged += (_, _) => Update();
        sprites.ValueChanged += (_, _) => Update();
        shuffle.Click += (_, _) => { seed = Random.Shared.Next(); Update(); };

        var body = new StackPanel { Spacing = 12, Margin = new Avalonia.Thickness(20) };
        body.Children.Add(new StackPanel { Spacing = 4, Children = { new TextBlock { Text = "Palette" }, palette } });
        body.Children.Add(new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = "Sprites" },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { sprites, shuffle } },
            },
        });
        body.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 16,
            Children =
            {
                new StackPanel { Spacing = 4, Children = { new TextBlock { Text = "Spacing" }, spacingBox } },
                new StackPanel { Spacing = 4, Children = { new TextBlock { Text = "Swatch size" }, swatchBox } },
            },
        });
        body.Children.Add(info);
        body.Children.Add(preview);
        body.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8,
            Children = { export, cancel },
        });
        var dialog = new Window
        {
            Background = ThemeBrush("ThemePanelBg"), Foreground = ThemeBrush("ThemeText"),
            Title = "Create Palette Template", SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false, ShowInTaskbar = false,
            Content = body,
        };
        info.MaxWidth = preview.Width;
        export.Click += (_, _) =>
        {
            result.TrySetResult(Current());
            dialog.Close();
        };
        cancel.Click += (_, _) => { result.TrySetResult(null); dialog.Close(); };
        dialog.Closed += (_, _) => result.TrySetResult(null);
        Update();
        dialog.ShowModal(owner);
        return result.Task;
    }
}
