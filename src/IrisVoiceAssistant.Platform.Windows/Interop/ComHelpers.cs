using System.Runtime.InteropServices;
using IrisVoiceAssistant.Core.Audio;

namespace IrisVoiceAssistant.Platform.Windows.Interop;

internal static class ComHelpers
{
    /// <summary>Throws <see cref="AudioDeviceLostException"/> for device-gone codes, a COM exception otherwise.</summary>
    public static void Check(int hr, string operation)
    {
        if (hr >= 0)
        {
            return;
        }

        if (IsDeviceLost(hr))
        {
            throw new AudioDeviceLostException($"{operation}: device unavailable (0x{hr:X8})", new AudioInteropException(operation, hr));
        }

        throw new AudioInteropException(operation, hr);
    }

    public static bool IsDeviceLost(int hr) => hr is HResult.AUDCLNT_E_DEVICE_INVALIDATED
        or HResult.AUDCLNT_E_RESOURCES_INVALIDATED
        or HResult.AUDCLNT_E_SERVICE_NOT_RUNNING
        or HResult.E_NOTFOUND;

    public static void Release(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
        {
            Marshal.ReleaseComObject(comObject);
        }
    }

    /// <summary>Reads a WAVEFORMATEX(TENSIBLE) into the portable format; null when it cannot be decoded.</summary>
    public static RawAudioFormat? ToRawFormat(IntPtr waveFormat)
    {
        WaveFormatEx format = Marshal.PtrToStructure<WaveFormatEx>(waveFormat);
        bool isFloat;
        if (format.FormatTag == WaveFormatEx.FormatExtensible && format.ExtraSize >= 22)
        {
            WaveFormatExtensible extensible = Marshal.PtrToStructure<WaveFormatExtensible>(waveFormat);
            if (extensible.SubFormat == WaveFormatExtensible.SubtypeIeeeFloat)
            {
                isFloat = true;
            }
            else if (extensible.SubFormat == WaveFormatExtensible.SubtypePcm)
            {
                isFloat = false;
            }
            else
            {
                return null;
            }
        }
        else if (format.FormatTag is WaveFormatEx.FormatIeeeFloat or WaveFormatEx.FormatPcm)
        {
            isFloat = format.FormatTag == WaveFormatEx.FormatIeeeFloat;
        }
        else
        {
            return null;
        }

        RawSampleType? type = (isFloat, format.BitsPerSample) switch
        {
            (true, 32) => RawSampleType.IeeeFloat,
            (false, 16) => RawSampleType.Pcm16,
            (false, 24) => RawSampleType.Pcm24,
            (false, 32) => RawSampleType.Pcm32, // also 24-in-32 containers: valid bits are left-justified
            _ => null,
        };
        if (type is null || format.BlockAlign != format.Channels * format.BitsPerSample / 8)
        {
            return null;
        }

        return new RawAudioFormat((int)format.SamplesPerSec, format.Channels, type.Value);
    }

    /// <summary>Allocates a 32-bit float WAVEFORMATEX; free with <see cref="Marshal.FreeHGlobal"/>.</summary>
    public static IntPtr AllocFloatFormat(int sampleRate, int channels)
    {
        var format = new WaveFormatEx
        {
            FormatTag = WaveFormatEx.FormatIeeeFloat,
            Channels = (ushort)channels,
            SamplesPerSec = (uint)sampleRate,
            BitsPerSample = 32,
            BlockAlign = (ushort)(channels * 4),
            AvgBytesPerSec = (uint)(sampleRate * channels * 4),
            ExtraSize = 0,
        };
        IntPtr pointer = Marshal.AllocHGlobal(Marshal.SizeOf<WaveFormatEx>());
        Marshal.StructureToPtr(format, pointer, fDeleteOld: false);
        return pointer;
    }
}
