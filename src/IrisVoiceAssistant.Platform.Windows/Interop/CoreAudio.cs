using System.Runtime.InteropServices;

namespace IrisVoiceAssistant.Platform.Windows.Interop;

// Minimal Core Audio (MMDevice + WASAPI) interop. Method order must match the native vtables exactly
// (mmdeviceapi.h, audioclient.h, propsys.h, audioclientactivationparams.h). Every method returns its HRESULT
// (PreserveSig) so callers can map device-loss codes explicitly.

internal enum EDataFlow
{
    Render = 0,
    Capture = 1,
}

internal enum ERole
{
    Console = 0,
    Multimedia = 1,
    Communications = 2,
}

internal static class HResult
{
    public const int S_OK = 0;
    public const int AUDCLNT_S_BUFFER_EMPTY = 0x08890001;
    public const int AUDCLNT_E_DEVICE_INVALIDATED = unchecked((int)0x88890004);
    public const int AUDCLNT_E_DEVICE_IN_USE = unchecked((int)0x8889000A);
    public const int AUDCLNT_E_SERVICE_NOT_RUNNING = unchecked((int)0x88890010);
    public const int AUDCLNT_E_RESOURCES_INVALIDATED = unchecked((int)0x88890026);
    public const int E_NOTFOUND = unchecked((int)0x80070490);
}

internal static class AudioClientConstants
{
    public const int SharedMode = 0;
    public const uint StreamFlagsLoopback = 0x00020000;
    public const uint StreamFlagsEventCallback = 0x00040000;
    public const uint StreamFlagsSrcDefaultQuality = 0x08000000;
    public const uint StreamFlagsAutoConvertPcm = 0x80000000;
    public const uint BufferFlagsDataDiscontinuity = 0x1;
    public const uint BufferFlagsSilent = 0x2;
    public const uint BufferFlagsTimestampError = 0x4;
    public const uint DeviceStateActive = 0x1;
    public const uint ClsctxAll = 0x17;
    public const string VirtualAudioDeviceProcessLoopback = @"VAD\Process_Loopback";
}

[ComImport]
[Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumeratorComObject
{
}

[ComImport]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig]
    int EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out IMMDeviceCollection devices);

    [PreserveSig]
    int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);

    [PreserveSig]
    int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);

    [PreserveSig]
    int RegisterEndpointNotificationCallback(IntPtr client);

    [PreserveSig]
    int UnregisterEndpointNotificationCallback(IntPtr client);
}

[ComImport]
[Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceCollection
{
    [PreserveSig]
    int GetCount(out uint count);

    [PreserveSig]
    int Item(uint index, out IMMDevice device);
}

[ComImport]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig]
    int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);

    [PreserveSig]
    int OpenPropertyStore(uint access, out IPropertyStore properties);

    [PreserveSig]
    int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

    [PreserveSig]
    int GetState(out uint state);
}

[ComImport]
[Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyStore
{
    [PreserveSig]
    int GetCount(out uint count);

    [PreserveSig]
    int GetAt(uint index, out PropertyKey key);

    [PreserveSig]
    int GetValue(ref PropertyKey key, out PropVariant value);

    [PreserveSig]
    int SetValue(ref PropertyKey key, ref PropVariant value);

    [PreserveSig]
    int Commit();
}

[ComImport]
[Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient
{
    [PreserveSig]
    int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr audioSessionGuid);

    [PreserveSig]
    int GetBufferSize(out uint bufferFrames);

    [PreserveSig]
    int GetStreamLatency(out long latency);

    [PreserveSig]
    int GetCurrentPadding(out uint paddingFrames);

    [PreserveSig]
    int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);

    [PreserveSig]
    int GetMixFormat(out IntPtr deviceFormat);

    [PreserveSig]
    int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);

    [PreserveSig]
    int Start();

    [PreserveSig]
    int Stop();

    [PreserveSig]
    int Reset();

    [PreserveSig]
    int SetEventHandle(IntPtr eventHandle);

    [PreserveSig]
    int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
}

[ComImport]
[Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioCaptureClient
{
    [PreserveSig]
    int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);

    [PreserveSig]
    int ReleaseBuffer(uint frames);

    [PreserveSig]
    int GetNextPacketSize(out uint frames);
}

[ComImport]
[Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceAsyncOperation
{
    [PreserveSig]
    int GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
}

[ComImport]
[Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceCompletionHandler
{
    [PreserveSig]
    int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
}

/// <summary>Marker that the completion handler may be called on any thread (required by ActivateAudioInterfaceAsync).</summary>
[ComImport]
[Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAgileObject
{
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey
{
    public Guid FormatId;
    public int PropertyId;

    public PropertyKey(Guid formatId, int propertyId)
    {
        FormatId = formatId;
        PropertyId = propertyId;
    }

    public static PropertyKey DeviceFriendlyName => new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);

    public static PropertyKey AudioEndpointFormFactor => new(new Guid("1DA5D803-D492-4EDD-8C23-E0C0FFEE7F0E"), 0);
}

/// <summary>PROPVARIANT for 64-bit processes: the value union starts at offset 8.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PropVariant
{
    public const ushort VtUi4 = 19;
    public const ushort VtLpwstr = 31;
    public const ushort VtBlob = 65;

    public ushort ValueType;
    public ushort Reserved1;
    public ushort Reserved2;
    public ushort Reserved3;
    public IntPtr Value1;
    public IntPtr Value2;

    public static PropVariant Blob(IntPtr data, int size) => new()
    {
        ValueType = VtBlob,
        Value1 = size,   // BLOB.cbSize (ULONG) at offset 8
        Value2 = data,   // BLOB.pBlobData at offset 16
    };

    public readonly string? AsString() => ValueType == VtLpwstr ? Marshal.PtrToStringUni(Value1) : null;

    public readonly uint? AsUInt32() => ValueType == VtUi4 ? (uint)(Value1.ToInt64() & 0xFFFFFFFF) : null;
}

[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct WaveFormatEx
{
    public const ushort FormatPcm = 1;
    public const ushort FormatIeeeFloat = 3;
    public const ushort FormatExtensible = 0xFFFE;

    public ushort FormatTag;
    public ushort Channels;
    public uint SamplesPerSec;
    public uint AvgBytesPerSec;
    public ushort BlockAlign;
    public ushort BitsPerSample;
    public ushort ExtraSize;
}

[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct WaveFormatExtensible
{
    public static readonly Guid SubtypePcm = new("00000001-0000-0010-8000-00AA00389B71");
    public static readonly Guid SubtypeIeeeFloat = new("00000003-0000-0010-8000-00AA00389B71");

    public WaveFormatEx Format;
    public ushort ValidBitsPerSample;
    public uint ChannelMask;
    public Guid SubFormat;
}

[StructLayout(LayoutKind.Sequential)]
internal struct AudioClientActivationParams
{
    public const int TypeProcessLoopback = 1;
    public const int LoopbackModeIncludeTargetProcessTree = 0;
    public const int LoopbackModeExcludeTargetProcessTree = 1;

    public int ActivationType;
    public uint TargetProcessId;
    public int ProcessLoopbackMode;
}

internal static partial class NativeMethods
{
    [LibraryImport("Mmdevapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int ActivateAudioInterfaceAsync(
        string deviceInterfacePath,
        ref Guid riid,
        ref PropVariant activationParams,
        IntPtr completionHandler,
        out IntPtr activationOperation);

    [LibraryImport("Ole32.dll")]
    internal static partial int PropVariantClear(ref PropVariant value);
}
