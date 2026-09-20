namespace IkemenMediaEditor.Tests;

/// <summary>
/// Sample character files live in the repo's gitignored working/ folder, never
/// under tests/, so they can't be committed or shipped. Tests that need one skip
/// when it's absent, so a fresh clone still passes.
/// </summary>
internal static class WorkingFiles
{
    /// <summary>working/<paramref name="name"/>, found by walking up from the test binary; null when absent.</summary>
    public static string? Find(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "working", name);
            if (File.Exists(path)) return path;
        }
        return null;
    }
}
