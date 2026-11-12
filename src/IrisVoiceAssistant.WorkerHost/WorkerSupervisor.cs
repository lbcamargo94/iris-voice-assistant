using System.Diagnostics;
using System.Text.Json.Nodes;
using IrisVoiceAssistant.AI.Contracts;
using IrisVoiceAssistant.Core.Audio;

namespace IrisVoiceAssistant.WorkerHost;

/// <summary>
/// Keeps one worker alive (ADR-014): Starting → Ready; on exit, protocol violation or missed pings it restarts
/// with exponential backoff (Restarting), and after <see cref="WorkerOptions.MaxFailures"/> failures within
/// <see cref="WorkerOptions.FailureWindow"/> it stops trying (Faulted). Calls made while the worker is not Ready
/// fail fast with <see cref="WorkerUnavailableException"/>; callers keep their own bounded buffers (ADR-009).
/// </summary>
public sealed class WorkerSupervisor : IAsyncDisposable
{
    private readonly WorkerOptions _options;
    private readonly object _gate = new();
    private readonly Queue<long> _failures = new();
    private readonly CancellationTokenSource _stopping = new();
    private WorkerConnection? _connection;
    private Task? _supervision;
    private int _restarts;

    public WorkerSupervisor(WorkerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    public event Action<WorkerState, string?>? StateChanged;

    /// <summary>Audio frames sent by the worker (e.g. synthesized speech).</summary>
    public event Action<AudioFrame>? AudioReceived;

    /// <summary>Events the worker sends on its own (e.g. <c>status</c>, <c>stt.final</c>), on the reader thread, in order.</summary>
    public event Action<ControlMessage>? ControlReceived;

    public string Name => _options.Name;

    public WorkerState State { get; private set; } = WorkerState.Stopped;

    /// <summary>Why the last connection ended, or why the worker is Faulted.</summary>
    public string? LastError { get; private set; }

    public WorkerHello? Hello { get; private set; }

    public int Restarts => Volatile.Read(ref _restarts);

    public int? ProcessId
    {
        get
        {
            lock (_gate)
            {
                return _connection?.ProcessId;
            }
        }
    }

    /// <summary>Starts the worker and waits until it is Ready, or until it ends Faulted after its retries.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_supervision is not null)
        {
            throw new InvalidOperationException("Already started.");
        }

        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnState(WorkerState state, string? _)
        {
            if (state is WorkerState.Ready or WorkerState.Faulted)
            {
                ready.TrySetResult();
            }
        }

        StateChanged += OnState;
        try
        {
            _supervision = Task.Run(() => SuperviseAsync(_stopping.Token), CancellationToken.None);
            await ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            StateChanged -= OnState;
        }

        if (State == WorkerState.Faulted)
        {
            throw new WorkerUnavailableException($"{Name} faulted: {LastError}");
        }
    }

    public Task<JsonNode?> RequestAsync(string method, JsonNode? parameters = null, CancellationToken cancellationToken = default) =>
        Current().RequestAsync(method, parameters, cancellationToken);

    public Task SendAudioAsync(AudioFrame frame, CancellationToken cancellationToken = default) =>
        Current().SendAudioAsync(frame, cancellationToken);

    /// <summary>Asks the worker to shut down, kills it after the grace period, and stops supervising.</summary>
    public async Task StopAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        WorkerConnection? connection;
        lock (_gate)
        {
            connection = _connection;
            _connection = null;
        }

        if (connection is not null)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        if (_supervision is not null)
        {
            await _supervision.ConfigureAwait(false);
        }

        SetState(WorkerState.Stopped, null);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _stopping.Dispose();
    }

    private WorkerConnection Current()
    {
        lock (_gate)
        {
            if (State == WorkerState.Ready && _connection is not null)
            {
                return _connection;
            }
        }

        throw new WorkerUnavailableException($"{Name} is {State}{(LastError is null ? "" : $": {LastError}")}");
    }

    private async Task SuperviseAsync(CancellationToken stopping)
    {
        TimeSpan backoff = _options.InitialBackoff;
        bool first = true;
        while (!stopping.IsCancellationRequested)
        {
            SetState(first ? WorkerState.Starting : WorkerState.Restarting, LastError);
            WorkerConnection? connection = null;
            try
            {
                connection = await WorkerConnection.StartAsync(_options, stopping).ConfigureAwait(false);
                connection.AudioReceived += frame => AudioReceived?.Invoke(frame);
                connection.ControlReceived += message => ControlReceived?.Invoke(message);
                lock (_gate)
                {
                    _connection = connection;
                    Hello = connection.Hello;
                }

                if (!first)
                {
                    Interlocked.Increment(ref _restarts);
                }

                SetState(WorkerState.Ready, null);
                backoff = _options.InitialBackoff; // a good start resets the backoff
                LastError = await connection.Completion.WaitAsync(stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
            catch (WorkerUnavailableException error)
            {
                LastError = error.Message;
            }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                LastError = $"cannot start {_options.FileName}: {error.Message}";
            }

            lock (_gate)
            {
                if (ReferenceEquals(_connection, connection))
                {
                    _connection = null;
                }
            }

            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }

            if (stopping.IsCancellationRequested)
            {
                return;
            }

            if (RecordFailure())
            {
                SetState(WorkerState.Faulted, $"{LastError} ({_options.MaxFailures} failures within {_options.FailureWindow.TotalSeconds:F0} s)");
                return;
            }

            first = false;
            SetState(WorkerState.Restarting, LastError);
            try
            {
                await Task.Delay(backoff, stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, _options.MaxBackoff.Ticks));
        }
    }

    /// <summary>Records a failure; true when the failure budget for the window is exhausted.</summary>
    private bool RecordFailure()
    {
        long now = Stopwatch.GetTimestamp();
        _failures.Enqueue(now);
        while (_failures.Count > 0 && Stopwatch.GetElapsedTime(_failures.Peek(), now) > _options.FailureWindow)
        {
            _failures.Dequeue();
        }

        return _failures.Count >= _options.MaxFailures;
    }

    private void SetState(WorkerState state, string? reason)
    {
        lock (_gate)
        {
            if (State == state && state != WorkerState.Restarting)
            {
                return;
            }

            State = state;
            if (state == WorkerState.Faulted)
            {
                LastError = reason;
            }
        }

        StateChanged?.Invoke(state, reason);
    }
}
