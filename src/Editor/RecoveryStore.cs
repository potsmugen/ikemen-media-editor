using System.Security.Cryptography;
using System.Text.Json;

namespace Editor;

internal sealed record RecoverySnapshot(string Kind, string Title, string? SourcePath, string? SourceHash,
    string FileName, bool Embedded, int Group = 0, int Index = 0, bool Dirty = true,
    string? ContextPath = null, string? ContextHash = null, string? SessionVersion = null);

internal sealed record RecoveryManifest(int Version, DateTimeOffset CapturedAt, List<RecoverySnapshot> Items);

internal static class RecoveryStore
{
    internal sealed record ActivePackage(string DirectoryName);
    private const int ManifestVersion = 1;
    private const string LockFileName = "run.lock";
    private static string? _testRoot, _testBaseRoot, _runRoot;
    private static FileStream? _runLock;

    /// <summary>Holds one run-* folder per running copy of the app, plus the shared recovered/ folder.</summary>
    private static string BaseRoot => _testBaseRoot ?? Path.Combine(AppPaths.Data, "recovery");

    /// <summary>This run's own folder; other running copies never touch it.</summary>
    private static string Root => _testRoot ?? _runRoot ?? throw new InvalidOperationException("Recovery has not started.");

    internal static string? TestRoot
    {
        get => _testRoot;
        set => _testRoot = value;
    }
    internal static string? TestBaseRoot
    {
        get => _testBaseRoot;
        set => _testBaseRoot = value;
    }
    public static string TempDirectory => Path.Combine(Root, "temp");
    public static string RecoveredDirectory => Path.Combine(BaseRoot, "recovered");
    private static string ActivePathIn(string root) => Path.Combine(root, "active.json");
    private static string ActivePath => ActivePathIn(Root);

