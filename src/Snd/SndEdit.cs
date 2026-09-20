using System.Buffers.Binary;

namespace Snd;

public readonly record struct LoudnessResult(double InputLufs, double OutputLufs, double Amount);

/// <summary>Fade shape: Linear ramps evenly; EqualPower follows a quarter sine and sounds smoother.</summary>
public enum FadeCurve { Linear, EqualPower }

/// <summary>
/// Pure-byte WAV edits and PCM loudness processing. No decode library is used;
/// container bytes outside a rewritten "data" payload remain untouched.
/// </summary>
public static class SndEdit
{
    /// <summary>Amplitude below this (on the same -1..1 scale WaveformView uses) counts as silence. Kept in step with the editor's SilenceThreshold default.</summary>
    public const double DefaultSilenceThreshold = 0.01;

    /// <summary>Range and default for the Increase Loudness amount dial (1 = almost no change, 100 = strongest lift).</summary>
    public const double MinLoudnessAmount = 1, MaxLoudnessAmount = 100, DefaultLoudnessAmount = 50;

    // How fast the loudness envelope follows the signal. Slow relative to audio-rate
    // zero crossings on purpose: applying the curve to something that moves sample to
    // sample reshapes the waveform itself (distortion), not just its overall level.
    private const double LoudnessEnvelopeAttackSeconds = 0.005;
    private const double LoudnessEnvelopeReleaseSeconds = 0.100;
    /// <summary>
    /// Scales the PCM payload so its loudest sample reaches full scale.
    /// Returns the new file bytes and the gain applied, or null when the
    /// header can't be read, the sound is entirely silent, or it is already
    /// at (or within a rounding step of) full volume — gain is 1 then.
    /// </summary>
    public static byte[]? MaximizeVolume(SoundEntry entry, out float gain)
    {
        gain = 1f;
        if (entry.BitsPerSample == 0 || entry.Channels == 0 || entry.DataOffset < 4) return null;

        int bytesPerSample = Math.Max(entry.BitsPerSample, 8) / 8;
        int frameBytes = bytesPerSample * entry.Channels;
        if (frameBytes <= 0) return null;

        int offset = entry.DataOffset;
        var d = entry.Data;
        // Re-read the declared chunk size rather than trusting Data.Length - offset,
        // in case anything follows the data chunk (matches SndReader's own clamp).
        uint declared = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(offset - 4, 4));
        int dataLen = (int)Math.Min(declared, (uint)Math.Max(0, d.Length - offset));
        int totalFrames = dataLen / frameBytes;
        if (totalFrames <= 0) return null;

        float peak = 0;
        for (int frame = 0; frame < totalFrames; frame++)
        {
            int baseOff = offset + frame * frameBytes;
            for (int ch = 0; ch < entry.Channels; ch++)
                peak = Math.Max(peak, Math.Abs(ReadSample(d, baseOff + ch * bytesPerSample, entry.BitsPerSample, entry.FormatTag)));
        }
        if (peak <= 0) return null; // entirely silent — there is nothing to scale
        if (peak >= 0.999f) return null; // already at full volume; a rewrite would only jitter rounding

        gain = 1f / peak;

