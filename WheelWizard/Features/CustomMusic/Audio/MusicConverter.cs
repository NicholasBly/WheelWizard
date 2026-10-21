using WheelWizard.CustomMusic.Audio.Dsp;
using WheelWizard.CustomMusic.Audio.Formats;

namespace WheelWizard.CustomMusic.Audio;

/// <summary>How the final lap version of a course song is made.</summary>
public enum FinalLapStyle
{
    /// <summary>Speed and pitch +6%: exactly what Retro Rewind's own "music speed-up" does to the normal song.</summary>
    Faster,

    /// <summary>Tempo +8% with the pitch kept, closest to the original game's final lap tracks.</summary>
    FasterKeepPitch,

    /// <summary>The final lap plays the same song as laps 1 and 2.</summary>
    Unchanged,
}

public sealed record ConversionOptions
{
    /// <summary>Mario Kart Wii streams are 32 kHz.</summary>
    public int SampleRate { get; init; } = 32000;

    /// <summary>Whether the result loops (course and menu music) or plays once (fanfares and jingles).</summary>
    public bool Loop { get; init; } = true;

    /// <summary>Also produce the final lap file.</summary>
    public bool CreateFinalLap { get; init; }

    public FinalLapStyle FinalLapStyle { get; init; } = FinalLapStyle.Faster;

    /// <summary>Integrated loudness the song is matched to, normally that of the music it replaces.</summary>
    public double TargetLufs { get; init; } = MusicLoudness.DefaultTargetLufs;

    /// <summary>User adjustment on top of the matched loudness.</summary>
    public double VolumeOffsetDb { get; init; }

    /// <summary>
    /// The volume byte (1..127) to store in the BRSTM header, or 0 for none. Retro Rewind reads it when the music
    /// starts and uses it instead of the volume in the sound archive, so a replacement must carry the same byte
    /// as the music it replaces to play at the same level.
    /// </summary>
    public byte PulsarVolume { get; init; }

    /// <summary>Length of the crossfade that makes songs without loop points loop smoothly.</summary>
    public double CrossfadeSeconds { get; init; } = 2.5;

    public double MaxDurationSeconds { get; init; } = 15 * 60;
}

public sealed record ConversionReport(
    double SourceLufs,
    double TargetLufs,
    double AppliedGainDb,
    double LimiterReductionDb,
    TimeSpan Duration,
    bool UsedSourceLoopPoints,
    int LoopStartSample,
    int SampleCount
);

public sealed record ConversionResult(byte[] Normal, byte[]? FinalLap, ConversionReport Report);

public static class MusicLoudness
{
    /// <summary>
    /// Fallback when the original music could not be measured: a typical level for game music (loud enough
    /// to sit over engine noise and effects). Real measurements of the replaced music always take priority.
    /// </summary>
    public const double DefaultTargetLufs = -16.0;

    public const double FinalLapPitchSpeed = 1.06;
    public const double FinalLapTempoSpeed = 1.08;
}

