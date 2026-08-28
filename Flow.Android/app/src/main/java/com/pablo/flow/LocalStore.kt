package com.pablo.flow

import android.content.ContentValues
import android.content.Context
import android.database.sqlite.SQLiteDatabase
import android.database.sqlite.SQLiteOpenHelper
import org.json.JSONArray
import org.json.JSONObject
import java.util.UUID

class LocalStore(context: Context) : SQLiteOpenHelper(context, "flow.db", null, 4) {
    override fun onCreate(db: SQLiteDatabase) {
        db.execSQL("CREATE TABLE sync_outbox(event_id TEXT PRIMARY KEY, entity TEXT NOT NULL, entity_id TEXT NOT NULL, operation TEXT NOT NULL, payload_json TEXT NOT NULL, created_at TEXT NOT NULL)")
        db.execSQL("CREATE TABLE sync_state(key TEXT PRIMARY KEY, value TEXT NOT NULL)")
        createContentTables(db)
    }

    override fun onUpgrade(db: SQLiteDatabase, oldVersion: Int, newVersion: Int) {
        if (oldVersion < 2) createContentTables(db)
        if (oldVersion < 3) createSyncableTables(db)
        if (oldVersion < 4) addMeetingMetadataColumns(db)
    }

    fun enqueue(entity: String, entityId: String, operation: String, payloadJson: String) {
        val values = ContentValues().apply {
            put("event_id", UUID.randomUUID().toString())
            put("entity", entity)
            put("entity_id", entityId)
            put("operation", operation)
            put("payload_json", payloadJson)
            put("created_at", System.currentTimeMillis().toString())
        }
        writableDatabase.insertOrThrow("sync_outbox", null, values)
    }

    fun saveDictation(result: DictationResult, deviceId: String) {
        val values = ContentValues().apply {
            put("id", result.id)
            put("text", result.text)
            put("raw_transcript", result.rawTranscript)
            put("language", "es")
            put("duration_ms", result.durationMs)
            put("corrected", if (result.corrected) 1 else 0)
            put("transcription_model", result.transcriptionModel)
            result.correctionModel?.let { put("correction_model", it) } ?: putNull("correction_model")
            put("created_at", System.currentTimeMillis())
            put("device_id", deviceId)
            put("favorite", 0)
        }
        writableDatabase.insertWithOnConflict("dictations", null, values, SQLiteDatabase.CONFLICT_REPLACE)
    }

    fun saveMeeting(meeting: MeetingRecord, synced: Boolean = false) {
        val values = ContentValues().apply {
            put("id", meeting.id)
            put("title", meeting.title)
            put("started_at", meeting.startedAt)
            meeting.endedAt?.let { put("ended_at", it) } ?: putNull("ended_at")
            put("duration_ms", meeting.durationMs)
            meeting.summary?.let { put("summary", it) } ?: putNull("summary")
            meeting.transcript?.let { put("transcript", it) } ?: putNull("transcript")
            put("participants_json", JSONArray(meeting.participants).toString())
            put("agreements_json", JSONArray(meeting.agreements).toString())
            put("tasks_json", JSONArray(meeting.tasks).toString())
            put("segments_json", JSONArray().apply { meeting.segments.forEach { put(JSONObject().apply {
                put("segmentId", it.id); put("speaker", it.speaker); put("startMs", it.startMs); put("endMs", it.endMs); put("text", it.text)
            }) } }.toString())
            meeting.audioPath?.let { put("audio_path", it) } ?: putNull("audio_path")
            meeting.audioAssetId?.let { put("audio_asset_id", it) } ?: putNull("audio_asset_id")
            meeting.audioFileName?.let { put("audio_file_name", it) } ?: putNull("audio_file_name")
            meeting.audioSha256?.let { put("audio_sha256", it) } ?: putNull("audio_sha256")
            put("synced", if (synced) 1 else 0)
        }
        writableDatabase.insertWithOnConflict("meetings", null, values, SQLiteDatabase.CONFLICT_REPLACE)
    }

