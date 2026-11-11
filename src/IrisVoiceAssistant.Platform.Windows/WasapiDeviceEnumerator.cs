using System.Runtime.InteropServices;
using IrisVoiceAssistant.Core.Audio;
using IrisVoiceAssistant.Platform.Windows.Interop;

namespace IrisVoiceAssistant.Platform.Windows;

/// <summary>Lists active WASAPI endpoints with their friendly name and form factor (ADR-007, layer C).</summary>
public sealed class WasapiDeviceEnumerator : IAudioDeviceEnumerator
{
    public IReadOnlyList<AudioDeviceInfo> GetDevices(AudioDeviceFlow flow)
    {
        IMMDeviceEnumerator enumerator = CreateEnumerator();
        IMMDeviceCollection? collection = null;
        try
        {
            string? defaultId = DefaultId(enumerator, flow);
            ComHelpers.Check(enumerator.EnumAudioEndpoints(ToDataFlow(flow), AudioClientConstants.DeviceStateActive, out collection), "EnumAudioEndpoints");
            ComHelpers.Check(collection.GetCount(out uint count), "GetCount");
            var devices = new List<AudioDeviceInfo>((int)count);
            for (uint i = 0; i < count; i++)
            {
                ComHelpers.Check(collection.Item(i, out IMMDevice device), "Item");
                try
                {
                    devices.Add(Describe(device, flow, defaultId));
                }
                finally
                {
                    ComHelpers.Release(device);
                }
            }

            return devices;
        }
        finally
        {
            ComHelpers.Release(collection);
            ComHelpers.Release(enumerator);
        }
    }

    public AudioDeviceInfo? GetDefault(AudioDeviceFlow flow)
    {
        IMMDeviceEnumerator enumerator = CreateEnumerator();
        try
        {
            int hr = enumerator.GetDefaultAudioEndpoint(ToDataFlow(flow), ERole.Console, out IMMDevice device);
            if (hr == HResult.E_NOTFOUND)
            {
                return null;
            }

            ComHelpers.Check(hr, "GetDefaultAudioEndpoint");
            try
            {
                ComHelpers.Check(device.GetId(out string id), "GetId");
                return Describe(device, flow, id);
            }
            finally
            {
                ComHelpers.Release(device);
            }
        }
        finally
        {
            ComHelpers.Release(enumerator);
        }
    }

    internal static IMMDeviceEnumerator CreateEnumerator() => (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();

    internal static EDataFlow ToDataFlow(AudioDeviceFlow flow) => flow == AudioDeviceFlow.Render ? EDataFlow.Render : EDataFlow.Capture;

    internal static AudioDeviceFormFactor ToFormFactor(uint? value) => value switch
    {
        0 => AudioDeviceFormFactor.Remote,
        1 => AudioDeviceFormFactor.Speakers,
        2 => AudioDeviceFormFactor.LineLevel,
        3 => AudioDeviceFormFactor.Headphones,
        4 => AudioDeviceFormFactor.Microphone,
        5 => AudioDeviceFormFactor.Headset,
        6 => AudioDeviceFormFactor.Handset,
        7 or 8 or 9 => AudioDeviceFormFactor.Digital,
        null => AudioDeviceFormFactor.Unknown,
        _ => AudioDeviceFormFactor.Other,
    };

    private static string? DefaultId(IMMDeviceEnumerator enumerator, AudioDeviceFlow flow)
    {
        if (enumerator.GetDefaultAudioEndpoint(ToDataFlow(flow), ERole.Console, out IMMDevice device) < 0)
        {
            return null;
        }

        try
        {
            return device.GetId(out string id) >= 0 ? id : null;
        }
        finally
        {
            ComHelpers.Release(device);
        }
    }

    private static AudioDeviceInfo Describe(IMMDevice device, AudioDeviceFlow flow, string? defaultId)
    {
        ComHelpers.Check(device.GetId(out string id), "GetId");
        string name = id;
        uint? formFactor = null;
        if (device.OpenPropertyStore(0 /* STGM_READ */, out IPropertyStore store) >= 0)
        {
            try
            {
                name = ReadString(store, PropertyKey.DeviceFriendlyName) ?? id;
                formFactor = ReadUInt32(store, PropertyKey.AudioEndpointFormFactor);
            }
            finally
            {
                ComHelpers.Release(store);
            }
        }

        return new AudioDeviceInfo(id, name, flow, ToFormFactor(formFactor), string.Equals(id, defaultId, StringComparison.Ordinal));
    }

    private static string? ReadString(IPropertyStore store, PropertyKey key)
    {
        if (store.GetValue(ref key, out PropVariant value) < 0)
        {
            return null;
        }

        try
        {
            return value.AsString();
        }
        finally
        {
            _ = NativeMethods.PropVariantClear(ref value);
        }
    }

    private static uint? ReadUInt32(IPropertyStore store, PropertyKey key)
    {
        if (store.GetValue(ref key, out PropVariant value) < 0)
        {
            return null;
        }

        try
        {
            return value.AsUInt32();
        }
        finally
        {
            _ = NativeMethods.PropVariantClear(ref value);
        }
    }
}
