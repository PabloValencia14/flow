package com.pablo.flow

import android.animation.ObjectAnimator
import android.app.Service
import android.graphics.Color
import android.graphics.drawable.GradientDrawable
import android.graphics.drawable.StateListDrawable
import android.content.ComponentName
import android.content.Intent
import android.content.pm.PackageManager
import android.provider.Settings
import android.service.quicksettings.TileService
import android.view.Gravity
import android.view.View
import android.view.WindowManager
import android.widget.ImageButton
import android.widget.LinearLayout
import android.widget.TextView
import android.os.IBinder
import android.os.Handler
import android.os.Looper
import android.os.Build
import android.content.pm.ServiceInfo
import android.view.animation.DecelerateInterpolator
import android.view.animation.PathInterpolator
import android.graphics.Typeface
import java.util.Locale
import androidx.core.app.ServiceCompat
import androidx.core.content.ContextCompat

class FlowOverlayService : Service() {
    companion object {
        private const val TAG = "FlowOverlayService"
        const val ACTION_START = "com.pablo.flow.action.START_DICTATION"
        const val ACTION_STOP = "com.pablo.flow.action.STOP_DICTATION"
        const val ACTION_CANCEL = "com.pablo.flow.action.CANCEL_DICTATION"
    }

    private var engine: FlowEngine? = null
    private var overlay: View? = null
    private var bars = emptyList<View>()
    private var elapsedText: TextView? = null
    private var stateDot: View? = null
    private var pulseAnimator: ObjectAnimator? = null
    private val windowManager by lazy { getSystemService(WINDOW_SERVICE) as WindowManager }
    private val handler = Handler(Looper.getMainLooper())
    private var startTime = 0L
    private val timerRunnable = object : Runnable {
        override fun run() {
            if (engine?.state == FlowState.RECORDING) {
                val elapsed = (System.currentTimeMillis() - startTime) / 1000
                val label = String.format(Locale.ROOT, "%d:%02d", elapsed / 60, elapsed % 60)
                val notification = FlowNotificationManager.buildDictationNotification(
                    this@FlowOverlayService, true, label
                )
                val nm = getSystemService(android.app.NotificationManager::class.java)
                nm?.notify(FlowNotificationManager.NOTIFICATION_DICTATION, notification)
                elapsedText?.text = label
                handler.postDelayed(this, 500)
            }
        }
    }

    private val listener = object : FlowEngineListener {
        override fun onStateChanged(state: FlowState) {
                FlowOverlayState.state = state
                handler.post { updateOverlayState(state) }
            when (state) {
                FlowState.RECORDING -> {
                    startTime = System.currentTimeMillis()
                    handler.post(timerRunnable)
                }
                FlowState.TRANSCRIBING -> {
                    handler.removeCallbacks(timerRunnable)
                    val notification = FlowNotificationManager.buildDictationNotification(this@FlowOverlayService, false)
                    val nm = getSystemService(android.app.NotificationManager::class.java)
                    nm?.notify(FlowNotificationManager.NOTIFICATION_DICTATION, notification)
                }
                FlowState.IDLE -> {
                    handler.removeCallbacks(timerRunnable)
                    FlowOverlayState.isActive = false
                    notifyTileState()
                    stopSelf()
                }
                else -> {}
            }
        }

        override fun onAudioLevel(level: Float) {
            FlowOverlayState.audioLevel = level
            handler.post { updateOverlayLevel(level) }
        }

        override fun onStatus(message: String) {
            FlowOverlayState.statusMessage = message
        }

        override fun onResult(result: DictationResult) {
            FlowOverlayState.lastResult = result
            FlowOverlayState.statusMessage = "Insertando…"
            FlowTextAccessibilityService.insertIntoFocusedField(this@FlowOverlayService, result.text) { outcome ->
                FlowOverlayState.statusMessage = outcome.message
            }
        }

        override fun onError(message: String, cause: Throwable?) {
            FlowOverlayState.statusMessage = message
        }
    }

    override fun onCreate() {
        super.onCreate()
        android.util.Log.i(TAG, "FlowOverlayService creado")
        FlowNotificationManager.createChannels(this)
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        android.util.Log.i(TAG, "Orden recibida: ${intent?.action}")
        when (intent?.action) {
            ACTION_START, "START_DICTATION" -> startDictation()
            ACTION_STOP, "STOP_DICTATION" -> stopDictation()
            ACTION_CANCEL, "CANCEL_DICTATION" -> cancelDictation()
            else -> stopSelf()
        }
        return START_NOT_STICKY
    }

