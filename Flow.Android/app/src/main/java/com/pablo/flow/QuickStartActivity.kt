package com.pablo.flow

import android.app.Activity
import android.Manifest
import android.content.pm.PackageManager
import android.os.Bundle
import android.util.Log
import android.content.Intent
import androidx.core.content.ContextCompat

/**
 * Activity translúcida y efímera usada por el tile.
 *
 * Android exige que un FGS que usa el micrófono se promueva mientras la app
 * está visible. La Activity no pinta interfaz ni entra en Recientes: solo
 * proporciona ese contexto visible durante el arranque y devuelve el foco a
 * la aplicación que el usuario estaba utilizando.
 */
class QuickStartActivity : Activity() {
    companion object {
        private const val TAG = "FlowQuickStart"
        private const val REQUEST_MICROPHONE = 701
        const val ACTION_START_DICTATION = "com.pablo.flow.action.QUICK_START_DICTATION"
        const val ACTION_START_MEETING = "com.pablo.flow.action.QUICK_START_MEETING"
        const val EXTRA_MEETING_TITLE = "quick_start_meeting_title"
    }

    private var dispatched = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.setDimAmount(0f)
    }

    override fun onResume() {
        super.onResume()
        if (dispatched) return

        if (ContextCompat.checkSelfPermission(this, Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
            dispatched = true
            requestPermissions(arrayOf(Manifest.permission.RECORD_AUDIO), REQUEST_MICROPHONE)
            return
        }

        startFlowAndFinish()
    }

    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        if (requestCode != REQUEST_MICROPHONE) return
        if (grantResults.firstOrNull() == PackageManager.PERMISSION_GRANTED) {
            startFlowAndFinish()
        } else {
            Log.w(TAG, "El usuario no concedió permiso para el micrófono")
            finish()
        }
    }

    private fun startFlowAndFinish() {
        if (isFinishing) return
        dispatched = true

        runCatching {
            if (intent?.action == ACTION_START_MEETING) {
                ContextCompat.startForegroundService(
                    this,
                    Intent(this, MeetingRecordService::class.java).apply {
                        action = MeetingRecordService.ACTION_START
                        putExtra(
                            MeetingRecordService.EXTRA_TITLE,
                            intent?.getStringExtra(EXTRA_MEETING_TITLE)
                        )
                    }
                )
                Log.i(TAG, "Servicio de reunión enviado con Activity visible")
            } else {
                ContextCompat.startForegroundService(
                    this,
                    Intent(this, FlowOverlayService::class.java).apply {
                        action = FlowOverlayService.ACTION_START
                    }
                )
                Log.i(TAG, "Servicio de dictado enviado con Activity visible")
            }
        }.onFailure { error ->
            val kind = if (intent?.action == ACTION_START_MEETING) "reunión" else "dictado"
            Log.e(TAG, "No se pudo enviar el servicio de $kind", error)
        }

        // Dejamos terminar la transición de visibilidad antes de devolver el
        // foco, evitando que el servicio pierda la excepción de while-in-use.
        window.decorView.postDelayed({ finish() }, 120L)
    }
}