        var result = (byte[])d.Clone(); // same length, so every size field stays valid untouched
        for (int frame = 0; frame < totalFrames; frame++)
        {
            int baseOff = offset + frame * frameBytes;
            for (int ch = 0; ch < entry.Channels; ch++)
            {
                int pos = baseOff + ch * bytesPerSample;
                WriteSample(result, pos, entry.BitsPerSample, ReadSample(d, pos, entry.BitsPerSample, entry.FormatTag) * gain, entry.FormatTag);
            }
        }
        return result;
    }

    /// <summary>
    /// ITU-R BS.1770-5 K-weighted integrated loudness with absolute and relative
    /// gating. Clips shorter than 400 ms use one whole-clip block, an explicit
    /// SFX extension. This meter is independent of the quiet-part boost.
    /// </summary>
    public static double MeasureIntegratedLoudness(SoundEntry entry, out string? reason)
    {
        reason = null;
        if (!TryReadPcm(entry, out var pcm, out reason)) return double.NaN;
        var channels = DecodeChannels(pcm);
        if (IsDigitalSilence(channels))
        {
            reason = "silent";
            return double.NegativeInfinity;
        }
        double loudness = IntegratedLoudness(channels, pcm.SampleRate);
        if (double.IsNegativeInfinity(loudness)) reason = "below-gate";
        return loudness;
    }

    /// <summary>
    /// Pulls quiet parts up toward the clip's own peak with a curve — not a fixed
    /// target. A peak-following envelope maps through target = peak * (envelope/peak)^k
    /// to get a gain, applied to the raw sample: 0 stays 0, the loudest existing sample
    /// is (by construction, with a safety clamp for envelope lag) left unchanged, and
    /// quieter passages get progressively less lift the closer they already are to the
    /// peak. k comes from the 1-100 amount. The envelope is smoothed over tens of
    /// milliseconds rather than tracking each sample — applying the curve directly to
    /// individual samples reshapes the waveform inside every cycle (audible as
    /// distortion, even with no sample ever hitting the ceiling); following a slow
    /// envelope instead changes the level without changing the waveform's shape. A
    /// frame's channels share one gain (linked to that frame's loudest channel) so the
    /// stereo image holds.
    /// </summary>
    public static byte[]? IncreaseLoudness(SoundEntry entry, double amount,
        out LoudnessResult result, out string? reason)
    {
        result = default;
        reason = null;
        if (!double.IsFinite(amount) || amount < MinLoudnessAmount || amount > MaxLoudnessAmount)
            throw new ArgumentOutOfRangeException(nameof(amount),
                $"Amount must be between {MinLoudnessAmount:0} and {MaxLoudnessAmount:0}.");
        if (!TryReadPcm(entry, out var pcm, out reason)) return null;

        var frames = DecodeChannels(pcm);
        if (IsDigitalSilence(frames))
        {
            reason = "silent";
            return null;
        }

        double inputLufs = IntegratedLoudness(frames, pcm.SampleRate);
        var boosted = ApplyLoudnessCurve(frames, pcm.SampleRate, amount);
        var bytes = (byte[])entry.Data.Clone();
        for (int frame = 0; frame < pcm.FrameCount; frame++)
            for (int ch = 0; ch < pcm.Channels; ch++)
                WritePcmSample(bytes, pcm.DataOffset + frame * pcm.FrameBytes + ch * pcm.BytesPerSample,
                    pcm.BitsPerSample, boosted[frame, ch]);

        if (bytes.AsSpan(pcm.DataOffset, pcm.DataLength).SequenceEqual(
                entry.Data.AsSpan(pcm.DataOffset, pcm.DataLength)))
        {
            result = new(inputLufs, inputLufs, amount);
            reason = "already-loud";
            return null;
        }

        var outputEntry = new SoundEntry { Data = bytes };
        if (!TryReadPcm(outputEntry, out var written, out _))
        {
            reason = "header";
            return null;
        }
        var writtenFrames = DecodeChannels(written);
        double outputLufs = IntegratedLoudness(writtenFrames, written.SampleRate);
        result = new(inputLufs, outputLufs, amount);
        return bytes;
    }

    private static double[,] ApplyLoudnessCurve(double[,] input, int sampleRate, double amount)
    {
        int frames = input.GetLength(0), channels = input.GetLength(1);
        var output = new double[frames, channels];
        double peak = SamplePeak(input);
        if (peak <= 0) return (double[,])input.Clone(); // fully silent; nothing to curve

        // amount=1 -> k close to 1 (no change); amount=100 -> k=0.2 (strong lift).
        double k = 1 - 0.8 * (amount - MinLoudnessAmount) / (MaxLoudnessAmount - MinLoudnessAmount);
        double attack = 1 - Math.Exp(-1 / (sampleRate * LoudnessEnvelopeAttackSeconds));
        double release = 1 - Math.Exp(-1 / (sampleRate * LoudnessEnvelopeReleaseSeconds));
        double envelope = 0;

        for (int frame = 0; frame < frames; frame++)
        {
            double framePeak = 0;
            for (int ch = 0; ch < channels; ch++) framePeak = Math.Max(framePeak, Math.Abs(input[frame, ch]));
            double step = framePeak > envelope ? attack : release;
            envelope += (framePeak - envelope) * step;

            double gain = 1;
            if (envelope > 0)
            {
                double target = peak * Math.Pow(envelope / peak, k);
                gain = target / envelope;
            }
            // The envelope lags the true instantaneous peak, so cap gain per frame
            // against it directly — this sample can never end up louder than the
            // clip's own peak, whatever the (smoothed) envelope says.
            if (framePeak > 0) gain = Math.Min(gain, peak / framePeak);
            for (int ch = 0; ch < channels; ch++) output[frame, ch] = input[frame, ch] * gain;
        }
        return output;
    }
    private sealed record PcmData(byte[] Data, int SampleRate, int Channels, int BitsPerSample,
        ushort FormatTag, int DataOffset, int DataLength)
    {
        public int BytesPerSample => BitsPerSample / 8;
        public int FrameBytes => Channels * BytesPerSample;
        public int FrameCount => DataLength / FrameBytes;
    }

    private static bool TryReadPcm(SoundEntry entry, out PcmData pcm, out string? reason)
    {
        pcm = null!;
        reason = "header";
        byte[] d = entry.Data;
        if (d.Length < 12 || !d.AsSpan(0, 4).SequenceEqual("RIFF"u8) ||
            !d.AsSpan(8, 4).SequenceEqual("WAVE"u8)) return false;

        int fmtBody = -1, dataOffset = -1;
        uint fmtSize = 0, dataSize = 0;
        long p = 12;
        while (p + 8 <= d.Length)
        {
            int chunk = (int)p;
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(chunk + 4, 4));
            long body = p + 8, end = body + size;
            if (end > d.Length || end < body) return false;
            if (d.AsSpan(chunk, 4).SequenceEqual("fmt "u8))
            {
                fmtBody = (int)body;
                fmtSize = size;
            }
            else if (d.AsSpan(chunk, 4).SequenceEqual("data"u8))
            {
                dataOffset = (int)body;
                dataSize = size;
            }
            p = end + (size & 1);
            if (size == 0) break;
        }
        if (fmtBody < 0 || fmtSize < 16 || dataOffset < 0) return false;

        ushort format = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(fmtBody));
        ushort channels = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(fmtBody + 2));
        uint rate = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(fmtBody + 4));
        ushort align = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(fmtBody + 12));
        ushort bits = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(fmtBody + 14));
        ushort validBits = bits;
        uint channelMask = 0;
        if (format == 0xfffe)
        {
            ReadOnlySpan<byte> pcmGuid = [1, 0, 0, 0, 0, 0, 0x10, 0, 0x80, 0, 0, 0xaa, 0, 0x38, 0x9b, 0x71];
            if (fmtSize < 40 || BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(fmtBody + 16)) < 22 ||
                !d.AsSpan(fmtBody + 24, 16).SequenceEqual(pcmGuid))
            {
                reason = "format";
                return false;
            }
            validBits = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(fmtBody + 18));
            channelMask = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(fmtBody + 20));
            format = 1;
        }
        uint expectedMask = channels == 1 ? 0x4u : 0x3u; // mono center; stereo left + right
        bool supportedMask = channelMask == 0 || channelMask == expectedMask
            || (channels == 1 && channelMask == 0x1u); // mono files are also commonly marked front-left
        if (format != 1 || (channels != 1 && channels != 2) ||
            (bits != 8 && bits != 16 && bits != 24 && bits != 32) || validBits != bits ||
            !supportedMask || rate < 4000 || rate > int.MaxValue || align != channels * (bits / 8))
        {
            reason = "format";
            return false;
        }
        if (dataSize % align != 0)
        {
            reason = "format";
            return false;
        }
        reason = null;
        pcm = new(d, (int)rate, channels, bits, format, dataOffset, (int)dataSize);
        return true;
    }

    private static bool IsDigitalSilence(double[,] samples)
    {
        for (int frame = 0; frame < samples.GetLength(0); frame++)
            for (int ch = 0; ch < samples.GetLength(1); ch++)
                if (samples[frame, ch] != 0) return false;
        return true;
    }

    private static double[,] DecodeChannels(PcmData pcm)
    {
        var frames = new double[pcm.FrameCount, pcm.Channels];
        for (int f = 0; f < pcm.FrameCount; f++)
            for (int ch = 0; ch < pcm.Channels; ch++)
                frames[f, ch] = ReadPcmSample(pcm.Data,
                    pcm.DataOffset + f * pcm.FrameBytes + ch * pcm.BytesPerSample, pcm.BitsPerSample);
        return frames;
    }

    private static double ReadPcmSample(byte[] d, int position, int bits) => bits switch
    {
        8 => (d[position] - 128) / 128.0,
        16 => BinaryPrimitives.ReadInt16LittleEndian(d.AsSpan(position, 2)) / 32768.0,
        24 => Sign24(d[position] | (d[position + 1] << 8) | (d[position + 2] << 16)) / 8388608.0,
        32 => BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(position, 4)) / 2147483648.0,
        _ => 0,
    };

    private static void WritePcmSample(byte[] d, int position, int bits, double value)
    {
        value = Math.Clamp(value, -1, 1);
        switch (bits)
        {
            case 8:
                d[position] = (byte)Math.Round((value + 1) / 2 * 255);
                break;
            case 16:
                BinaryPrimitives.WriteInt16LittleEndian(d.AsSpan(position, 2),
                    (short)Math.Clamp(Math.Round(value * 32768), short.MinValue, short.MaxValue));
                break;
            case 24:
                int sample24 = (int)Math.Clamp(Math.Round(value * 8388608), -8388608, 8388607);
                d[position] = (byte)sample24;
                d[position + 1] = (byte)(sample24 >> 8);
                d[position + 2] = (byte)(sample24 >> 16);
                break;
            case 32:
                int sample32 = (int)Math.Clamp(Math.Round(value * 2147483648d), int.MinValue, (double)int.MaxValue);
                BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(position, 4), sample32);
                break;
        }
    }

    private static double IntegratedLoudness(double[,] samples, int sampleRate, bool applyGates = true)
    {
        int frameCount = samples.GetLength(0), channels = samples.GetLength(1);
        if (frameCount == 0) return double.NegativeInfinity;
        var weighted = new double[frameCount, channels];
        for (int ch = 0; ch < channels; ch++)
        {
            var signal = new double[frameCount];
            for (int frame = 0; frame < frameCount; frame++) signal[frame] = samples[frame, ch];
            ApplyBiquad(signal, KWeightingShelf(sampleRate));
            ApplyBiquad(signal, KWeightingHighPass(sampleRate));
            for (int frame = 0; frame < frameCount; frame++) weighted[frame, ch] = signal[frame];
        }

        int blockFrames = Math.Max(1, (int)Math.Round(sampleRate * 0.4));
        int hopFrames = Math.Max(1, (int)Math.Round(sampleRate * 0.1));
        var energies = new List<double>();
        int block = Math.Min(blockFrames, frameCount);
        for (int start = 0; start + block <= frameCount; start += frameCount < blockFrames ? frameCount : hopFrames)
        {
            double energy = 0;
            for (int frame = start; frame < start + block; frame++)
                for (int ch = 0; ch < channels; ch++)
                    energy += weighted[frame, ch] * weighted[frame, ch];
            energy /= block;
            if (!applyGates || LufsFromEnergy(energy) >= -70) energies.Add(energy);
            if (frameCount < blockFrames) break;
        }
        if (energies.Count == 0) return double.NegativeInfinity;
        if (!applyGates) return LufsFromEnergy(energies.Average());

        double preliminary = LufsFromEnergy(energies.Average());
        double gate = Math.Max(-70, preliminary - 10);
        double sum = 0;
        int retained = 0;
        foreach (double energy in energies)
        {
            if (LufsFromEnergy(energy) < gate) continue;
            sum += energy;
            retained++;
        }
        return retained == 0 ? double.NegativeInfinity : LufsFromEnergy(sum / retained);
    }

    private static double LufsFromEnergy(double energy) =>
        energy <= 0 ? double.NegativeInfinity : -0.691 + 10 * Math.Log10(energy);

    private readonly record struct Biquad(double B0, double B1, double B2, double A1, double A2);

    private static Biquad KWeightingShelf(int sampleRate)
    {
        const double frequency = 1681.974450955533;
        const double gainDb = 3.999843853973347;
        const double q = 0.7071752369554196;
        double k = Math.Tan(Math.PI * frequency / sampleRate);
        double vh = Math.Pow(10, gainDb / 20);
        double vb = Math.Pow(vh, 0.4996667741545416);
        double a0 = 1 + k / q + k * k;
        return new(
            (vh + vb * k / q + k * k) / a0,
            2 * (k * k - vh) / a0,
            (vh - vb * k / q + k * k) / a0,
            2 * (k * k - 1) / a0,
            (1 - k / q + k * k) / a0);
    }

    private static Biquad KWeightingHighPass(int sampleRate)
    {
        const double frequency = 38.13547087602444;
        const double q = 0.5003270373238773;
        double k = Math.Tan(Math.PI * frequency / sampleRate);
        double a0 = 1 + k / q + k * k;
        return new(
            1 / a0,
            -2 / a0,
            1 / a0,
            2 * (k * k - 1) / a0,
            (1 - k / q + k * k) / a0);
    }

    private static void ApplyBiquad(double[] samples, Biquad c)
    {
        double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            double x = samples[i];
            double y = c.B0 * x + c.B1 * x1 + c.B2 * x2 - c.A1 * y1 - c.A2 * y2;
            samples[i] = y;
            x2 = x1; x1 = x; y2 = y1; y1 = y;
        }
    }

    private static double SamplePeak(double[,] samples)
    {
        double peak = 0;
        for (int frame = 0; frame < samples.GetLength(0); frame++)
            for (int ch = 0; ch < samples.GetLength(1); ch++)
                peak = Math.Max(peak, Math.Abs(samples[frame, ch]));
        return peak;
    }

    /// <summary>
    /// Scales the PCM payload by an exact gain (1.0 = unchanged), clipping any
    /// sample that would exceed full scale. Returns the new file bytes, or null
    /// when the header can't be read or the gain is close enough to 1 to be a no-op.
    /// </summary>
    public static byte[]? SetVolume(SoundEntry entry, float gain)
    {
        if (entry.BitsPerSample == 0 || entry.Channels == 0 || entry.DataOffset < 4) return null;
        if (Math.Abs(gain - 1f) < 0.001f) return null;

        int bytesPerSample = Math.Max(entry.BitsPerSample, 8) / 8;
        int frameBytes = bytesPerSample * entry.Channels;
        if (frameBytes <= 0) return null;

        int offset = entry.DataOffset;
        var d = entry.Data;
        uint declared = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(offset - 4, 4));
        int dataLen = (int)Math.Min(declared, (uint)Math.Max(0, d.Length - offset));
        int totalFrames = dataLen / frameBytes;
        if (totalFrames <= 0) return null;

        var result = (byte[])d.Clone(); // same length, so every size field stays valid untouched
        for (int frame = 0; frame < totalFrames; frame++)
        {
            int baseOff = offset + frame * frameBytes;
            for (int ch = 0; ch < entry.Channels; ch++)
            {
                int pos = baseOff + ch * bytesPerSample;
                WriteSample(result, pos, entry.BitsPerSample, ReadSample(d, pos, entry.BitsPerSample, entry.FormatTag) * gain, entry.FormatTag);
            }
        }
        return result;
    }

    /// <summary>
    /// Mixes every channel down to one by averaging, and fixes up the fmt
    /// chunk's channel count, byte rate and block align to match. Returns the
    /// new file bytes, or null when the header can't be read or it's already mono.
    /// </summary>
    public static byte[]? ConvertToMono(SoundEntry entry)
    {
        if (entry.BitsPerSample == 0 || entry.Channels == 0 || entry.DataOffset < 4) return null;
        if (entry.Channels == 1) return null;

        int bytesPerSample = Math.Max(entry.BitsPerSample, 8) / 8;
        int frameBytes = bytesPerSample * entry.Channels;
        if (frameBytes <= 0) return null;

        int offset = entry.DataOffset;
        var d = entry.Data;
        uint declared = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(offset - 4, 4));
        int dataLen = (int)Math.Min(declared, (uint)Math.Max(0, d.Length - offset));
        int totalFrames = dataLen / frameBytes;
        if (totalFrames <= 0) return null;

        var mono = new byte[totalFrames * bytesPerSample];
        for (int frame = 0; frame < totalFrames; frame++)
        {
            int baseOff = offset + frame * frameBytes;
            float sum = 0;
            for (int ch = 0; ch < entry.Channels; ch++)
                sum += ReadSample(d, baseOff + ch * bytesPerSample, entry.BitsPerSample, entry.FormatTag);
            WriteSample(mono, frame * bytesPerSample, entry.BitsPerSample, sum / entry.Channels, entry.FormatTag);
        }

        var result = Rebuild(d, offset, mono);
        int fmtBody = FindFmtBody(result);
        if (fmtBody >= 0 && fmtBody + 16 <= result.Length)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(fmtBody + 2), 1); // NumChannels
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(fmtBody + 8), (uint)(entry.SampleRate * bytesPerSample)); // ByteRate
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(fmtBody + 12), (ushort)bytesPerSample); // BlockAlign
        }
        return result;
    }

    /// <summary>Lowest and highest sample rates Resample accepts.</summary>
    public const int MinSampleRate = 4000, MaxSampleRate = 192000;

    /// <summary>
    /// Changes the sample rate with a Blackman-windowed sinc interpolator. The
    /// low-pass sits just under the lower of the two Nyquist limits, so downsampling
    /// removes what the new rate can't hold instead of aliasing it back in.
    /// Prototyped in Python first: a 1 kHz tone keeps its level; a 15 kHz tone taken
    /// from 44100 to 22050 Hz disappears. Upsampling works but adds no detail.
    /// Returns null if the header can't be read, or the rate is unchanged or out of range.
    /// </summary>
    public static byte[]? Resample(SoundEntry entry, int targetRate, CancellationToken cancel = default)
    {
        if (entry.SampleRate <= 0) return null;
        if (targetRate == entry.SampleRate || targetRate < MinSampleRate || targetRate > MaxSampleRate) return null;
        if (!TryReadFloatChannels(entry, out var channels, out int offset, out int frameBytes, out int bytesPerSample, allowFloatInput: true)) return null;

        double ratio = (double)targetRate / entry.SampleRate;
        int outFrames = (int)Math.Round(channels[0].Length * ratio);
        if (outFrames <= 0) return null;

        var result = WriteFloatChannels(entry, offset, SincResample(channels, ratio, outFrames, cancel), frameBytes, bytesPerSample);
        int fmtBody = FindFmtBody(result);
        if (fmtBody >= 0 && fmtBody + 16 <= result.Length)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(fmtBody + 4), (uint)targetRate); // SampleRate
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(fmtBody + 8), (uint)(targetRate * frameBytes)); // ByteRate
        }
        return result;
    }

    // WSOLA tuning: 30 ms Hann windows at 50% overlap, each shifted up to ±10 ms from
    // its ideal spot to where it best continues the previous one. Short enough for SFX
    // transients, long enough for low voices.
    private const double StretchWindowSeconds = 0.030, StretchToleranceSeconds = 0.010;

    /// <summary>
    /// Tape-style speed change: pitch and length move together (200% = an octave up,
    /// half as long). Sinc-resamples the content and keeps the header's sample rate.
    /// The allowed range is the editor's (settings.json); here only non-positive or
    /// non-finite values are refused. Returns null if the header can't be read, or the
    /// percent is 100, zero, negative or not a number.
    /// </summary>
    public static byte[]? ChangeSpeed(SoundEntry entry, double percent, CancellationToken cancel = default)
    {
        if (!double.IsFinite(percent) || percent <= 0 || Math.Abs(percent - 100) < 0.001) return null;
        if (!TryReadFloatChannels(entry, out var channels, out int offset, out int frameBytes, out int bytesPerSample, allowFloatInput: true)) return null;

        double ratio = 100 / percent; // output length / input length
        int outFrames = ScaledFrames(channels[0].Length, ratio);
        return WriteFloatChannels(entry, offset, SincResample(channels, ratio, outFrames, cancel), frameBytes, bytesPerSample);
    }

    /// <summary>
    /// Changes speed while keeping pitch (200% = half as long, same notes), by WSOLA
    /// time-stretching. Returns null if the header can't be read, or the percent is
    /// 100, zero, negative or not a number.
    /// </summary>
    public static byte[]? ChangeTempo(SoundEntry entry, double percent, CancellationToken cancel = default)
    {
        if (!double.IsFinite(percent) || percent <= 0 || Math.Abs(percent - 100) < 0.001) return null;
        if (entry.SampleRate <= 0) return null;
        if (!TryReadFloatChannels(entry, out var channels, out int offset, out int frameBytes, out int bytesPerSample, allowFloatInput: true)) return null;

        var stretched = TimeStretch(channels, entry.SampleRate, 100 / percent, cancel);
        return WriteFloatChannels(entry, offset, stretched, frameBytes, bytesPerSample);
    }

    /// <summary>
    /// Shifts pitch while keeping length: WSOLA-stretches by the pitch factor, then
    /// sinc-resamples back to the original frame count. Prototyped in Python: a 440 Hz
    /// tone shifted +12 comes out at 880 Hz, same length and level. Returns null if the
    /// header can't be read, or the shift is 0 or not a number.
    /// </summary>
    public static byte[]? ChangePitch(SoundEntry entry, double semitones, CancellationToken cancel = default)
    {
        if (!double.IsFinite(semitones) || Math.Abs(semitones) < 0.001) return null;
        if (entry.SampleRate <= 0) return null;
        if (!TryReadFloatChannels(entry, out var channels, out int offset, out int frameBytes, out int bytesPerSample, allowFloatInput: true)) return null;

        double factor = Math.Pow(2, semitones / 12);
        var stretched = TimeStretch(channels, entry.SampleRate, factor, cancel);
        var shifted = SincResample(stretched, 1 / factor, channels[0].Length, cancel);
        return WriteFloatChannels(entry, offset, shifted, frameBytes, bytesPerSample);
    }

    /// <summary>Frames after scaling a length by factor. Throws for factors that make the result too long to hold (possible with extreme editor limits).</summary>
    private static int ScaledFrames(int frames, double factor)
    {
        double n = Math.Round(frames * factor);
        if (!(n <= int.MaxValue / 8))
            throw new ArgumentOutOfRangeException(nameof(factor), $"A length factor of {factor:0.###} makes the sound too long to hold.");
        return Math.Max(1, (int)n);
    }

    /// <summary>Decodes the PCM payload into one -1..1 array per channel. False if the header can't be read or holds no frames.</summary>
    private static bool TryReadFloatChannels(SoundEntry entry, out float[][] channels,
        out int offset, out int frameBytes, out int bytesPerSample, bool allowFloatInput = false)
    {
        channels = [];
        offset = frameBytes = bytesPerSample = 0;
        if (entry.BitsPerSample == 0 || entry.Channels == 0 || entry.DataOffset < 4) return false;
        if (entry.FormatTag == 3 && (!allowFloatInput || entry.BitsPerSample != 32)) return false;
        if (entry.FormatTag is not (1 or 3)) return false;

        bytesPerSample = Math.Max(entry.BitsPerSample, 8) / 8;
        frameBytes = bytesPerSample * entry.Channels;
        if (frameBytes <= 0) return false;

        offset = entry.DataOffset;
        var d = entry.Data;
        uint declared = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(offset - 4, 4));
        int dataLen = (int)Math.Min(declared, (uint)Math.Max(0, d.Length - offset));
        int totalFrames = dataLen / frameBytes;
        if (totalFrames <= 0) return false;

        channels = new float[entry.Channels][];
        for (int c = 0; c < entry.Channels; c++)
        {
            channels[c] = new float[totalFrames];
            for (int f = 0; f < totalFrames; f++)
                channels[c][f] = ReadSample(d, offset + f * frameBytes + c * bytesPerSample, entry.BitsPerSample, entry.FormatTag);
        }
        return true;
    }

    /// <summary>Encodes per-channel samples back into the entry's format and swaps them in as the data payload.</summary>
    private static byte[] WriteFloatChannels(SoundEntry entry, int offset, float[][] channels, int frameBytes, int bytesPerSample)
    {
        int frames = channels[0].Length;
        var pcm = new byte[frames * frameBytes];
        for (int f = 0; f < frames; f++)
            for (int c = 0; c < channels.Length; c++)
                WriteSample(pcm, f * frameBytes + c * bytesPerSample, entry.BitsPerSample, channels[c][f], entry.FormatTag);
        return Rebuild(entry.Data, offset, pcm);
    }

    /// <summary>
    /// Blackman-windowed sinc interpolation to outFrames frames; ratio is output length
    /// over input length (output frame n reads input position n / ratio). The cutoff
    /// follows the lower Nyquist, so shrinking filters instead of aliasing. Throws
    /// OperationCanceledException when canceled (checked every 1024 frames).
    /// </summary>
    private static float[][] SincResample(float[][] input, double ratio, int outFrames, CancellationToken cancel)
    {
        int totalFrames = input[0].Length;
        double fc = 0.5 * Math.Min(1.0, ratio) * 0.95; // cutoff, in cycles per source sample
        double halfWidth = 16 / (2 * fc);               // 16 zero crossings each side, in source samples

        var output = new float[input.Length][];
        for (int c = 0; c < input.Length; c++) output[c] = new float[outFrames];
        var acc = new double[input.Length];
        for (int n = 0; n < outFrames; n++)
        {
            if ((n & 1023) == 0) cancel.ThrowIfCancellationRequested();
            double t = n / ratio;
            // Clamped as doubles first so extreme ratios can't overflow the casts.
            int k0 = (int)Math.Max(0, Math.Floor(t - halfWidth) + 1);
            int k1 = (int)Math.Min(totalFrames - 1, Math.Floor(t + halfWidth));
            Array.Clear(acc);
            for (int k = k0; k <= k1; k++)
            {
                double dist = t - k;
                double a = 2 * fc * dist;
                double sinc = a == 0 ? 1 : Math.Sin(Math.PI * a) / (Math.PI * a);
                double w = 0.42 + 0.5 * Math.Cos(Math.PI * dist / halfWidth) + 0.08 * Math.Cos(2 * Math.PI * dist / halfWidth);
                double g = 2 * fc * sinc * w;
                for (int c = 0; c < acc.Length; c++) acc[c] += input[c][k] * g;
            }
            for (int c = 0; c < acc.Length; c++) output[c][n] = (float)acc[c];
        }
        return output;
    }

    /// <summary>
    /// WSOLA time-stretch: output is round(length × stretch) frames at the same pitch.
    /// Windows are laid at a fixed output hop and read from input spots advancing at
    /// hop / stretch, each nudged within the tolerance to where its waveform best matches
    /// the natural continuation of the previous window (normalized cross-correlation on
    /// the channel sum, so stereo stays linked). Periodic Hann at 50% overlap sums to 1.
    /// Throws OperationCanceledException when canceled (checked every window).
    /// </summary>
    private static float[][] TimeStretch(float[][] input, int sampleRate, double stretch, CancellationToken cancel)
    {
        int channels = input.Length, inFrames = input[0].Length;
        int outFrames = ScaledFrames(inFrames, stretch);
        int window = Math.Max(2, (int)Math.Round(StretchWindowSeconds * sampleRate)) & ~1;
        int hop = window / 2;
        double inHop = hop / stretch;
        int tolerance = Math.Max(1, (int)Math.Round(StretchToleranceSeconds * sampleRate));

        var hann = new double[window];
        for (int i = 0; i < window; i++) hann[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / window);

        // Zero-padded channel sum for the search, so no index ever needs a bounds check.
        // Ideal spots are capped just past the end (everything there reads silence
        // anyway), so the padding doesn't grow with extreme stretch factors.
        int idealCap = inFrames + tolerance + 2;
        int pad = 2 * tolerance + hop + window + 8;
        var mono = new float[pad + inFrames + pad];
        for (int c = 0; c < channels; c++)
            for (int f = 0; f < inFrames; f++) mono[pad + f] += input[c][f];

        var acc = new double[channels][];
        for (int c = 0; c < channels; c++) acc[c] = new double[outFrames];
        int prev = 0;
        // Window k covers output [k*hop - hop, k*hop + hop), centered on k*hop.
        for (int k = 0; (long)k * hop - hop < outFrames; k++)
        {
            cancel.ThrowIfCancellationRequested();
            int ideal = (int)Math.Min(Math.Round(k * inHop), idealCap + hop) - hop;
            int best = k == 0 ? ideal : BestOverlap(mono, pad, prev + hop, ideal, tolerance, window);
            int o = k * hop - hop;
            for (int i = Math.Max(0, -o); i < window && o + i < outFrames; i++)
            {
                int src = best + i;
                if ((uint)src >= (uint)inFrames) continue;
                for (int c = 0; c < channels; c++) acc[c][o + i] += input[c][src] * hann[i];
            }
            prev = best;
        }

        var output = new float[channels][];
        for (int c = 0; c < channels; c++)
        {
            output[c] = new float[outFrames];
            for (int f = 0; f < outFrames; f++) output[c][f] = (float)acc[c][f];
        }
        return output;
    }

    /// <summary>
    /// The input start within ideal ± tolerance whose window best matches the one at
    /// target. Coarse pass on every 2nd sample and candidate, then a ±1 full-rate refine
    /// (4× faster; same results as the full search in the Python prototype).
    /// </summary>
    private static int BestOverlap(float[] mono, int pad, int target, int ideal, int tolerance, int window)
    {
        double Score(int p, int stride)
        {
            double c = 0, e = 0;
            for (int i = 0; i < window; i += stride)
            {
                double s = mono[pad + p + i];
                c += s * mono[pad + target + i];
                e += s * s;
            }
            return e > 1e-12 ? c / Math.Sqrt(e) : 0;
        }

        int best = ideal;
        double bestScore = double.NegativeInfinity;
        for (int p = ideal - tolerance; p <= ideal + tolerance; p += 2)
        {
            double s = Score(p, 2);
            if (s > bestScore) (bestScore, best) = (s, p);
        }
        int coarse = best;
        bestScore = double.NegativeInfinity;
        for (int p = coarse - 1; p <= coarse + 1; p++)
        {
            double s = Score(p, 1);
            if (s > bestScore) (bestScore, best) = (s, p);
        }
        return best;
    }

    // ---- ranges, reverse, fades, bit depth ------------------------------------

    private static int FrameBytes(SoundEntry entry) => Math.Max(entry.BitsPerSample, 8) / 8 * entry.Channels;

    /// <summary>Frames in the data chunk (declared size, clamped to the bytes present), or 0 if the header wasn't read.</summary>
    public static int FrameCount(SoundEntry entry)
    {
        if (entry.BitsPerSample == 0 || entry.Channels == 0 || entry.DataOffset < 4 || entry.DataOffset > entry.Data.Length) return 0;
        uint declared = BinaryPrimitives.ReadUInt32LittleEndian(entry.Data.AsSpan(entry.DataOffset - 4, 4));
        int dataLen = (int)Math.Min(declared, (uint)Math.Max(0, entry.Data.Length - entry.DataOffset));
        return dataLen / FrameBytes(entry);
    }

    /// <summary>Loudest absolute sample (0..1) in frames [start, end), clamped to the sound. Null if the header can't be read or the range is empty.</summary>
    public static float? Peak(SoundEntry entry, int start = 0, int end = int.MaxValue)
    {
        int total = FrameCount(entry);
        start = Math.Max(0, start);
        end = Math.Min(end, total);
        if (end <= start) return null;
        int fb = FrameBytes(entry), bps = Math.Max(entry.BitsPerSample, 8) / 8;
        float peak = 0;
        for (int frame = start; frame < end; frame++)
            for (int ch = 0; ch < entry.Channels; ch++)
                peak = Math.Max(peak, Math.Abs(ReadSample(entry.Data, entry.DataOffset + frame * fb + ch * bps, entry.BitsPerSample, entry.FormatTag)));
        return peak;
    }

    /// <summary>Frames [start, end) as a WAV of their own, same format and header chunks. Null if the header can't be read or the range is empty or out of bounds.</summary>
    public static byte[]? ExtractRange(SoundEntry entry, int start, int end)
    {
        int total = FrameCount(entry);
        if (total == 0 || start < 0 || end > total || end <= start) return null;
        int fb = FrameBytes(entry);
        var pcm = new byte[(end - start) * fb];
        Array.Copy(entry.Data, entry.DataOffset + start * fb, pcm, 0, pcm.Length);
        return Rebuild(entry.Data, entry.DataOffset, pcm);
    }

    /// <summary>
    /// Runs a whole-sound edit on frames [start, end) only: the range is cut out as a WAV of
    /// its own, edited, and spliced back (it may come back longer or shorter). Where the range
    /// meets untouched audio, crossfadeFrames blend original into edited and back, so a level
    /// or pitch jump at the edge doesn't click (capped at half the range). Untouched frames are
    /// copied byte for byte. Only for edits that keep the format. Null if the header or range
    /// is unusable, or the edit returned null (nothing to do).
    /// </summary>
    public static byte[]? ApplyToRange(SoundEntry entry, int start, int end, int crossfadeFrames, Func<SoundEntry, byte[]?> edit)
    {
        if (ExtractRange(entry, start, end) is not { } sliceBytes) return null;
        var slice = new SoundEntry { Group = entry.Group, Index = entry.Index, Data = sliceBytes };
        SndReader.Describe(slice);
        if (edit(slice) is not { } editedBytes) return null;
        var edited = new SoundEntry { Data = editedBytes };
        SndReader.Describe(edited);
        int fb = FrameBytes(entry);
        if (edited.BitsPerSample != entry.BitsPerSample || edited.Channels != entry.Channels)
            throw new InvalidOperationException("A range edit changed the sound's format.");

        int total = FrameCount(entry), n = FrameCount(edited);
        int o = entry.DataOffset, eo = edited.DataOffset;
        int bps = Math.Max(entry.BitsPerSample, 8) / 8, bits = entry.BitsPerSample;
        var pcm = new byte[(total - (end - start) + n) * fb];
        Array.Copy(entry.Data, o, pcm, 0, start * fb);
        Array.Copy(edited.Data, eo, pcm, start * fb, n * fb);
        Array.Copy(entry.Data, o + end * fb, pcm, (start + n) * fb, (total - end) * fb);

        int x = Math.Min(Math.Max(0, crossfadeFrames), Math.Min(n, end - start) / 2);
        for (int i = 0; i < x; i++)
        {
            float w = (i + 0.5f) / x; // share of the side being faded towards
            for (int c = 0; c < entry.Channels; c++)
            {
                if (start > 0) // original -> edited at the leading edge
                {
                    float a = ReadSample(entry.Data, o + (start + i) * fb + c * bps, bits, entry.FormatTag);
                    float b = ReadSample(edited.Data, eo + i * fb + c * bps, bits, edited.FormatTag);
                    WriteSample(pcm, (start + i) * fb + c * bps, bits, a + (b - a) * w, entry.FormatTag);
                }
                if (end < total) // edited -> original at the trailing edge
                {
                    float a = ReadSample(edited.Data, eo + (n - x + i) * fb + c * bps, bits, edited.FormatTag);
                    float b = ReadSample(entry.Data, o + (end - x + i) * fb + c * bps, bits, entry.FormatTag);
                    WriteSample(pcm, (start + n - x + i) * fb + c * bps, bits, a + (b - a) * w, entry.FormatTag);
                }
            }
        }
        return Rebuild(entry.Data, o, pcm);
    }

    /// <summary>
    /// Repeats Auto uses: until a repeat of a part peaking at `peak` falls below `silence` (the same
    /// -1..1 threshold Trim Silence uses), at least 1 and at most maxRepeats.
    /// </summary>
    public static int AutoEchoRepeats(double decay, double peak, double silence, int maxRepeats)
    {
        int max = Math.Max(1, maxRepeats);
        if (decay >= 1) return max; // never fades
        if (decay <= 0 || peak <= silence || silence <= 0) return 1;
        return Math.Clamp((int)Math.Ceiling(Math.Log(silence / peak) / Math.Log(decay)), 1, max);
    }

    /// <summary>
    /// Echo: frames [start, end) repeat `repeats` times, each `delayFrames` after the last and `decay`
    /// times as loud, added on top of the sound (which is otherwise left as it is). The sound gets
    /// longer when the echoes run past its end. Samples past full scale clip. Null if the header
    /// can't be read, the range is empty, or there is nothing to add.
    /// </summary>
    public static byte[]? Echo(SoundEntry entry, int start, int end, int delayFrames, double decay, int repeats, CancellationToken cancel = default)
    {
        if (EchoMix(entry, start, end, delayFrames, decay, repeats, cancel, out int offset, out int fb, out int bps) is not { } mixed) return null;
        return WriteFloatChannels(entry, offset, mixed, fb, bps);
    }

    /// <summary>Loudest sample (0..) Echo would produce with the same arguments, before clipping; null when Echo would be.</summary>
    public static float? EchoPeak(SoundEntry entry, int start, int end, int delayFrames, double decay, int repeats) =>
        EchoMix(entry, start, end, delayFrames, decay, repeats, default, out _, out _, out _) is { } mixed
            ? mixed.Max(c => c.Length == 0 ? 0 : c.Max(Math.Abs)) : null;

    /// <summary>Frames Echo returns: the sound, or the last echo's end when that's later.</summary>
    public static long EchoLength(int totalFrames, int end, int delayFrames, int repeats) =>
        Math.Max(totalFrames, end + (long)repeats * delayFrames);

    private static float[][]? EchoMix(SoundEntry entry, int start, int end, int delayFrames, double decay, int repeats,
        CancellationToken cancel, out int offset, out int frameBytes, out int bytesPerSample)
    {
        offset = frameBytes = bytesPerSample = 0;
        if (delayFrames < 1 || repeats < 1 || decay <= 0) return null;
        if (!TryReadFloatChannels(entry, out var input, out offset, out frameBytes, out bytesPerSample, allowFloatInput: true)) return null;
        int total = input[0].Length;
        start = Math.Clamp(start, 0, total);
        end = Math.Clamp(end, start, total);
        if (end <= start) return null;
        long length = EchoLength(total, end, delayFrames, repeats);
        if (length * frameBytes > int.MaxValue - offset - 1)
            throw new ArgumentOutOfRangeException(nameof(repeats), "Those echoes make the sound too long to hold.");

        var output = new float[input.Length][];
        for (int c = 0; c < input.Length; c++)
        {
            output[c] = new float[length];
            Array.Copy(input[c], output[c], total);
        }
        double gain = 1;
        for (int k = 1; k <= repeats; k++)
        {
            cancel.ThrowIfCancellationRequested();
            gain *= decay;
            float g = (float)gain;
            int shift = k * delayFrames;
            for (int c = 0; c < input.Length; c++)
                for (int f = start; f < end; f++)
                    output[c][f + shift] += input[c][f] * g;
        }
        return output;
    }

    /// <summary>Plays the sound backwards: frames in reverse order, each frame's channels kept together. Byte-exact. Null if the header can't be read or there are fewer than two frames.</summary>
    public static byte[]? Reverse(SoundEntry entry)
    {
        int total = FrameCount(entry);
        if (total < 2) return null;
        int fb = FrameBytes(entry);
        var pcm = new byte[total * fb];
        for (int f = 0; f < total; f++)
            Array.Copy(entry.Data, entry.DataOffset + (total - 1 - f) * fb, pcm, f * fb, fb);
        return Rebuild(entry.Data, entry.DataOffset, pcm);
    }

    /// <summary>
    /// Pads the sound with `leading` silent frames before it and `trailing` after it. Existing
    /// audio is copied byte for byte. Null if the header can't be read or both counts are 0.
    /// </summary>
    public static byte[]? InsertSilence(SoundEntry entry, int leading, int trailing)
    {
        int total = FrameCount(entry);
        if (total == 0 || leading < 0 || trailing < 0 || leading + trailing == 0) return null;
        int fb = FrameBytes(entry);
        long size = ((long)total + leading + trailing) * fb;
        if (size > int.MaxValue - entry.DataOffset - 1)
            throw new ArgumentOutOfRangeException(nameof(leading), "That much silence makes the sound too long to hold.");
        var pcm = new byte[size];
        if (entry.BitsPerSample == 8) Array.Fill(pcm, (byte)0x80); // 8-bit WAV is unsigned: 128 is silence
        Array.Copy(entry.Data, entry.DataOffset, pcm, (long)leading * fb, (long)total * fb);
        return Rebuild(entry.Data, entry.DataOffset, pcm);
    }

    /// <summary>
    /// Fades the first (fadeIn) or last `frames` frames from or to silence. Linear ramps the
    /// gain evenly; EqualPower follows a quarter sine, which sounds smoother. The silent end
    /// is exactly 0. Null if the header can't be read or frames isn't 1 to the sound's length.
    /// </summary>
    public static byte[]? Fade(SoundEntry entry, bool fadeIn, int frames, FadeCurve curve)
    {
        int total = FrameCount(entry);
        if (total == 0 || frames < 1 || frames > total) return null;
        int fb = FrameBytes(entry), bps = Math.Max(entry.BitsPerSample, 8) / 8, bits = entry.BitsPerSample;
        int first = entry.DataOffset + (fadeIn ? 0 : total - frames) * fb;
        var result = (byte[])entry.Data.Clone(); // same length, so every size field stays valid
        for (int i = 0; i < frames; i++)
        {
            double t = fadeIn ? (double)i / frames : (double)(frames - 1 - i) / frames; // 0 at the silent end
            float g = (float)(curve == FadeCurve.EqualPower ? Math.Sin(t * Math.PI / 2) : t);
            for (int c = 0; c < entry.Channels; c++)
            {
                int pos = first + i * fb + c * bps;
                WriteSample(result, pos, bits, ReadSample(entry.Data, pos, bits, entry.FormatTag) * g);
            }
        }
        return result;
    }

    /// <summary>Depths Change Bit Depth writes: the integer PCM Ikemen's decoder plays (it reads 32-bit only as float).</summary>
    public static readonly int[] BitDepthTargets = [8, 16, 24];

    private const int DitherSeed = 1; // fixed, so the same input always gives the same output

    /// <summary>
    /// Rewrites the samples at another integer depth and fixes up the fmt chunk (bits, block
    /// align, byte rate, and valid bits for WAVE_FORMAT_EXTENSIBLE). When lowering, dither adds
    /// TPDF noise of ±1 step of the new depth before rounding, which turns rounding distortion
    /// in quiet parts into a faint steady hiss. Null if the header can't be read, the target
    /// isn't 8/16/24, or it's already that depth.
    /// </summary>
    public static byte[]? ChangeBitDepth(SoundEntry entry, int targetBits, bool dither)
    {
        if (Array.IndexOf(BitDepthTargets, targetBits) < 0 || targetBits == entry.BitsPerSample) return null;
        if (!TryReadFloatChannels(entry, out var channels, out int offset, out _, out _, allowFloatInput: true)) return null;

        int frames = channels[0].Length, count = channels.Length;
        int outBps = targetBits / 8, outFb = outBps * count;
        bool addDither = dither && targetBits < entry.BitsPerSample;
        double step = 1.0 / (1 << (targetBits - 1)); // one step of the new depth on the -1..1 scale
        var rng = new Random(DitherSeed);
        var pcm = new byte[frames * outFb];
        for (int f = 0; f < frames; f++)
            for (int c = 0; c < count; c++)
            {
                double v = channels[c][f];
                if (addDither) v += (rng.NextDouble() - rng.NextDouble()) * step;
                WriteSample(pcm, f * outFb + c * outBps, targetBits, (float)v);
            }

        var result = Rebuild(entry.Data, offset, pcm);
        int fmtBody = FindFmtBody(result);
        if (fmtBody < 0 || fmtBody + 16 > result.Length) return null;
        bool extensible = BinaryPrimitives.ReadUInt16LittleEndian(result.AsSpan(fmtBody)) == 0xfffe;
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(fmtBody + 8), (uint)(entry.SampleRate * outFb)); // ByteRate
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(fmtBody + 12), (ushort)outFb);                  // BlockAlign
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(fmtBody + 14), (ushort)targetBits);             // BitsPerSample
        if (extensible)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(fmtBody + 18), (ushort)targetBits);         // ValidBitsPerSample
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(fmtBody + 24), 1);                          // PCM subformat
        }
        else BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(fmtBody), 1);                              // PCM
        return result;
    }

    /// <summary>Walks the chunks to find "fmt "'s body offset, the way Describe does. -1 if not found.</summary>
    private static int FindFmtBody(byte[] d)
    {
        int p = 12;
        while (p + 8 <= d.Length)
        {
            var id = d.AsSpan(p, 4);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(p + 4));
            int body = p + 8;
            if (id.SequenceEqual("fmt "u8)) return body;
            long next = (long)body + size + (size & 1); // long: a huge size ends the walk, as in Describe
            if (size == 0 || next + 8 > d.Length) break;
            p = (int)next;
        }
        return -1;
    }


    /// <summary>Writes one sample scaled to -1..1, mirroring ReadSample's formats. Rounded to the nearest step and clamped to the format's range.</summary>
    private static void WriteSample(byte[] d, int pos, int bits, float v, ushort formatTag = 1)
    {
        v = Math.Clamp(v, -1f, 1f);
        if (bits == 32 && formatTag == 3)
        {
            BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(pos, 4), BitConverter.SingleToInt32Bits(v));
            return;
        }
        switch (bits)
        {
            case 8:
                d[pos] = (byte)Math.Round((v + 1f) / 2f * 255f); // WAV 8-bit PCM is unsigned
                break;
            case 16:
                short s16 = (short)Math.Clamp((int)Math.Round(v * 32768f), short.MinValue, short.MaxValue);
                d[pos] = (byte)s16;
                d[pos + 1] = (byte)(s16 >> 8);
                break;
            case 24:
                int s24 = Math.Clamp((int)Math.Round(v * 8388608f), -8388608, 8388607);
                d[pos] = (byte)s24;
                d[pos + 1] = (byte)(s24 >> 8);
                d[pos + 2] = (byte)(s24 >> 16);
                break;
            case 32:
                int s32 = (int)Math.Clamp(Math.Round((double)v * 2147483648d), int.MinValue, (double)int.MaxValue);
                d[pos] = (byte)s32;
                d[pos + 1] = (byte)(s32 >> 8);
                d[pos + 2] = (byte)(s32 >> 16);
                d[pos + 3] = (byte)(s32 >> 24);
                break;
        }
    }

    /// <summary>
    /// Trims near-silent frames from the start and end of a WAV's PCM data.
    /// Returns the new file bytes, or null if the header can't be read or
    /// there was nothing to trim (already tight, or entirely silent).
    /// </summary>
    public static byte[]? TrimSilence(SoundEntry entry, double threshold = DefaultSilenceThreshold)
    {
        if (entry.BitsPerSample == 0 || entry.Channels == 0 || entry.DataOffset < 4) return null;

        int bytesPerSample = Math.Max(entry.BitsPerSample, 8) / 8;
        int frameBytes = bytesPerSample * entry.Channels;
        if (frameBytes <= 0) return null;

        int offset = entry.DataOffset;
        var d = entry.Data;
        // Re-read the declared chunk size rather than trusting Data.Length - offset,
        // in case anything follows the data chunk (matches SndReader's own clamp).
        uint declared = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(offset - 4, 4));
        int dataLen = (int)Math.Min(declared, (uint)Math.Max(0, d.Length - offset));
        int totalFrames = dataLen / frameBytes;
        if (totalFrames <= 0) return null;

        bool Loud(int frame)
        {
            int baseOff = offset + frame * frameBytes;
            for (int ch = 0; ch < entry.Channels; ch++)
                if (Math.Abs(ReadSample(d, baseOff + ch * bytesPerSample, entry.BitsPerSample, entry.FormatTag)) > threshold)
                    return true;
            return false;
        }

        int start = 0;
        while (start < totalFrames && !Loud(start)) start++;
        if (start == totalFrames) return null; // entirely silent — leave it alone rather than emit a zero-length sound

        int end = totalFrames - 1;
        while (end > start && !Loud(end)) end--;

        if (start == 0 && end == totalFrames - 1) return null; // already tight

        int keepFrames = end - start + 1;
        var trimmed = new byte[keepFrames * frameBytes];
        Array.Copy(d, offset + start * frameBytes, trimmed, 0, trimmed.Length);
        return Rebuild(d, offset, trimmed);
    }

    /// <summary>Keeps every byte before the data payload untouched (fmt chunk, any other chunks) and replaces just the payload, fixing up the data-chunk and RIFF sizes.</summary>
    private static byte[] Rebuild(byte[] original, int dataOffset, byte[] newPcm)
    {
        uint oldDeclared = BinaryPrimitives.ReadUInt32LittleEndian(original.AsSpan(dataOffset - 4, 4));
        int oldLength = (int)Math.Min(oldDeclared, (uint)Math.Max(0, original.Length - dataOffset));
        int oldEnd = Math.Min(original.Length, dataOffset + oldLength + (oldLength & 1));
        bool pad = (newPcm.Length & 1) != 0; // RIFF chunks are word-aligned; the pad byte itself doesn't count in the size field
        int tailLength = original.Length - oldEnd;
        var result = new byte[dataOffset + newPcm.Length + (pad ? 1 : 0) + tailLength];
        Array.Copy(original, result, dataOffset);
        Array.Copy(newPcm, 0, result, dataOffset, newPcm.Length);
        Array.Copy(original, oldEnd, result, dataOffset + newPcm.Length + (pad ? 1 : 0), tailLength);

        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(dataOffset - 4, 4), (uint)newPcm.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4, 4), (uint)(result.Length - 8)); // RIFF size = file length - 8
        return result;
    }

    /// <summary>Reads one sample (one channel of one frame) scaled to -1..1. Mirrors WaveformView's reader, kept separate since Editor can't be referenced from here.</summary>
    private static float ReadSample(byte[] d, int pos, int bits, ushort formatTag = 1)
    {
        if (pos < 0 || pos + Math.Max(bits, 8) / 8 > d.Length) return 0;
        if (bits == 32 && formatTag == 3)
            return BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(pos, 4)));
        return bits switch
        {
            8 => d[pos] / 255f * 2f - 1f, // WAV 8-bit PCM is unsigned
            16 => (short)(d[pos] | (d[pos + 1] << 8)) / 32768f,
            24 => Sign24(d[pos] | (d[pos + 1] << 8) | (d[pos + 2] << 16)) / 8388608f,
            32 => BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(pos, 4)) / 2147483648f,
            _ => 0,
        };
    }

    private static int Sign24(int v) => (v & 0x800000) != 0 ? v | unchecked((int)0xFF000000) : v;
}