    fun enqueueMeeting(meeting: MeetingRecord, deviceId: String) {
        val payload = JSONObject().apply {
            put("meetingId", meeting.id)
            put("title", meeting.title)
            put("startedAt", meeting.startedAt)
            meeting.endedAt?.let { put("endedAt", it) }
            put("durationMs", meeting.durationMs)
            put("participants", JSONArray(meeting.participants))
            meeting.summary?.let { put("summary", it) }
            put("agreements", JSONArray(meeting.agreements))
            put("tasks", JSONArray(meeting.tasks))
            meeting.transcript?.let { put("transcript", it) }
            put("segments", JSONArray().apply { meeting.segments.forEach { segment -> put(JSONObject().apply {
                put("segmentId", segment.id); put("speaker", segment.speaker); put("startMs", segment.startMs); put("endMs", segment.endMs); put("text", segment.text)
            }) } })
            meeting.audioAssetId?.let { put("audioAssetId", it) }
            meeting.audioFileName?.let { put("audioFileName", it) }
            meeting.audioSha256?.let { put("audioSha256", it) }
            put("deviceId", deviceId)
            put("exportToKnowledge", true)
        }
        enqueue("meetings", meeting.id, "upsert", payload.toString())
    }

    fun saveMeetingSegment(meetingId: String, index: Int, audioPath: String, transcript: String?) {
        val values = ContentValues().apply {
            put("id", "$meetingId-$index")
            put("meeting_id", meetingId)
            put("segment_index", index)
            put("audio_path", audioPath)
            transcript?.let { put("transcript", it) } ?: putNull("transcript")
            put("created_at", System.currentTimeMillis())
        }
        writableDatabase.insertWithOnConflict("meeting_segments", null, values, SQLiteDatabase.CONFLICT_REPLACE)
    }

    fun markMeetingSynced(id: String) {
        writableDatabase.update("meetings", ContentValues().apply { put("synced", 1) }, "id = ?", arrayOf(id))
    }

    fun recentDictations(limit: Int = 10): List<DictationRecord> {
        val result = mutableListOf<DictationRecord>()
        readableDatabase.query("dictations", null, null, null, null, null, "created_at DESC", limit.toString()).use { cursor ->
            val id = cursor.getColumnIndexOrThrow("id")
            val text = cursor.getColumnIndexOrThrow("text")
            val raw = cursor.getColumnIndexOrThrow("raw_transcript")
            val duration = cursor.getColumnIndexOrThrow("duration_ms")
            val created = cursor.getColumnIndexOrThrow("created_at")
            while (cursor.moveToNext()) {
                result += DictationRecord(cursor.getString(id), cursor.getString(text), cursor.getString(raw), cursor.getLong(duration), cursor.getLong(created))
            }
        }
        return result
    }

    fun meetings(limit: Int = 50): List<MeetingRecord> {
        val result = mutableListOf<MeetingRecord>()
        readableDatabase.query("meetings", null, null, null, null, null, "started_at DESC", limit.toString()).use { cursor ->
            val id = cursor.getColumnIndexOrThrow("id")
            val title = cursor.getColumnIndexOrThrow("title")
            val started = cursor.getColumnIndexOrThrow("started_at")
            val ended = cursor.getColumnIndexOrThrow("ended_at")
            val duration = cursor.getColumnIndexOrThrow("duration_ms")
            val summary = cursor.getColumnIndexOrThrow("summary")
            val transcript = cursor.getColumnIndexOrThrow("transcript")
            val participants = cursor.getColumnIndexOrThrow("participants_json")
            val agreements = cursor.getColumnIndexOrThrow("agreements_json")
            val tasks = cursor.getColumnIndexOrThrow("tasks_json")
            while (cursor.moveToNext()) {
                result += MeetingRecord(
                    id = cursor.getString(id), title = cursor.getString(title), startedAt = cursor.getString(started),
                    endedAt = cursor.getStringOrNull(ended), durationMs = cursor.getLong(duration),
                    summary = cursor.getStringOrNull(summary), transcript = cursor.getStringOrNull(transcript),
                    participants = jsonArray(cursor.getString(participants)), agreements = jsonArray(cursor.getString(agreements)),
                    tasks = jsonArray(cursor.getString(tasks)), segments = jsonSegments(cursor.getStringOrNull(cursor.getColumnIndexOrThrow("segments_json"))),
                    audioPath = cursor.getStringOrNull(cursor.getColumnIndexOrThrow("audio_path")),
                    audioAssetId = cursor.getStringOrNull(cursor.getColumnIndexOrThrow("audio_asset_id")),
                    audioFileName = cursor.getStringOrNull(cursor.getColumnIndexOrThrow("audio_file_name")),
                    audioSha256 = cursor.getStringOrNull(cursor.getColumnIndexOrThrow("audio_sha256"))
                )
            }
        }
        return result
    }

