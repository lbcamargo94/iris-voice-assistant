using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using IrisVoiceAssistant.AI.Contracts;
using IrisVoiceAssistant.Core.Audio;

namespace IrisVoiceAssistant.AI.Contracts.Tests;

public sealed class WorkerProtocolTests
{
    /// <summary>Also asserted by workers/tests/test_protocol.py, so both sides agree on every byte.</summary>
    private const string PythonReferenceHex = "1C000000" + "02" + "01" + "0800" + "2A00000000000000" + "8034000000000000" + "01000000" + "0000003F";
    private static readonly float[] HalfSample = [0.5f];
    private static readonly byte[] UnknownKind = [0x07, 1, 2];
    private static readonly byte[] ArrayJson = [0x01, (byte)'[', (byte)']'];
    private static readonly byte[] BrokenJson = [0x01, (byte)'{'];
    private static readonly byte[] ShortAudio = [0x02, 1, 0];

    public static TheoryData<byte[], string> MalformedBodies => new()
    {
        { UnknownKind, "unknown message kind" },
        { ArrayJson, "string 'type'" },
        { BrokenJson, "invalid control JSON" },
        { ShortAudio, "truncated audio header" },
    };

    private static async Task<WorkerMessage?> RoundTrip(byte[] wire) =>
        await WorkerProtocol.ReadAsync(new MemoryStream(wire), CancellationToken.None);

    [Fact]
    public async Task Control_messages_round_trip_with_their_id()
    {
        byte[] wire = WorkerProtocol.EncodeControl(new JsonObject { ["type"] = "result", ["id"] = 42, ["data"] = new JsonObject { ["ok"] = true } });
        Assert.Equal(wire.Length - 4, BinaryPrimitives.ReadInt32LittleEndian(wire));
        Assert.Equal(WorkerProtocol.KindControl, wire[4]);

        ControlMessage message = Assert.IsType<ControlMessage>(await RoundTrip(wire));
        Assert.Equal("result", message.Type);
        Assert.Equal(42, message.Id);
        Assert.True((bool)message.Body["data"]!["ok"]!);
    }

    [Fact]
    public async Task Audio_frames_round_trip_bit_exact()
    {
        float[] samples = [0.5f, -0.25f, 1f, float.Epsilon];
        var frame = new AudioFrame(AudioStreamKind.Microphone, 7, 2_240, samples, AudioFrameMarkers.FilledSilence | AudioFrameMarkers.DuringAssistantPlayback);
        byte[] wire = WorkerProtocol.EncodeAudio(frame);
        Assert.Equal(4 + WorkerProtocol.AudioHeaderBytes + 16, wire.Length);

        AudioFrame back = Assert.IsType<AudioMessage>(await RoundTrip(wire)).Frame;
        Assert.Equal((frame.Stream, frame.Sequence, frame.StreamPosition, frame.Markers), (back.Stream, back.Sequence, back.StreamPosition, back.Markers));
        Assert.Equal(samples, back.Samples.ToArray());
    }

    [Fact]
    public async Task Clean_end_of_stream_reads_as_null()
    {
        Assert.Null(await RoundTrip([]));
    }

    [Theory]
    [MemberData(nameof(MalformedBodies))]
    public void Malformed_bodies_are_protocol_violations(byte[] body, string expected)
    {
        WorkerProtocolException error = Assert.Throws<WorkerProtocolException>(() => WorkerProtocol.Decode(body));
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Audio_sample_count_must_match_the_payload()
    {
        byte[] wire = WorkerProtocol.EncodeAudio(new AudioFrame(AudioStreamKind.SystemAudio, 0, 0, new float[2], AudioFrameMarkers.None));
        BinaryPrimitives.WriteUInt32LittleEndian(wire.AsSpan(4 + 20), 3);
        Assert.Throws<WorkerProtocolException>(() => WorkerProtocol.Decode(wire.AsSpan(4)));
    }

    [Fact]
    public async Task Oversized_and_empty_lengths_are_rejected_before_allocating()
    {
        byte[] huge = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(huge, WorkerProtocol.MaxMessageBytes + 1);
        await Assert.ThrowsAsync<WorkerProtocolException>(() => RoundTrip(huge));
        await Assert.ThrowsAsync<WorkerProtocolException>(() => RoundTrip(new byte[4]));
        Assert.Throws<WorkerProtocolException>(() => WorkerProtocol.EncodeControl(new JsonObject
        {
            ["type"] = "big",
            ["data"] = new string('x', WorkerProtocol.MaxMessageBytes),
        }));
    }

    [Fact]
    public void Wire_format_matches_the_python_worker_byte_for_byte()
    {
        // Same frame as workers/tests/test_protocol.py: header <BBHqqI then float32 LE samples.
        byte[] wire = WorkerProtocol.EncodeAudio(new AudioFrame(AudioStreamKind.SystemAudio, 42, 13_440, HalfSample, (AudioFrameMarkers)8));
        Assert.Equal(PythonReferenceHex, Convert.ToHexString(wire));
        Assert.Throws<ArgumentException>(() => WorkerProtocol.EncodeControl(new JsonObject { ["id"] = 1 }));
        Assert.Equal("ping", ((ControlMessage)WorkerProtocol.Decode(Encoding.UTF8.GetBytes("\u0001{\"type\":\"ping\"}"))).Type);
    }
}
