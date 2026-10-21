using System.Buffers.Binary;
using System.Text;
using WheelWizard.CustomMusic.Audio;
using WheelWizard.CustomMusic.Audio.Codecs;
using WheelWizard.CustomMusic.Audio.Dsp;
using WheelWizard.CustomMusic.Audio.Formats;
using WheelWizard.CustomMusic.Audio.Tags;

namespace WheelWizard.Test.Features.CustomMusic;

public class CustomMusicAudioTests
{
    private const int Rate = 32000;

    [Fact]
    public void DspAdpcm_RoundTrip_KeepsTheSignal()
    {
        var pcm = ToPcm16(Tone(Rate * 2, Rate, [(440, 0.3), (1320, 0.1)]));

        var encoded = DspAdpcm.Encode(pcm);

        Assert.Equal(DspAdpcm.SampleCountToByteCount(pcm.Length), encoded.Adpcm.Length);
        Assert.Equal(encoded.Decoded, DspAdpcm.Decode(encoded.Adpcm, encoded.Coefficients, pcm.Length));
        Assert.True(Snr(pcm, encoded.Decoded) > 25, "4-bit ADPCM should keep tones well above 25 dB SNR");
    }

    [Fact]
    public void DspAdpcm_SilenceEncodes()
    {
        var encoded = DspAdpcm.Encode(new short[10_000]);

        Assert.All(encoded.Decoded, sample => Assert.Equal(0, sample));
    }

    [Theory]
    [InlineData(14, 8)]
    [InlineData(15, 10)]
    [InlineData(28, 16)]
    [InlineData(14336, 8192)]
    public void DspAdpcm_SampleCountToByteCount(int samples, int bytes) => Assert.Equal(bytes, DspAdpcm.SampleCountToByteCount(samples));

