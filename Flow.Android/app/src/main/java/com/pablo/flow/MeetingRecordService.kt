package com.pablo.flow

import android.app.Service
import android.content.Intent
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import android.os.Build
import android.content.pm.ServiceInfo
import androidx.core.app.ServiceCompat
import java.io.File
import java.time.Instant
import java.util.concurrent.Executors

class MeetingRecordService : Service() {
    companion object {
        const val ACTION_START = "com.pablo.flow.action.START_MEETING"
        const val ACTION_STOP = "com.pablo.flow.action.STOP_MEETING"
        const val ACTION_CANCEL = "com.pablo.flow.action.CANCEL_MEETING"
        const val EXTRA_TITLE = "meeting_title"
        const val EXTRA_MEETING_ID = "meeting_id"
    }

    private val handler = Handler(Looper.getMainLooper())
    private val executor = Executors.newSingleThreadExecutor { Thread(it, "flow-meeting-processing") }
    private var recorder: MeetingSegmentRecorder? = null
    private var meetingId: String? = null
    private var meetingTitle = "Reunión sin título"
    private var startedAtMs = 0L
    private var stopping = false

    private val timer = object : Runnable {
        override fun run() {
            if (recorder?.isRecording() != true) return
            val elapsed = ((System.currentTimeMillis() - startedAtMs) / 1_000).toInt()
            val label = "%d:%02d:%02d".format(elapsed / 3_600, (elapsed % 3_600) / 60, elapsed % 60)
            getSystemService(android.app.NotificationManager::class.java)?.notify(
                FlowNotificationManager.NOTIFICATION_MEETING,
                FlowNotificationManager.buildMeetingNotification(this@MeetingRecordService, meetingTitle, label)
            )
            handler.postDelayed(this, 1_000)
        }
    }

