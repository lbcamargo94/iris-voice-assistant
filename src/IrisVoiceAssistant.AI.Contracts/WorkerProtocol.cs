using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using IrisVoiceAssistant.Core.Audio;

namespace IrisVoiceAssistant.AI.Contracts;

/// <summary>
/// Iris Worker Protocol v1 (ADR-014). Each message is <c>[u32 LE length][kind u8][body]</c>, at most
/// <see cref="MaxMessageBytes"/>. Kind 0x01 is a UTF-8 JSON control object with a <c>type</c> field; kind 0x02 is an
/// audio frame: 24-byte header (stream u8, markers u16, sequence i64, position i64, sample count u32, all
/// little-endian) followed by float32 LE samples.
/// </summary>
public static class WorkerProtocol
{
    public const int Version = 1;
    public const int MaxMessageBytes = 1 << 20;
    public const byte KindControl = 0x01;
    public const byte KindAudio = 0x02;
    public const int AudioHeaderBytes = 24;

    public static byte[] EncodeControl(JsonObject message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message["type"] is null)
        {
            throw new ArgumentException("Control messages need a 'type'.", nameof(message));
        }

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(message);
        byte[] payload = new byte[1 + json.Length];
        payload[0] = KindControl;
        json.CopyTo(payload, 1);
        return Frame(payload);
    }

    public static byte[] EncodeAudio(AudioFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ReadOnlySpan<float> samples = frame.Samples.Span;
        byte[] payload = new byte[AudioHeaderBytes + (samples.Length * 4)];
        Span<byte> span = payload;
        span[0] = KindAudio;
        span[1] = (byte)frame.Stream;
        BinaryPrimitives.WriteUInt16LittleEndian(span[2..], (ushort)frame.Markers);
        BinaryPrimitives.WriteInt64LittleEndian(span[4..], frame.Sequence);
        BinaryPrimitives.WriteInt64LittleEndian(span[12..], frame.StreamPosition);
        BinaryPrimitives.WriteUInt32LittleEndian(span[20..], (uint)samples.Length);
        MemoryMarshal.AsBytes(samples).CopyTo(span[AudioHeaderBytes..]); // x86/ARM are little-endian
        return Frame(payload);
    }

    /// <summary>Decodes one message body (without the length prefix).</summary>
    public static WorkerMessage Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty)
        {
            throw new WorkerProtocolException("empty message");
        }

        switch (payload[0])
        {
            case KindControl:
                JsonObject? json;
                try
                {
                    json = JsonNode.Parse(payload[1..]) as JsonObject;
                }
                catch (JsonException error)
                {
                    throw new WorkerProtocolException($"invalid control JSON: {error.Message}");
                }

                string? type = json?["type"]?.GetValueKind() == JsonValueKind.String ? (string?)json["type"] : null;
                if (json is null || type is null)
                {
                    throw new WorkerProtocolException("control message without a string 'type'");
                }

                return new ControlMessage(type, json);

            case KindAudio:
                if (payload.Length < AudioHeaderBytes)
                {
                    throw new WorkerProtocolException("truncated audio header");
                }

                uint count = BinaryPrimitives.ReadUInt32LittleEndian(payload[20..]);
                if (payload.Length != AudioHeaderBytes + ((long)count * 4))
                {
                    throw new WorkerProtocolException($"audio frame declares {count} samples but carries {payload.Length - AudioHeaderBytes} bytes");
                }

                var stream = (AudioStreamKind)payload[1];
                if (!Enum.IsDefined(stream))
                {
                    throw new WorkerProtocolException($"unknown audio stream {payload[1]}");
                }

                float[] samples = MemoryMarshal.Cast<byte, float>(payload[AudioHeaderBytes..]).ToArray();
                return new AudioMessage(new AudioFrame(
                    stream,
                    BinaryPrimitives.ReadInt64LittleEndian(payload[4..]),
                    BinaryPrimitives.ReadInt64LittleEndian(payload[12..]),
                    samples,
                    (AudioFrameMarkers)BinaryPrimitives.ReadUInt16LittleEndian(payload[2..])));

            default:
                throw new WorkerProtocolException($"unknown message kind 0x{payload[0]:X2}");
        }
    }

    /// <summary>Reads one message; null at a clean end of stream.</summary>
    public static async ValueTask<WorkerMessage?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] header = new byte[4];
        int first = await stream.ReadAsync(header.AsMemory(0, 4), cancellationToken).ConfigureAwait(false);
        if (first == 0)
        {
            return null;
        }

        if (first < 4)
        {
            await stream.ReadExactlyAsync(header.AsMemory(first), cancellationToken).ConfigureAwait(false);
        }

        uint length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length == 0 || length > MaxMessageBytes)
        {
            throw new WorkerProtocolException($"message length {length} outside 1..{MaxMessageBytes}");
        }

        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return Decode(payload);
    }

    private static byte[] Frame(byte[] payload)
    {
        if (payload.Length > MaxMessageBytes)
        {
            throw new WorkerProtocolException($"message of {payload.Length} bytes exceeds {MaxMessageBytes}");
        }

        byte[] message = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(message, (uint)payload.Length);
        payload.CopyTo(message, 4);
        return message;
    }
}

public abstract record WorkerMessage;

public sealed record ControlMessage(string Type, JsonObject Body) : WorkerMessage
{
    public long? Id => Body["id"]?.GetValueKind() == JsonValueKind.Number ? (long)Body["id"]! : null;
}

public sealed record AudioMessage(AudioFrame Frame) : WorkerMessage;

/// <summary>The peer violated the protocol (framing, size or content). The connection cannot continue.</summary>
public sealed class WorkerProtocolException : Exception
{
    public WorkerProtocolException()
    {
    }

    public WorkerProtocolException(string message)
        : base(message)
    {
    }

    public WorkerProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
