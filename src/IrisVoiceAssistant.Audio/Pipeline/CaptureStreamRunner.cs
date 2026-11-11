using System.Diagnostics;
using IrisVoiceAssistant.Core.Audio;

namespace IrisVoiceAssistant.Audio.Pipeline;

/// <summary>
/// Runs one capture stream on a dedicated thread with the AUDIO-ENGINE.md states:
/// Starting → Capturing ⇄ Paused, Capturing → Recovering (device lost, retried) → Capturing, and Faulted on any
/// other error. Pausing releases the device, so the OS capture indicator turns off.
/// </summary>
public sealed class CaptureStreamRunner : IDisposable
{
    private readonly Func<IAudioCaptureSource> _sourceFactory;
    private readonly long _sessionStart;
    private readonly TimeSpan _retryDelay;
    private readonly int _queueCapacityFrames;
    private readonly object _gate = new();
    private readonly ManualResetEventSlim _resumed = new(initialState: true);
    private readonly CancellationTokenSource _stopping = new();

    private IAudioCaptureSource? _source;
    private CancellationTokenSource? _run;
    private Thread? _thread;
    private bool _recovering;
    private bool _disposed;

    public CaptureStreamRunner(
        AudioStreamKind stream,
        Func<IAudioCaptureSource> sourceFactory,
        long sessionStartTimestamp,
        TimeSpan? retryDelay = null,
        int queueCapacityFrames = 1500)
    {
        ArgumentNullException.ThrowIfNull(sourceFactory);
        Stream = stream;
        _sourceFactory = sourceFactory;
        _sessionStart = sessionStartTimestamp;
        _retryDelay = retryDelay ?? TimeSpan.FromSeconds(1);
        _queueCapacityFrames = queueCapacityFrames;
    }

    public event Action<AudioStreamKind, AudioCaptureState, string?>? StateChanged;

    public AudioStreamKind Stream { get; }

    public AudioCaptureState State { get; private set; } = AudioCaptureState.Stopped;

    /// <summary>Why the stream is Recovering or Faulted.</summary>
    public string? LastError { get; private set; }

    public string? SourceDescription { get; private set; }

    /// <summary>Available after <see cref="Start"/>.</summary>
    public CaptureStreamPipeline Pipeline { get; private set; } = null!;

    /// <summary>Opens the device (throwing if it cannot be opened now) and starts capturing.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_thread is not null)
        {
            throw new InvalidOperationException("Already started.");
        }

        SetState(AudioCaptureState.Starting, null);
        try
        {
            _source = _sourceFactory();
        }
        catch (Exception error)
        {
            SetState(AudioCaptureState.Faulted, error.Message);
            throw;
        }

        SourceDescription = _source.Description;
        Pipeline = new CaptureStreamPipeline(Stream, _source.Format, _sessionStart, _queueCapacityFrames);
        _thread = new Thread(Loop) { IsBackground = true, Name = $"capture-{Stream}", Priority = ThreadPriority.Highest };
        _thread.Start();
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (State is not (AudioCaptureState.Capturing or AudioCaptureState.Recovering))
            {
                return;
            }

            Pipeline.Pause(Stopwatch.GetTimestamp());
            _resumed.Reset();
            _run?.Cancel();
            SetState(AudioCaptureState.Paused, null);
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (State != AudioCaptureState.Paused)
            {
                return;
            }

            Pipeline.Resume(Stopwatch.GetTimestamp());
            _resumed.Set();
        }
    }

    /// <summary>Stops capturing, releases the device and completes the frame queue.</summary>
    public void Stop()
    {
        if (_thread is null)
        {
            return;
        }

        _stopping.Cancel();
        lock (_gate)
        {
            _run?.Cancel();
        }

        _resumed.Set();
        _thread.Join();
        _thread = null;
        if (_recovering)
        {
            // Stopped before the device came back: the lost span until now must still be on the timeline.
            Pipeline.MarkNotCaptured(Stopwatch.GetTimestamp(), AudioGapCause.DeviceLost);
            _recovering = false;
        }

        Pipeline.Complete();
        SetState(AudioCaptureState.Stopped, null);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _source?.Dispose();
        _resumed.Dispose();
        _stopping.Dispose();
        _disposed = true;
    }

    private void Loop()
    {
        CancellationToken stopping = _stopping.Token;
        while (!stopping.IsCancellationRequested)
        {
            if (!_resumed.IsSet)
            {
                ReleaseSource();
                try
                {
                    _resumed.Wait(stopping);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                continue;
            }

            try
            {
                if (_source is null)
                {
                    _source = _sourceFactory();
                    SourceDescription = _source.Description;
                    Pipeline.Reconfigure(_source.Format);
                }

                if (_recovering)
                {
                    Pipeline.MarkNotCaptured(Stopwatch.GetTimestamp(), AudioGapCause.DeviceLost);
                    _recovering = false;
                }

                CancellationToken run;
                lock (_gate)
                {
                    if (!_resumed.IsSet)
                    {
                        continue;
                    }

                    _run = CancellationTokenSource.CreateLinkedTokenSource(stopping);
                    run = _run.Token;
                    SetState(AudioCaptureState.Capturing, null);
                }

                _source.Run(Pipeline, run);
            }
            catch (AudioDeviceLostException error)
            {
                ReleaseSource();
                if (!_recovering)
                {
                    _recovering = true;
                }

                lock (_gate)
                {
                    if (State != AudioCaptureState.Paused)
                    {
                        SetState(AudioCaptureState.Recovering, error.Message);
                    }
                }

                stopping.WaitHandle.WaitOne(_retryDelay);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested || !_resumed.IsSet)
            {
                // stop or pause
            }
            catch (Exception error)
            {
                ReleaseSource();
                SetState(AudioCaptureState.Faulted, $"{error.GetType().Name}: {error.Message}");
                return;
            }
            finally
            {
                lock (_gate)
                {
                    _run?.Dispose();
                    _run = null;
                }
            }
        }

        ReleaseSource();
    }

    private void ReleaseSource()
    {
        _source?.Dispose();
        _source = null;
    }

    private void SetState(AudioCaptureState state, string? error)
    {
        State = state;
        LastError = error;
        StateChanged?.Invoke(Stream, state, error);
    }
}