    fun unsyncedMeetings(limit: Int = 20): List<MeetingRecord> {
        val result = mutableListOf<MeetingRecord>()
        readableDatabase.query("meetings", null, "synced = 0", null, null, null, "started_at ASC", limit.toString()).use { cursor ->
            val id = cursor.getColumnIndexOrThrow("id")
            val title = cursor.getColumnIndexOrThrow("title")
            val started = cursor.getColumnIndexOrThrow("started_at")
            val ended = cursor.getColumnIndexOrThrow("ended_at")
            val duration = cursor.getColumnIndexOrThrow("duration_ms")
            val summary = cursor.getColumnIndexOrThrow("summary")
            val transcript = cursor.getColumnIndexOrThrow("transcript")
            val participants = cursor.getColumnIndexOrThrow("participants_json")
            val agreements = cursor.getColumnIndexOrThrow("agreements_json")
            val tasks = cursor.getColumnIndexOrThrow("tasks_json")
            while (cursor.moveToNext()) {
                result += MeetingRecord(
                    cursor.getString(id), cursor.getString(title), cursor.getString(started), cursor.getStringOrNull(ended),
                    cursor.getLong(duration), cursor.getStringOrNull(summary), cursor.getStringOrNull(transcript),
                    jsonArray(cursor.getString(participants)), jsonArray(cursor.getString(agreements)), jsonArray(cursor.getString(tasks)),
                    jsonSegments(cursor.getStringOrNull(cursor.getColumnIndexOrThrow("segments_json"))),
                    cursor.getStringOrNull(cursor.getColumnIndexOrThrow("audio_path")), cursor.getStringOrNull(cursor.getColumnIndexOrThrow("audio_asset_id")),
                    cursor.getStringOrNull(cursor.getColumnIndexOrThrow("audio_file_name")), cursor.getStringOrNull(cursor.getColumnIndexOrThrow("audio_sha256"))
                )
            }
        }
        return result
    }

    fun serverSequence(): Long = readableDatabase.query("sync_state", arrayOf("value"), "key = ?", arrayOf("server_seq"), null, null, null).use { cursor ->
        if (cursor.moveToFirst()) cursor.getString(0).toLongOrNull() ?: 0L else 0L
    }

    fun setServerSequence(sequence: Long) {
        writableDatabase.insertWithOnConflict("sync_state", null, ContentValues().apply {
            put("key", "server_seq")
            put("value", sequence.toString())
        }, SQLiteDatabase.CONFLICT_REPLACE)
    }

    fun syncableSetting(key: String): String? = readableDatabase.query(
        "app_settings", arrayOf("value"), "key = ? AND (key LIKE 'correction_%' OR key LIKE 'style_%')",
        arrayOf(key), null, null, null
    ).use { cursor -> if (cursor.moveToFirst()) cursor.getString(0) else null }

    fun correctionOptions(): DictationCorrectionOptions = DictationCorrectionOptions(
        removeFillers = syncableSetting("correction_remove_fillers")?.toBooleanStrictOrNull() ?: true,
        removeRepetitions = syncableSetting("correction_remove_repetitions")?.toBooleanStrictOrNull() ?: true,
        resolveSelfCorrections = syncableSetting("correction_resolve_self_corrections")?.toBooleanStrictOrNull() ?: true,
        formatText = syncableSetting("correction_format_text")?.toBooleanStrictOrNull() ?: true
    )

    fun dictionaryEntries(): List<DictionaryEntryRecord> {
        val result = mutableListOf<DictionaryEntryRecord>()
        readableDatabase.query("dictionary_entries", null, null, null, null, null, "word ASC").use { cursor ->
            val id = cursor.getColumnIndexOrThrow("id")
            val word = cursor.getColumnIndexOrThrow("word")
            val replacement = cursor.getColumnIndexOrThrow("replacement")
            val category = cursor.getColumnIndexOrThrow("category")
            while (cursor.moveToNext()) {
                result += DictionaryEntryRecord(
                    cursor.getString(id), cursor.getString(word), cursor.getStringOrNull(replacement),
                    cursor.getStringOrNull(category)
                )
            }
        }
        return result
    }

