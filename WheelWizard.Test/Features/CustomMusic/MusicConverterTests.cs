using System.Text;
using NAudio.Wave;
using VGAudio.Containers.NintendoWare;
using VGAudio.Formats.Pcm16;
using WheelWizard.CustomMusic;

namespace WheelWizard.Test.Features.CustomMusic;

public class MusicConverterTests
{
    [Fact]
    public void Wav_BecomesALoopingStereoBrstm_WithAFasterFinalLap()
    {
        var music = MusicConverter.Convert(Wav(48000, channels: 1, seconds: 2), ".WAV", withFinalLap: true);

        var normal = ReadBrstm(music.Normal);
        Assert.Equal(MusicConverter.SampleRate, normal.SampleRate);
        Assert.Equal(2, normal.ChannelCount);
        Assert.InRange(normal.SampleCount, 63_900, 64_100);
        Assert.True(normal.Looping);
        Assert.Equal((0, normal.SampleCount), (normal.LoopStart, normal.LoopEnd));
        Assert.True(normal.Channels[1].Max() > 10_000); // the mono song plays on both sides

        var finalLap = ReadBrstm(music.FinalLap!);
        Assert.Equal(MusicConverter.SampleRate, finalLap.SampleRate);
        Assert.InRange(
            finalLap.SampleCount,
            normal.SampleCount / MusicConverter.FinalLapSpeed - 50,
            normal.SampleCount / MusicConverter.FinalLapSpeed + 50
        );
    }

    [Fact]
    public void Brstm_KeepsItsLoop()
    {
        var channel = Tone(32000 * 4, 32000);
        var song = new BrstmWriter().GetFile(new Pcm16Format([channel, channel], 32000).WithLoop(true, 32000, 96000));

        var music = MusicConverter.Convert(song, ".brstm", withFinalLap: false);

        var normal = ReadBrstm(music.Normal);
        Assert.Null(music.FinalLap);
        Assert.True(normal.Looping);
        Assert.Equal(64000, normal.LoopEnd - normal.LoopStart);
    }

    [Fact]
    public void Tags_GiveTheTitleAndArtist()
    {
        var music = MusicConverter.Convert(
            WithInfo(Wav(32000, channels: 2, seconds: 1), title: "Test Song", artist: "Wii Bands"),
            ".wav",
            withFinalLap: false
        );

        Assert.Equal(("Test Song", "Wii Bands"), (music.Title, music.Artist));
    }

    [Theory]
    [InlineData(".flac")]
    [InlineData(".m4a")]
    public void OtherFileTypes_AreNotSupported(string extension)
    {
        Assert.False(MusicConverter.IsSupported(extension));
        Assert.Throws<NotSupportedException>(() => MusicConverter.Convert(Wav(32000, 2, 1), extension, withFinalLap: false));
    }

    [Fact]
    public void SongsShorterThanASecond_AreRejected() =>
        Assert.Throws<InvalidDataException>(() => MusicConverter.Convert(Wav(32000, 2, 0.5), ".wav", withFinalLap: false));

    [Fact]
    public void SongsLongerThanTheLimit_AreRejected() =>
        Assert.Throws<SongTooLongException>(
            () => MusicConverter.Convert(Wav(32000, 1, MusicConverter.MaxSeconds + 1), ".wav", withFinalLap: false)
        );

    /// <summary>A 440 Hz tone.</summary>
    internal static byte[] Wav(int sampleRate, int channels, double seconds)
    {
        var samples = Tone((int)(sampleRate * seconds), sampleRate).SelectMany(sample => Enumerable.Repeat(sample, channels)).ToArray();
        using var stream = new MemoryStream();
        using (var writer = new WaveFileWriter(stream, new WaveFormat(sampleRate, 16, channels)))
            writer.WriteSamples(samples, 0, samples.Length);

        return stream.ToArray();
    }

    private static short[] Tone(int length, int sampleRate) =>
        Enumerable.Range(0, length).Select(i => (short)(16000 * Math.Sin(2 * Math.PI * 440 * i / sampleRate))).ToArray();

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

    private static Pcm16Format ReadBrstm(byte[] file) => new BrstmReader().Read(file).GetFormat<Pcm16Format>();
}
