namespace IrisVoiceAssistant.Core.Audio;

/// <summary>
/// Sample accounting for one stream (ADR-009, item 4), in canonical samples.
/// Captured and filled samples enter the queue; each one is later processed, dropped or still queued.
/// </summary>
public sealed record AudioStreamCounters(
    AudioStreamKind Stream,
    long CapturedSamples,
    long FilledSilenceSamples,
    long ProcessedSamples,
    long DroppedSamples,
    long QueuedSamples,
    long NotCapturedSamples,
    int Gaps)
{
    /// <summary>Samples that entered the pipeline.</summary>
    public long ProducedSamples => CapturedSamples + FilledSilenceSamples;

    /// <summary>The reconciliation rule: every produced sample is processed, dropped or still queued.</summary>
    public bool Reconciles => ProducedSamples == ProcessedSamples + DroppedSamples + QueuedSamples;
}