    fun snippets(): List<SnippetRecord> {
        val result = mutableListOf<SnippetRecord>()
        readableDatabase.query("snippets", null, null, null, null, null, "trigger ASC").use { cursor ->
            val id = cursor.getColumnIndexOrThrow("id")
            val trigger = cursor.getColumnIndexOrThrow("trigger")
            val expansion = cursor.getColumnIndexOrThrow("expansion")
            val category = cursor.getColumnIndexOrThrow("category")
            while (cursor.moveToNext()) {
                result += SnippetRecord(
                    cursor.getString(id), cursor.getString(trigger), cursor.getString(expansion),
                    cursor.getStringOrNull(category)
                )
            }
        }
        return result
    }

    fun applyRemoteEvent(entity: String, operation: String, payload: JSONObject) {
        val id = payload.optString("dictationId").ifBlank {
            payload.optString("meetingId").ifBlank { payload.optString("id") }
        }
        if (operation.equals("delete", ignoreCase = true)) {
            val settingKey = payload.optString("key").ifBlank { id }
            if (id.isBlank() && settingKey.isBlank()) return
            when (entity) {
                "dictations" -> writableDatabase.delete("dictations", "id = ?", arrayOf(id))
                "meetings" -> writableDatabase.delete("meetings", "id = ?", arrayOf(id))
                "dictionary" -> writableDatabase.delete("dictionary_entries", "id = ?", arrayOf(id))
                "snippets" -> writableDatabase.delete("snippets", "id = ?", arrayOf(id))
                "settings" -> if (isSyncableSetting(settingKey)) writableDatabase.delete("app_settings", "key = ?", arrayOf(settingKey))
            }
            return
        }
        if (!operation.equals("create", ignoreCase = true) && !operation.equals("upsert", ignoreCase = true)) return
        when (entity) {
            "dictations" -> {
                if (id.isBlank()) return
                val values = ContentValues().apply {
                    put("id", id)
                    put("text", payload.optString("text"))
                    put("raw_transcript", payload.optString("rawTranscript", payload.optString("text")))
                    put("language", payload.optString("language", "es"))
                    put("duration_ms", payload.optLong("durationMs", 0L))
                    put("corrected", if (payload.optBoolean("corrected", false)) 1 else 0)
                    put("transcription_model", payload.optString("transcriptionModel", "remote"))
                    put("correction_model", payload.optString("correctionModel").ifBlank { null })
                    put("created_at", payload.optString("createdAt").toEpochMillis())
                    put("device_id", payload.optString("deviceId", "remote"))
                    put("favorite", if (payload.optBoolean("favorite", false)) 1 else 0)
                }
                writableDatabase.insertWithOnConflict("dictations", null, values, SQLiteDatabase.CONFLICT_REPLACE)
            }
            "meetings" -> {
                if (id.isBlank()) return
                saveMeeting(MeetingRecord(
                    id = id,
                    title = payload.optString("title", "Reunión"),
                    startedAt = payload.optString("startedAt"),
                     durationMs = payload.optLong("durationMs", 0L),
                    summary = payload.optString("summary").ifBlank { null },
                    transcript = payload.optString("transcript").ifBlank { null },
                    participants = payload.stringList("participants"),
                    agreements = payload.stringList("agreements"),
                    tasks = payload.stringList("tasks"), endedAt = payload.optString("endedAt").ifBlank { null },
                    segments = payload.meetingSegments(), audioAssetId = payload.optString("audioAssetId").ifBlank { null },
                    audioFileName = payload.optString("audioFileName").ifBlank { null }, audioSha256 = payload.optString("audioSha256").ifBlank { null }
                ), synced = true)
            }
            "dictionary" -> {
                if (id.isBlank()) return
                val values = ContentValues().apply {
                    put("id", id)
                    put("word", payload.optString("word"))
                    put("replacement", payload.optString("replacement").ifBlank { null })
                    put("category", payload.optString("category").ifBlank { "General" })
                    put("created_at", payload.optString("createdAt").toEpochMillis())
                }
                writableDatabase.insertWithOnConflict("dictionary_entries", null, values, SQLiteDatabase.CONFLICT_REPLACE)
            }
            "snippets" -> {
                if (id.isBlank()) return
                val values = ContentValues().apply {
                    put("id", id)
                    put("trigger", payload.optString("trigger"))
                    put("expansion", payload.optString("expansion"))
                    put("category", payload.optString("category").ifBlank { "General" })
                    put("created_at", payload.optString("createdAt").toEpochMillis())
                }
                writableDatabase.insertWithOnConflict("snippets", null, values, SQLiteDatabase.CONFLICT_REPLACE)
            }
            "settings" -> {
                val key = payload.optString("key").ifBlank { id }
                if (!isSyncableSetting(key)) return
                writableDatabase.insertWithOnConflict("app_settings", null, ContentValues().apply {
                    put("key", key)
                    put("value", payload.optString("value"))
                }, SQLiteDatabase.CONFLICT_REPLACE)
            }
        }
    }

