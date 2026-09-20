using System.Buffers.Binary;
using Sff;
using Xunit;

namespace IkemenMediaEditor.Tests;

/// <summary>
/// Corrupt counts, offsets and lengths must fail with a clear InvalidDataException
/// or a per-sprite DecodeError, never a huge allocation, a wrapped offset or a
/// silently empty sprite.
/// </summary>
public class MalformedSffTests
{
    private static byte[] Written(SffVersion version)
    {
        var pal = new Palette { Group = 1, Index = 1, Colors = Enumerable.Range(0, 256).Select(i => new Rgba((byte)i, 0, 0, 255)).ToArray() };
        var f = new SffFile { Version = version, CompatVersion = version, Palettes = [pal] };
        f.Sprites.Add(new Sprite { Group = 0, Index = 0, Width = 2, Height = 2, Palette = pal, Indexed = [0, 1, 2, 3], PixelsDirty = true });
        string tmp = Path.Combine(Path.GetTempPath(), $"malformed-{Guid.NewGuid():N}.sff");
        try
        {
            f.Save(tmp, version);
            return File.ReadAllBytes(tmp);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }

    private static uint U32(byte[] b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at));
    private static void Put(byte[] b, int at, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), v);

    [Fact]
    public void V2_HugeSpriteCountIsRejectedBeforeAllocating()
    {
        var b = Written(SffVersions.V2_1);
        Put(b, 40, uint.MaxValue);
        var ex = Assert.Throws<InvalidDataException>(() => SffReader.Decode(b));
        Assert.Contains("sprite table", ex.Message);
    }

    [Fact]
    public void V2_PaletteShorterThanOneColorIsRejected()
    {
        var b = Written(SffVersions.V2_1);
        Put(b, (int)U32(b, 44) + 12, 2);
        var ex = Assert.Throws<InvalidDataException>(() => SffReader.Decode(b));
        Assert.Contains("shorter than one color", ex.Message);
    }

    [Fact]
    public void V2_SpriteOffsetPastTheEndIsADecodeError()
    {
        var b = Written(SffVersions.V2_1);
        Put(b, (int)U32(b, 36) + 16, 0xFFFFFF00); // used to wrap the int offset
        var s = Assert.Single(SffReader.Decode(b).Sprites);
        Assert.Equal("sprite data lies past the end of the file", s.DecodeError);
    }

    [Fact]
    public void V1_HugeSpriteLengthIsNotSilentlyEmpty()
    {
        var b = Written(SffVersions.V1);
        Put(b, (int)U32(b, 24) + 4, 0x80000000); // used to turn negative and skip decoding
        var s = Assert.Single(SffReader.Decode(b).Sprites);
        Assert.True(s.HasPixels || s.DecodeError is not null);
    }

    [Fact]
    public void V1_FirstOffsetNearUIntMaxEndsTheList()
    {
        var b = Written(SffVersions.V1);
        Put(b, 24, 0xFFFFFFF0); // off + 32 used to wrap to a small number
        Assert.Empty(SffReader.Decode(b).Sprites);
    }
}
