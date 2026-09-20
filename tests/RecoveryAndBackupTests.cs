using Editor;
using Xunit;

namespace IkemenMediaEditor.Tests;

public class RecoveryAndBackupTests
{
    [Fact]
    public void Backups_RetainConfiguredGenerations()
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "asset.sff");
        try
        {
            File.WriteAllText(path, "version-0");
            for (int i = 1; i <= 4; i++)
            {
                Backups.Make(path, 3);
                File.WriteAllText(path, $"version-{i}");
            }

            Assert.Equal("version-4", File.ReadAllText(path));
            Assert.Equal("version-3", File.ReadAllText(path + ".bak"));
            Assert.Equal("version-2", File.ReadAllText(path + ".bak.1"));
            Assert.Equal("version-1", File.ReadAllText(path + ".bak.2"));
            Assert.False(File.Exists(path + ".bak.3"));

            File.WriteAllText(path, "version-4");
            Backups.Make(path, 1);
            Assert.Equal("version-4", File.ReadAllText(path + ".bak"));
            Assert.False(File.Exists(path + ".bak.1"));

            File.WriteAllText(path, "version-5");
            Assert.Null(Backups.Make(path, 0));
            Assert.Equal("version-5", File.ReadAllText(path));
            Assert.Equal("version-4", File.ReadAllText(path + ".bak"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void RecoveryStore_PublishesCompleteNewGeneration()
    {
        string? prior = RecoveryStore.TestRoot;
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        RecoveryStore.TestRoot = directory;
        try
        {
            RecoveryStore.Publish([(new("sff", "asset.sff", "asset.sff", "AA", "sff.sff", false), [1, 2, 3])]);
            var first = RecoveryStore.ReadActive()!;
            Assert.Single(first.Items);
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(RecoveryStore.SnapshotPath(first.Items[0])));

            RecoveryStore.Publish([(new("snd", "asset.snd", "asset.snd", "BB", "snd.snd", false), [4, 5])]);
            var second = RecoveryStore.ReadActive()!;
            Assert.Equal(2, second.Items.Count);
            var secondSound = second.Items.Single(x => x.Kind == "snd");
            var carriedSprite = second.Items.Single(x => x.Kind == "sff");
            Assert.Equal(new byte[] { 4, 5 }, File.ReadAllBytes(RecoveryStore.SnapshotPath(secondSound)));
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(RecoveryStore.SnapshotPath(carriedSprite)));

            RecoveryStore.Resolve("snd", "asset.snd");
            var third = RecoveryStore.ReadActive()!;
            Assert.Single(third.Items);
            Assert.Equal("sff", third.Items[0].Kind);
        }
        finally
        {
            RecoveryStore.TestRoot = prior;
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RecoveryStore_TakesOverEndedRunsOnly()
    {
        string? priorRoot = RecoveryStore.TestRoot, priorBase = RecoveryStore.TestBaseRoot;
        string baseDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        string ended = Path.Combine(baseDir, "run-ended"), live = Path.Combine(baseDir, "run-live");
        try
        {
            RecoveryStore.TestBaseRoot = baseDir;
            RecoveryStore.TestRoot = ended;
            RecoveryStore.Publish([(new("sff", "a.sff", "a.sff", "AA", "sff.sff", false), [1, 2, 3])]);
            File.WriteAllText(Path.Combine(ended, "run.lock"), ""); // lock file left by a crashed run
            RecoveryStore.TestRoot = live;
            RecoveryStore.Publish([(new("snd", "b.snd", "b.snd", "BB", "snd.snd", false), [4, 5])]);
            using var liveLock = new FileStream(Path.Combine(live, "run.lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            RecoveryStore.TestRoot = null;

            Assert.True(RecoveryStore.BeginRun(testMode: false));
            var item = Assert.Single(RecoveryStore.ReadActive()!.Items);
            Assert.Equal("sff", item.Kind);
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(RecoveryStore.SnapshotPath(item)));
            Assert.False(Directory.Exists(ended));
            Assert.True(File.Exists(Path.Combine(live, "active.json"))); // the running copy's package is untouched

            RecoveryStore.DiscardActive();
            RecoveryStore.MarkCleanShutdown();
            Assert.Equal(new[] { live }, Directory.GetDirectories(baseDir, "run-*"));
        }
        finally
        {
            RecoveryStore.MarkCleanShutdown();
            RecoveryStore.TestRoot = priorRoot;
            RecoveryStore.TestBaseRoot = priorBase;
            if (Directory.Exists(baseDir)) Directory.Delete(baseDir, recursive: true);
        }
    }
}
