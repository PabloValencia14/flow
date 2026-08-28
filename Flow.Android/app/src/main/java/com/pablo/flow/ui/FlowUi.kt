package com.pablo.flow.ui

import android.media.MediaPlayer
import androidx.compose.animation.animateColorAsState
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBars
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.outlined.ArrowBack
import androidx.compose.material.icons.outlined.AutoAwesome
import androidx.compose.material.icons.outlined.CheckCircle
import androidx.compose.material.icons.outlined.ContentCopy
import androidx.compose.material.icons.outlined.DeleteOutline
import androidx.compose.material.icons.outlined.Download
import androidx.compose.material.icons.outlined.History
import androidx.compose.material.icons.outlined.MeetingRoom
import androidx.compose.material.icons.outlined.Mic
import androidx.compose.material.icons.outlined.PlayArrow
import androidx.compose.material.icons.outlined.Pause
import androidx.compose.material.icons.outlined.Refresh
import androidx.compose.material.icons.outlined.Settings
import androidx.compose.material.icons.outlined.StopCircle
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.FloatingActionButton
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.scale
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.withContext
import com.pablo.flow.DictationRecord
import com.pablo.flow.DictationResult
import com.pablo.flow.FlowState
import com.pablo.flow.MeetingRecord
import java.text.DateFormat
import java.util.Date

enum class FlowTab(val label: String) { DICTATION("Dictado"), MEETINGS("Reuniones"), HISTORY("Historial"), SETTINGS("Ajustes") }

@Composable
fun FlowScaffold(tab: FlowTab, onTabSelected: (FlowTab) -> Unit, content: @Composable (PaddingValues) -> Unit) {
    Scaffold(
        contentWindowInsets = WindowInsets.navigationBars,
        bottomBar = {
            NavigationBar {
                FlowTab.values().forEach { item ->
                    NavigationBarItem(
                        selected = tab == item, onClick = { onTabSelected(item) },
                        icon = { Icon(when (item) {
                            FlowTab.DICTATION -> Icons.Outlined.Mic
                            FlowTab.MEETINGS -> Icons.Outlined.MeetingRoom
                            FlowTab.HISTORY -> Icons.Outlined.History
                            FlowTab.SETTINGS -> Icons.Outlined.Settings
                        }, contentDescription = null) }, label = { Text(item.label) }
                    )
                }
            }
        }, content = content
    )
}

