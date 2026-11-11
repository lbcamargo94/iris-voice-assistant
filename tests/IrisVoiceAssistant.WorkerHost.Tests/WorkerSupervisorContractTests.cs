using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using IrisVoiceAssistant.Core.Audio;

namespace IrisVoiceAssistant.WorkerHost.Tests;

/// <summary>Skips when the Python workers environment (workers/.venv) has not been synced.</summary>
public sealed class PythonWorkerFactAttribute : FactAttribute
{
    public PythonWorkerFactAttribute()
    {
        if (!File.Exists(ContractWorker.Python))
        {
            Skip = $"workers/.venv not found ({ContractWorker.Python}); run 'uv sync' in workers/.";
        }
    }
}

internal static class ContractWorker
{
    public static readonly string Repo = FindRepo();

    public static string Python => OperatingSystem.IsWindows()
        ? Path.Combine(Repo, "workers", ".venv", "Scripts", "python.exe")
        : Path.Combine(Repo, "workers", ".venv", "bin", "python");

    public static WorkerOptions Options(params string[] extra) => new()
    {
        Name = "contract-test",
        FileName = Python,
        Arguments = ["-I", "-m", "iris_workers.testing.contract_worker", .. extra],
        PingInterval = TimeSpan.FromMilliseconds(200),
        PingTimeout = TimeSpan.FromMilliseconds(1500),
        HelloTimeout = TimeSpan.FromSeconds(20),
        InitialBackoff = TimeSpan.FromMilliseconds(100),
        MaxBackoff = TimeSpan.FromMilliseconds(400),
        MaxFailures = 3,
        FailureWindow = TimeSpan.FromMinutes(1),
        ShutdownGrace = TimeSpan.FromSeconds(3),
    };

    public static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 15_000)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.ElapsedMilliseconds < timeoutMs, "timed out waiting for condition");
            await Task.Delay(20);
        }
    }

    private static string FindRepo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "IrisVoiceAssistant.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }
}

/// <summary>
/// Contract tests against a real Python worker (iris_workers.testing.contract_worker) over stdio (ADR-014):
/// handshake, requests, errors, cancellation, audio in both directions, crash/freeze/garbage/oversize recovery,
/// version mismatch, startup timeout and orderly shutdown.
/// </summary>
public sealed class WorkerSupervisorContractTests
{
    [PythonWorkerFact]
    public async Task Starts_announces_itself_and_answers_requests()
    {
        await using var worker = new WorkerSupervisor(ContractWorker.Options());
        await worker.StartAsync();

        Assert.Equal(WorkerState.Ready, worker.State);
        Assert.Equal("contract-test", worker.Hello!.Worker);
        Assert.Equal(1, worker.Hello.Protocol);
        Assert.Contains("echo", worker.Hello.Capabilities);

        JsonNode? echo = await worker.RequestAsync("echo", new JsonObject { ["text"] = "olá, Iris", ["n"] = 3 });
        Assert.Equal("olá, Iris", (string?)echo!["text"]);
        Assert.Equal(3, (int)echo["n"]!);
    }

    [PythonWorkerFact]
    public async Task Handler_errors_and_unknown_methods_are_typed_and_the_worker_keeps_serving()
    {
        await using var worker = new WorkerSupervisor(ContractWorker.Options());
        await worker.StartAsync();

        WorkerRequestException failed = await Assert.ThrowsAsync<WorkerRequestException>(() => worker.RequestAsync("fail"));
        Assert.Equal("handler_failed", failed.Code);
        WorkerRequestException unknown = await Assert.ThrowsAsync<WorkerRequestException>(() => worker.RequestAsync("does_not_exist"));
        Assert.Equal("unknown_method", unknown.Code);
        Assert.Equal("printed", (string?)await worker.RequestAsync("print")); // a stray print does not corrupt stdout
        Assert.Equal(0, worker.Restarts);
    }

