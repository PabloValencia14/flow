package com.pablo.flow

import android.accessibilityservice.AccessibilityService
import android.accessibilityservice.AccessibilityServiceInfo
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.os.Bundle
import android.util.Log
import android.view.accessibility.AccessibilityNodeInfo
import java.util.ArrayDeque

/**
 * Inserts a completed dictation into the editable field that had the input
 * focus in the application the user was dictating into.
 *
 * Android does not expose a general-purpose API for another application to
 * type text. This service is therefore an explicit user-enabled capability,
 * and it is deliberately limited to the currently focused editable node.
 */
class FlowTextAccessibilityService : AccessibilityService() {
    companion object {
        private const val TAG = "FlowTextAccessibility"
        private const val ACCESSIBILITY_EVENT_TYPES =
            android.view.accessibility.AccessibilityEvent.TYPE_VIEW_FOCUSED or
                android.view.accessibility.AccessibilityEvent.TYPE_WINDOW_STATE_CHANGED or
                android.view.accessibility.AccessibilityEvent.TYPE_WINDOW_CONTENT_CHANGED

        @Volatile
        private var activeService: FlowTextAccessibilityService? = null

        @Volatile
        private var activeTargetAppName: String? = null

        private val mainHandler = android.os.Handler(android.os.Looper.getMainLooper())

        fun isEnabled(): Boolean = activeService != null

        fun currentTargetAppName(): String? = activeTargetAppName

        fun insertIntoFocusedField(
            context: Context,
            text: String,
            onComplete: (InsertOutcome) -> Unit
        ) {
            // AccessibilityNodeInfo and ClipboardManager are used on Android's
            // main thread. The engine calls this callback from its worker.
            mainHandler.post {
                val service = activeService
                val outcome = if (service == null) {
                    copyToClipboard(context, text)
                    InsertOutcome(
                        inserted = false,
                        message = "Texto copiado. Activa la inserción de Flow en Accesibilidad."
                    )
                } else {
                    service.insertText(text)
                }
                onComplete(outcome)
            }
        }

        private fun copyToClipboard(context: Context, text: String) {
            val clipboard = context.getSystemService(ClipboardManager::class.java)
            clipboard?.setPrimaryClip(ClipData.newPlainText("Flow", text))
        }
    }

    data class InsertOutcome(val inserted: Boolean, val message: String)

    override fun onServiceConnected() {
        super.onServiceConnected()
        activeService = this
        serviceInfo = serviceInfo.apply {
            eventTypes = ACCESSIBILITY_EVENT_TYPES
            feedbackType = AccessibilityServiceInfo.FEEDBACK_GENERIC
            flags = flags or AccessibilityServiceInfo.FLAG_REPORT_VIEW_IDS
            notificationTimeout = 100
        }
        Log.i(TAG, "Servicio de inserción conectado")
    }

    override fun onAccessibilityEvent(event: android.view.accessibility.AccessibilityEvent?) {
        if (event == null || event.eventType != android.view.accessibility.AccessibilityEvent.TYPE_WINDOW_STATE_CHANGED) return
        val packageName = event.packageName?.toString().orEmpty()
        if (packageName.isBlank() || packageName == this.packageName || packageName == "com.android.systemui") return

        // Window-state text is used only as a service-name hint for browser
        // tabs. The raw title is not stored, logged, synced, or sent to Groq.
        val titleHint = event.text.joinToString(" ")
        activeTargetAppName = ForegroundTargetDetector.detect(packageName, titleHint)
    }

    override fun onInterrupt() = Unit

    override fun onDestroy() {
        if (activeService === this) activeService = null
        Log.i(TAG, "Servicio de inserción desconectado")
        super.onDestroy()
    }

    private fun insertText(text: String): InsertOutcome {
        val value = text.trim()
        if (value.isEmpty()) return InsertOutcome(false, "La transcripción está vacía.")

        val root = rootInActiveWindow
        val target = root?.let(::findFocusedEditable)
        if (target == null || target.isPassword) {
            copyToClipboard(this, value)
            return InsertOutcome(
                inserted = false,
                message = "No hay un campo de texto enfocado; texto copiado."
            )
        }

        val clipboard = getSystemService(ClipboardManager::class.java)
        clipboard?.setPrimaryClip(ClipData.newPlainText("Flow", value))
        if (target.performAction(AccessibilityNodeInfo.ACTION_PASTE)) {
            return InsertOutcome(true, "Insertado ✓")
        }

        // Some editors do not expose ACTION_PASTE but do expose SET_TEXT.
        // Preserve the existing value and caret instead of replacing the
        // whole field with the dictation.
        val existing = target.text?.toString().orEmpty()
        val start = target.textSelectionStart.takeIf { it >= 0 }?.coerceAtMost(existing.length)
            ?: existing.length
        val end = target.textSelectionEnd.takeIf { it >= 0 }?.coerceIn(start, existing.length)
            ?: start
        val merged = buildString(existing.length + value.length) {
            append(existing, 0, start)
            append(value)
            append(existing, end, existing.length)
        }
        val arguments = Bundle().apply {
            putCharSequence(AccessibilityNodeInfo.ACTION_ARGUMENT_SET_TEXT_CHARSEQUENCE, merged)
        }
        if (target.performAction(AccessibilityNodeInfo.ACTION_SET_TEXT, arguments)) {
            val caret = start + value.length
            target.performAction(
                AccessibilityNodeInfo.ACTION_SET_SELECTION,
                Bundle().apply {
                    putInt(AccessibilityNodeInfo.ACTION_ARGUMENT_SELECTION_START_INT, caret)
                    putInt(AccessibilityNodeInfo.ACTION_ARGUMENT_SELECTION_END_INT, caret)
                }
            )
            return InsertOutcome(true, "Insertado ✓")
        }

        copyToClipboard(this, value)
        return InsertOutcome(false, "No se pudo insertar; texto copiado.")
    }

    private fun findFocusedEditable(root: AccessibilityNodeInfo): AccessibilityNodeInfo? {
        val inputFocus = root.findFocus(AccessibilityNodeInfo.FOCUS_INPUT)
        if (inputFocus?.isEditable == true && !inputFocus.isPassword) return inputFocus

        val pending = ArrayDeque<AccessibilityNodeInfo>()
        pending.add(root)
        while (pending.isNotEmpty()) {
            val node = pending.removeFirst()
            if (node.isEditable && (node.isFocused || node.isAccessibilityFocused) && !node.isPassword) {
                return node
            }
            for (index in 0 until node.childCount) {
                node.getChild(index)?.let(pending::addLast)
            }
        }
        return null
    }

}