@Composable
fun DictationScreen(
    state: FlowState, status: String, level: Float, recent: List<DictationRecord>, lastResult: DictationResult?,
    onStart: () -> Unit, onFinish: () -> Unit, onRefresh: () -> Unit, onCopy: (String) -> Unit
) {
    val recording = state == FlowState.RECORDING
    val processing = state != FlowState.IDLE && !recording
    LazyColumn(
        modifier = Modifier.fillMaxSize().windowInsetsPadding(WindowInsets.navigationBars),
        contentPadding = PaddingValues(24.dp), verticalArrangement = Arrangement.spacedBy(18.dp)
    ) {
        item { Text("Flow", style = MaterialTheme.typography.headlineLarge, fontWeight = FontWeight.SemiBold); Spacer(Modifier.height(4.dp)); Text("Dictado local-first", color = MaterialTheme.colorScheme.onSurfaceVariant) }
        item { StatusCard(state, status) }
        item {
            Column(horizontalAlignment = Alignment.CenterHorizontally, modifier = Modifier.fillMaxWidth()) {
                AudioLevelBars(level, recording); Spacer(Modifier.height(20.dp))
                DictationButton(recording, !processing, if (recording) onFinish else onStart)
                Spacer(Modifier.height(12.dp))
                Text(if (recording) "Pulsa para terminar" else if (processing) "Procesando…" else "Pulsa para hablar", color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
        }
        lastResult?.let { item { ResultCard(it, onCopy) } }
        item {
            Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.fillMaxWidth()) {
                Text("Últimos dictados", style = MaterialTheme.typography.titleLarge, modifier = Modifier.weight(1f))
                IconButton(onClick = onRefresh, modifier = Modifier.size(48.dp)) { Icon(Icons.Outlined.Refresh, "Actualizar dictados") }
            }
        }
        if (recent.isEmpty()) item { EmptyState("Todavía no hay dictados guardados.") }
        else items(recent, key = { it.id }) { DictationRow(it, onCopy) }
    }
}

@Composable
private fun StatusCard(state: FlowState, status: String) {
    val color by animateColorAsState(if (state == FlowState.RECORDING) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.secondary, label = "status")
    Card(colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceVariant)) {
        Row(Modifier.fillMaxWidth().padding(16.dp), verticalAlignment = Alignment.CenterVertically) {
            Box(Modifier.size(10.dp).background(color, CircleShape)); Spacer(Modifier.width(12.dp))
            Column(Modifier.weight(1f)) {
                Text(when (state) {
                    FlowState.IDLE -> "Listo para dictar"
                    FlowState.RECORDING -> "Escuchando"
                    FlowState.TRANSCRIBING -> "Transcribiendo"
                    FlowState.CORRECTING -> "Corrigiendo contexto"
                    FlowState.SYNCING -> "Sincronizando"
                }, fontWeight = FontWeight.Medium)
                Text(status.ifBlank { "La captura queda guardada localmente." }, color = MaterialTheme.colorScheme.onSurfaceVariant, fontSize = 13.sp)
            }
            if (state != FlowState.IDLE) LinearProgressIndicator(Modifier.width(52.dp))
        }
    }
}

@Composable
private fun DictationButton(recording: Boolean, enabled: Boolean, onClick: () -> Unit) {
    val scale by animateFloatAsState(if (recording) 1.06f else 1f, label = "recording-button")
    val container = if (recording) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.primary
    val content = if (recording) MaterialTheme.colorScheme.onError else MaterialTheme.colorScheme.onPrimary
    Surface(onClick = onClick, enabled = enabled, modifier = Modifier.size(136.dp).scale(scale).semantics { contentDescription = if (recording) "Terminar dictado" else "Empezar dictado" }, shape = CircleShape, color = container, contentColor = content, tonalElevation = 2.dp) {
        Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) { Icon(if (recording) Icons.Outlined.StopCircle else Icons.Outlined.Mic, null, Modifier.size(48.dp)) }
    }
}

@Composable
private fun AudioLevelBars(level: Float, active: Boolean) {
    val factors = listOf(.38f, .72f, 1f, .58f, .84f, .5f, .68f)
    Row(horizontalArrangement = Arrangement.spacedBy(5.dp), verticalAlignment = Alignment.CenterVertically, modifier = Modifier.height(42.dp)) {
        factors.forEachIndexed { index, factor ->
            val target = if (active) (0.18f + level.coerceIn(0f, 1f) * factor).coerceIn(.18f, 1f) else .18f
            val animated by animateFloatAsState(target, label = "audio-bar-$index")
            Box(Modifier.width(5.dp).height((42 * animated).dp).background(if (active) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.outline, RoundedCornerShape(4.dp)))
        }
    }
}

@Composable
private fun ResultCard(result: DictationResult, onCopy: (String) -> Unit) {
    Card(colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.primaryContainer)) {
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Icon(Icons.Outlined.AutoAwesome, null, tint = MaterialTheme.colorScheme.primary); Spacer(Modifier.width(8.dp))
                Text(if (result.corrected) "Texto corregido" else "Texto transcrito", fontWeight = FontWeight.Medium, modifier = Modifier.weight(1f))
                IconButton(onClick = { onCopy(result.text) }, modifier = Modifier.size(48.dp)) { Icon(Icons.Outlined.ContentCopy, "Copiar texto") }
            }
            Text(result.text, style = MaterialTheme.typography.bodyLarge)
        }
    }
}

