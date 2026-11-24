using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace WheelWizard.CustomMusic;

public interface IMusicPlayer
{
    /// <summary>Plays a BRSTM from the start, replacing whatever was playing.</summary>
    void Play(byte[] brstm);

    void Stop();

    /// <summary>Raised, on any thread, when the music finishes playing on its own.</summary>
    event EventHandler? Finished;
}

/// <summary>Plays music through NAudio's output for each system: WinMM on Windows, ALSA on Linux and Core Audio on macOS.</summary>
public sealed class MusicPlayer : IMusicPlayer
{
    private IWavePlayer? _output;

    public event EventHandler? Finished;

    public void Play(byte[] brstm)
    {
        Stop();
        var audio = new SampleToWaveProvider16(MusicConverter.Decode(brstm, ".brstm"));
        var output = CreateOutput();
        try
        {
            output.Init(audio);
            output.PlaybackStopped += (_, _) =>
            {
                if (output == _output)
                    Finished?.Invoke(this, EventArgs.Empty);
            };
            _output = output;
            output.Play();
        }
        catch
        {
            _output = null;
            output.Dispose();
            throw;
        }
    }

    public void Stop()
    {
        var output = _output;
        _output = null;
        output?.Stop();
        output?.Dispose();
    }

    private static IWavePlayer CreateOutput()
    {
        if (OperatingSystem.IsWindows())
            return new WaveOut();
        if (OperatingSystem.IsMacOS())
            return new CoreAudioPlayer();
        if (OperatingSystem.IsLinux())
            return new NAudio.Wave.Alsa.AlsaOut();
        throw new PlatformNotSupportedException();
    }
}