    override fun onCreate() {
        super.onCreate()
        FlowNotificationManager.createChannels(this)
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_START -> startMeeting(intent)
            ACTION_STOP, "STOP_MEETING" -> finishMeeting()
            ACTION_CANCEL, "CANCEL_MEETING" -> cancelMeeting()
        }
        return START_NOT_STICKY
    }

    private fun startMeeting(intent: Intent) {
        if (recorder?.isRecording() == true) return
        meetingId = intent.getStringExtra(EXTRA_MEETING_ID) ?: java.util.UUID.randomUUID().toString()
        meetingTitle = intent.getStringExtra(EXTRA_TITLE)?.trim().takeUnless { it.isNullOrEmpty() } ?: "Reunión sin título"
        startedAtMs = System.currentTimeMillis()
        stopping = false
        FlowMeetingState.isActive = true
        FlowMeetingState.title = meetingTitle
        FlowMeetingState.status = "Grabando reunión…"
        ServiceCompat.startForeground(
            this,
            FlowNotificationManager.NOTIFICATION_MEETING,
            FlowNotificationManager.buildMeetingNotification(this, meetingTitle, "0:00:00"),
            if (Build.VERSION.SDK_INT >= 30) ServiceInfo.FOREGROUND_SERVICE_TYPE_MICROPHONE else 0
        )
        val directory = File(filesDir, "meetings/${meetingId}")
        recorder = MeetingSegmentRecorder(this, directory, onLevel = { level: Float -> FlowMeetingState.audioLevel = level })
        try {
            recorder?.start()
            handler.post(timer)
        } catch (error: Exception) {
            FlowMeetingState.status = error.message ?: "No se pudo iniciar la reunión."
            stopSelf()
        }
    }

    private fun finishMeeting() {
        if (stopping || recorder?.isRecording() != true) return
        stopping = true
        handler.removeCallbacks(timer)
        FlowMeetingState.status = "Procesando la reunión…"
        executor.execute {
            try {
                val segments = recorder?.stop().orEmpty()
                if (segments.isEmpty()) error("No se ha capturado audio de la reunión.")
                processMeeting(segments)
            } catch (error: Exception) {
                FlowMeetingState.status = error.message ?: "No se pudo procesar la reunión."
            } finally {
                FlowMeetingState.isActive = false
                FlowMeetingState.audioLevel = 0f
                stopSelf()
            }
        }
    }

    private fun processMeeting(segments: List<MeetingAudioSegment>) {
        val groq = GroqApi(SecureStore(this))
        val localStore = LocalStore(this)
        val transcriptSegments = mutableListOf<MeetingTranscriptSegmentRecord>()
        val currentMeetingId = meetingId ?: error("Falta el identificador de reunión.")
        val directory = segments.first().file.parentFile ?: File(filesDir, "meetings/$currentMeetingId")
        val mergedAudio = File(directory, "meeting.wav")
        MeetingAudio.mergeWav(segments.sortedBy { it.index }.map { it.file }, mergedAudio)

        segments.sortedBy { it.index }.forEach { segment ->
            FlowMeetingState.status = "Transcribiendo segmento ${segment.index + 1}/${segments.size}…"
            val result = groq.transcribeDetailed(
                segment.file.readBytes(),
                fileName = segment.file.name,
                offsetMs = (segment.startedAtMs - startedAtMs).coerceAtLeast(0L)
            )
            val items = result.segments.ifEmpty {
                result.text.trim().takeIf { it.isNotBlank() }?.let {
                    listOf(MeetingTranscriptSegment("Persona 1", segment.startedAtMs - startedAtMs, segment.endedAtMs - startedAtMs, it))
                }.orEmpty()
            }
            items.forEachIndexed { itemIndex, item ->
                transcriptSegments += MeetingTranscriptSegmentRecord(
                    id = "$currentMeetingId-${segment.index}-$itemIndex",
                    speaker = item.speaker,
                    startMs = item.startMs,
                    endMs = item.endMs,
                    text = item.text
                )
            }
            localStore.saveMeetingSegment(currentMeetingId, segment.index, segment.file.absolutePath, result.text.trim())
        }
        val orderedSegments = transcriptSegments.sortedBy { it.startMs }
        FlowMeetingState.status = "Corrigiendo la transcripción completa…"
        val corrections = groq.correctMeetingSegments(orderedSegments)
        val correctedSegments = orderedSegments.map { segment ->
            corrections?.get(segment.id)?.takeIf { it.isNotBlank() }?.let { segment.copy(text = it) } ?: segment
        }
        val transcript = correctedSegments.joinToString("\n\n") {
            "[${formatTimestamp(it.startMs)}] ${it.speaker}: ${it.text}"
        }.trim()
        if (transcript.isBlank()) error("Groq no devolvió texto para la reunión.")

        FlowMeetingState.status = "Generando resumen…"
        val summary = groq.summarizeMeeting(transcript)
        val started = Instant.ofEpochMilli(startedAtMs).toString()
        val ended = Instant.now().toString()
        val meeting = MeetingRecord(
            id = currentMeetingId,
            title = summary?.title ?: meetingTitle,
            startedAt = started,
            endedAt = ended,
            durationMs = (System.currentTimeMillis() - startedAtMs).coerceAtLeast(0),
            summary = summary?.summary,
            transcript = transcript,
            participants = summary?.participants.orEmpty(),
            agreements = summary?.agreements.orEmpty(),
            tasks = summary?.tasks.orEmpty(),
            segments = correctedSegments,
            audioPath = mergedAudio.absolutePath,
            audioAssetId = currentMeetingId,
            audioFileName = "meeting.wav",
            audioSha256 = MeetingAudio.sha256(mergedAudio)
        )
        localStore.saveMeeting(meeting, synced = false)
        localStore.enqueueMeeting(meeting, FlowPreferences(this).deviceId)
        FlowSyncWorker.schedule(this)
        FlowSyncWorker.runNow(this)
        FlowMeetingState.status = "Reunión guardada; sincronización pendiente o en curso."
        segments.forEach { runCatching { it.file.delete() } }
    }

    private fun formatTimestamp(milliseconds: Long): String {
        val totalSeconds = (milliseconds / 1_000).coerceAtLeast(0L)
        return "%02d:%02d".format(totalSeconds / 60, totalSeconds % 60)
    }

    private fun cancelMeeting() {
        handler.removeCallbacks(timer)
        executor.execute { recorder?.stop() }
        meetingId?.let { File(filesDir, "meetings/$it").deleteRecursively() }
        FlowMeetingState.isActive = false
        FlowMeetingState.audioLevel = 0f
        stopSelf()
    }

    override fun onDestroy() {
        handler.removeCallbacks(timer)
        if (recorder?.isRecording() == true) runCatching { recorder?.stop() }
        executor.shutdownNow()
        FlowMeetingState.isActive = false
        super.onDestroy()
    }

    override fun onBind(intent: Intent?): IBinder? = null
}

object FlowMeetingState {
    @Volatile var isActive = false
    @Volatile var title = ""
    @Volatile var status = ""
    @Volatile var audioLevel = 0f
}
