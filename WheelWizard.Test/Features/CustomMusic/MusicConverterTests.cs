using System.Text;
using CUETools.Codecs.FLAKE;
using NAudio.Wave;
using VGAudio.Containers.NintendoWare;
using VGAudio.Formats.Pcm16;
using WheelWizard.CustomMusic;

namespace WheelWizard.Test.Features.CustomMusic;

public class MusicConverterTests
{
    [Fact]
    public void Wav_BecomesALoopingStereoBrstm()
    {
        var music = MusicConverter.Convert(Wav(48000, channels: 1, seconds: 3), ".WAV", new ConvertOptions(WithFinalLap: false));

        var normal = ReadBrstm(music.Normal);
        Assert.Null(music.FinalLap);
        Assert.Equal((MusicConverter.SampleRate, 2), (normal.SampleRate, normal.ChannelCount));
        Assert.True(normal.Channels[1].Max() > 10_000); // the mono song plays on both sides
        Assert.InRange(Frequency(normal), 437, 443);
        // Without a repeat in the music, the ending crossfades into the opening and the loop continues after it.
        Assert.True(normal.Looping);
        Assert.True(normal.LoopStart > 0);
        Assert.Equal(normal.SampleCount, normal.LoopEnd);
    }

    [Fact]
    public void FinalLap_FollowsTheChosenStyle()
    {
        var song = Wav(32000, channels: 2, seconds: 4);
        var normal = ReadBrstm(MusicConverter.Convert(song, ".wav", new ConvertOptions(false)).Normal);

        var faster = ReadBrstm(MusicConverter.Convert(song, ".wav", new ConvertOptions(true, FinalLapStyle.Faster)).FinalLap!);
        Assert.InRange(LoopLength(faster) * MusicConverter.FinalLapSpeed, LoopLength(normal) * 0.995, LoopLength(normal) * 1.005);
        Assert.InRange(Frequency(faster), 463, 470);

        var samePitch = ReadBrstm(MusicConverter.Convert(song, ".wav", new ConvertOptions(true, FinalLapStyle.FasterSamePitch)).FinalLap!);
        Assert.InRange(LoopLength(samePitch) * MusicConverter.FinalLapTempo, LoopLength(normal) * 0.995, LoopLength(normal) * 1.005);
        Assert.InRange(Frequency(samePitch), 436, 444);

        var unchanged = MusicConverter.Convert(song, ".wav", new ConvertOptions(true, FinalLapStyle.Unchanged));
        Assert.Same(unchanged.Normal, unchanged.FinalLap);
    }

    [Fact]
    public void RepeatingMusic_LoopsAtTheRepeat()
    {
        var music = MusicConverter.Convert(StructuredSong(), ".wav", new ConvertOptions(false));

        // The verse and chorus (31 s together) play twice; the loop jumps from the second time back to the first.
        Assert.InRange(LoopLength(ReadBrstm(music.Normal)) / (double)MusicConverter.SampleRate, 30.9, 31.1);
    }

    [Fact]
    public void Brstm_KeepsItsLoop()
    {
        var channel = Tone(32000 * 4, 32000, 0.5);
        var song = new BrstmWriter().GetFile(new Pcm16Format([channel, channel], 32000).WithLoop(true, 32000, 96000));

        var normal = ReadBrstm(MusicConverter.Convert(song, ".brstm", new ConvertOptions(false)).Normal);

        Assert.True(normal.Looping);
        Assert.Equal(64000, LoopLength(normal));
    }

    [Fact]
    public void Flac_IsDecoded()
    {
        var normal = ReadBrstm(MusicConverter.Convert(Flac(44100, seconds: 2), ".flac", new ConvertOptions(false)).Normal);

        Assert.Equal((MusicConverter.SampleRate, 2), (normal.SampleRate, normal.ChannelCount));
        Assert.InRange(Frequency(normal), 437, 443);
    }

    [Fact]
    public void Volume_MatchesTheTarget_AsFarAsThePeaksAllow()
    {
        var quiet = Wav(32000, channels: 2, seconds: 10, amplitude: 0.1);
        Assert.InRange(MusicConverter.MeasureLoudness(quiet, ".wav"), -21, -20); // a sine at -20 dBFS, lowered a bit by K-weighting

        var matched = ReadBrstm(MusicConverter.Convert(quiet, ".wav", new ConvertOptions(false, TargetLoudness: -16)).Normal);
        Assert.InRange(LoudnessOf(matched), -16.5, -15.5);

        // A sine at 0 LUFS would have to peak at full scale; the volume stops short of that.
        var limited = ReadBrstm(MusicConverter.Convert(quiet, ".wav", new ConvertOptions(false, TargetLoudness: 0)).Normal);
        Assert.True(limited.Channels[0].Max(sample => Math.Abs((int)sample)) < short.MaxValue);
        Assert.True(LoudnessOf(limited) < -1);
    }

    [Fact]
    public void Tags_GiveTheTitleAndArtist()
    {
        var music = MusicConverter.Convert(
            WithInfo(Wav(32000, channels: 2, seconds: 1), title: "Test Song", artist: "Wii Bands"),
            ".wav",
            new ConvertOptions(false)
        );

        Assert.Equal(("Test Song", "Wii Bands"), (music.Title, music.Artist));
    }

