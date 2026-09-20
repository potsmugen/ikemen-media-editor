using System.Globalization;

namespace Editor;

/// <summary>Retains numbered prior versions when a save overwrites a file.</summary>
internal static class Backups
{
    public static string? Make(string path, int retainedCount)
    {
        retainedCount = Settings.Checked(nameof(Settings.BackupCount), retainedCount,
            Settings.DefaultBackupCount, v => v is >= 0 and <= Settings.DefaultBackupCountMax);
        if (retainedCount == 0 || !File.Exists(path)) return null;

        for (int i = retainedCount - 1; i >= 1; i--)
        {
            string source = i == 1 ? path + ".bak" : $"{path}.bak.{i - 1}";
            string target = $"{path}.bak.{i}";
            if (File.Exists(source)) File.Copy(source, target, overwrite: true);
            else if (File.Exists(target)) File.Delete(target);
        }

        string backup = path + ".bak";
        string staged = backup + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.Copy(path, staged);
            File.Move(staged, backup, overwrite: true);
        }
        finally
        {
            if (File.Exists(staged)) File.Delete(staged);
        }

        foreach (string candidate in Directory.EnumerateFiles(Path.GetDirectoryName(Path.GetFullPath(path))!, Path.GetFileName(backup) + ".*"))
        {
            string suffix = candidate[(candidate.LastIndexOf('.') + 1)..];
            if (int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out int number)
                && number >= retainedCount)
                File.Delete(candidate);
        }

        CrashLog.Trace($"[backup] {path} -> {Path.GetFileName(backup)} (retained {retainedCount})");
        return backup;
    }
}
