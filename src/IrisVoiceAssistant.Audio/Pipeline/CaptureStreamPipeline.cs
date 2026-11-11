using System.Diagnostics;
using System.Threading.Channels;
using IrisVoiceAssistant.Audio.Conversion;
using IrisVoiceAssistant.Core.Audio;

namespace IrisVoiceAssistant.Audio.Pipeline;

/// <summary>
/// Turns one stream's native audio into canonical 20 ms frames (ADR-007) in a bounded queue (ADR-009).
/// </summary>
/// <remarks>
/// <para>Timeline. The stream position is a sample count: every produced sample advances it by one. The capture
/// timestamp (Stopwatch/QPC) is only used to find holes: when the device delivers nothing (loopback while nothing
/// plays) the stream is filled with silence up to <c>now - silenceFillDelay</c>, so both streams keep advancing
/// together. Real samples are never dropped to correct clock drift; drift is measured instead.</para>
/// <para>Holes after a device glitch are filled with silence and recorded as <see cref="AudioGapCause.DeviceDiscontinuity"/>.
/// Pauses and device loss move the position forward without producing frames and are recorded as gaps.</para>
/// <para>Thread safety: producer methods may be called from the capture thread and control methods from any thread;
/// one reader consumes <see cref="ReadAllAsync"/>.</para>
/// </remarks>
public sealed class CaptureStreamPipeline : IRawAudioSink
{
    /// <summary>Overflow drops within this distance of the previous one belong to the same episode (1 s).</summary>
    private const long OverflowEpisodeSamples = CanonicalAudioFormat.SampleRate;

    private readonly object _gate = new();
    private readonly AudioStreamKind _stream;
    private readonly long _sessionStart;
    private readonly long _holeThresholdSamples;
    private readonly long _fillDelaySamples;
    private readonly Channel<AudioFrame> _queue;
    private readonly List<AudioGap> _gaps = [];
    private readonly List<float> _resampled = new(4096);
    private readonly float[] _pending = new float[CanonicalAudioFormat.SamplesPerFrame];

    private PcmDecoder _decoder;
    private StreamingResampler _resampler;
    private float[] _mono = new float[4096];
    private int _pendingCount;
    private AudioFrameMarkers _pendingFlags;
    private bool _pendingHasCaptured;
    private long _framedPosition;
    private long _sequence;
    private long _captured;
    private long _filled;
    private long _processed;
    private long _dropped;
    private long _notCaptured;
    private long _maxDriftSamples;
    private long? _pauseStarted;
    private bool _aligned;