    fun pending(limit: Int = 50): List<OutboxItem> {
        val result = mutableListOf<OutboxItem>()
        readableDatabase.query(
            "sync_outbox",
            arrayOf("event_id", "entity", "entity_id", "operation", "payload_json", "created_at"),
            null,
            null,
            null,
            null,
            "created_at ASC",
            limit.toString()
        ).use { cursor ->
            while (cursor.moveToNext()) {
                result += OutboxItem(
                    eventId = cursor.getString(0),
                    entity = cursor.getString(1),
                    entityId = cursor.getString(2),
                    operation = cursor.getString(3),
                    payloadJson = cursor.getString(4),
                    createdAt = cursor.getString(5)
                )
            }
        }
        return result
    }

    fun remove(eventIds: Collection<String>) {
        if (eventIds.isEmpty()) return
        writableDatabase.beginTransaction()
        try {
            eventIds.forEach { writableDatabase.delete("sync_outbox", "event_id = ?", arrayOf(it)) }
            writableDatabase.setTransactionSuccessful()
        } finally {
            writableDatabase.endTransaction()
        }
    }

    private fun createContentTables(db: SQLiteDatabase) {
        db.execSQL("CREATE TABLE IF NOT EXISTS dictations(id TEXT PRIMARY KEY, text TEXT NOT NULL, raw_transcript TEXT NOT NULL, language TEXT NOT NULL, duration_ms INTEGER NOT NULL, corrected INTEGER NOT NULL DEFAULT 0, transcription_model TEXT NOT NULL, correction_model TEXT, created_at INTEGER NOT NULL, device_id TEXT NOT NULL, favorite INTEGER NOT NULL DEFAULT 0)")
        db.execSQL("CREATE INDEX IF NOT EXISTS ix_dictations_created_at ON dictations(created_at DESC)")
        db.execSQL("CREATE TABLE IF NOT EXISTS meetings(id TEXT PRIMARY KEY, title TEXT NOT NULL, started_at TEXT NOT NULL, ended_at TEXT, duration_ms INTEGER NOT NULL DEFAULT 0, summary TEXT, transcript TEXT, participants_json TEXT NOT NULL DEFAULT '[]', agreements_json TEXT NOT NULL DEFAULT '[]', tasks_json TEXT NOT NULL DEFAULT '[]', segments_json TEXT NOT NULL DEFAULT '[]', audio_path TEXT, audio_asset_id TEXT, audio_file_name TEXT, audio_sha256 TEXT, synced INTEGER NOT NULL DEFAULT 0)")
        db.execSQL("CREATE TABLE IF NOT EXISTS meeting_segments(id TEXT PRIMARY KEY, meeting_id TEXT NOT NULL, segment_index INTEGER NOT NULL, audio_path TEXT NOT NULL, transcript TEXT, created_at INTEGER NOT NULL, FOREIGN KEY(meeting_id) REFERENCES meetings(id) ON DELETE CASCADE)")
        db.execSQL("CREATE INDEX IF NOT EXISTS ix_meeting_segments_meeting ON meeting_segments(meeting_id, segment_index)")
        createSyncableTables(db)
    }

