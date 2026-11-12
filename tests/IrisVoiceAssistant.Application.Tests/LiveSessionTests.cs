using System.Collections.Concurrent;
using System.Diagnostics;
using IrisVoiceAssistant.Application.Live;
using IrisVoiceAssistant.Core.Audio;
using IrisVoiceAssistant.Core.Transcripts;
using IrisVoiceAssistant.Infrastructure;
using IrisVoiceAssistant.WorkerHost;

namespace IrisVoiceAssistant.Application.Tests;

/// <summary>Skips when workers/.venv has not been synced (the fake workers are Python, but need no models).</summary>
public sealed class PythonFactAttribute : FactAttribute
{
    public PythonFactAttribute()
    {
        if (!File.Exists(Fakes.Python))
        {
            Skip = "workers/.venv not found; run 'uv sync' in workers/.";
        }
    }
}

internal static class Fakes
{
    public static readonly string Repo = FindRepo();

    public static string Python => Path.Combine(Repo, "workers", ".venv", "Scripts", "python.exe");

    public static WorkerOptions Worker(string name, params string[] extra) => new()
    {
        Name = name,
        FileName = Python,
        Arguments = ["-I", "-m", "iris_workers.testing.fake_pipeline_workers", name, .. extra],
        PingInterval = TimeSpan.FromMilliseconds(250),
        InitialBackoff = TimeSpan.FromMilliseconds(100),
    };

    /// <summary>A capture device that streams 10 ms blocks of 16 kHz mono silence in real time.</summary>
    public sealed class Source(AudioStreamKind stream) : IAudioCaptureSource
    {
        public AudioStreamKind Stream { get; } = stream;

        public RawAudioFormat Format { get; } = new(16_000, 1, RawSampleType.IeeeFloat);

        public string Description => Stream == AudioStreamKind.SystemAudio ? "process loopback (fake)" : "microphone (fake)";

        public void Run(IRawAudioSink sink, CancellationToken cancellationToken)
        {
            byte[] block = new byte[160 * 4];
            long next = Stopwatch.GetTimestamp();
            while (!cancellationToken.IsCancellationRequested)
            {
                sink.OnAudio(block, 160, Stopwatch.GetTimestamp(), RawBlockMarkers.None);
                next += Stopwatch.Frequency / 100;
                int wait = (int)((next - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency);
                cancellationToken.WaitHandle.WaitOne(Math.Max(0, wait));
            }
        }

        public void Dispose()
        {
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

public sealed class LiveSessionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "iris-live-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static CaptureSourceSpec[] BothStreams =>
    [
        new(AudioStreamKind.SystemAudio, () => new Fakes.Source(AudioStreamKind.SystemAudio)),
        new(AudioStreamKind.Microphone, () => new Fakes.Source(AudioStreamKind.Microphone)),
    ];

    [PythonFact]
    public async Task Finals_are_saved_before_they_are_published_and_only_system_audio_is_translated()
    {
        string db = Path.Combine(_dir, "iris.db");
        await using var session = new LiveSession(new LiveSessionOptions
        {
            DatabasePath = db,
            Speech = Fakes.Worker("speech"),
            Translation = Fakes.Worker("translation"),
        });
        var published = new ConcurrentQueue<TranscriptSegment>();
        var translated = new ConcurrentDictionary<Guid, string>();
        session.SegmentFinalized += (segment, _) => published.Enqueue(segment);
        session.SegmentTranslated += (segment, text, _) => translated[segment.Id] = text;

        await session.StartAsync(BothStreams);
        await Task.Delay(TimeSpan.FromSeconds(4.5));
        LiveSessionSummary summary = await session.StopAsync();

        Assert.InRange(summary.Finals, 6, 12); // ~4 per stream
        Assert.Equal(0, summary.DuplicateFinalsDropped);
        await using MeetingStore store = await MeetingStore.OpenAsync(db);
        IReadOnlyList<TranscriptSegment> saved = await store.GetSegmentsAsync(session.SessionId);
        Assert.Equal(published.Select(s => s.Id).Order(), saved.Select(s => s.Id).Order()); // every published one is saved

        foreach (AudioStreamKind stream in new[] { AudioStreamKind.SystemAudio, AudioStreamKind.Microphone })
        {
            long[] sequence = [.. saved.Where(s => s.Stream == stream).Select(s => s.Sequence).Order()];
            Assert.Equal(Enumerable.Range(0, sequence.Length).Select(i => (long)i), sequence); // gap-free
        }

        Assert.All(saved.Where(s => s.Stream == AudioStreamKind.SystemAudio), s =>
            Assert.Equal("PT: " + s.Text, translated.GetValueOrDefault(s.Id)));
        Assert.DoesNotContain(saved.Where(s => s.Stream == AudioStreamKind.Microphone), s => translated.ContainsKey(s.Id)); // RF-TR-006
        Assert.Equal(summary.Translations, translated.Count);
        Assert.All(summary.Counters, c => Assert.True(c.Reconciles));
    }

    [PythonFact]
    public async Task A_speech_worker_crash_is_recovered_by_replay_without_losing_or_duplicating_finals()
    {
        string marker = Path.Combine(_dir, "crashed.marker");
        Directory.CreateDirectory(_dir);
        await using var session = new LiveSession(new LiveSessionOptions
        {
            DatabasePath = Path.Combine(_dir, "iris.db"),
            Speech = Fakes.Worker("speech", "--crash-at", "2.5", "--crash-marker", marker),
            Translation = Fakes.Worker("translation"),
        });
        var states = new ConcurrentQueue<WorkerState>();
        session.WorkerStateChanged += (worker, state, _) =>
        {
            if (worker == "speech")
            {
                states.Enqueue(state);
            }
        };

        await session.StartAsync([new(AudioStreamKind.SystemAudio, () => new Fakes.Source(AudioStreamKind.SystemAudio))]);
        await Task.Delay(TimeSpan.FromSeconds(6));
        LiveSessionSummary summary = await session.StopAsync();

        Assert.True(File.Exists(marker), "the fake worker must have crashed once");
        Assert.Equal(1, summary.SpeechRestarts);
        Assert.Contains(WorkerState.Restarting, states);
        Assert.DoesNotContain(summary.Gaps, g => g.Cause == AudioGapCause.WorkerRecovery); // replay covered the restart

        await using MeetingStore store = await MeetingStore.OpenAsync(Path.Combine(_dir, "iris.db"));
        IReadOnlyList<TranscriptSegment> saved = await store.GetSegmentsAsync(session.SessionId);
        string[] seconds = [.. saved.Select(s => s.Text)];
        Assert.Equal(seconds.Distinct(), seconds);                                    // no duplicate sentence
        long[] starts = [.. saved.Select(s => s.StartSample / 16_000)];
        Assert.Equal(Enumerable.Range((int)starts[0], starts.Length).Select(i => (long)i), starts); // no second missing
        Assert.True(saved.Count >= 4, $"only {saved.Count} finals");
    }
}