    public CaptureStreamPipeline(
        AudioStreamKind stream,
        RawAudioFormat format,
        long sessionStartTimestamp,
        int queueCapacityFrames = 1500,
        TimeSpan? holeThreshold = null,
        TimeSpan? silenceFillDelay = null)
    {
        ArgumentNullException.ThrowIfNull(format);
        ArgumentOutOfRangeException.ThrowIfLessThan(queueCapacityFrames, 1);
        _stream = stream;
        _sessionStart = sessionStartTimestamp;
        _holeThresholdSamples = CanonicalAudioFormat.ToSamples(holeThreshold ?? TimeSpan.FromMilliseconds(100));
        _fillDelaySamples = CanonicalAudioFormat.ToSamples(silenceFillDelay ?? TimeSpan.FromMilliseconds(250));
        _decoder = new PcmDecoder(format);
        _resampler = new StreamingResampler(format.SampleRate, CanonicalAudioFormat.SampleRate);
        Format = format;
        QueueCapacityFrames = queueCapacityFrames;
        _queue = Channel.CreateBounded<AudioFrame>(
            new BoundedChannelOptions(queueCapacityFrames)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            },
            OnFrameDropped);
    }

    /// <summary>Raised (outside internal locks) when a new gap is recorded or an existing one grows.</summary>
    public event Action<AudioGap>? GapRecorded;

    public AudioStreamKind Stream => _stream;

    public RawAudioFormat Format { get; private set; }

    public int QueueCapacityFrames { get; }

    /// <summary>Canonical samples produced so far, including the partial frame still being assembled.</summary>
    public long Position
    {
        get
        {
            lock (_gate)
            {
                return _framedPosition + _pendingCount;
            }
        }
    }

    /// <summary>Largest observed difference between the sample clock and the capture timestamps.</summary>
    public TimeSpan MaxClockDrift
    {
        get
        {
            lock (_gate)
            {
                return CanonicalAudioFormat.ToTimeSpan(_maxDriftSamples);
            }
        }
    }

    public IReadOnlyList<AudioGap> Gaps
    {
        get
        {
            lock (_gate)
            {
                return [.. _gaps];
            }
        }
    }

    public AudioStreamCounters Counters
    {
        get
        {
            lock (_gate)
            {
                return new AudioStreamCounters(
                    _stream,
                    _captured,
                    _filled,
                    Interlocked.Read(ref _processed),
                    _dropped,
                    ((long)_queue.Reader.Count * CanonicalAudioFormat.SamplesPerFrame) + _pendingCount,
                    _notCaptured,
                    _gaps.Count);
            }
        }
    }

    /// <summary>Reads frames in order; each frame read counts as processed.</summary>
    public async IAsyncEnumerable<AudioFrame> ReadAllAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (AudioFrame frame in _queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            Interlocked.Add(ref _processed, frame.Samples.Length);
            yield return frame;
        }
    }

    public bool TryRead(out AudioFrame? frame)
    {
        if (_queue.Reader.TryRead(out frame))
        {
            Interlocked.Add(ref _processed, frame.Samples.Length);
            return true;
        }

        return false;
    }

    /// <summary>Switches to a new native format (e.g. another device after recovery), keeping the timeline.</summary>
    public void Reconfigure(RawAudioFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        lock (_gate)
        {
            if (format == Format)
            {
                return;
            }

            _decoder = new PcmDecoder(format);
            _resampler = new StreamingResampler(format.SampleRate, CanonicalAudioFormat.SampleRate);
            Format = format;
        }
    }

    public void OnAudio(ReadOnlySpan<byte> data, int frameCount, long timestamp, RawBlockMarkers flags)
    {
        List<AudioGap> changed = [];
        lock (_gate)
        {
            if (_pauseStarted is not null)
            {
                return; // late packet after pause: the pause gap covers it
            }

            long expected = ExpectedPosition(timestamp);
            long hole = expected - Produced;
            if (!_aligned)
            {
                // First packet (or first after a skip): place it exactly at its capture time, so streams that
                // start a few tens of milliseconds apart stay aligned. Start-up latency is not drift.
                if (hole > 0)
                {
                    AppendSilence(hole);
                }

                _aligned = true;
            }
            else if (hole > _holeThresholdSamples)
            {
                long start = Produced;
                AppendSilence(hole);
                if (flags.HasFlag(RawBlockMarkers.Discontinuity))
                {
                    RecordGap(start, hole, AudioGapCause.DeviceDiscontinuity, changed);
                    _notCaptured += hole;
                }
            }
            else
            {
                _maxDriftSamples = Math.Max(_maxDriftSamples, Math.Abs(hole));
            }

            if (frameCount > 0)
            {
                if (_mono.Length < frameCount)
                {
                    _mono = new float[Math.Max(frameCount, _mono.Length * 2)];
                }

                Span<float> mono = _mono.AsSpan(0, frameCount);
                if (flags.HasFlag(RawBlockMarkers.Silent))
                {
                    mono.Clear();
                }
                else
                {
                    _decoder.DecodeMono(data, frameCount, mono);
                }

                _resampled.Clear();
                _resampler.Process(mono, _resampled);
                AudioFrameMarkers extra = (flags.HasFlag(RawBlockMarkers.Silent) ? AudioFrameMarkers.DeviceSilent : 0)
                    | (flags.HasFlag(RawBlockMarkers.Discontinuity) ? AudioFrameMarkers.AfterDiscontinuity : 0);
                Append(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_resampled), extra, captured: true);
            }
        }

        Publish(changed);
    }

    public void OnIdle(long timestamp)
    {
        lock (_gate)
        {
            if (_pauseStarted is not null)
            {
                return;
            }

            long target = ExpectedPosition(timestamp) - _fillDelaySamples;
            long missing = target - Produced;
            if (missing > 0)
            {
                AppendSilence(missing);
            }
        }
    }

    /// <summary>Stops producing until <see cref="Resume"/>; the paused span becomes a <see cref="AudioGapCause.Paused"/> gap.</summary>
    public void Pause(long timestamp)
    {
        lock (_gate)
        {
            if (_pauseStarted is null)
            {
                FlushPartialFrame();
                _pauseStarted = timestamp;
            }
        }
    }

    public void Resume(long timestamp) => SkipTo(timestamp, AudioGapCause.Paused, endPause: true);

    /// <summary>
    /// Records that nothing was captured from the current position until <paramref name="timestamp"/>
    /// (device lost and recovered), moving the position forward.
    /// </summary>
    public void MarkNotCaptured(long timestamp, AudioGapCause cause) => SkipTo(timestamp, cause, endPause: false);

    /// <summary>Emits the partial frame (padded with silence) and completes the queue.</summary>
    public void Complete()
    {
        lock (_gate)
        {
            FlushPartialFrame();
        }

        _queue.Writer.TryComplete();
    }

    private long Produced => _framedPosition + _pendingCount;

    private long ExpectedPosition(long timestamp)
    {
        long elapsedTicks = Math.Max(0, timestamp - _sessionStart);
        return (long)((double)elapsedTicks / Stopwatch.Frequency * CanonicalAudioFormat.SampleRate);
    }

    private void SkipTo(long timestamp, AudioGapCause cause, bool endPause)
    {
        List<AudioGap> changed = [];
        lock (_gate)
        {
            if (endPause && _pauseStarted is null)
            {
                return;
            }

            FlushPartialFrame();
            long target = ExpectedPosition(timestamp);
            long skip = target - _framedPosition;
            if (skip > 0)
            {
                RecordGap(_framedPosition, skip, cause, changed);
                _framedPosition = target;
                _notCaptured += skip;
                _resampler = new StreamingResampler(Format.SampleRate, CanonicalAudioFormat.SampleRate); // no history across the gap
            }

            _aligned = false; // the next packet re-aligns to its capture time

            if (endPause)
            {
                _pauseStarted = null;
            }
        }

        Publish(changed);
    }

    private void AppendSilence(long samples)
    {
        Span<float> zeros = stackalloc float[CanonicalAudioFormat.SamplesPerFrame];
        while (samples > 0)
        {
            int n = (int)Math.Min(samples, zeros.Length);
            Append(zeros[..n], AudioFrameMarkers.FilledSilence, captured: false);
            samples -= n;
        }
    }

    private void Append(ReadOnlySpan<float> samples, AudioFrameMarkers flags, bool captured)
    {
        if (captured)
        {
            _captured += samples.Length;
        }
        else
        {
            _filled += samples.Length;
        }

        while (!samples.IsEmpty)
        {
            int n = Math.Min(samples.Length, _pending.Length - _pendingCount);
            samples[..n].CopyTo(_pending.AsSpan(_pendingCount));
            _pendingCount += n;
            samples = samples[n..];
            if (captured)
            {
                _pendingHasCaptured = true;
            }

            _pendingFlags |= flags;
            if (_pendingCount == _pending.Length)
            {
                EmitFrame();
            }
        }
    }

    private void FlushPartialFrame()
    {
        if (_pendingCount == 0)
        {
            return;
        }

        int missing = _pending.Length - _pendingCount;
        _pending.AsSpan(_pendingCount).Clear();
        _filled += missing;
        _pendingFlags |= AudioFrameMarkers.FilledSilence;
        _pendingCount = _pending.Length;
        EmitFrame();
    }

    private void EmitFrame()
    {
        AudioFrameMarkers flags = _pendingFlags;
        if (_pendingHasCaptured)
        {
            flags &= ~AudioFrameMarkers.FilledSilence;
            if (_pendingFlags.HasFlag(AudioFrameMarkers.FilledSilence))
            {
                flags |= AudioFrameMarkers.FilledSilence; // partially filled
            }
        }

        var frame = new AudioFrame(_stream, _sequence++, _framedPosition, _pending.ToArray(), flags);
        _framedPosition += _pending.Length;
        _pendingCount = 0;
        _pendingFlags = AudioFrameMarkers.None;
        _pendingHasCaptured = false;
        _queue.Writer.TryWrite(frame); // never blocks: DropOldest evicts and reports through OnFrameDropped
    }

    private void OnFrameDropped(AudioFrame frame)
    {
        // Invoked synchronously from TryWrite, i.e. while _gate is held by the producer.
        _dropped += frame.Samples.Length;
        List<AudioGap> changed = [];
        RecordGap(frame.StreamPosition, frame.Samples.Length, AudioGapCause.Overflow, changed);
        ThreadPool.UnsafeQueueUserWorkItem(_ => Publish(changed), null);
    }

    private void RecordGap(long start, long length, AudioGapCause cause, List<AudioGap> changed)
    {
        int last = _gaps.FindLastIndex(g => g.Cause == cause);
        if (last >= 0)
        {
            AudioGap previous = _gaps[last];
            long end = previous.StartPosition + previous.LengthSamples;
            long allowed = cause == AudioGapCause.Overflow ? OverflowEpisodeSamples : 0;
            if (start >= end && start - end <= allowed)
            {
                AudioGap merged = previous with
                {
                    LengthSamples = start + length - previous.StartPosition,
                    MissingSamples = previous.MissingSamples + length,
                };
                _gaps[last] = merged;
                changed.Add(merged);
                return;
            }
        }

        var gap = new AudioGap(_stream, start, length, cause);
        _gaps.Add(gap);
        changed.Add(gap);
    }

    private void Publish(List<AudioGap> changed)
    {
        foreach (AudioGap gap in changed)
        {
            GapRecorded?.Invoke(gap);
        }
    }
}
