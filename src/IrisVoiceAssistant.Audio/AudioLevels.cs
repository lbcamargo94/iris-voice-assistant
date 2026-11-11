namespace IrisVoiceAssistant.Audio;

/// <summary>Signal levels for meters and diagnostics.</summary>
public static class AudioLevels
{
    /// <summary>Peak in dBFS; <see cref="double.NegativeInfinity"/> for digital silence.</summary>
    public static double PeakDbfs(ReadOnlySpan<float> samples)
    {
        float peak = 0;
        foreach (float s in samples)
        {
            peak = Math.Max(peak, Math.Abs(s));
        }

        return ToDb(peak);
    }

    public static double RmsDbfs(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
        {
            return double.NegativeInfinity;
        }

        double sum = 0;
        foreach (float s in samples)
        {
            sum += s * (double)s;
        }

        return ToDb(Math.Sqrt(sum / samples.Length));
    }

    /// <summary>Fraction of samples at or above full scale (|x| ≥ 0.999).</summary>
    public static double ClippedFraction(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
        {
            return 0;
        }

        int clipped = 0;
        foreach (float s in samples)
        {
            if (Math.Abs(s) >= 0.999f)
            {
                clipped++;
            }
        }

        return (double)clipped / samples.Length;
    }

    private static double ToDb(double amplitude) => amplitude <= 0 ? double.NegativeInfinity : 20 * Math.Log10(amplitude);
}