    [PythonWorkerFact]
    public async Task Cancellation_returns_immediately_and_the_late_result_is_ignored()
    {
        await using var worker = new WorkerSupervisor(ContractWorker.Options());
        await worker.StartAsync();

        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.RequestAsync("sleep", new JsonObject { ["seconds"] = 30 }, cancel.Token));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), watch.Elapsed.ToString());

        Assert.NotNull(await worker.RequestAsync("echo", new JsonObject { ["after"] = "cancel" }));
        Assert.Equal(WorkerState.Ready, worker.State);
    }

    [PythonWorkerFact]
    public async Task Audio_frames_cross_the_boundary_in_order_and_bit_exact()
    {
        await using var worker = new WorkerSupervisor(ContractWorker.Options());
        var echoed = new ConcurrentQueue<AudioFrame>();
        worker.AudioReceived += echoed.Enqueue;
        await worker.StartAsync();
        await worker.RequestAsync("echo_audio", new JsonObject { ["on"] = true });

        var sent = new List<AudioFrame>();
        for (int i = 0; i < 100; i++)
        {
            float[] samples = Enumerable.Range(0, CanonicalAudioFormat.SamplesPerFrame).Select(n => (float)Math.Sin((i * 320) + n)).ToArray();
            var frame = new AudioFrame(i % 2 == 0 ? AudioStreamKind.SystemAudio : AudioStreamKind.Microphone, i, i * 320L, samples, AudioFrameMarkers.None);
            sent.Add(frame);
            await worker.SendAudioAsync(frame);
        }

        JsonNode stats = (await worker.RequestAsync("audio_stats"))!;
        Assert.Equal(100, (int)stats["frames"]!);
        Assert.Equal(99, (int)stats["last_sequence"]!);
        Assert.Equal(100 * 320, (int)stats["samples"]!);

        await ContractWorker.WaitForAsync(() => echoed.Count == 100);
        AudioFrame[] back = [.. echoed];
        for (int i = 0; i < 100; i++)
        {
            Assert.Equal((sent[i].Stream, sent[i].Sequence, sent[i].StreamPosition), (back[i].Stream, back[i].Sequence, back[i].StreamPosition));
            Assert.Equal(sent[i].Samples.ToArray(), back[i].Samples.ToArray());
        }
    }

    [PythonWorkerFact]
    public async Task A_crash_fails_pending_requests_and_the_worker_is_restarted()
    {
        await using var worker = new WorkerSupervisor(ContractWorker.Options());
        var states = new ConcurrentQueue<WorkerState>();
        worker.StateChanged += (state, _) => states.Enqueue(state);
        await worker.StartAsync();
        int firstPid = worker.ProcessId!.Value;

        // Requests run one at a time in the worker, so the crash comes from outside while a request is pending.
        Task<JsonNode?> pending = worker.RequestAsync("sleep", new JsonObject { ["seconds"] = 30 });
        await Task.Delay(200);
        using (Process process = Process.GetProcessById(firstPid))
        {
            process.Kill();
        }

        await Assert.ThrowsAsync<WorkerUnavailableException>(() => pending);
        await ContractWorker.WaitForAsync(() => worker.State == WorkerState.Ready && worker.Restarts == 1);
        Assert.NotEqual(firstPid, worker.ProcessId);
        Assert.Contains(WorkerState.Restarting, states);
        Assert.NotNull(await worker.RequestAsync("echo", new JsonObject()));

        // A worker that exits on its own (os._exit(3)) is restarted too, and the exit code is reported.
        await Assert.ThrowsAsync<WorkerUnavailableException>(() => worker.RequestAsync("crash"));
        await ContractWorker.WaitForAsync(() => worker.State == WorkerState.Ready && worker.Restarts == 2);
        Assert.Contains("exited with code 3", worker.LastError ?? string.Empty, StringComparison.Ordinal);
    }

    [PythonWorkerFact]
    public async Task Calls_during_a_restart_fail_fast_with_WorkerUnavailable()
    {
        WorkerOptions slowRestart = ContractWorker.Options() with { InitialBackoff = TimeSpan.FromSeconds(2) };
        await using var worker = new WorkerSupervisor(slowRestart);
        await worker.StartAsync();
        _ = worker.RequestAsync("crash").ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
        await ContractWorker.WaitForAsync(() => worker.State == WorkerState.Restarting);

        await Assert.ThrowsAsync<WorkerUnavailableException>(() => worker.RequestAsync("echo"));
        await Assert.ThrowsAsync<WorkerUnavailableException>(() => worker.SendAudioAsync(
            new AudioFrame(AudioStreamKind.SystemAudio, 0, 0, new float[320], AudioFrameMarkers.None)));
    }

    [PythonWorkerFact]
    public async Task An_unresponsive_worker_is_detected_by_missed_pings_and_restarted()
    {
        await using var worker = new WorkerSupervisor(ContractWorker.Options());
        await worker.StartAsync();
        _ = worker.RequestAsync("freeze").ContinueWith(t => _ = t.Exception, TaskScheduler.Default);

        await ContractWorker.WaitForAsync(() => worker.Restarts == 1 && worker.State == WorkerState.Ready);
        Assert.Contains("unresponsive", worker.LastError ?? string.Empty, StringComparison.Ordinal);
    }

    [PythonWorkerFact]
    public async Task Garbage_and_oversized_messages_are_protocol_violations_that_restart_the_worker()
    {
        await using var worker = new WorkerSupervisor(ContractWorker.Options());
        await worker.StartAsync();

        _ = worker.RequestAsync("garbage").ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
        await ContractWorker.WaitForAsync(() => worker.Restarts == 1 && worker.State == WorkerState.Ready);
        Assert.Contains("protocol violation", worker.LastError ?? string.Empty, StringComparison.Ordinal);

        _ = worker.RequestAsync("oversize").ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
        await ContractWorker.WaitForAsync(() => worker.Restarts == 2 && worker.State == WorkerState.Ready);
        Assert.Contains("outside 1..", worker.LastError ?? string.Empty, StringComparison.Ordinal);
    }

    [PythonWorkerFact]
    public async Task A_protocol_version_mismatch_is_refused_until_the_worker_faults()
    {
        await using var worker = new WorkerSupervisor(ContractWorker.Options("--protocol-version", "2"));
        var states = new ConcurrentQueue<WorkerState>();
        worker.StateChanged += (state, _) => states.Enqueue(state);

        WorkerUnavailableException error = await Assert.ThrowsAsync<WorkerUnavailableException>(() => worker.StartAsync());
        Assert.Equal(WorkerState.Faulted, worker.State);
        Assert.Contains("protocol 2 not supported", error.Message, StringComparison.Ordinal);
        Assert.Contains("3 failures", worker.LastError ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(0, worker.Restarts); // never reached Ready
    }

    [PythonWorkerFact]
    public async Task A_worker_that_never_says_hello_times_out()
    {
        WorkerOptions options = ContractWorker.Options("--no-hello") with { HelloTimeout = TimeSpan.FromSeconds(1), MaxFailures = 1 };
        await using var worker = new WorkerSupervisor(options);
        var watch = Stopwatch.StartNew();
        WorkerUnavailableException error = await Assert.ThrowsAsync<WorkerUnavailableException>(() => worker.StartAsync());
        Assert.Contains("no hello", error.Message, StringComparison.Ordinal);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), watch.Elapsed.ToString());
    }

    [PythonWorkerFact]
    public async Task Stop_shuts_the_worker_down_in_order()
    {
        var worker = new WorkerSupervisor(ContractWorker.Options());
        await worker.StartAsync();
        using Process process = Process.GetProcessById(worker.ProcessId!.Value);

        var watch = Stopwatch.StartNew();
        await worker.DisposeAsync();

        Assert.Equal(WorkerState.Stopped, worker.State);
        Assert.True(process.WaitForExit(5000), "the worker process must be gone after stop");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2.5), $"took {watch.Elapsed}: it was killed after the 3 s grace instead of shutting down");
        await Assert.ThrowsAsync<WorkerUnavailableException>(() => worker.RequestAsync("echo"));
    }
}
