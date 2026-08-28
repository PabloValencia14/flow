package com.pablo.flow

/**
 * Maps the active Android package and, for browsers, the window title to a
 * normalized destination. Only the normalized label is retained; message or
 * page content is never copied into the dictation context.
 */
object ForegroundTargetDetector {
    fun detect(packageName: String?, windowTitle: String? = null): String? {
        val pkg = packageName.orEmpty().lowercase()
        val title = windowTitle.orEmpty().lowercase()

        return when {
            pkg == "com.whatsapp" || pkg == "com.whatsapp.w4b" -> "WhatsApp"
            pkg == "com.google.android.gm" -> "Gmail"
            pkg == "com.openai.chatgpt" -> "ChatGPT"
            pkg == "org.telegram.messenger" -> "Telegram"
            pkg == "com.microsoft.office.outlook" -> "Outlook"
            pkg == "com.slack" -> "Slack"
            pkg == "com.discord" -> "Discord"
            pkg == "com.microsoft.teams" -> "Teams"
            title.contains("whatsapp") -> "WhatsApp"
            title.contains("gmail") || title.contains("mail.google.com") -> "Gmail"
            title.contains("chatgpt") || title.contains("chat.openai.com") -> "ChatGPT"
            title.contains("telegram") -> "Telegram"
            else -> null
        }
    }

    fun styleKey(targetAppName: String?): String = when (targetAppName?.lowercase()) {
        "whatsapp", "telegram" -> "style_personal"
        "gmail", "outlook" -> "style_email"
        "slack", "teams", "discord" -> "style_work"
        "cursor", "windsurf", "visual studio", "visual studio code" -> "style_code"
        "chatgpt" -> "style_chat"
        else -> "style_personal"
    }
}
