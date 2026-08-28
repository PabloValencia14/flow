package com.pablo.flow

import android.content.Context
import android.net.Uri
import java.io.File
import java.time.Instant
import java.util.UUID

/** Procesa un audio importado conservando el archivo para reproducirlo después. */
class MeetingImportProcessor(private val context: Context, private val onStatus: (String) -> Unit = {}) {
    fun process(uri: Uri, title: String): MeetingRecord {
        val id = UUID.randomUUID().toString()
        val directory = File(context.filesDir, "meetings/$id").apply { mkdirs() }
        val fileName = (context.contentResolver.getType(uri)?.substringAfterLast('/') ?: "audio").let { extension ->
            if (extension.length in 1..8) "meeting.$extension" else "meeting.audio"
        }
        val audioFile = File(directory, fileName)
        onStatus("Copiando el audio…")
        context.contentResolver.openInputStream(uri)?.use { source ->
            audioFile.outputStream().use { target -> source.copyTo(target) }
        } ?: error("No se pudo abrir el audio seleccionado.")
        require(audioFile.length() <= 25L * 1024 * 1024) {
            "El audio supera 25 MB. Divide la grabación en partes para procesarla con la cuenta gratuita de Groq."
        }

        onStatus("Transcribiendo con timestamps…")
        val transcription = GroqApi(SecureStore(context)).transcribeDetailed(audioFile.readBytes(), fileName)
        val rawSegments = transcription.segments.mapIndexed { index, segment ->
            MeetingTranscriptSegmentRecord("$id-$index", segment.speaker, segment.startMs, segment.endMs, segment.text)
        }
        onStatus("Corrigiendo la transcripción completa…")
        val corrections = GroqApi(SecureStore(context)).correctMeetingSegments(rawSegments)
        val segments = rawSegments.map { segment ->
            corrections?.get(segment.id)?.takeIf { it.isNotBlank() }?.let { segment.copy(text = it) } ?: segment
        }
        val transcript = segments.joinToString("\n\n") { "[${timestamp(it.startMs)}] ${it.speaker}: ${it.text}" }
            .ifBlank { transcription.text }
        require(transcript.isNotBlank()) { "Groq no devolvió texto para el audio." }

        onStatus("Generando resumen contextual…")
        val summary = GroqApi(SecureStore(context)).summarizeMeeting(transcript)
        val meeting = MeetingRecord(
            id = id,
            title = summary?.title ?: title.trim().ifBlank { "Reunión importada" },
            startedAt = Instant.now().toString(),
            endedAt = Instant.now().toString(),
            durationMs = segments.maxOfOrNull { it.endMs } ?: 0L,
            summary = summary?.summary,
            transcript = transcript,
            participants = summary?.participants.orEmpty(),
            agreements = summary?.agreements.orEmpty(),
            tasks = summary?.tasks.orEmpty(),
            segments = segments,
            audioPath = audioFile.absolutePath,
            audioAssetId = id,
            audioFileName = fileName,
            audioSha256 = MeetingAudio.sha256(audioFile)
        )
        LocalStore(context).apply {
            saveMeeting(meeting)
            enqueueMeeting(meeting, FlowPreferences(context).deviceId)
            close()
        }
        FlowSyncWorker.schedule(context)
        FlowSyncWorker.runNow(context)
        return meeting
    }

    private fun timestamp(ms: Long): String {
        val seconds = (ms / 1_000).coerceAtLeast(0L)
        return "%02d:%02d".format(seconds / 60, seconds % 60)
    }
}
