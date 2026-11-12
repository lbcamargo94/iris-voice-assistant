using IrisVoiceAssistant.Application.Live;
using IrisVoiceAssistant.Core.Audio;

namespace IrisVoiceAssistant.Application.Tests;

public sealed class ReplayBufferTests
{
    private static AudioFrame Frame(long index) =>
        new(AudioStreamKind.SystemAudio, index, index * 320, new float[320], AudioFrameMarkers.None);

    [Fact]
    public void Acknowledged_audio_is_released_and_the_rest_is_replayed()
    {
        var buffer = new ReplayBuffer(TimeSpan.FromSeconds(30));
        for (int i = 0; i < 10; i++)
        {
            buffer.Add(Frame(i));
        }

        buffer.Acknowledge(4 * 320);           // frames 0..3 end at or before 1280
        Assert.Equal(6, buffer.Count);
        Assert.Equal(4 * 320, buffer.Earliest);
        Assert.Equal([6L, 7, 8, 9], buffer.FramesFrom((6 * 320) + 10).Select(f => f.Sequence)); // frame 6 ends at 2240
    }

    [Fact]
    public void A_frame_straddling_the_ack_is_kept()
    {
        var buffer = new ReplayBuffer(TimeSpan.FromSeconds(30));
        buffer.Add(Frame(0));
        buffer.Add(Frame(1));
        buffer.Acknowledge(400);               // inside frame 1 (320..640)
        Assert.Equal([1L], buffer.FramesFrom(400).Select(f => f.Sequence));
    }

    [Fact]
    public void Capacity_drops_the_oldest_frames()
    {
        var buffer = new ReplayBuffer(TimeSpan.FromSeconds(0.1)); // 1600 samples = 5 frames
        for (int i = 0; i < 8; i++)
        {
            buffer.Add(Frame(i));
        }

        Assert.Equal(5, buffer.Count);
        Assert.Equal(3 * 320, buffer.Earliest); // frames 0..2 fell out: unrecoverable after a restart
    }
}
