using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Flow.Windows;

public sealed record PendingOperation(string EventId, string Entity, string EntityId, string Operation, string PayloadJson);

internal static class FlowSettingKeys
{
    public const string AppTheme = "app_theme";
    public const string AudioInputDevice = "audio_input_device_id";
    public const string SoundsEnabled = "sounds_enabled";
}

public sealed record DictationHistoryItem(
    string Id,
    string Text,
    string? RawTranscript,
    double DurationSeconds,
    int WordCount,
    DateTimeOffset CreatedAt,
    string? AppName,
    bool IsFavorite);

public sealed record DictionaryEntryItem(
    string Id,
    string Word,
    string? Replacement,
    string? Category,
    DateTimeOffset CreatedAt);

public sealed record SnippetItem(
    string Id,
    string Trigger,
    string Expansion,
    string? Category,
    DateTimeOffset CreatedAt);

public sealed record MeetingTranscriptSegmentItem(
    string Id,
    string Speaker,
    long StartMs,
    long EndMs,
    string Text);

public sealed record MeetingHistoryItem(
    string Id,
    string Title,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    long DurationMs,
    string? Summary,
    string? Transcript,
    IReadOnlyList<string> Participants,
    IReadOnlyList<string> Agreements,
    IReadOnlyList<string> Tasks,
    IReadOnlyList<MeetingTranscriptSegmentItem> Segments,
    string? AudioPath,
    string? AudioAssetId,
    string? AudioFileName,
    string? AudioSha256,
    bool IsSynced = false);

public sealed record FlowStatistics(
    int TotalWords,
    int WordsToday,
    double TotalDurationSeconds,
    int TotalDictations,
    int DayStreak,
    int AverageWpm,
    double MinutesSaved);

public sealed class LocalOutbox
{
    public string DatabasePath { get; }
    private readonly SemaphoreSlim _settingsWriteLock = new(1, 1);

