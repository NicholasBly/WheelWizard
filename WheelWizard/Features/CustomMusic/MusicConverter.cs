using System.Buffers.Binary;
using NAudio.Vorbis;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NLayer.NAudioSupport;
using NWaves.Operations;
using NWaves.Signals;
using VGAudio.Containers.NintendoWare;
using VGAudio.Formats.Pcm16;
using Flac = CUETools.Codecs.FLAKE;

namespace WheelWizard.CustomMusic;

public enum FinalLapStyle
{
    /// <summary>Faster and higher, like Retro Rewind's own speed-up.</summary>
    Faster,

    /// <summary>Faster at the same pitch, closest to the original game's final lap music.</summary>
    FasterSamePitch,

    /// <summary>The same as laps 1 and 2.</summary>
    Unchanged,
}

/// <param name="TargetLoudness">Loudness (LUFS) to match, or null to keep the song's own volume.</param>
public sealed record ConvertOptions(bool WithFinalLap, FinalLapStyle FinalLap = FinalLapStyle.Faster, double? TargetLoudness = null);

/// <summary>A song converted into the BRSTM files the game streams, plus the tags shown in the list.</summary>
public sealed record ConvertedMusic(byte[] Normal, byte[]? FinalLap, string? Title, string? Artist);

public sealed class SongTooLongException() : Exception($"Songs can be at most {MusicConverter.MaxSeconds / 60} minutes long.");

/// <summary>Converts a song into looping 32 kHz stereo BRSTMs with NAudio, NLayer, NVorbis, FLAKE, NWaves, VGAudio and TagLib#.</summary>
public static class MusicConverter
{
    public const int SampleRate = 32000;

    /// <summary>Retro Rewind plays a track's music this much faster (and higher) on the final lap.</summary>
    public const double FinalLapSpeed = 1.06;

    /// <summary>Tempo of the same-pitch final lap version, closest to the original game's final lap music.</summary>
    public const double FinalLapTempo = 1.08;

    /// <summary>A typical loudness of the game's music, used when the original music cannot be measured.</summary>
    public const double DefaultLoudness = -16;

    public const int MaxSeconds = 10 * 60;

    // -1 dBFS, which leaves room for DSP-ADPCM's small overshoots.
    private const float PeakCeiling = 0.891f;

    public static readonly string[] Extensions = [".mp3", ".ogg", ".flac", ".wav", ".aif", ".aiff", ".brstm", ".bcstm", ".bfstm"];

    public static bool IsSupported(string extension) => Extensions.Contains(extension.ToLowerInvariant());

    public static ConvertedMusic Convert(
        byte[] file,
        string extension,
        ConvertOptions options,
        CancellationToken cancellationToken = default
    )
    {
        extension = extension.ToLowerInvariant();
        var tags = ReadTags(file, extension);
        var song = MakeLoop(Render(file, extension, tags.Loop, cancellationToken), cancellationToken);
        if (options.TargetLoudness is { } target)
            MatchLoudness(song, target);

        var normal = Encode(song);
        var finalLap = !options.WithFinalLap
            ? null
            : options.FinalLap switch
            {
                FinalLapStyle.Faster => Encode(Speed(song, (int)Math.Round(SampleRate / FinalLapSpeed))),
                FinalLapStyle.FasterSamePitch => Encode(Stretch(song, 1 / FinalLapTempo)),
                _ => normal,
            };
        return new ConvertedMusic(normal, finalLap, tags.Title, tags.Artist);
    }

    /// <summary>How loud a song file plays, in LUFS.</summary>
    public static double MeasureLoudness(byte[] file, string extension)
    {
        var song = Render(file, extension.ToLowerInvariant(), null, CancellationToken.None);
        return Loudness.Measure(song.Channels, SampleRate, song.End);
    }

    /// <summary>The song as stereo audio, for playing it.</summary>
    public static ISampleProvider Decode(byte[] file, string extension) => ToStereo(Open(file, extension.ToLowerInvariant()).Audio);

    /// <summary>Stereo 32 kHz audio; playback runs to <see cref="End"/> and, once looped, jumps back to <see cref="LoopStart"/>.</summary>
    private sealed record Song(float[][] Channels, int LoopStart, int End, bool HasLoop);

