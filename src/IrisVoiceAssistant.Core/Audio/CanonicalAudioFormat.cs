namespace IrisVoiceAssistant.Core.Audio;

/// <summary>
/// Canonical internal audio format and sample-based clock (ADR-007):
/// PCM float32, 16 kHz, mono, 20 ms frames. Platform adapters convert to this format once, at the edge.
/// Stream positions are expressed in samples since the start of the session.
/// </summary>
public static class CanonicalAudioFormat
{
    public const int SampleRate = 16_000;
    public const int Channels = 1;
    public const int BitsPerSample = 32;
    public const int FrameDurationMilliseconds = 20;
    public const int SamplesPerFrame = SampleRate * FrameDurationMilliseconds / 1000;
    public const int BytesPerSample = BitsPerSample / 8;
    public const int BytesPerFrame = SamplesPerFrame * Channels * BytesPerSample;

    /// <summary>Converts a stream position (in samples) to elapsed time, exact to the tick.</summary>
    public static TimeSpan ToTimeSpan(long samples)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(samples);
        // 16 000 samples/s and 10 000 000 ticks/s: one sample = 625 ticks, so this is exact.
        return TimeSpan.FromTicks(checked(samples * (TimeSpan.TicksPerSecond / SampleRate)));
    }

    /// <summary>
    /// Converts elapsed time to a stream position, rounding down to a whole sample.
    /// </summary>
    public static long ToSamples(TimeSpan elapsed)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(elapsed, TimeSpan.Zero);
        return elapsed.Ticks / (TimeSpan.TicksPerSecond / SampleRate);
    }
}
