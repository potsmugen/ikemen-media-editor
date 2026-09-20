using System.Diagnostics;
using Avalonia.Threading;

namespace Editor;

/// <summary>
/// The rule for multi-selection actions. While one runs, the menu and the tabs are
/// disabled, so no other action can start from the mouse, a menu accelerator, or a
/// keyboard shortcut (see OnKeyDown). The status bar stays live and shows
/// "&lt;what&gt; i/n…" progress.
///
/// Only an action that yields between items can be interrupted, so only those take
/// the lock and report progress. A multi-selection action that finishes in one turn
/// — integer-only edits such as Shift Offset and Set Axis — cannot be interrupted and
/// needs neither.
/// </summary>
public partial class MainWindow
{
    private bool _busy;

    /// <summary>
    /// Takes the busy lock for a multi-selection action. Returns false, locking
    /// nothing, when another action already holds it, so the caller returns at once.
    /// Always pair with <see cref="EndBusy"/> in a finally.
    /// </summary>
    private bool TryBeginBusy()
    {
        if (_busy) return false;
        _busy = true;
        MainMenu.IsEnabled = false;
        Tabs.IsEnabled = false;
        return true;
    }

    private void EndBusy()
    {
        if (!_busy) return;
        Tabs.IsEnabled = true;
        MainMenu.IsEnabled = true;
        _busy = false;
    }

    /// <summary>A refusal or failure the user must read. Posted, so sync code can call it
    /// without waiting for the dialog to close.</summary>
    private void ShowPopup(string title, string message) =>
        Dispatcher.UIThread.Post(() => _ = Dialogs.ShowMessage(this, title, message), DispatcherPriority.Background);

    /// <summary>One item of a multi-selection action: status-bar progress, a trace
    /// breadcrumb (so a hang shows which item), and a background-priority yield that
    /// lets the locked window repaint the progress.</summary>
    private async Task BusyStep(string what, int i, int n, string item)
    {
        StatusText.Text = $"{what} {i + 1}/{n}\u2026";
        CrashLog.Trace($"[busy] {what} {i + 1}/{n}: {item}");
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    /// <summary>
    /// Runs a file load or write on a background thread under the busy lock, so the window
    /// keeps painting. The status shows first. Exceptions reach the caller.
    /// </summary>
    private async Task<T> RunFileWorkAsync<T>(string status, Func<T> work)
    {
        if (!TryBeginBusy()) throw new InvalidOperationException("Another action is still running.");
        var sw = Stopwatch.StartNew();
        var ticker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        ticker.Tick += (_, _) => StatusText.Text = $"{status} {sw.Elapsed.TotalSeconds:0.00} s";
        try
        {
            StatusText.Text = status;
            ticker.Start();
            return await Task.Run(work);
        }
        finally
        {
            ticker.Stop();
            EndBusy();
        }
    }

    private Task RunFileWriteAsync(string status, Action work) =>
        RunFileWorkAsync<bool>(status, () => { work(); return true; });
}