public static class MusicConverter
{
    public static ConversionResult Convert(
        AudioBuffer source,
        ConversionOptions options,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default
    )
    {
        progress?.Report(0.05);
        // Loop points from the file (WAV smpl, LOOPSTART tags, BRSTM) are used as-is; otherwise we build a loop.
        var hasSourceLoop = options.Loop && source.Loop is { } sourceLoop && sourceLoop.IsValid(source.SampleCount);
        if (!hasSourceLoop)
            source.Loop = null;

        var stereo = AudioOps.ToStereo(source);
        var trimmed = AudioOps.TrimSilence(stereo);
        cancellationToken.ThrowIfCancellationRequested();

        // Give the resampler real audio to look at across the loop seam.
        var extended = AudioOps.ExtendPastLoopEnd(trimmed, trimmed.SampleRate / 10);
        var audio = Resampler.Resample(extended, options.SampleRate);
        audio = AudioOps.Truncate(audio, (int)(options.MaxDurationSeconds * options.SampleRate));
        progress?.Report(0.3);
        cancellationToken.ThrowIfCancellationRequested();

        // Loudness matching (measured on the part that actually plays, before any loop seam work).
        var measureLength = audio.Loop?.End ?? audio.SampleCount;
        var loudness = LoudnessMeter.Measure(audio.Channels, audio.SampleRate, 0, measureLength);
        if (loudness.IsSilent)
            throw new InvalidDataException("This song is silent, so there is nothing to convert.");

        var gainDb = options.TargetLufs - loudness.IntegratedLufs + options.VolumeOffsetDb;
        AudioOps.ApplyGain(audio, gainDb);
        var limiterReduction = PeakLimiter.Apply(audio, ceilingDb: -1.0);
        progress?.Report(0.45);
        cancellationToken.ThrowIfCancellationRequested();

        if (options.Loop)
        {
            if (audio.Loop == null)
            {
                audio = AudioOps.CreateCrossfadeLoop(audio, options.CrossfadeSeconds);
                // An equal-power crossfade can lift very correlated material (a held note) a little; re-check.
                var looped = LoudnessMeter.Measure(audio.Channels, audio.SampleRate, 0, audio.Loop?.End ?? audio.SampleCount);
                var correction = options.TargetLufs + options.VolumeOffsetDb - looped.IntegratedLufs;
                if (!looped.IsSilent && Math.Abs(correction) > 0.2)
                {
                    AudioOps.ApplyGain(audio, correction);
                    gainDb += correction;
                }

                limiterReduction = Math.Max(limiterReduction, PeakLimiter.Apply(audio, ceilingDb: -1.0));
            }
        }
        else
        {
            AudioOps.FadeOutTail(audio, 0.01);
        }

        var normalBuffer = options.Loop ? AudioOps.AlignLoopStart(audio, Brstm.SamplesPerBlock) : audio;
        AudioBuffer? finalBuffer = null;
        if (options.CreateFinalLap && options.FinalLapStyle != FinalLapStyle.Unchanged)
        {
            finalBuffer = options.FinalLapStyle == FinalLapStyle.FasterKeepPitch
                ? MakeFinalLap(audio, buffer => TimeStretcher.ChangeTempo(buffer, MusicLoudness.FinalLapTempoSpeed))
                : MakeFinalLap(audio, buffer => Resampler.Resample(buffer, buffer.SampleRate, MusicLoudness.FinalLapPitchSpeed));
        }

        progress?.Report(0.6);
        cancellationToken.ThrowIfCancellationRequested();

        // The DSP-ADPCM encode is the slow part; both versions encode at the same time.
        byte[] normal = [];
        byte[]? finalLap = null;
        Parallel.Invoke(
            () => normal = Encode(normalBuffer, options.PulsarVolume),
            () =>
            {
                if (finalBuffer != null)
                    finalLap = Encode(finalBuffer, options.PulsarVolume);
            }
        );
        if (options.CreateFinalLap && options.FinalLapStyle == FinalLapStyle.Unchanged)
            finalLap = normal;
        progress?.Report(1.0);

        var report = new ConversionReport(
            loudness.IntegratedLufs,
            options.TargetLufs + options.VolumeOffsetDb,
            gainDb,
            limiterReduction,
            TimeSpan.FromSeconds(normalBuffer.SampleCount / (double)normalBuffer.SampleRate),
            hasSourceLoop,
            normalBuffer.Loop?.Start ?? 0,
            normalBuffer.SampleCount
        );
        return new ConversionResult(normal, finalLap, report);
    }

    private static AudioBuffer MakeFinalLap(AudioBuffer looped, Func<AudioBuffer, AudioBuffer> speedUp)
    {
        var extended = AudioOps.ExtendPastLoopEnd(looped, looped.SampleRate / 5);
        var faster = speedUp(extended);
        return faster.Loop != null ? AudioOps.AlignLoopStart(faster, Brstm.SamplesPerBlock) : faster;
    }

    private static byte[] Encode(AudioBuffer buffer, byte pulsarVolume)
    {
        var pcm = new short[buffer.ChannelCount][];
        for (var c = 0; c < buffer.ChannelCount; c++)
            pcm[c] = AudioOps.ToPcm16(buffer.Channels[c], seed: 0x5EED + c);

        return BrstmWriter.Write(
            new BrstmStream
            {
                Channels = pcm,
                SampleRate = buffer.SampleRate,
                Looping = buffer.Loop != null,
                LoopStart = buffer.Loop?.Start ?? 0,
                PulsarVolume = pulsarVolume == 0 ? null : Math.Min(pulsarVolume, (byte)0x7F),
            }
        );
    }
}
