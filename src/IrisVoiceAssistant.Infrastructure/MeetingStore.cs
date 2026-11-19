using System.Globalization;
using IrisVoiceAssistant.Core.Audio;
using IrisVoiceAssistant.Core.Transcripts;
using Microsoft.Data.Sqlite;

namespace IrisVoiceAssistant.Infrastructure;

/// <summary>
/// Local meeting database (DATABASE.md): SQLite in WAL mode with <c>synchronous=FULL</c>, so a final segment that
/// was saved survives an app or system crash (RNF-006). Callers save each final segment before publishing it
/// (ADR-009, RF-DATA-006). Sequence numbers are assigned here, per session and stream, and are gap-free.
/// Only text and timings are stored; never audio (RF-DATA-005).
/// </summary>
public sealed class MeetingStore : IAsyncDisposable
{
    private const int SchemaVersion = 2;

    /// <summary>A term becomes active once it appeared in this many distinct final sentences (ADR-015).</summary>
    public const int ActivationOccurrences = 3;

    /// <summary>Active terms not seen for this long stop being used (but are kept in the history).</summary>
    public static readonly TimeSpan ActiveFor = TimeSpan.FromDays(30);
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private MeetingStore(SqliteConnection connection, string path)
    {
        _connection = connection;
        Path = path;
    }

    public string Path { get; }

