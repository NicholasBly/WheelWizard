using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WheelWizard.CustomMusic.Audio.Formats;

/// <summary>
/// Optional fallback for formats without a managed decoder (M4A/AAC, Opus, WMA, ALAC, ...).
/// It only works when the user has FFmpeg installed; Wheel Wizard never downloads it.
/// </summary>
public static class FfmpegReader
{
    private const int OutputSampleRate = 48000;

    public static string? FindExecutable()
    {
        var name = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ffmpeg.exe" : "ffmpeg";
        var candidates = new List<string> { Path.Combine(AppContext.BaseDirectory, name) };

        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        candidates.AddRange(
            pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Select(directory => Path.Combine(directory.Trim('"'), name))
        );

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            candidates.AddRange(["/opt/homebrew/bin/ffmpeg", "/usr/local/bin/ffmpeg"]);

        return candidates.FirstOrDefault(File.Exists);
    }

    public static bool IsAvailable => FindExecutable() != null;

    public static async Task<AudioBuffer> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        var executable =
            FindExecutable() ?? throw new NotSupportedException("This audio format needs FFmpeg. Install FFmpeg, or convert the song to MP3, FLAC, OGG or WAV.");

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (
            var argument in new[]
            {
                "-v",
                "error",
                "-nostdin",
                "-i",
                path,
                "-vn",
                "-ac",
                "2",
                "-ar",
                OutputSampleRate.ToString(),
                "-f",
                "f32le",
                "-acodec",
                "pcm_f32le",
                "pipe:1",
            }
        )
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("FFmpeg could not be started.");
        await using var registration = cancellationToken.Register(() =>
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }
        });

        using var output = new MemoryStream();
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.StandardOutput.BaseStream.CopyToAsync(output, cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var errors = await errorTask;

        if (process.ExitCode != 0 || output.Length < 8)
            throw new InvalidDataException($"FFmpeg could not decode this file. {errors.Trim()}".Trim());

        var bytes = output.GetBuffer().AsSpan(0, (int)output.Length);
        var frames = bytes.Length / 8;
        var left = new float[frames];
        var right = new float[frames];
        var samples = MemoryMarshal.Cast<byte, float>(bytes[..(frames * 8)]);
        for (var i = 0; i < frames; i++)
        {
            left[i] = samples[i * 2];
            right[i] = samples[i * 2 + 1];
        }

        return new AudioBuffer([left, right], OutputSampleRate);
    }
}
