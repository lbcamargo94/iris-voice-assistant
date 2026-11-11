namespace IrisVoiceAssistant.Core.Audio;

public enum RawSampleType
{
    IeeeFloat = 1,
    Pcm16 = 2,
    Pcm24 = 3,
    Pcm32 = 4,
}

/// <summary>A device's native interleaved PCM format, before conversion at the adapter edge (ADR-007).</summary>
public sealed record RawAudioFormat(int SampleRate, int Channels, RawSampleType SampleType)
{
    public int BytesPerSample => SampleType switch
    {
        RawSampleType.Pcm16 => 2,
        RawSampleType.Pcm24 => 3,
        _ => 4,
    };

    public int BlockAlign => BytesPerSample * Channels;

    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(SampleRate, 8_000);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(SampleRate, 384_000);
        ArgumentOutOfRangeException.ThrowIfLessThan(Channels, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Channels, 32);
        if (!Enum.IsDefined(SampleType))
        {
            throw new ArgumentOutOfRangeException(nameof(SampleType), SampleType, "Unknown sample type.");
        }
    }
}

[Flags]
public enum RawBlockMarkers
{
    None = 0,

    /// <summary>The device marked the packet as silent; its data must be treated as zeros.</summary>
    Silent = 1,

    /// <summary>The device reported a glitch before this packet.</summary>
    Discontinuity = 2,
}

/// <summary>
/// Receives native audio from a platform capture source, on the source's capture thread. Implementations must
/// return quickly and must not block.
/// </summary>
public interface IRawAudioSink
{
    /// <param name="data">Interleaved samples in <see cref="IAudioCaptureSource.Format"/>.</param>
    /// <param name="frameCount">Number of sample frames (one sample per channel) in <paramref name="data"/>.</param>
    /// <param name="timestamp">Capture time of the first frame, in <see cref="System.Diagnostics.Stopwatch"/> ticks.</param>
    void OnAudio(ReadOnlySpan<byte> data, int frameCount, long timestamp, RawBlockMarkers flags);

    /// <summary>The source woke up without data; <paramref name="timestamp"/> is "now" in Stopwatch ticks.</summary>
    void OnIdle(long timestamp);
}

/// <summary>
/// A platform capture endpoint (microphone, endpoint loopback or process loopback) delivering native audio.
/// Throws <see cref="AudioDeviceLostException"/> from its capture loop when the device goes away.
/// </summary>
public interface IAudioCaptureSource : IDisposable
{
    AudioStreamKind Stream { get; }

    RawAudioFormat Format { get; }

    /// <summary>Human-readable description, e.g. "process loopback (excludes this app)".</summary>
    string Description { get; }

    /// <summary>Runs the capture loop on the calling thread until cancelled or the device is lost.</summary>
    void Run(IRawAudioSink sink, CancellationToken cancellationToken);
}

/// <summary>The capture device was removed, disabled or invalidated.</summary>
public sealed class AudioDeviceLostException : Exception
{
    public AudioDeviceLostException()
    {
    }

    public AudioDeviceLostException(string message)
        : base(message)
    {
    }

    public AudioDeviceLostException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
