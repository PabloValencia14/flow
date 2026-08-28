namespace Flow.Windows;

using System.IO;

public static class MeetingExportFormatter
{
    public static string Markdown(MeetingHistoryItem meeting)
    {
        var lines = new List<string>
        {
            $"# {meeting.Title}", "", $"Fecha: {meeting.StartedAt:dd/MM/yyyy HH:mm} UTC", $"Flow meeting ID: `{meeting.Id}`", "",
            "## Resumen", "", meeting.Summary ?? "—", "", "## Participantes", "",
            meeting.Participants.Count > 0 ? string.Join(", ", meeting.Participants) : "—", "", "## Acuerdos", ""
        };
        lines.AddRange(meeting.Agreements.Count > 0 ? meeting.Agreements.Select(item => $"- {item}") : ["- —"]);
        lines.AddRange(["", "## Tareas", ""]);
        lines.AddRange(meeting.Tasks.Count > 0 ? meeting.Tasks.Select(item => $"- [ ] {item}") : ["- [ ] —"]);
        lines.AddRange(["", "## Transcripción", ""]);
        lines.AddRange(meeting.Segments.Count > 0
            ? meeting.Segments.OrderBy(item => item.StartMs).Select(item => $"- **[{Timestamp(item.StartMs)}] {item.Speaker}:** {item.Text}")
            : [meeting.Transcript ?? "—"]);
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    public static string Text(MeetingHistoryItem meeting) => meeting.Segments.Count > 0
        ? string.Join(Environment.NewLine + Environment.NewLine, meeting.Segments.OrderBy(item => item.StartMs).Select(item => $"[{Timestamp(item.StartMs)}] {item.Speaker}: {item.Text}"))
        : meeting.Transcript ?? string.Empty;

    public static string SafeName(string value) => string.IsNullOrWhiteSpace(value)
        ? "reunion"
        : string.Concat(value.Trim().Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));

    private static string Timestamp(long milliseconds)
    {
        var seconds = Math.Max(0, milliseconds / 1_000);
        return $"{seconds / 60:00}:{seconds % 60:00}";
    }
}