    private static Song Render(byte[] file, string extension, (long Start, long End)? tagLoop, CancellationToken cancellationToken)
    {
        using var source = Open(file, extension);
        var sourceRate = source.Audio.WaveFormat.SampleRate;
        var stereo = ToStereo(source.Audio);
        var output = sourceRate == SampleRate ? stereo : new WdlResamplingSampleProvider(stereo, SampleRate);

        var left = new List<float>(SampleRate * 60);
        var right = new List<float>(SampleRate * 60);
        var buffer = new float[16384];
        int read;
        while ((read = output.Read(buffer)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var i = 0; i + 1 < read; i += 2)
            {
                left.Add(float.IsFinite(buffer[i]) ? buffer[i] : 0);
                right.Add(float.IsFinite(buffer[i + 1]) ? buffer[i + 1] : 0);
            }

            if (left.Count > MaxSeconds * SampleRate)
                throw new SongTooLongException();
        }

        // Damaged or mislabelled files (an M4A renamed to .mp3) can decode to a few milliseconds of noise.
        if (left.Count < SampleRate)
            throw new InvalidDataException("The file contains less than a second of audio.");

        float[][] channels = [left.ToArray(), right.ToArray()];
        if ((source.Loop ?? tagLoop) is { } loop)
        {
            var start = (int)(loop.Start * SampleRate / sourceRate);
            var end = (int)Math.Min(channels[0].Length, loop.End * SampleRate / sourceRate);
            if (start >= 0 && end - start >= SampleRate)
                return new Song(channels, start, end, HasLoop: true);
        }

        return TrimSilence(channels);
    }

    private static ISampleProvider ToStereo(ISampleProvider audio) =>
        audio.WaveFormat.Channels switch
        {
            1 => new MonoToStereoSampleProvider(audio),
            2 => audio,
            // The first two channels: the first track of a multi-track BRSTM, the front of surround sound.
            _ => new MultiplexingSampleProvider([audio], 2),
        };

    /// <summary>Drops silence at the start and the end, which would otherwise leave a gap in the loop.</summary>
    private static Song TrimSilence(float[][] channels)
    {
        const float threshold = 0.001f;
        var length = channels[0].Length;
        bool Audible(int i) => Math.Abs(channels[0][i]) > threshold || Math.Abs(channels[1][i]) > threshold;
        var first = 0;
        while (first < length - 1 && !Audible(first))
            first++;
        var last = length - 1;
        while (last > first && !Audible(last))
            last--;

        var keep = SampleRate / 100;
        first = Math.Max(0, first - keep);
        last = Math.Min(length - 1, last + keep);
        if (last - first < SampleRate)
            return new Song(channels, 0, length, HasLoop: false);
        return new Song([channels[0][first..(last + 1)], channels[1][first..(last + 1)]], 0, last + 1 - first, HasLoop: false);
    }

    /// <summary>Loops the song where the music repeats, or else by crossfading the ending into the opening.</summary>
    private static Song MakeLoop(Song song, CancellationToken cancellationToken)
    {
        if (song.HasLoop)
            return song;

        if (LoopFinder.Find(song.Channels, SampleRate, cancellationToken) is { } found)
            return Blend(song with { LoopStart = found.Start, End = found.End, HasLoop = true });

        var fade = Math.Min(SampleRate * 5 / 2, song.End / 4);
        var channels = song.Channels.Select(channel => channel[..song.End]).ToArray();
        foreach (var channel in channels)
        {
            for (var i = 0; i < fade; i++)
            {
                // Equal power keeps the level steady across unrelated music.
                var t = (i + 0.5) / fade * Math.PI / 2;
                var at = song.End - fade + i;
                channel[at] = (float)(channel[at] * Math.Cos(t) + channel[i] * Math.Sin(t));
            }
        }

        // The opening already played its first part during the crossfade, so the loop continues from there.
        return new Song(channels, fade, song.End, HasLoop: true);
    }