    [Theory]
    [InlineData(".m4a")]
    [InlineData(".wma")]
    public void OtherFileTypes_AreNotSupported(string extension)
    {
        Assert.False(MusicConverter.IsSupported(extension));
        Assert.Throws<NotSupportedException>(() => MusicConverter.Convert(Wav(32000, 2, 1), extension, new ConvertOptions(false)));
    }

    [Fact]
    public void SongsShorterThanASecond_AreRejected() =>
        Assert.Throws<InvalidDataException>(() => MusicConverter.Convert(Wav(32000, 2, 0.5), ".wav", new ConvertOptions(false)));

    [Fact]
    public void SongsLongerThanTheLimit_AreRejected() =>
        Assert.Throws<SongTooLongException>(
            () => MusicConverter.Convert(Wav(32000, 1, MusicConverter.MaxSeconds + 1), ".wav", new ConvertOptions(false))
        );

    /// <summary>A 440 Hz tone.</summary>
    internal static byte[] Wav(int sampleRate, int channels, double seconds, double amplitude = 0.5) =>
        Wav(sampleRate, channels, Tone((int)(sampleRate * seconds), sampleRate, amplitude));

    private static byte[] Wav(int sampleRate, int channels, short[] mono)
    {
        var samples = mono.SelectMany(sample => Enumerable.Repeat(sample, channels)).ToArray();
        using var stream = new MemoryStream();
        using (var writer = new WaveFileWriter(stream, new WaveFormat(sampleRate, 16, channels)))
            writer.WriteSamples(samples, 0, samples.Length);

        return stream.ToArray();
    }

    private static short[] Tone(int length, int sampleRate, double amplitude) =>
        Enumerable
            .Range(0, length)
            .Select(i => (short)(amplitude * short.MaxValue * Math.Sin(2 * Math.PI * 440 * i / sampleRate)))
            .ToArray();

    /// <summary>Intro, verse, chorus, verse, chorus, outro: random notes, but each verse and chorus is played identically twice.</summary>
    private static byte[] StructuredSong()
    {
        const int rate = 32000;
        short[] Section(int seed, int beats)
        {
            var random = new Random(seed);
            var notes = new List<short>();
            for (var beat = 0; beat < beats; beat++)
            {
                var frequency = 220 * Math.Pow(2, random.Next(12) / 12.0);
                var volume = 0.15 + 0.15 * random.NextDouble();
                for (var i = 0; i < rate / 2; i++)
                {
                    var t = i / (double)rate;
                    var envelope = Math.Min(1, t / 0.01) * Math.Exp(-3 * t);
                    notes.Add(
                        (short)(
                            short.MaxValue
                            * volume
                            * envelope
                            * (Math.Sin(2 * Math.PI * frequency * t) + 0.5 * Math.Sin(4 * Math.PI * frequency * t))
                        )
                    );
                }
            }

            return notes.ToArray();
        }

        var verse = Section(2, 30);
        var chorus = Section(3, 32);
        return Wav(rate, 2, [.. Section(1, 16), .. verse, .. chorus, .. verse, .. chorus, .. Section(4, 10)]);
    }

    private static byte[] Flac(int sampleRate, double seconds)
    {
        var mono = Tone((int)(sampleRate * seconds), sampleRate, 0.5);
        var pcm = new AudioPCMConfig(16, 2, sampleRate);
        var bytes = mono.SelectMany(sample => BitConverter.GetBytes(sample).Concat(BitConverter.GetBytes(sample))).ToArray();
        using var stream = new MemoryStream();
        var writer = new FlakeWriter(null, stream, new FlakeWriterSettings { PCM = pcm, EncoderMode = "5" });
        writer.Write(new AudioBuffer(pcm, bytes, mono.Length));
        writer.Close();
        return stream.ToArray();
    }

    // Adds a RIFF INFO chunk, where WAV files keep their tags.
    private static byte[] WithInfo(byte[] wav, string title, string artist)
    {
        byte[] info = [.. "INFO"u8, .. Item("INAM", title), .. Item("IART", artist)];
        byte[] file = [.. wav, .. "LIST"u8, .. BitConverter.GetBytes(info.Length), .. info];
        BitConverter.GetBytes(file.Length - 8).CopyTo(file, 4);
        return file;

        static byte[] Item(string id, string text) =>
            [.. Encoding.ASCII.GetBytes(id), .. BitConverter.GetBytes(text.Length + 1), .. Encoding.ASCII.GetBytes(text), 0];
    }

    internal static Pcm16Format ReadBrstm(byte[] file) => new BrstmReader().Read(file).GetFormat<Pcm16Format>();

    private static int LoopLength(Pcm16Format pcm) => pcm.LoopEnd - pcm.LoopStart;

    // Zero crossings of the left channel, away from the crossfaded edges.
    private static double Frequency(Pcm16Format pcm)
    {
        var samples = pcm.Channels[0];
        int from = pcm.SampleRate / 10,
            to = pcm.LoopEnd - pcm.SampleRate / 2,
            crossings = 0;
        for (var i = from + 1; i < to; i++)
        {
            if (samples[i - 1] < 0 && samples[i] >= 0)
                crossings++;
        }

        return crossings / ((to - from) / (double)pcm.SampleRate);
    }

    internal static double LoudnessOf(Pcm16Format pcm) =>
        WheelWizard.CustomMusic.Loudness.Measure(
            pcm.Channels.Select(channel => channel.Select(sample => sample / 32768f).ToArray()).ToArray(),
            pcm.SampleRate,
            pcm.LoopEnd
        );
}
