using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Data.Sqlite;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService();

var dataRoot = Environment.GetEnvironmentVariable("FLOW_HUB_DATA_ROOT")
    ?? builder.Configuration["FlowHub:DataRoot"]
    ?? Path.Combine(AppContext.BaseDirectory, "data");
var knowledgeRoot = Environment.GetEnvironmentVariable("FLOW_HUB_KNOWLEDGE_ROOT")
    ?? builder.Configuration["FlowHub:KnowledgeRoot"]
    ?? Path.Combine(dataRoot, "knowledge-export");
var databasePath = Environment.GetEnvironmentVariable("FLOW_HUB_DATABASE")
    ?? builder.Configuration["FlowHub:Database"]
    ?? Path.Combine(dataRoot, "flow.db");
var listenUrl = Environment.GetEnvironmentVariable("FLOW_HUB_URLS")
    ?? builder.Configuration["FlowHub:Urls"]
    ?? "http://127.0.0.1:8790";

Directory.CreateDirectory(dataRoot);
Directory.CreateDirectory(knowledgeRoot);
builder.WebHost.UseUrls(listenUrl);
builder.Services.Configure<JsonOptions>(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.CamelCase;
});
builder.Services.AddSingleton(new FlowStore(databasePath, knowledgeRoot));
builder.Services.AddSingleton<FlowEventBus>();

var app = builder.Build();
app.UseWebSockets();

var appToken = Environment.GetEnvironmentVariable("FLOW_HUB_APP_TOKEN");
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/v1") && !string.IsNullOrWhiteSpace(appToken))
    {
        var supplied = context.Request.Headers.Authorization.ToString();
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(supplied),
                Encoding.UTF8.GetBytes($"Bearer {appToken}")))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "application_auth_required" });
            return;
        }
    }

    await next();
});

var store = app.Services.GetRequiredService<FlowStore>();
await store.InitializeAsync();

app.MapGet("/healthz", async () =>
{
    var dbOk = await store.CanReadAsync();
    return dbOk
        ? Results.Ok(new { status = "ok", service = "flow-hub", database = "ok" })
        : Results.Json(new { status = "degraded", service = "flow-hub", database = "error" }, statusCode: 503);
});

app.MapGet("/v1/devices", async () => Results.Ok(await store.ListDevicesAsync()));

app.MapPost("/v1/devices", async (DeviceRequest request, FlowEventBus bus) =>
{
    if (string.IsNullOrWhiteSpace(request.DeviceId) || string.IsNullOrWhiteSpace(request.Name))
        return Results.BadRequest(new { error = "device_id_and_name_required" });

    var device = await store.UpsertDeviceAsync(request);
    if (device.Event is not null)
        await bus.PublishAsync(device.Event);
    return Results.Ok(device.Device);
});

app.MapGet("/v1/sync/pull", async (long? after, int? limit) =>
{
    var safeAfter = Math.Max(0, after ?? 0);
    var safeLimit = Math.Clamp(limit ?? 500, 1, 2_000);
    return Results.Ok(await store.PullAsync(safeAfter, safeLimit));
});

app.MapPost("/v1/sync/push", async (SyncPushRequest request, FlowEventBus bus) =>
{
    if (string.IsNullOrWhiteSpace(request.DeviceId))
        return Results.BadRequest(new { error = "device_id_required" });
    if (request.Operations is null || request.Operations.Count == 0)
        return Results.BadRequest(new { error = "operations_required" });
    if (request.Operations.Count > 500)
        return Results.BadRequest(new { error = "too_many_operations", max = 500 });

    var events = await store.PushAsync(request.DeviceId, request.Operations);
    foreach (var item in events)
        await bus.PublishAsync(item);
    return Results.Ok(new
    {
        accepted = events.Count,
        acknowledgedEventIds = request.Operations
            .Where(item => !string.IsNullOrWhiteSpace(item.EventId))
            .Select(item => item.EventId!)
            .Distinct(StringComparer.Ordinal)
            .ToArray(),
        serverSeq = events.Count == 0 ? (long?)null : events[^1].ServerSeq,
        events
    });
});

app.MapPost("/v1/meetings", async (MeetingRequest request, FlowEventBus bus) =>
{
    if (string.IsNullOrWhiteSpace(request.Title))
        return Results.BadRequest(new { error = "title_required" });

    var result = await store.CreateMeetingAsync(request);
    if (result.Event is not null)
        await bus.PublishAsync(result.Event);
    return Results.Created($"/v1/meetings/{result.Meeting.MeetingId}", result.Meeting);
});

