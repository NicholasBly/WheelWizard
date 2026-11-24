namespace WheelWizard.CustomMusic;

/// <summary>Integrated loudness in LUFS (ITU-R BS.1770 / EBU R128): K-weighted, in gated 400 ms blocks.</summary>
public static class Loudness
{
    /// <returns>The loudness of the first <paramref name="length"/> samples, or negative infinity for silence.</returns>
    public static double Measure(float[][] channels, int sampleRate, int length)
    {
        var block = sampleRate * 4 / 10;
        var step = block / 4;
        if (length < block)
            return double.NegativeInfinity;

        // Per channel, the K-weighted energy of every 100 ms step; a block is four steps.
        var steps = new double[length / step];
        foreach (var channel in channels)
        {
            var filter = new KWeighting(sampleRate);
            for (var i = 0; i < steps.Length * step; i++)
            {
                var y = filter.Process(channel[i]);
                steps[i / step] += y * y;
            }
        }

        var blocks = Enumerable
            .Range(0, steps.Length - 3)
            .Select(i => (steps[i] + steps[i + 1] + steps[i + 2] + steps[i + 3]) / block)
            .ToArray();
        var absolute = blocks.Where(power => Lufs(power) > -70).ToArray();
        if (absolute.Length == 0)
            return double.NegativeInfinity;

        var relative = Lufs(absolute.Average()) - 10;
        return Lufs(absolute.Where(power => Lufs(power) > relative).Average());
    }

    private static double Lufs(double power) => -0.691 + 10 * Math.Log10(power);

    /// <summary>The two-stage K-weighting filter (a high shelf, then a high pass), with coefficients for any sample rate.</summary>
    private sealed class KWeighting
    {
        private readonly Biquad _shelf;
        private readonly Biquad _highPass;

        public KWeighting(int sampleRate)
        {
            var k = Math.Tan(Math.PI * 1681.974450955533 / sampleRate);
            var q = 0.7071752369554196;
            var vh = Math.Pow(10, 3.999843853973347 / 20);
            var vb = Math.Pow(vh, 0.4996667741545416);
            var a0 = 1 + k / q + k * k;
            _shelf = new Biquad(
                (vh + vb * k / q + k * k) / a0,
                2 * (k * k - vh) / a0,
                (vh - vb * k / q + k * k) / a0,
                2 * (k * k - 1) / a0,
                (1 - k / q + k * k) / a0
            );

            k = Math.Tan(Math.PI * 38.13547087602444 / sampleRate);
            q = 0.5003270373238773;
            a0 = 1 + k / q + k * k;
            _highPass = new Biquad(1, -2, 1, 2 * (k * k - 1) / a0, (1 - k / q + k * k) / a0);
        }

        public double Process(double x) => _highPass.Process(_shelf.Process(x));
    }

    private sealed class Biquad(double b0, double b1, double b2, double a1, double a2)
    {
        private double _x1,
            _x2,
            _y1,
            _y2;

        public double Process(double x)
        {
            var y = b0 * x + b1 * _x1 + b2 * _x2 - a1 * _y1 - a2 * _y2;
            (_x2, _x1, _y2, _y1) = (_x1, x, _y1, y);
            return y;
        }
    }
}
