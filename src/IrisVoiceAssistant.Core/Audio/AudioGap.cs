namespace IrisVoiceAssistant.Core.Audio;

/// <summary>Why a stretch of the timeline has no captured audio (ADR-009).</summary>
public enum AudioGapCause
{
    /// <summary>Frames were captured but dropped because the bounded queue was full. Counts as loss.</summary>
    Overflow = 1,

    /// <summary>The device reported a glitch (data discontinuity); the missing span was never captured.</summary>
    DeviceDiscontinuity = 2,

    /// <summary>The device was removed, disabled or invalidated until capture recovered.</summary>
    DeviceLost = 3,

    /// <summary>The user paused capture. Not a loss, but still shown on the timeline.</summary>
    Paused = 4,

    /// <summary>
    /// Captured audio that could not be re-sent to the speech worker after it restarted, because it had already
    /// left the replay buffer (ADR-014 decision 5). Counts as loss.
    /// </summary>
    WorkerRecovery = 5,
}

/// <summary>
/// A visible hole in one stream's timeline (ADR-009). Positions are in canonical samples since the session started.
/// Contiguous gaps with the same cause are merged. Overflow drops close together (frames dropped and kept in
/// alternation under sustained load) form one episode: <see cref="LengthSamples"/> is the affected span and
/// <see cref="MissingSamples"/> the exact number of samples lost in it.
/// </summary>
public sealed record AudioGap(AudioStreamKind Stream, long StartPosition, long LengthSamples, AudioGapCause Cause)
{
    /// <summary>Samples actually missing inside the span; equals <see cref="LengthSamples"/> except for overflow episodes.</summary>
    public long MissingSamples { get; init; } = LengthSamples;

    public TimeSpan Missing => CanonicalAudioFormat.ToTimeSpan(MissingSamples);

    public TimeSpan Start => CanonicalAudioFormat.ToTimeSpan(StartPosition);

    public TimeSpan Duration => CanonicalAudioFormat.ToTimeSpan(LengthSamples);

    /// <summary>True when captured audio was discarded (as opposed to never captured).</summary>
    public bool IsLoss => Cause is AudioGapCause.Overflow or AudioGapCause.WorkerRecovery;
}