app.MapGet("/v1/meetings", async () => Results.Ok(await store.ListMeetingsAsync()));

app.MapPost("/v1/meetings/{meetingId}/audio", async (string meetingId, HttpRequest request) =>
{
    if (string.IsNullOrWhiteSpace(meetingId) || request.ContentLength is null or <= 0)
        return Results.BadRequest(new { error = "meeting_id_and_audio_required" });
    var result = await store.SaveMeetingAudioAsync(meetingId, request.Body, request.ContentLength.Value,
        request.Headers["X-Flow-SHA256"].ToString(), request.Query["filename"].ToString());
    return result is null ? Results.NotFound(new { error = "meeting_not_found" }) : Results.Ok(result);
});

app.MapGet("/v1/meetings/{meetingId}/audio", async (string meetingId) =>
{
    var result = await store.GetMeetingAudioAsync(meetingId);
    return result is null ? Results.NotFound(new { error = "audio_not_found" }) : Results.File(result.Value.Path, result.Value.ContentType, enableRangeProcessing: true);
});

app.Map("/v1/events/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    var bus = context.RequestServices.GetRequiredService<FlowEventBus>();
    using var subscription = bus.Subscribe(socket);
    var buffer = new byte[1024];

    try
    {
        while (socket.State == WebSocketState.Open)
        {
            var received = await socket.ReceiveAsync(buffer, context.RequestAborted);
            if (received.MessageType == WebSocketMessageType.Close)
                break;
        }
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
    {
    }
    catch (WebSocketException)
    {
    }
    finally
    {
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closed", CancellationToken.None); }
            catch (WebSocketException) { }
        }
    }
});

await app.RunAsync();

public sealed record DeviceRequest(string DeviceId, string Name, string Platform, string? Version);

public sealed record SyncPushRequest(string DeviceId, List<SyncOperationRequest> Operations);

public sealed record SyncOperationRequest(
    string? EventId,
    string Entity,
    string EntityId,
    string Operation,
    JsonElement Payload);

public sealed record MeetingRequest(
    string Title,
    DateTimeOffset? StartedAt,
    List<string>? Participants,
    string? Summary,
    List<string>? Agreements,
    List<string>? Tasks,
    string? Transcript,
    bool ExportToKnowledge = true,
    string? MeetingId = null,
    DateTimeOffset? EndedAt = null,
    long DurationMs = 0,
    List<MeetingSegmentRequest>? Segments = null,
    string? AudioAssetId = null,
    string? AudioFileName = null,
    string? AudioSha256 = null);

public sealed record MeetingSegmentRequest(
    string SegmentId,
    string Speaker,
    long StartMs,
    long EndMs,
    string Text);

public sealed record DeviceView(
    string DeviceId,
    string Name,
    string Platform,
    string? Version,
    DateTimeOffset LastSeen,
    long LastServerSeq);

public sealed record SyncEvent(
    long ServerSeq,
    string EventId,
    string DeviceId,
    string Entity,
    string EntityId,
    string Operation,
    JsonElement Payload,
    DateTimeOffset CreatedAt);

public sealed record MeetingView(
    string MeetingId,
    string Title,
    DateTimeOffset StartedAt,
    string? Summary,
    string? KnowledgePath,
    string? TextPath = null,
    DateTimeOffset? EndedAt = null,
    long DurationMs = 0,
    string? AudioAssetId = null);

public sealed record DeviceWriteResult(DeviceView Device, SyncEvent? Event);

public sealed record MeetingWriteResult(MeetingView Meeting, SyncEvent? Event);

public sealed record MeetingSyncPayload(
    string? MeetingId,
    string? Title,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    long DurationMs,
    List<string>? Participants,
    string? Summary,
    List<string>? Agreements,
    List<string>? Tasks,
    string? Transcript,
    List<MeetingSegmentRequest>? Segments,
    string? AudioAssetId,
    string? AudioFileName,
    string? AudioSha256,
    bool ExportToKnowledge = true);

public sealed record MeetingExportPaths(string? MarkdownPath, string? TextPath);

public sealed record AudioAssetView(string AssetId, string Path, string ContentType, string Sha256, long Length);

public sealed class FlowEventBus
{
    private readonly ConcurrentDictionary<Guid, WebSocket> _sockets = new();

    public IDisposable Subscribe(WebSocket socket)
    {
        var id = Guid.NewGuid();
        _sockets[id] = socket;
        return new Subscription(() => _sockets.TryRemove(id, out _));
    }

