package com.pablo.flow

import android.Manifest
import android.content.Context
import android.content.pm.PackageManager
import android.os.Build
import androidx.core.content.ContextCompat
import java.util.UUID
import java.util.concurrent.ExecutorService
import java.util.concurrent.Executors

enum class FlowState { IDLE, RECORDING, TRANSCRIBING, CORRECTING, SYNCING }

data class DictationResult(
    val id: String,
    val rawTranscript: String,
    val text: String,
    val corrected: Boolean,
    val durationMs: Long,
    val transcriptionModel: String,
    val correctionModel: String?
)

interface FlowEngineListener {
    fun onStateChanged(state: FlowState) = Unit
    fun onAudioLevel(level: Float) = Unit
    fun onStatus(message: String) = Unit
    fun onResult(result: DictationResult) = Unit
    fun onError(message: String, cause: Throwable? = null) = Unit
}

class FlowEngine(
    context: Context,
    private val listener: FlowEngineListener = object : FlowEngineListener {}
) : AutoCloseable {
    private val appContext = context.applicationContext
    val preferences = FlowPreferences(appContext)
    private val secureStore = SecureStore(appContext)
    private val localStore = LocalStore(appContext)
    private val groq = GroqApi(secureStore)
    private val sync = FlowSyncClient(secureStore)
    private val executor: ExecutorService = Executors.newSingleThreadExecutor { runnable ->
        Thread(runnable, "flow-engine").apply { isDaemon = true }
    }
    private val recorder = AudioRecorder(appContext) { listener.onAudioLevel(it) }
    @Volatile
    var state: FlowState = FlowState.IDLE
        private set
    private var recordingStartedAt = 0L
    private var targetAppName: String? = null

    fun hasMicrophonePermission(): Boolean = ContextCompat.checkSelfPermission(
        appContext, Manifest.permission.RECORD_AUDIO
    ) == PackageManager.PERMISSION_GRANTED

    fun getGroqApi(): GroqApi = groq

    fun hasGroqApiKey(): Boolean = !secureStore.get("groq_api_key").isNullOrBlank()

    fun hasFlowHubToken(): Boolean = !secureStore.get("flowhub_app_token").isNullOrBlank()

    fun setGroqApiKey(value: String) {
        require(value.trim().isNotEmpty()) { "La clave de Groq no puede estar vacía." }
        secureStore.put("groq_api_key", value.trim())
    }

    fun removeGroqApiKey() = secureStore.remove("groq_api_key")

    fun setFlowHubToken(value: String?) {
        if (value.isNullOrBlank()) secureStore.remove("flowhub_app_token")
        else secureStore.put("flowhub_app_token", value.trim())
    }

    fun setServerUrl(value: String?) {
        preferences.serverUrl = value
    }

    fun checkFlowHub(serverUrl: String): Boolean = FlowSyncClient(secureStore).checkConnection(serverUrl.trim().ifEmpty { null })

    @Synchronized
    fun start() {
        check(state == FlowState.IDLE) { "Flow no está listo para iniciar otra captura." }
        check(hasMicrophonePermission()) { "Android aún no ha concedido permiso para el micrófono." }
        targetAppName = FlowTextAccessibilityService.currentTargetAppName()
        recorder.start()
        recordingStartedAt = System.currentTimeMillis()
        state = FlowState.RECORDING
        listener.onStateChanged(state)
        listener.onStatus("Grabando…")
    }

    fun finish() {
        synchronized(this) {
            if (state != FlowState.RECORDING) return
            state = FlowState.TRANSCRIBING
            listener.onStateChanged(state)
        }
        executor.execute {
            try {
                val wav = recorder.stop()
                if (wav.size <= 44) throw IllegalStateException("No se ha capturado audio suficiente.")
                listener.onStatus("Transcribiendo…")
                val raw = groq.transcribe(wav)
                if (raw.isBlank()) throw IllegalStateException("Groq no devolvió texto.")

                state = FlowState.CORRECTING
                listener.onStateChanged(state)
                listener.onStatus("Corrigiendo…")
                val correctionOptions = localStore.correctionOptions()
                val correctionInput = DictationTextProcessor.prepareForCorrection(raw, correctionOptions)
                val dictionary = localStore.dictionaryEntries().map { it.word to it.replacement }
                val style = DictationTextProcessor.styleInstruction(
                    localStore.syncableSetting(ForegroundTargetDetector.styleKey(targetAppName))
                )
                val corrected = runCatching {
                    groq.correct(
                        correctionInput,
                        DictationCorrectionContext(
                            targetAppName = targetAppName,
                            personalDictionary = dictionary,
                            options = correctionOptions,
                            styleInstruction = style
                        )
                    )
                }.getOrNull()
                val finalText = DictationTextProcessor.expandSnippets(
                    DictationTextProcessor.cleanFinal(corrected ?: raw, correctionOptions),
                    localStore.snippets()
                )
                val result = DictationResult(
                    id = UUID.randomUUID().toString(),
                    rawTranscript = raw,
                    text = finalText,
                    corrected = corrected != null && corrected != raw,
                    durationMs = (System.currentTimeMillis() - recordingStartedAt).coerceAtLeast(0),
                    transcriptionModel = GroqApi.TRANSCRIPTION_MODEL,
                    correctionModel = corrected?.let { GroqApi.CORRECTION_MODEL }
                )
                localStore.saveDictation(result, preferences.deviceId)
                localStore.enqueue("dictations", result.id, "create", result.toJson(preferences.deviceId).toString())
                listener.onResult(result)

                state = FlowState.SYNCING
                listener.onStateChanged(state)
                listener.onStatus("Sincronizando…")
                val serverUrl = preferences.serverUrl
                if (sync.registerDevice(
                        serverUrl,
                        preferences.deviceId,
                        Build.MODEL.ifBlank { "Android" },
                        "android",
                        Build.VERSION.RELEASE ?: "unknown"
                    )
                ) {
                    val pending = localStore.pending()
                    when (val syncResult = sync.push(serverUrl, preferences.deviceId, pending)) {
                        SyncResult.NotNeeded -> Unit
                        is SyncResult.Pushed -> {
                            localStore.remove(syncResult.acknowledgedEventIds)
                            listener.onStatus("Sincronizado con FlowHub ✓")
                        }
                        is SyncResult.Failed -> listener.onStatus("Guardado localmente (sync pendiente)")
                    }
                } else {
                    listener.onStatus("Guardado localmente (FlowHub no disponible)")
                }
                sync.pullAndApply(serverUrl, localStore)
                state = FlowState.IDLE
                listener.onStateChanged(state)
            } catch (error: Exception) {
                state = FlowState.IDLE
                listener.onStateChanged(state)
                listener.onError(error.message ?: "No se pudo completar el dictado.", error)
            } finally {
                listener.onAudioLevel(0f)
            }
        }
    }

    @Synchronized
    fun cancel() {
        if (state != FlowState.RECORDING) return
        recorder.stop()
        state = FlowState.IDLE
        listener.onStateChanged(state)
        listener.onStatus("Captura cancelada.")
        listener.onAudioLevel(0f)
    }

    fun syncPending() {
        executor.execute {
            val serverUrl = preferences.serverUrl
            if (!sync.registerDevice(
                    serverUrl,
                    preferences.deviceId,
                    Build.MODEL.ifBlank { "Android" },
                    "android",
                    Build.VERSION.RELEASE ?: "unknown"
                )
            ) return@execute
            val pending = localStore.pending()
            val result = sync.push(serverUrl, preferences.deviceId, pending)
            if (result is SyncResult.Pushed) localStore.remove(result.acknowledgedEventIds)
            sync.pullAndApply(serverUrl, localStore)
        }
    }

    fun getLocalStore(): LocalStore = localStore

    override fun close() {
        if (state == FlowState.RECORDING) runCatching { recorder.stop() }
        executor.shutdownNow()
        localStore.close()
    }
}

private fun DictationResult.toJson(deviceId: String) = org.json.JSONObject().apply {
    put("dictationId", id)
    put("text", text)
    put("rawTranscript", rawTranscript)
    put("language", "es")
    put("durationMs", durationMs)
    put("corrected", corrected)
    put("transcriptionModel", transcriptionModel)
    correctionModel?.let { put("correctionModel", it) }
    put("deviceId", deviceId)
    put("createdAt", java.time.Instant.now().toString())
}
