package com.pablo.flow

import org.json.JSONArray
import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URL
import java.net.URLEncoder
import java.io.File
import android.util.Log

class FlowSyncClient(private val secureStore: SecureStore) {
    companion object {
        private const val TAG = "FlowSync"
    }
    fun push(serverUrl: String?, deviceId: String, pending: List<OutboxItem>): SyncResult {
        if (serverUrl.isNullOrBlank() || pending.isEmpty()) return SyncResult.NotNeeded
        val endpoint = serverUrl.trimEnd('/') + "/v1/sync/push"
        val request = JSONObject().apply {
            put("deviceId", deviceId)
            put("operations", JSONArray().apply {
                pending.forEach { item ->
                    put(JSONObject().apply {
                        put("eventId", item.eventId)
                        put("entity", item.entity)
                        put("entityId", item.entityId)
                        put("operation", item.operation)
                        put("payload", JSONObject(item.payloadJson))
                    })
                }
            })
        }
        val connection = openConnection(endpoint)
        return try {
            connection.outputStream.use { it.write(request.toString().toByteArray(Charsets.UTF_8)) }
            val code = connection.responseCode
            if (code !in 200..299) return SyncResult.Failed("FlowHub devolvió HTTP $code")
            val response = connection.inputStream.bufferedReader(Charsets.UTF_8).use { it.readText() }
            val acknowledged = JSONObject(response).optJSONArray("acknowledgedEventIds")
                ?.let { array -> (0 until array.length()).map { array.optString(it) }.filter { it.isNotBlank() }.toSet() }
                .orEmpty()
            SyncResult.Pushed(acknowledged)
        } catch (error: Exception) {
            SyncResult.Failed(error.message ?: "FlowHub no disponible")
        } finally {
            connection.disconnect()
        }
    }

    fun registerDevice(serverUrl: String?, deviceId: String, name: String, platform: String, version: String): Boolean {
        if (serverUrl.isNullOrBlank()) return false
        val endpoint = serverUrl.trimEnd('/') + "/v1/devices"
        val request = JSONObject().apply {
            put("deviceId", deviceId)
            put("name", name)
            put("platform", platform)
            put("version", version)
        }
        val connection = openConnection(endpoint)
        return try {
            connection.outputStream.use { it.write(request.toString().toByteArray(Charsets.UTF_8)) }
            val code = connection.responseCode
            if (code !in 200..299) Log.w(TAG, "Registro de dispositivo rechazado por FlowHub: HTTP $code")
            code in 200..299
        } catch (error: Exception) {
            Log.w(TAG, "No se pudo registrar el dispositivo en FlowHub", error)
            false
        } finally {
            connection.disconnect()
        }
    }

    fun pushMeeting(serverUrl: String?, meeting: MeetingPayload): SyncResult {
        if (serverUrl.isNullOrBlank()) return SyncResult.NotNeeded
        val endpoint = serverUrl.trimEnd('/') + "/v1/meetings"
        val request = JSONObject().apply {
            put("meetingId", meeting.meetingId)
            put("title", meeting.title)
            put("startedAt", meeting.startedAt)
            meeting.participants?.let { put("participants", JSONArray(it)) }
            meeting.summary?.let { put("summary", it) }
            meeting.agreements?.let { put("agreements", JSONArray(it)) }
            meeting.tasks?.let { put("tasks", JSONArray(it)) }
            meeting.transcript?.let { put("transcript", it) }
            put("exportToKnowledge", true)
        }
        val connection = openConnection(endpoint)
        return try {
            connection.outputStream.use { it.write(request.toString().toByteArray(Charsets.UTF_8)) }
            val code = connection.responseCode
            if (code in 200..299) SyncResult.Pushed(emptySet())
            else SyncResult.Failed("FlowHub devolvió HTTP $code")
        } catch (error: Exception) {
            SyncResult.Failed(error.message ?: "FlowHub no disponible")
        } finally {
            connection.disconnect()
        }
    }

