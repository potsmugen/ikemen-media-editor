using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Editor;

/// <summary>
/// A minimal splash shown while the main window is being built. There's no
/// genuinely slow step today, so this mostly gives a "the app is doing
/// something, not stuck" cue for a moment rather than covering real load
/// time — it closes as soon as the main window reports itself opened.
/// </summary>
public sealed class SplashWindow : Window
{
    public SplashWindow()
    {
        Title = "Ikemen Media Editor";
        Width = 360;
        Height = 180;
        CanResize = false;
        ShowInTaskbar = false;
        SystemDecorations = SystemDecorations.None;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(0x1b, 0x1b, 0x22));

        Content = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Spacing = 8,
            Children =
            {
                new TextBlock
                {
                    Text = "Ikemen Media Editor",
                    FontSize = 20,
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
                new TextBlock
                {
                    Text = "Loading...",
                    Foreground = new SolidColorBrush(Color.FromRgb(0xa0, 0xa0, 0xb0)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
            },
        };
    }
}