    /// <summary>
    /// Creates this run's folder and locks it until exit. Folders of runs that are no
    /// longer live (crashed, or kept for later) are merged into this run's package;
    /// locked folders belong to another running copy and are left alone.
    /// Returns true when an earlier package was taken over.
    /// </summary>
    public static bool BeginRun(bool testMode)
    {
        Directory.CreateDirectory(BaseRoot);
        string run = Path.Combine(BaseRoot, "run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(run);
        // The OS drops this lock when the process ends, crash included.
        _runLock = new FileStream(Path.Combine(run, LockFileName), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        _runRoot = run;

        var held = new List<(string Directory, FileStream Lock, bool Read)>();
        var found = new List<(RecoverySnapshot Item, byte[] Data, DateTimeOffset At)>();
        foreach (string other in Directory.EnumerateDirectories(BaseRoot, "run-*"))
        {
            if (other == run) continue;
            string lockPath = Path.Combine(other, LockFileName);
            if (!File.Exists(lockPath)) continue; // being created, or not a run folder
            FileStream otherLock;
            try { otherLock = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { continue; } // another copy is running with it
            bool read = false;
            try
            {
                var items = new List<(RecoverySnapshot, byte[], DateTimeOffset)>();
                if (ReadPackage(other) is { } manifest && ActiveDirectoryIn(other) is { } dir)
                    foreach (var item in manifest.Items)
                        items.Add((item, File.ReadAllBytes(Path.Combine(dir, item.FileName)), manifest.CapturedAt));
                found.AddRange(items);
                read = true;
            }
            catch (Exception ex)
            {
                CrashLog.Write("RecoveryStore.BeginRun", ex); // left in place for a later run to retry
            }
            held.Add((other, otherLock, read));
        }

        bool published = false;
        try
        {
            // Two runs may hold the same file; the newer snapshot wins.
            var newest = found.GroupBy(x => Key(x.Item))
                .Select(g => g.MaxBy(x => x.At))
                .Select(x => (x.Item, x.Data)).ToList();
            if (newest.Count > 0) Publish(newest);
            published = true;
        }
        finally
        {
            foreach (var (directory, otherLock, read) in held)
            {
                try
                {
                    // Taken over: drop the pointer while still locked, so no other copy adopts it too.
                    if (read && published)
                    {
                        if (File.Exists(ActivePathIn(directory))) File.Delete(ActivePathIn(directory));
                        otherLock.Dispose();
                        Directory.Delete(directory, recursive: true);
                    }
                    else otherLock.Dispose();
                }
                catch (Exception ex)
                {
                    otherLock.Dispose();
                    CrashLog.Write("RecoveryStore.BeginRun", ex);
                }
            }
        }

        bool adopted = found.Count > 0;
        CrashLog.Trace($"[recovery] run started in {Path.GetFileName(run)}; took over {held.Count} earlier run(s), {found.Count} snapshot(s)");
        return adopted || testMode;
    }

    /// <summary>Releases this run's lock. The folder stays only when it holds a package to offer next time.</summary>
    public static void MarkCleanShutdown()
    {
        if (_runRoot is not { } run) return;
        _runLock?.Dispose();
        _runLock = null;
        _runRoot = null;
        try
        {
            if (!File.Exists(ActivePathIn(run))) Directory.Delete(run, recursive: true);
        }
        catch (Exception ex)
        {
            CrashLog.Write("RecoveryStore.MarkCleanShutdown", ex);
        }
        CrashLog.Trace("[recovery] clean shutdown");
    }

    public static bool HasPackage => File.Exists(ActivePath);

    public static RecoveryManifest? ReadActive() => ReadPackage(Root);

    private static RecoveryManifest? ReadPackage(string root)
    {
        string activePath = ActivePathIn(root);
        if (!File.Exists(activePath)) return null;
        var active = JsonSerializer.Deserialize(File.ReadAllText(activePath), SettingsContext.Default.ActivePackage);
        if (active is null || Path.GetFileName(active.DirectoryName) != active.DirectoryName)
            throw new InvalidDataException("The recovery package pointer is invalid.");
        string directory = Path.Combine(root, active.DirectoryName);
        var manifest = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(directory, "manifest.json")),
            SettingsContext.Default.RecoveryManifest);
        if (manifest is null || manifest.Version != ManifestVersion)
            throw new InvalidDataException("The recovery package version is not supported.");
        foreach (var item in manifest.Items)
        {
            if (Path.GetFileName(item.FileName) != item.FileName || !File.Exists(Path.Combine(directory, item.FileName)))
                throw new InvalidDataException("A recovery snapshot is missing or has an invalid name.");
        }
        return manifest;
    }

    public static string SnapshotPath(RecoverySnapshot item)
    {
        var active = JsonSerializer.Deserialize(File.ReadAllText(ActivePath), SettingsContext.Default.ActivePackage)
            ?? throw new InvalidDataException("The recovery package pointer is invalid.");
        return Path.Combine(Root, active.DirectoryName, item.FileName);
    }

    public static void Publish(IReadOnlyList<(RecoverySnapshot Item, byte[] Data)> snapshots)
    {
        if (snapshots.Count == 0) return;
        Directory.CreateDirectory(Root);
        var previous = ReadActive();
        var previousDirectory = ActiveDirectory();
        var byKey = snapshots.ToDictionary(x => Key(x.Item), x => x);
        var merged = new List<(RecoverySnapshot Item, byte[] Data)>(snapshots);
        if (previous is not null && previousDirectory is not null)
        {
            foreach (var item in previous.Items)
                if (!byKey.ContainsKey(Key(item)))
                    merged.Add((item, File.ReadAllBytes(Path.Combine(previousDirectory, item.FileName))));
        }

        string generation = "snapshot-" + Guid.NewGuid().ToString("N");
        string staging = Path.Combine(Root, generation + ".tmp");
        string complete = Path.Combine(Root, generation);
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var (item, data) in merged)
                File.WriteAllBytes(Path.Combine(staging, item.FileName), data);
            var manifest = new RecoveryManifest(ManifestVersion, DateTimeOffset.UtcNow,
                merged.Select(s => s.Item).ToList());
            File.WriteAllText(Path.Combine(staging, "manifest.json"),
                JsonSerializer.Serialize(manifest, SettingsContext.Default.RecoveryManifest));
            Directory.Move(staging, complete);

            string pointerTemp = ActivePath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(pointerTemp, JsonSerializer.Serialize(new ActivePackage(generation), SettingsContext.Default.ActivePackage));
                File.Move(pointerTemp, ActivePath, overwrite: true);
            }
            finally
            {
                if (File.Exists(pointerTemp)) File.Delete(pointerTemp);
            }
            if (previousDirectory is not null && Directory.Exists(previousDirectory))
                Directory.Delete(previousDirectory, recursive: true);
            CrashLog.Trace($"[recovery] published {merged.Count} snapshot(s)");
        }
        catch
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            throw;
        }
    }

    private static string Key(RecoverySnapshot item)
    {
        string path = item.SourcePath is null ? "(new)" : Path.GetFullPath(item.SourcePath);
        if (OperatingSystem.IsWindows()) path = path.ToUpperInvariant();
        return $"{item.Kind}|{path}|{item.Group}|{item.Index}";
    }

    private static string? ActiveDirectory() => ActiveDirectoryIn(Root);

    private static string? ActiveDirectoryIn(string root)
    {
        string activePath = ActivePathIn(root);
        if (!File.Exists(activePath)) return null;
        var active = JsonSerializer.Deserialize(File.ReadAllText(activePath), SettingsContext.Default.ActivePackage);
        return active is null || Path.GetFileName(active.DirectoryName) != active.DirectoryName
            ? throw new InvalidDataException("The recovery package pointer is invalid.")
            : Path.Combine(root, active.DirectoryName);
    }

    public static bool SourceMatches(RecoverySnapshot item)
    {
        if (item.SourcePath is null || item.SourceHash is null || !File.Exists(item.SourcePath)) return false;
        return CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(item.SourceHash), SHA256.HashData(File.ReadAllBytes(item.SourcePath)));
    }

    public static string? HashFile(string? path) => path is not null && File.Exists(path)
        ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : null;

    public static void Resolve(string kind, string? sourcePath, int? group = null, int? index = null)
    {
        var manifest = ReadActive();
        if (manifest is null) return;
        var remaining = manifest.Items.Where(item => item.Kind != kind
            || !(sourcePath is null
                ? item.SourcePath is null && (group is null || item.Group == group) && (index is null || item.Index == index)
                : string.Equals(item.SourcePath, sourcePath, OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))).ToList();
        if (remaining.Count == manifest.Items.Count) return;
        if (remaining.Count == 0) { DiscardActive(); return; }

        string activeDirectory = SnapshotPathForManifest(manifest.Items[0]);
        string packageDirectory = Path.GetDirectoryName(activeDirectory)!;
        string generation = "snapshot-" + Guid.NewGuid().ToString("N");
        string staging = Path.Combine(Root, generation + ".tmp");
        string complete = Path.Combine(Root, generation);
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var item in remaining) File.Copy(Path.Combine(packageDirectory, item.FileName), Path.Combine(staging, item.FileName));
            var next = manifest with { CapturedAt = DateTimeOffset.UtcNow, Items = remaining };
            File.WriteAllText(Path.Combine(staging, "manifest.json"), JsonSerializer.Serialize(next, SettingsContext.Default.RecoveryManifest));
            Directory.Move(staging, complete);
            string pointerTemp = ActivePath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(pointerTemp, JsonSerializer.Serialize(new ActivePackage(generation), SettingsContext.Default.ActivePackage));
                File.Move(pointerTemp, ActivePath, overwrite: true);
            }
            finally { if (File.Exists(pointerTemp)) File.Delete(pointerTemp); }
            Directory.Delete(packageDirectory, recursive: true);
            CrashLog.Trace($"[recovery] resolved {kind}: {sourcePath}");
        }
        catch
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            throw;
        }
    }

    private static string SnapshotPathForManifest(RecoverySnapshot item)
    {
        var active = JsonSerializer.Deserialize(File.ReadAllText(ActivePath), SettingsContext.Default.ActivePackage)
            ?? throw new InvalidDataException("The recovery package pointer is invalid.");
        return Path.Combine(Root, active.DirectoryName, item.FileName);
    }

    public static void DiscardActive()
    {
        if (!File.Exists(ActivePath)) return;
        var active = JsonSerializer.Deserialize(File.ReadAllText(ActivePath), SettingsContext.Default.ActivePackage);
        File.Delete(ActivePath);
        if (active is not null && Path.GetFileName(active.DirectoryName) == active.DirectoryName)
        {
            string directory = Path.Combine(Root, active.DirectoryName);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        CrashLog.Trace("[recovery] discarded active package");
    }
}
