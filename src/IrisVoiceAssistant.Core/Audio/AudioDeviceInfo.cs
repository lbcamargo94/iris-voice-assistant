namespace IrisVoiceAssistant.Core.Audio;

public enum AudioDeviceFlow
{
    Render = 0,
    Capture = 1,
}

/// <summary>Physical kind of an endpoint, used for the speaker-echo warning (ADR-007, layer C).</summary>
public enum AudioDeviceFormFactor
{
    Unknown = 0,
    Speakers = 1,
    Headphones = 2,
    Headset = 3,
    Microphone = 4,
    LineLevel = 5,
    Digital = 6,
    Handset = 7,
    Remote = 8,
    Other = 9,
}

/// <summary>An audio endpoint as shown to the user for explicit device selection.</summary>
public sealed record AudioDeviceInfo(
    string Id,
    string Name,
    AudioDeviceFlow Flow,
    AudioDeviceFormFactor FormFactor,
    bool IsDefault)
{
    /// <summary>
    /// True when Iris speaking on this output can be picked up by the microphone acoustically.
    /// Unknown kinds are treated as speakers so the user is warned rather than not.
    /// </summary>
    public bool MayEchoIntoMicrophone => Flow == AudioDeviceFlow.Render
        && FormFactor is not (AudioDeviceFormFactor.Headphones or AudioDeviceFormFactor.Headset or AudioDeviceFormFactor.Handset);
}

public interface IAudioDeviceEnumerator
{
    IReadOnlyList<AudioDeviceInfo> GetDevices(AudioDeviceFlow flow);

    AudioDeviceInfo? GetDefault(AudioDeviceFlow flow);
}
