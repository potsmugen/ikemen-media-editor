using System.Globalization;
using Avalonia.Controls;
using Avalonia.Layout;
using Snd;

namespace Editor;

/// <summary>
/// Sound edits on several selected sounds. A single sound edits through the pending
/// session (preview, Commit/Reset), which follows one sound only; with several selected,
/// each action asks once (no preview), edits every sound and records one undo step.
/// The value parsers here are shared with the single-sound prompts.
/// </summary>
public partial class MainWindow
{
    /// <summary>The selected sounds when there are several, else null (the single-sound path applies).</summary>
    private List<SoundEntry>? MultiSelection()
    {
        var selected = GetSelectedSounds();
        return selected.Count > 1 ? selected : null;
    }

    private static (byte[] Data, string? Problem, int Ch, int Rate, int Bits, ushort Format, TimeSpan Dur) SoundState(SoundEntry s) =>
        (s.Data, s.Problem, s.Channels, s.SampleRate, s.BitsPerSample, s.FormatTag, s.Duration);

    /// <summary>
    /// Runs edit on each target under the busy lock and applies the results as one undo step.
    /// edit runs off the UI thread, so it must touch no UI. It returns the new bytes, or null
    /// Data: with Skip, a reason logged to crash.log; without, nothing to change. Sounds with
    /// unreadable headers are skipped before edit sees them. done gets the changed count.
    /// </summary>
    private async Task EditSounds(List<SoundEntry> targets, string busyText, string undoName,
        Func<int, string> done, Func<SoundEntry, (byte[]? Data, string? Skip)> edit)
    {
        if (_snd is not { } file || _busy || !await ResolvePendingSoundEditsBeforeSwitch()) return;
        if (!ReferenceEquals(_snd, file)) return;

        var changes = new List<(SoundEntry Sound,
            (byte[] Data, string? Problem, int Ch, int Rate, int Bits, ushort Format, TimeSpan Dur) Before,
            (byte[] Data, string? Problem, int Ch, int Rate, int Bits, ushort Format, TimeSpan Dur) After)>();
        var skipped = new List<string>();
        int unchanged = 0;
        if (!TryBeginBusy()) return;
        try
        {
            for (int n = 0; n < targets.Count; n++)
            {
                var s = targets[n];
                await BusyStep(busyText, n, targets.Count, $"{s.Group},{s.Index}");
                if (!ReferenceEquals(_snd, file))
                {
                    StatusText.Text = $"{busyText} stopped — the file was closed.";
                    return;
                }
                if (!file.Sounds.Contains(s)) continue;
                if (s.Problem is not null || s.SampleRate <= 0)
                {
                    skipped.Add($"{s.Group},{s.Index}: unreadable WAV header");
                    continue;
                }

                var (data, skip) = await Task.Run(() => edit(s));
                if (data is null)
                {
                    if (skip is null) unchanged++;
                    else skipped.Add($"{s.Group},{s.Index}: {skip}");
                    continue;
                }
                var after = new SoundEntry { Data = data };
                SndReader.Describe(after);
                changes.Add((s, SoundState(s), SoundState(after)));
            }

            foreach (var item in skipped) CrashLog.Error(undoName, item);
            CrashLog.Trace($"[sound] {undoName}: {changes.Count} changed, {unchanged} unchanged, {skipped.Count} skipped");
            string skippedText = skipped.Count > 0 ? $"; {skipped.Count} skipped (see crash.log)" : "";
            if (changes.Count == 0)
            {
                StatusText.Text = skipped.Count == 0 ? "Nothing to change in the selected sounds." : $"No sounds changed{skippedText}.";
                return;
            }

            void Apply(bool useAfter)
            {
                foreach (var (s, before, after) in changes)
                    (s.Data, s.Problem, s.Channels, s.SampleRate, s.BitsPerSample, s.FormatTag, s.Duration) = useAfter ? after : before;
                AfterSoundEdit();
            }

            foreach (var (s, _, after) in changes)
                (s.Data, s.Problem, s.Channels, s.SampleRate, s.BitsPerSample, s.FormatTag, s.Duration) = after;
            ClearSoundSelection();
            _undo.Record($"{undoName} on {changes.Count} sound(s)", () => Apply(false), () => Apply(true));
            MarkSndDirty();
            AfterSoundEdit();
            StatusText.Text = done(changes.Count) + (unchanged > 0 ? $"; {unchanged} unchanged" : "") + skippedText + ".";
        }
        finally
        {
            EndBusy();
        }
    }

