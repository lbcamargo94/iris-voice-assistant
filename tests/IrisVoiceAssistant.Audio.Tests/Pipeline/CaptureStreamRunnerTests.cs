using System.Collections.Concurrent;
using System.Diagnostics;
using IrisVoiceAssistant.Audio.Pipeline;
using IrisVoiceAssistant.Core.Audio;

namespace IrisVoiceAssistant.Audio.Tests.Pipeline;

public sealed class CaptureStreamRunnerTests
{
    private sealed class FakeSource(Action<FakeSource, IRawAudioSink, CancellationToken> body) : IAudioCaptureSource
    {
        public AudioStreamKind Stream => AudioStreamKind.Microphone;

        public RawAudioFormat Format { get; } = new(16_000, 1, RawSampleType.IeeeFloat);

        public string Description => "fake";

        public void Run(IRawAudioSink sink, CancellationToken cancellationToken) => body(this, sink, cancellationToken);

        public void Dispose()
        {
        }

        /// <summary>Delivers 10 ms blocks in real time until cancelled.</summary>
        public static void Stream10ms(IRawAudioSink sink, CancellationToken token)
        {
            byte[] block = new byte[160 * 4];
            while (!token.IsCancellationRequested)
            {
                sink.OnAudio(block, 160, Stopwatch.GetTimestamp(), RawBlockMarkers.None);
                token.WaitHandle.WaitOne(10);
            }
        }
    }

    private static void WaitFor(Func<bool> condition, int timeoutMs = 5000)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.ElapsedMilliseconds < timeoutMs, "timed out waiting for condition");
            Thread.Sleep(10);
        }
    }

    [Fact]
    public void Captures_pauses_resumes_and_stops()
    {
        var states = new ConcurrentQueue<AudioCaptureState>();
        int opened = 0;
        using var runner = new CaptureStreamRunner(
            AudioStreamKind.Microphone,
            () =>
            {
                Interlocked.Increment(ref opened);
                return new FakeSource((_, sink, token) => FakeSource.Stream10ms(sink, token));
            },
            Stopwatch.GetTimestamp());
        runner.StateChanged += (_, state, _) => states.Enqueue(state);

        runner.Start();
        WaitFor(() => runner.Pipeline.Counters.CapturedSamples > 1_600);
        runner.Pause();
        Assert.Equal(AudioCaptureState.Paused, runner.State);
        Thread.Sleep(150);
        long whilePaused = runner.Pipeline.Counters.CapturedSamples;
        Thread.Sleep(150);
        Assert.Equal(whilePaused, runner.Pipeline.Counters.CapturedSamples);

        runner.Resume();
        WaitFor(() => runner.State == AudioCaptureState.Capturing && runner.Pipeline.Counters.CapturedSamples > whilePaused);
        runner.Stop();

        Assert.Equal(AudioCaptureState.Stopped, runner.State);
        Assert.Equal(2, opened); // the device is released while paused and reopened on resume
        AudioGap pause = Assert.Single(runner.Pipeline.Gaps);
        Assert.Equal(AudioGapCause.Paused, pause.Cause);
        Assert.InRange(pause.Duration.TotalMilliseconds, 250, 2000);
        Assert.Equal(
            [AudioCaptureState.Starting, AudioCaptureState.Capturing, AudioCaptureState.Paused, AudioCaptureState.Capturing, AudioCaptureState.Stopped],
            states.ToArray());
        Assert.True(runner.Pipeline.Counters.Reconciles);
    }

    [Fact]
    public void Device_loss_moves_to_recovering_and_back_with_a_gap()
    {
        int attempt = 0;
        using var runner = new CaptureStreamRunner(
            AudioStreamKind.Microphone,
            () =>
            {
                int n = Interlocked.Increment(ref attempt);
                if (n == 2)
                {
                    throw new AudioDeviceLostException("still unplugged");
                }

                return new FakeSource((_, sink, token) =>
                {
                    if (n == 1)
                    {
                        sink.OnAudio(new byte[160 * 4], 160, Stopwatch.GetTimestamp(), RawBlockMarkers.None);
                        throw new AudioDeviceLostException("unplugged");
                    }

                    FakeSource.Stream10ms(sink, token);
                });
            },
            Stopwatch.GetTimestamp(),
            retryDelay: TimeSpan.FromMilliseconds(100));
        var states = new ConcurrentQueue<AudioCaptureState>();
        runner.StateChanged += (_, state, _) => states.Enqueue(state);

        runner.Start();
        WaitFor(() => attempt >= 3 && runner.State == AudioCaptureState.Capturing);
        runner.Stop();

        Assert.Contains(AudioCaptureState.Recovering, states);
        AudioGap lost = Assert.Single(runner.Pipeline.Gaps, g => g.Cause == AudioGapCause.DeviceLost);
        Assert.InRange(lost.Duration.TotalMilliseconds, 100, 3000);
        Assert.True(runner.Pipeline.Counters.Reconciles);
    }

    [Fact]
    public void Stopping_while_recovering_still_records_the_lost_span()
    {
        int attempt = 0;
        using var runner = new CaptureStreamRunner(
            AudioStreamKind.Microphone,
            () =>
            {
                if (Interlocked.Increment(ref attempt) > 1)
                {
                    throw new AudioDeviceLostException("not found");
                }

                return new FakeSource((_, sink, _) =>
                {
                    sink.OnAudio(new byte[160 * 4], 160, Stopwatch.GetTimestamp(), RawBlockMarkers.None);
                    throw new AudioDeviceLostException("unplugged");
                });
            },
            Stopwatch.GetTimestamp(),
            retryDelay: TimeSpan.FromMilliseconds(50));

        runner.Start();
        WaitFor(() => attempt >= 3);
        Thread.Sleep(200);
        runner.Stop();

        AudioGap lost = Assert.Single(runner.Pipeline.Gaps);
        Assert.Equal(AudioGapCause.DeviceLost, lost.Cause);
        Assert.True(lost.Duration >= TimeSpan.FromMilliseconds(200), lost.Duration.ToString());
        Assert.True(runner.Pipeline.Counters.Reconciles);
    }

    [Fact]
    public void Unexpected_errors_fault_the_stream_with_the_reason()
    {
        using var runner = new CaptureStreamRunner(
            AudioStreamKind.Microphone,
            () => new FakeSource((_, _, _) => throw new InvalidOperationException("driver exploded")),
            Stopwatch.GetTimestamp());

        runner.Start();
        WaitFor(() => runner.State == AudioCaptureState.Faulted);
        Assert.Contains("driver exploded", runner.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public void A_device_that_cannot_be_opened_fails_start()
    {
        using var runner = new CaptureStreamRunner(
            AudioStreamKind.Microphone,
            () => throw new AudioDeviceLostException("no microphone"),
            Stopwatch.GetTimestamp());

        Assert.Throws<AudioDeviceLostException>(runner.Start);
        Assert.Equal(AudioCaptureState.Faulted, runner.State);
    }
}
