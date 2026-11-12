using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using IrisVoiceAssistant.AI.Contracts;
using IrisVoiceAssistant.Audio.Pipeline;
using IrisVoiceAssistant.Core.Audio;
using IrisVoiceAssistant.Core.Transcripts;
using IrisVoiceAssistant.Infrastructure;
using IrisVoiceAssistant.WorkerHost;

namespace IrisVoiceAssistant.Application.Live;

public sealed record LiveSessionOptions
{
    public required string DatabasePath { get; init; }

    public required WorkerOptions Speech { get; init; }

    public required WorkerOptions Translation { get; init; }

    public string Title { get; init; } = "Sessão ao vivo";

    /// <summary>Off by default (RF-TR-006): the user's own speech is transcribed but not translated.</summary>
    public bool TranslateMicrophone { get; init; }

    public TimeSpan ModelReadyTimeout { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan ReplayWindow { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(90);

    public string TranslationModel { get; init; } = "opus-mt-tc-big-en-pt";
}

/// <summary>A capture stream to open; the factory comes from a platform adapter (ADR-004).</summary>
public sealed record CaptureSourceSpec(AudioStreamKind Stream, Func<IAudioCaptureSource> Open);

public sealed record LiveSessionSummary(
    int Finals,
    int Translations,
    int TranslationFailures,
    int DuplicateFinalsDropped,
    IReadOnlyList<TimeSpan> FinalLatencies,
    IReadOnlyList<TimeSpan> TranslationLatencies,
    IReadOnlyList<AudioGap> Gaps,
    IReadOnlyList<AudioStreamCounters> Counters,
    int SpeechRestarts);

/// <summary>
/// The text pipeline of POC-001D: capture (one runner per stream) → speech worker → final sentences saved to SQLite
/// and then published → fast translation saved and published. Ordering and loss rules:
/// <list type="bullet">
/// <item>Final segments are persisted before <see cref="SegmentFinalized"/> fires (ADR-009, RF-DATA-006).</item>
/// <item>Worker events are processed by one consumer, in arrival order.</item>
/// <item>While the speech worker restarts, frames wait in the bounded capture queues; audio sent but not yet
/// acknowledged is replayed from the last acknowledged position (ADR-014 decision 5). Audio that left the replay
/// window first becomes a <see cref="AudioGapCause.WorkerRecovery"/> gap.</item>
/// <item>Translations never block transcription; a failed translation is counted, the original stays saved.</item>
/// </list>
/// </summary>
public sealed class LiveSession : IAsyncDisposable
{
    private readonly LiveSessionOptions _options;
    private readonly Channel<object> _events = Channel.CreateUnbounded<object>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Channel<TranscriptSegment> _translations = Channel.CreateUnbounded<TranscriptSegment>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _stopping = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly object _gate = new();
    private readonly Dictionary<AudioStreamKind, ReplayBuffer> _replay = [];
    private readonly Dictionary<AudioStreamKind, long> _acknowledged = [];
    private readonly Dictionary<AudioStreamKind, long> _lastFinalEnd = [];
    private readonly Dictionary<AudioStreamKind, long> _replayedUpTo = [];
    private readonly List<CaptureStreamRunner> _runners = [];
    private readonly List<Task> _pumps = [];
    private readonly List<TimeSpan> _finalLatencies = [];
    private readonly List<TimeSpan> _translationLatencies = [];
    private TaskCompletionSource _speechReady = NewSignal();
    private TaskCompletionSource _translationReady = NewSignal();
    private WorkerSupervisor? _speech;
    private WorkerSupervisor? _translation;
    private MeetingStore? _store;
    private Task? _eventLoop;
    private Task? _translationLoop;
    private long _sessionStart;
    private int _finals;
    private int _translated;
    private int _translationFailures;
    private int _duplicates;
    private string? _speechFailure;
    private string? _translationFailure;

    public LiveSession(LiveSessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    public event Action<string>? Status;

    public event Action<AudioStreamKind, string>? PartialUpdated;

    /// <summary>A final sentence, already saved. The latency is from the end of the sentence's last word.</summary>
    public event Action<TranscriptSegment, TimeSpan>? SegmentFinalized;

    /// <summary>The fast translation of a segment, already saved.</summary>
    public event Action<TranscriptSegment, string, TimeSpan>? SegmentTranslated;

    public event Action<AudioGap>? GapRecorded;

    public event Action<string, WorkerState, string?>? WorkerStateChanged;

    public event Action<AudioStreamKind, AudioCaptureState, string?>? CaptureStateChanged;

    public Guid MeetingId { get; private set; }

    public Guid SessionId { get; private set; }

    public string? DatabasePath => _store?.Path;

    public IReadOnlyList<CaptureStreamRunner> Runners => _runners;

    /// <summary>Starts the workers, waits for their models, opens the database and starts capturing.</summary>
    public async Task StartAsync(IReadOnlyList<CaptureSourceSpec> sources, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count == 0)
        {
            throw new ArgumentException("At least one capture source is required.", nameof(sources));
        }

        _speech = new WorkerSupervisor(_options.Speech);
        _translation = new WorkerSupervisor(_options.Translation);
        _speech.ControlReceived += OnSpeechMessage;
        _translation.ControlReceived += OnTranslationMessage;
        _speech.StateChanged += (state, reason) => OnWorkerState("speech", state, reason, ref _speechReady);
        _translation.StateChanged += (state, reason) => OnWorkerState("translation", state, reason, ref _translationReady);

        Status?.Invoke("Iniciando os workers e carregando os modelos (pode levar até 1 minuto lendo do HD)...");
        await Task.WhenAll(_speech.StartAsync(cancellationToken), _translation.StartAsync(cancellationToken)).ConfigureAwait(false);
        using var ready = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ready.CancelAfter(_options.ModelReadyTimeout);
        await Task.WhenAll(_speechReady.Task, _translationReady.Task).WaitAsync(ready.Token).ConfigureAwait(false);
        if (_speechFailure is not null || _translationFailure is not null)
        {
            throw new WorkerUnavailableException($"model load failed: {_speechFailure ?? _translationFailure}");
        }

        _store = await MeetingStore.OpenAsync(_options.DatabasePath, cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        MeetingId = await _store.CreateMeetingAsync(_options.Title, now, cancellationToken).ConfigureAwait(false);
        _sessionStart = Stopwatch.GetTimestamp();

        foreach (CaptureSourceSpec source in sources)
        {
            var runner = new CaptureStreamRunner(source.Stream, source.Open, _sessionStart);
            runner.StateChanged += (stream, state, error) => CaptureStateChanged?.Invoke(stream, state, error);
            runner.Start();
            runner.Pipeline.GapRecorded += gap => _events.Writer.TryWrite(gap);
            lock (_gate)
            {
                _replay[source.Stream] = new ReplayBuffer(_options.ReplayWindow);
                _acknowledged[source.Stream] = 0;
                _replayedUpTo[source.Stream] = 0;
            }

            _runners.Add(runner);
        }

        string? Description(AudioStreamKind kind) => _runners.FirstOrDefault(r => r.Stream == kind)?.SourceDescription;
        SessionId = await _store.StartSessionAsync(MeetingId, new SessionInfo(
            now, _sessionStart, Description(AudioStreamKind.SystemAudio), Description(AudioStreamKind.Microphone),
            Description(AudioStreamKind.SystemAudio)?.StartsWith("process loopback", StringComparison.Ordinal) == true ? "ProcessLoopbackExclude" : "EndpointLoopback"),
            cancellationToken).ConfigureAwait(false);

        _eventLoop = Task.Run(EventLoopAsync, CancellationToken.None);
        _translationLoop = Task.Run(TranslationLoopAsync, CancellationToken.None);
        foreach (CaptureStreamRunner runner in _runners)
        {
            _pumps.Add(Task.Run(() => PumpAsync(runner), CancellationToken.None));
        }

        Status?.Invoke("Capturando.");
    }

    public void Pause()
    {
        foreach (CaptureStreamRunner runner in _runners)
        {
            runner.Pause();
            _ = EndSegmentQuietlyAsync(runner.Stream); // finalize the words spoken before the pause
        }
    }

    public void Resume() => _runners.ForEach(r => r.Resume());

    /// <summary>Stops capturing, finalizes every pending sentence and translation, and returns the session summary.</summary>
    public async Task<LiveSessionSummary> StopAsync()
    {
        using var timeout = new CancellationTokenSource(_options.StopTimeout);
        _runners.ForEach(r => r.Stop());
        await Task.WhenAll(_pumps).WaitAsync(timeout.Token).ConfigureAwait(false);
        if (_speech is { State: WorkerState.Ready })
        {
            Status?.Invoke("Finalizando as últimas frases...");
            try
            {
                await _speech.RequestAsync("flush", null, timeout.Token).ConfigureAwait(false); // finals precede its answer
            }
            catch (Exception error) when (error is WorkerUnavailableException or WorkerRequestException)
            {
                Status?.Invoke($"Não foi possível finalizar as últimas frases: {error.Message}");
            }
        }

        _events.Writer.TryComplete();
        if (_eventLoop is not null)
        {
            await _eventLoop.WaitAsync(timeout.Token).ConfigureAwait(false);
        }

        _translations.Writer.TryComplete();
        if (_translationLoop is not null)
        {
            await _translationLoop.WaitAsync(timeout.Token).ConfigureAwait(false);
        }

        IReadOnlyList<AudioGap> gaps = _store is null ? [] : await _store.GetGapsAsync(SessionId).ConfigureAwait(false);
        lock (_gate)
        {
            return new LiveSessionSummary(_finals, _translated, _translationFailures, _duplicates, [.. _finalLatencies],
                [.. _translationLatencies], gaps, [.. _runners.Select(r => r.Pipeline.Counters)], _speech?.Restarts ?? 0);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        _runners.ForEach(r => r.Dispose());
        if (_speech is not null)
        {
            await _speech.DisposeAsync().ConfigureAwait(false);
        }

        if (_translation is not null)
        {
            await _translation.DisposeAsync().ConfigureAwait(false);
        }

        if (_store is not null)
        {
            await _store.DisposeAsync().ConfigureAwait(false);
        }

        _sendLock.Dispose();
        _stopping.Dispose();
    }

    // ---- capture → speech worker ------------------------------------------------------------------------

    private async Task PumpAsync(CaptureStreamRunner runner)
    {
        AudioStreamKind stream = runner.Stream;
        await foreach (AudioFrame frame in runner.Pipeline.ReadAllAsync().ConfigureAwait(false))
        {
            lock (_gate)
            {
                _replay[stream].Add(frame);
            }

            while (!_stopping.IsCancellationRequested)
            {
                Task ready = _speechReady.Task;
                await ready.ConfigureAwait(false); // while restarting, the capture queue holds the audio
                await _sendLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    lock (_gate)
                    {
                        if (frame.StreamPosition + frame.Samples.Length <= _replayedUpTo[stream])
                        {
                            break; // a recovery already re-sent this frame
                        }
                    }

                    if (!ReferenceEquals(ready, _speechReady.Task))
                    {
                        continue; // a restart began meanwhile
                    }

                    await _speech!.SendAudioAsync(frame).ConfigureAwait(false);
                    break;
                }
                catch (WorkerUnavailableException)
                {
                    // the recovery after the restart re-sends this frame from the replay buffer
                }
                finally
                {
                    _sendLock.Release();
                }
            }
        }
    }

    private void OnSpeechMessage(ControlMessage message)
    {
        if (message.Type == "status")
        {
            string state = (string?)message.Body["state"] ?? string.Empty;
            if (state == "ready")
            {
                _ = Task.Run(RecoverSpeechAsync); // first start or after a restart
            }
            else if (state == "failed")
            {
                _speechFailure = (string?)message.Body["message"];
                _speechReady.TrySetResult();
            }

            return;
        }

        _events.Writer.TryWrite(message);
    }

    /// <summary>Re-sends unacknowledged audio to a (re)started speech worker, then lets the pumps continue.</summary>
    private async Task RecoverSpeechAsync()
    {
        await _sendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            List<(AudioStreamKind Stream, IReadOnlyList<AudioFrame> Frames, long From)> replay;
            lock (_gate)
            {
                replay = [.. _replay.Select(kv => (kv.Key, kv.Value.FramesFrom(_acknowledged[kv.Key]), _acknowledged[kv.Key]))];
            }

            foreach ((AudioStreamKind stream, IReadOnlyList<AudioFrame> frames, long from) in replay)
            {
                if (frames.Count > 0 && frames[0].StreamPosition > from && _speech!.Restarts > 0)
                {
                    var lost = new AudioGap(stream, from, frames[0].StreamPosition - from, AudioGapCause.WorkerRecovery);
                    _events.Writer.TryWrite(lost);
                }

                foreach (AudioFrame frame in frames)
                {
                    await _speech!.SendAudioAsync(frame).ConfigureAwait(false);
                }

                lock (_gate)
                {
                    if (frames.Count > 0)
                    {
                        _replayedUpTo[stream] = frames[^1].StreamPosition + frames[^1].Samples.Length;
                    }
                }
            }

            if (replay.Any(r => r.Frames.Count > 0))
            {
                Status?.Invoke($"STT reiniciado: {replay.Sum(r => r.Frames.Count)} blocos reenviados.");
            }

            _speechReady.TrySetResult();
        }
        catch (WorkerUnavailableException)
        {
            // it went down again; the next "ready" retries
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private void OnTranslationMessage(ControlMessage message)
    {
        if (message.Type != "status")
        {
            return;
        }

        string state = (string?)message.Body["state"] ?? string.Empty;
        if (state == "failed")
        {
            _translationFailure = (string?)message.Body["message"];
        }

        _translationReady.TrySetResult();
    }

    private void OnWorkerState(string worker, WorkerState state, string? reason, ref TaskCompletionSource ready)
    {
        if (state is WorkerState.Restarting or WorkerState.Starting && ready.Task.IsCompleted)
        {
            ready = NewSignal(); // wait for the new process's "ready" before sending again
        }

        WorkerStateChanged?.Invoke(worker, state, reason);
    }

    // ---- speech events → database → subscribers --------------------------------------------------------

    private async Task EventLoopAsync()
    {
        await foreach (object item in _events.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                switch (item)
                {
                    case AudioGap gap:
                        await _store!.SaveGapAsync(SessionId, gap, DateTimeOffset.UtcNow).ConfigureAwait(false);
                        GapRecorded?.Invoke(gap);
                        break;
                    case ControlMessage { Type: "stt.partial" } partial:
                        PartialUpdated?.Invoke((AudioStreamKind)(int)partial.Body["stream"]!, (string?)partial.Body["text"] ?? string.Empty);
                        break;
                    case ControlMessage { Type: "stt.ack" } ack:
                        Acknowledge((AudioStreamKind)(int)ack.Body["stream"]!, (long)ack.Body["replay_from"]!);
                        break;
                    case ControlMessage { Type: "stt.final" } final:
                        await OnFinalAsync(final.Body).ConfigureAwait(false);
                        break;
                }
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or Microsoft.Data.Sqlite.SqliteException)
            {
                Status?.Invoke($"Evento ignorado ({error.GetType().Name}): {error.Message}");
            }
        }
    }

    private async Task OnFinalAsync(JsonObject body)
    {
        var stream = (AudioStreamKind)(int)body["stream"]!;
        long start = (long)body["start"]!;
        long end = (long)body["end"]!;
        lock (_gate)
        {
            if (_lastFinalEnd.TryGetValue(stream, out long previousEnd) && end <= previousEnd)
            {
                _duplicates++; // re-emitted after a replay: already saved
                return;
            }

            _lastFinalEnd[stream] = end;
        }

        TranscriptSegment segment = await _store!.AppendFinalAsync(SessionId, stream, start, end,
            (string?)body["text"] ?? string.Empty, (bool?)body["forced"] ?? false, DateTimeOffset.UtcNow).ConfigureAwait(false);
        Acknowledge(stream, end);
        TimeSpan latency = Since(end);
        lock (_gate)
        {
            _finals++;
            _finalLatencies.Add(latency);
        }

        SegmentFinalized?.Invoke(segment, latency);
        if (stream == AudioStreamKind.SystemAudio || _options.TranslateMicrophone)
        {
            _translations.Writer.TryWrite(segment);
        }
    }

    private void Acknowledge(AudioStreamKind stream, long position)
    {
        lock (_gate)
        {
            if (!_acknowledged.TryGetValue(stream, out long current) || position > current)
            {
                _acknowledged[stream] = position;
                _replay.GetValueOrDefault(stream)?.Acknowledge(position);
            }
        }
    }

    // ---- translations -----------------------------------------------------------------------------------

    private async Task TranslationLoopAsync()
    {
        await foreach (TranscriptSegment segment in _translations.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            string? text = await TranslateAsync(segment.Text).ConfigureAwait(false);
            if (text is null)
            {
                lock (_gate)
                {
                    _translationFailures++;
                }

                continue;
            }

            await _store!.SaveTranslationAsync(segment.Id, TranslationLayer.Fast, text, _options.TranslationModel, DateTimeOffset.UtcNow).ConfigureAwait(false);
            TimeSpan latency = Since(segment.EndSample);
            lock (_gate)
            {
                _translated++;
                _translationLatencies.Add(latency);
            }

            SegmentTranslated?.Invoke(segment, text, latency);
        }
    }

    private async Task<string?> TranslateAsync(string source)
    {
        for (int attempt = 0; attempt < 20 && !_stopping.IsCancellationRequested; attempt++)
        {
            try
            {
                await _translationReady.Task.ConfigureAwait(false);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                JsonNode? result = await _translation!.RequestAsync("translate", new JsonObject { ["text"] = source }, timeout.Token).ConfigureAwait(false);
                return (string?)result?["text"];
            }
            catch (WorkerRequestException error) when (error.Code is "handler_failed" && error.Message.Contains("NotReady", StringComparison.Ordinal))
            {
                await Task.Delay(200).ConfigureAwait(false);
            }
            catch (WorkerUnavailableException)
            {
                await Task.Delay(500).ConfigureAwait(false);
            }
            catch (Exception error) when (error is WorkerRequestException or OperationCanceledException)
            {
                Status?.Invoke($"Tradução falhou: {error.Message}");
                return null;
            }
        }

        return null;
    }

    private async Task EndSegmentQuietlyAsync(AudioStreamKind stream)
    {
        try
        {
            await _speech!.RequestAsync("end_segment", new JsonObject { ["stream"] = (int)stream }).ConfigureAwait(false);
        }
        catch (Exception error) when (error is WorkerUnavailableException or WorkerRequestException)
        {
        }
    }

    private TimeSpan Since(long streamPosition) =>
        Stopwatch.GetElapsedTime(_sessionStart) - CanonicalAudioFormat.ToTimeSpan(streamPosition);

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
