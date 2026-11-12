using IrisVoiceAssistant.Core.Audio;

namespace IrisVoiceAssistant.Application.Live;

/// <summary>
/// Audio already sent to the speech worker but not yet acknowledged (ADR-014 decision 5): after a restart it is
/// re-sent from the last acknowledged position. Bounded by <see cref="Capacity"/>; frames that fall out before
/// being acknowledged can no longer be replayed, which the session records as a <see cref="AudioGapCause.WorkerRecovery"/> gap.
/// </summary>
internal sealed class ReplayBuffer
{
    private readonly LinkedList<AudioFrame> _frames = new();
    private readonly long _capacitySamples;
    private long _samples;

    public ReplayBuffer(TimeSpan capacity)
    {
        _capacitySamples = CanonicalAudioFormat.ToSamples(capacity);
        Capacity = capacity;
    }

    public TimeSpan Capacity { get; }

    /// <summary>Position of the oldest frame still held, or null when empty.</summary>
    public long? Earliest => _frames.First?.Value.StreamPosition;

    public int Count => _frames.Count;

    public void Add(AudioFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        _frames.AddLast(frame);
        _samples += frame.Samples.Length;
        while (_samples > _capacitySamples && _frames.First is { } first)
        {
            _samples -= first.Value.Samples.Length;
            _frames.RemoveFirst();
        }
    }

    /// <summary>Drops frames that end at or before <paramref name="position"/>: the worker no longer needs them.</summary>
    public void Acknowledge(long position)
    {
        while (_frames.First is { } first && first.Value.StreamPosition + first.Value.Samples.Length <= position)
        {
            _samples -= first.Value.Samples.Length;
            _frames.RemoveFirst();
        }
    }

    /// <summary>Frames still needed by a fresh worker that has seen nothing after <paramref name="position"/>.</summary>
    public IReadOnlyList<AudioFrame> FramesFrom(long position) =>
        [.. _frames.Where(f => f.StreamPosition + f.Samples.Length > position)];
}
