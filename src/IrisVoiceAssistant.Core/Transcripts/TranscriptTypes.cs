using IrisVoiceAssistant.Core.Audio;

namespace IrisVoiceAssistant.Core.Transcripts;

/// <summary>An immutable final sentence of one stream (ADR-008, DATABASE.md). Positions are session samples.</summary>
public sealed record TranscriptSegment(
    Guid Id,
    Guid SessionId,
    AudioStreamKind Stream,
    long Sequence,
    long StartSample,
    long EndSample,
    string Text,
    bool Forced,
    DateTimeOffset CreatedUtc)
{
    public TimeSpan Start => CanonicalAudioFormat.ToTimeSpan(StartSample);

    public TimeSpan End => CanonicalAudioFormat.ToTimeSpan(EndSample);
}

public enum TranslationLayer
{
    Fast = 1,
    Refined = 2,
}

/// <summary>A translation of one segment; original and translation are stored separately (ADR-010).</summary>
public sealed record TranslationSegment(
    Guid Id,
    Guid SegmentId,
    string TargetLanguage,
    TranslationLayer Layer,
    string Text,
    string Model,
    DateTimeOffset CreatedUtc);

/// <summary>How a capture session was set up, for the record (DATABASE.md, MeetingSession).</summary>
public sealed record SessionInfo(
    DateTimeOffset StartedUtc,
    long StartedTimestamp,
    string? SystemSource,
    string? MicrophoneSource,
    string IsolationMode);
