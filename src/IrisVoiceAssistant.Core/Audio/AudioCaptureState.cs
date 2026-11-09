namespace IrisVoiceAssistant.Core.Audio;

/// <summary>
/// Lifecycle states of a capture stream (AUDIO-ENGINE.md).
/// </summary>
public enum AudioCaptureState
{
    Stopped = 0,
    Starting = 1,
    Capturing = 2,
    Paused = 3,
    Recovering = 4,
    Faulted = 5,
}
