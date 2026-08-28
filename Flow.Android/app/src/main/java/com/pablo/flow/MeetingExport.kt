package com.pablo.flow

import android.content.ContentValues
import android.content.Context
import android.os.Build
import android.provider.MediaStore
import java.time.Instant

object MeetingExport {
    fun markdown(meeting: MeetingRecord): String = buildString {
        appendLine("# ${meeting.title}")
        appendLine()
        appendLine("Fecha: ${meeting.startedAt}")
        appendLine("Flow meeting ID: `${meeting.id}`")
        appendLine()
        appendLine("## Resumen")
        appendLine()
        appendLine(meeting.summary ?: "—")
        appendLine()
        appendLine("## Participantes")
        appendLine()
        appendLine(meeting.participants.takeIf { it.isNotEmpty() }?.joinToString(", ") ?: "—")
        appendLine()
        appendLine("## Acuerdos")
        appendLine()
        meeting.agreements.ifEmpty { listOf("—") }.forEach { appendLine("- $it") }
        appendLine()
        appendLine("## Tareas")
        appendLine()
        meeting.tasks.ifEmpty { listOf("—") }.forEach { appendLine("- [ ] $it") }
        appendLine()
        appendLine("## Transcripción")
        appendLine()
        meeting.segments.sortedBy { it.startMs }.ifEmpty { emptyList() }.forEach {
            appendLine("- **[${timestamp(it.startMs)}] ${it.speaker}:** ${it.text}")
        }
        if (meeting.segments.isEmpty()) appendLine(meeting.transcript ?: "—")
    }

    fun text(meeting: MeetingRecord): String = meeting.segments.sortedBy { it.startMs }
        .joinToString("\n\n") { "[${timestamp(it.startMs)}] ${it.speaker}: ${it.text}" }
        .ifBlank { meeting.transcript ?: "" }

    fun writeToDownloads(context: Context, meeting: MeetingRecord, markdown: Boolean): Boolean {
        val extension = if (markdown) "md" else "txt"
        val mime = if (markdown) "text/markdown" else "text/plain"
        val values = ContentValues().apply {
            put(MediaStore.Downloads.DISPLAY_NAME, "${safeName(meeting.title)}.$extension")
            put(MediaStore.Downloads.MIME_TYPE, mime)
            if (Build.VERSION.SDK_INT >= 29) put(MediaStore.Downloads.RELATIVE_PATH, "Download/Flow")
        }
        val uri = context.contentResolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, values) ?: return false
        return runCatching {
            context.contentResolver.openOutputStream(uri)?.use { output ->
                output.write((if (markdown) markdown(meeting) else text(meeting)).toByteArray(Charsets.UTF_8))
            } ?: error("No se pudo abrir el archivo de destino.")
            true
        }.getOrElse {
            context.contentResolver.delete(uri, null, null)
            false
        }
    }

    private fun safeName(value: String): String = value.trim().ifBlank { "reunion" }
        .replace(Regex("[^A-Za-z0-9áéíóúÁÉÍÓÚñÑ _-]"), "_").take(80)

    private fun timestamp(ms: Long): String {
        val seconds = (ms / 1_000).coerceAtLeast(0L)
        return "%02d:%02d".format(seconds / 60, seconds % 60)
    }
}
