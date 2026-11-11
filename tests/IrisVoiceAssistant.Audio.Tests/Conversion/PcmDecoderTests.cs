using System.Buffers.Binary;
using IrisVoiceAssistant.Audio.Conversion;
using IrisVoiceAssistant.Core.Audio;

namespace IrisVoiceAssistant.Audio.Tests.Conversion;

public sealed class PcmDecoderTests
{
    [Fact]
    public void Downmixes_stereo_float_by_averaging()
    {
        byte[] data = new byte[16];
        float[] values = [0.5f, -0.5f, 1.0f, 0.0f];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(i * 4), values[i]);
        }

        float[] mono = new float[2];
        new PcmDecoder(new RawAudioFormat(48_000, 2, RawSampleType.IeeeFloat)).DecodeMono(data, 2, mono);
        Assert.Equal([0.0f, 0.5f], mono);
    }

    [Fact]
    public void Decodes_signed_integer_formats()
    {
        float[] mono = new float[2];
        byte[] pcm16 = [0x00, 0x80, 0xFF, 0x7F]; // -32768, 32767
        new PcmDecoder(new RawAudioFormat(16_000, 1, RawSampleType.Pcm16)).DecodeMono(pcm16, 2, mono);
        Assert.Equal(-1.0f, mono[0]);
        Assert.Equal(32767 / 32768f, mono[1]);

        byte[] pcm24 = [0x00, 0x00, 0xC0, 0xFF, 0xFF, 0x3F]; // -0.5 and ~+0.5
        new PcmDecoder(new RawAudioFormat(16_000, 1, RawSampleType.Pcm24)).DecodeMono(pcm24, 2, mono);
        Assert.Equal(-0.5f, mono[0]);
        Assert.Equal(0.5f, mono[1], 1e-6f);

        byte[] pcm32 = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(pcm32, int.MinValue / 4);
        BinaryPrimitives.WriteInt32LittleEndian(pcm32.AsSpan(4), int.MaxValue);
        new PcmDecoder(new RawAudioFormat(16_000, 1, RawSampleType.Pcm32)).DecodeMono(pcm32, 2, mono);
        Assert.Equal(-0.25f, mono[0]);
        Assert.Equal(1.0f, mono[1], 1e-6f);
    }

    [Fact]
    public void Rejects_short_buffers_and_invalid_formats()
    {
        var decoder = new PcmDecoder(new RawAudioFormat(48_000, 2, RawSampleType.Pcm16));
        Assert.Throws<ArgumentException>(() => decoder.DecodeMono(new byte[7], 2, new float[2]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PcmDecoder(new RawAudioFormat(48_000, 0, RawSampleType.Pcm16)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PcmDecoder(new RawAudioFormat(48_000, 2, (RawSampleType)99)));
    }
}