@Composable
private fun DictationRow(record: DictationRecord, onCopy: (String) -> Unit) {
    Card {
        Row(Modifier.fillMaxWidth().padding(16.dp), verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(4.dp)) { Text(record.text, maxLines = 3, style = MaterialTheme.typography.bodyLarge); Text(formatDate(record.createdAt), color = MaterialTheme.colorScheme.onSurfaceVariant, fontSize = 12.sp) }
            IconButton(onClick = { onCopy(record.text) }, modifier = Modifier.size(48.dp)) { Icon(Icons.Outlined.ContentCopy, "Copiar dictado") }
        }
    }
}

@Composable
fun MeetingsScreen(meetings: List<MeetingRecord>, meetingActive: Boolean, onStart: (String) -> Unit, onStop: () -> Unit, onSelect: (MeetingRecord) -> Unit, onRefresh: () -> Unit, onImport: () -> Unit) {
    var showDialog by remember { mutableStateOf(false) }
    var title by remember { mutableStateOf("") }
    LazyColumn(Modifier.fillMaxSize().windowInsetsPadding(WindowInsets.navigationBars), contentPadding = PaddingValues(24.dp), verticalArrangement = Arrangement.spacedBy(14.dp)) {
        item {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Column(Modifier.weight(1f)) { Text("Reuniones", style = MaterialTheme.typography.headlineLarge, fontWeight = FontWeight.SemiBold); Text("Graba clases y conversaciones largas", color = MaterialTheme.colorScheme.onSurfaceVariant) }
                IconButton(onClick = onRefresh, modifier = Modifier.size(48.dp)) { Icon(Icons.Outlined.Refresh, "Actualizar reuniones") }
            }
        }
        item {
            if (meetingActive) Button(onClick = onStop, modifier = Modifier.fillMaxWidth()) { Icon(Icons.Outlined.StopCircle, null); Spacer(Modifier.width(8.dp)); Text("Finalizar reunión") }
            else Row(horizontalArrangement = Arrangement.spacedBy(10.dp), modifier = Modifier.fillMaxWidth()) {
                Button(onClick = { showDialog = true }, modifier = Modifier.weight(1f).height(56.dp)) { Icon(Icons.Outlined.PlayArrow, null); Spacer(Modifier.width(8.dp)); Text("Nueva reunión") }
                OutlinedButton(onClick = onImport, modifier = Modifier.weight(1f).height(56.dp)) { Icon(Icons.Outlined.Download, null); Spacer(Modifier.width(8.dp)); Text("Importar audio") }
            }
        }
        if (meetings.isEmpty()) item { EmptyState("No hay reuniones guardadas todavía.") }
        else items(meetings, key = { it.id }) { MeetingRow(it, onSelect) }
    }
    if (showDialog) AlertDialog(onDismissRequest = { showDialog = false }, title = { Text("Nueva reunión") }, text = { OutlinedTextField(title, { title = it }, label = { Text("Título") }, singleLine = true, modifier = Modifier.fillMaxWidth()) }, confirmButton = { TextButton(onClick = { showDialog = false; onStart(title.ifBlank { "Reunión sin título" }); title = "" }) { Text("Empezar") } }, dismissButton = { TextButton(onClick = { showDialog = false }) { Text("Cancelar") } })
}