    /// <summary>Blends the last milliseconds before the loop end into the audio before the loop start, so the jump has no click.</summary>
    private static Song Blend(Song song)
    {
        var blend = Math.Min(SampleRate * 3 / 100, song.LoopStart);
        var channels = song.Channels.Select(channel => channel[..song.End]).ToArray();
        foreach (var channel in channels)
        {
            for (var i = 0; i < blend; i++)
            {
                var weight = (i + 0.5f) / blend;
                var at = song.End - blend + i;
                channel[at] = channel[at] * (1 - weight) + channel[song.LoopStart - blend + i] * weight;
            }
        }

        return song with
        {
            Channels = channels,
        };
    }

    /// <summary>Changes the volume to <paramref name="target"/> LUFS, as far as the peaks allow without distorting.</summary>
    private static void MatchLoudness(Song song, double target)
    {
        var loudness = Loudness.Measure(song.Channels, SampleRate, song.End);
        if (double.IsNegativeInfinity(loudness))
            return;

        var peak = 0f;
        foreach (var channel in song.Channels)
        {
            for (var i = 0; i < song.End; i++)
                peak = Math.Max(peak, Math.Abs(channel[i]));
        }

        var gain = (float)Math.Min(Math.Pow(10, (target - loudness) / 20), PeakCeiling / Math.Max(peak, 1e-6f));
        foreach (var channel in song.Channels)
        {
            for (var i = 0; i < channel.Length; i++)
                channel[i] *= gain;
        }
    }

    /// <summary>Plays the song faster and higher: resampled to <paramref name="rate"/> but stored as 32 kHz.</summary>
    private static Song Speed(Song song, int rate) =>
        Retime(
            song,
            (double)rate / SampleRate,
            channels => channels.Select(channel => ReadAll(new WdlResamplingSampleProvider(new Samples(channel), rate))).ToArray()
        );

    /// <summary>Plays the song faster at the same pitch: <paramref name="stretch"/> is the new length relative to the old one.</summary>
    private static Song Stretch(Song song, double stretch) =>
        Retime(
            song,
            stretch,
            channels =>
                channels.Select(channel => Operation.TimeStretch(new DiscreteSignal(SampleRate, channel), stretch).Samples).ToArray()
        );

    /// <summary>Changes the speed with <paramref name="process"/>, given the music past the loop start so the jump stays smooth.</summary>
    private static Song Retime(Song song, double ratio, Func<float[][], float[][]> process)
    {
        var tail = Math.Min(SampleRate / 2, song.End - song.LoopStart);
        var processed = process(
            song.Channels.Select(channel => channel[..song.End].Concat(channel[song.LoopStart..(song.LoopStart + tail)]).ToArray())
                .ToArray()
        );
        var end = Math.Min((int)Math.Round(song.End * ratio), processed.Min(channel => channel.Length));
        return Blend(new Song(processed, (int)Math.Round(song.LoopStart * ratio), end, HasLoop: true));
    }

    private static float[] ReadAll(ISampleProvider audio)
    {
        var samples = new List<float>();
        var buffer = new float[16384];
        int read;
        while ((read = audio.Read(buffer)) > 0)
            samples.AddRange(buffer.AsSpan(0, read));
        return samples.ToArray();
    }

    private sealed class Samples(float[] samples) : ISampleProvider
    {
        private int _position;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 1);

        public int Read(Span<float> buffer)
        {
            var count = Math.Min(buffer.Length, samples.Length - _position);
            samples.AsSpan(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }
    }

    private static byte[] Encode(Song song)
    {
        var channels = song.Channels.Select(channel => new short[song.End]).ToArray();
        for (var c = 0; c < channels.Length; c++)
        {
            for (var i = 0; i < song.End; i++)
                channels[c][i] = (short)Math.Clamp(MathF.Round(song.Channels[c][i] * short.MaxValue), short.MinValue, short.MaxValue);
        }

        return new BrstmWriter().GetFile(new Pcm16Format(channels, SampleRate).WithLoop(true, song.LoopStart, song.End));
    }

    private sealed record Source(ISampleProvider Audio, IDisposable Owner, (long Start, long End)? Loop = null) : IDisposable
    {
        public void Dispose() => Owner.Dispose();
    }

