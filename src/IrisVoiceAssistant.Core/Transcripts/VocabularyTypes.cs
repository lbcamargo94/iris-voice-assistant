namespace IrisVoiceAssistant.Core.Transcripts;

/// <summary>Learning state of a vocabulary term (ADR-015).</summary>
public enum VocabularyState
{
    /// <summary>Seen, but not often enough to be used yet.</summary>
    Candidate = 1,

    /// <summary>Used as a speech hint (and, for names/acronyms, kept in English in translations).</summary>
    Active = 2,

    /// <summary>The user removed it; it is not learned again until the vocabulary is cleared.</summary>
    Forgotten = 3,
}

/// <summary>A term the speech worker flagged in a final sentence: rare word, name or acronym.</summary>
public sealed record TermObservation(string Text, string Kind);

/// <summary>
/// A term learned automatically from what the user watched. It is an inference, not a confirmed fact
/// (AGENTS.md §2): it only hints the recognizer and never changes saved transcripts.
/// </summary>
public sealed record VocabularyTerm(
    string Term,
    string Kind,
    int Occurrences,
    int Sessions,
    DateTimeOffset FirstSeenUtc,
    DateTimeOffset LastSeenUtc,
    VocabularyState State)
{
    public bool KeepInEnglish => Kind is "name" or "acronym";
}
