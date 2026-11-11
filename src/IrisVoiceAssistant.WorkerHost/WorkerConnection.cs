using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using IrisVoiceAssistant.AI.Contracts;
using IrisVoiceAssistant.Core.Audio;

namespace IrisVoiceAssistant.WorkerHost;

/// <summary>
/// One running worker process: hello handshake, ping/pong, requests with cancellation and audio frames over stdio.
/// <see cref="Completion"/> finishes (with the reason) when the process, the protocol or the health check fails.
/// </summary>
internal sealed class WorkerConnection : IAsyncDisposable
{
    private readonly WorkerOptions _options;
    private readonly Process _process;
    private readonly Stream _stdin;
    private readonly Stream _stdout;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly CancellationTokenSource _closing = new();
    private readonly TaskCompletionSource<string> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentQueue<string> _stderrTail = new();
    private long _nextId;
    private long _lastPong;
    private int _disposed;

    private WorkerConnection(WorkerOptions options, Process process)
    {
        _options = options;
        _process = process;
        _stdin = process.StandardInput.BaseStream;
        _stdout = process.StandardOutput.BaseStream;
        _lastPong = Stopwatch.GetTimestamp();
    }

    public event Action<AudioFrame>? AudioReceived;

    public WorkerHello Hello { get; private set; } = null!;

    public int ProcessId => _process.Id;

    /// <summary>Completes with a human-readable reason when the connection ends for any reason.</summary>
    public Task<string> Completion => _completion.Task;

    public string StderrTail => string.Join(" | ", _stderrTail);

