package com.pablo.flow

import android.content.Context
import java.util.UUID

class FlowPreferences(context: Context) {
    private val preferences = context.getSharedPreferences("flow_preferences", Context.MODE_PRIVATE)

    var serverUrl: String?
        get() = preferences.getString("server_url", null)?.trim()?.takeIf { it.isNotEmpty() }
        set(value) = preferences.edit().putString("server_url", value?.trim()).apply()

    var darkMode: String
        get() = preferences.getString("dark_mode", "system") ?: "system"
        set(value) = preferences.edit().putString("dark_mode", value).apply()

    var soundsEnabled: Boolean
        get() = preferences.getBoolean("sounds_enabled", true)
        set(value) = preferences.edit().putBoolean("sounds_enabled", value).apply()

    val deviceId: String
        get() {
            val existing = preferences.getString("device_id", null)
            if (existing != null) return existing
            val created = "android-" + UUID.randomUUID().toString()
            preferences.edit().putString("device_id", created).apply()
            return created
        }

}
