using Avalonia.Controls;
using Avalonia.Layout;

namespace Editor;

/// <summary>Convert Color Depth's answer: 32-bit or 8-bit, and for 8-bit whether the
/// sprites share one generated palette instead of getting one each</summary>
public sealed record SpriteDepthChoice(bool TrueColor, bool SharedPalette);

public static partial class Dialogs
{
    /// <summary>Asks for the target depth. paletteChoice adds the per-sprite or shared palette
    /// option, for when several true-color sprites may go to 8-bit. Null = canceled</summary>
    public static Task<SpriteDepthChoice?> PromptSpriteDepth(Window owner, int spriteCount, bool initialTrueColor,
        bool paletteChoice)
    {
        var result = new TaskCompletionSource<SpriteDepthChoice?>(TaskCreationOptions.RunContinuationsAsynchronously);

        var indexed = new RadioButton { GroupName = "SpriteDepth", Content = "8-bit indexed", IsChecked = !initialTrueColor };
        var trueColor = new RadioButton { GroupName = "SpriteDepth", Content = "32-bit color", IsChecked = initialTrueColor };
        ToolTip.SetTip(indexed, "Builds a new palette from the sprite colors.");
        ToolTip.SetTip(trueColor, "Draws each pixel with its palette color and alpha.");
        var shared = new CheckBox { Content = "Share one palette", IsChecked = true };

        var ok = new Button { Content = "Convert", MinWidth = 90, IsDefault = true };
        var cancel = new Button { Content = "Cancel", MinWidth = 90, IsCancel = true };
        var body = new StackPanel { Margin = new Avalonia.Thickness(20), Spacing = 8, MaxWidth = 440 };
        body.Children.Add(new TextBlock
        {
            Text = spriteCount > 1
                ? $"Choose the depth for the {spriteCount} selected sprites."
                : "Choose the depth for this sprite.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });
        body.Children.Add(indexed);
        // Always shown under 8-bit; dimmed with the reason as tooltip when it doesn't apply
        shared.Margin = new Avalonia.Thickness(24, 0, 0, 0);
        body.Children.Add(shared);
        body.Children.Add(trueColor);
        void Update()
        {
            bool on = paletteChoice && indexed.IsChecked == true;
            shared.IsEnabled = on;
            ToolTip.SetTip(shared, on ? "Reduces the colors when the sprites use more than 255 together."
                : paletteChoice ? "Available for 8-bit indexed only." : "Available when several 32-bit sprites are selected.");
        }
        indexed.IsCheckedChanged += (_, _) => Update();
        Update();
        body.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8, Margin = new Avalonia.Thickness(0, 8, 0, 0), Children = { ok, cancel },
        });

        var dialog = new Window
        {
            Background = ThemeBrush("ThemePanelBg"), Foreground = ThemeBrush("ThemeText"),
            Title = "Convert Color Depth", SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false, ShowInTaskbar = false,
            Content = body,
        };
        ok.Click += (_, _) =>
        {
            result.TrySetResult(new(trueColor.IsChecked == true, paletteChoice && shared.IsChecked == true));
            dialog.Close();
        };
        cancel.Click += (_, _) => { result.TrySetResult(null); dialog.Close(); };
        dialog.Closed += (_, _) => result.TrySetResult(null);
        // Focus Convert so Enter converts right away
        dialog.Opened += (_, _) => ok.Focus();

        dialog.ShowModal(owner);
        return result.Task;
    }
}