    [Fact]
    public void Brstm_StoresLoopAndStreamInfo_AndDecodesToTheEncodedAudio()
    {
        var length = Brstm.SamplesPerBlock * 3 + 1234; // several blocks plus a short, padded last block
        var left = ToPcm16(Tone(length, Rate, [(330, 0.25)]));
        var right = ToPcm16(Tone(length, Rate, [(495, 0.25)]));
        var loopStart = Brstm.SamplesPerBlock * 2;

        var file = BrstmWriter.Write(new BrstmStream { Channels = [left, right], SampleRate = Rate, Looping = true, LoopStart = loopStart });
        var info = BrstmReader.ReadInfo(file);
        var decoded = BrstmReader.Decode(file);

        Assert.Equal("RSTM", Encoding.ASCII.GetString(file, 0, 4));
        Assert.Equal(file.Length, BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(8)));
        Assert.True(info.Looping);
        Assert.Equal(loopStart, info.LoopStart);
        Assert.Equal(length, info.SampleCount);
        Assert.Equal(2, info.ChannelCount);
        Assert.Equal(1, info.TrackCount);
        Assert.Equal(Rate, info.SampleRate);
        Assert.Equal(new LoopRegion(loopStart, length), decoded.Loop);
        var expectedRight = DspAdpcm.Encode(right).Decoded;
        for (var i = 0; i < length; i += 97)
            Assert.Equal(expectedRight[i], (short)Math.Round(decoded.Channels[1][i] * 32768));
    }

    [Fact]
    public void Brstm_PulsarVolumeByteIsWrittenAndRead()
    {
        var file = BrstmWriter.Write(new BrstmStream { Channels = [new short[5000]], PulsarVolume = 0x40 });

        Assert.Equal(0x40, file[Brstm.PulsarVolumeOffset]);
        Assert.InRange(BrstmReader.ReadInfo(file).PulsarGain, 0.50, 0.51);
    }

    [Fact]
    public void LoudnessMeter_MeasuresAMinus20DbfsStereoSineAsMinus20Lufs()
    {
        // BS.1770: a 1 kHz sine at -20 dBFS (peak) in both channels reads -20 LUFS.
        var tone = Tone(48000 * 5, 48000, [(1000, 0.1)]);

        var result = LoudnessMeter.Measure([tone, tone], 48000);

        Assert.InRange(result.IntegratedLufs, -20.1, -19.9);
        Assert.InRange(result.SamplePeak, 0.099, 0.101);
    }

    [Fact]
    public void LoudnessMeter_ReportsSilence() => Assert.True(LoudnessMeter.Measure([new float[48000 * 2]], 48000).IsSilent);

    [Fact]
    public void Resampler_KeepsTonesAndRemovesWhatCannotBeRepresented()
    {
        var kept = Resampler.Resample(Tone(44100 * 2, 44100, [(5000, 0.5)]), 44100, Rate);
        var aliased = Resampler.Resample(Tone(44100 * 2, 44100, [(19000, 0.5)]), 44100, Rate);

        Assert.InRange(kept.Length, Rate * 2 - 2, Rate * 2);
        Assert.InRange(Rms(kept, 2000), 0.35, 0.36); // 0.5 / sqrt(2)
        Assert.True(Rms(aliased, 2000) < 0.001, "content above 16 kHz must not fold back");
    }

    [Fact]
    public void Converter_MatchesTheTargetLoudness_AndLoopsSeamlessly()
    {
        var song = Song(44100, seconds: 20);

        var result = MusicConverter.Convert(song, new ConversionOptions { TargetLufs = -18, CreateFinalLap = true });
        var info = BrstmReader.ReadInfo(result.Normal);
        var audio = BrstmReader.Decode(result.Normal);

        Assert.InRange(LoudnessMeter.Measure(audio).IntegratedLufs, -18.6, -17.4);
        Assert.True(info.Looping);
        Assert.Equal(0, info.LoopStart % Brstm.SamplesPerBlock);
        Assert.Equal(Rate, info.SampleRate);
        Assert.Equal(2, info.ChannelCount);
        // Jumping from the last sample back to the loop start must be as smooth as the music itself.
        foreach (var channel in audio.Channels)
            Assert.True(Math.Abs(channel[info.LoopStart] - channel[info.SampleCount - 1]) < 0.05);
    }

    [Theory]
    [InlineData(FinalLapStyle.Faster, MusicLoudness.FinalLapPitchSpeed)]
    [InlineData(FinalLapStyle.FasterKeepPitch, MusicLoudness.FinalLapTempoSpeed)]
    [InlineData(FinalLapStyle.Unchanged, 1.0)]
    public void Converter_FinalLapVersionPlaysFaster(FinalLapStyle style, double speed)
    {
        var result = MusicConverter.Convert(Song(32000, seconds: 12), new ConversionOptions { CreateFinalLap = true, FinalLapStyle = style });
        var normal = BrstmReader.ReadInfo(result.Normal);
        var final = BrstmReader.ReadInfo(result.FinalLap!);

        var ratio = (normal.SampleCount - normal.LoopStart) / (double)(final.SampleCount - final.LoopStart);
        Assert.InRange(ratio, speed - 0.01, speed + 0.01);
        Assert.True(final.Looping);
    }

    [Fact]
    public void Converter_JinglesPlayOnce()
    {
        var result = MusicConverter.Convert(Song(48000, seconds: 4), new ConversionOptions { Loop = false });

        Assert.False(BrstmReader.ReadInfo(result.Normal).Looping);
        Assert.Null(result.FinalLap);
    }

    [Fact]
    public void Converter_KeepsLoopPointsFromTheFile()
    {
        var song = Song(32000, seconds: 10);
        song.Loop = new LoopRegion(32000 * 3, 32000 * 9);

        var result = MusicConverter.Convert(song, new ConversionOptions());
        var info = BrstmReader.ReadInfo(result.Normal);

        Assert.True(result.Report.UsedSourceLoopPoints);
        // The loop start moves forward onto a block boundary and the end moves with it: same loop length.
        Assert.Equal(32000 * 6, info.SampleCount - info.LoopStart);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0x40)]
    [InlineData(0xFF)]
    public void Converter_WritesTheRequestedVolumeByteToBothFiles(int volume)
    {
        var result = MusicConverter.Convert(Song(32000, seconds: 8), new ConversionOptions { CreateFinalLap = true, PulsarVolume = (byte)volume });

        var expected = (byte)Math.Min(volume, 0x7F); // Retro Rewind ignores anything above 0x7F
        Assert.Equal(expected, BrstmReader.ReadInfo(result.Normal).PulsarVolume);
        Assert.Equal(expected, BrstmReader.ReadInfo(result.FinalLap!).PulsarVolume);
    }

    [Fact]
    public void Converter_VolumeOffsetMakesTheSongLouder()
    {
        var song = Song(32000, seconds: 10);
        var plain = MusicConverter.Convert(song, new ConversionOptions { TargetLufs = -20 });
        var louder = MusicConverter.Convert(song, new ConversionOptions { TargetLufs = -20, VolumeOffsetDb = 4 });

        var difference = LoudnessMeter.Measure(BrstmReader.Decode(louder.Normal)).IntegratedLufs - LoudnessMeter.Measure(BrstmReader.Decode(plain.Normal)).IntegratedLufs;
        Assert.InRange(difference, 3.6, 4.4);
    }

    [Fact]
    public void Converter_RejectsSilence() =>
        Assert.Throws<InvalidDataException>(() => MusicConverter.Convert(new AudioBuffer([new float[96000], new float[96000]], 48000), new ConversionOptions()));

    [Fact]
    public void WavReader_ReadsSamplesAndSmplLoop()
    {
        var samples = new short[] { 0, 1000, -1000, short.MaxValue, short.MinValue, 42 };
        var wav = BuildWav(samples, loopStart: 1, loopEndInclusive: 4);

        Assert.True(WavReader.IsWav(wav));
        var audio = WavReader.Read(wav);

        Assert.Equal(44100, audio.SampleRate);
        Assert.Equal(samples.Length, audio.SampleCount);
        Assert.Equal(1000 / 32768f, audio.Channels[0][1], 5);
        Assert.Equal(new LoopRegion(1, 5), audio.Loop);
    }

    [Fact]
    public void Id3v2Reader_ReadsTitleArtistAndFrontCover()
    {
        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4, 5, 6 };
        var tag = BuildId3([TextFrame("TIT2", "Rainbow Road"), TextFrame("TPE1", "Wheel Wizard"), PictureFrame(jpeg)]);

        var metadata = Id3v2Reader.Read(tag);

        Assert.Equal(tag.Length, Id3v2Reader.GetTagSize(tag));
        Assert.Equal("Rainbow Road", metadata.Title);
        Assert.Equal("Wheel Wizard", metadata.Artist);
        Assert.Equal("image/jpeg", metadata.Cover?.MimeType);
        Assert.Equal(jpeg, metadata.Cover?.Data);
    }

    [Fact]
    public void VorbisComments_ReadTagsAndGameLoopPoints()
    {
        var metadata = VorbisComments.FromComments(["TITLE=Coconut Mall", "artist=Someone", "LOOPSTART=44100", "LOOPLENGTH=88200"]);

        Assert.Equal("Coconut Mall", metadata.Title);
        Assert.Equal("Someone", metadata.Artist);
        Assert.Equal(new LoopRegion(44100, 132300), metadata.ResolveLoopTags(500_000));
    }

    [Fact]
    public void CoverArt_IsRecognisedByItsSignature()
    {
        Assert.Equal("image/png", CoverArt.FromBytes([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0])?.MimeType);
        Assert.Null(CoverArt.FromBytes([1, 2, 3, 4, 5, 6, 7, 8, 9], "application/octet-stream"));
    }

    #region Helpers

    private static float[] Tone(int length, int rate, (double Frequency, double Amplitude)[] partials)
    {
        var output = new float[length];
        for (var i = 0; i < length; i++)
        {
            double value = 0;
            foreach (var (frequency, amplitude) in partials)
                value += amplitude * Math.Sin(2 * Math.PI * frequency * i / rate);
            output[i] = (float)value;
        }

        return output;
    }

    /// <summary>A few seconds of chords, a bass pulse and noise bursts: enough structure to look like music.</summary>
    private static AudioBuffer Song(int rate, int seconds)
    {
        var random = new Random(1234);
        var length = rate * seconds;
        var left = new float[length];
        var right = new float[length];
        double[] roots = [220, 174.61, 261.63, 196];
        for (var i = 0; i < length; i++)
        {
            var t = i / (double)rate;
            var root = roots[(int)(t / 2) % roots.Length];
            var envelope = Math.Exp(-(t % 2) * 0.8);
            var chord = 0.12 * envelope * (Math.Sin(2 * Math.PI * root * t) + Math.Sin(2 * Math.PI * root * 1.26 * t) + Math.Sin(2 * Math.PI * root * 1.5 * t));
            var bass = t % 0.5 < 0.2 ? 0.25 * Math.Sin(2 * Math.PI * 55 * t) : 0;
            var noise = t % 0.25 < 0.03 ? (random.NextDouble() - 0.5) * 0.1 : 0;
            left[i] = (float)(chord + bass + noise);
            right[i] = (float)(chord * 0.9 + bass + noise);
        }

        return new AudioBuffer([left, right], rate);
    }

    private static short[] ToPcm16(float[] samples) => samples.Select(s => (short)Math.Clamp(Math.Round(s * 32767), short.MinValue, short.MaxValue)).ToArray();

    private static double Snr(short[] reference, short[] test)
    {
        double signal = 0,
            noise = 0;
        for (var i = 0; i < reference.Length; i++)
        {
            signal += (double)reference[i] * reference[i];
            noise += Math.Pow(reference[i] - test[i], 2);
        }

        return 10 * Math.Log10(signal / Math.Max(noise, 1));
    }

    private static double Rms(float[] samples, int skip) => Math.Sqrt(samples.Skip(skip).Take(samples.Length - 2 * skip).Average(s => (double)s * s));

    private static byte[] BuildWav(short[] samples, int loopStart, int loopEndInclusive)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(0);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(44100);
        writer.Write(44100 * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(samples.Length * 2);
        foreach (var sample in samples)
            writer.Write(sample);
        writer.Write("smpl"u8);
        writer.Write(36 + 24);
        writer.Write(new byte[28]);
        writer.Write(1); // loop count
        writer.Write(0); // sampler data
        writer.Write(0); // cue id
        writer.Write(0); // loop type
        writer.Write(loopStart);
        writer.Write(loopEndInclusive);
        writer.Write(0);
        writer.Write(0);
        writer.Flush();
        var bytes = stream.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), bytes.Length - 8);
        return bytes;
    }

    private static byte[] TextFrame(string id, string text) => Frame(id, [3, .. Encoding.UTF8.GetBytes(text)]);

    private static byte[] PictureFrame(byte[] image) => Frame("APIC", [0, .. "image/jpeg"u8, 0, 3, 0, .. image]);

    private static byte[] Frame(string id, byte[] body)
    {
        var frame = new byte[10 + body.Length];
        Encoding.ASCII.GetBytes(id, frame.AsSpan(0, 4));
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(4), body.Length); // ID3v2.3 sizes are plain big endian
        body.CopyTo(frame, 10);
        return frame;
    }

    private static byte[] BuildId3(byte[][] frames)
    {
        var body = frames.SelectMany(frame => frame).ToArray();
        var size = body.Length;
        byte[] header = [(byte)'I', (byte)'D', (byte)'3', 3, 0, 0, (byte)((size >> 21) & 0x7F), (byte)((size >> 14) & 0x7F), (byte)((size >> 7) & 0x7F), (byte)(size & 0x7F)];
        return [.. header, .. body];
    }

    #endregion
}
