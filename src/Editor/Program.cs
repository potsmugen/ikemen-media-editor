using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Runtime.Versioning;
using Avalonia;

namespace Editor;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args is ["--reset-settings"])
        {
            Settings.ResetToDefaults();
            return;
        }

#if DEBUG
        EnsureConsole(); // Release is WinExe and must not open a console window
#endif
        CrashLog.Install();
        UseLibFolder();
        // So there's no hunting for where these landed on a given OS.
        Console.WriteLine($"[CrashLog] logs folder: {CrashLog.LogDir}");
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception e)
        {
            // A windowed build has no console, so a startup crash would otherwise
            // look like the app simply doing nothing.
            CrashLog.Write("Main", e);
            Console.Error.WriteLine($"\nWritten to {CrashLog.Path}");
            throw;
        }
    }

    /// <summary>
    /// A published build keeps its native DLLs (Skia, HarfBuzz, ANGLE) in lib/, so the folder
    /// shows the exe and little else. .NET only looks beside the exe, so failed lookups are
    /// retried in lib/. Dev builds have no lib/ and load as before.
    /// </summary>
    private static void UseLibFolder()
    {
        string lib = Path.Combine(AppContext.BaseDirectory, "lib");
        if (!Directory.Exists(lib)) return;
        if (OperatingSystem.IsMacOS()) ClearQuarantine(lib);

        AssemblyLoadContext.Default.ResolvingUnmanagedDll += (_, name) =>
        {
            foreach (string file in new[] { name, name + ".dll", name + ".so", name + ".dylib" })
            {
                string path = Path.Combine(lib, file);
                if (!File.Exists(path)) continue;
                try { return NativeLibrary.Load(path); }
                catch (Exception ex) { CrashLog.Write($"UseLibFolder({name})", ex); }
            }
            return IntPtr.Zero;
        };

        // Avalonia loads ANGLE with its own LoadLibrary("av_libGLESv2.dll"), which never reaches
        // the event above. Loaded first, it's already in memory, and Windows reuses it by name.
        string angle = Path.Combine(lib, "av_libglesv2.dll");
        if (OperatingSystem.IsWindows() && File.Exists(angle))
        {
            try { NativeLibrary.Load(angle); }
            catch (Exception ex) { CrashLog.Write("UseLibFolder(ANGLE)", ex); }
        }
    }

    /// <summary>
    /// macOS refuses to load a browser-downloaded dylib that isn't notarized ("library load
    /// disallowed by system policy"). The user already approved the exe, so the libraries we
    /// ship beside it get the same trust. Unverified on a Mac; see agents/features.md.
    /// </summary>
    [SupportedOSPlatform("macos")]
    private static void ClearQuarantine(string lib)
    {
        const int ENOATTR = 93; // Not quarantined; nothing to do.
        int cleared = 0;
        try
        {
            foreach (string file in Directory.EnumerateFiles(lib, "*", SearchOption.AllDirectories))
            {
                if (removexattr(file, "com.apple.quarantine", 0) == 0) cleared++;
                else if (Marshal.GetLastPInvokeError() is int err and not ENOATTR)
                    CrashLog.Error("ClearQuarantine", $"{file}: errno {err}");
            }
            if (cleared > 0) CrashLog.Trace($"ClearQuarantine: cleared {cleared} file(s) in lib/");
        }
        catch (Exception ex) { CrashLog.Write("ClearQuarantine", ex); }
    }

    [SupportedOSPlatform("macos")]
    [DllImport("/usr/lib/libSystem.B.dylib", SetLastError = true)]
    private static extern int removexattr([MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int options);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();

#if DEBUG
    /// <summary>
    /// Debug only. A Debug build started without a console (e.g. by an IDE
    /// that redirects output) has none at all, so every
    /// Console.WriteLine before this point went nowhere — not buffered, not
    /// redirected, just discarded. On Windows this reattaches to whichever
    /// console launched the process, or opens a new window if there wasn't
    /// one (double-click, a desktop shortcut, Explorer). Linux/macOS always
    /// have a real terminal already, so there is nothing to do there.
    /// </summary>
    private static void EnsureConsole()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (GetConsoleWindow() != IntPtr.Zero) return; // already attached (e.g. `dotnet run`)

        const int AttachParentProcess = -1;
        if (!AttachConsole(AttachParentProcess)) AllocConsole();

        // Console's own writers were bound before a console existed; rebind
        // them to the one we just attached/opened, or WriteLine still goes
        // nowhere. AutoFlush matters: StreamWriter buffers by default, and a
        // freeze is exactly the case where the last line must not be stuck
        // in a buffer that never gets flushed.
        var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        Console.SetOut(stdout);
        var stderr = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
        Console.SetError(stderr);
    }

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll")]
    private static extern bool AllocConsole();

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();
#endif
}

