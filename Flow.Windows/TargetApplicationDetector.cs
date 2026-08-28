namespace Flow.Windows;

/// <summary>
/// Identifies the destination profile from the foreground process and, when
/// available, the foreground window title. Browser titles are used only to
/// classify the destination; the complete title is never stored or synced.
/// </summary>
public static class TargetApplicationDetector
{
    public static string? Detect(string? processName, string? windowTitle)
    {
        var identity = string.Join(' ', new[] { processName, windowTitle }
            .Where(value => !string.IsNullOrWhiteSpace(value)))
            .ToLowerInvariant();

        if (identity.Length == 0) return null;
        if (ContainsAny(identity, "whatsapp", "whatsapp web")) return "WhatsApp";
        if (ContainsAny(identity, "gmail", "mail.google.com")) return "Gmail";
        if (ContainsAny(identity, "chatgpt", "chat.openai.com", "openai chat")) return "ChatGPT";
        if (ContainsAny(identity, "outlook", "thunderbird")) return "Outlook";
        if (ContainsAny(identity, "slack")) return "Slack";
        if (ContainsAny(identity, "msteams", "microsoft teams", "teams")) return "Teams";
        if (ContainsAny(identity, "discord")) return "Discord";
        if (ContainsAny(identity, "telegram")) return "Telegram";
        if (ContainsAny(identity, "cursor")) return "Cursor";
        if (ContainsAny(identity, "windsurf")) return "Windsurf";
        if (ContainsAny(identity, "devenv", "visual studio")) return "Visual Studio";
        if (ContainsAny(identity, "code")) return "Visual Studio Code";

        // Vivaldi is a supported browser even when its tab title does not
        // identify a known destination. Page-specific matches above always
        // win, so WhatsApp/Gmail/ChatGPT keep their contextual profiles.
        if (ContainsAny(processName?.ToLowerInvariant() ?? string.Empty, "vivaldi")) return "Vivaldi";

        return processName?.Trim();
    }

    private static bool ContainsAny(string value, params string[] candidates) =>
        candidates.Any(value.Contains);
}