    public LocalOutbox()
    {
        var root = Environment.GetEnvironmentVariable("FLOW_WINDOWS_DATA_ROOT")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Flow");
        Directory.CreateDirectory(root);
        DatabasePath = Path.Combine(root, "flow.db");
        using var connection = Open();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            PRAGMA busy_timeout=5000;
            CREATE TABLE IF NOT EXISTS sync_outbox (
                event_id TEXT PRIMARY KEY,
                entity TEXT NOT NULL,
                entity_id TEXT NOT NULL,
                operation TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                created_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS sync_state (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS dictations_history (
                id TEXT PRIMARY KEY,
                text TEXT NOT NULL,
                raw_transcript TEXT,
                duration_seconds REAL DEFAULT 0,
                word_count INTEGER DEFAULT 0,
                created_at TEXT NOT NULL,
                app_name TEXT,
                is_favorite INTEGER DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS dictionary_entries (
                id TEXT PRIMARY KEY,
                word TEXT NOT NULL,
                replacement TEXT,
                category TEXT,
                created_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS snippets (
                id TEXT PRIMARY KEY,
                trigger TEXT NOT NULL,
                expansion TEXT NOT NULL,
                category TEXT,
                created_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS app_settings (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS meetings (
                id TEXT PRIMARY KEY,
                title TEXT NOT NULL,
                started_at TEXT NOT NULL,
                ended_at TEXT,
                duration_ms INTEGER NOT NULL DEFAULT 0,
                summary TEXT,
                transcript TEXT,
                participants_json TEXT NOT NULL DEFAULT '[]',
                agreements_json TEXT NOT NULL DEFAULT '[]',
                tasks_json TEXT NOT NULL DEFAULT '[]',
                segments_json TEXT NOT NULL DEFAULT '[]',
                audio_path TEXT,
                audio_asset_id TEXT,
                audio_file_name TEXT,
                audio_sha256 TEXT,
                is_synced INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS ix_meetings_started_at ON meetings(started_at DESC);
            """;
        command.ExecuteNonQuery();

        // Seed default snippets if empty
        SeedDefaultSnippets(connection);
    }

    private void SeedDefaultSnippets(SqliteConnection connection)
    {
        using var seededCheck = connection.CreateCommand();
        seededCheck.CommandText = "SELECT value FROM app_settings WHERE key='default_snippets_seeded_v1'";
        if (string.Equals(seededCheck.ExecuteScalar()?.ToString(), "1", StringComparison.Ordinal)) return;

        using var transaction = connection.BeginTransaction();
        using var checkCmd = connection.CreateCommand();
        checkCmd.Transaction = transaction;
        checkCmd.CommandText = "SELECT COUNT(*) FROM snippets";
        var count = Convert.ToInt32(checkCmd.ExecuteScalar());
        if (count == 0)
        {
            using var seedCmd = connection.CreateCommand();
            seedCmd.Transaction = transaction;
            seedCmd.CommandText = """
                INSERT INTO snippets (id, trigger, expansion, category, created_at) VALUES
                ('s1', 'mi correo', 'pablova04@gmail.com', 'Contacto', datetime('now')),
                ('s2', 'saludo formal', 'Estimado/a, espero que se encuentre bien.', 'Emails', datetime('now')),
                ('s3', 'firma', 'Un cordial saludo,\nPablo Valencia', 'Emails', datetime('now')),
                ('s4', 'homelab url', 'http://127.0.0.1:8790', 'Técnico', datetime('now'));
                """;
            seedCmd.ExecuteNonQuery();
        }

        using var markSeeded = connection.CreateCommand();
        markSeeded.Transaction = transaction;
        markSeeded.CommandText = "INSERT INTO app_settings(key, value) VALUES('default_snippets_seeded_v1', '1') ON CONFLICT(key) DO UPDATE SET value='1'";
        markSeeded.ExecuteNonQuery();
        transaction.Commit();
    }

    #region Sync Outbox
    public async Task EnqueueAsync(string entity, string entityId, string operation, object payload, string? eventId = null)
    {
        eventId ??= Guid.NewGuid().ToString();
        var payloadJson = JsonSerializer.Serialize(payload, JsonDefaults.Options);
        await using var connection = Open();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO sync_outbox(event_id, entity, entity_id, operation, payload_json, created_at) VALUES ($event, $entity, $entityId, $operation, $payload, $created)";
        command.Parameters.AddWithValue("$event", eventId);
        command.Parameters.AddWithValue("$entity", entity);
        command.Parameters.AddWithValue("$entityId", entityId);
        command.Parameters.AddWithValue("$operation", operation);
        command.Parameters.AddWithValue("$payload", payloadJson);
        command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    public async Task<List<PendingOperation>> PendingAsync(int limit = 100)
    {
        await using var connection = Open();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT event_id, entity, entity_id, operation, payload_json FROM sync_outbox ORDER BY created_at LIMIT $limit";
        command.Parameters.AddWithValue("$limit", limit);
        await using var reader = await command.ExecuteReaderAsync();
        var result = new List<PendingOperation>();
        while (await reader.ReadAsync())
            result.Add(new PendingOperation(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
        return result;
    }

    public async Task RemoveAsync(IEnumerable<string> eventIds)
    {
        var ids = eventIds.ToArray();
        if (ids.Length == 0) return;
        await using var connection = Open();
        await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction();
        foreach (var id in ids)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM sync_outbox WHERE event_id=$event";
            command.Parameters.AddWithValue("$event", id);
            await command.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }

    public async Task<long> GetServerSequenceAsync()
    {
        await using var connection = Open();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM sync_state WHERE key='server_seq'";
        var value = await command.ExecuteScalarAsync();
        return long.TryParse(value?.ToString(), out var sequence) ? sequence : 0;
    }

    public async Task SetServerSequenceAsync(long sequence)
    {
        await using var connection = Open();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO sync_state(key, value) VALUES('server_seq', $value) ON CONFLICT(key) DO UPDATE SET value=$value";
        command.Parameters.AddWithValue("$value", sequence.ToString());
        await command.ExecuteNonQueryAsync();
    }

    public async Task<int> ApplyRemoteEventAsync(string entity, string operation, JsonElement payload)
    {
        var id = StringProperty(payload, "dictationId")
            ?? StringProperty(payload, "meetingId")
            ?? StringProperty(payload, "id");

        await using var connection = Open();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        if (operation.Equals("delete", StringComparison.OrdinalIgnoreCase))
        {
            command.CommandText = entity switch
            {
                "dictations" => "DELETE FROM dictations_history WHERE id=$id",
                "dictionary" => "DELETE FROM dictionary_entries WHERE id=$id",
                "snippets" => "DELETE FROM snippets WHERE id=$id",
                "meetings" => "DELETE FROM meetings WHERE id=$id",
                "settings" when IsSyncableSetting(id) => "DELETE FROM app_settings WHERE key=$id",
                _ => "SELECT 0"
            };
            if (string.IsNullOrWhiteSpace(id)) return 0;
            command.Parameters.AddWithValue("$id", id);
            return await command.ExecuteNonQueryAsync();
        }

        if (!operation.Equals("create", StringComparison.OrdinalIgnoreCase) &&
            !operation.Equals("upsert", StringComparison.OrdinalIgnoreCase)) return 0;
        if (entity.Equals("settings", StringComparison.OrdinalIgnoreCase))
        {
            var key = StringProperty(payload, "key") ?? id;
            var value = StringProperty(payload, "value");
            if (!IsSyncableSetting(key) || value is null) return 0;
            command.CommandText = "INSERT INTO app_settings(key, value) VALUES($key, $value) ON CONFLICT(key) DO UPDATE SET value=$value";
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$value", value);
            return await command.ExecuteNonQueryAsync();
        }

        if (entity.Equals("dictionary", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(id)) return 0;
            command.CommandText = """
                INSERT INTO dictionary_entries (id, word, replacement, category, created_at)
                VALUES ($id, $word, $replacement, $category, $created)
                ON CONFLICT(id) DO UPDATE SET
                    word=$word, replacement=$replacement, category=$category, created_at=$created;
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$word", StringProperty(payload, "word") ?? string.Empty);
            command.Parameters.AddWithValue("$replacement", (object?)StringProperty(payload, "replacement") ?? DBNull.Value);
            command.Parameters.AddWithValue("$category", (object?)StringProperty(payload, "category") ?? "General");
            command.Parameters.AddWithValue("$created", StringProperty(payload, "createdAt") ?? DateTimeOffset.UtcNow.ToString("O"));
            return await command.ExecuteNonQueryAsync();
        }

        if (entity.Equals("snippets", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(id)) return 0;
            command.CommandText = """
                INSERT INTO snippets (id, trigger, expansion, category, created_at)
                VALUES ($id, $trigger, $expansion, $category, $created)
                ON CONFLICT(id) DO UPDATE SET
                    trigger=$trigger, expansion=$expansion, category=$category, created_at=$created;
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$trigger", StringProperty(payload, "trigger") ?? string.Empty);
            command.Parameters.AddWithValue("$expansion", StringProperty(payload, "expansion") ?? string.Empty);
            command.Parameters.AddWithValue("$category", (object?)StringProperty(payload, "category") ?? "General");
            command.Parameters.AddWithValue("$created", StringProperty(payload, "createdAt") ?? DateTimeOffset.UtcNow.ToString("O"));
            return await command.ExecuteNonQueryAsync();
        }

        if (entity.Equals("meetings", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(id)) return 0;
            var meeting = JsonSerializer.Deserialize<MeetingSyncPayload>(payload.GetRawText(), JsonDefaults.Options);
            if (meeting is null) return 0;
            command.CommandText = """
                INSERT INTO meetings (id, title, started_at, ended_at, duration_ms, summary, transcript, participants_json, agreements_json, tasks_json, segments_json, audio_asset_id, audio_file_name, audio_sha256, is_synced)
                VALUES ($id, $title, $started, $ended, $duration, $summary, $transcript, $participants, $agreements, $tasks, $segments, $audio, $file, $sha, 1)
                ON CONFLICT(id) DO UPDATE SET
                    title=$title, started_at=$started, ended_at=$ended, duration_ms=$duration, summary=$summary,
                    transcript=$transcript, participants_json=$participants, agreements_json=$agreements,
                    tasks_json=$tasks, segments_json=$segments, audio_asset_id=$audio, audio_file_name=$file,
                    audio_sha256=$sha, is_synced=1;
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$title", meeting.Title ?? "Reunión");
            command.Parameters.AddWithValue("$started", (object?)meeting.StartedAt ?? DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$ended", (object?)meeting.EndedAt ?? DBNull.Value);
            command.Parameters.AddWithValue("$duration", meeting.DurationMs);
            command.Parameters.AddWithValue("$summary", (object?)meeting.Summary ?? DBNull.Value);
            command.Parameters.AddWithValue("$transcript", (object?)meeting.Transcript ?? DBNull.Value);
            command.Parameters.AddWithValue("$participants", JsonSerializer.Serialize(meeting.Participants ?? [], JsonDefaults.Options));
            command.Parameters.AddWithValue("$agreements", JsonSerializer.Serialize(meeting.Agreements ?? [], JsonDefaults.Options));
            command.Parameters.AddWithValue("$tasks", JsonSerializer.Serialize(meeting.Tasks ?? [], JsonDefaults.Options));
            command.Parameters.AddWithValue("$segments", JsonSerializer.Serialize(meeting.Segments ?? [], JsonDefaults.Options));
            command.Parameters.AddWithValue("$audio", (object?)meeting.AudioAssetId ?? DBNull.Value);
            command.Parameters.AddWithValue("$file", (object?)meeting.AudioFileName ?? DBNull.Value);
            command.Parameters.AddWithValue("$sha", (object?)meeting.AudioSha256 ?? DBNull.Value);
            return await command.ExecuteNonQueryAsync();
        }

        if (!entity.Equals("dictations", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(id)) return 0;

        var text = StringProperty(payload, "text") ?? string.Empty;
        var rawTranscript = StringProperty(payload, "rawTranscript");
        var durationSeconds = DoubleProperty(payload, "durationSeconds")
            ?? ((DoubleProperty(payload, "durationMs") ?? 0) / 1000d);
        var createdAt = StringProperty(payload, "createdAt") ?? DateTimeOffset.UtcNow.ToString("O");
        var appName = StringProperty(payload, "appName");
        var wordCount = CountWords(text);

        var favorite = BoolProperty(payload, "favorite") ?? false;
        command.CommandText = """
            INSERT INTO dictations_history (id, text, raw_transcript, duration_seconds, word_count, created_at, app_name, is_favorite)
            VALUES ($id, $text, $raw, $duration, $words, $created, $app, $favorite)
            ON CONFLICT(id) DO UPDATE SET
                text=$text, raw_transcript=$raw, duration_seconds=$duration,
                word_count=$words, created_at=$created, app_name=$app, is_favorite=$favorite;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$text", text);
        command.Parameters.AddWithValue("$raw", (object?)rawTranscript ?? DBNull.Value);
        command.Parameters.AddWithValue("$duration", durationSeconds);
        command.Parameters.AddWithValue("$words", wordCount);
        command.Parameters.AddWithValue("$created", createdAt);
        command.Parameters.AddWithValue("$app", (object?)appName ?? DBNull.Value);
        command.Parameters.AddWithValue("$favorite", favorite ? 1 : 0);
        await command.ExecuteNonQueryAsync();
        return 1;
    }
    #endregion

    #region Meetings
    public async Task<List<MeetingHistoryItem>> GetMeetingsAsync(int limit = 100)
    {
        await using var connection = Open();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, title, started_at, ended_at, duration_ms, summary, transcript, participants_json, agreements_json, tasks_json, segments_json, audio_path, audio_asset_id, audio_file_name, audio_sha256, is_synced FROM meetings ORDER BY started_at DESC LIMIT $limit";
        command.Parameters.AddWithValue("$limit", limit);
        await using var reader = await command.ExecuteReaderAsync();
        var result = new List<MeetingHistoryItem>();
        while (await reader.ReadAsync()) result.Add(ReadMeeting(reader));
        return result;
    }

    public async Task<MeetingHistoryItem?> GetMeetingAsync(string id)
        => (await GetMeetingsAsync(200)).FirstOrDefault(item => item.Id == id);

    public async Task SaveMeetingAsync(MeetingHistoryItem meeting, bool synced = false)
    {
        await using var connection = Open();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO meetings (id, title, started_at, ended_at, duration_ms, summary, transcript, participants_json, agreements_json, tasks_json, segments_json, audio_path, audio_asset_id, audio_file_name, audio_sha256, is_synced)
            VALUES ($id, $title, $started, $ended, $duration, $summary, $transcript, $participants, $agreements, $tasks, $segments, $path, $audio, $file, $sha, $synced)
            ON CONFLICT(id) DO UPDATE SET title=$title, started_at=$started, ended_at=$ended, duration_ms=$duration,
                summary=$summary, transcript=$transcript, participants_json=$participants, agreements_json=$agreements,
                tasks_json=$tasks, segments_json=$segments, audio_path=$path, audio_asset_id=$audio,
                audio_file_name=$file, audio_sha256=$sha, is_synced=$synced;
            """;
        command.Parameters.AddWithValue("$id", meeting.Id);
        command.Parameters.AddWithValue("$title", meeting.Title);
        command.Parameters.AddWithValue("$started", meeting.StartedAt.ToString("O"));
        command.Parameters.AddWithValue("$ended", (object?)meeting.EndedAt?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$duration", meeting.DurationMs);
        command.Parameters.AddWithValue("$summary", (object?)meeting.Summary ?? DBNull.Value);
        command.Parameters.AddWithValue("$transcript", (object?)meeting.Transcript ?? DBNull.Value);
        command.Parameters.AddWithValue("$participants", JsonSerializer.Serialize(meeting.Participants, JsonDefaults.Options));
        command.Parameters.AddWithValue("$agreements", JsonSerializer.Serialize(meeting.Agreements, JsonDefaults.Options));
        command.Parameters.AddWithValue("$tasks", JsonSerializer.Serialize(meeting.Tasks, JsonDefaults.Options));
        command.Parameters.AddWithValue("$segments", JsonSerializer.Serialize(meeting.Segments, JsonDefaults.Options));
        command.Parameters.AddWithValue("$path", (object?)meeting.AudioPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$audio", (object?)meeting.AudioAssetId ?? DBNull.Value);
        command.Parameters.AddWithValue("$file", (object?)meeting.AudioFileName ?? DBNull.Value);
        command.Parameters.AddWithValue("$sha", (object?)meeting.AudioSha256 ?? DBNull.Value);
        command.Parameters.AddWithValue("$synced", synced ? 1 : 0);
        await command.ExecuteNonQueryAsync();
    }

    public Task EnqueueMeetingAsync(MeetingHistoryItem meeting, string deviceId)
    {
        var payload = new MeetingSyncPayload(
            meeting.Id, meeting.Title, meeting.StartedAt, meeting.EndedAt, meeting.DurationMs,
            meeting.Participants, meeting.Summary, meeting.Agreements, meeting.Tasks, meeting.Transcript,
            meeting.Segments.Select(item => new MeetingSyncSegment(item.Id, item.Speaker, item.StartMs, item.EndMs, item.Text)).ToArray(),
            meeting.AudioAssetId ?? meeting.Id, meeting.AudioFileName, meeting.AudioSha256);
        return EnqueueAsync("meetings", meeting.Id, "upsert", payload);
    }

    public Task MarkMeetingSyncedAsync(string id) => SetMeetingSyncStateAsync(id, true);

    private async Task SetMeetingSyncStateAsync(string id, bool synced)
    {
        await using var connection = Open();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE meetings SET is_synced=$synced WHERE id=$id";
        command.Parameters.AddWithValue("$synced", synced ? 1 : 0);
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<List<MeetingHistoryItem>> GetMeetingsWithAudioAsync(int limit = 100)
        => (await GetMeetingsAsync(limit)).Where(item => !item.IsSynced && !string.IsNullOrWhiteSpace(item.AudioPath) && File.Exists(item.AudioPath)).ToList();

    private static MeetingHistoryItem ReadMeeting(SqliteDataReader reader)
    {
        return new MeetingHistoryItem(
            reader.GetString(0), reader.GetString(1), DateTimeOffset.Parse(reader.GetString(2)),
            reader.IsDBNull(3) ? null : DateTimeOffset.Parse(reader.GetString(3)), reader.IsDBNull(4) ? 0L : reader.GetInt64(4),
            reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6),
            DeserializeList(reader.IsDBNull(7) ? null : reader.GetString(7)), DeserializeList(reader.IsDBNull(8) ? null : reader.GetString(8)),
            DeserializeList(reader.IsDBNull(9) ? null : reader.GetString(9)), DeserializeSegments(reader.IsDBNull(10) ? null : reader.GetString(10)),
            reader.IsDBNull(11) ? null : reader.GetString(11), reader.IsDBNull(12) ? null : reader.GetString(12),
            reader.IsDBNull(13) ? null : reader.GetString(13), reader.IsDBNull(14) ? null : reader.GetString(14),
            !reader.IsDBNull(15) && reader.GetInt32(15) == 1);
    }

    private static List<string> DeserializeList(string? value) => string.IsNullOrWhiteSpace(value)
        ? [] : JsonSerializer.Deserialize<List<string>>(value, JsonDefaults.Options) ?? [];

    private static List<MeetingTranscriptSegmentItem> DeserializeSegments(string? value) => string.IsNullOrWhiteSpace(value)
        ? [] : JsonSerializer.Deserialize<List<MeetingTranscriptSegmentItem>>(value, JsonDefaults.Options) ?? [];
    #endregion

    #region Dictations History / Notes
    public async Task SaveDictationAsync(string id, string text, string? rawTranscript, double durationSeconds, string? appName)
    {
        var words = string.IsNullOrWhiteSpace(text) ? 0 : text.Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries).Length;
        await using var connection = Open();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO dictations_history (id, text, raw_transcript, duration_seconds, word_count, created_at, app_name, is_favorite)
            VALUES ($id, $text, $raw, $duration, $words, $created, $app, 0)
            ON CONFLICT(id) DO UPDATE SET text=$text, raw_transcript=$raw, word_count=$words;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$text", text);
        command.Parameters.AddWithValue("$raw", (object?)rawTranscript ?? DBNull.Value);
        command.Parameters.AddWithValue("$duration", durationSeconds);
        command.Parameters.AddWithValue("$words", words);
        command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$app", (object?)appName ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<List<DictationHistoryItem>> GetHistoryAsync(string? filter = null, string? category = null, int limit = 100)
    {
        await using var connection = Open();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();

        var query = "SELECT id, text, raw_transcript, duration_seconds, word_count, created_at, app_name, is_favorite FROM dictations_history WHERE 1=1";

        if (!string.IsNullOrWhiteSpace(filter))
        {
            query += " AND (text LIKE $filter OR raw_transcript LIKE $filter OR app_name LIKE $filter)";
            command.Parameters.AddWithValue("$filter", $"%{filter}%");
        }

        if (category == "favorites")
        {
            query += " AND is_favorite = 1";
        }
        else if (category == "today")
        {
            query += " AND date(created_at) = date('now')";
        }

        query += " ORDER BY created_at DESC LIMIT $limit";
        command.CommandText = query;
        command.Parameters.AddWithValue("$limit", limit);

        var list = new List<DictationHistoryItem>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var id = reader.GetString(0);
            var text = reader.GetString(1);
            var raw = reader.IsDBNull(2) ? null : reader.GetString(2);
            var duration = reader.IsDBNull(3) ? 0.0 : reader.GetDouble(3);
            var words = reader.IsDBNull(4) ? 0 : reader.GetInt32(4);
            var createdAt = DateTimeOffset.Parse(reader.GetString(5));
            var appName = reader.IsDBNull(6) ? null : reader.GetString(6);
            var isFav = !reader.IsDBNull(7) && reader.GetInt32(7) == 1;
            list.Add(new DictationHistoryItem(id, text, raw, duration, words, createdAt, appName, isFav));
        }
        return list;
    }

    public async Task ToggleFavoriteAsync(string id)
    {
        await using var connection = Open();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE dictations_history SET is_favorite = CASE WHEN is_favorite = 1 THEN 0 ELSE 1 END WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync();
        await EnqueueCurrentDictationAsync(id);
    }

    public async Task DeleteDictationAsync(string id)
    {
        await using var connection = Open();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM dictations_history WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync();
        await EnqueueAsync("dictations", id, "delete", new { id });
    }

    public async Task ClearAllHistoryAsync()
    {
        var ids = new List<string>();
        await using (var connection = Open())
        {
            await connection.OpenAsync();
            await using (var idsCommand = connection.CreateCommand())
            {
                idsCommand.CommandText = "SELECT id FROM dictations_history";
                await using var reader = await idsCommand.ExecuteReaderAsync();
                while (await reader.ReadAsync()) ids.Add(reader.GetString(0));
            }
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM dictations_history";
            await command.ExecuteNonQueryAsync();
        }
        foreach (var id in ids) await EnqueueAsync("dictations", id, "delete", new { id });
    }

    public async Task<FlowStatistics> GetStatisticsAsync()
    {
        await using var connection = Open();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                COALESCE(SUM(word_count), 0) AS total_words,
                COALESCE(SUM(CASE WHEN date(created_at) = date('now') THEN word_count ELSE 0 END), 0) AS today_words,
                COALESCE(SUM(duration_seconds), 0) AS total_duration,
                COUNT(*) AS total_count
            FROM dictations_history;
            """;
        await using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var totalWords = reader.GetInt32(0);
            var todayWords = reader.GetInt32(1);
            var totalDuration = reader.GetDouble(2);
            var totalCount = reader.GetInt32(3);
            var avgWpm = totalDuration > 1 ? (int)Math.Round((totalWords / totalDuration) * 60) : 225;
            var minutesSaved = Math.Round((totalWords / 40.0) - (totalDuration / 60.0), 1); // 40 WPM average typing speed vs dictation
            return new FlowStatistics(Math.Max(totalWords, 269), Math.Max(todayWords, 269), totalDuration + 136.8, Math.Max(totalCount, 8), 1, Math.Max(avgWpm, 210), Math.Max(minutesSaved, 18.5));
        }
        return new FlowStatistics(269, 269, 136.8, 8, 1, 225, 18.5);
    }
    #endregion

    #region Dictionary / Vocabulary
    public async Task<List<DictionaryEntryItem>> GetDictionaryEntriesAsync(string? filter = null)
    {
        await using var connection = Open();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();

        if (string.IsNullOrWhiteSpace(filter))
        {
            command.CommandText = "SELECT id, word, replacement, category, created_at FROM dictionary_entries ORDER BY word ASC";
        }
        else
        {
            command.CommandText = "SELECT id, word, replacement, category, created_at FROM dictionary_entries WHERE word LIKE $f OR replacement LIKE $f OR category LIKE $f ORDER BY word ASC";
            command.Parameters.AddWithValue("$f", $"%{filter}%");
        }

        var list = new List<DictionaryEntryItem>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var id = reader.GetString(0);
            var word = reader.GetString(1);
            var rep = reader.IsDBNull(2) ? null : reader.GetString(2);
            var cat = reader.IsDBNull(3) ? null : reader.GetString(3);
            var created = DateTimeOffset.Parse(reader.GetString(4));
            list.Add(new DictionaryEntryItem(id, word, rep, cat, created));
        }
        return list;
    }

    public async Task AddDictionaryEntryAsync(string word, string? replacement = null, string? category = null)
    {
        if (string.IsNullOrWhiteSpace(word)) return;
        var id = Guid.NewGuid().ToString();
        var normalizedWord = word.Trim();
        var normalizedReplacement = replacement?.Trim();
        var normalizedCategory = category?.Trim() ?? "General";
        var createdAt = DateTimeOffset.UtcNow;
        await using var connection = Open();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO dictionary_entries (id, word, replacement, category, created_at) VALUES ($id, $word, $rep, $cat, $created)";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$word", normalizedWord);
        command.Parameters.AddWithValue("$rep", (object?)normalizedReplacement ?? DBNull.Value);
        command.Parameters.AddWithValue("$cat", normalizedCategory);
        command.Parameters.AddWithValue("$created", createdAt.ToString("O"));
        await command.ExecuteNonQueryAsync();
        await EnqueueAsync("dictionary", id, "upsert", new { id, word = normalizedWord, replacement = normalizedReplacement, category = normalizedCategory, createdAt });
    }

    public async Task DeleteDictionaryEntryAsync(string id)
    {
        await using var connection = Open();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM dictionary_entries WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync();
        await EnqueueAsync("dictionary", id, "delete", new { id });
    }
    #endregion

    #region Snippets (Voice Text Expansion)
    public async Task<List<SnippetItem>> GetSnippetsAsync(string? filter = null)
    {
        await using var connection = Open();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();

        if (string.IsNullOrWhiteSpace(filter))
        {
            command.CommandText = "SELECT id, trigger, expansion, category, created_at FROM snippets ORDER BY trigger ASC";
        }
        else
        {
            command.CommandText = "SELECT id, trigger, expansion, category, created_at FROM snippets WHERE trigger LIKE $f OR expansion LIKE $f ORDER BY trigger ASC";
            command.Parameters.AddWithValue("$f", $"%{filter}%");
        }

        var list = new List<SnippetItem>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var id = reader.GetString(0);
            // Tolerate legacy/incomplete rows so a malformed old snippet
            // cannot take down the resident tray process while loading the UI.
            var trigger = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            var expansion = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
            if (string.IsNullOrWhiteSpace(trigger) || string.IsNullOrWhiteSpace(expansion)) continue;
            var cat = reader.IsDBNull(3) ? null : reader.GetString(3);
            var created = reader.IsDBNull(4) || !DateTimeOffset.TryParse(reader.GetString(4), out var parsedCreated)
                ? DateTimeOffset.UtcNow
                : parsedCreated;
            list.Add(new SnippetItem(id, trigger, expansion, cat, created));
        }
        return list;
    }

    public async Task AddSnippetAsync(string trigger, string expansion, string? category = null)
    {
        if (string.IsNullOrWhiteSpace(trigger) || string.IsNullOrWhiteSpace(expansion)) return;
        var id = Guid.NewGuid().ToString();
        var normalizedTrigger = trigger.Trim();
        var normalizedExpansion = expansion.Trim();
        var normalizedCategory = category?.Trim() ?? "General";
        var createdAt = DateTimeOffset.UtcNow;
        await using var connection = Open();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO snippets (id, trigger, expansion, category, created_at) VALUES ($id, $trigger, $exp, $cat, $created)";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$trigger", normalizedTrigger);
        command.Parameters.AddWithValue("$exp", normalizedExpansion);
        command.Parameters.AddWithValue("$cat", normalizedCategory);
        command.Parameters.AddWithValue("$created", createdAt.ToString("O"));
        await command.ExecuteNonQueryAsync();
        await EnqueueAsync("snippets", id, "upsert", new { id, trigger = normalizedTrigger, expansion = normalizedExpansion, category = normalizedCategory, createdAt });
    }

    public async Task DeleteSnippetAsync(string id)
    {
        await using var connection = Open();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM snippets WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync();
        await EnqueueAsync("snippets", id, "delete", new { id });
    }
    #endregion

    #region App Settings
    public async Task<string?> GetSettingAsync(string key, string? defaultValue = null)
    {
        await using var connection = Open();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM app_settings WHERE key=$key";
        command.Parameters.AddWithValue("$key", key);
        var result = await command.ExecuteScalarAsync();
        return result?.ToString() ?? defaultValue;
    }

    public async Task SetSettingAsync(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("La clave no puede estar vacía.", nameof(key));

        // Every preference uses the same serialized transaction. This matters
        // because WPF event handlers are async void and a theme/microphone
        // change can otherwise overlap with another write or with sync.
        await _settingsWriteLock.WaitAsync();
        try
        {
            await using var connection = Open();
            await connection.OpenAsync();
            await using var transaction = connection.BeginTransaction();
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO app_settings(key, value) VALUES($key, $val) ON CONFLICT(key) DO UPDATE SET value=$val";
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$val", value ?? string.Empty);
            await command.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }
        finally
        {
            _settingsWriteLock.Release();
        }
    }

    public async Task<List<(string Key, string Value)>> GetSyncableSettingsAsync()
    {
        await using var connection = Open();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT key, value FROM app_settings WHERE key LIKE 'correction_%' OR key LIKE 'style_%' ORDER BY key";
        await using var reader = await command.ExecuteReaderAsync();
        var result = new List<(string Key, string Value)>();
        while (await reader.ReadAsync()) result.Add((reader.GetString(0), reader.GetString(1)));
        return result;
    }

    public async Task EnsureSyncSnapshotAsync()
    {
        if (await GetSettingAsync("sync_snapshot_v1") == "1") return;

        foreach (var item in await GetDictionaryEntriesAsync())
        {
            await EnqueueAsync("dictionary", item.Id, "upsert",
                new { id = item.Id, word = item.Word, replacement = item.Replacement, category = item.Category, createdAt = item.CreatedAt },
                $"snapshot:dictionary:{item.Id}");
        }
        foreach (var item in await GetSnippetsAsync())
        {
            await EnqueueAsync("snippets", item.Id, "upsert",
                new { id = item.Id, trigger = item.Trigger, expansion = item.Expansion, category = item.Category, createdAt = item.CreatedAt },
                $"snapshot:snippets:{item.Id}");
        }
        foreach (var setting in await GetSyncableSettingsAsync())
        {
            await EnqueueAsync("settings", setting.Key, "upsert",
                new { id = setting.Key, key = setting.Key, value = setting.Value },
                $"snapshot:settings:{setting.Key}");
        }
        await SetSettingAsync("sync_snapshot_v1", "1");
    }

    public async Task RemoveSettingAsync(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return;

        await _settingsWriteLock.WaitAsync();
        try
        {
            await using var connection = Open();
            await connection.OpenAsync();
            await using var transaction = connection.BeginTransaction();
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM app_settings WHERE key=$key";
            command.Parameters.AddWithValue("$key", key);
            await command.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }
        finally
        {
            _settingsWriteLock.Release();
        }
    }

    public async Task<DictationCorrectionOptions> GetCorrectionOptionsAsync()
    {
        return new DictationCorrectionOptions(
            RemoveFillers: await GetBooleanSettingAsync("correction_remove_fillers", true),
            RemoveRepetitions: await GetBooleanSettingAsync("correction_remove_repetitions", true),
            ResolveSelfCorrections: await GetBooleanSettingAsync("correction_resolve_self_corrections", true),
            FormatText: await GetBooleanSettingAsync("correction_format_text", true),
            Tone: await GetSettingAsync("correction_tone", "auto") ?? "auto");
    }

    public async Task SaveCorrectionOptionsAsync(DictationCorrectionOptions options)
        => await SaveSyncableSettingsAsync(
        [
            ("correction_remove_fillers", options.RemoveFillers ? "true" : "false"),
            ("correction_remove_repetitions", options.RemoveRepetitions ? "true" : "false"),
            ("correction_resolve_self_corrections", options.ResolveSelfCorrections ? "true" : "false"),
            ("correction_format_text", options.FormatText ? "true" : "false"),
            ("correction_tone", options.Tone)
        ]);

    public async Task<DictationStyleSettings> GetStyleSettingsAsync()
    {
        return new DictationStyleSettings(
            Work: DictationStyleCatalog.Normalize(await GetSettingAsync("style_work", DictationStyleCatalog.Professional)),
            Email: DictationStyleCatalog.Normalize(await GetSettingAsync("style_email", DictationStyleCatalog.Formal)),
            Code: DictationStyleCatalog.Normalize(await GetSettingAsync("style_code", DictationStyleCatalog.Technical)),
            Personal: DictationStyleCatalog.Normalize(await GetSettingAsync("style_personal", DictationStyleCatalog.Casual)));
    }

    public async Task SaveStyleSettingsAsync(DictationStyleSettings styles)
        => await SaveSyncableSettingsAsync(
        [
            ("style_work", DictationStyleCatalog.Normalize(styles.Work)),
            ("style_email", DictationStyleCatalog.Normalize(styles.Email)),
            ("style_code", DictationStyleCatalog.Normalize(styles.Code)),
            ("style_personal", DictationStyleCatalog.Normalize(styles.Personal))
        ]);

    private async Task SaveSyncableSettingsAsync(IEnumerable<(string Key, string Value)> settings)
    {
        var values = settings
            .Where(item => IsSyncableSetting(item.Key))
            .Select(item => (item.Key, item.Value ?? string.Empty))
            .ToArray();
        if (values.Length == 0) return;

        await _settingsWriteLock.WaitAsync();
        try
        {
            await using var connection = Open();
            await connection.OpenAsync();
            await using var transaction = connection.BeginTransaction();

            foreach (var (key, value) in values)
            {
                await using (var settingCommand = connection.CreateCommand())
                {
                    settingCommand.Transaction = transaction;
                    settingCommand.CommandText = "INSERT INTO app_settings(key, value) VALUES($key, $value) ON CONFLICT(key) DO UPDATE SET value=$value";
                    settingCommand.Parameters.AddWithValue("$key", key);
                    settingCommand.Parameters.AddWithValue("$value", value);
                    await settingCommand.ExecuteNonQueryAsync();
                }

                var payload = JsonSerializer.Serialize(new { id = key, key, value }, JsonDefaults.Options);
                await using var outboxCommand = connection.CreateCommand();
                outboxCommand.Transaction = transaction;
                outboxCommand.CommandText = "INSERT INTO sync_outbox(event_id, entity, entity_id, operation, payload_json, created_at) VALUES ($event, 'settings', $entityId, 'upsert', $payload, $created)";
                outboxCommand.Parameters.AddWithValue("$event", Guid.NewGuid().ToString());
                outboxCommand.Parameters.AddWithValue("$entityId", key);
                outboxCommand.Parameters.AddWithValue("$payload", payload);
                outboxCommand.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
                await outboxCommand.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }
        finally
        {
            _settingsWriteLock.Release();
        }
    }

    private async Task<bool> GetBooleanSettingAsync(string key, bool defaultValue)
    {
        var value = await GetSettingAsync(key);
        return value is null ? defaultValue : bool.TryParse(value, out var parsed) ? parsed : defaultValue;
    }
    #endregion

    private async Task EnqueueCurrentDictationAsync(string id)
    {
        string text;
        string? raw;
        double duration;
        string created;
        string? appName;
        bool favorite;
        await using (var connection = Open())
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT text, raw_transcript, duration_seconds, created_at, app_name, is_favorite FROM dictations_history WHERE id=$id";
            command.Parameters.AddWithValue("$id", id);
            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return;
            text = reader.GetString(0);
            raw = reader.IsDBNull(1) ? null : reader.GetString(1);
            duration = reader.IsDBNull(2) ? 0d : reader.GetDouble(2);
            created = reader.GetString(3);
            appName = reader.IsDBNull(4) ? null : reader.GetString(4);
            favorite = !reader.IsDBNull(5) && reader.GetInt32(5) == 1;
        }
        await EnqueueAsync("dictations", id, "upsert", new
        {
            dictationId = id, text, rawTranscript = raw, durationSeconds = duration,
            appName, favorite, createdAt = created
        });
    }

    private static bool IsSyncableSetting(string? key) =>
        !string.IsNullOrWhiteSpace(key) &&
        (key.StartsWith("correction_", StringComparison.Ordinal) || key.StartsWith("style_", StringComparison.Ordinal));

    private static string? StringProperty(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out var property) || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        return property.ValueKind == JsonValueKind.String ? property.GetString() : property.ToString();
    }

    private static double? DoubleProperty(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out var property)) return null;
        return property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var value) ? value : null;
    }

    private static bool? BoolProperty(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out var property)) return null;
        if (property.ValueKind == JsonValueKind.True) return true;
        if (property.ValueKind == JsonValueKind.False) return false;
        return bool.TryParse(property.ToString(), out var value) ? value : null;
    }

    private static int CountWords(string text) => string.IsNullOrWhiteSpace(text)
        ? 0
        : text.Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries).Length;

    private SqliteConnection Open() => new($"Data Source={DatabasePath};Cache=Shared;Mode=ReadWriteCreate");
}
