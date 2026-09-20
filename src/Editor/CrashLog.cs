using System.Threading.Tasks;

namespace Editor;

/// <summary>
/// Logs unhandled exceptions to a stable, append-only file before the process
/// goes down. Modern .NET terminates the process on any unhandled exception
/// regardless of which thread it came from — that part cannot be prevented.
/// What this buys is the difference between the app silently vanishing and
/// leaving a file that says exactly what happened and when, which is what
/// "catch the panics and log them" actually means in a .NET app: there is no
/// generic recover-and-continue, only guaranteeing nothing is lost on the way
/// down. Handlers that can reasonably continue after a failure — Save, Open,
/// import — already catch their own exceptions and show a dialog instead of
/// relying on this as a first line of defense; this is the backstop under that.
/// </summary>
public static class CrashLog
{
    // Everything in one place, next to the exe: a user reporting a problem
    // grabs the folder the app lives in, not a per-OS convention they have to
    // be told where to find. GetFolderPath can return "" on some Linux setups
    // (no XDG/HOME env); the old fallback logic existed for that, but it
    // guarded a location we no longer use.
    internal static string LogDir { get; } = System.IO.Path.Combine(AppContext.BaseDirectory, "logs");

    public static string Path { get; } = System.IO.Path.Combine(LogDir, "crash.log");

    /// <summary>Call once, as the very first thing in Main.</summary>
    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Write("AppDomain.UnhandledException", e.ExceptionObject as Exception);

        // Covers a Task whose exception was never awaited or observed, which
        // would otherwise surface much later on the finalizer thread, or not
        // at all, with no clear link back to what caused it.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write("TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved();
        };
    }

    public static void Write(string source, Exception? ex) => Append(source, ex?.ToString() ?? "(no exception)");

    /// <summary>Logs a failure that isn't an exception (e.g. an import entry that was skipped).</summary>
    public static void Error(string source, string message) => Append(source, "ERROR: " + message);

    private static readonly object WriteLock = new();

    private static void Append(string source, string details)
    {
        Console.Error.WriteLine($"{source}: {details}");
        lock (WriteLock)
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            System.IO.File.AppendAllText(Path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}\n{details}\n\n");
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[CrashLog] could not write {Path}: {e.Message}");
        }
    }

    // In the same logs folder as crash.log — see LogDir above. This is a
    // temporary debugging aid (see Trace below), so "wherever you just built
    // it" beats a per-OS convention nobody needs to go hunting for.
    public static string TracePath { get; } = System.IO.Path.Combine(LogDir, "trace.log");

    private const long TraceMaxBytes = 256 * 1024;

    /// <summary>
    /// A breadcrumb, not an error — for the failure mode Write can't cover: a
    /// freeze throws no exception, so there is nothing for AppDomain or a
    /// try/catch to report. Written immediately, not buffered, so whatever
    /// was happening right before a hang is still the last thing on disk.
    /// Kept in its own file rather than crash.log, since this is routine
    /// traffic, not a postmortem, and trimmed to roughly its last 256KB so it
    /// never grows without bound. Sprinkled at a few of the more fragile
    /// spots (opening a file, switching the Frames view, committing a Table
    /// or Text edit) rather than everywhere — enough to place where things
    /// stopped, not a full trace of the whole app.
    /// </summary>
    public static void Trace(string message)
    {
        // Console first: this must show up even if the file write below fails.
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
        lock (WriteLock)
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(TracePath)!);
            System.IO.File.AppendAllText(TracePath, $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n");

            var info = new System.IO.FileInfo(TracePath);
            if (info.Exists && info.Length > TraceMaxBytes)
            {
                var lines = System.IO.File.ReadAllLines(TracePath);
                System.IO.File.WriteAllLines(TracePath, lines[(lines.Length / 2)..]);
            }
        }
        catch (Exception e)
        {
            // The write failing must never throw and mask the original message,
            // but must also not vanish silently the way it did before.
            Console.Error.WriteLine($"[CrashLog] could not write {TracePath}: {e.Message}");
        }
    }
}