    public static async Task<MeetingStore> OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string full = System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = full,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var store = new MeetingStore(connection, full);
        await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
        return store;
    }

    public async Task<Guid> CreateMeetingAsync(string title, DateTimeOffset createdUtc, CancellationToken cancellationToken = default)
    {
        Guid id = Guid.CreateVersion7(createdUtc);
        await ExecuteAsync(
            "INSERT INTO meetings (id, title, created_utc, language) VALUES ($id, $title, $created, 'en');",
            cancellationToken,
            ("$id", id.ToString()), ("$title", title), ("$created", Utc(createdUtc))).ConfigureAwait(false);
        return id;
    }

    public async Task<Guid> StartSessionAsync(Guid meetingId, SessionInfo info, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(info);
        Guid id = Guid.CreateVersion7(info.StartedUtc);
        await ExecuteAsync(
            """
            INSERT INTO sessions (id, meeting_id, started_utc, started_timestamp, system_source, mic_source, isolation_mode)
            VALUES ($id, $meeting, $started, $ts, $system, $mic, $isolation);
            """,
            cancellationToken,
            ("$id", id.ToString()), ("$meeting", meetingId.ToString()), ("$started", Utc(info.StartedUtc)),
            ("$ts", info.StartedTimestamp), ("$system", info.SystemSource), ("$mic", info.MicrophoneSource),
            ("$isolation", info.IsolationMode)).ConfigureAwait(false);
        return id;
    }

    /// <summary>Saves a final segment with the next sequence number of its stream. Durable when this returns.</summary>
    public async Task<TranscriptSegment> AppendFinalAsync(
        Guid sessionId, AudioStreamKind stream, long startSample, long endSample, string text, bool forced,
        DateTimeOffset createdUtc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteTransaction transaction = (SqliteTransaction)await _connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteCommand next = Command(
                "SELECT COALESCE(MAX(sequence) + 1, 0) FROM transcript_segments WHERE session_id = $session AND stream = $stream;",
                ("$session", sessionId.ToString()), ("$stream", (int)stream));
            next.Transaction = transaction;
            long sequence = (long)(await next.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
            var segment = new TranscriptSegment(Guid.CreateVersion7(createdUtc), sessionId, stream, sequence, startSample, endSample, text, forced, createdUtc);
            await using SqliteCommand insert = Command(
                """
                INSERT INTO transcript_segments (id, session_id, stream, sequence, start_sample, end_sample, text, state, forced, created_utc)
                VALUES ($id, $session, $stream, $seq, $start, $end, $text, 'Final', $forced, $created);
                """,
                ("$id", segment.Id.ToString()), ("$session", sessionId.ToString()), ("$stream", (int)stream), ("$seq", sequence),
                ("$start", startSample), ("$end", endSample), ("$text", text), ("$forced", forced ? 1 : 0), ("$created", Utc(createdUtc)));
            insert.Transaction = transaction;
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return segment;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TranslationSegment> SaveTranslationAsync(
        Guid segmentId, TranslationLayer layer, string text, string model, DateTimeOffset createdUtc, CancellationToken cancellationToken = default)
    {
        var translation = new TranslationSegment(Guid.CreateVersion7(createdUtc), segmentId, "pt-BR", layer, text, model, createdUtc);
        await ExecuteAsync(
            """
            INSERT INTO translation_segments (id, segment_id, target_language, layer, text, model, created_utc)
            VALUES ($id, $segment, $lang, $layer, $text, $model, $created);
            """,
            cancellationToken,
            ("$id", translation.Id.ToString()), ("$segment", segmentId.ToString()), ("$lang", translation.TargetLanguage),
            ("$layer", layer.ToString()), ("$text", text), ("$model", model), ("$created", Utc(createdUtc))).ConfigureAwait(false);
        return translation;
    }

    /// <summary>Inserts a gap or updates it in place (overflow episodes and device-loss spans grow while open).</summary>
    public Task SaveGapAsync(Guid sessionId, AudioGap gap, DateTimeOffset recordedUtc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gap);
        return ExecuteAsync(
            """
            INSERT INTO audio_gaps (session_id, stream, start_sample, cause, length_samples, missing_samples, recorded_utc)
            VALUES ($session, $stream, $start, $cause, $length, $missing, $recorded)
            ON CONFLICT (session_id, stream, start_sample, cause)
            DO UPDATE SET length_samples = excluded.length_samples, missing_samples = excluded.missing_samples, recorded_utc = excluded.recorded_utc;
            """,
            cancellationToken,
            ("$session", sessionId.ToString()), ("$stream", (int)gap.Stream), ("$start", gap.StartPosition), ("$cause", gap.Cause.ToString()),
            ("$length", gap.LengthSamples), ("$missing", gap.MissingSamples), ("$recorded", Utc(recordedUtc)));
    }

    public async Task<IReadOnlyList<TranscriptSegment>> GetSegmentsAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteCommand command = Command(
                """
                SELECT id, stream, sequence, start_sample, end_sample, text, forced, created_utc
                FROM transcript_segments WHERE session_id = $session ORDER BY start_sample, stream;
                """,
                ("$session", sessionId.ToString()));
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var segments = new List<TranscriptSegment>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                segments.Add(new TranscriptSegment(
                    Guid.Parse(reader.GetString(0)), sessionId, (AudioStreamKind)reader.GetInt32(1), reader.GetInt64(2),
                    reader.GetInt64(3), reader.GetInt64(4), reader.GetString(5), reader.GetInt32(6) == 1,
                    DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture)));
            }

            return segments;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string?> GetTranslationAsync(Guid segmentId, TranslationLayer layer, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteCommand command = Command(
                "SELECT text FROM translation_segments WHERE segment_id = $segment AND layer = $layer;",
                ("$segment", segmentId.ToString()), ("$layer", layer.ToString()));
            return (string?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<AudioGap>> GetGapsAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteCommand command = Command(
                "SELECT stream, start_sample, length_samples, missing_samples, cause FROM audio_gaps WHERE session_id = $session ORDER BY start_sample;",
                ("$session", sessionId.ToString()));
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var gaps = new List<AudioGap>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                gaps.Add(new AudioGap((AudioStreamKind)reader.GetInt32(0), reader.GetInt64(1), reader.GetInt64(2), Enum.Parse<AudioGapCause>(reader.GetString(4)))
                {
                    MissingSamples = reader.GetInt64(3),
                });
            }

            return gaps;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Records the terms of one final sentence (each counts once) and returns those that just became active.
    /// Forgotten terms are ignored.
    /// </summary>
    public async Task<IReadOnlyList<VocabularyTerm>> ObserveTermsAsync(
        Guid sessionId, IReadOnlyList<TermObservation> terms, DateTimeOffset seenUtc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(terms);
        var activated = new List<VocabularyTerm>();
        if (terms.Count == 0)
        {
            return activated;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var transaction = (SqliteTransaction)await _connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            foreach (TermObservation observation in terms.DistinctBy(t => t.Text.ToLowerInvariant()))
            {
                string key = observation.Text.ToLowerInvariant();
                await using SqliteCommand find = Command(
                    "SELECT state, occurrences, sessions, last_session_id, first_seen_utc FROM vocabulary WHERE key = $key;", ("$key", key));
                find.Transaction = transaction;
                await using SqliteDataReader row = await find.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await row.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    await row.DisposeAsync().ConfigureAwait(false);
                    await RunAsync(transaction,
                        """
                        INSERT INTO vocabulary (key, term, kind, occurrences, sessions, last_session_id, first_seen_utc, last_seen_utc, state, source)
                        VALUES ($key, $term, $kind, 1, 1, $session, $seen, $seen, 'Candidate', 'auto');
                        """, cancellationToken,
                        ("$key", key), ("$term", observation.Text), ("$kind", observation.Kind), ("$session", sessionId.ToString()), ("$seen", Utc(seenUtc))).ConfigureAwait(false);
                    continue;
                }

                var state = Enum.Parse<VocabularyState>(row.GetString(0));
                int occurrences = row.GetInt32(1) + 1;
                int sessions = row.GetInt32(2) + (row.IsDBNull(3) || row.GetString(3) != sessionId.ToString() ? 1 : 0);
                DateTimeOffset firstSeen = DateTimeOffset.Parse(row.GetString(4), CultureInfo.InvariantCulture);
                await row.DisposeAsync().ConfigureAwait(false);
                if (state == VocabularyState.Forgotten)
                {
                    continue;
                }

                VocabularyState next = state == VocabularyState.Candidate && occurrences >= ActivationOccurrences ? VocabularyState.Active : state;
                await RunAsync(transaction,
                    """
                    UPDATE vocabulary SET term = $term, occurrences = $occ, sessions = $sessions, last_session_id = $session,
                        last_seen_utc = $seen, state = $state WHERE key = $key;
                    """, cancellationToken,
                    ("$key", key), ("$term", observation.Text), ("$occ", occurrences), ("$sessions", sessions),
                    ("$session", sessionId.ToString()), ("$seen", Utc(seenUtc)), ("$state", next.ToString())).ConfigureAwait(false);
                if (next != state)
                {
                    activated.Add(new VocabularyTerm(observation.Text, observation.Kind, occurrences, sessions, firstSeen, seenUtc, next));
                }
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return activated;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Active terms seen within <see cref="ActiveFor"/> of <paramref name="nowUtc"/>, most frequent and recent first.</summary>
    public async Task<IReadOnlyList<VocabularyTerm>> GetActiveTermsAsync(DateTimeOffset nowUtc, int limit = 40, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<VocabularyTerm> all = await ListVocabularyAsync(cancellationToken).ConfigureAwait(false);
        return [.. all.Where(t => t.State == VocabularyState.Active && nowUtc - t.LastSeenUtc <= ActiveFor)
                      .OrderByDescending(t => t.Occurrences).ThenByDescending(t => t.LastSeenUtc).Take(limit)];
    }

    public async Task<IReadOnlyList<VocabularyTerm>> ListVocabularyAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteCommand command = Command(
                "SELECT term, kind, occurrences, sessions, first_seen_utc, last_seen_utc, state FROM vocabulary ORDER BY occurrences DESC, term;");
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var terms = new List<VocabularyTerm>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                terms.Add(new VocabularyTerm(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3),
                    DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
                    DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture), Enum.Parse<VocabularyState>(reader.GetString(6))));
            }

            return terms;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Stops using a term and keeps it from being learned again. Returns false if unknown.</summary>
    public async Task<bool> ForgetTermAsync(string term, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(term);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteCommand command = Command("UPDATE vocabulary SET state = 'Forgotten' WHERE key = $key;", ("$key", term.ToLowerInvariant()));
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Deletes everything learned, including forgotten terms.</summary>
    public Task ClearVocabularyAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync("DELETE FROM vocabulary;", cancellationToken);

    /// <summary>Deletes a meeting and everything derived from it (RF-DATA-004): sessions, segments, translations, gaps.</summary>
    public Task DeleteMeetingAsync(Guid meetingId, CancellationToken cancellationToken = default) =>
        ExecuteAsync("DELETE FROM meetings WHERE id = $id;", cancellationToken, ("$id", meetingId.ToString()));

    public async Task<string> JournalModeAsync()
    {
        await using SqliteCommand command = Command("PRAGMA journal_mode;");
        return (string)(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await ExecuteAsync("PRAGMA journal_mode = WAL; PRAGMA synchronous = FULL; PRAGMA foreign_keys = ON;", cancellationToken).ConfigureAwait(false);
        await using SqliteCommand version = Command("PRAGMA user_version;");
        long current = (long)(await version.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        if (current > SchemaVersion)
        {
            throw new InvalidOperationException($"Database schema {current} is newer than this app ({SchemaVersion}).");
        }

        if (current < 1)
        {
            await CreateSchemaV1Async(cancellationToken).ConfigureAwait(false);
        }

        if (current < 2)
        {
            // v2 (ADR-015): vocabulary learned across sessions. Not tied to a meeting: it survives meeting
            // deletion by design and is removed with ClearVocabularyAsync or by deleting the database.
            await ExecuteAsync(
                """
                BEGIN;
                CREATE TABLE vocabulary (
                    key TEXT PRIMARY KEY, term TEXT NOT NULL, kind TEXT NOT NULL,
                    occurrences INTEGER NOT NULL, sessions INTEGER NOT NULL, last_session_id TEXT,
                    first_seen_utc TEXT NOT NULL, last_seen_utc TEXT NOT NULL,
                    state TEXT NOT NULL, source TEXT NOT NULL);
                PRAGMA user_version = 2;
                COMMIT;
                """,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task CreateSchemaV1Async(CancellationToken cancellationToken)
    {
        await ExecuteAsync(
            """
            BEGIN;
            CREATE TABLE meetings (
                id TEXT PRIMARY KEY, title TEXT NOT NULL, created_utc TEXT NOT NULL, language TEXT NOT NULL);
            CREATE TABLE sessions (
                id TEXT PRIMARY KEY,
                meeting_id TEXT NOT NULL REFERENCES meetings(id) ON DELETE CASCADE,
                started_utc TEXT NOT NULL, started_timestamp INTEGER NOT NULL,
                system_source TEXT, mic_source TEXT, isolation_mode TEXT NOT NULL);
            CREATE TABLE transcript_segments (
                id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
                stream INTEGER NOT NULL, sequence INTEGER NOT NULL,
                start_sample INTEGER NOT NULL, end_sample INTEGER NOT NULL,
                text TEXT NOT NULL, state TEXT NOT NULL, forced INTEGER NOT NULL, created_utc TEXT NOT NULL,
                UNIQUE (session_id, stream, sequence));
            CREATE TABLE translation_segments (
                id TEXT PRIMARY KEY,
                segment_id TEXT NOT NULL REFERENCES transcript_segments(id) ON DELETE CASCADE,
                target_language TEXT NOT NULL, layer TEXT NOT NULL, text TEXT NOT NULL, model TEXT NOT NULL,
                created_utc TEXT NOT NULL,
                UNIQUE (segment_id, layer));
            CREATE TABLE audio_gaps (
                session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
                stream INTEGER NOT NULL, start_sample INTEGER NOT NULL, cause TEXT NOT NULL,
                length_samples INTEGER NOT NULL, missing_samples INTEGER NOT NULL, recorded_utc TEXT NOT NULL,
                PRIMARY KEY (session_id, stream, start_sample, cause));
            CREATE INDEX transcript_segments_by_time ON transcript_segments(session_id, start_sample);
            PRAGMA user_version = 1;
            COMMIT;
            """,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task ExecuteAsync(string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteCommand command = Command(sql, parameters);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RunAsync(SqliteTransaction transaction, string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using SqliteCommand command = Command(sql, parameters);
        command.Transaction = transaction;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private SqliteCommand Command(string sql, params (string Name, object? Value)[] parameters)
    {
        SqliteCommand command = _connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object? value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    private static string Utc(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
