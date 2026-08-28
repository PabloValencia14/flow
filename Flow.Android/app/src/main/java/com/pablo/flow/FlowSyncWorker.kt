package com.pablo.flow

import android.content.Context
import android.os.Build
import androidx.work.Constraints
import androidx.work.CoroutineWorker
import androidx.work.ExistingPeriodicWorkPolicy
import androidx.work.ExistingWorkPolicy
import androidx.work.NetworkType
import androidx.work.OneTimeWorkRequestBuilder
import androidx.work.PeriodicWorkRequestBuilder
import androidx.work.WorkManager
import androidx.work.WorkerParameters
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.util.concurrent.TimeUnit

class FlowSyncWorker(context: Context, params: WorkerParameters) : CoroutineWorker(context, params) {
    override suspend fun doWork(): Result = withContext(Dispatchers.IO) {
        val preferences = FlowPreferences(applicationContext)
        val secureStore = SecureStore(applicationContext)
        val localStore = LocalStore(applicationContext)
        val serverUrl = preferences.serverUrl ?: return@withContext Result.success()
        try {
            val sync = FlowSyncClient(secureStore)
            if (!sync.registerDevice(
                    serverUrl,
                    preferences.deviceId,
                    Build.MODEL.ifBlank { "Android" },
                    "android",
                    Build.VERSION.RELEASE ?: "unknown"
                )
            ) return@withContext Result.retry()
            val pending = localStore.pending(500)
            if (pending.isNotEmpty()) {
                when (val result = sync.push(serverUrl, preferences.deviceId, pending)) {
                    is SyncResult.Pushed -> {
                        localStore.remove(result.acknowledgedEventIds)
                    }
                    is SyncResult.Failed -> return@withContext Result.retry()
                    SyncResult.NotNeeded -> Unit
                }
            }
            localStore.unsyncedMeetings(20).forEach { meeting ->
                when (sync.uploadMeetingAudio(serverUrl, meeting)) {
                    is SyncResult.Pushed, SyncResult.NotNeeded -> localStore.markMeetingSynced(meeting.id)
                    is SyncResult.Failed -> return@withContext Result.retry()
                }
            }
            when (sync.pullAndApply(serverUrl, localStore)) {
                is PullResult.Failed -> Result.retry()
                else -> Result.success()
            }
        } finally {
            localStore.close()
        }
    }

    companion object {
        private const val UNIQUE_NAME = "flow-periodic-sync"

        fun schedule(context: Context) {
            val constraints = Constraints.Builder()
                .setRequiredNetworkType(NetworkType.CONNECTED)
                .build()
            val request = PeriodicWorkRequestBuilder<FlowSyncWorker>(15, TimeUnit.MINUTES)
                .setConstraints(constraints)
                .build()
            WorkManager.getInstance(context).enqueueUniquePeriodicWork(
                UNIQUE_NAME,
                ExistingPeriodicWorkPolicy.KEEP,
                request
            )
        }

        fun runNow(context: Context) {
            val constraints = Constraints.Builder()
                .setRequiredNetworkType(NetworkType.CONNECTED)
                .build()
            val request = OneTimeWorkRequestBuilder<FlowSyncWorker>()
                .setConstraints(constraints)
                .build()
            WorkManager.getInstance(context).enqueueUniqueWork(
                "flow-sync-now",
                ExistingWorkPolicy.REPLACE,
                request
            )
        }
    }
}
