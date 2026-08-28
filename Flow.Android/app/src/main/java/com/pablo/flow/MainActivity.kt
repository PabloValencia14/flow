package com.pablo.flow

import android.Manifest
import android.app.StatusBarManager
import android.content.ComponentName
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.drawable.Icon
import android.os.Bundle
import android.os.Build
import android.provider.Settings
import android.net.Uri
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.foundation.layout.padding
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalClipboardManager
import androidx.compose.ui.text.AnnotatedString
import androidx.core.content.ContextCompat
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import com.pablo.flow.ui.DictationScreen
import com.pablo.flow.ui.FlowScaffold
import com.pablo.flow.ui.FlowTab
import com.pablo.flow.ui.FlowTheme
import com.pablo.flow.ui.HistoryScreen
import com.pablo.flow.ui.MeetingDetailScreen
import com.pablo.flow.ui.MeetingsScreen
import com.pablo.flow.ui.SettingsScreen
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        FlowNotificationManager.createChannels(this)
        FlowSyncWorker.schedule(this)
        val provisioningStatus = listOfNotNull(
            FlowProvisioning.importPendingGroqKey(this),
            FlowProvisioning.importPendingFlowHub(this)
        ).joinToString(" ").ifBlank { null }
        setContent {
            var tab by rememberSaveable { mutableStateOf(FlowTab.DICTATION) }
            var status by remember { mutableStateOf(provisioningStatus ?: "Listo para dictar.") }
            var flowState by remember { mutableStateOf(FlowOverlayState.state) }
            var audioLevel by remember { mutableFloatStateOf(0f) }
            var lastResult by remember { mutableStateOf(FlowOverlayState.lastResult) }
            var records by remember { mutableStateOf(emptyList<DictationRecord>()) }
            var meetings by remember { mutableStateOf(emptyList<MeetingRecord>()) }
            var selectedMeeting by remember { mutableStateOf<MeetingRecord?>(null) }
            var meetingActive by remember { mutableStateOf(FlowMeetingState.isActive) }
            var refreshVersion by remember { mutableIntStateOf(0) }
            val scope = rememberCoroutineScope()
            val clipboard = LocalClipboardManager.current
            var accessibilityEnabled by remember { mutableStateOf(FlowTextAccessibilityService.isEnabled()) }

            DisposableEffect(Unit) {
                val observer = LifecycleEventObserver { _, event ->
                    if (event == Lifecycle.Event.ON_RESUME) {
                        accessibilityEnabled = FlowTextAccessibilityService.isEnabled()
                    }
                }
                lifecycle.addObserver(observer)
                onDispose { lifecycle.removeObserver(observer) }
            }

            val engine = remember {
                FlowEngine(this@MainActivity, object : FlowEngineListener {
                    override fun onStateChanged(state: FlowState) = runOnUiThread { flowState = state }
                    override fun onAudioLevel(level: Float) = runOnUiThread { audioLevel = level }
                    override fun onStatus(message: String) = runOnUiThread { status = message; meetingActive = FlowMeetingState.isActive }
                    override fun onResult(result: DictationResult) = runOnUiThread { lastResult = result; refreshVersion++ }
                    override fun onError(message: String, cause: Throwable?) = runOnUiThread { status = message; meetingActive = FlowMeetingState.isActive }
                })
            }
            DisposableEffect(engine) { onDispose { engine.close() } }

            fun refreshLocal() {
                scope.launch {
                    val snapshot = withContext(Dispatchers.IO) { engine.getLocalStore().recentDictations(50) to engine.getLocalStore().meetings(50) }
                    records = snapshot.first
                    meetings = snapshot.second
                    meetingActive = FlowMeetingState.isActive
                }
            }
            LaunchedEffect(refreshVersion) { refreshLocal() }

            LaunchedEffect(Unit) {
                engine.syncPending()
                var previousState = FlowOverlayState.state
                while (true) {
                    val sharedState = FlowOverlayState.state
                    if (previousState != FlowState.IDLE && sharedState == FlowState.IDLE) refreshVersion++
                    flowState = sharedState
                    audioLevel = FlowOverlayState.audioLevel
                    FlowOverlayState.statusMessage.takeIf { it.isNotBlank() }?.let { status = it }
                    FlowOverlayState.lastResult?.let { result ->
                        if (result != lastResult) lastResult = result
                    }
                    previousState = sharedState
                    delay(150)
                }
            }

            val permission = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
                if (granted) startDictationService()
                else status = "Android no ha concedido permiso para el micrófono."
            }
            val notificationPermission = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { }
            LaunchedEffect(Unit) {
                if (Build.VERSION.SDK_INT >= 33 && ContextCompat.checkSelfPermission(this@MainActivity, Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
                    notificationPermission.launch(Manifest.permission.POST_NOTIFICATIONS)
                }
            }

            FlowTheme(darkTheme = when (engine.preferences.darkMode) { "light" -> false; "dark" -> true; else -> androidx.compose.foundation.isSystemInDarkTheme() }) {
            val importAudio = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri ->
                if (uri == null) return@rememberLauncherForActivityResult
                scope.launch(Dispatchers.IO) {
                    runCatching {
                        MeetingImportProcessor(this@MainActivity) { message -> runOnUiThread { status = message } }
                            .process(uri, "Reunión importada")
                    }.onSuccess { meeting ->
                        withContext(Dispatchers.Main) {
                            status = "Audio importado y procesado."
                            selectedMeeting = meeting
                            refreshVersion++
                        }
                    }.onFailure { error -> withContext(Dispatchers.Main) { status = error.message ?: "No se pudo procesar el audio." } }
                }
            }

            selectedMeeting?.let { meeting ->
                    MeetingDetailScreen(
                        meeting,
                        onBack = { selectedMeeting = null },
                        onExportMarkdown = {
                            val ok = MeetingExport.writeToDownloads(this@MainActivity, meeting, markdown = true)
                            Toast.makeText(this@MainActivity, if (ok) "Markdown exportado en Descargas/Flow" else "No se pudo exportar el Markdown", Toast.LENGTH_LONG).show()
                        },
                        onExportText = {
                            val ok = MeetingExport.writeToDownloads(this@MainActivity, meeting, markdown = false)
                            Toast.makeText(this@MainActivity, if (ok) "Texto exportado en Descargas/Flow" else "No se pudo exportar el texto", Toast.LENGTH_LONG).show()
                        }
                    )
                } ?: FlowScaffold(tab, { tab = it }) { padding ->
                    androidx.compose.foundation.layout.Box(Modifier.padding(padding)) {
                        when (tab) {
                            FlowTab.DICTATION -> DictationScreen(
                                state = flowState, status = status, level = audioLevel, recent = records.take(10), lastResult = lastResult,
                                onStart = {
                                    if (ContextCompat.checkSelfPermission(this@MainActivity, Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) permission.launch(Manifest.permission.RECORD_AUDIO)
                                    else startDictationService()
                                },
                                onFinish = { stopDictationService() }, onRefresh = ::refreshLocal,
                                onCopy = { clipboard.setText(AnnotatedString(it)); Toast.makeText(this@MainActivity, "Texto copiado", Toast.LENGTH_SHORT).show() }
                            )
                            FlowTab.MEETINGS -> MeetingsScreen(
                                meetings = meetings, meetingActive = meetingActive,
                                onStart = { title -> startMeeting(title); meetingActive = true },
                                onStop = { stopMeeting(); meetingActive = false },
                                onSelect = { selectedMeeting = it }, onRefresh = ::refreshLocal,
                                onImport = { importAudio.launch(arrayOf("audio/*", "video/*")) }
                            )
                            FlowTab.HISTORY -> HistoryScreen(records, onCopy = { clipboard.setText(AnnotatedString(it)); Toast.makeText(this@MainActivity, "Texto copiado", Toast.LENGTH_SHORT).show() }, onRefresh = ::refreshLocal)
                            FlowTab.SETTINGS -> SettingsScreen(
                                serverUrl = engine.preferences.serverUrl ?: "", hasGroqKey = engine.hasGroqApiKey(), hasToken = engine.hasFlowHubToken(), status = status,
                                darkMode = engine.preferences.darkMode,
                                onSave = { url, key, token, theme ->
                                    engine.setServerUrl(url.ifBlank { null })
                                    if (key.isNotBlank()) engine.setGroqApiKey(key)
                                    if (token.isNotBlank()) engine.setFlowHubToken(token)
                                    engine.preferences.darkMode = theme
                                    status = "Configuración guardada de forma segura."
                                },
                                onRemoveKey = { engine.removeGroqApiKey(); status = "Clave de Groq eliminada." },
                                onTest = { url ->
                                    scope.launch(Dispatchers.IO) {
                                        val ok = engine.checkFlowHub(url)
                                        withContext(Dispatchers.Main) { status = if (ok) "FlowHub disponible." else "FlowHub no responde." }
                                    }
                                },
                                onScheduleSync = { FlowSyncWorker.schedule(this@MainActivity); status = "Sincronización periódica activada." },
                                onRequestOverlay = {
                                    requestOverlayPermission()
                                },
                                onRequestTile = ::requestQuickSettingsTile,
                                onRequestMeetingTile = ::requestMeetingQuickSettingsTile,
                                textInsertionEnabled = accessibilityEnabled,
                                onRequestTextInsertion = {
                                    startActivity(Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS))
                                }
                            )
                        }
                    }
                }
            }
        }
    }

    private fun startMeeting(title: String) {
        if (ContextCompat.checkSelfPermission(this, Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
            Toast.makeText(this, "Concede permiso de micrófono antes de iniciar una reunión.", Toast.LENGTH_LONG).show()
            return
        }
        ContextCompat.startForegroundService(this, Intent(this, MeetingRecordService::class.java).apply {
            action = MeetingRecordService.ACTION_START
            putExtra(MeetingRecordService.EXTRA_TITLE, title)
        })
    }

    private fun stopMeeting() {
        startService(Intent(this, MeetingRecordService::class.java).apply { action = MeetingRecordService.ACTION_STOP })
    }

    private fun startDictationService() {
        if (!Settings.canDrawOverlays(this)) {
            Toast.makeText(this, "Concede permiso para mostrar la burbuja flotante de Flow.", Toast.LENGTH_LONG).show()
            requestOverlayPermission()
            return
        }
        runCatching {
            ContextCompat.startForegroundService(this, Intent(this, FlowOverlayService::class.java).apply {
                action = FlowOverlayService.ACTION_START
            })
        }.onFailure { Toast.makeText(this, it.message ?: "No se pudo iniciar el dictado.", Toast.LENGTH_LONG).show() }
    }

    private fun stopDictationService() {
        startService(Intent(this, FlowOverlayService::class.java).apply { action = FlowOverlayService.ACTION_STOP })
    }

    private fun requestOverlayPermission() {
        startActivity(Intent(Settings.ACTION_MANAGE_OVERLAY_PERMISSION, Uri.parse("package:$packageName")))
    }

    private fun requestQuickSettingsTile() {
        requestTile(
            ComponentName(this, FlowQuickTileService::class.java),
            "Flow · Grabar"
        )
    }

    private fun requestMeetingQuickSettingsTile() {
        requestTile(
            ComponentName(this, FlowMeetingQuickTileService::class.java),
            "Flow · Reunión"
        )
    }

    private fun requestTile(component: ComponentName, label: String) {
        if (Build.VERSION.SDK_INT < 33) {
            Toast.makeText(this, "Abre Ajustes rápidos, pulsa editar y añade el tile de Flow.", Toast.LENGTH_LONG).show()
            return
        }
        val manager = getSystemService(StatusBarManager::class.java)
        if (manager == null) {
            Toast.makeText(this, "Este sistema no permite solicitar el tile automáticamente.", Toast.LENGTH_LONG).show()
            return
        }
        manager.requestAddTileService(
            component,
            label,
            Icon.createWithResource(this, R.drawable.flow_logo),
            mainExecutor
        ) { result ->
            val message = when (result) {
                StatusBarManager.TILE_ADD_REQUEST_RESULT_TILE_ADDED -> "Botón de Flow añadido a Ajustes rápidos."
                StatusBarManager.TILE_ADD_REQUEST_RESULT_TILE_ALREADY_ADDED -> "El botón de Flow ya está en Ajustes rápidos."
                else -> "Puedes añadir Flow desde Ajustes rápidos → Editar."
            }
            Toast.makeText(this, message, Toast.LENGTH_LONG).show()
        }
    }
}
