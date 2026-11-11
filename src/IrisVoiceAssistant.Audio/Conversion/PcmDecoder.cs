using System.Buffers.Binary;
using IrisVoiceAssistant.Core.Audio;

namespace IrisVoiceAssistant.Audio.Conversion;

/// <summary>Decodes interleaved native PCM into mono float32 by averaging the channels (downmix).</summary>
public sealed class PcmDecoder
{
    private readonly RawAudioFormat _format;

    public PcmDecoder(RawAudioFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        format.Validate();
        _format = format;
    }

    /// <summary>Writes <paramref name="frameCount"/> mono samples to <paramref name="destination"/>.</summary>
    public void DecodeMono(ReadOnlySpan<byte> data, int frameCount, Span<float> destination)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frameCount);
        int needed = checked(frameCount * _format.BlockAlign);
        if (data.Length < needed)
        {
            throw new ArgumentException($"Expected at least {needed} bytes for {frameCount} frames, got {data.Length}.", nameof(data));
        }

        if (destination.Length < frameCount)
        {
            throw new ArgumentException("Destination is too small.", nameof(destination));
        }

        int channels = _format.Channels;
        int bytesPerSample = _format.BytesPerSample;
        float scale = 1f / channels;
        for (int frame = 0; frame < frameCount; frame++)
        {
            ReadOnlySpan<byte> block = data.Slice(frame * _format.BlockAlign, _format.BlockAlign);
            float sum = 0f;
            for (int channel = 0; channel < channels; channel++)
            {
                sum += ReadSample(block.Slice(channel * bytesPerSample, bytesPerSample));
            }

            destination[frame] = sum * scale;
        }
    }

    private float ReadSample(ReadOnlySpan<byte> bytes) => _format.SampleType switch
    {
        RawSampleType.IeeeFloat => BinaryPrimitives.ReadSingleLittleEndian(bytes),
        RawSampleType.Pcm16 => BinaryPrimitives.ReadInt16LittleEndian(bytes) / 32768f,
        RawSampleType.Pcm24 => ((bytes[2] << 24 | bytes[1] << 16 | bytes[0] << 8) >> 8) / 8388608f,
        RawSampleType.Pcm32 => BinaryPrimitives.ReadInt32LittleEndian(bytes) / 2147483648f,
        _ => throw new InvalidOperationException($"Unsupported sample type {_format.SampleType}."),
    };
}