@Composable
private fun MeetingRow(meeting: MeetingRecord, onSelect: (MeetingRecord) -> Unit) {
    Card(onClick = { onSelect(meeting) }) { Column(Modifier.fillMaxWidth().padding(16.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) { Text(meeting.title, style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.Medium); Text(formatDate(meeting.startedAt), color = MaterialTheme.colorScheme.onSurfaceVariant, fontSize = 12.sp); Text(meeting.summary ?: "Sin resumen disponible", maxLines = 2, color = MaterialTheme.colorScheme.onSurfaceVariant) } }
}

@Composable
@OptIn(ExperimentalMaterial3Api::class)
fun MeetingDetailScreen(meeting: MeetingRecord, onBack: () -> Unit, onExportMarkdown: () -> Unit, onExportText: () -> Unit) {
    var playing by remember(meeting.id) { mutableStateOf(false) }
    var position by remember(meeting.id) { mutableIntStateOf(0) }
    var duration by remember(meeting.id) { mutableIntStateOf(meeting.durationMs.coerceAtMost(Int.MAX_VALUE.toLong()).toInt()) }
    val player = remember(meeting.id) { MediaPlayer() }
    DisposableEffect(player) {
        onDispose { runCatching { player.stop() }; player.release() }
    }
    LaunchedEffect(meeting.audioPath) {
        val path = meeting.audioPath ?: return@LaunchedEffect
        runCatching {
            withContext(Dispatchers.IO) { player.setDataSource(path); player.prepare() }
            duration = player.duration.coerceAtLeast(duration)
            player.setOnCompletionListener { playing = false; position = duration }
        }
    }
    LaunchedEffect(playing) {
        while (playing) {
            position = player.currentPosition
            delay(200)
        }
    }
    Scaffold(topBar = { TopAppBar(title = { Text(meeting.title) }, navigationIcon = { IconButton(onClick = onBack) { Icon(Icons.AutoMirrored.Outlined.ArrowBack, "Volver") } }) }) { padding ->
        LazyColumn(Modifier.fillMaxSize().padding(padding), contentPadding = PaddingValues(24.dp), verticalArrangement = Arrangement.spacedBy(16.dp)) {
            item { Text(formatDate(meeting.startedAt), color = MaterialTheme.colorScheme.onSurfaceVariant) }
            item {
                Card(colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceVariant)) {
                    Column(Modifier.fillMaxWidth().padding(16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            Button(onClick = {
                                if (playing) { player.pause(); playing = false }
                                else if (meeting.audioPath != null) { player.start(); playing = true }
                            }, enabled = meeting.audioPath != null, modifier = Modifier.height(48.dp)) {
                                Icon(if (playing) Icons.Outlined.Pause else Icons.Outlined.PlayArrow, null)
                                Spacer(Modifier.width(8.dp)); Text(if (playing) "Pausar" else "Reproducir")
                            }
                            Spacer(Modifier.width(12.dp))
                            Text("${timestamp(position.toLong())} / ${timestamp(duration.toLong())}", color = MaterialTheme.colorScheme.onSurfaceVariant)
                        }
                        androidx.compose.material3.Slider(
                            value = position.toFloat().coerceIn(0f, duration.coerceAtLeast(1).toFloat()),
                            onValueChange = { value -> position = value.toInt(); if (meeting.audioPath != null) player.seekTo(position) },
                            valueRange = 0f..duration.coerceAtLeast(1).toFloat(),
                            enabled = meeting.audioPath != null,
                            modifier = Modifier.fillMaxWidth()
                        )
                        Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                            OutlinedButton(onClick = onExportMarkdown, modifier = Modifier.height(48.dp)) { Text("Exportar Markdown") }
                            OutlinedButton(onClick = onExportText, modifier = Modifier.height(48.dp)) { Text("Exportar .txt") }
                        }
                    }
                }
            }
            item { DetailBlock("Resumen", meeting.summary ?: "Sin resumen disponible.") }
            if (meeting.agreements.isNotEmpty()) item { ListBlock("Acuerdos", meeting.agreements) }
            if (meeting.tasks.isNotEmpty()) item { ListBlock("Tareas", meeting.tasks) }
            item {
                Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    Text("Transcripción", style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold)
                    if (meeting.segments.isEmpty()) {
                        Card(colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceVariant)) { Text(meeting.transcript ?: "Sin transcripción disponible.", Modifier.padding(16.dp)) }
                    } else meeting.segments.sortedBy { it.startMs }.forEach { segment ->
                        TextButton(onClick = { if (meeting.audioPath != null) { player.seekTo(segment.startMs.toInt()); position = segment.startMs.toInt() } }, modifier = Modifier.fillMaxWidth()) {
                            Column(Modifier.fillMaxWidth(), horizontalAlignment = Alignment.Start) {
                                Text("${timestamp(segment.startMs)}  ·  ${segment.speaker}", color = MaterialTheme.colorScheme.primary, fontWeight = FontWeight.Medium)
                                Text(segment.text, color = MaterialTheme.colorScheme.onSurface)
                            }
                        }
                    }
                }
            }
        }
    }
}

