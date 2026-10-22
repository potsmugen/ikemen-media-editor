using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace Editor;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// The previous attempt closed the splash on MainWindow.Opened, which
    /// never worked: Avalonia's classic desktop lifetime shows whatever is
    /// assigned to desktop.MainWindow itself, immediately once this method
    /// returns — so MainWindow always appeared straight away regardless of
    /// the splash's own timing. The fix (confirmed against Avalonia's own
    /// splash-screen examples) is to make the splash itself desktop.MainWindow
    /// first, so the framework's own auto-show displays *it*, then swap in
    /// the real window afterwards.
    /// </summary>
    public override async void OnFrameworkInitializationCompleted()
    {
        try
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var splash = new SplashWindow();
                desktop.MainWindow = splash;
                splash.Show();
                var shown = Stopwatch.StartNew();

                var startup = Settings.Load();
                bool recoveryInterrupted = RecoveryStore.BeginRun(desktop.Args?.Contains("--test-recovery", StringComparer.Ordinal) == true);
                Sff.ImageImport.TransparentAlphaThreshold = Settings.Checked(
                    nameof(Settings.NearestColorAlphaThreshold), startup.NearestColorAlphaThreshold,
                    Settings.DefaultNearestColorAlphaThreshold, v => v is >= 0 and <= 255);
                Sff.SheetImport.MinimumSize = Settings.Checked(
                    nameof(Settings.SheetMinimumSizePixels), startup.SheetMinimumSizePixels,
                    Settings.DefaultSheetMinimumSizePixels, v => v is >= 0 and <= 4096);
                int minimum = Settings.Checked(nameof(Settings.SplashMilliseconds), startup.SplashMilliseconds,
                    Settings.DefaultSplashMilliseconds, v => v >= 0);

                // Let the splash paint before building the main window blocks the UI thread.
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                var window = new MainWindow { RecoveryTestMode = desktop.Args?.Contains("--test-recovery", StringComparer.Ordinal) == true };
                window.RecoveryInterrupted = recoveryInterrupted;

                // Closes only once the minimum duration has elapsed and the main window is up.
                var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                window.Opened += (_, _) => opened.TrySetResult();
                window.ShowActivated = false;
                desktop.MainWindow = window;
                window.Show();
                var ownedSplash = new SplashWindow();
                ownedSplash.Show(window);
                splash.Close();
                splash = ownedSplash;
                window.Closed += (_, _) =>
                {
                    if (splash.IsVisible) splash.Close();
                };
                await opened.Task;

                var left = TimeSpan.FromMilliseconds(minimum) - shown.Elapsed;
                if (left > TimeSpan.Zero) await Task.Delay(left);
                splash.Close();
                window.Activate();

                // Recovery is always offered first. The launch file opens only if the recovered session was not restored.
                bool recovered = await window.OfferRecovery();

                string? startupPath = desktop.Args?.FirstOrDefault(a => a != "--test-recovery");
                if (startupPath is not null && !recovered)
                    await window.OpenLaunchFile(startupPath);
                if (window.RecoveryTestMode) window.CaptureRecoveryForTest();
            }
            base.OnFrameworkInitializationCompleted();
        }
        catch (Exception ex)
        {
            CrashLog.Write("OnFrameworkInitializationCompleted", ex);
            Environment.Exit(1); // no window came up; don't leave a headless process behind
        }
    }
}
