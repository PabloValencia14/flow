package com.pablo.flow

import android.content.Context
import java.io.File
import java.nio.charset.StandardCharsets

/** Imports a one-shot credential staged in the app-private directory. */
object FlowProvisioning {
    private const val GROQ_STAGING_FILE = ".flow-provisioning-groq"
    private const val FLOWHUB_STAGING_FILE = ".flow-provisioning-flowhub"

    /**
     * Reads and immediately removes the temporary Groq credential, storing it
     * in SecureStore. The staging file is never included in the APK or backup
     * data and is deleted even when validation/import fails.
     */
    fun importPendingGroqKey(context: Context): String? {
        val stagingFile = File(context.noBackupFilesDir, GROQ_STAGING_FILE)
        if (!stagingFile.isFile) return null

        return try {
            val value = stagingFile.readText(StandardCharsets.UTF_8).trim()
            require(value.startsWith("gsk_") && value.length >= 16) {
                "La credencial de Groq recibida no tiene un formato válido."
            }
            require(value.all { it.code in 33..126 }) {
                "La credencial de Groq recibida contiene caracteres no válidos."
            }
            SecureStore(context.applicationContext).put("groq_api_key", value)
            "Clave de Groq importada desde Windows y guardada de forma segura."
        } catch (error: Exception) {
            "No se pudo importar la clave de Groq: ${error.message ?: "formato no válido"}"
        } finally {
            runCatching { stagingFile.delete() }
        }
    }

    /** Imports the FlowHub URL and token staged by the trusted local setup script. */
    fun importPendingFlowHub(context: Context): String? {
        val stagingFile = File(context.noBackupFilesDir, FLOWHUB_STAGING_FILE)
        if (!stagingFile.isFile) return null

        return try {
            val lines = stagingFile.readLines(StandardCharsets.UTF_8)
            val url = lines.firstOrNull()?.trim().orEmpty()
            val token = lines.drop(1).joinToString("\n").trim()
            val parsed = java.net.URI(url)
            val isHttps = parsed.scheme.equals("https", ignoreCase = true)
            val isTailscaleHttp = parsed.scheme.equals("http", ignoreCase = true) && isTailscaleIpv4(parsed.host)
            require((isHttps || isTailscaleHttp) && !parsed.host.isNullOrBlank()) {
                "La URL de FlowHub debe ser HTTPS o una IP Tailscale 100.64.0.0/10."
            }
            require(token.isNotBlank() && token.all { it.code in 33..126 }) {
                "El token de FlowHub no tiene un formato válido."
            }
            FlowPreferences(context.applicationContext).serverUrl = url
            SecureStore(context.applicationContext).put("flowhub_app_token", token)
            "FlowHub configurado y token guardado de forma segura."
        } catch (error: Exception) {
            "No se pudo importar la configuración de FlowHub: ${error.message ?: "formato no válido"}"
        } finally {
            runCatching { stagingFile.delete() }
        }
    }

    private fun isTailscaleIpv4(host: String?): Boolean {
        val octets = host?.split('.')?.mapNotNull { it.toIntOrNull() } ?: return false
        return octets.size == 4 && octets[0] == 100 && octets[1] in 64..127 && octets.all { it in 0..255 }
    }
}
