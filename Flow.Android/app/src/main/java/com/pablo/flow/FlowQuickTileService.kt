package com.pablo.flow

import android.app.PendingIntent
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.provider.Settings
import android.service.quicksettings.Tile
import android.service.quicksettings.TileService
import android.util.Log
import androidx.core.content.ContextCompat

class FlowQuickTileService : TileService() {
    companion object {
        private const val TAG = "FlowQuickTile"
        private const val REQUEST_OVERLAY = 42
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
        Log.i(TAG, "onClick recibido: locked=$isLocked")
        // La pulsación del tile es una acción explícita del usuario. No abrimos
        // MainActivity para iniciar el dictado: el servicio FGS recibe la orden
        // directamente, por lo que la aplicación que estaba en primer plano no
        // pierde el foco y la burbuja aparece encima de ella.
        // En algunas capas del fabricante, unlockAndRun no ejecuta el callback
        // cuando el dispositivo ya está desbloqueado. Ejecutamos directamente
        // en ese caso y conservamos unlockAndRun solo para la pantalla bloqueada.
        if (isLocked) unlockAndRun { handleClick() } else handleClick()
    }

    private fun handleClick() {
        Log.i(TAG, "Tile pulsado: active=${FlowOverlayState.isActive}, mic=${ContextCompat.checkSelfPermission(this, android.Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED}, overlay=${Settings.canDrawOverlays(this)}")
        if (FlowOverlayState.isActive) {
            runCatching {
                startService(Intent(this, FlowOverlayService::class.java).apply {
                    action = FlowOverlayService.ACTION_STOP
                })
            }.onFailure { error ->
                Log.e(TAG, "No se pudo detener el dictado desde Ajustes rápidos", error)
            }
        } else if (ContextCompat.checkSelfPermission(this, android.Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
            // La actividad translúcida puede solicitar el permiso sin mostrar el
            // panel principal ni quitar el foco a la aplicación actual.
            launchQuickStartActivity()
        } else if (!Settings.canDrawOverlays(this)) {
            requestOverlayPermission()
        } else {
            // Android 14+ concede RECORD_AUDIO como permiso "while in use".
            // El tile autoriza el arranque del FGS, pero no siempre concede a
            // la vez esa capacidad al proceso que estaba en segundo plano.
            // Una Activity translúcida de un instante hace que el arranque sea
            // visible para Android sin tapar la aplicación que estaba abierta.
            launchQuickStartActivity()
        }
        updateTile()
    }

    private fun launchQuickStartActivity() {
        val intent = Intent(this, QuickStartActivity::class.java).apply {
            action = QuickStartActivity.ACTION_START_DICTATION
            addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_NO_ANIMATION)
        }
        runCatching {
            val pending = PendingIntent.getActivity(
                this,
                44,
                intent,
                PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
            )
            if (Build.VERSION.SDK_INT >= 34) startActivityAndCollapse(pending) else startActivity(intent)
            Log.i(TAG, "Activity transparente de arranque enviada")
        }.onFailure { error ->
            // Algunas capas de Android bloquean la Activity desde el tile. El
            // tile no debe propagar la excepción a SystemUI ni abrir MainActivity.
            Log.e(TAG, "No se pudo iniciar Flow desde Ajustes rápidos", error)
        }
    }

    private fun requestOverlayPermission() {
        val settingsIntent = Intent(Settings.ACTION_MANAGE_OVERLAY_PERMISSION).apply {
            data = android.net.Uri.parse("package:$packageName")
            addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        }
        runCatching {
            if (Build.VERSION.SDK_INT >= 34) {
                val pendingSettings = PendingIntent.getActivity(
                    this,
                    REQUEST_OVERLAY,
                    settingsIntent,
                    PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
                )
                startActivityAndCollapse(pendingSettings)
            } else {
                startActivity(settingsIntent)
            }
        }.onFailure { error ->
            Log.e(TAG, "No se pudo abrir el permiso de burbuja", error)
        }
    }

    private fun updateTile() {
        qsTile?.apply {
            label = "Flow"
            contentDescription = if (FlowOverlayState.isActive) "Detener dictado de Flow" else "Iniciar dictado de Flow"
            state = if (FlowOverlayState.isActive) Tile.STATE_ACTIVE else Tile.STATE_INACTIVE
            updateTile()
        }
    }
}
