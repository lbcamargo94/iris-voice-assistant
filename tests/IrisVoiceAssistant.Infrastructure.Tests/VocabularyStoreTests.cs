using IrisVoiceAssistant.Core.Transcripts;
using Microsoft.Data.Sqlite;

namespace IrisVoiceAssistant.Infrastructure.Tests;

public sealed class VocabularyStoreTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 12, 15, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "iris-vocab-tests", Guid.NewGuid().ToString("N"));

    private string Db => Path.Combine(_dir, "iris.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static TermObservation[] Terms(params string[] names) => [.. names.Select(n => new TermObservation(n, "name"))];

    [Fact]
    public async Task A_term_becomes_active_on_its_third_sentence_and_is_reported_once()
    {
        await using MeetingStore store = await MeetingStore.OpenAsync(Db);
        Guid session = Guid.CreateVersion7();

        Assert.Empty(await store.ObserveTermsAsync(session, Terms("Francina"), T0));
        Assert.Empty(await store.ObserveTermsAsync(session, Terms("Francina", "francina"), T0.AddSeconds(5))); // once per sentence
        IReadOnlyList<VocabularyTerm> activated = await store.ObserveTermsAsync(session, Terms("Francina"), T0.AddSeconds(9));
        Assert.Equal("Francina", Assert.Single(activated).Term);
        Assert.Empty(await store.ObserveTermsAsync(session, Terms("Francina"), T0.AddSeconds(12))); // already active

        VocabularyTerm term = Assert.Single(await store.GetActiveTermsAsync(T0.AddMinutes(1)));
        Assert.Equal((4, 1, VocabularyState.Active), (term.Occurrences, term.Sessions, term.State));
        Assert.True(term.KeepInEnglish);
    }

    [Fact]
    public async Task Sessions_are_counted_and_learning_carries_over_between_sessions()
    {
        await using (MeetingStore first = await MeetingStore.OpenAsync(Db))
        {
            Guid session = Guid.CreateVersion7();
            await first.ObserveTermsAsync(session, [new TermObservation("refactor", "rare")], T0);
            await first.ObserveTermsAsync(session, [new TermObservation("refactor", "rare")], T0.AddSeconds(3));
        }

        await using MeetingStore second = await MeetingStore.OpenAsync(Db);
        IReadOnlyList<VocabularyTerm> activated = await second.ObserveTermsAsync(Guid.CreateVersion7(), [new TermObservation("refactor", "rare")], T0.AddDays(1));
        VocabularyTerm term = Assert.Single(activated);
        Assert.Equal(2, term.Sessions);
        Assert.False(term.KeepInEnglish); // a rare lowercase word is still translated
    }

    [Fact]
    public async Task Forgotten_terms_stop_being_used_and_are_not_relearned_until_cleared()
    {
        await using MeetingStore store = await MeetingStore.OpenAsync(Db);
        Guid session = Guid.CreateVersion7();
        for (int i = 0; i < 3; i++)
        {
            await store.ObserveTermsAsync(session, Terms("OKR"), T0.AddSeconds(i));
        }

        Assert.True(await store.ForgetTermAsync("okr"));
        Assert.False(await store.ForgetTermAsync("never-seen"));
        for (int i = 0; i < 5; i++)
        {
            Assert.Empty(await store.ObserveTermsAsync(session, Terms("OKR"), T0.AddMinutes(i)));
        }

        Assert.Empty(await store.GetActiveTermsAsync(T0.AddHours(1)));
        Assert.Equal(VocabularyState.Forgotten, Assert.Single(await store.ListVocabularyAsync()).State);

        await store.ClearVocabularyAsync();
        Assert.Empty(await store.ListVocabularyAsync());
    }

    [Fact]
    public async Task Active_terms_unseen_for_30_days_are_no_longer_used()
    {
        await using MeetingStore store = await MeetingStore.OpenAsync(Db);
        Guid session = Guid.CreateVersion7();
        for (int i = 0; i < 3; i++)
        {
            await store.ObserveTermsAsync(session, Terms("Kinetic"), T0);
        }

        Assert.Single(await store.GetActiveTermsAsync(T0.AddDays(29)));
        Assert.Empty(await store.GetActiveTermsAsync(T0.AddDays(31)));
        Assert.Single(await store.ListVocabularyAsync()); // kept in the history
    }

    [Fact]
    public async Task A_version_1_database_is_migrated_without_losing_meetings()
    {
        Guid meeting;
        await using (MeetingStore store = await MeetingStore.OpenAsync(Db))
        {
            meeting = await store.CreateMeetingAsync("Before v2", T0);
        }

        await using (var raw = new SqliteConnection($"Data Source={Db};Pooling=False"))
        {
            await raw.OpenAsync();
            await using SqliteCommand downgrade = raw.CreateCommand();
            downgrade.CommandText = "DROP TABLE vocabulary; PRAGMA user_version = 1;";
            await downgrade.ExecuteNonQueryAsync();
        }

        await using MeetingStore migrated = await MeetingStore.OpenAsync(Db);
        Guid session = await migrated.StartSessionAsync(meeting, new SessionInfo(T0, 1, null, null, "test")); // v1 data intact
        Assert.NotEqual(Guid.Empty, session);
        await migrated.ObserveTermsAsync(session, Terms("Migrated"), T0);
        Assert.Single(await migrated.ListVocabularyAsync());
    }
}
