namespace IrisVoiceAssistant.Core.Audio;

/// <summary>
/// Independent audio streams handled by the application (AUDIO-ENGINE.md).
/// </summary>
public enum AudioStreamKind
{
    /// <summary>Audio rendered by the meeting platform; shown to the user as "Participantes".</summary>
    SystemAudio = 1,

    /// <summary>The local user's microphone; shown to the user as "Você".</summary>
    Microphone = 2,

    /// <summary>Speech synthesized by Iris. Must never re-enter speech recognition (ADR-007).</summary>
    AssistantPlayback = 3,
}
