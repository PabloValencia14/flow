package com.pablo.flow

import android.annotation.SuppressLint
import android.Manifest
import android.content.Context
import android.content.pm.PackageManager
import android.media.AudioFormat
import android.media.AudioRecord
import android.media.MediaRecorder
import androidx.core.content.ContextCompat
import java.io.ByteArrayOutputStream
import java.io.File
import kotlin.math.max

data class MeetingAudioSegment(val index: Int, val file: File, val startedAtMs: Long, val endedAtMs: Long)

/** Long-running recorder that limits loss and memory use by rotating WAV files. */
class MeetingSegmentRecorder(
    private val context: Context,
    private val directory: File,
    private val onLevel: (Float) -> Unit = {},
    private val segmentDurationMs: Long = 5 * 60 * 1_000L
) {
    @Volatile private var recording = false
    private var recorder: AudioRecord? = null
    private var worker: Thread? = null
    private val segments = mutableListOf<MeetingAudioSegment>()

    @Synchronized
    @SuppressLint("MissingPermission")
    fun start() {
        check(!recording) { "La grabación de la reunión ya está activa." }
        check(ContextCompat.checkSelfPermission(context, Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED) {
            "Android aún no ha concedido permiso para el micrófono."
        }
        directory.mkdirs()
        val minimum = AudioRecord.getMinBufferSize(
            AudioRecorder.SAMPLE_RATE,
            AudioFormat.CHANNEL_IN_MONO,
            AudioFormat.ENCODING_PCM_16BIT
        )
        check(minimum > 0) { "Android no ofrece un buffer válido para la reunión." }
        val bufferSize = max(minimum, AudioRecorder.SAMPLE_RATE * 2 / 5)
        val created = AudioRecord(
            MediaRecorder.AudioSource.MIC,
            AudioRecorder.SAMPLE_RATE,
            AudioFormat.CHANNEL_IN_MONO,
            AudioFormat.ENCODING_PCM_16BIT,
            bufferSize
        )
        check(created.state == AudioRecord.STATE_INITIALIZED) {
            created.release()
            "No se pudo abrir el micrófono para la reunión."
        }
        segments.clear()
        recorder = created
        recording = true
        created.startRecording()
        worker = Thread({ capture(created, bufferSize) }, "flow-meeting-capture").apply { start() }
    }

    fun stop(): List<MeetingAudioSegment> {
        val active: AudioRecord
        val activeWorker: Thread?
        synchronized(this) {
            if (!recording && recorder == null) return emptyList()
            recording = false
            active = recorder ?: return emptyList()
            activeWorker = worker
            runCatching { active.stop() }
        }
        activeWorker?.join(3_000)
        synchronized(this) {
            runCatching { active.release() }
            recorder = null
            worker = null
            onLevel(0f)
            return segments.toList()
        }
    }

    @Synchronized
    fun isRecording(): Boolean = recording

    private fun capture(active: AudioRecord, bufferSize: Int) {
        var index = 0
        var startedAt = System.currentTimeMillis()
        var pcm = ByteArrayOutputStream()
        val buffer = ByteArray(bufferSize)
        try {
            while (recording) {
                val count = active.read(buffer, 0, buffer.size, AudioRecord.READ_BLOCKING)
                if (count <= 0) continue
                pcm.write(buffer, 0, count)
                onLevel(levelOf(buffer, count))
                val now = System.currentTimeMillis()
                if (now - startedAt >= segmentDurationMs) {
                    appendSegment(index++, pcm.toByteArray(), startedAt, now)
                    pcm = ByteArrayOutputStream()
                    startedAt = now
                }
            }
        } finally {
            val endedAt = System.currentTimeMillis()
            if (pcm.size() > 0) appendSegment(index, pcm.toByteArray(), startedAt, endedAt)
            onLevel(0f)
        }
    }

    private fun appendSegment(index: Int, pcm: ByteArray, startedAt: Long, endedAt: Long) {
        val file = File(directory, "segment-%04d.wav".format(index))
        file.writeBytes(WavPcm.encode(pcm, AudioRecorder.SAMPLE_RATE, 1))
        synchronized(this) { segments += MeetingAudioSegment(index, file, startedAt, endedAt) }
    }

    private fun levelOf(buffer: ByteArray, count: Int): Float {
        var peak = 0
        var index = 0
        while (index + 1 < count) {
            val value = ((buffer[index].toInt() and 0xff) or (buffer[index + 1].toInt() shl 8)).toShort().toInt()
            peak = max(peak, kotlin.math.abs(value))
            index += 2
        }
        return (peak / 32768f).coerceIn(0f, 1f)
    }
}
