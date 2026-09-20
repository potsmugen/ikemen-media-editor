using System.Buffers.Binary;
using Snd;
using Xunit;

namespace IkemenMediaEditor.Tests;

/// <summary>
/// A .snd is a container: sounds go in and come out as whole WAV files.
/// These test its layout, byte-level edits, and BCL-only PCM loudness processing.
/// </summary>
public class SndTests
{
    /// <summary>A minimal but valid WAV, since the tests need real headers.</summary>
    private static byte[] MakeWav(int sampleRate = 22050, int channels = 1, int bits = 16, int frames = 11025)
    {
        int frameSize = channels * bits / 8;
        int dataBytes = frames * frameSize;
        var w = new byte[44 + dataBytes];

        "RIFF"u8.CopyTo(w);
        BinaryPrimitives.WriteInt32LittleEndian(w.AsSpan(4), 36 + dataBytes);
        "WAVE"u8.CopyTo(w.AsSpan(8));
        "fmt "u8.CopyTo(w.AsSpan(12));
        BinaryPrimitives.WriteInt32LittleEndian(w.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(w.AsSpan(20), 1); // PCM
        BinaryPrimitives.WriteUInt16LittleEndian(w.AsSpan(22), (ushort)channels);
        BinaryPrimitives.WriteInt32LittleEndian(w.AsSpan(24), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(w.AsSpan(28), sampleRate * frameSize);
        BinaryPrimitives.WriteUInt16LittleEndian(w.AsSpan(32), (ushort)frameSize);
        BinaryPrimitives.WriteUInt16LittleEndian(w.AsSpan(34), (ushort)bits);
        "data"u8.CopyTo(w.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(w.AsSpan(40), dataBytes);
        return w;
    }

    private static SndFile MakeFile()
    {
        var f = new SndFile();
        f.Sounds.Add(new SoundEntry { Group = 0, Index = 0, Data = MakeWav() });
        f.Sounds.Add(new SoundEntry { Group = 0, Index = 1, Data = MakeWav(44100, 2, 16, 44100) });
        f.Sounds.Add(new SoundEntry { Group = 5000, Index = 0, Data = MakeWav() });
        foreach (var s in f.Sounds) SndReader.Describe(s);
        return f;
    }

    [Fact]
    public void RoundTripsThroughTheContainer()
    {
        var original = MakeFile();
        var tmp = Path.Combine(Path.GetTempPath(), $"snd-{Guid.NewGuid():N}.snd");
        try
        {
            original.Save(tmp);
            var reloaded = SndFile.Open(tmp);

            Assert.Equal(original.Sounds.Count, reloaded.Sounds.Count);
            for (int i = 0; i < original.Sounds.Count; i++)
            {
                Assert.Equal(original.Sounds[i].Group, reloaded.Sounds[i].Group);
                Assert.Equal(original.Sounds[i].Index, reloaded.Sounds[i].Index);
                Assert.Equal(original.Sounds[i].Data, reloaded.Sounds[i].Data); // bytes untouched
                Assert.Null(reloaded.Sounds[i].Problem);
            }
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void ReadsTheWavHeader()
    {
        var s = new SoundEntry { Data = MakeWav(44100, 2, 16, 44100) };
        SndReader.Describe(s);

        Assert.Null(s.Problem);
        Assert.Equal(2, s.Channels);
        Assert.Equal(44100, s.SampleRate);
        Assert.Equal(16, s.BitsPerSample);
        Assert.Equal(1.0, s.Duration.TotalSeconds, 3);
    }

    [Fact]
    public void FindsTheFormatChunkPastAnExtraChunk()
    {
        // Real files put LIST or fact chunks before fmt, so a reader that assumes
        // a fixed layout gets the wrong numbers.
        var wav = MakeWav();
        var padded = new byte[wav.Length + 12];
        wav.AsSpan(0, 12).CopyTo(padded);
        "LIST"u8.CopyTo(padded.AsSpan(12));
        BinaryPrimitives.WriteInt32LittleEndian(padded.AsSpan(16), 4);
        wav.AsSpan(12).CopyTo(padded.AsSpan(24));
        BinaryPrimitives.WriteInt32LittleEndian(padded.AsSpan(4), padded.Length - 8);

        var s = new SoundEntry { Data = padded };
        SndReader.Describe(s);
        Assert.Equal(22050, s.SampleRate);
    }

    [Theory]
    [InlineData(0, "empty")]
    [InlineData(20, "too short to be a WAV")]
    public void ReportsUnreadableSounds(int size, string expected)
    {
        var s = new SoundEntry { Data = new byte[size] };
        SndReader.Describe(s);
        Assert.Equal(expected, s.Problem);
    }

    [Fact]
    public void ReportsSomethingThatIsNotAWav()
    {
        var s = new SoundEntry { Data = new byte[64] };
        "OggS"u8.CopyTo(s.Data);
        SndReader.Describe(s);
        Assert.Equal("not a RIFF WAVE", s.Problem);
    }

    [Theory]
    [InlineData(0x7FFFFFF0u)] // used to wrap the chunk position negative and throw
    [InlineData(0xFFFFFFF0u)] // used to step the position backward
    public void HugeChunkSizeEndsTheWalk(uint size)
    {
        var wav = MakeWav();
        var padded = new byte[wav.Length + 8];
        wav.AsSpan(0, 36).CopyTo(padded); // up to the end of fmt
        "junk"u8.CopyTo(padded.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(padded.AsSpan(40), size);
        wav.AsSpan(36).CopyTo(padded.AsSpan(44));

        var s = new SoundEntry { Data = padded };
        SndReader.Describe(s);
        Assert.Equal(22050, s.SampleRate); // fmt came first, so it's still read
    }

    private static byte[] SndHeader(int length, uint count, uint firstOffset)
    {
        var b = new byte[length];
        SndReader.Signature.CopyTo(b);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(16), count);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(20), firstOffset);
        return b;
    }

    [Fact]
    public void OversizedEntryIsReportedNotThrown()
    {
        var b = SndHeader(512 + 16 + 44, 1, 512);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(512 + 4), 0x7FFFFFFF); // start + length overflows int
        var s = Assert.Single(SndReader.Decode(b).Sounds);
        Assert.Equal("the sound runs past the end of the file", s.Problem);
    }

    [Fact]
    public void FirstOffsetNearUIntMaxEndsTheList()
    {
        var b = SndHeader(64, 1, 0xFFFFFFF8); // offset + 16 used to wrap to 8
        Assert.Empty(SndReader.Decode(b).Sounds);
    }

    [Fact]
    public void FindsDuplicateKeys()
    {
        var f = MakeFile();
        f.Sounds.Add(new SoundEntry { Group = 0, Index = 0, Data = MakeWav() });

        var dup = Assert.Single(f.Duplicates());
        Assert.Equal(0, dup.Group);
        Assert.Equal(0, dup.Index);
        Assert.Same(f.Sounds[^1], dup); // the later one is the ignored one
    }

    [Fact]
    public void RefusesAFileThatIsNotSnd()
    {
        Assert.Throws<InvalidDataException>(() => SndReader.Decode(new byte[64]));
    }

    private static SoundEntry Tone(double hz, int rate, int frames, double amp = 0.5, int channels = 1)
    {
        var s = new SoundEntry { Data = MakeWav(rate, channels, 16, frames) };
        for (int i = 0; i < frames; i++)
        {
            short sample = (short)Math.Round(amp * 32767 * Math.Sin(2 * Math.PI * hz * i / rate));
            for (int ch = 0; ch < channels; ch++)
                BinaryPrimitives.WriteInt16LittleEndian(s.Data.AsSpan(44 + (i * channels + ch) * 2), sample);
        }
        SndReader.Describe(s);
        return s;
    }

    private static SoundEntry Tone32(double hz, int rate, int frames, double amp)
    {
        var s = new SoundEntry { Data = MakeWav(rate, 1, 32, frames) };
        for (int i = 0; i < frames; i++)
        {
            int sample = (int)Math.Clamp(Math.Round(amp * int.MaxValue * Math.Sin(2 * Math.PI * hz * i / rate)), int.MinValue, int.MaxValue);
            BinaryPrimitives.WriteInt32LittleEndian(s.Data.AsSpan(44 + i * 4), sample);
        }
        SndReader.Describe(s);
        return s;
    }

    private static double MiddlePeak(byte[] wav)
    {
        int frames = (wav.Length - 44) / 2, peak = 0;
        for (int i = frames / 4; i < frames * 3 / 4; i++)
            peak = Math.Max(peak, Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(44 + i * 2))));
        return peak / 32768.0;
    }

    [Fact]
    public void ResampleKeepsInBandLevelAndUpdatesTheHeader()
    {
        var s = Tone(1000, 44100, 4410);
        var half = SndEdit.Resample(s, 22050);

        Assert.NotNull(half);
        Assert.Equal(22050, BinaryPrimitives.ReadInt32LittleEndian(half.AsSpan(24)));       // SampleRate
        Assert.Equal(22050 * 2, BinaryPrimitives.ReadInt32LittleEndian(half.AsSpan(28)));   // ByteRate
        Assert.Equal(2205 * 2, BinaryPrimitives.ReadInt32LittleEndian(half.AsSpan(40)));    // data size
        Assert.InRange(MiddlePeak(half), 0.47, 0.53);
        Assert.Null(SndEdit.Resample(s, 44100)); // same rate: nothing to do
    }

    [Fact]
    public void DownsamplingFiltersOutWhatTheNewRateCantHold()
    {
        // 15 kHz is above 22050 Hz's 11025 Hz limit: it must vanish, not alias to 7 kHz.
        var s = Tone(15000, 44100, 4410);
        Assert.True(MiddlePeak(SndEdit.Resample(s, 22050)!) < 0.02);
    }

    /// <summary>Frequency from rising zero crossings over the middle half, clear of edge effects. 16-bit mono only.</summary>
    private static double MiddleFrequency(byte[] wav, int rate)
    {
        int frames = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40)) / 2;
        short At(int i) => BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(44 + i * 2));
        int first = -1, last = -1, count = 0;
        for (int i = frames / 4; i < frames * 3 / 4; i++)
            if (At(i - 1) < 0 && At(i) >= 0)
            {
                if (first < 0) first = i;
                last = i;
                count++;
            }
        return (count - 1) * (double)rate / (last - first);
    }

    private static int DataFrames(byte[] wav, int frameBytes) => BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40)) / frameBytes;

    [Theory]
    [InlineData(50, 44100)]
    [InlineData(200, 11025)]
    public void ChangeTempoScalesLengthAndKeepsPitch(double percent, int expectedFrames)
    {
        var s = Tone(440, 44100, 22050);
        var result = SndEdit.ChangeTempo(s, percent)!;

        Assert.Equal(expectedFrames, DataFrames(result, 2));
        Assert.InRange(MiddleFrequency(result, 44100), 437, 443);
        Assert.InRange(MiddlePeak(result), 0.47, 0.53);
        Assert.Null(SndEdit.ChangeTempo(s, 100)); // unchanged: nothing to do
    }

    [Theory]
    [InlineData(12, 880)]
    [InlineData(-12, 220)]
    [InlineData(5, 587.33)]
    public void ChangePitchShiftsFrequencyAndKeepsLength(double semitones, double expectedHz)
    {
        var s = Tone(440, 44100, 22050);
        var result = SndEdit.ChangePitch(s, semitones)!;

        Assert.Equal(22050, DataFrames(result, 2));
        Assert.InRange(MiddleFrequency(result, 44100), expectedHz * 0.99, expectedHz * 1.01);
        Assert.InRange(MiddlePeak(result), 0.47, 0.53);
        Assert.Null(SndEdit.ChangePitch(s, 0));
    }

    [Theory]
    [InlineData(200, 11025, 880)]
    [InlineData(50, 44100, 220)]
    public void ChangeSpeedMovesPitchAndLengthTogether(double percent, int expectedFrames, double expectedHz)
    {
        var s = Tone(440, 44100, 22050);
        var result = SndEdit.ChangeSpeed(s, percent)!;

        Assert.Equal(expectedFrames, DataFrames(result, 2));
        Assert.Equal(44100, BinaryPrimitives.ReadInt32LittleEndian(result.AsSpan(24))); // header rate untouched
        Assert.InRange(MiddleFrequency(result, 44100), expectedHz * 0.99, expectedHz * 1.01);
        Assert.Null(SndEdit.ChangeSpeed(s, 100));
    }

    [Fact]
    public void PitchAndTempoKeepStereoChannelsLinked()
    {
        var s = Tone(440, 44100, 22050, channels: 2);
        foreach (var result in new[] { SndEdit.ChangePitch(s, 7)!, SndEdit.ChangeTempo(s, 150)! })
        {
            int frames = DataFrames(result, 4);
            for (int i = 0; i < frames; i++)
                Assert.Equal(BinaryPrimitives.ReadInt16LittleEndian(result.AsSpan(44 + i * 4)),
                    BinaryPrimitives.ReadInt16LittleEndian(result.AsSpan(46 + i * 4)));
        }
    }

    [Fact]
    public void SlowEditsStopWhenCanceled()
    {
        var s = Tone(440, 44100, 22050);
        var canceled = new CancellationToken(true);
        Assert.ThrowsAny<OperationCanceledException>(() => SndEdit.ChangePitch(s, 12, canceled));
        Assert.ThrowsAny<OperationCanceledException>(() => SndEdit.ChangeTempo(s, 150, canceled));
        Assert.ThrowsAny<OperationCanceledException>(() => SndEdit.ChangeSpeed(s, 150, canceled));
        Assert.ThrowsAny<OperationCanceledException>(() => SndEdit.Resample(s, 22050, canceled));
    }

    [Fact]
    public void RateEditsRefuseOnlyNonsenseValues()
    {
        // The allowed range lives in the editor's settings; the library only refuses nonsense.
        var s = Tone(440, 44100, 4410);
        Assert.Null(SndEdit.ChangePitch(s, double.NaN));
        Assert.Null(SndEdit.ChangeTempo(s, 0));
        Assert.Null(SndEdit.ChangeSpeed(s, -50));
        Assert.Null(SndEdit.ChangeTempo(s, double.PositiveInfinity));
        Assert.Equal(4410 / 8, DataFrames(SndEdit.ChangeSpeed(s, 800)!, 2)); // past the default 400% is fine
        Assert.Equal(4410, DataFrames(SndEdit.ChangePitch(s, 30)!, 2));       // past the default 24 st too
    }

    private static short Sample16(byte[] wav, int frame, int channels = 1, int channel = 0) =>
        BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(44 + (frame * channels + channel) * 2));

    private static SoundEntry Entry(byte[] wav)
    {
        var e = new SoundEntry { Data = wav };
        SndReader.Describe(e);
        return e;
    }

    [Fact]
    public void ReverseFlipsFrameOrderExactly()
    {
        var s = Tone(440, 44100, 1000, channels: 2);
        var reversed = SndEdit.Reverse(s)!;
        Assert.Equal(Sample16(s.Data, 0, 2, 1), Sample16(reversed, 999, 2, 1));
        Assert.Equal(Sample16(s.Data, 999, 2, 0), Sample16(reversed, 0, 2, 0));
        Assert.Equal(s.Data, SndEdit.Reverse(Entry(reversed))); // twice = original, byte for byte
    }

    [Fact]
    public void InsertSilencePadsBothEnds()
    {
        var s = Tone(440, 44100, 1000, channels: 2);
        var padded = Entry(SndEdit.InsertSilence(s, 100, 50)!);
        Assert.Equal(1150, SndEdit.FrameCount(padded));
        Assert.Equal(0, Sample16(padded.Data, 99, 2, 1));
        Assert.Equal(Sample16(s.Data, 0, 2, 1), Sample16(padded.Data, 100, 2, 1));
        Assert.Equal(Sample16(s.Data, 999, 2, 0), Sample16(padded.Data, 1099, 2, 0));
        Assert.Equal(0, Sample16(padded.Data, 1149, 2, 0));
        Assert.Null(SndEdit.InsertSilence(s, 0, 0));
    }

    [Fact]
    public void FadesReachSilenceAtTheirEnd()
    {
        var s = Tone(0, 44100, 1000); // sin(0) = 0 everywhere, so use a DC level instead:
        for (int i = 0; i < 1000; i++) BinaryPrimitives.WriteInt16LittleEndian(s.Data.AsSpan(44 + i * 2), 16000);

        var fadeIn = SndEdit.Fade(s, fadeIn: true, 100, FadeCurve.Linear)!;
        Assert.Equal(0, Sample16(fadeIn, 0));
        Assert.InRange(Sample16(fadeIn, 50), 7900, 8100); // halfway = half level
        Assert.Equal(16000, Sample16(fadeIn, 100));       // past the fade: untouched

        var fadeOut = SndEdit.Fade(s, fadeIn: false, 100, FadeCurve.EqualPower)!;
        Assert.Equal(0, Sample16(fadeOut, 999));
        Assert.InRange(Sample16(fadeOut, 950), 11000, 11500); // sin(45°) ≈ 0.707 of the level
        Assert.Equal(16000, Sample16(fadeOut, 899));

        Assert.Null(SndEdit.Fade(s, true, 1001, FadeCurve.Linear)); // longer than the sound
    }

    [Fact]
    public void ChangeBitDepthRewritesSamplesAndHeader()
    {
        var s = Tone(440, 44100, 4410);
        var eight = SndEdit.ChangeBitDepth(s, 8, dither: true)!;
        Assert.Equal(8, BinaryPrimitives.ReadUInt16LittleEndian(eight.AsSpan(34)));   // BitsPerSample
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(eight.AsSpan(32)));   // BlockAlign
        Assert.Equal(44100, BinaryPrimitives.ReadInt32LittleEndian(eight.AsSpan(28))); // ByteRate
        Assert.Equal(4410, BinaryPrimitives.ReadInt32LittleEndian(eight.AsSpan(40)));  // data size

        var back = Entry(eight);
        Assert.Equal(4410, SndEdit.FrameCount(back));
        var sixteen = SndEdit.ChangeBitDepth(back, 16, dither: false)!;
        Assert.InRange(MiddlePeak(sixteen), 0.47, 0.53); // level survives the round trip

        Assert.Null(SndEdit.ChangeBitDepth(s, 16, true)); // already 16
        Assert.Null(SndEdit.ChangeBitDepth(s, 32, true)); // Ikemen plays 32-bit only as float
        Assert.Equal(eight, SndEdit.ChangeBitDepth(s, 8, dither: true)); // same input, same output
    }

    [Fact]
    public void RangeEditsLeaveTheRestByteForByte()
    {
        var s = Tone(440, 44100, 4410);
        var louder = SndEdit.ApplyToRange(s, 1000, 2000, 0, e => SndEdit.SetVolume(e, 0.5f))!;
        Assert.Equal(s.Data.AsSpan(44, 1000 * 2).ToArray(), louder.AsSpan(44, 1000 * 2).ToArray());
        Assert.Equal(s.Data.AsSpan(44 + 2000 * 2).ToArray(), louder.AsSpan(44 + 2000 * 2).ToArray());
        Assert.InRange(Sample16(louder, 1500), Sample16(s.Data, 1500) / 2 - 1, Sample16(s.Data, 1500) / 2 + 1);

        // A length-changing edit on a range: the rest shifts, still untouched.
        var faster = Entry(SndEdit.ApplyToRange(s, 1000, 2000, 0, e => SndEdit.ChangeSpeed(e, 200))!);
        Assert.Equal(4410 - 500, SndEdit.FrameCount(faster));
        Assert.Equal(Sample16(s.Data, 2000), Sample16(faster.Data, 1500));
    }

    [Fact]
    public void RangeCrossfadeBlendsTheEdges()
    {
        var s = Tone(0, 44100, 1000);
        for (int i = 0; i < 1000; i++) BinaryPrimitives.WriteInt16LittleEndian(s.Data.AsSpan(44 + i * 2), 16000);
        var silenced = SndEdit.ApplyToRange(s, 400, 600, 10, e => SndEdit.SetVolume(e, 0.01f))!;
        Assert.Equal(16000, Sample16(silenced, 399));                        // before the range: untouched
        Assert.InRange(Sample16(silenced, 404), 6000, 10000);                // mid-blend at the leading edge
        Assert.InRange(Sample16(silenced, 500), 150, 170);                   // inside: edited
        Assert.InRange(Sample16(silenced, 595), 6000, 10000);                // mid-blend at the trailing edge
        Assert.Equal(16000, Sample16(silenced, 600));                        // after: untouched
    }

    [Fact]
    public void ExtractRangeCutsAPlayableWav()
    {
        var s = Tone(440, 44100, 4410, channels: 2);
        var part = Entry(SndEdit.ExtractRange(s, 100, 300)!);
        Assert.Null(part.Problem);
        Assert.Equal(200, SndEdit.FrameCount(part));
        Assert.Equal(Sample16(s.Data, 100, 2, 1), Sample16(part.Data, 0, 2, 1));
        Assert.Null(SndEdit.ExtractRange(s, 300, 300));
        Assert.Null(SndEdit.ExtractRange(s, 0, 4411));
    }

    [Fact]
    public void MaximizesTheLoudestSampleToFullScale()
    {
        var s = new SoundEntry { Data = MakeWav(frames: 4) };
        SndReader.Describe(s);
        // Peak 8192/32768 = 0.25, so the gain must be exactly 4.
        BinaryPrimitives.WriteInt16LittleEndian(s.Data.AsSpan(44), 100);
        BinaryPrimitives.WriteInt16LittleEndian(s.Data.AsSpan(46), -8192);
        BinaryPrimitives.WriteInt16LittleEndian(s.Data.AsSpan(48), 4096);
        BinaryPrimitives.WriteInt16LittleEndian(s.Data.AsSpan(50), 0);

        var louder = SndEdit.MaximizeVolume(s, out var gain);

        Assert.NotNull(louder);
        Assert.Equal(4f, gain, 5);
        Assert.Equal(400, BinaryPrimitives.ReadInt16LittleEndian(louder.AsSpan(44)));
        Assert.Equal(short.MinValue, BinaryPrimitives.ReadInt16LittleEndian(louder.AsSpan(46))); // the peak, at full scale
        Assert.Equal(16384, BinaryPrimitives.ReadInt16LittleEndian(louder.AsSpan(48)));
        Assert.Equal(0, BinaryPrimitives.ReadInt16LittleEndian(louder.AsSpan(50)));
        Assert.Equal(s.Data.Length, louder.Length); // header and framing untouched
    }

    [Fact]
    public void MaximizeVolumeSkipsWhatIsAlreadyLoudOrSilent()
    {
        var full = new SoundEntry { Data = MakeWav(frames: 2) };
        SndReader.Describe(full);
        BinaryPrimitives.WriteInt16LittleEndian(full.Data.AsSpan(44), short.MaxValue);
        Assert.Null(SndEdit.MaximizeVolume(full, out var fullGain));
        Assert.Equal(1f, fullGain);

        var silent = new SoundEntry { Data = MakeWav(frames: 2) };
        SndReader.Describe(silent);
        Assert.Null(SndEdit.MaximizeVolume(silent, out var silentGain));
        Assert.Equal(1f, silentGain);
    }

    [Fact]
    public void VolumeEditReadsAndWritesFloatWavSamples()
    {
        var wav = MakeWav(bits: 32, frames: 2);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(20), 3);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(44), BitConverter.SingleToInt32Bits(0.25f));
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(48), BitConverter.SingleToInt32Bits(-0.5f));
        var entry = Entry(wav);

        var result = SndEdit.SetVolume(entry, 0.5f)!;

        Assert.Equal(0.125f, BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(result.AsSpan(44))));
        Assert.Equal(-0.25f, BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(result.AsSpan(48))));
        Assert.Equal(3, BinaryPrimitives.ReadUInt16LittleEndian(result.AsSpan(20)));
    }

    [Fact]
    public void ResamplesFloatWavWithoutChangingItsEncoding()
    {
        const int rate = 8000;
        var wav = MakeWav(rate, 1, 32, 80);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(20), 3);
        for (int i = 0; i < 80; i++)
            BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(44 + i * 4), BitConverter.SingleToInt32Bits(0.25f));
        var entry = Entry(wav);

        var result = SndEdit.Resample(entry, 4000)!;

        Assert.Equal(3, BinaryPrimitives.ReadUInt16LittleEndian(result.AsSpan(20)));
        Assert.Equal(40, BinaryPrimitives.ReadInt32LittleEndian(result.AsSpan(40)) / 4);
        var sample = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(result.AsSpan(44 + 20 * 4)));
        Assert.InRange(sample, 0.23f, 0.27f);
    }

    [Fact]
    public void ExtensibleFloatRequiresTheCompleteSubformatGuid()
    {
        static byte[] MakeExtensibleFloat(byte finalGuidByte)
        {
            var basic = MakeWav(bits: 32, frames: 2);
            var extensible = new byte[basic.Length + 24];
            basic.AsSpan(0, 36).CopyTo(extensible);
            BinaryPrimitives.WriteUInt32LittleEndian(extensible.AsSpan(16), 40);
            BinaryPrimitives.WriteUInt16LittleEndian(extensible.AsSpan(20), 0xfffe);
            BinaryPrimitives.WriteUInt16LittleEndian(extensible.AsSpan(36), 22);
            BinaryPrimitives.WriteUInt16LittleEndian(extensible.AsSpan(38), 32);
            byte[] guid = [3, 0, 0, 0, 0, 0, 0x10, 0, 0x80, 0, 0, 0xaa, 0, 0x38, 0x9b, finalGuidByte];
            guid.CopyTo(extensible, 44);
            basic.AsSpan(36).CopyTo(extensible.AsSpan(60));
            BinaryPrimitives.WriteInt32LittleEndian(extensible.AsSpan(4), extensible.Length - 8);
            return extensible;
        }

        var valid = Entry(MakeExtensibleFloat(0x71));
        Assert.Equal(3, valid.FormatTag);
        Assert.NotNull(SndEdit.SetVolume(valid, 0.5f));

        var invalid = Entry(MakeExtensibleFloat(0x70));
        Assert.Equal(0xfffe, invalid.FormatTag);
        Assert.Null(SndEdit.IncreaseLoudness(invalid, SndEdit.DefaultLoudnessAmount, out _, out var reason));
        Assert.Equal("format", reason);
    }

    [Fact]
    public void SampleEditPreservesChunksAfterData()
    {
        var basic = MakeWav(frames: 2);
        var wav = new byte[basic.Length + 12];
        basic.CopyTo(wav, 0);
        "LIST"u8.CopyTo(wav.AsSpan(basic.Length));
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(basic.Length + 4), 4);
        "INFO"u8.CopyTo(wav.AsSpan(basic.Length + 8));
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4), wav.Length - 8);
        var entry = Entry(wav);

        var result = SndEdit.SetVolume(entry, 0.5f)!;

        Assert.Equal("LIST"u8.ToArray(), result.AsSpan(result.Length - 12, 4).ToArray());
        Assert.Equal(4, BinaryPrimitives.ReadInt32LittleEndian(result.AsSpan(result.Length - 8)));
        Assert.Equal("INFO"u8.ToArray(), result.AsSpan(result.Length - 4, 4).ToArray());
        Assert.Equal(result.Length - 8, BinaryPrimitives.ReadInt32LittleEndian(result.AsSpan(4)));
    }

    [Fact]
    public void ConvertsFloatWavToIntegerBitDepth()
    {
        var wav = MakeWav(bits: 32, frames: 2);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(20), 3);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(44), BitConverter.SingleToInt32Bits(0.25f));
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(48), BitConverter.SingleToInt32Bits(-0.5f));
        var entry = Entry(wav);

        var result = SndEdit.ChangeBitDepth(entry, 16, dither: false)!;

        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(result.AsSpan(20)));
        Assert.Equal(8192, BinaryPrimitives.ReadInt16LittleEndian(result.AsSpan(44)));
        Assert.Equal(-16384, BinaryPrimitives.ReadInt16LittleEndian(result.AsSpan(46)));
    }

    [Fact]
    public void MeasuresKnownOneKilohertzSineAndStereoChannelWeighting()
    {
        var mono = Tone(1000, 48000, 48000, 0.5);
        var stereo = Tone(1000, 48000, 48000, 0.5, channels: 2);
        double monoLufs = SndEdit.MeasureIntegratedLoudness(mono, out var monoReason);
        double stereoLufs = SndEdit.MeasureIntegratedLoudness(stereo, out var stereoReason);

        Assert.Null(monoReason);
        Assert.Null(stereoReason);
        Assert.InRange(monoLufs, -9.1, -9.0);
        Assert.Equal(10 * Math.Log10(2), stereoLufs - monoLufs, 2);
    }

    [Fact]
    public void LoudnessTracksAmplitudeAndAppliesAbsoluteAndRelativeGates()
    {
        var halfAmplitude = Tone(1000, 48000, 48000, 0.5);
        var quarterAmplitude = Tone(1000, 48000, 48000, 0.25);
        double halfLufs = SndEdit.MeasureIntegratedLoudness(halfAmplitude, out _);
        double quarterLufs = SndEdit.MeasureIntegratedLoudness(quarterAmplitude, out _);
        Assert.Equal(-6.0206, quarterLufs - halfLufs, 2);

        var gated = Tone(1000, 48000, 48000, 0.0001);
        Assert.True(double.IsNegativeInfinity(SndEdit.MeasureIntegratedLoudness(gated, out var gatedReason)));
        Assert.Equal("below-gate", gatedReason);

        var allZero = new SoundEntry { Data = MakeWav(48000, 1, 16, 48000) };
        Assert.True(double.IsNegativeInfinity(SndEdit.MeasureIntegratedLoudness(allZero, out var zeroMeasureReason)));
        Assert.Equal("silent", zeroMeasureReason);
        Assert.Null(SndEdit.IncreaseLoudness(allZero, SndEdit.DefaultLoudnessAmount, out _, out var zeroEditReason));
        Assert.Equal("silent", zeroEditReason);

        var unsignedSilence = new SoundEntry { Data = MakeWav(48000, 1, 8, 48000) };
        Array.Fill(unsignedSilence.Data, (byte)128, 44, unsignedSilence.Data.Length - 44);
        Assert.True(double.IsNegativeInfinity(SndEdit.MeasureIntegratedLoudness(unsignedSilence, out var unsignedReason)));
        Assert.Equal("silent", unsignedReason);

        var lowerButAudible = Tone(1000, 48000, 48000, 0.001);
        Assert.True(double.IsFinite(SndEdit.MeasureIntegratedLoudness(lowerButAudible, out var audibleReason)));
        Assert.Null(audibleReason);

        var mixed = new SoundEntry { Data = MakeWav(48000, 1, 16, 48000) };
        for (int i = 0; i < 48000; i++)
        {
            double amp = i < 24000 ? 0.5 : 0.005;
            short sample = (short)Math.Round(amp * 32767 * Math.Sin(2 * Math.PI * 1000 * i / 48000));
            BinaryPrimitives.WriteInt16LittleEndian(mixed.Data.AsSpan(44 + i * 2), sample);
        }
        SndReader.Describe(mixed);
        double mixedLufs = SndEdit.MeasureIntegratedLoudness(mixed, out _);
        // Relative gating excludes the all-quiet blocks, but the mixed high/low
        // blocks remain in the integrated average.
        Assert.InRange(mixedLufs, halfLufs - 2.0, halfLufs - 1.0);
    }

    [Fact]
    public void UsesOneWholeClipBlockForAudioShorterThan400Milliseconds()
    {
        var shortTone = Tone(1000, 48000, 4800, 0.5); // 100 ms
        double lufs = SndEdit.MeasureIntegratedLoudness(shortTone, out var reason);

        Assert.Null(reason);
        Assert.InRange(lufs, -9.6, -9.0);
    }

    [Fact]
    public void BoostsQuietAudioSmoothlyWithoutChangingDigitalSilence()
    {
        const int rate = 48000;
        var quiet = new SoundEntry { Data = MakeWav(rate, 1, 16, rate) };
        for (int i = 0; i < rate; i++)
        {
            // A short exact-silence lead-in followed by a quiet tone.
            short sample = i < 2400 ? (short)0 : (short)Math.Round(0.001 * 32767 * Math.Sin(2 * Math.PI * 1000 * i / rate));
            BinaryPrimitives.WriteInt16LittleEndian(quiet.Data.AsSpan(44 + i * 2), sample);
        }
        SndReader.Describe(quiet);
        var output = SndEdit.IncreaseLoudness(quiet, SndEdit.DefaultLoudnessAmount, out var result, out var reason);

        Assert.NotNull(output);
        Assert.Null(reason);
        Assert.True(result.OutputLufs > result.InputLufs);
        for (int i = 0; i < 2400; i++)
            Assert.Equal((short)0, BinaryPrimitives.ReadInt16LittleEndian(output!.AsSpan(44 + i * 2)));
        int sourceQuietPeak = 0, outputQuietPeak = 0;
        long sourceAbsSum = 0, outputAbsSum = 0;
        for (int i = 2400; i < rate; i++)
        {
            int sourceSample = Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(quiet.Data.AsSpan(44 + i * 2)));
            sourceQuietPeak = Math.Max(sourceQuietPeak, sourceSample);
            sourceAbsSum += sourceSample;
            short sample = BinaryPrimitives.ReadInt16LittleEndian(output!.AsSpan(44 + i * 2));
            outputQuietPeak = Math.Max(outputQuietPeak, Math.Abs((int)sample));
            outputAbsSum += Math.Abs((int)sample);
            Assert.InRange(sample, short.MinValue, short.MaxValue);
        }
        // The loudest existing sample anchors the curve, so the peak itself doesn't move...
        Assert.InRange(outputQuietPeak, sourceQuietPeak - 1, sourceQuietPeak + 1);
        // ...but everything below it, including this quiet tail, gets pulled up.
        Assert.True(outputAbsSum > sourceAbsSum);
    }
    [Fact]
    public void KeepsPcmSamplesInRangeAndRejectsUnsupportedWavFormats()
    {
        var clipped = Tone(1000, 48000, 48000, 0.01);
        var output = SndEdit.IncreaseLoudness(clipped, SndEdit.DefaultLoudnessAmount, out var result, out var reason);
        Assert.NotNull(output);
        Assert.Null(reason);
        for (int p = 44; p + 1 < output!.Length; p += 2)
        {
            short sample = BinaryPrimitives.ReadInt16LittleEndian(output.AsSpan(p));
            Assert.InRange(sample, short.MinValue, short.MaxValue);
        }

        var floatWav = Tone(1000, 48000, 48000).Data;
        BinaryPrimitives.WriteUInt16LittleEndian(floatWav.AsSpan(20), 3); // IEEE float, not integer PCM
        var floatEntry = new SoundEntry { Data = floatWav };
        Assert.Null(SndEdit.IncreaseLoudness(floatEntry, SndEdit.DefaultLoudnessAmount, out _, out var floatReason));
        Assert.Equal("format", floatReason);

        var threeChannel = new SoundEntry { Data = MakeWav(48000, 3, 16, 48000) };
        Assert.Null(SndEdit.IncreaseLoudness(threeChannel, SndEdit.DefaultLoudnessAmount, out _, out var channelReason));
        Assert.Equal("format", channelReason);
    }

    [Fact]
    public void AcceptsPcmInAWaveFormatExtensibleChunk()
    {
        var basic = Tone(1000, 48000, 4800).Data;
        var extensible = new byte[basic.Length + 24];
        basic.AsSpan(0, 36).CopyTo(extensible);
        BinaryPrimitives.WriteUInt32LittleEndian(extensible.AsSpan(16), 40);
        BinaryPrimitives.WriteUInt16LittleEndian(extensible.AsSpan(20), 0xfffe);
        BinaryPrimitives.WriteUInt16LittleEndian(extensible.AsSpan(36), 22); // extension size
        BinaryPrimitives.WriteUInt16LittleEndian(extensible.AsSpan(38), 16); // valid bits
        BinaryPrimitives.WriteUInt32LittleEndian(extensible.AsSpan(40), 4); // front-center mask
        byte[] pcmGuid = [1, 0, 0, 0, 0, 0, 0x10, 0, 0x80, 0, 0, 0xaa, 0, 0x38, 0x9b, 0x71];
        pcmGuid.CopyTo(extensible, 44);
        basic.AsSpan(36).CopyTo(extensible.AsSpan(60));
        BinaryPrimitives.WriteInt32LittleEndian(extensible.AsSpan(4), extensible.Length - 8);

        var entry = new SoundEntry { Data = extensible };
        double lufs = SndEdit.MeasureIntegratedLoudness(entry, out var reason);
        Assert.Null(reason);
        Assert.InRange(lufs, -9.6, -9.0);
    }

    [Fact]
    public void PreservesNoOpAndHeaderReasonBehavior()
    {
        // Amount 1 is the identity end of the curve (k=1): nothing should change.
        var alreadyLoud = Tone(1000, 48000, 48000, 0.5);
        Assert.Null(SndEdit.IncreaseLoudness(alreadyLoud, SndEdit.MinLoudnessAmount, out var noOp, out var noOpReason));
        Assert.Equal("already-loud", noOpReason);
        Assert.Equal(noOp.InputLufs, noOp.OutputLufs);

        var invalid = new SoundEntry { Data = new byte[64] };
        Assert.Null(SndEdit.IncreaseLoudness(invalid, SndEdit.DefaultLoudnessAmount, out _, out var headerReason));
        Assert.Equal("header", headerReason);
        Assert.True(double.IsNaN(SndEdit.MeasureIntegratedLoudness(invalid, out var measureReason)));
        Assert.Equal("header", measureReason);
    }

    [Fact]
    public void RejectsAmountOutsideItsRange()
    {
        var tone = Tone(1000, 48000, 4800, 0.5);
        Assert.Throws<ArgumentOutOfRangeException>(() => SndEdit.IncreaseLoudness(tone, 0, out _, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => SndEdit.IncreaseLoudness(tone, 101, out _, out _));
    }

    [Fact]
    public void NeverLiftsAQuietSectionNearTheClipsOwnPeak()
    {
        // Regression test for the old envelope-boost bug: a quiet section next to a much
        // louder one used to get chased toward a fixed target and could spike close to
        // full scale. The curve is anchored on the clip's own peak, so even the strongest
        // amount should leave a quiet section well below it, not spiking up to meet it.
        const int rate = 48000;
        var mixed = new SoundEntry { Data = MakeWav(rate, 1, 16, rate) };
        for (int i = 0; i < rate; i++)
        {
            double amp = i < rate / 2 ? 0.5 : 0.001; // loud half, then a much quieter half
            short sample = (short)Math.Round(amp * 32767 * Math.Sin(2 * Math.PI * 1000 * i / rate));
            BinaryPrimitives.WriteInt16LittleEndian(mixed.Data.AsSpan(44 + i * 2), sample);
        }
        SndReader.Describe(mixed);

        var output = SndEdit.IncreaseLoudness(mixed, SndEdit.MaxLoudnessAmount, out _, out var reason);
        Assert.NotNull(output);
        Assert.Null(reason);

        int loudPeak = 0, quietSourcePeak = 0, quietOutputPeak = 0;
        for (int i = 0; i < rate / 2; i++)
            loudPeak = Math.Max(loudPeak, Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(output!.AsSpan(44 + i * 2))));
        // Only the settled tail: the envelope takes a few hundred ms to fully follow
        // the drop at the halfway point, so skip past that transition first.
        for (int i = 4 * rate / 5; i < rate; i++)
        {
            quietSourcePeak = Math.Max(quietSourcePeak, Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(mixed.Data.AsSpan(44 + i * 2))));
            quietOutputPeak = Math.Max(quietOutputPeak, Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(output!.AsSpan(44 + i * 2))));
        }

        Assert.True(quietOutputPeak > quietSourcePeak * 10); // really did get boosted...
        Assert.True(quietOutputPeak < loudPeak * 3 / 5);      // ...but nowhere near spiking up to the peak
    }

    [Fact]
    public void KeepsGainSteadyAcrossASteadyToneInsteadOfReshapingEachCycle()
    {
        // Regression test for the waveshaping bug: applying the curve to each raw
        // sample (rather than to a smoothed envelope) reshaped the waveform inside
        // every cycle, which is audible as distortion even though no sample ever
        // reaches full scale. Once the envelope has settled on a steady tone, the
        // gain from sample to sample should be close to constant, not swinging wildly
        // near every zero crossing.
        const int rate = 48000;
        var mixed = new SoundEntry { Data = MakeWav(rate, 1, 16, rate) };
        for (int i = 0; i < rate; i++)
        {
            double amp = i < rate / 5 ? 0.5 : 0.05; // brief loud lead-in sets the peak, then a steady quiet tone
            short sample = (short)Math.Round(amp * 32767 * Math.Sin(2 * Math.PI * 1000 * i / rate));
            BinaryPrimitives.WriteInt16LittleEndian(mixed.Data.AsSpan(44 + i * 2), sample);
        }
        SndReader.Describe(mixed);

        var output = SndEdit.IncreaseLoudness(mixed, SndEdit.MaxLoudnessAmount, out _, out var reason);
        Assert.NotNull(output);
        Assert.Null(reason);

        double minRatio = double.MaxValue, maxRatio = double.MinValue;
        for (int i = 4 * rate / 5; i < rate; i++) // settled tail of the steady tone
        {
            int inSample = BinaryPrimitives.ReadInt16LittleEndian(mixed.Data.AsSpan(44 + i * 2));
            if (Math.Abs(inSample) < 1000) continue; // skip near zero-crossings: the ratio is ill-conditioned there
            int outSample = BinaryPrimitives.ReadInt16LittleEndian(output!.AsSpan(44 + i * 2));
            double ratio = (double)outSample / inSample;
            minRatio = Math.Min(minRatio, ratio);
            maxRatio = Math.Max(maxRatio, ratio);
        }
        Assert.True(minRatio > 1.0); // still a real, meaningful boost...
        Assert.True(maxRatio / minRatio < 1.15); // ...but essentially one constant gain, not a per-sample curve
    }

    [Fact]
    public void WrittenLayoutMatchesSndmaker()
    {
        var file = new SndFile();
        file.Sounds.Add(new SoundEntry { Group = 1, Index = 2, Data = [1, 2, 3] });
        file.Sounds.Add(new SoundEntry { Group = -1, Index = 7, Data = [4, 5] });
        var tmp = Path.Combine(Path.GetTempPath(), $"layout-{Guid.NewGuid():N}.snd");
        try
        {
            SndWriter.Save(file, tmp);
            var b = File.ReadAllBytes(tmp);
            Assert.Equal(new byte[] { 4, 0, 0, 0 }, b[12..16]);
            Assert.Equal(512, BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(20)));
            int second = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(512));
            Assert.Equal(512 + 16 + 3, second);
            Assert.Equal(b.Length, BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(second))); // last points at the end

            var r = SndReader.Decode(b);
            Assert.Equal(2, r.Sounds.Count);
            Assert.Equal(-1, r.Sounds[1].Group);
            Assert.Equal(new byte[] { 4, 5 }, r.Sounds[1].Data);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void UnusedHeaderSpaceStaysZero()
    {
        var file = new SndFile();
        file.Sounds.Add(new SoundEntry { Group = 1, Index = 2, Data = [1, 2, 3] });
        var tmp = Path.Combine(Path.GetTempPath(), $"sndblank-{Guid.NewGuid():N}.snd");
        try
        {
            SndWriter.Save(file, tmp);
            Assert.All(File.ReadAllBytes(tmp)[24..512], x => Assert.Equal(0, x)); // as sndmaker leaves it
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void EchoRepeatsFadeAndLengthenTheSound()
    {
        Assert.Equal(7, SndEdit.AutoEchoRepeats(0.5, 1.0, 0.01, 16));  // 0.5^7 = 0.0078, first under the 0.01 silence
        Assert.Equal(6, SndEdit.AutoEchoRepeats(0.5, 0.5, 0.01, 16));  // a quieter part reaches silence sooner
        Assert.Equal(16, SndEdit.AutoEchoRepeats(0.99, 1.0, 0.01, 16)); // capped: never goes on for ever
        Assert.Equal(16, SndEdit.AutoEchoRepeats(1.0, 1.0, 0.01, 16));  // 100% never fades; the cap still ends it

        var s = Tone(440, 22050, 1000, 0.25);
        var echoed = new SoundEntry { Data = SndEdit.Echo(s, 0, 1000, 500, 0.5, 2)! };
        SndReader.Describe(echoed);
        Assert.Equal(2000, SndEdit.FrameCount(echoed));           // last echo ends 2 x 500 frames later

        var part = new SoundEntry { Data = SndEdit.Echo(s, 0, 100, 50, 0.5, 1)! };
        SndReader.Describe(part);
        Assert.Equal(1000, SndEdit.FrameCount(part));             // a selection's echo that ends inside keeps the length
        Assert.True(SndEdit.EchoPeak(s, 0, 1000, 1, 0.99, 32) > 1); // many loud overlapping repeats clip
    }
}
