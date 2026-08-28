package com.pablo.flow

import android.annotation.SuppressLint
import android.Manifest
import android.content.Context
import android.content.pm.PackageManager
import android.media.AudioFormat
import android.media.AudioRecord
import android.media.MediaRecorder
import androidx.core.content.ContextCompat
import kotlin.math.max

/** Captures the Android default microphone as mono 16 kHz PCM16. */
class AudioRecorder(private val context: Context, private val onLevel: (Float) -> Unit = {}) {
    companion object {
        const val SAMPLE_RATE = 16_000
        private const val CHANNEL_COUNT = 1
        private const val BYTES_PER_SAMPLE = 2
    }

    @Volatile
    private var recording = false
    private var recorder: AudioRecord? = null
    private var worker: Thread? = null
    private var pcm = java.io.ByteArrayOutputStream()

    @Synchronized
    @SuppressLint("MissingPermission")
    fun start() {
        check(!recording) { "La captura ya está activa." }
        check(ContextCompat.checkSelfPermission(context, Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED) {
            "Android aún no ha concedido permiso para el micrófono."
        }
        val minimum = AudioRecord.getMinBufferSize(
            SAMPLE_RATE,
            AudioFormat.CHANNEL_IN_MONO,
            AudioFormat.ENCODING_PCM_16BIT
        )
        check(minimum > 0) { "Android no ofrece un buffer válido para el micrófono." }
        val bufferSize = max(minimum, SAMPLE_RATE * CHANNEL_COUNT * BYTES_PER_SAMPLE / 5)
        val created = AudioRecord(
            MediaRecorder.AudioSource.MIC,
            SAMPLE_RATE,
            AudioFormat.CHANNEL_IN_MONO,
            AudioFormat.ENCODING_PCM_16BIT,
            bufferSize
        )
        check(created.state == AudioRecord.STATE_INITIALIZED) {
            created.release()
            "No se pudo abrir el micrófono predeterminado de Android."
        }

        pcm = java.io.ByteArrayOutputStream()
        recorder = created
        recording = true
        created.startRecording()
        worker = Thread({ captureLoop(created, bufferSize) }, "flow-audio-capture").apply { start() }
    }

    fun stop(): ByteArray {
        val activeRecorder: AudioRecord
        val activeWorker: Thread?
        synchronized(this) {
            if (!recording && recorder == null) return ByteArray(0)
            recording = false
            activeRecorder = recorder ?: return ByteArray(0)
            activeWorker = worker
            runCatching { activeRecorder.stop() }
        }

        activeWorker?.join(1_500)
        synchronized(this) {
            runCatching { activeRecorder.release() }
            recorder = null
            worker = null
            onLevel(0f)
            return WavPcm.encode(pcm.toByteArray(), SAMPLE_RATE, CHANNEL_COUNT)
        }
    }

    @Synchronized
    fun isRecording(): Boolean = recording

    private fun captureLoop(activeRecorder: AudioRecord, bufferSize: Int) {
        val buffer = ByteArray(bufferSize)
        while (recording) {
            val count = activeRecorder.read(buffer, 0, buffer.size, AudioRecord.READ_BLOCKING)
            if (count <= 0) continue
            synchronized(this) { if (recording || recorder === activeRecorder) pcm.write(buffer, 0, count) }
            onLevel(levelOf(buffer, count))
        }
    }

    private fun levelOf(buffer: ByteArray, count: Int): Float {
        var peak = 0
        var index = 0
        while (index + 1 < count) {
            val sample = (buffer[index].toInt() and 0xff) or (buffer[index + 1].toInt() shl 8)
            peak = max(peak, kotlin.math.abs(sample.toShort().toInt()))
            index += 2
        }
        return (peak / 32768f).coerceIn(0f, 1f)
    }
}

object WavPcm {
    fun encode(pcm: ByteArray, sampleRate: Int, channels: Int): ByteArray {
        val dataSize = pcm.size
        val output = java.io.ByteArrayOutputStream(44 + dataSize)
        fun ascii(value: String) = output.write(value.toByteArray(Charsets.US_ASCII))
        fun littleEndian(value: Int) {
            output.write(value and 0xff)
            output.write((value shr 8) and 0xff)
            output.write((value shr 16) and 0xff)
            output.write((value shr 24) and 0xff)
        }
        fun littleEndianShort(value: Int) {
            output.write(value and 0xff)
            output.write((value shr 8) and 0xff)
        }

        ascii("RIFF")
        littleEndian(36 + dataSize)
        ascii("WAVEfmt ")
        littleEndian(16)
        littleEndianShort(1)
        littleEndianShort(channels)
        littleEndian(sampleRate)
        littleEndian(sampleRate * channels * 2)
        littleEndianShort(channels * 2)
        littleEndianShort(16)
        ascii("data")
        littleEndian(dataSize)
        output.write(pcm)
        return output.toByteArray()
    }
}