    fun uploadMeetingAudio(serverUrl: String?, meeting: MeetingRecord): SyncResult {
        val path = meeting.audioPath?.let(::File) ?: return SyncResult.NotNeeded
        if (!path.isFile || serverUrl.isNullOrBlank()) return SyncResult.NotNeeded
        val fileName = URLEncoder.encode(meeting.audioFileName ?: path.name, Charsets.UTF_8.name())
        val endpoint = serverUrl.trimEnd('/') + "/v1/meetings/${meeting.id}/audio?filename=$fileName"
        val connection = (URL(endpoint).openConnection() as HttpURLConnection).apply {
            requestMethod = "POST"
            connectTimeout = 10_000
            readTimeout = 120_000
            doInput = true
            doOutput = true
            setFixedLengthStreamingMode(path.length())
            setRequestProperty("Content-Type", "application/octet-stream")
            meeting.audioSha256?.let { setRequestProperty("X-Flow-SHA256", it) }
            secureStore.get("flowhub_app_token")?.takeIf { it.isNotBlank() }?.let {
                setRequestProperty("Authorization", "Bearer $it")
            }
        }
        return try {
            path.inputStream().use { source -> connection.outputStream.use { target -> source.copyTo(target) } }
            if (connection.responseCode in 200..299) SyncResult.Pushed(emptySet())
            else SyncResult.Failed("FlowHub devolvió HTTP ${connection.responseCode} al subir el audio")
        } catch (error: Exception) {
            SyncResult.Failed(error.message ?: "No se pudo subir el audio de la reunión")
        } finally { connection.disconnect() }
    }

    fun checkConnection(serverUrl: String?): Boolean {
        if (serverUrl.isNullOrBlank()) return false
        val endpoint = serverUrl.trimEnd('/') + "/v1/devices"
        return try {
            val connection = (URL(endpoint).openConnection() as HttpURLConnection).apply {
                requestMethod = "GET"
                connectTimeout = 5_000
                readTimeout = 5_000
                secureStore.get("flowhub_app_token")?.takeIf { it.isNotBlank() }?.let {
                    setRequestProperty("Authorization", "Bearer $it")
                }
            }
            val code = connection.responseCode
            connection.disconnect()
            code in 200..299
        } catch (_: Exception) { false }
    }

    fun pull(serverUrl: String?, after: Long, localStore: LocalStore, limit: Int = 500): PullResult {
        if (serverUrl.isNullOrBlank()) return PullResult.NotNeeded
        val endpoint = serverUrl.trimEnd('/') + "/v1/sync/pull?after=$after&limit=${limit.coerceIn(1, 2_000)}"
        val connection = (URL(endpoint).openConnection() as HttpURLConnection).apply {
            requestMethod = "GET"
            connectTimeout = 10_000
            readTimeout = 20_000
            doInput = true
            secureStore.get("flowhub_app_token")?.takeIf { it.isNotBlank() }?.let {
                setRequestProperty("Authorization", "Bearer $it")
            }
        }
        return try {
            val code = connection.responseCode
            if (code !in 200..299) return PullResult.Failed("FlowHub devolvió HTTP $code")
            val array = JSONArray(connection.inputStream.bufferedReader(Charsets.UTF_8).use { it.readText() })
            var newest = after
            for (index in 0 until array.length()) {
                val event = array.getJSONObject(index)
                localStore.applyRemoteEvent(
                    event.optString("entity"),
                    event.optString("operation"),
                    event.optJSONObject("payload") ?: JSONObject()
                )
                newest = maxOf(newest, event.optLong("serverSeq", newest))
            }
            if (newest > after) localStore.setServerSequence(newest)
            PullResult.Applied(array.length(), newest)
        } catch (error: Exception) {
            PullResult.Failed(error.message ?: "No se pudo leer la sincronización")
        } finally {
            connection.disconnect()
        }
    }

    fun pullAndApply(serverUrl: String?, localStore: LocalStore): PullResult =
        pull(serverUrl, localStore.serverSequence(), localStore)

    private fun openConnection(endpoint: String): HttpURLConnection =
        (URL(endpoint).openConnection() as HttpURLConnection).apply {
            requestMethod = "POST"
            connectTimeout = 10_000
            readTimeout = 20_000
            doInput = true
            doOutput = true
            setRequestProperty("Content-Type", "application/json")
            secureStore.get("flowhub_app_token")?.takeIf { it.isNotBlank() }?.let {
                setRequestProperty("Authorization", "Bearer $it")
            }
        }
}

data class MeetingPayload(
    val meetingId: String,
    val title: String,
    val startedAt: String,
    val participants: List<String>?,
    val summary: String?,
    val agreements: List<String>?,
    val tasks: List<String>?,
    val transcript: String?,
    val endedAt: String? = null,
    val durationMs: Long = 0L,
    val segments: List<MeetingTranscriptSegmentRecord> = emptyList(),
    val audioAssetId: String? = null,
    val audioFileName: String? = null,
    val audioSha256: String? = null
)

sealed interface SyncResult {
    data object NotNeeded : SyncResult
    data class Pushed(val acknowledgedEventIds: Set<String>) : SyncResult
    data class Failed(val reason: String) : SyncResult
}

sealed interface PullResult {
    data object NotNeeded : PullResult
    data class Applied(val count: Int, val serverSequence: Long) : PullResult
    data class Failed(val reason: String) : PullResult
}
