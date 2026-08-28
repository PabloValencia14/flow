package com.pablo.flow

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.os.Build
import androidx.core.app.NotificationCompat

object FlowNotificationManager {
    const val CHANNEL_DICTATION = "flow_dictation"
    const val CHANNEL_MEETING = "flow_meeting"
    const val NOTIFICATION_DICTATION = 1001
    const val NOTIFICATION_MEETING = 1002

    fun createChannels(context: Context) {
        val manager = context.getSystemService(NotificationManager::class.java) ?: return

        val dictationChannel = NotificationChannel(
            CHANNEL_DICTATION, "Dictado de voz", NotificationManager.IMPORTANCE_LOW
        ).apply {
            description = "Notificación activa durante la grabación de dictados"
            setShowBadge(false)
        }

        val meetingChannel = NotificationChannel(
            CHANNEL_MEETING, "Grabación de reuniones", NotificationManager.IMPORTANCE_LOW
        ).apply {
            description = "Notificación activa durante la grabación de reuniones o clases"
            setShowBadge(false)
        }

        manager.createNotificationChannel(dictationChannel)
        manager.createNotificationChannel(meetingChannel)
    }

    fun buildDictationNotification(context: Context, recording: Boolean, elapsed: String = "0:00"): Notification {
        val openIntent = Intent(context, MainActivity::class.java).apply {
            flags = Intent.FLAG_ACTIVITY_SINGLE_TOP
        }
        val pendingOpen = PendingIntent.getActivity(context, 0, openIntent, PendingIntent.FLAG_IMMUTABLE)

        val stopIntent = Intent(context, FlowOverlayService::class.java).apply {
            action = "STOP_DICTATION"
        }
        val pendingStop = PendingIntent.getService(context, 1, stopIntent, PendingIntent.FLAG_IMMUTABLE)

        return NotificationCompat.Builder(context, CHANNEL_DICTATION)
            .setSmallIcon(android.R.drawable.ic_btn_speak_now)
            .setContentTitle(if (recording) "Grabando dictado — $elapsed" else "Flow — Transcribiendo…")
            .setContentText(if (recording) "Toca para abrir Flow" else "Procesando tu dictado…")
            .setOngoing(true)
            .setContentIntent(pendingOpen)
            .addAction(android.R.drawable.ic_menu_close_clear_cancel, "Detener", pendingStop)
            .setCategory(NotificationCompat.CATEGORY_SERVICE)
            .setSilent(true)
            .build()
    }

    fun buildMeetingNotification(context: Context, title: String, elapsed: String): Notification {
        val openIntent = Intent(context, MainActivity::class.java).apply {
            flags = Intent.FLAG_ACTIVITY_SINGLE_TOP
        }
        val pendingOpen = PendingIntent.getActivity(context, 2, openIntent, PendingIntent.FLAG_IMMUTABLE)

        val stopIntent = Intent(context, MeetingRecordService::class.java).apply {
            action = "STOP_MEETING"
        }
        val pendingStop = PendingIntent.getService(context, 3, stopIntent, PendingIntent.FLAG_IMMUTABLE)

        return NotificationCompat.Builder(context, CHANNEL_MEETING)
            .setSmallIcon(android.R.drawable.ic_btn_speak_now)
            .setContentTitle("Grabando: $title — $elapsed")
            .setContentText("Grabación de reunión/clase en curso")
            .setOngoing(true)
            .setContentIntent(pendingOpen)
            .addAction(android.R.drawable.ic_media_pause, "Finalizar reunión", pendingStop)
            .setCategory(NotificationCompat.CATEGORY_SERVICE)
            .setUsesChronometer(true)
            .setSilent(true)
            .build()
    }
}