    private static Source Open(byte[] file, string extension)
    {
        var stream = new MemoryStream(file, writable: false);
        switch (extension)
        {
            case ".mp3":
                var mp3 = new ManagedMpegStream(stream);
                return new(mp3.ToSampleProvider(), mp3);
            case ".ogg":
                var ogg = new VorbisSampleProvider(stream);
                return new(ogg, ogg);
            case ".flac":
                var flac = new Flac.FlakeReader(null, stream);
                var decoded = new MemoryStream();
                var block = new Flac.AudioBuffer(flac, 16384);
                while (flac.Read(block, -1) > 0)
                    decoded.Write(block.Bytes, 0, block.ByteLength);
                flac.Close();
                return Raw(decoded.ToArray(), flac.PCM.SampleRate, flac.PCM.BitsPerSample, flac.PCM.ChannelCount, null);
            case ".wav":
                var wav = new WaveFileReader(stream);
                return new(wav.ToSampleProvider(), wav);
            case ".aif"
            or ".aiff":
                var aiff = new AiffFileReader(stream);
                return new(aiff.ToSampleProvider(), aiff);
            case ".brstm"
            or ".bcstm"
            or ".bfstm":
                var audio = extension == ".brstm" ? new BrstmReader().Read(file) : new BCFstmReader().Read(file);
                var pcm = audio.GetFormat<Pcm16Format>();
                var interleaved = new byte[pcm.SampleCount * pcm.ChannelCount * 2];
                for (var i = 0; i < pcm.SampleCount; i++)
                {
                    for (var channel = 0; channel < pcm.ChannelCount; channel++)
                        BinaryPrimitives.WriteInt16LittleEndian(
                            interleaved.AsSpan((i * pcm.ChannelCount + channel) * 2),
                            pcm.Channels[channel][i]
                        );
                }

                return Raw(interleaved, pcm.SampleRate, 16, pcm.ChannelCount, pcm.Looping ? (pcm.LoopStart, pcm.LoopEnd) : null);
            default:
                throw new NotSupportedException($"{extension} files are not supported.");
        }
    }

    private static Source Raw(byte[] pcm, int sampleRate, int bits, int channels, (long Start, long End)? loop)
    {
        var raw = new RawSourceWaveStream(pcm, 0, pcm.Length, new WaveFormat(sampleRate, bits, channels));
        return new(raw.ToSampleProvider(), raw, loop);
    }

    private sealed record Tags(string? Title, string? Artist, (long Start, long End)? Loop);

    private static Tags ReadTags(byte[] file, string extension)
    {
        try
        {
            using var tags = TagLib.File.Create(new TagSource("song" + extension, file), TagLib.ReadStyle.None);
            return new Tags(Clean(tags.Tag.Title), Clean(tags.Tag.FirstPerformer ?? tags.Tag.FirstAlbumArtist), LoopTags(tags));
        }
        // Tags are optional, and TagLib# throws many kinds of exceptions on files it cannot read (BRSTMs have none).
        catch (Exception)
        {
            return new Tags(null, null, null);
        }
    }

    /// <summary>The LOOPSTART and LOOPLENGTH (or LOOPEND) tags, in samples, that game music rips often carry.</summary>
    private static (long Start, long End)? LoopTags(TagLib.File tags)
    {
        if (
            tags.GetTag(TagLib.TagTypes.Xiph) is not TagLib.Ogg.XiphComment comment
            || !long.TryParse(comment.GetFirstField("LOOPSTART"), out var start)
        )
            return null;
        if (long.TryParse(comment.GetFirstField("LOOPLENGTH"), out var length))
            return (start, start + length);
        return long.TryParse(comment.GetFirstField("LOOPEND"), out var end) ? (start, end) : null;
    }

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private sealed class TagSource(string name, byte[] file) : TagLib.File.IFileAbstraction
    {
        private readonly MemoryStream _stream = new(file, writable: false);

        public string Name => name;
        public Stream ReadStream => _stream;
        public Stream WriteStream => _stream;

        public void CloseStream(Stream stream) { }
    }
}
