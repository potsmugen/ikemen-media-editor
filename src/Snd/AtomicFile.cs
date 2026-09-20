namespace Snd;

/// <summary>
/// Writes a file through a temp file beside it, then moves that over the target.
/// A write that fails or is cut short leaves the old file untouched.
/// </summary>
internal static class AtomicFile
{
    public static void Write(string path, Action<Stream> write)
    {
        string full = Path.GetFullPath(path);
        string temp = full + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                write(file);
                file.Flush(flushToDisk: true); // on disk before it replaces the old file
            }
            File.Move(temp, full, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp); // left only when the write or move failed
        }
    }

    public static void WriteAllBytes(string path, byte[] bytes) => Write(path, s => s.Write(bytes));
}