    public async Task PublishAsync(SyncEvent item)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(item, JsonDefaults.Options);
        var dead = new List<Guid>();
        foreach (var pair in _sockets)
        {
            if (pair.Value.State != WebSocketState.Open)
            {
                dead.Add(pair.Key);
                continue;
            }

            try
            {
                await pair.Value.SendAsync(json, WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch (WebSocketException)
            {
                dead.Add(pair.Key);
            }
        }

        foreach (var id in dead)
            _sockets.TryRemove(id, out _);
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}

public sealed class FlowStore(string databasePath, string knowledgeRoot)
{
    private readonly string _databasePath = databasePath;
    private readonly string _knowledgeRoot = knowledgeRoot;
    private readonly string _audioRoot = Path.Combine(Path.GetDirectoryName(databasePath) ?? AppContext.BaseDirectory, "audio-assets");
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        await using var connection = OpenConnection();
        await connection.OpenAsync();
        Directory.CreateDirectory(_audioRoot);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA foreign_keys=ON;
            CREATE TABLE IF NOT EXISTS devices (
                device_id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                platform TEXT NOT NULL,
                version TEXT,
                last_seen TEXT NOT NULL,
                last_server_seq INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS settings (
                key TEXT PRIMARY KEY,
                value_json TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS app_profiles (
                profile_id TEXT PRIMARY KEY,
                payload_json TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS dictionary (
                entry_id TEXT PRIMARY KEY,
                payload_json TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS dictionary_aliases (
                alias_id TEXT PRIMARY KEY,
                payload_json TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS snippets (
                snippet_id TEXT PRIMARY KEY,
                payload_json TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS dictations (
                dictation_id TEXT PRIMARY KEY,
                payload_json TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS corrections (
                correction_id TEXT PRIMARY KEY,
                payload_json TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS transforms (
                transform_id TEXT PRIMARY KEY,
                payload_json TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS meetings (
                meeting_id TEXT PRIMARY KEY,
                title TEXT NOT NULL,
                started_at TEXT NOT NULL,
                summary TEXT,
                knowledge_path TEXT
            );
            CREATE TABLE IF NOT EXISTS meeting_segments (
                segment_id TEXT PRIMARY KEY,
                meeting_id TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                FOREIGN KEY(meeting_id) REFERENCES meetings(meeting_id)
            );
            CREATE TABLE IF NOT EXISTS audio_assets (
                asset_id TEXT PRIMARY KEY,
                meeting_id TEXT NOT NULL,
                path TEXT NOT NULL,
                sha256 TEXT,
                FOREIGN KEY(meeting_id) REFERENCES meetings(meeting_id)
            );
            CREATE TABLE IF NOT EXISTS sync_events (
                server_seq INTEGER PRIMARY KEY AUTOINCREMENT,
                event_id TEXT NOT NULL UNIQUE,
                device_id TEXT NOT NULL,
                entity TEXT NOT NULL,
                entity_id TEXT NOT NULL,
                operation TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                created_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_sync_events_entity ON sync_events(entity, entity_id);
            CREATE TABLE IF NOT EXISTS device_state (
                device_id TEXT PRIMARY KEY,
                last_server_seq INTEGER NOT NULL DEFAULT 0
            );
            """;
        await command.ExecuteNonQueryAsync();
        foreach (var statement in new[]
        {
            "ALTER TABLE meetings ADD COLUMN ended_at TEXT",
            "ALTER TABLE meetings ADD COLUMN duration_ms INTEGER NOT NULL DEFAULT 0",
            "ALTER TABLE meetings ADD COLUMN text_path TEXT",
            "ALTER TABLE meetings ADD COLUMN audio_asset_id TEXT",
            "ALTER TABLE meetings ADD COLUMN audio_file_name TEXT",
            "ALTER TABLE meetings ADD COLUMN audio_sha256 TEXT"
        })
        {
            try
            {
                await using var alter = connection.CreateCommand();
                alter.CommandText = statement;
                await alter.ExecuteNonQueryAsync();
            }
            catch (SqliteException)
            {
                // The column already exists on an upgraded FlowHub database.
            }
        }
    }

    public async Task<bool> CanReadAsync()
    {
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM sync_events LIMIT 1";
            await command.ExecuteScalarAsync();
            return true;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    public async Task<List<DeviceView>> ListDevicesAsync()
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT device_id, name, platform, version, last_seen, last_server_seq FROM devices ORDER BY name";
        await using var reader = await command.ExecuteReaderAsync();
        var result = new List<DeviceView>();
        while (await reader.ReadAsync())
        {
            result.Add(new DeviceView(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                DateTimeOffset.Parse(reader.GetString(4)), reader.GetInt64(5)));
        }
        return result;
    }

    public async Task<DeviceWriteResult> UpsertDeviceAsync(DeviceRequest request)
    {
        var now = DateTimeOffset.UtcNow;
        await _writeLock.WaitAsync();
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync();
            using var transaction = connection.BeginTransaction();
            (string Name, string Platform, string? Version, long LastServerSeq)? existing = null;
            await using (var lookup = connection.CreateCommand())
            {
                lookup.Transaction = transaction;
                lookup.CommandText = "SELECT name, platform, version, last_server_seq FROM devices WHERE device_id=$id";
                lookup.Parameters.AddWithValue("$id", request.DeviceId);
                await using var reader = await lookup.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    existing = (
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.IsDBNull(2) ? null : reader.GetString(2),
                        reader.GetInt64(3));
                }
            }

            var changed = existing is null || existing.Value.Name != request.Name ||
                existing.Value.Platform != request.Platform || existing.Value.Version != request.Version;
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO devices(device_id, name, platform, version, last_seen)
                    VALUES ($id, $name, $platform, $version, $seen)
                    ON CONFLICT(device_id) DO UPDATE SET
                        name=excluded.name, platform=excluded.platform,
                        version=excluded.version, last_seen=excluded.last_seen
                    """;
                command.Parameters.AddWithValue("$id", request.DeviceId);
                command.Parameters.AddWithValue("$name", request.Name);
                command.Parameters.AddWithValue("$platform", request.Platform);
                command.Parameters.AddWithValue("$version", (object?)request.Version ?? DBNull.Value);
                command.Parameters.AddWithValue("$seen", now.ToString("O"));
                await command.ExecuteNonQueryAsync();
            }

            SyncEvent? eventItem = null;
            if (changed)
            {
                eventItem = await InsertEventAsync(connection, transaction, request.DeviceId,
                    "devices", request.DeviceId, "upsert", JsonSerializer.SerializeToElement(request));
                await using var sequenceUpdate = connection.CreateCommand();
                sequenceUpdate.Transaction = transaction;
                sequenceUpdate.CommandText = "UPDATE devices SET last_server_seq=$seq WHERE device_id=$id";
                sequenceUpdate.Parameters.AddWithValue("$seq", eventItem.ServerSeq);
                sequenceUpdate.Parameters.AddWithValue("$id", request.DeviceId);
                await sequenceUpdate.ExecuteNonQueryAsync();
            }
            await transaction.CommitAsync();
            var device = new DeviceView(request.DeviceId, request.Name, request.Platform, request.Version, now,
                eventItem?.ServerSeq ?? existing?.LastServerSeq ?? 0);
            return new DeviceWriteResult(device, eventItem);
        }
        finally { _writeLock.Release(); }
    }

    public async Task<List<SyncEvent>> PullAsync(long after, int limit)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT server_seq, event_id, device_id, entity, entity_id, operation, payload_json, created_at
            FROM sync_events WHERE server_seq > $after ORDER BY server_seq LIMIT $limit
            """;
        command.Parameters.AddWithValue("$after", after);
        command.Parameters.AddWithValue("$limit", limit);
        return await ReadEventsAsync(command);
    }

    public async Task<List<SyncEvent>> PushAsync(string deviceId, IReadOnlyList<SyncOperationRequest> operations)
    {
        await _writeLock.WaitAsync();
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync();
            using var transaction = connection.BeginTransaction();
            var result = new List<SyncEvent>();
            foreach (var operation in operations)
            {
                if (string.IsNullOrWhiteSpace(operation.Entity) || string.IsNullOrWhiteSpace(operation.EntityId) ||
                    string.IsNullOrWhiteSpace(operation.Operation))
                    throw new ArgumentException("Each sync operation needs entity, entityId and operation.");

                var eventId = string.IsNullOrWhiteSpace(operation.EventId) ? NewUuidV7().ToString() : operation.EventId;
                var payload = operation.Payload.ValueKind == JsonValueKind.Undefined
                    ? JsonSerializer.SerializeToElement(new { })
                    : operation.Payload;
                var inserted = await InsertEventIfNewAsync(connection, transaction, eventId, deviceId,
                    operation.Entity, operation.EntityId, operation.Operation, payload);
                if (inserted is not null)
                {
                    if (operation.Entity.Equals("meetings", StringComparison.OrdinalIgnoreCase))
                        await ApplyMeetingPayloadAsync(connection, transaction, operation.EntityId, operation.Operation, payload);
                    result.Add(inserted);
                }
            }

            await transaction.CommitAsync();
            return result;
        }
        finally { _writeLock.Release(); }
    }

    private async Task ApplyMeetingPayloadAsync(SqliteConnection connection, SqliteTransaction transaction,
        string entityId, string operation, JsonElement payload)
    {
        if (operation.Equals("delete", StringComparison.OrdinalIgnoreCase))
        {
            await using var deleteSegments = connection.CreateCommand();
            deleteSegments.Transaction = transaction;
            deleteSegments.CommandText = "DELETE FROM meeting_segments WHERE meeting_id=$id; DELETE FROM meetings WHERE meeting_id=$id";
            deleteSegments.Parameters.AddWithValue("$id", entityId);
            await deleteSegments.ExecuteNonQueryAsync();
            return;
        }
        if (!operation.Equals("create", StringComparison.OrdinalIgnoreCase) &&
            !operation.Equals("upsert", StringComparison.OrdinalIgnoreCase)) return;

        var incoming = JsonSerializer.Deserialize<MeetingSyncPayload>(payload.GetRawText(), JsonDefaults.Options)
            ?? throw new InvalidDataException("Payload de reunión no válido.");
        var meetingId = string.IsNullOrWhiteSpace(incoming.MeetingId) ? entityId : incoming.MeetingId;
        var startedAt = incoming.StartedAt ?? DateTimeOffset.UtcNow;
        var request = new MeetingRequest(
            incoming.Title ?? "Reunión",
            startedAt,
            incoming.Participants,
            incoming.Summary,
            incoming.Agreements,
            incoming.Tasks,
            incoming.Transcript,
            incoming.ExportToKnowledge,
            meetingId,
            incoming.EndedAt,
            incoming.DurationMs,
            incoming.Segments,
            incoming.AudioAssetId,
            incoming.AudioFileName,
            incoming.AudioSha256);
        var exports = request.ExportToKnowledge
            ? await ExportMeetingAsync(meetingId, request, startedAt)
            : new MeetingExportPaths(null, null);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO meetings(meeting_id, title, started_at, summary, knowledge_path, ended_at, duration_ms, text_path, audio_asset_id, audio_file_name, audio_sha256)
                VALUES ($id, $title, $started, $summary, $knowledge, $ended, $duration, $text, $audio, $file, $sha)
                ON CONFLICT(meeting_id) DO UPDATE SET
                    title=excluded.title, started_at=excluded.started_at, summary=excluded.summary,
                    knowledge_path=excluded.knowledge_path, ended_at=excluded.ended_at, duration_ms=excluded.duration_ms,
                    text_path=excluded.text_path, audio_asset_id=excluded.audio_asset_id,
                    audio_file_name=excluded.audio_file_name, audio_sha256=excluded.audio_sha256;
                """;
            command.Parameters.AddWithValue("$id", meetingId);
            command.Parameters.AddWithValue("$title", request.Title);
            command.Parameters.AddWithValue("$started", startedAt.ToString("O"));
            command.Parameters.AddWithValue("$summary", (object?)request.Summary ?? DBNull.Value);
            command.Parameters.AddWithValue("$knowledge", (object?)exports.MarkdownPath ?? DBNull.Value);
            command.Parameters.AddWithValue("$ended", (object?)request.EndedAt?.ToString("O") ?? DBNull.Value);
            command.Parameters.AddWithValue("$duration", request.DurationMs);
            command.Parameters.AddWithValue("$text", (object?)exports.TextPath ?? DBNull.Value);
            command.Parameters.AddWithValue("$audio", (object?)request.AudioAssetId ?? DBNull.Value);
            command.Parameters.AddWithValue("$file", (object?)request.AudioFileName ?? DBNull.Value);
            command.Parameters.AddWithValue("$sha", (object?)request.AudioSha256 ?? DBNull.Value);
            await command.ExecuteNonQueryAsync();
        }

        await using (var clearSegments = connection.CreateCommand())
        {
            clearSegments.Transaction = transaction;
            clearSegments.CommandText = "DELETE FROM meeting_segments WHERE meeting_id=$id";
            clearSegments.Parameters.AddWithValue("$id", meetingId);
            await clearSegments.ExecuteNonQueryAsync();
        }
        foreach (var segment in request.Segments ?? [])
        {
            await using var segmentCommand = connection.CreateCommand();
            segmentCommand.Transaction = transaction;
            segmentCommand.CommandText = "INSERT INTO meeting_segments(segment_id, meeting_id, payload_json) VALUES ($segment, $meeting, $payload)";
            segmentCommand.Parameters.AddWithValue("$segment", segment.SegmentId);
            segmentCommand.Parameters.AddWithValue("$meeting", meetingId);
            segmentCommand.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(segment, JsonDefaults.Options));
            await segmentCommand.ExecuteNonQueryAsync();
        }
    }

    public async Task<AudioAssetView?> SaveMeetingAudioAsync(string meetingId, Stream source, long length, string? expectedSha256, string? requestedFileName)
    {
        await _writeLock.WaitAsync();
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync();
            await using (var check = connection.CreateCommand())
            {
                check.CommandText = "SELECT 1 FROM meetings WHERE meeting_id=$id";
                check.Parameters.AddWithValue("$id", meetingId);
                if (await check.ExecuteScalarAsync() is null) return null;
            }

            var extension = Path.GetExtension(requestedFileName ?? string.Empty);
            if (string.IsNullOrWhiteSpace(extension) || extension.Length > 8) extension = ".audio";
            var path = Path.Combine(_audioRoot, $"{Slugify(meetingId)}{extension.ToLowerInvariant()}");
            var temporary = path + ".partial";
            Directory.CreateDirectory(_audioRoot);
            await using (var target = File.Create(temporary))
            {
                await source.CopyToAsync(target);
            }
            var hash = await ComputeSha256Async(temporary);
            if (!string.IsNullOrWhiteSpace(expectedSha256) && !hash.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(temporary);
                throw new InvalidDataException("La huella SHA-256 del audio no coincide.");
            }
            File.Move(temporary, path, true);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO audio_assets(asset_id, meeting_id, path, sha256)
                VALUES ($asset, $meeting, $path, $sha)
                ON CONFLICT(asset_id) DO UPDATE SET path=$path, sha256=$sha;
                UPDATE meetings SET audio_asset_id=$asset, audio_file_name=$name, audio_sha256=$sha WHERE meeting_id=$meeting;
                """;
            command.Parameters.AddWithValue("$asset", meetingId);
            command.Parameters.AddWithValue("$meeting", meetingId);
            command.Parameters.AddWithValue("$path", path);
            command.Parameters.AddWithValue("$sha", hash);
            command.Parameters.AddWithValue("$name", Path.GetFileName(path));
            await command.ExecuteNonQueryAsync();
            return new AudioAssetView(meetingId, path, "application/octet-stream", hash, length);
        }
        finally { _writeLock.Release(); }
    }

    public async Task<(string Path, string ContentType)?> GetMeetingAudioAsync(string meetingId)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT path FROM audio_assets WHERE meeting_id=$id ORDER BY asset_id LIMIT 1";
        command.Parameters.AddWithValue("$id", meetingId);
        var path = await command.ExecuteScalarAsync() as string;
        return path is not null && File.Exists(path) ? (path, ContentType(Path.GetExtension(path))) : null;
    }

    public async Task<MeetingWriteResult> CreateMeetingAsync(MeetingRequest request)
    {
        var meetingId = string.IsNullOrWhiteSpace(request.MeetingId) ? NewUuidV7().ToString() : request.MeetingId.Trim();
        var startedAt = request.StartedAt ?? DateTimeOffset.UtcNow;

        await _writeLock.WaitAsync();
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync();
            using var transaction = connection.BeginTransaction();
            await using (var existingCommand = connection.CreateCommand())
            {
                existingCommand.Transaction = transaction;
                existingCommand.CommandText = "SELECT title, started_at, summary, knowledge_path FROM meetings WHERE meeting_id=$id";
                existingCommand.Parameters.AddWithValue("$id", meetingId);
                await using var existingReader = await existingCommand.ExecuteReaderAsync();
                if (await existingReader.ReadAsync())
                {
                    var existingView = new MeetingView(
                        meetingId,
                        existingReader.GetString(0),
                        DateTimeOffset.Parse(existingReader.GetString(1)),
                        existingReader.IsDBNull(2) ? null : existingReader.GetString(2),
                        existingReader.IsDBNull(3) ? null : existingReader.GetString(3));
                    await transaction.CommitAsync();
                    return new MeetingWriteResult(existingView, null);
                }
            }

            var exports = request.ExportToKnowledge
                ? await ExportMeetingAsync(meetingId, request, startedAt)
                : new MeetingExportPaths(null, null);
            var knowledgePath = exports.MarkdownPath;
            var payload = JsonSerializer.SerializeToElement(new
            {
                meetingId, title = request.Title, startedAt, request.EndedAt, durationMs = request.DurationMs,
                participants = request.Participants, summary = request.Summary, agreements = request.Agreements,
                tasks = request.Tasks, transcript = request.Transcript, segments = request.Segments,
                audioAssetId = request.AudioAssetId, audioFileName = request.AudioFileName,
                audioSha256 = request.AudioSha256, exportToKnowledge = request.ExportToKnowledge,
                knowledgePath, textPath = exports.TextPath
            }, JsonDefaults.Options);
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "INSERT INTO meetings(meeting_id, title, started_at, summary, knowledge_path, ended_at, duration_ms, text_path, audio_asset_id, audio_file_name, audio_sha256) VALUES ($id, $title, $started, $summary, $path, $ended, $duration, $text, $audio, $file, $sha)";
                command.Parameters.AddWithValue("$id", meetingId);
                command.Parameters.AddWithValue("$title", request.Title);
                command.Parameters.AddWithValue("$started", startedAt.ToString("O"));
                command.Parameters.AddWithValue("$summary", (object?)request.Summary ?? DBNull.Value);
                command.Parameters.AddWithValue("$path", (object?)knowledgePath ?? DBNull.Value);
                command.Parameters.AddWithValue("$ended", (object?)request.EndedAt?.ToString("O") ?? DBNull.Value);
                command.Parameters.AddWithValue("$duration", request.DurationMs);
                command.Parameters.AddWithValue("$text", (object?)exports.TextPath ?? DBNull.Value);
                command.Parameters.AddWithValue("$audio", (object?)request.AudioAssetId ?? DBNull.Value);
                command.Parameters.AddWithValue("$file", (object?)request.AudioFileName ?? DBNull.Value);
                command.Parameters.AddWithValue("$sha", (object?)request.AudioSha256 ?? DBNull.Value);
                await command.ExecuteNonQueryAsync();
            }
            var eventItem = await InsertEventIfNewAsync(connection, transaction, $"meeting:{meetingId}", "flowhub", "meetings", meetingId, "create", payload)
                ?? throw new InvalidOperationException("No se pudo registrar el evento idempotente de la reunión.");
            await transaction.CommitAsync();
            var view = new MeetingView(meetingId, request.Title, startedAt, request.Summary, knowledgePath, exports.TextPath, request.EndedAt, request.DurationMs, request.AudioAssetId);
            return new MeetingWriteResult(view, eventItem);
        }
        finally { _writeLock.Release(); }
    }

    public async Task<List<MeetingView>> ListMeetingsAsync()
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT meeting_id, title, started_at, summary, knowledge_path, text_path, ended_at, duration_ms, audio_asset_id FROM meetings ORDER BY started_at DESC";
        await using var reader = await command.ExecuteReaderAsync();
        var result = new List<MeetingView>();
        while (await reader.ReadAsync())
        {
            result.Add(new MeetingView(reader.GetString(0), reader.GetString(1), DateTimeOffset.Parse(reader.GetString(2)),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6)),
                reader.IsDBNull(7) ? 0L : reader.GetInt64(7), reader.IsDBNull(8) ? null : reader.GetString(8)));
        }
        return result;
    }

    private async Task<MeetingExportPaths> ExportMeetingAsync(string meetingId, MeetingRequest request, DateTimeOffset startedAt)
    {
        var folder = Path.Combine(_knowledgeRoot, "Vault", "Meetings", startedAt.ToString("yyyy"), startedAt.ToString("MM"));
        Directory.CreateDirectory(folder);
        var slug = Slugify(request.Title);
        var shortId = meetingId.Length >= 8 ? meetingId[..8] : meetingId;
        var path = Path.Combine(folder, $"{startedAt:yyyy-MM-dd} - {slug} - {shortId}.md");
        var textPath = Path.ChangeExtension(path, ".txt");

        var participants = request.Participants is { Count: > 0 } ? string.Join(", ", request.Participants) : "—";
        var agreements = request.Agreements is { Count: > 0 }
            ? string.Join(Environment.NewLine, request.Agreements.Select(item => $"- {item}")) : "- —";
        var tasks = request.Tasks is { Count: > 0 }
            ? string.Join(Environment.NewLine, request.Tasks.Select(item => $"- [ ] {item}")) : "- [ ] —";
        var transcriptLines = request.Segments is { Count: > 0 }
            ? request.Segments.OrderBy(item => item.StartMs).Select(item => $"- **[{FormatTimestamp(item.StartMs)}] {item.Speaker}:** {item.Text}").ToArray()
            : new[] { request.Transcript ?? "—" };
        var plainTranscript = request.Segments is { Count: > 0 }
            ? string.Join(Environment.NewLine + Environment.NewLine, request.Segments.OrderBy(item => item.StartMs).Select(item => $"[{FormatTimestamp(item.StartMs)}] {item.Speaker}: {item.Text}"))
            : request.Transcript ?? "";
        var markdown = string.Join(Environment.NewLine, new[]
        {
            $"# {request.Title}",
            "",
            $"Fecha: {startedAt:dd/MM/yyyy HH:mm} UTC",
            $"Flow meeting ID: `{meetingId}`",
            "",
            "## Participantes",
            "",
            participants,
            "",
            "## Resumen",
            "",
            request.Summary ?? "—",
            "",
            "## Acuerdos",
            "",
            agreements,
            "",
            "## Tareas",
            "",
            tasks,
            "",
            "## Transcripción",
            "",
            string.Join(Environment.NewLine, transcriptLines),
            ""
        });
        await File.WriteAllTextAsync(path, markdown.TrimStart(), Encoding.UTF8);
        await File.WriteAllTextAsync(textPath, plainTranscript, Encoding.UTF8);
        return new MeetingExportPaths(Path.GetRelativePath(_knowledgeRoot, path), Path.GetRelativePath(_knowledgeRoot, textPath));
    }

    private static string FormatTimestamp(long milliseconds)
    {
        var seconds = Math.Max(0, milliseconds / 1_000);
        return $"{seconds / 60:00}:{seconds % 60:00}";
    }

    private async Task<SyncEvent> InsertEventAsync(SqliteConnection connection, SqliteTransaction transaction,
        string deviceId, string entity, string entityId, string operation, JsonElement payload)
    {
        return (await InsertEventIfNewAsync(connection, transaction, NewUuidV7().ToString(), deviceId, entity, entityId, operation, payload))!;
    }

    private static async Task<SyncEvent?> InsertEventIfNewAsync(SqliteConnection connection, SqliteTransaction transaction,
        string eventId, string deviceId, string entity, string entityId, string operation, JsonElement payload)
    {
        var createdAt = DateTimeOffset.UtcNow;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "INSERT OR IGNORE INTO sync_events(event_id, device_id, entity, entity_id, operation, payload_json, created_at) VALUES ($event, $device, $entity, $entityId, $operation, $payload, $created)";
            command.Parameters.AddWithValue("$event", eventId);
            command.Parameters.AddWithValue("$device", deviceId);
            command.Parameters.AddWithValue("$entity", entity);
            command.Parameters.AddWithValue("$entityId", entityId);
            command.Parameters.AddWithValue("$operation", operation);
            command.Parameters.AddWithValue("$payload", payload.GetRawText());
            command.Parameters.AddWithValue("$created", createdAt.ToString("O"));
            if (await command.ExecuteNonQueryAsync() == 0)
                return null;
        }

        await using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = "SELECT server_seq FROM sync_events WHERE event_id=$event";
        select.Parameters.AddWithValue("$event", eventId);
        var sequence = (long)(await select.ExecuteScalarAsync())!;
        return new SyncEvent(sequence, eventId, deviceId, entity, entityId, operation, payload, createdAt);
    }

    private static async Task<List<SyncEvent>> ReadEventsAsync(SqliteCommand command)
    {
        await using var reader = await command.ExecuteReaderAsync();
        var result = new List<SyncEvent>();
        while (await reader.ReadAsync())
        {
            using var payload = JsonDocument.Parse(reader.GetString(6));
            result.Add(new SyncEvent(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), payload.RootElement.Clone(), DateTimeOffset.Parse(reader.GetString(7))));
        }
        return result;
    }

    private SqliteConnection OpenConnection() => new($"Data Source={_databasePath};Cache=Shared;Mode=ReadWriteCreate");

    private static async Task<string> ComputeSha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        using var hash = SHA256.Create();
        var digest = await hash.ComputeHashAsync(stream);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private static string ContentType(string extension) => extension.ToLowerInvariant() switch
    {
        ".wav" => "audio/wav",
        ".mp3" => "audio/mpeg",
        ".m4a" => "audio/mp4",
        ".ogg" => "audio/ogg",
        ".flac" => "audio/flac",
        _ => "application/octet-stream"
    };

    private static string Slugify(string value)
    {
        var chars = value.ToLowerInvariant().Select(character => char.IsLetterOrDigit(character) ? character : '-').ToArray();
        var slug = new string(chars).Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? "reunion" : slug;
    }

    private static Guid NewUuidV7()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        bytes[0] = (byte)(timestamp >> 40);
        bytes[1] = (byte)(timestamp >> 32);
        bytes[2] = (byte)(timestamp >> 24);
        bytes[3] = (byte)(timestamp >> 16);
        bytes[4] = (byte)(timestamp >> 8);
        bytes[5] = (byte)timestamp;
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x70);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes);
    }
}

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