@Composable private fun DetailBlock(title: String, value: String) { Column(verticalArrangement = Arrangement.spacedBy(8.dp)) { Text(title, style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold); Card(colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceVariant)) { Text(value, Modifier.padding(16.dp)) } } }
@Composable private fun ListBlock(title: String, values: List<String>) { Column(verticalArrangement = Arrangement.spacedBy(8.dp)) { Text(title, style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold); values.forEach { value -> Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.Top) { Icon(Icons.Outlined.CheckCircle, null, Modifier.size(20.dp), tint = MaterialTheme.colorScheme.primary); Spacer(Modifier.width(8.dp)); Text(value) } } } }

@Composable
fun HistoryScreen(records: List<DictationRecord>, onCopy: (String) -> Unit, onRefresh: () -> Unit) {
    LazyColumn(Modifier.fillMaxSize().windowInsetsPadding(WindowInsets.navigationBars), contentPadding = PaddingValues(24.dp), verticalArrangement = Arrangement.spacedBy(14.dp)) {
        item { Row(verticalAlignment = Alignment.CenterVertically) { Text("Historial", style = MaterialTheme.typography.headlineLarge, fontWeight = FontWeight.SemiBold, modifier = Modifier.weight(1f)); IconButton(onClick = onRefresh, modifier = Modifier.size(48.dp)) { Icon(Icons.Outlined.Refresh, "Actualizar historial") } } }
        if (records.isEmpty()) item { EmptyState("Tus dictados aparecerán aquí.") } else items(records, key = { it.id }) { DictationRow(it, onCopy) }
    }
}

