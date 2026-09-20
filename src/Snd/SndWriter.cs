using System.Buffers.Binary;

namespace Snd;

/// <summary>
/// Writes .snd back out, rebuilding the linked list of subheaders. The layout
/// follows sndmaker: a zeroed 512-byte header, and the last subheader's next
/// offset pointing at the end of the file.
/// </summary>
public static class SndWriter
{
    private const int WrittenHeaderSize = 512;

    public static void Save(SndFile file, string path)
    {
        var body = new MemoryStream();
        var header = new byte[WrittenHeaderSize];
        SndReader.Signature.CopyTo(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(12), file.Version);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(14), file.Version2);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), (uint)file.Sounds.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), WrittenHeaderSize);

        // Offsets are known up front, so the next pointers can be written as we go.
        int offset = WrittenHeaderSize;
        for (int i = 0; i < file.Sounds.Count; i++)
        {
            var s = file.Sounds[i];
            int next = offset + SndReader.SubHeaderSize + s.Data.Length;

            var n = new byte[SndReader.SubHeaderSize];
            BinaryPrimitives.WriteUInt32LittleEndian(n, (uint)next); // the last one points at the end of the file
            BinaryPrimitives.WriteUInt32LittleEndian(n.AsSpan(4), (uint)s.Data.Length);
            BinaryPrimitives.WriteInt32LittleEndian(n.AsSpan(8), s.Group);
            BinaryPrimitives.WriteInt32LittleEndian(n.AsSpan(12), s.Index);

            body.Write(n);
            body.Write(s.Data);
            offset = next;
        }

        AtomicFile.Write(path, f =>
        {
            f.Write(header);
            body.Position = 0;
            body.CopyTo(f);
        });
    }
}
