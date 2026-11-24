using System.Buffers.Binary;
using NAudio.Vorbis;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NLayer.NAudioSupport;
using VGAudio.Containers.NintendoWare;
using VGAudio.Formats.Pcm16;

namespace WheelWizard.CustomMusic;

/// <summary>A song converted into the BRSTM files the game streams, plus the tags shown in the list.</summary>
public sealed record ConvertedMusic(byte[] Normal, byte[]? FinalLap, string? Title, string? Artist);

public sealed class SongTooLongException() : Exception($"Songs can be at most {MusicConverter.MaxSeconds / 60} minutes long.");

/// <summary>
/// Converts a song into looping 32 kHz stereo DSP-ADPCM BRSTMs. NAudio (with NLayer and NVorbis) decodes and
/// resamples, VGAudio reads and writes the Nintendo stream formats and TagLib# reads the title and artist.
/// </summary>
public static class MusicConverter
{
    public const int SampleRate = 32000;

    /// <summary>Retro Rewind plays a track's music this much faster (and higher) on the final lap; the final lap file matches it.</summary>
    public const double FinalLapSpeed = 1.06;

    public const int MaxSeconds = 10 * 60;

    public static readonly string[] Extensions = [".mp3", ".ogg", ".wav", ".aif", ".aiff", ".brstm", ".bcstm", ".bfstm"];

    public static bool IsSupported(string extension) => Extensions.Contains(extension.ToLowerInvariant());

    /// <param name="file">The whole song file.</param>
    /// <param name="extension">The file's extension, which picks the decoder.</param>
    /// <param name="withFinalLap">Also make the final lap version.</param>
    /// <param name="cancellationToken">Stops the conversion.</param>
    public static ConvertedMusic Convert(byte[] file, string extension, bool withFinalLap, CancellationToken cancellationToken = default)
    {
        extension = extension.ToLowerInvariant();
        var normal = Render(file, extension, SampleRate, cancellationToken);
        // Rendered at a lower rate but stored as 32 kHz, the song plays faster and higher.
        var finalLap = withFinalLap ? Render(file, extension, (int)Math.Round(SampleRate / FinalLapSpeed), cancellationToken) : null;
        var (title, artist) = ReadTags(file, extension);
        return new ConvertedMusic(normal, finalLap, title, artist);
    }

    private static byte[] Render(byte[] file, string extension, int rate, CancellationToken cancellationToken)
    {
        using var source = Open(file, extension);
        var sourceRate = source.Audio.WaveFormat.SampleRate;
        ISampleProvider stereo = source.Audio.WaveFormat.Channels switch
        {
            1 => new MonoToStereoSampleProvider(source.Audio),
            2 => source.Audio,
            // The first two channels: the first track of a multi-track BRSTM, the front of surround sound.
            _ => new MultiplexingSampleProvider([source.Audio], 2),
        };
        var output = sourceRate == rate ? stereo : new WdlResamplingSampleProvider(stereo, rate);

        var left = new List<short>(rate * 60);
        var right = new List<short>(rate * 60);
        var buffer = new float[16384];
        int read;
        while ((read = output.Read(buffer)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var i = 0; i + 1 < read; i += 2)
            {
                left.Add(ToPcm16(buffer[i]));
                right.Add(ToPcm16(buffer[i + 1]));
            }

            if (left.Count > MaxSeconds * rate)
                throw new SongTooLongException();
        }

        // Damaged or mislabelled files (an M4A renamed to .mp3) can decode to a few milliseconds of noise.
        if (left.Count < rate)
            throw new InvalidDataException("The file contains less than a second of audio.");

        // Loop points from a Nintendo stream are kept; everything else loops as a whole.
        var (loopStart, loopEnd) = (0, left.Count);
        if (source.Loop is { } loop)
        {
            var start = (int)((long)loop.Start * rate / sourceRate);
            var end = (int)Math.Min(left.Count, (long)loop.End * rate / sourceRate);
            if (end - start >= rate)
                (loopStart, loopEnd) = (start, end);
        }

        var pcm = new Pcm16Format([left.ToArray(), right.ToArray()], SampleRate).WithLoop(true, loopStart, loopEnd);
        return new BrstmWriter().GetFile(pcm);
    }

    private static short ToPcm16(float sample) =>
        float.IsFinite(sample) ? (short)Math.Clamp(MathF.Round(sample * short.MaxValue), short.MinValue, short.MaxValue) : (short)0;

    private sealed record Source(ISampleProvider Audio, IDisposable Owner, (int Start, int End)? Loop = null) : IDisposable
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

                var raw = new RawSourceWaveStream(interleaved, 0, interleaved.Length, new WaveFormat(pcm.SampleRate, 16, pcm.ChannelCount));
                return new(raw.ToSampleProvider(), raw, pcm.Looping ? (pcm.LoopStart, pcm.LoopEnd) : null);
            default:
                throw new NotSupportedException($"{extension} files are not supported.");
        }
    }

    private static (string? Title, string? Artist) ReadTags(byte[] file, string extension)
    {
        try
        {
            using var tags = TagLib.File.Create(new TagSource("song" + extension, file), TagLib.ReadStyle.None);
            return (Clean(tags.Tag.Title), Clean(tags.Tag.FirstPerformer ?? tags.Tag.FirstAlbumArtist));
        }
        // Tags are optional, and TagLib# throws many kinds of exceptions on files it cannot read (BRSTMs have none).
        catch (Exception)
        {
            return (null, null);
        }
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