    /// <summary>The single-sound prompt without Preview: there's no one sound to hear. Null if canceled.</summary>
    private Task<string?> PromptForSounds(string title, string label, string? initial, Func<string, string?> validate,
        Control? extra = null, Func<string, string?>? warn = null) =>
        Dialogs.PromptTextWithPreview(this, title, label, initial, validate,
            _ => Task.FromResult<string?>(null), () => { }, extra, warn, showPreview: false);

    // ---- value parsers (shared with the single-sound prompts) ---------------

    /// <summary>A finite number, read with the invariant culture first, then the user's own.</summary>
    private static bool TryParseNumber(string t, out double v) =>
        (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out v)
            || double.TryParse(t, NumberStyles.Float, CultureInfo.CurrentCulture, out v))
        && double.IsFinite(v);

    /// <summary>A positive length in ms ("ms" suffix allowed); what names it in the error ("length", "delay").</summary>
    private static string? ParseMilliseconds(string raw, string what, out double ms)
    {
        string t = raw.Trim();
        if (t.EndsWith("ms", StringComparison.OrdinalIgnoreCase)) t = t[..^2].Trim();
        if (!TryParseNumber(t, out ms) || ms <= 0) return $"\"{t}\" isn't a {what} in ms.";
        return null;
    }

    /// <summary>A value from min to max ("%" suffix allowed); rangeText completes "isn't …" in the error.</summary>
    private static string? ParseInRange(string raw, double min, double max, string rangeText, out double v)
    {
        string t = raw.Trim().TrimEnd('%').Trim();
        if (!TryParseNumber(t, out v) || v < min || v > max) return $"\"{t}\" isn't {rangeText}.";
        return null;
    }

    private static string? ParseVolumePercent(string raw, double maxPercent, out float percent)
    {
        string t = raw.Trim().TrimEnd('%').Trim();
        percent = 0;
        if (!TryParseNumber(t, out double p) || p <= 0 || p > maxPercent) return $"\"{t}\" isn't a volume between 1 and {maxPercent:0.##}%.";
        percent = (float)p;
        return null;
    }

    private static string? ParseLoudnessAmount(string raw, out double amount) =>
        ParseInRange(raw, SndEdit.MinLoudnessAmount, SndEdit.MaxLoudnessAmount,
            $"an amount between {SndEdit.MinLoudnessAmount:0} and {SndEdit.MaxLoudnessAmount:0}", out amount);

    private static string? ParseSampleRate(string raw, out int rate)
    {
        string t = raw.Trim();
        if (t.EndsWith("hz", StringComparison.OrdinalIgnoreCase)) t = t[..^2].Trim();
        if (!int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out rate)
            || rate < SndEdit.MinSampleRate || rate > SndEdit.MaxSampleRate)
            return $"\"{t}\" isn't a sample rate between {SndEdit.MinSampleRate} and {SndEdit.MaxSampleRate} Hz.";
        return null;
    }

    private static int MsToFrames(double ms, SoundEntry s) => Math.Max(1, (int)Math.Round(ms * s.SampleRate / 1000));

    // ---- the actions ---------------------------------------------------------

    private Task TrimSilenceMany(List<SoundEntry> many)
    {
        double threshold = _settings.Sounds.SilenceThreshold;
        return EditSounds(many, "Trimming silence", "trim silence", n => $"Trimmed silence on {n} sound(s)",
            s => (SndEdit.TrimSilence(s, threshold), null));
    }

    private Task MaximizeVolumeMany(List<SoundEntry> many) =>
        EditSounds(many, "Maximizing volume", "maximize volume", n => $"Maximized {n} sound(s) to full volume",
            s => (SndEdit.MaximizeVolume(s, out _), null));

    private Task ReverseMany(List<SoundEntry> many) =>
        EditSounds(many, "Reversing", "reverse", n => $"Reversed {n} sound(s)", s => (SndEdit.Reverse(s), null));

    private Task ConvertToMonoMany(List<SoundEntry> many) =>
        EditSounds(many, "Converting to mono", "convert to mono", n => $"Converted {n} sound(s) to mono",
            s => (SndEdit.ConvertToMono(s), null));

    private async Task IncreaseLoudnessMany(List<SoundEntry> many)
    {
        if (_busy || !await ResolvePendingSoundEditsBeforeSwitch()) return;
        double defaultAmount = Settings.Checked(nameof(Settings.SoundsSection.LoudnessDefaultAmount), _settings.Sounds.LoudnessDefaultAmount,
            SndEdit.DefaultLoudnessAmount, v => v >= SndEdit.MinLoudnessAmount && v <= SndEdit.MaxLoudnessAmount);
        if (await PromptForSounds("Increase Loudness",
                $"Amount ({SndEdit.MinLoudnessAmount:0}-{SndEdit.MaxLoudnessAmount:0}; higher lifts quiet parts more, each sound's peak stays unchanged):",
                defaultAmount.ToString("0.##", CultureInfo.InvariantCulture), raw => ParseLoudnessAmount(raw, out _)) is not { } text) return;
        ParseLoudnessAmount(text, out double amount);
        await EditSounds(many, "Increasing loudness", "increase loudness", n => $"Increased loudness on {n} sound(s)", s =>
        {
            if (SndEdit.IncreaseLoudness(s, amount, out _, out string? why) is { } data) return (data, null);
            return (null, why switch
            {
                "silent" or "already-loud" => null,
                "format" => "unsupported format",
                "header" => "unreadable WAV header",
                "below-gate" => "no measurable loudness",
                _ => why ?? "could not be processed",
            });
        });
    }

    private async Task ChangeVolumeMany(List<SoundEntry> many)
    {
        if (_busy || !await ResolvePendingSoundEditsBeforeSwitch()) return;
        double maxPercent = Settings.Checked(nameof(Settings.SoundsSection.VolumeMaxPercent), _settings.Sounds.VolumeMaxPercent,
            Settings.DefaultVolumeMaxPercent, v => double.IsFinite(v) && v > 0);
        // Peaks read once: the sounds can't change while the dialog is open.
        var peaks = many.Where(x => x.Problem is null).Select(x => SndEdit.Peak(x) ?? 0f).Where(p => p > 0).ToList();
        string? ClipWarning(string raw)
        {
            if (ParseVolumePercent(raw, maxPercent, out float p) is not null) return null;
            int clipping = peaks.Count(pk => pk * p / 100f > 1f);
            if (clipping == 0) return null;
            double limit = Math.Floor(1000 / peaks.Max()) / 10; // round down so the quoted value itself doesn't clip
            return $"This will clip {clipping} of the sounds. Highest volume without clipping any: {limit:0.#}%.";
        }

        if (await PromptForSounds("Change Volume", "Volume % (100 = unchanged):", "100",
                raw => ParseVolumePercent(raw, maxPercent, out _), warn: ClipWarning) is not { } text) return;
        ParseVolumePercent(text, maxPercent, out float percent);
        float gain = percent / 100f;
        if (Math.Abs(gain - 1f) < 0.001f)
        {
            StatusText.Text = "That volume is the same as it already is.";
            return;
        }
        await EditSounds(many, "Changing volume", "change volume", n => $"Set {n} sound(s) to {percent:0.#}% volume",
            s => (SndEdit.SetVolume(s, gain), null));
    }

    /// <summary>Change Pitch/Tempo/Speed on several sounds: one value for all.</summary>
    private async Task RateEditMany(List<SoundEntry> many, string title, string prompt, double neutral, double min, double max,
        string rangeText, Func<SoundEntry, double, CancellationToken, byte[]?> edit, Func<int, double, string> done)
    {
        if (_busy || !await ResolvePendingSoundEditsBeforeSwitch()) return;
        if (await PromptForSounds(title, prompt, neutral.ToString("0", CultureInfo.InvariantCulture),
                raw => ParseInRange(raw, min, max, rangeText, out _)) is not { } text) return;
        ParseInRange(text, min, max, rangeText, out double value);
        if (Math.Abs(value - neutral) < 0.001)
        {
            StatusText.Text = "That's the same as it already is.";
            return;
        }
        string verb = title.ToLowerInvariant();
        await EditSounds(many, title, verb, n => done(n, value), s => (edit(s, value, CancellationToken.None), null));
    }

    /// <summary>Fade In / Fade Out on several sounds: one length in ms; a sound shorter than that fades over its whole length.</summary>
    private async Task FadeMany(List<SoundEntry> many, bool fadeIn)
    {
        if (_busy || !await ResolvePendingSoundEditsBeforeSwitch()) return;
        string title = fadeIn ? "Fade in" : "Fade out";
        var linear = new RadioButton { Content = "Linear", IsChecked = true };
        var equalPower = new RadioButton { Content = "Equal-power" };
        var curves = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Children = { linear, equalPower } };
        double defaultMs = Settings.Checked(nameof(Settings.SoundsSection.FadeMilliseconds), _settings.Sounds.FadeMilliseconds,
            Settings.DefaultFadeMilliseconds, v => double.IsFinite(v) && v > 0);

        if (await PromptForSounds(title, $"{(fadeIn ? "Fade-in" : "Fade-out")} length in ms (shorter sounds fade over their whole length):",
                defaultMs.ToString("0.##", CultureInfo.InvariantCulture), raw => ParseMilliseconds(raw, "length", out _), curves) is not { } text) return;
        ParseMilliseconds(text, "length", out double ms);
        var curve = equalPower.IsChecked == true ? FadeCurve.EqualPower : FadeCurve.Linear;
        string shape = curve == FadeCurve.Linear ? "linear" : "equal-power";
        await EditSounds(many, fadeIn ? "Fading in" : "Fading out", title.ToLowerInvariant(),
            n => $"Faded {(fadeIn ? "in" : "out")} {n} sound(s) over {ms:0} ms ({shape})",
            s => (SndEdit.Fade(s, fadeIn, Math.Min(MsToFrames(ms, s), SndEdit.FrameCount(s)), curve), null));
    }

    private async Task InsertSilenceMany(List<SoundEntry> many)
    {
        if (_busy || !await ResolvePendingSoundEditsBeforeSwitch()) return;
        double maxMs = Settings.Checked(nameof(Settings.SoundsSection.InsertSilenceMaxMilliseconds), _settings.Sounds.InsertSilenceMaxMilliseconds,
            Settings.DefaultInsertSilenceMaxMilliseconds, v => double.IsFinite(v) && v > 0);
        double defaultMs = Settings.Checked(nameof(Settings.SoundsSection.InsertSilenceMilliseconds), _settings.Sounds.InsertSilenceMilliseconds,
            Settings.DefaultInsertSilenceMilliseconds, v => double.IsFinite(v) && v > 0 && v <= maxMs);
        string[] places = ["Leading (before the start)", "Trailing (after the end)", "Leading and trailing"];
        var where = new ComboBox { ItemsSource = places, SelectedIndex = 1, HorizontalAlignment = HorizontalAlignment.Stretch };
        var extra = new StackPanel { Spacing = 4, Children = { new TextBlock { Text = "Where:" }, where } };
        string? Parse(string raw, out double ms) =>
            ParseMilliseconds(raw, "length", out ms) ?? (ms > maxMs ? $"Up to {maxMs:N0} ms at a time." : null);

        if (await PromptForSounds("Insert Silence", "Silence length in ms:", defaultMs.ToString("0.##", CultureInfo.InvariantCulture),
                raw => Parse(raw, out _), extra) is not { } text) return;
        Parse(text, out double ms);
        int place = where.SelectedIndex; // 0 = leading only, 1 = trailing only, 2 = both
        string kind = place switch { 0 => "leading", 1 => "trailing", _ => "leading and trailing" };
        await EditSounds(many, "Inserting silence", "insert silence", n => $"Added {ms:0} ms of {kind} silence to {n} sound(s)", s =>
        {
            int frames = MsToFrames(ms, s);
            return (SndEdit.InsertSilence(s, place != 1 ? frames : 0, place != 0 ? frames : 0), null);
        });
    }

    /// <summary>Echo on several sounds: one delay, decay and repeat setting; Auto works out each sound's own repeats.</summary>
    private async Task EchoMany(List<SoundEntry> many)
    {
        if (_busy || !await ResolvePendingSoundEditsBeforeSwitch()) return;
        const double maxDecay = 100;
        int maxRepeats = Settings.Checked(nameof(Settings.SoundsSection.EchoMaxRepeats), _settings.Sounds.EchoMaxRepeats,
            Settings.DefaultEchoMaxRepeats, v => v >= 1);
        double silence = Settings.Checked(nameof(Settings.SoundsSection.SilenceThreshold), _settings.Sounds.SilenceThreshold,
            SndEdit.DefaultSilenceThreshold, v => double.IsFinite(v) && v > 0 && v < 1);
        double defaultDelay = Settings.Checked(nameof(Settings.SoundsSection.EchoDelayMilliseconds), _settings.Sounds.EchoDelayMilliseconds,
            Settings.DefaultEchoDelayMilliseconds, v => double.IsFinite(v) && v > 0);
        double defaultDecay = Settings.Checked(nameof(Settings.SoundsSection.EchoDecayPercent), _settings.Sounds.EchoDecayPercent,
            Settings.DefaultEchoDecayPercent, v => v > 0 && v <= maxDecay);
        double decayStep = Settings.Checked(nameof(Settings.SoundsSection.EchoDecayStep), _settings.Sounds.EchoDecayStep,
            Settings.DefaultEchoDecayStep, v => v is >= 1 and <= 100);

        var decayBox = new NumericUpDown { Minimum = 1, Maximum = (decimal)maxDecay, Increment = (decimal)decayStep, Value = (decimal)defaultDecay, FormatString = "0", Width = 110 };
        var auto = new CheckBox { Content = "Auto", IsChecked = true };
        var repeatsBox = new NumericUpDown { Minimum = 1, Maximum = maxRepeats, Increment = 1, Value = 1, FormatString = "0", Width = 110, IsEnabled = false };
        auto.IsCheckedChanged += (_, _) => repeatsBox.IsEnabled = auto.IsChecked != true;
        var extra = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children =
                {
                    new TextBlock { Text = "Decay %", Width = 70, VerticalAlignment = VerticalAlignment.Center }, decayBox,
                } },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children =
                {
                    new TextBlock { Text = "Repeats", Width = 70, VerticalAlignment = VerticalAlignment.Center }, repeatsBox, auto,
                } },
            },
        };

        double Decay() => (double)(decayBox.Value ?? (decimal)defaultDecay) / 100;
        // Peaks read once: the sounds can't change while the dialog is open.
        var peaks = many.Where(x => x.Problem is null && x.SampleRate > 0).ToDictionary(x => x, x => (double)(SndEdit.Peak(x) ?? 1));
        int RepeatsFor(SoundEntry s, double decay, bool isAuto, int fixedRepeats) =>
            isAuto ? SndEdit.AutoEchoRepeats(decay, peaks.TryGetValue(s, out double pk) ? pk : 1, silence, maxRepeats) : fixedRepeats;
        string? ClipWarning(string raw)
        {
            if (ParseMilliseconds(raw, "delay", out double ms) is not null) return null;
            double decay = Decay();
            bool isAuto = auto.IsChecked == true;
            int fixedRepeats = (int)(repeatsBox.Value ?? 1);
            int clipping = peaks.Keys.Count(s =>
                SndEdit.EchoPeak(s, 0, SndEdit.FrameCount(s), MsToFrames(ms, s), decay, RepeatsFor(s, decay, isAuto, fixedRepeats)) is > 1f);
            return clipping == 0 ? null : $"This will clip {clipping} of the sounds. Lower the decay, or their volume first.";
        }

        if (await PromptForSounds("Echo", "Delay in ms:", defaultDelay.ToString("0.##", CultureInfo.InvariantCulture),
                raw => ParseMilliseconds(raw, "delay", out _), extra, ClipWarning) is not { } text) return;
        ParseMilliseconds(text, "delay", out double delayMs);
        double chosenDecay = Decay();
        bool chosenAuto = auto.IsChecked == true;
        int chosenRepeats = (int)(repeatsBox.Value ?? 1);
        await EditSounds(many, "Adding echo", "echo",
            n => $"Added echo to {n} sound(s): {delayMs:0} ms, {chosenDecay * 100:0}%, {(chosenAuto ? "auto" : chosenRepeats.ToString(CultureInfo.InvariantCulture))} repeats", s =>
            {
                int total = SndEdit.FrameCount(s), delay = MsToFrames(delayMs, s);
                int repeats = RepeatsFor(s, chosenDecay, chosenAuto, chosenRepeats);
                int frameBytes = Math.Max(s.BitsPerSample, 8) / 8 * s.Channels;
                if (SndEdit.EchoLength(total, total, delay, repeats) * frameBytes > int.MaxValue / 2)
                    return (null, "the echoes would make it too long to hold");
                return (SndEdit.Echo(s, 0, total, delay, chosenDecay, repeats), null);
            });
    }

    private async Task ResampleMany(List<SoundEntry> many)
    {
        if (_busy || !await ResolvePendingSoundEditsBeforeSwitch()) return;
        int initial = many.FirstOrDefault(x => x.Problem is null && x.SampleRate > 0)?.SampleRate ?? 44100;
        if (await PromptForSounds("Resample", "New sample rate in Hz (common: 48000, 44100, 22050, 11025):",
                initial.ToString(CultureInfo.InvariantCulture), raw => ParseSampleRate(raw, out _)) is not { } text) return;
        ParseSampleRate(text, out int rate);
        int raising = many.Count(x => x.Problem is null && x.SampleRate > 0 && x.SampleRate < rate);
        if (raising > 0 && !await Dialogs.Confirm(this, "Upsample",
                $"{raising} of these sounds would go up to {rate} Hz, which adds no detail and makes them bigger.\n" +
                "It's only worth it right before Change Pitch or Change Speed shifts them up.",
                "Upsample")) return;
        await EditSounds(many, "Resampling", $"resample to {rate} Hz", n => $"Resampled {n} sound(s) to {rate} Hz", s =>
        {
            if (s.SampleRate == rate) return (null, null);
            if (SndEdit.Resample(s, rate) is { } data) return (data, null);
            return (null, "PCM data couldn't be read");
        });
    }

    private async Task ChangeBitDepthMany(List<SoundEntry> many)
    {
        if (_busy || !await ResolvePendingSoundEditsBeforeSwitch()) return;
        int first = many.FirstOrDefault(x => x.Problem is null)?.BitsPerSample ?? 0;
        int target = SndEdit.BitDepthTargets.Contains(first) ? first : 24;
        var radios = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        foreach (int bits in SndEdit.BitDepthTargets)
        {
            var radio = new RadioButton { Content = $"{bits}-bit", IsChecked = bits == target };
            radio.IsCheckedChanged += (_, _) => { if (radio.IsChecked == true) target = bits; };
            radios.Children.Add(radio);
        }
        var ditherBox = new CheckBox { Content = "Dither when lowering", IsChecked = true };
        var extra = new StackPanel { Spacing = 8, Children = { radios, ditherBox } };

        if (await PromptForSounds("Change Bit Depth", "New bit depth:", null, _ => null, extra) is null) return;
        int to = target;
        bool dither = ditherBox.IsChecked == true;
        int raising = many.Count(x => x.Problem is null && x.BitsPerSample < to);
        if (raising > 0 && !await Dialogs.Confirm(this, "Raise Bit Depth",
                $"{raising} of these sounds would go up to {to}-bit, which adds no detail and makes them bigger.\n" +
                "It's only worth it before several edits on them.",
                "Raise")) return;
        await EditSounds(many, "Changing bit depth", $"change to {to}-bit", n => $"Changed {n} sound(s) to {to}-bit",
            s => (SndEdit.ChangeBitDepth(s, to, dither), null));
    }

    /// <summary>Writes each selected sound as Group_Number.wav into a picked folder; asks before overwriting.</summary>
    private async Task ExportSoundsMany(List<SoundEntry> many)
    {
        var duplicateKeys = many.GroupBy(s => (s.Group, s.Index)).Where(g => g.Count() > 1).ToList();
        if (duplicateKeys.Count > 0)
        {
            var keys = string.Join(", ", duplicateKeys.Select(g => $"{g.Key.Group},{g.Key.Index}"));
            CrashLog.Error("ExportSoundsMany", $"Cannot export duplicate sound keys: {keys}");
            await Dialogs.ShowMessage(this, "Cannot Export Duplicate Sound Keys", keys);
            return;
        }
        if (await PickFolder("Export Sounds") is not { } dir) return;

        var files = many.Select(s => (Sound: s, Path: Path.Combine(dir, $"{s.Group}_{s.Index}.wav"))).ToList();
        int existing = files.Count(f => File.Exists(f.Path));
        if (existing > 0 && !await Dialogs.Confirm(this, "Export Sounds",
                $"{existing} of these files already exist in that folder and will be overwritten.", "Overwrite")) return;

        foreach (var (s, path) in files) File.WriteAllBytes(path, s.Data);
        CrashLog.Trace($"[bulk] exported {files.Count} selected sounds to {dir}");
        StatusText.Text = $"Exported {files.Count} sounds to {dir}";
    }

    /// <summary>Copies each selected sound right after its original (same group, next free number); one undo step.</summary>
    private void DuplicateSoundsMany(List<SoundEntry> many)
    {
        if (_snd is not { } file) return;
        var before = file.Sounds.ToList();
        // In list order, so each copy's number is picked after the copies before it exist.
        foreach (var s in many.OrderBy(file.Sounds.IndexOf).ToList())
        {
            var copy = new SoundEntry { Group = s.Group, Data = s.Data };
            copy.Index = NextFreeSoundIndex(copy.Group);
            SndReader.Describe(copy);
            file.Sounds.Insert(file.Sounds.IndexOf(s) + 1, copy);
            CrashLog.Trace($"[sound] duplicate {s.Group},{s.Index} -> {copy.Group},{copy.Index}");
        }
        var after = file.Sounds.ToList();

        void Apply(List<SoundEntry> order)
        {
            file.Sounds = [.. order];
            AfterSoundEdit();
        }
        _undo.Record($"duplicate {many.Count} sounds", () => Apply(before), () => Apply(after));
        MarkSndDirty();
        AfterSoundEdit();
        StatusText.Text = $"Duplicated {many.Count} sounds.";
    }
}
