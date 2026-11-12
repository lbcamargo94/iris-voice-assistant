using IrisVoiceAssistant.Core.Audio;
using IrisVoiceAssistant.Core.Transcripts;

namespace IrisVoiceAssistant.Infrastructure.Tests;

public sealed class MeetingStoreTests : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 12, 15, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "iris-store-tests", Guid.NewGuid().ToString("N"));
    private string DbPath => Path.Combine(_dir, "iris.db");

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }

        return Task.CompletedTask;
    }

    private static SessionInfo Info => new(T0, 123, "process loopback (excludes this app)", "microphone: headset", "ProcessLoopbackExclude");

    [Fact]
    public async Task Opens_in_wal_mode_and_creates_the_schema()
    {
        await using MeetingStore store = await MeetingStore.OpenAsync(DbPath);
        Assert.Equal("wal", await store.JournalModeAsync());
        Assert.True(File.Exists(DbPath));
    }

    [Fact]
    public async Task Final_segments_get_gap_free_sequence_numbers_per_stream()
    {
        await using MeetingStore store = await MeetingStore.OpenAsync(DbPath);
        Guid meeting = await store.CreateMeetingAsync("Daily", T0);
        Guid session = await store.StartSessionAsync(meeting, Info);

        TranscriptSegment a = await store.AppendFinalAsync(session, AudioStreamKind.SystemAudio, 0, 16_000, "Hello.", false, T0);
        TranscriptSegment b = await store.AppendFinalAsync(session, AudioStreamKind.Microphone, 8_000, 20_000, "Hi.", false, T0);
        TranscriptSegment c = await store.AppendFinalAsync(session, AudioStreamKind.SystemAudio, 20_000, 40_000, "Next item.", true, T0);

        Assert.Equal((0L, 0L, 1L), (a.Sequence, b.Sequence, c.Sequence));
        Assert.Equal(7, a.Id.Version); // UUIDv7 (ADR-012)
        IReadOnlyList<TranscriptSegment> all = await store.GetSegmentsAsync(session);
        Assert.Equal(["Hello.", "Hi.", "Next item."], all.Select(s => s.Text));
        Assert.True(all[2].Forced);
        Assert.Equal(TimeSpan.FromSeconds(2.5), all[2].End); // 40 000 samples at 16 kHz
    }

    [Fact]
    public async Task Concurrent_appends_never_duplicate_or_skip_a_sequence_number()
    {
        await using MeetingStore store = await MeetingStore.OpenAsync(DbPath);
        Guid session = await store.StartSessionAsync(await store.CreateMeetingAsync("Load", T0), Info);

        await Task.WhenAll(Enumerable.Range(0, 50).Select(i =>
            store.AppendFinalAsync(session, AudioStreamKind.SystemAudio, i * 1000L, (i * 1000L) + 500, $"s{i}", false, T0)));

        IReadOnlyList<TranscriptSegment> all = await store.GetSegmentsAsync(session);
        Assert.Equal(Enumerable.Range(0, 50).Select(i => (long)i), all.Select(s => s.Sequence).Order());
    }

    [Fact]
    public async Task Translations_are_stored_per_layer_next_to_the_original()
    {
        await using MeetingStore store = await MeetingStore.OpenAsync(DbPath);
        Guid session = await store.StartSessionAsync(await store.CreateMeetingAsync("T", T0), Info);
        TranscriptSegment segment = await store.AppendFinalAsync(session, AudioStreamKind.SystemAudio, 0, 100, "We shipped it.", false, T0);

        await store.SaveTranslationAsync(segment.Id, TranslationLayer.Fast, "Nós enviamos.", "opus-mt-tc-big-en-pt", T0);
        await store.SaveTranslationAsync(segment.Id, TranslationLayer.Refined, "Nós entregamos.", "qwen3:4b-instruct", T0);

        Assert.Equal("Nós enviamos.", await store.GetTranslationAsync(segment.Id, TranslationLayer.Fast));
        Assert.Equal("Nós entregamos.", await store.GetTranslationAsync(segment.Id, TranslationLayer.Refined));
        Assert.Equal("We shipped it.", (await store.GetSegmentsAsync(session))[0].Text); // original untouched
        await Assert.ThrowsAnyAsync<Microsoft.Data.Sqlite.SqliteException>(() =>
            store.SaveTranslationAsync(segment.Id, TranslationLayer.Fast, "again", "m", T0)); // one per layer
    }

    [Fact]
    public async Task Growing_gaps_are_updated_in_place()
    {
        await using MeetingStore store = await MeetingStore.OpenAsync(DbPath);
        Guid session = await store.StartSessionAsync(await store.CreateMeetingAsync("G", T0), Info);
        var episode = new AudioGap(AudioStreamKind.SystemAudio, 32_000, 640, AudioGapCause.Overflow);

        await store.SaveGapAsync(session, episode, T0);
        await store.SaveGapAsync(session, episode with { LengthSamples = 16_000, MissingSamples = 8_000 }, T0);
        await store.SaveGapAsync(session, new AudioGap(AudioStreamKind.Microphone, 0, 80_000, AudioGapCause.Paused), T0);

        IReadOnlyList<AudioGap> gaps = await store.GetGapsAsync(session);
        Assert.Equal(2, gaps.Count);
        AudioGap overflow = gaps.Single(g => g.Cause == AudioGapCause.Overflow);
        Assert.Equal((16_000L, 8_000L), (overflow.LengthSamples, overflow.MissingSamples));
    }

    [Fact]
    public async Task Data_survives_reopening_and_deleting_a_meeting_removes_everything_derived()
    {
        Guid meeting, session, segmentId;
        await using (MeetingStore store = await MeetingStore.OpenAsync(DbPath))
        {
            meeting = await store.CreateMeetingAsync("Keep", T0);
            session = await store.StartSessionAsync(meeting, Info);
            segmentId = (await store.AppendFinalAsync(session, AudioStreamKind.SystemAudio, 0, 10, "Persisted.", false, T0)).Id;
            await store.SaveTranslationAsync(segmentId, TranslationLayer.Fast, "Persistido.", "m", T0);
            await store.SaveGapAsync(session, new AudioGap(AudioStreamKind.SystemAudio, 10, 10, AudioGapCause.DeviceLost), T0);
        }

        await using (MeetingStore reopened = await MeetingStore.OpenAsync(DbPath))
        {
            Assert.Equal("Persisted.", Assert.Single(await reopened.GetSegmentsAsync(session)).Text);
            await reopened.DeleteMeetingAsync(meeting);
            Assert.Empty(await reopened.GetSegmentsAsync(session));
            Assert.Empty(await reopened.GetGapsAsync(session));
            Assert.Null(await reopened.GetTranslationAsync(segmentId, TranslationLayer.Fast));
        }
    }
}
