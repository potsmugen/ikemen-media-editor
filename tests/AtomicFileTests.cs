using Sff;
using Xunit;

namespace IkemenMediaEditor.Tests;

/// <summary>A failed write must leave the old file as it was, with no temp file behind.</summary>
public class AtomicFileTests
{
    private static string NewDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "atomic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void ReplacesContentOnSuccess()
    {
        string dir = NewDir();
        try
        {
            string path = Path.Combine(dir, "a.sff");
            File.WriteAllBytes(path, [1, 2, 3]);
            AtomicFile.WriteAllBytes(path, [9, 8]);
            Assert.Equal(new byte[] { 9, 8 }, File.ReadAllBytes(path));
            Assert.Single(Directory.GetFiles(dir));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void KeepsOldFileWhenWriteThrows()
    {
        string dir = NewDir();
        try
        {
            string path = Path.Combine(dir, "a.sff");
            File.WriteAllBytes(path, [1, 2, 3]);
            Assert.Throws<IOException>(() => AtomicFile.Write(path, s =>
            {
                s.Write([7, 7, 7, 7]);
                throw new IOException("disk full");
            }));
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
            Assert.Single(Directory.GetFiles(dir));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
