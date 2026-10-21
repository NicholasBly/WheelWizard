namespace WheelWizard.CustomMusic.Audio.Dsp;

/// <summary>
/// Offline, stereo-linked look-ahead peak limiter. It only acts on the few peaks that would clip after
/// loudness matching, so quiet songs can be raised to the target without distortion.
/// </summary>
public static class PeakLimiter
{
    /// <returns>The largest gain reduction applied, in dB (0 when nothing was limited).</returns>
    public static double Apply(AudioBuffer buffer, double ceilingDb = -1.0, double lookAheadSeconds = 0.005, double releaseSeconds = 0.12)
    {
        var length = buffer.SampleCount;
        if (length == 0)
            return 0;

        var ceiling = Math.Pow(10, ceilingDb / 20);
        var required = new double[length];
        var anyOver = false;
        for (var i = 0; i < length; i++)
        {
            double peak = 0;
            foreach (var channel in buffer.Channels)
                peak = Math.Max(peak, Math.Abs(channel[i]));
            required[i] = peak > ceiling ? ceiling / peak : 1.0;
            anyOver |= peak > ceiling;
        }

        if (!anyOver)
            return 0;

        var lookAhead = Math.Max(1, (int)(buffer.SampleRate * lookAheadSeconds));

        // 1) Minimum over a symmetric window of +/- lookAhead: gain is already down when the peak arrives.
        var minimum = SlidingMinimum(required, lookAhead);

        // 2) Average over +/- lookAhead/2: a smooth ramp that still never exceeds the required gain,
        //    because every averaged value already includes the peak's own requirement.
        var half = Math.Max(1, lookAhead / 2);
        var prefix = new double[length + 1];
        for (var i = 0; i < length; i++)
            prefix[i + 1] = prefix[i] + minimum[i];

        // 3) Slow release so the gain does not pump between peaks.
        var releaseAlpha = 1 - Math.Exp(-1.0 / (buffer.SampleRate * releaseSeconds));
        var gain = 1.0;
        var maxReduction = 1.0;
        var gains = new float[length];
        for (var i = 0; i < length; i++)
        {
            var from = Math.Max(0, i - half);
            var to = Math.Min(length - 1, i + half);
            var smooth = (prefix[to + 1] - prefix[from]) / (to - from + 1);
            gain = smooth < gain ? smooth : gain + (smooth - gain) * releaseAlpha;
            gains[i] = (float)gain;
            maxReduction = Math.Min(maxReduction, gain);
        }

        foreach (var channel in buffer.Channels)
        {
            for (var i = 0; i < length; i++)
                channel[i] *= gains[i];
        }

        return -20 * Math.Log10(maxReduction);
    }

    private static double[] SlidingMinimum(double[] values, int radius)
    {
        var length = values.Length;
        var result = new double[length];
        var deque = new int[length];
        int head = 0,
            tail = 0;
        var next = 0;

        for (var i = 0; i < length; i++)
        {
            // Push everything up to i + radius into the monotonic deque.
            var limit = Math.Min(length - 1, i + radius);
            while (next <= limit)
            {
                while (tail > head && values[deque[tail - 1]] >= values[next])
                    tail--;
                deque[tail++] = next++;
            }

            while (deque[head] < i - radius)
                head++;

            result[i] = values[deque[head]];
        }

        return result;
    }
}
