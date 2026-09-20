namespace Editor;

/// <summary>
/// The editor is portable: everything it writes stays in its own folder, so deleting the
/// folder removes it. User data goes in data/ (settings, recovery, strings, action names);
/// logs/ and themes/ sit beside it. Nothing goes to AppData or Temp.
/// </summary>
internal static class AppPaths
{
    /// <summary>data/ beside the exe, created on first use.</summary>
    public static string Data
    {
        get
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "data");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string DataFile(string name) => Path.Combine(Data, name);
}
