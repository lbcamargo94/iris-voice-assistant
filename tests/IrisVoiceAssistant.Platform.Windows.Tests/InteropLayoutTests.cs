using System.Runtime.InteropServices;
using IrisVoiceAssistant.Core.Audio;
using IrisVoiceAssistant.Platform.Windows;
using IrisVoiceAssistant.Platform.Windows.Interop;

namespace IrisVoiceAssistant.Platform.Windows.Tests;

/// <summary>Native layouts and format decoding; no audio device needed (runs in CI).</summary>
public sealed class InteropLayoutTests
{
    [Fact]
    public void Native_structures_have_the_sizes_the_headers_define()
    {
        Assert.Equal(8, IntPtr.Size); // PropVariant's union offsets assume a 64-bit process
        Assert.Equal(24, Marshal.SizeOf<PropVariant>());
        Assert.Equal(18, Marshal.SizeOf<WaveFormatEx>());
        Assert.Equal(40, Marshal.SizeOf<WaveFormatExtensible>());
        Assert.Equal(12, Marshal.SizeOf<AudioClientActivationParams>());
        Assert.Equal(20, Marshal.SizeOf<PropertyKey>());
    }

    [Fact]
    public void Blob_variant_puts_size_and_pointer_where_propidl_expects_them()
    {
        PropVariant blob = PropVariant.Blob(new IntPtr(0x1234), 12);
        Assert.Equal(PropVariant.VtBlob, blob.ValueType);
        Assert.Equal(12, blob.Value1.ToInt64());
        Assert.Equal(0x1234, blob.Value2.ToInt64());
    }

    [Theory]
    [InlineData(48_000, 2)]
    [InlineData(44_100, 1)]
    public void Allocated_float_format_round_trips(int rate, int channels)
    {
        IntPtr format = ComHelpers.AllocFloatFormat(rate, channels);
        try
        {
            Assert.Equal(new RawAudioFormat(rate, channels, RawSampleType.IeeeFloat), ComHelpers.ToRawFormat(format));
        }
        finally
        {
            Marshal.FreeHGlobal(format);
        }
    }

    [Theory]
    [InlineData(3, 32, RawSampleType.IeeeFloat)]
    [InlineData(1, 16, RawSampleType.Pcm16)]
    [InlineData(1, 24, RawSampleType.Pcm24)]
    [InlineData(1, 32, RawSampleType.Pcm32)]
    public void Extensible_formats_are_decoded_by_subformat(int subtype, int bits, RawSampleType expected)
    {
        var format = new WaveFormatExtensible
        {
            Format = new WaveFormatEx
            {
                FormatTag = WaveFormatEx.FormatExtensible,
                Channels = 2,
                SamplesPerSec = 48_000,
                BitsPerSample = (ushort)bits,
                BlockAlign = (ushort)(2 * bits / 8),
                AvgBytesPerSec = (uint)(48_000 * 2 * bits / 8),
                ExtraSize = 22,
            },
            ValidBitsPerSample = (ushort)bits,
            ChannelMask = 3,
            SubFormat = subtype == 3 ? WaveFormatExtensible.SubtypeIeeeFloat : WaveFormatExtensible.SubtypePcm,
        };
        IntPtr pointer = Marshal.AllocHGlobal(Marshal.SizeOf<WaveFormatExtensible>());
        try
        {
            Marshal.StructureToPtr(format, pointer, fDeleteOld: false);
            Assert.Equal(new RawAudioFormat(48_000, 2, expected), ComHelpers.ToRawFormat(pointer));
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    [Fact]
    public void Unsupported_formats_are_rejected_instead_of_misread()
    {
        var format = new WaveFormatEx { FormatTag = 0x0055 /* MP3 */, Channels = 2, SamplesPerSec = 44_100, BitsPerSample = 0, BlockAlign = 1 };
        IntPtr pointer = Marshal.AllocHGlobal(Marshal.SizeOf<WaveFormatEx>());
        try
        {
            Marshal.StructureToPtr(format, pointer, fDeleteOld: false);
            Assert.Null(ComHelpers.ToRawFormat(pointer));
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    [Theory]
    [InlineData(1u, AudioDeviceFormFactor.Speakers)]
    [InlineData(3u, AudioDeviceFormFactor.Headphones)]
    [InlineData(5u, AudioDeviceFormFactor.Headset)]
    [InlineData(8u, AudioDeviceFormFactor.Digital)]
    [InlineData(10u, AudioDeviceFormFactor.Other)]
    public void Form_factors_map_from_the_endpoint_property(uint value, AudioDeviceFormFactor expected) =>
        Assert.Equal(expected, WasapiDeviceEnumerator.ToFormFactor(value));

    [Fact]
    public void Device_loss_codes_become_AudioDeviceLostException_others_AudioInteropException()
    {
        Assert.Throws<AudioDeviceLostException>(() => ComHelpers.Check(HResult.AUDCLNT_E_DEVICE_INVALIDATED, "op"));
        Assert.Throws<AudioDeviceLostException>(() => ComHelpers.Check(HResult.E_NOTFOUND, "op"));
        AudioInteropException other = Assert.Throws<AudioInteropException>(() => ComHelpers.Check(HResult.AUDCLNT_E_DEVICE_IN_USE, "Initialize"));
        Assert.Equal(HResult.AUDCLNT_E_DEVICE_IN_USE, other.HResult);
        Assert.Contains("Initialize", other.Message, StringComparison.Ordinal);
        ComHelpers.Check(HResult.S_OK, "op"); // success does not throw
        ComHelpers.Check(HResult.AUDCLNT_S_BUFFER_EMPTY, "op"); // success codes are not errors
    }

    [Fact]
    public void Speakers_and_unknown_outputs_warn_about_echo_headphones_do_not()
    {
        static AudioDeviceInfo Output(AudioDeviceFormFactor kind) => new("id", "name", AudioDeviceFlow.Render, kind, true);
        Assert.True(Output(AudioDeviceFormFactor.Speakers).MayEchoIntoMicrophone);
        Assert.True(Output(AudioDeviceFormFactor.Unknown).MayEchoIntoMicrophone);
        Assert.False(Output(AudioDeviceFormFactor.Headphones).MayEchoIntoMicrophone);
        Assert.False(Output(AudioDeviceFormFactor.Headset).MayEchoIntoMicrophone);
    }
}
