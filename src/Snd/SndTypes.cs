namespace Snd;

/// <summary>
/// One packed sound, addressed by group and number exactly as sprites are. The
/// payload is a complete WAV file, stored whole rather than decoded.
/// </summary>
public sealed class SoundEntry
{
    public int Group;
    public int Index;

    /// <summary>The WAV file, bytes untouched.</summary>
    public byte[] Data = [];

    /// <summary>Anything wrong found while reading the header, or null.</summary>
    public string? Problem;

    // Read from the WAV header for display. Zero when it could not be read.
    public int Channels;
    public int SampleRate;
    public int BitsPerSample;
    public ushort FormatTag;

    /// <summary>Playing length, or zero when the header could not be read.</summary>
    public TimeSpan Duration;

    /// <summary>Byte offset of the "data" chunk's payload within Data, for reading raw PCM (e.g. a waveform view). Zero if never found.</summary>
    public int DataOffset;

    public override string ToString() => $"{Group},{Index}";
}

/// <summary>A parsed .snd file.</summary>
public sealed class SndFile
{
    /// <summary>Version quad from the header, as two 16-bit halves.</summary>
    public ushort Version = 4; // what sndmaker writes (bytes 04 00 00 00); opened files keep their own
    public ushort Version2;

    public List<SoundEntry> Sounds = [];

    public static SndFile Open(string path) => SndReader.Decode(File.ReadAllBytes(path));

    public void Save(string path) => SndWriter.Save(this, path);

    public SoundEntry? Find(int group, int index) =>
        Sounds.FirstOrDefault(s => s.Group == group && s.Index == index);

    /// <summary>
    /// Entries sharing a group and number. The engine keeps the first and
    /// ignores the rest, so they are worth pointing out rather than hiding.
    /// </summary>
    public IEnumerable<SoundEntry> Duplicates() =>
        Sounds.GroupBy(s => (s.Group, s.Index)).Where(g => g.Count() > 1).SelectMany(g => g.Skip(1));
}
