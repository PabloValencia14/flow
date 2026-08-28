using System.Security.Cryptography;
using System.IO;

namespace Flow.Windows;

public sealed class MeetingProcessor(LocalOutbox outbox, GroqTranscriber transcriber, GroqMeetingAnalyzer analyzer)
{
    public async Task<MeetingHistoryItem> ImportAsync(string sourcePath, string? title, string deviceId, Action<string>? status, CancellationToken cancellationToken)
    {
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("No se encontró el audio seleccionado.", sourcePath);
        var length = new FileInfo(sourcePath).Length;
        if (length > 25L * 1024 * 1024)
            throw new InvalidOperationException("El audio supera 25 MB, el límite de la cuenta gratuita de Groq.");

        var id = Guid.NewGuid().ToString();
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Flow", "Meetings", id);
        Directory.CreateDirectory(directory);
        var fileName = Path.GetFileName(sourcePath);
        var audioPath = Path.Combine(directory, fileName);
        File.Copy(sourcePath, audioPath, overwrite: false);
        status?.Invoke("Transcribiendo con timestamps…");
        var result = await transcriber.TranscribeDetailedAsync(await File.ReadAllBytesAsync(audioPath, cancellationToken), fileName, cancellationToken);
        var rawSegments = result.Segments.Select(item => new MeetingTranscriptSegmentItem(
            $"{id}-{item.Id}", item.Speaker, item.StartMs, item.EndMs, item.Text)).ToArray();
        status?.Invoke("Corrigiendo la transcripción completa…");
        var corrections = await analyzer.CorrectSegmentsAsync(rawSegments, cancellationToken);
        var segments = rawSegments.Select(item => corrections is not null && corrections.TryGetValue(item.Id, out var correctedText)
            ? item with { Text = correctedText }
            : item).ToArray();
        var transcript = segments.Length > 0
            ? string.Join(Environment.NewLine + Environment.NewLine, segments.OrderBy(item => item.StartMs).Select(item => $"[{FormatTimestamp(item.StartMs)}] {item.Speaker}: {item.Text}"))
            : result.Text;
        if (string.IsNullOrWhiteSpace(transcript)) throw new InvalidOperationException("Groq no devolvió texto para el audio.");

        status?.Invoke("Generando resumen contextual…");
        var analysis = await analyzer.AnalyzeAsync(transcript, cancellationToken);
        var started = DateTimeOffset.UtcNow;
        var meetingTitle = analysis?.Title;
        if (string.IsNullOrWhiteSpace(meetingTitle)) meetingTitle = title?.Trim();
        if (string.IsNullOrWhiteSpace(meetingTitle)) meetingTitle = "Reunión importada";
        var meeting = new MeetingHistoryItem(
            id,
            meetingTitle,
            started,
            started,
            segments.Length == 0 ? 0L : segments.Max(item => item.EndMs),
            analysis?.Summary,
            transcript,
            analysis?.Participants ?? ["Persona 1"],
            analysis?.Agreements ?? [],
            analysis?.Tasks ?? [],
            segments,
            audioPath,
            id,
            fileName,
            await Sha256Async(audioPath));
        await outbox.SaveMeetingAsync(meeting);
        await outbox.EnqueueMeetingAsync(meeting, deviceId);
        return meeting;
    }

    private static async Task<string> Sha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        using var hash = SHA256.Create();
        return Convert.ToHexString(await hash.ComputeHashAsync(stream)).ToLowerInvariant();
    }

    private static string FormatTimestamp(long milliseconds)
    {
        var seconds = Math.Max(0, milliseconds / 1_000);
        return $"{seconds / 60:00}:{seconds % 60:00}";
    }
}