    private fun addMeetingMetadataColumns(db: SQLiteDatabase) {
        listOf(
            "ALTER TABLE meetings ADD COLUMN segments_json TEXT NOT NULL DEFAULT '[]'",
            "ALTER TABLE meetings ADD COLUMN audio_path TEXT",
            "ALTER TABLE meetings ADD COLUMN audio_asset_id TEXT",
            "ALTER TABLE meetings ADD COLUMN audio_file_name TEXT",
            "ALTER TABLE meetings ADD COLUMN audio_sha256 TEXT"
        ).forEach { statement -> runCatching { db.execSQL(statement) } }
    }

    private fun createSyncableTables(db: SQLiteDatabase) {
        db.execSQL("CREATE TABLE IF NOT EXISTS dictionary_entries(id TEXT PRIMARY KEY, word TEXT NOT NULL, replacement TEXT, category TEXT, created_at INTEGER NOT NULL)")
        db.execSQL("CREATE INDEX IF NOT EXISTS ix_dictionary_word ON dictionary_entries(word)")
        db.execSQL("CREATE TABLE IF NOT EXISTS snippets(id TEXT PRIMARY KEY, trigger TEXT NOT NULL, expansion TEXT NOT NULL, category TEXT, created_at INTEGER NOT NULL)")
        db.execSQL("CREATE INDEX IF NOT EXISTS ix_snippets_trigger ON snippets(trigger)")
        db.execSQL("CREATE TABLE IF NOT EXISTS app_settings(key TEXT PRIMARY KEY, value TEXT NOT NULL)")
    }

    private fun isSyncableSetting(key: String): Boolean =
        key.startsWith("correction_") || key.startsWith("style_")

    private fun jsonArray(value: String?): List<String> = runCatching {
        val array = JSONArray(value ?: "[]")
        (0 until array.length()).map { array.optString(it) }.filter { it.isNotBlank() }
    }.getOrDefault(emptyList())
}

data class OutboxItem(
    val eventId: String,
    val entity: String,
    val entityId: String,
    val operation: String,
    val payloadJson: String,
    val createdAt: String
)

data class DictationRecord(val id: String, val text: String, val rawTranscript: String, val durationMs: Long, val createdAt: Long)

data class DictionaryEntryRecord(val id: String, val word: String, val replacement: String?, val category: String?)

data class SnippetRecord(val id: String, val trigger: String, val expansion: String, val category: String?)

data class MeetingRecord(
    val id: String,
    val title: String,
    val startedAt: String,
    val endedAt: String?,
    val durationMs: Long,
    val summary: String?,
    val transcript: String?,
    val participants: List<String> = emptyList(),
    val agreements: List<String> = emptyList(),
    val tasks: List<String> = emptyList(),
    val segments: List<MeetingTranscriptSegmentRecord> = emptyList(),
    val audioPath: String? = null,
    val audioAssetId: String? = null,
    val audioFileName: String? = null,
    val audioSha256: String? = null
)

data class MeetingTranscriptSegmentRecord(
    val id: String,
    val speaker: String,
    val startMs: Long,
    val endMs: Long,
    val text: String
)

private fun android.database.Cursor.getStringOrNull(index: Int): String? = if (isNull(index)) null else getString(index)

private fun JSONObject.stringList(name: String): List<String> {
    val array = optJSONArray(name) ?: return emptyList()
    return (0 until array.length()).map { array.optString(it) }.filter { it.isNotBlank() }
}

private fun JSONObject.meetingSegments(): List<MeetingTranscriptSegmentRecord> = buildList {
    val array = optJSONArray("segments") ?: return@buildList
    for (index in 0 until array.length()) {
        val item = array.optJSONObject(index) ?: continue
        val text = item.optString("text").trim()
        if (text.isBlank()) continue
        add(MeetingTranscriptSegmentRecord(
            id = item.optString("segmentId").ifBlank { item.optString("id").ifBlank { UUID.randomUUID().toString() } },
            speaker = item.optString("speaker", "Persona 1"), startMs = item.optLong("startMs", 0L), endMs = item.optLong("endMs", 0L), text = text
        ))
    }
}

private fun jsonSegments(value: String?): List<MeetingTranscriptSegmentRecord> = runCatching {
    JSONObject().put("segments", JSONArray(value ?: "[]")).meetingSegments()
}.getOrDefault(emptyList())

private fun String.toEpochMillis(): Long = toLongOrNull() ?: runCatching {
    java.time.Instant.parse(this).toEpochMilli()
}.getOrDefault(System.currentTimeMillis())
