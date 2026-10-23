using System.Buffers.Binary;

namespace Snd;

/// <summary>
/// Reads .snd. The layout is a 16-byte header after the signature, then a linked
/// list of 16-byte subheaders each followed by a whole WAV file. Sounds are
/// addressed by group and number, the same scheme sprites use.
/// </summary>
public static class SndReader
{
    internal static ReadOnlySpan<byte> Signature => "ElecbyteSnd\0"u8;
    internal const int HeaderSize = 28;    // signature, two version halves, count, first offset
    internal const int SubHeaderSize = 16; // next offset, length, group, number

    public static SndFile Decode(byte[] b)
    {
        if (b.Length < HeaderSize)
            throw new InvalidDataException($"file is {b.Length} bytes, too short to be a .snd");
        if (!b.AsSpan(0, 12).SequenceEqual(Signature))
            throw new InvalidDataException("bad signature, not a .snd file");

        var file = new SndFile
        {
            Version = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(12)),
            Version2 = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(14)),
        };
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(16));
        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(20));

        // Walk the list rather than trusting the count, and stop on a pointer
        // that does not advance so a malformed file cannot loop forever.
        for (uint i = 0; i < count && (long)offset + SubHeaderSize <= b.Length; i++)
        {
            var n = b.AsSpan((int)offset, SubHeaderSize);
            uint next = BinaryPrimitives.ReadUInt32LittleEndian(n);
            int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(n[4..]);

            var entry = new SoundEntry
            {
                Group = BinaryPrimitives.ReadInt32LittleEndian(n[8..]),
                Index = BinaryPrimitives.ReadInt32LittleEndian(n[12..]),
            };

            int start = (int)offset + SubHeaderSize;
            if (length < 0 || (long)start + length > b.Length)
            {
                entry.Problem = "the sound runs past the end of the file";
                length = Math.Max(0, Math.Min(length, b.Length - start));
            }
            entry.Data = b.AsSpan(start, length).ToArray();
            Describe(entry);
            file.Sounds.Add(entry);

            if (next <= offset) break;
            offset = next;
        }
        return file;
    }

    /// <summary>
    /// Reads what the WAV header says about the audio. The engine decodes every
    /// sound on load to prove it plays; that is more than an editor needs, but a
    /// header that does not parse is worth reporting.
    /// </summary>
    public static void Describe(SoundEntry entry)
    {
        var d = entry.Data;
        entry.FormatTag = 0;
        if (d.Length < 44)
        {
            entry.Problem ??= d.Length == 0 ? "empty" : "too short to be a WAV";
            return;
        }
        if (!d.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !d.AsSpan(8, 4).SequenceEqual("WAVE"u8))
        {
            entry.Problem ??= "not a RIFF WAVE";
            return;
        }

        // Walk the chunks; fmt and data are not always where a naive reader looks.
        int p = 12;
        long dataBytes = 0;
        bool foundData = false;
        while (p + 8 <= d.Length)
        {
            var id = d.AsSpan(p, 4);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(p + 4));
            int body = p + 8;

            if (id.SequenceEqual("fmt "u8) && body + 16 <= d.Length)
            {
                entry.FormatTag = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(body));
                uint fmtSize = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(p + 4));
                if (entry.FormatTag == 0xfffe && fmtSize >= 40 && body + 40 <= d.Length
                    && BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(body + 16)) >= 22)
                {
                    ReadOnlySpan<byte> pcmGuid = [1, 0, 0, 0, 0, 0, 0x10, 0, 0x80, 0, 0, 0xaa, 0, 0x38, 0x9b, 0x71];
                    ReadOnlySpan<byte> floatGuid = [3, 0, 0, 0, 0, 0, 0x10, 0, 0x80, 0, 0, 0xaa, 0, 0x38, 0x9b, 0x71];
                    var subtype = d.AsSpan(body + 24, 16);
                    entry.FormatTag = subtype.SequenceEqual(pcmGuid) ? (ushort)1
                        : subtype.SequenceEqual(floatGuid) ? (ushort)3 : (ushort)0xfffe;
                }
                entry.Channels = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(body + 2));
                entry.SampleRate = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(body + 4));
                entry.BitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(body + 14));
            }
            else if (id.SequenceEqual("data"u8))
            {
                dataBytes = Math.Min(size, (uint)Math.Max(0, d.Length - body));
                entry.DataOffset = body;
                foundData = true;
            }

            // Chunks are word-aligned. In long, so a huge size ends the walk instead of
            // wrapping p negative or backward (which threw or looped forever).
            long next = (long)body + size + (size & 1);
            if (size == 0 || next + 8 > d.Length) break;
            p = (int)next;
        }

        // A bad chunk size can end the walk before data; without this it showed as a 0 s sound.
        if (!foundData) entry.Problem ??= "missing its data chunk";

        int frame = entry.Channels * Math.Max(entry.BitsPerSample, 1) / 8;
        if (frame > 0 && entry.SampleRate > 0)
            entry.Duration = TimeSpan.FromSeconds((double)dataBytes / frame / entry.SampleRate);
        else
            entry.Problem ??= "the WAV header could not be read";
    }
}
