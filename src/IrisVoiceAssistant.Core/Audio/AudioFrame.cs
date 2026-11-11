namespace IrisVoiceAssistant.Core.Audio;

/// <summary>Per-frame markers (AUDIO-ENGINE.md).</summary>
[Flags]
public enum AudioFrameMarkers
{
    None = 0,

    /// <summary>The frame was synthesized as silence because the device delivered no data (e.g. loopback while nothing plays).</summary>
    FilledSilence = 1,

    /// <summary>The device reported the packet as silent.</summary>
    DeviceSilent = 2,

    /// <summary>The device reported a glitch right before this frame (WASAPI data discontinuity).</summary>
    AfterDiscontinuity = 4,

    /// <summary>Iris was speaking while this frame was captured (ADR-007, layer C).</summary>
    DuringAssistantPlayback = 8,
}

/// <summary>
/// One 20 ms block in the canonical format (ADR-007): <see cref="CanonicalAudioFormat.SamplesPerFrame"/> float32
/// samples, 16 kHz mono. <see cref="StreamPosition"/> is the position of the first sample, in samples since the
/// session started; <see cref="Sequence"/> increases by one per frame and never repeats within a stream.
/// </summary>
public sealed record AudioFrame(
    AudioStreamKind Stream,
    long Sequence,
    long StreamPosition,
    ReadOnlyMemory<float> Samples,
    AudioFrameMarkers Markers)
{
    public TimeSpan Offset => CanonicalAudioFormat.ToTimeSpan(StreamPosition);
}
