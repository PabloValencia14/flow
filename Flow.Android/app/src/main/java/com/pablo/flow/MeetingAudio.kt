package com.pablo.flow

import java.io.BufferedInputStream
import java.io.BufferedOutputStream
import java.io.File
import java.io.FileInputStream
import java.io.FileOutputStream
import java.security.MessageDigest

object MeetingAudio {
    /** Une WAV PCM16 mono del grabador sin cargar toda la reunión en memoria. */
    fun mergeWav(inputs: List<File>, output: File): File {
        require(inputs.isNotEmpty()) { "No hay segmentos de audio para unir." }
        output.parentFile?.mkdirs()
        var dataBytes = 0L
        BufferedOutputStream(FileOutputStream(output)).use { target ->
            val header = inputs.first().inputStream().use { it.readNBytes(44) }
            require(header.size == 44 && String(header, 0, 4, Charsets.US_ASCII) == "RIFF") { "El audio grabado no tiene formato WAV válido." }
            target.write(header)
            inputs.forEach { file ->
                BufferedInputStream(FileInputStream(file)).use { source ->
                    source.skip(44)
                    val buffer = ByteArray(64 * 1024)
                    while (true) {
                        val count = source.read(buffer)
                        if (count <= 0) break
                        target.write(buffer, 0, count)
                        dataBytes += count
                    }
                }
            }
        }
        java.io.RandomAccessFile(output, "rw").use { file ->
            file.seek(4); writeLittleEndianInt(file, (36L + dataBytes).coerceAtMost(Int.MAX_VALUE.toLong()).toInt())
            file.seek(40); writeLittleEndianInt(file, dataBytes.coerceAtMost(Int.MAX_VALUE.toLong()).toInt())
        }
        return output
    }

    fun sha256(file: File): String {
        val digest = MessageDigest.getInstance("SHA-256")
        file.inputStream().use { stream ->
            val buffer = ByteArray(64 * 1024)
            while (true) {
                val count = stream.read(buffer)
                if (count <= 0) break
                digest.update(buffer, 0, count)
            }
        }
        return digest.digest().joinToString("") { "%02x".format(it) }
    }

    private fun writeLittleEndianInt(file: java.io.RandomAccessFile, value: Int) {
        file.write(value and 0xff)
        file.write((value ushr 8) and 0xff)
        file.write((value ushr 16) and 0xff)
        file.write((value ushr 24) and 0xff)
    }
}
