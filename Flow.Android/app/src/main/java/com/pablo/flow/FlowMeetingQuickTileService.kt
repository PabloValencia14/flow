package com.pablo.flow

import android.app.PendingIntent
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.service.quicksettings.Tile
import android.service.quicksettings.TileService
import android.util.Log
import androidx.core.content.ContextCompat
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/** Quick Settings entry point dedicated to meeting and class recordings. */
class FlowMeetingQuickTileService : TileService() {
    companion object {
        private const val TAG = "FlowMeetingTile"
        private const val REQUEST_START = 45
    }

    override fun onTileAdded() {
        super.onTileAdded()
        updateTile()
    }

    override fun onStartListening() {
        super.onStartListening()
        updateTile()
    }

    override fun onClick() {
        super.onClick()
        Log.i(TAG, "onClick recibido: locked=$isLocked active=${FlowMeetingState.isActive}")
        if (isLocked) unlockAndRun { handleClick() } else handleClick()
    }

    private fun handleClick() {
        val micGranted = ContextCompat.checkSelfPermission(
            this,
            android.Manifest.permission.RECORD_AUDIO
        ) == PackageManager.PERMISSION_GRANTED

        if (FlowMeetingState.isActive) {
            runCatching {
                startService(Intent(this, MeetingRecordService::class.java).apply {
                    action = MeetingRecordService.ACTION_STOP
                })
            }.onFailure { error ->
                Log.e(TAG, "No se pudo detener la reunión desde Ajustes rápidos", error)
            }
        } else if (!micGranted) {
            launchQuickStartActivity()
        } else if (FlowOverlayState.isActive) {
            // Do not try to open a second microphone recorder while dictating.
            Log.w(TAG, "No se inicia la reunión: hay un dictado activo")
        } else {
            launchQuickStartActivity()
        }
        updateTile()
    }

    private fun launchQuickStartActivity() {
        val title = "Reunión rápida · " + SimpleDateFormat(
            "yyyy-MM-dd HH:mm",
            Locale.getDefault()
        ).format(Date())
        val intent = Intent(this, QuickStartActivity::class.java).apply {
            action = QuickStartActivity.ACTION_START_MEETING
            putExtra(QuickStartActivity.EXTRA_MEETING_TITLE, title)
            addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_NO_ANIMATION)
        }
        runCatching {
            val pending = PendingIntent.getActivity(
                this,
                REQUEST_START,
                intent,
                PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
            )
            if (Build.VERSION.SDK_INT >= 34) startActivityAndCollapse(pending) else startActivity(intent)
            Log.i(TAG, "Activity transparente de reunión enviada")
        }.onFailure { error ->
            Log.e(TAG, "No se pudo iniciar la reunión desde Ajustes rápidos", error)
        }
    }

    private fun updateTile() {
        qsTile?.apply {
            label = "Flow · Reunión"
            contentDescription = if (FlowMeetingState.isActive) {
                "Finalizar reunión de Flow"
            } else {
                "Iniciar reunión de Flow"
            }
            state = if (FlowMeetingState.isActive) Tile.STATE_ACTIVE else Tile.STATE_INACTIVE
            updateTile()
        }
    }
}