    private fun startDictation() {
        android.util.Log.i(TAG, "Preparando inicio de grabación")
        if (engine?.state != null && engine?.state != FlowState.IDLE) return
        try {
            if (ContextCompat.checkSelfPermission(this, android.Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
                throw SecurityException("Android no ha concedido permiso para el micrófono.")
            }
            val notification = FlowNotificationManager.buildDictationNotification(this, true)
            ServiceCompat.startForeground(
                this,
                FlowNotificationManager.NOTIFICATION_DICTATION,
                notification,
                if (Build.VERSION.SDK_INT >= 30) ServiceInfo.FOREGROUND_SERVICE_TYPE_MICROPHONE else 0
            )
            engine = FlowEngine(this, listener)
            FlowOverlayState.lastResult = null
            FlowOverlayState.statusMessage = "Preparando micrófono…"
            engine?.start()
            FlowOverlayState.isActive = true
            if (Settings.canDrawOverlays(this)) showOverlay()
            else FlowOverlayState.statusMessage = "Permite la burbuja flotante para mostrar Dynamic Island."
            notifyTileState()
        } catch (error: Exception) {
            android.util.Log.e(TAG, "No se pudo iniciar el dictado", error)
            FlowOverlayState.statusMessage = error.message ?: "No se pudo iniciar el dictado."
            FlowOverlayState.isActive = false
            FlowOverlayState.state = FlowState.IDLE
            runCatching { ServiceCompat.stopForeground(this, ServiceCompat.STOP_FOREGROUND_REMOVE) }
            notifyTileState()
            stopSelf()
        }
    }

    private fun stopDictation() {
        engine?.finish()
    }

    private fun cancelDictation() {
        engine?.cancel()
        FlowOverlayState.isActive = false
        stopSelf()
    }

    private fun dp(value: Int): Int = (value * resources.displayMetrics.density + 0.5f).toInt()

    private fun pillBackground() = GradientDrawable().apply {
        setColor(Color.rgb(16, 16, 17))
        cornerRadius = dp(26).toFloat()
        setStroke(dp(1), Color.rgb(54, 54, 56))
    }

    private fun controlBackground() = StateListDrawable().apply {
        addState(intArrayOf(android.R.attr.state_pressed), GradientDrawable().apply {
            shape = GradientDrawable.OVAL
            setColor(Color.rgb(68, 68, 70))
            setStroke(dp(1), Color.rgb(105, 105, 108))
        })
        addState(intArrayOf(), GradientDrawable().apply {
            shape = GradientDrawable.OVAL
            setColor(Color.rgb(35, 35, 37))
            setStroke(dp(1), Color.rgb(67, 67, 70))
        })
    }

    private fun showOverlay() {
        if (overlay != null || !Settings.canDrawOverlays(this)) return
        val root = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            setPadding(dp(6), dp(3), dp(6), dp(3))
            background = pillBackground()
            elevation = dp(10).toFloat()
            contentDescription = "Flow: grabando. Pulsa cancelar o terminar."
        }
        fun icon(resource: Int, description: String, action: () -> Unit): ImageButton = ImageButton(this).apply {
            setImageResource(resource)
            contentDescription = description
            setColorFilter(Color.WHITE)
            background = controlBackground()
            scaleType = android.widget.ImageView.ScaleType.CENTER
            setPadding(0, 0, 0, 0)
            setOnClickListener { action() }
            minimumWidth = dp(42)
            minimumHeight = dp(42)
            layoutParams = LinearLayout.LayoutParams(dp(42), dp(42)).apply {
                setMargins(dp(2), 0, dp(2), 0)
            }
        }
        root.addView(icon(R.drawable.flow_close, "Cancelar dictado") { cancelDictation() })
        val center = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            layoutParams = LinearLayout.LayoutParams(dp(150), dp(44)).apply {
                setMargins(dp(2), 0, dp(2), 0)
            }
        }
        val dot = View(this).apply {
            background = GradientDrawable().apply {
                shape = GradientDrawable.OVAL
                setColor(Color.WHITE)
            }
            layoutParams = LinearLayout.LayoutParams(dp(8), dp(8)).apply {
                setMargins(0, 0, dp(8), 0)
            }
        }
        stateDot = dot
        center.addView(dot)
        val levelRow = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER
            setPadding(dp(2), 0, dp(2), 0)
            layoutParams = LinearLayout.LayoutParams(0, dp(44), 1f)
        }
        val initialHeights = intArrayOf(12, 18, 24, 30, 24, 18, 12)
        bars = (0 until 7).map { index ->
            View(this).apply {
                background = GradientDrawable().apply {
                    shape = GradientDrawable.RECTANGLE
                    setColor(Color.WHITE)
                    cornerRadius = dp(3).toFloat()
                }
                layoutParams = LinearLayout.LayoutParams(dp(3), dp(initialHeights[index])).apply {
                    setMargins(dp(2), 0, dp(2), 0)
                }
            }.also { levelRow.addView(it) }
        }
        center.addView(levelRow)
        elapsedText = TextView(this).apply {
            setTextColor(Color.WHITE)
            textSize = 11.5f
            text = getString(R.string.overlay_initial_time)
            typeface = Typeface.MONOSPACE
            gravity = Gravity.CENTER
            maxLines = 1
            layoutParams = LinearLayout.LayoutParams(dp(54), dp(44))
        }
        center.addView(elapsedText)
        root.addView(center)
        root.addView(icon(R.drawable.flow_check, "Terminar dictado") { stopDictation() })

        overlay = root
        val params = WindowManager.LayoutParams(
            WindowManager.LayoutParams.WRAP_CONTENT,
            WindowManager.LayoutParams.WRAP_CONTENT,
            WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY,
            WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE or WindowManager.LayoutParams.FLAG_NOT_TOUCH_MODAL,
            android.graphics.PixelFormat.TRANSLUCENT
        ).apply {
            gravity = Gravity.TOP or Gravity.CENTER_HORIZONTAL
            y = dp(14)
        }
        runCatching {
            windowManager.addView(root, params)
            root.alpha = 0f
            root.translationY = -dp(42).toFloat()
            root.scaleX = .98f
            root.scaleY = .98f
            pulseAnimator = ObjectAnimator.ofFloat(dot, View.ALPHA, 1f, .42f, 1f).apply {
                duration = 1100
                repeatCount = ObjectAnimator.INFINITE
                interpolator = DecelerateInterpolator()
            }
            root.post {
                pulseAnimator?.start()
                root.animate().alpha(1f).translationY(0f).scaleX(1f).scaleY(1f)
                    .setDuration(400)
                    .setInterpolator(PathInterpolator(.22f, 1f, .36f, 1f))
                    .start()
            }
        }.onFailure {
            overlay = null
            pulseAnimator?.cancel()
            pulseAnimator = null
        }
    }

    private fun updateOverlayState(state: FlowState) {
        if (state == FlowState.IDLE) {
            overlay?.animate()?.alpha(0f)?.translationY(-dp(18).toFloat())?.scaleX(.98f)?.scaleY(.98f)
                ?.setDuration(350)?.setInterpolator(PathInterpolator(.22f, 1f, .36f, 1f))
                ?.withEndAction { removeOverlay() }?.start()
            return
        }
        val processing = state != FlowState.RECORDING
        bars.forEach {
            it.alpha = if (processing) .45f else 1f
            it.visibility = if (processing) View.INVISIBLE else View.VISIBLE
        }
        elapsedText?.let { label ->
            if (processing) {
                label.text = "Procesando…"
                label.textSize = 11f
            } else if (label.text == "Procesando…") {
                label.text = getString(R.string.overlay_initial_time)
                label.textSize = 11.5f
            }
        }
        stateDot?.alpha = if (processing) .58f else 1f
        if (processing) pulseAnimator?.cancel() else if (pulseAnimator?.isStarted != true) pulseAnimator?.start()
    }

    private fun notifyTileState() {
        runCatching {
            TileService.requestListeningState(this, ComponentName(this, FlowQuickTileService::class.java))
        }
    }

    private fun updateOverlayLevel(level: Float) {
        val intensity = level.coerceIn(0f, 1f)
        bars.forEachIndexed { index, bar ->
            val factor = when (index) { 0, 6 -> .55f; 1, 5 -> .8f; else -> 1f }
            val height = dp((8 + intensity * 22 * factor).toInt())
            bar.layoutParams = (bar.layoutParams as LinearLayout.LayoutParams).apply { this.height = height }
            bar.requestLayout()
        }
    }

    private fun removeOverlay() {
        pulseAnimator?.cancel()
        pulseAnimator = null
        overlay?.let { view -> runCatching { windowManager.removeView(view) } }
        overlay = null
        bars = emptyList()
        elapsedText = null
        stateDot = null
    }

    override fun onDestroy() {
        handler.removeCallbacks(timerRunnable)
        removeOverlay()
        engine?.close()
        FlowOverlayState.isActive = false
        FlowOverlayState.state = FlowState.IDLE
        notifyTileState()
        super.onDestroy()
    }

    override fun onBind(intent: Intent?): IBinder? = null
}

/** Shared observable state for the Dynamic Island UI to react to */
object FlowOverlayState {
    @Volatile var isActive: Boolean = false
    @Volatile var state: FlowState = FlowState.IDLE
    @Volatile var audioLevel: Float = 0f
    @Volatile var statusMessage: String = ""
    @Volatile var lastResult: DictationResult? = null
}