    public static async Task<WorkerConnection> StartAsync(WorkerOptions options, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(options.FileName)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = options.WorkingDirectory ?? string.Empty,
        };
        foreach (string argument in options.Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        foreach (string variable in new[] { "PYTHONPATH", "PYTHONHOME", "PYTHONSTARTUP" })
        {
            info.Environment.Remove(variable); // the worker environment is the venv, nothing injected
        }

        Process process = Process.Start(info) ?? throw new WorkerUnavailableException($"{options.Name}: failed to start {options.FileName}");
        var connection = new WorkerConnection(options, process);
        process.ErrorDataReceived += (_, e) => connection.OnStderr(e.Data);
        process.BeginErrorReadLine();
        try
        {
            await connection.HandshakeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        _ = Task.Run(connection.ReadLoopAsync, CancellationToken.None);
        _ = Task.Run(connection.PingLoopAsync, CancellationToken.None);
        _ = connection.WatchExitAsync();
        return connection;
    }

    public async Task<JsonNode?> RequestAsync(string method, JsonNode? parameters, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(method);
        if (_completion.Task.IsCompleted)
        {
            throw new WorkerUnavailableException($"{_options.Name}: {_completion.Task.Result}");
        }

        long id = Interlocked.Increment(ref _nextId);
        var pending = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = pending;
        try
        {
            await SendAsync(WorkerProtocol.EncodeControl(new JsonObject
            {
                ["type"] = "request",
                ["id"] = id,
                ["method"] = method,
                ["params"] = parameters?.DeepClone(),
            }), cancellationToken).ConfigureAwait(false);

            using CancellationTokenRegistration registration = cancellationToken.Register(() =>
            {
                if (_pending.TryRemove(id, out TaskCompletionSource<JsonNode?>? cancelled))
                {
                    cancelled.TrySetCanceled(cancellationToken); // late results for this id are ignored
                    _ = SendQuietlyAsync(WorkerProtocol.EncodeControl(new JsonObject { ["type"] = "cancel", ["id"] = id }));
                }
            });
            return await pending.Task.ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public Task SendAudioAsync(AudioFrame frame, CancellationToken cancellationToken)
    {
        if (_completion.Task.IsCompleted)
        {
            throw new WorkerUnavailableException($"{_options.Name}: {_completion.Task.Result}");
        }

        return SendAsync(WorkerProtocol.EncodeAudio(frame), cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await SendQuietlyAsync(WorkerProtocol.EncodeControl(new JsonObject { ["type"] = "shutdown" })).ConfigureAwait(false);
        try
        {
            _stdin.Close(); // end of stdin also stops the worker
        }
        catch (IOException)
        {
        }

        using var grace = new CancellationTokenSource(_options.ShutdownGrace);
        try
        {
            await _process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillTree();
        }

        Fail("stopped");
        await _closing.CancelAsync().ConfigureAwait(false);
        _process.Dispose();
        _writeLock.Dispose();
        _closing.Dispose();
    }

    private async Task HandshakeAsync(CancellationToken cancellationToken)
    {
        // The redirected stdio handles are synchronous on Windows: a pending read ignores cancellation, so the
        // timeout kills the process, which closes the pipe and ends the read.
        Task<WorkerMessage?> read = WorkerProtocol.ReadAsync(_stdout, CancellationToken.None).AsTask();
        Task finished = await Task.WhenAny(read, Task.Delay(_options.HelloTimeout, cancellationToken)).ConfigureAwait(false);
        if (finished != read)
        {
            KillTree();
            _ = read.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default); // observe the read's failure
            cancellationToken.ThrowIfCancellationRequested();
            throw new WorkerUnavailableException($"{_options.Name}: no hello within {_options.HelloTimeout.TotalSeconds:F0} s. {StderrTail}");
        }

        WorkerMessage? first;
        try
        {
            first = await read.ConfigureAwait(false);
        }
        catch (WorkerProtocolException error)
        {
            throw new WorkerUnavailableException($"{_options.Name}: invalid hello: {error.Message}. {StderrTail}", error);
        }
        catch (Exception error) when (error is IOException or EndOfStreamException)
        {
            throw new WorkerUnavailableException($"{_options.Name}: worker ended before hello. {StderrTail}", error);
        }

        if (first is not ControlMessage { Type: "hello" } hello)
        {
            throw new WorkerUnavailableException($"{_options.Name}: expected hello, got {first?.GetType().Name ?? "end of stream"}. {StderrTail}");
        }

        int protocol = hello.Body["protocol"]?.GetValueKind() == JsonValueKind.Number ? (int)hello.Body["protocol"]! : -1;
        if (protocol != WorkerProtocol.Version)
        {
            throw new WorkerUnavailableException($"{_options.Name}: protocol {protocol} not supported (host speaks {WorkerProtocol.Version})");
        }

        string[] capabilities = hello.Body["capabilities"] is JsonArray array
            ? [.. array.Select(c => c?.ToString() ?? string.Empty)]
            : [];
        Hello = new WorkerHello((string?)hello.Body["worker"] ?? "?", (string?)hello.Body["version"] ?? "?", protocol, capabilities);
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (true)
            {
                WorkerMessage? message = await WorkerProtocol.ReadAsync(_stdout, _closing.Token).ConfigureAwait(false);
                switch (message)
                {
                    case null:
                        Fail($"{ExitReason("worker closed its output")}. {StderrTail}");
                        return;
                    case AudioMessage audio:
                        AudioReceived?.Invoke(audio.Frame);
                        break;
                    case ControlMessage control:
                        OnControl(control);
                        break;
                }
            }
        }
        catch (WorkerProtocolException error)
        {
            Fail($"protocol violation: {error.Message}");
            KillTree();
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException)
        {
            Fail($"{ExitReason($"connection closed: {error.Message}")}. {StderrTail}");
        }
    }

    /// <summary>End of stdout usually means the process is exiting: report its exit code when it comes quickly.</summary>
    private string ExitReason(string fallback)
    {
        try
        {
            return _process.WaitForExit(1000) ? $"process exited with code {_process.ExitCode}" : fallback;
        }
        catch (InvalidOperationException)
        {
            return fallback;
        }
    }

    private void OnControl(ControlMessage control)
    {
        switch (control.Type)
        {
            case "pong":
                Interlocked.Exchange(ref _lastPong, Stopwatch.GetTimestamp());
                break;
            case "result" when control.Id is long id && _pending.TryRemove(id, out TaskCompletionSource<JsonNode?>? pending):
                pending.TrySetResult(control.Body["data"]?.DeepClone());
                break;
            case "error" when control.Id is long id && _pending.TryRemove(id, out TaskCompletionSource<JsonNode?>? pending):
                pending.TrySetException(new WorkerRequestException((string?)control.Body["code"] ?? "error", (string?)control.Body["message"] ?? string.Empty));
                break;
            case "cancelled" when control.Id is long id && _pending.TryRemove(id, out TaskCompletionSource<JsonNode?>? pending):
                pending.TrySetCanceled();
                break;
            default:
                break; // late answers to cancelled requests, or messages this host does not use
        }
    }

    private async Task PingLoopAsync()
    {
        long seq = 0;
        using var timer = new PeriodicTimer(_options.PingInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(_closing.Token).ConfigureAwait(false))
            {
                TimeSpan silent = Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastPong));
                if (silent > _options.PingTimeout)
                {
                    Fail($"unresponsive: no pong for {silent.TotalSeconds:F1} s");
                    KillTree();
                    return;
                }

                await SendQuietlyAsync(WorkerProtocol.EncodeControl(new JsonObject { ["type"] = "ping", ["seq"] = ++seq })).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task WatchExitAsync()
    {
        try
        {
            await _process.WaitForExitAsync(_closing.Token).ConfigureAwait(false);
            Fail($"process exited with code {_process.ExitCode}. {StderrTail}");
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task SendAsync(byte[] message, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stdin.WriteAsync(message, cancellationToken).ConfigureAwait(false);
            await _stdin.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            Fail($"cannot write to worker: {error.Message}");
            throw new WorkerUnavailableException($"{_options.Name}: {error.Message}", error);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task SendQuietlyAsync(byte[] message)
    {
        try
        {
            await SendAsync(message, _closing.IsCancellationRequested ? CancellationToken.None : _closing.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is WorkerUnavailableException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    private void Fail(string reason)
    {
        if (!_completion.TrySetResult(reason))
        {
            return;
        }

        foreach (long id in _pending.Keys)
        {
            if (_pending.TryRemove(id, out TaskCompletionSource<JsonNode?>? pending))
            {
                pending.TrySetException(new WorkerUnavailableException($"{_options.Name}: {reason}"));
            }
        }
    }

    private void KillTree()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private void OnStderr(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        _stderrTail.Enqueue(line.Length > 300 ? line[..300] : line);
        while (_stderrTail.Count > 10)
        {
            _stderrTail.TryDequeue(out _);
        }
    }
}