@Composable
fun SettingsScreen(serverUrl: String, hasGroqKey: Boolean, hasToken: Boolean, darkMode: String, onSave: (String, String, String, String) -> Unit, onRemoveKey: () -> Unit, onTest: (String) -> Unit, onScheduleSync: () -> Unit, onRequestOverlay: () -> Unit, onRequestTile: () -> Unit, textInsertionEnabled: Boolean, onRequestTextInsertion: () -> Unit, status: String) {
    var url by remember(serverUrl) { mutableStateOf(serverUrl) }
    var groqKey by remember { mutableStateOf("") }
    var token by remember { mutableStateOf("") }
    var theme by remember(darkMode) { mutableStateOf(darkMode) }
    LazyColumn(Modifier.fillMaxSize().windowInsetsPadding(WindowInsets.navigationBars), contentPadding = PaddingValues(24.dp), verticalArrangement = Arrangement.spacedBy(16.dp)) {
        item { Text("Ajustes", style = MaterialTheme.typography.headlineLarge, fontWeight = FontWeight.SemiBold) }
        item {
            Card(colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceVariant)) {
                Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
                    Text("Servicios", style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold)
                    OutlinedTextField(url, { url = it }, label = { Text("URL de FlowHub") }, supportingText = { Text("Usa una URL HTTPS. Tailscale es opcional para el acceso privado entre dispositivos.") }, singleLine = true, modifier = Modifier.fillMaxWidth())
                    OutlinedTextField(groqKey, { groqKey = it }, label = { Text("Clave de Groq") }, placeholder = { Text(if (hasGroqKey) "Clave guardada" else "Introduce tu clave") }, visualTransformation = PasswordVisualTransformation(), singleLine = true, modifier = Modifier.fillMaxWidth())
                    OutlinedTextField(token, { token = it }, label = { Text("Token de FlowHub") }, placeholder = { Text(if (hasToken) "Token guardado" else "Opcional") }, visualTransformation = PasswordVisualTransformation(), singleLine = true, modifier = Modifier.fillMaxWidth())
                    Text("Tema", style = MaterialTheme.typography.labelLarge)
                    Row(horizontalArrangement = Arrangement.spacedBy(8.dp), modifier = Modifier.fillMaxWidth()) {
                        listOf("system" to "Sistema", "light" to "Claro", "dark" to "Oscuro").forEach { (value, label) ->
                            OutlinedButton(onClick = { theme = value }, modifier = Modifier.weight(1f)) { Text(if (theme == value) "✓ $label" else label) }
                        }
                    }
                    Button(onClick = { onSave(url, groqKey, token, theme); groqKey = ""; token = "" }, modifier = Modifier.fillMaxWidth()) { Text("Guardar configuración") }
                    Row(horizontalArrangement = Arrangement.spacedBy(8.dp), modifier = Modifier.fillMaxWidth()) { OutlinedButton(onClick = { onTest(url) }, modifier = Modifier.weight(1f)) { Text("Probar conexión") }; OutlinedButton(onClick = onScheduleSync, modifier = Modifier.weight(1f)) { Text("Activar sync") } }
                    OutlinedButton(onClick = onRequestOverlay, modifier = Modifier.fillMaxWidth()) { Text("Permitir burbuja flotante") }
                    OutlinedButton(onClick = onRequestTile, modifier = Modifier.fillMaxWidth()) { Text("Añadir botón a Ajustes rápidos") }
                    OutlinedButton(onClick = onRequestTextInsertion, modifier = Modifier.fillMaxWidth()) { Text(if (textInsertionEnabled) "Configurar inserción automática ✓" else "Activar inserción automática") }
                    Text(
                        if (textInsertionEnabled) "Flow puede insertar el resultado en el campo de texto que tenga el foco."
                        else "Activa Flow en Ajustes → Accesibilidad para insertar automáticamente la transcripción en otras aplicaciones.",
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        style = MaterialTheme.typography.bodySmall
                    )
                    Text("Después podrás iniciar el dictado desde el panel donde están el brillo y el volumen. La burbuja aparecerá sobre cualquier aplicación.", color = MaterialTheme.colorScheme.onSurfaceVariant, style = MaterialTheme.typography.bodySmall)
                    if (hasGroqKey) TextButton(onClick = onRemoveKey) { Icon(Icons.Outlined.DeleteOutline, null); Spacer(Modifier.width(6.dp)); Text("Borrar clave de Groq") }
                }
            }
        }
        item { Text(status, color = MaterialTheme.colorScheme.onSurfaceVariant) }
        item { Card { Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) { Text("Privacidad", style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold); Text("El audio se procesa con Groq y no se guarda en FlowHub. Las claves se cifran con Android Keystore. Los dictados quedan en SQLite hasta que puedan sincronizarse.", color = MaterialTheme.colorScheme.onSurfaceVariant) } } }
    }
}

@Composable private fun EmptyState(text: String) { Box(Modifier.fillMaxWidth().padding(vertical = 28.dp), contentAlignment = Alignment.Center) { Text(text, color = MaterialTheme.colorScheme.onSurfaceVariant) } }
private fun formatDate(value: Long): String = DateFormat.getDateTimeInstance(DateFormat.MEDIUM, DateFormat.SHORT).format(Date(value))
private fun formatDate(value: String): String = value.replace("T", " ").substringBefore(".")
private fun timestamp(value: Long): String {
    val seconds = (value / 1_000).coerceAtLeast(0L)
    return "%02d:%02d".format(seconds / 60, seconds % 60)
}
