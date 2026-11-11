using System.Buffers.Binary;
using System.Diagnostics;
using IrisVoiceAssistant.Audio.Pipeline;
using IrisVoiceAssistant.Core.Audio;

namespace IrisVoiceAssistant.Audio.Tests.Pipeline;

public sealed class CaptureStreamPipelineTests
{
    private const int Frame = CanonicalAudioFormat.SamplesPerFrame;
    private static readonly RawAudioFormat Mono16k = new(16_000, 1, RawSampleType.IeeeFloat);

    private static long At(double seconds) => (long)(seconds * Stopwatch.Frequency);

    private static byte[] Block(int frames, float value = 0.25f)
    {
        byte[] data = new byte[frames * 4];
        for (int i = 0; i < frames; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(i * 4), value);
        }

        return data;
    }

    private static List<AudioFrame> Drain(CaptureStreamPipeline pipeline)
    {
        var frames = new List<AudioFrame>();
        while (pipeline.TryRead(out AudioFrame? frame))
        {
            frames.Add(frame!);
        }

        return frames;
    }

    /// <summary>Feeds contiguous 10 ms blocks timed as a real device would deliver them.</summary>
    private static void Feed(CaptureStreamPipeline pipeline, double fromSeconds, double seconds, float value = 0.25f)
    {
        for (int i = 0; i < (int)Math.Round(seconds * 100); i++)
        {
            pipeline.OnAudio(Block(160, value), 160, At(fromSeconds + (i * 0.01)), RawBlockMarkers.None);
        }
    }

    [Fact]
    public void Emits_contiguous_20ms_frames_and_reconciles()
    {
        var pipeline = new CaptureStreamPipeline(AudioStreamKind.Microphone, Mono16k, sessionStartTimestamp: 0);
        Feed(pipeline, 0, 1.0);
        List<AudioFrame> frames = Drain(pipeline);

        Assert.Equal(50, frames.Count);
        Assert.All(frames, f => Assert.Equal(Frame, f.Samples.Length));
        Assert.Equal(Enumerable.Range(0, 50).Select(i => (long)i), frames.Select(f => f.Sequence));
        Assert.Equal(Enumerable.Range(0, 50).Select(i => (long)i * Frame), frames.Select(f => f.StreamPosition));
        Assert.All(frames, f => Assert.Equal(AudioFrameMarkers.None, f.Markers));

        AudioStreamCounters counters = pipeline.Counters;
        Assert.Equal(16_000, counters.CapturedSamples);
        Assert.Equal(16_000, counters.ProcessedSamples);
        Assert.True(counters.Reconciles);
        Assert.Empty(pipeline.Gaps);
    }

    [Fact]
    public void Idle_device_is_filled_with_silence_up_to_now_minus_the_delay()
    {
        var pipeline = new CaptureStreamPipeline(AudioStreamKind.SystemAudio, Mono16k, 0, silenceFillDelay: TimeSpan.FromMilliseconds(250));
        pipeline.OnIdle(At(1.0));
        List<AudioFrame> frames = Drain(pipeline);

        Assert.Equal(12_000, pipeline.Position); // 1 s - 250 ms
        Assert.Equal(37, frames.Count);          // 37 full frames + 160 samples pending
        Assert.All(frames, f => Assert.True(f.Markers.HasFlag(AudioFrameMarkers.FilledSilence)));
        Assert.Empty(pipeline.Gaps);             // silence while nothing plays is not a loss
        Assert.True(pipeline.Counters.Reconciles);
    }

    [Fact]
    public void Audio_after_a_quiet_period_lands_at_its_capture_time()
    {
        var pipeline = new CaptureStreamPipeline(AudioStreamKind.SystemAudio, Mono16k, 0);
        pipeline.OnIdle(At(2.0));
        Feed(pipeline, 2.0, 0.2);
        List<AudioFrame> frames = Drain(pipeline);

        AudioFrame firstAudio = frames.First(f => !f.Markers.HasFlag(AudioFrameMarkers.FilledSilence));
        Assert.Equal(2.0 * 16_000, firstAudio.StreamPosition);
        Assert.Empty(pipeline.Gaps);
    }

    [Fact]
    public void Device_glitch_hole_is_filled_and_recorded_as_a_gap()
    {
        var pipeline = new CaptureStreamPipeline(AudioStreamKind.Microphone, Mono16k, 0);
        Feed(pipeline, 0, 0.5);
        pipeline.OnAudio(Block(160), 160, At(0.8), RawBlockMarkers.Discontinuity);
        pipeline.OnAudio(Block(160), 160, At(0.81), RawBlockMarkers.None); // completes the frame at 12 800

        AudioGap gap = Assert.Single(pipeline.Gaps);
        Assert.Equal(AudioGapCause.DeviceDiscontinuity, gap.Cause);
        Assert.Equal(8_000, gap.StartPosition);
        Assert.Equal(4_800, gap.LengthSamples);
        Assert.False(gap.IsLoss);
        AudioFrame after = Drain(pipeline).Single(f => f.StreamPosition == 12_800);
        Assert.True(after.Markers.HasFlag(AudioFrameMarkers.AfterDiscontinuity));
    }

    [Fact]
    public void Small_clock_jitter_is_measured_not_corrected()
    {
        var pipeline = new CaptureStreamPipeline(AudioStreamKind.Microphone, Mono16k, 0);
        for (int i = 0; i < 100; i++)
        {
            double jitter = (i % 2 == 0 ? 1 : -1) * 0.004;
            pipeline.OnAudio(Block(160), 160, At((i * 0.01) + 0.02 + jitter), RawBlockMarkers.None);
        }

        Assert.Equal(16_000, pipeline.Counters.CapturedSamples);
        Assert.Equal(384, pipeline.Counters.FilledSilenceSamples); // start-up alignment only (first packet at 24 ms)
        Assert.InRange(pipeline.MaxClockDrift.TotalMilliseconds, 4, 10); // ±4 ms jitter, not the 24 ms start offset
    }

    [Fact]
    public void First_packet_is_aligned_to_its_capture_time_even_below_the_hole_threshold()
    {
        var mic = new CaptureStreamPipeline(AudioStreamKind.Microphone, Mono16k, 0);
        var system = new CaptureStreamPipeline(AudioStreamKind.SystemAudio, Mono16k, 0);
        Feed(mic, 0.09, 0.5);
        Feed(system, 0.02, 0.5);

        Assert.Equal(1_440, mic.Counters.FilledSilenceSamples);
        Assert.Equal(320, system.Counters.FilledSilenceSamples);
        Assert.Equal(1_440 + 8_000, mic.Position);  // both streams now place audio at its real session time
        Assert.Equal(320 + 8_000, system.Position);
        Assert.True(mic.MaxClockDrift < TimeSpan.FromMilliseconds(1), mic.MaxClockDrift.ToString()); // rounding only
        Assert.Empty(mic.Gaps);
    }

    [Fact]
    public void Overflow_drops_the_oldest_frames_into_one_visible_loss_gap()
    {
        var pipeline = new CaptureStreamPipeline(AudioStreamKind.SystemAudio, Mono16k, 0, queueCapacityFrames: 5);
        Feed(pipeline, 0, 0.4); // 20 frames, nobody reading

        List<AudioFrame> frames = Drain(pipeline);
        Assert.Equal([15L, 16, 17, 18, 19], frames.Select(f => f.Sequence));
        AudioGap gap = Assert.Single(pipeline.Gaps);
        Assert.Equal(AudioGapCause.Overflow, gap.Cause);
        Assert.True(gap.IsLoss);
        Assert.Equal(0, gap.StartPosition);
        Assert.Equal(15 * Frame, gap.LengthSamples);

        AudioStreamCounters counters = pipeline.Counters;
        Assert.Equal(15 * Frame, counters.DroppedSamples);
        Assert.True(counters.Reconciles, counters.ToString());
    }

    [Fact]
    public void Alternating_drops_under_sustained_load_form_one_episode_with_exact_loss()
    {
        var pipeline = new CaptureStreamPipeline(AudioStreamKind.SystemAudio, Mono16k, 0, queueCapacityFrames: 2);
        var kept = new List<AudioFrame>();
        for (int i = 0; i < 100; i++)
        {
            pipeline.OnAudio(Block(160), 160, At(i * 0.01), RawBlockMarkers.None);
            if (i % 4 == 3 && pipeline.TryRead(out AudioFrame? frame))
            {
                kept.Add(frame!); // a consumer at half the needed speed
            }
        }

        kept.AddRange(Drain(pipeline));
        AudioGap episode = Assert.Single(pipeline.Gaps);
        Assert.Equal(AudioGapCause.Overflow, episode.Cause);
        Assert.Equal(pipeline.Counters.DroppedSamples, episode.MissingSamples);
        Assert.True(episode.LengthSamples > episode.MissingSamples); // span includes the frames that got through
        Assert.Equal(50 * Frame, episode.MissingSamples + (kept.Count * Frame));
        Assert.True(pipeline.Counters.Reconciles);
    }

    [Fact]
    public void Drops_far_apart_are_separate_episodes()
    {
        var pipeline = new CaptureStreamPipeline(AudioStreamKind.SystemAudio, Mono16k, 0, queueCapacityFrames: 1);
        Feed(pipeline, 0, 0.04); // 2 frames, nobody reading: frame 0 dropped
        Drain(pipeline);
        for (int i = 4; i < 204; i++) // 2 s read in time: no drops
        {
            pipeline.OnAudio(Block(160), 160, At(i * 0.01), RawBlockMarkers.None);
            Drain(pipeline);
        }

        Feed(pipeline, 2.04, 0.04); // overflow again, 2 s after the first episode
        AudioGap[] episodes = [.. pipeline.Gaps];
        Assert.Equal(2, episodes.Length);
        Assert.All(episodes, g => Assert.Equal(Frame, g.MissingSamples));
        Assert.True(pipeline.Counters.Reconciles);
    }

    [Fact]
    public void Pause_skips_the_timeline_without_producing_frames()
    {
        var pipeline = new CaptureStreamPipeline(AudioStreamKind.Microphone, Mono16k, 0);
        Feed(pipeline, 0, 1.0);
        pipeline.Pause(At(1.0));
        pipeline.OnAudio(Block(160), 160, At(1.5), RawBlockMarkers.None); // late packet while paused: ignored
        pipeline.Resume(At(3.0));
        Feed(pipeline, 3.0, 0.1);

        AudioGap gap = Assert.Single(pipeline.Gaps);
        Assert.Equal(AudioGapCause.Paused, gap.Cause);
        Assert.Equal(16_000, gap.StartPosition);
        Assert.Equal(32_000, gap.LengthSamples);
        List<AudioFrame> frames = Drain(pipeline);
        Assert.Equal(48_000, frames.First(f => f.StreamPosition > 16_000).StreamPosition);
        Assert.Equal(frames.Select((_, i) => (long)i), frames.Select(f => f.Sequence)); // sequence stays contiguous
        Assert.Equal(32_000, pipeline.Counters.NotCapturedSamples);
        Assert.True(pipeline.Counters.Reconciles);
    }

    [Fact]
    public void Device_loss_is_recorded_and_the_format_can_change_on_recovery()
    {
        var pipeline = new CaptureStreamPipeline(AudioStreamKind.Microphone, Mono16k, 0);
        Feed(pipeline, 0, 0.5);
        pipeline.MarkNotCaptured(At(2.5), AudioGapCause.DeviceLost);
        pipeline.Reconfigure(new RawAudioFormat(48_000, 2, RawSampleType.IeeeFloat));
        pipeline.OnAudio(new byte[480 * 8], 480, At(2.5), RawBlockMarkers.Silent);

        AudioGap gap = Assert.Single(pipeline.Gaps);
        Assert.Equal(AudioGapCause.DeviceLost, gap.Cause);
        Assert.Equal((8_000, 32_000), (gap.StartPosition, gap.LengthSamples));
        Assert.Equal(48_000, pipeline.Format.SampleRate);
        Assert.True(pipeline.Counters.Reconciles);
    }

    [Fact]
    public void Gap_events_are_raised()
    {
        var pipeline = new CaptureStreamPipeline(AudioStreamKind.Microphone, Mono16k, 0);
        var seen = new List<AudioGap>();
        pipeline.GapRecorded += seen.Add;
        pipeline.Pause(At(0));
        pipeline.Resume(At(1));
        Assert.Single(seen);
    }

    [Fact]
    public async Task Complete_flushes_the_partial_frame_and_ends_the_reader()
    {
        var pipeline = new CaptureStreamPipeline(AudioStreamKind.Microphone, Mono16k, 0);
        pipeline.OnAudio(Block(100), 100, At(0), RawBlockMarkers.None);
        pipeline.Complete();

        var frames = new List<AudioFrame>();
        await foreach (AudioFrame frame in pipeline.ReadAllAsync())
        {
            frames.Add(frame);
        }

        AudioFrame only = Assert.Single(frames);
        Assert.True(only.Markers.HasFlag(AudioFrameMarkers.FilledSilence)); // partially filled
        Assert.Equal(0.25f, only.Samples.Span[0]);
        Assert.Equal(0f, only.Samples.Span[Frame - 1]);
        AudioStreamCounters counters = pipeline.Counters;
        Assert.Equal((100, Frame - 100), (counters.CapturedSamples, counters.FilledSilenceSamples));
        Assert.True(counters.Reconciles);
    }
}
