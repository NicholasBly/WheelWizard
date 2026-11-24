using NWaves.Transforms;
using NWaves.Windows;

namespace WheelWizard.CustomMusic;

/// <summary>Finds where a song repeats itself, comparing the spectra of 64 ms frames, so it loops without an audible cut.</summary>
public static class LoopFinder
{
    private const int FrameSize = 2048;
    private const int BandCount = 24;
    private const double MinLoopSeconds = 12;
    private const double MatchSeconds = 6;

    // Average similarity (cosine of the band spectra) the music must reach on both sides of the jump.
    private const double MinSimilarity = 0.95;

    // Frames quieter than this (silence, the tail of a fade) carry no usable shape.
    private const float SilenceDb = -50;

    // Similarity lost per dB of level difference, so a fade-out or a quieter repeat does not pass for the same music.
    private const float LevelCostPerDb = 0.03f;

    /// <returns>The loop, or null when the song does not repeat cleanly enough.</returns>
    public static (int Start, int End)? Find(float[][] channels, int sampleRate, CancellationToken cancellationToken = default)
    {
        var mono = new float[channels[0].Length];
        foreach (var channel in channels)
        {
            for (var i = 0; i < mono.Length; i++)
                mono[i] += channel[i] / channels.Length;
        }

        var (frames, levels) = Features(mono);
        var half = Math.Max(2, (int)(MatchSeconds * sampleRate / FrameSize / 2));
        var window = 2 * half;
        var minLag = Math.Max(window, (int)(MinLoopSeconds * sampleRate / FrameSize));
        if (frames.Length < minLag + window)
            return null;

        var similarity = new float[frames.Length];
        var bestScore = double.NegativeInfinity;
        int bestEnd = 0,
            bestLag = 0;
        for (var lag = minLag; lag + half < frames.Length - half; lag++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var t = lag; t < frames.Length; t++)
                similarity[t] = Similarity(frames, levels, t, t - lag);

            // The jump needs matching music before it and after it: what follows the jump is what the earlier section has to sound like.
            double sum = 0;
            for (var t = lag; t < lag + window; t++)
                sum += similarity[t];
            for (var end = lag + half; end + half <= frames.Length; end++)
            {
                if (end > lag + half)
                    sum += similarity[end + half - 1] - similarity[end - half - 1];
                var mean = sum / window;
                var score = mean + 0.04 * lag / frames.Length + 0.02 * end / frames.Length;
                if (mean >= MinSimilarity && score > bestScore)
                    (bestScore, bestEnd, bestLag) = (score, end, lag);
            }
        }

        if (bestLag == 0)
            return null;

        var loopEnd = bestEnd * FrameSize;
        return Refine(mono, loopEnd, (bestEnd - bestLag) * FrameSize) is { } start && loopEnd - start >= MinLoopSeconds * sampleRate * 0.9
            ? (start, loopEnd)
            : null;
    }

    /// <summary>Per frame: the log energy in bands evenly spaced in log frequency, centred and normalised to unit length.</summary>
    private static (float[][] Frames, float[] Levels) Features(float[] mono)
    {
        var count = mono.Length / FrameSize;
        var frames = new float[count][];
        var levels = new float[count];

        // Band edges in FFT bins, from about 60 Hz to the top of the spectrum.
        var bins = FrameSize / 2;
        var edges = Enumerable
            .Range(0, BandCount + 1)
            .Select(band => Math.Clamp((int)Math.Round(4 * Math.Pow(bins / 4.0, band / (double)BandCount)), 1, bins))
            .ToArray();

        var fft = new RealFft(FrameSize);
        var hann = Window.OfType(WindowType.Hann, FrameSize);
        var frame = new float[FrameSize];
        var spectrum = new float[bins + 1];
        var bands = new double[BandCount];
        for (var f = 0; f < count; f++)
        {
            Array.Copy(mono, f * FrameSize, frame, 0, FrameSize);
            levels[f] = (float)(10 * Math.Log10(frame.Average(x => x * x) + 1e-12));
            frame.ApplyWindow(hann);
            fft.PowerSpectrum(frame, spectrum, normalize: false);

            for (var b = 0; b < BandCount; b++)
            {
                double sum = 0;
                for (var k = edges[b]; k < Math.Max(edges[b] + 1, edges[b + 1]); k++)
                    sum += spectrum[k];
                bands[b] = Math.Log10(sum + 1e-10);
            }

            var mean = bands.Average();
            var norm = Math.Sqrt(bands.Sum(band => (band - mean) * (band - mean)));
            frames[f] = bands.Select(band => norm > 1e-9 ? (float)((band - mean) / norm) : 0f).ToArray();
        }

        return (frames, levels);
    }

    private static float Similarity(float[][] frames, float[] levels, int a, int b)
    {
        if (levels[a] < SilenceDb || levels[b] < SilenceDb)
            return 0f;
        var dot = 0f;
        for (var i = 0; i < BandCount; i++)
            dot += frames[a][i] * frames[b][i];
        return dot - LevelCostPerDb * Math.Abs(levels[a] - levels[b]);
    }

    /// <summary>The loop start near <paramref name="roughStart"/> that best matches the waveform at <paramref name="end"/>.</summary>
    private static int? Refine(float[] mono, int end, int roughStart)
    {
        const int span = 1024;
        if (end - span < 0 || end + span > mono.Length)
            return null;

        double targetEnergy = 0;
        for (var i = -span; i < span; i++)
            targetEnergy += (double)mono[end + i] * mono[end + i];

        var best = double.NegativeInfinity;
        int? bestStart = null;
        for (var candidate = Math.Max(span, roughStart - FrameSize); candidate <= roughStart + FrameSize && candidate < end; candidate++)
        {
            double cross = 0,
                energy = 0;
            for (var i = -span; i < span; i++)
            {
                cross += mono[candidate + i] * mono[end + i];
                energy += mono[candidate + i] * mono[candidate + i];
            }

            var correlation = cross / Math.Sqrt(Math.Max(energy * targetEnergy, 1e-20));
            if (correlation > best)
                (best, bestStart) = (correlation, candidate);
        }

        return bestStart;
    }
}
