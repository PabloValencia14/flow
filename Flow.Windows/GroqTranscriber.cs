using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.IO;

namespace Flow.Windows;

public sealed class GroqRateLimitException(string message) : InvalidOperationException(message);

public sealed class GroqTranscriber(HttpClient http)
{
    public const string Model = "whisper-large-v3";
    private const string DefaultPrompt = "Dictado breve en español de España. Transcribe literalmente las palabras pronunciadas.";

    public async Task<string> TranscribeAsync(byte[] wav, CancellationToken cancellationToken)
        => (await TranscribeDetailedAsync(wav, "flow-dictation.wav", cancellationToken)).Text;

    public async Task<IReadOnlyList<LocalWhisperSegment>> TranscribePreviewAsync(byte[] wav, CancellationToken cancellationToken)
    {
        var result = await TranscribeDetailedInternalAsync(wav, "flow-preview.wav", prompt: null, filterNoSpeech: true, cancellationToken);
        return result.Segments
            .Select(segment => new LocalWhisperSegment(
                segment.Text,
                TimeSpan.FromMilliseconds(segment.StartMs),
                TimeSpan.FromMilliseconds(segment.EndMs)))
            .ToArray();
    }

    public Task<MeetingGroqTranscription> TranscribeDetailedAsync(byte[] audio, string fileName, CancellationToken cancellationToken)
        => TranscribeDetailedInternalAsync(audio, fileName, DefaultPrompt, filterNoSpeech: false, cancellationToken);

    private async Task<MeetingGroqTranscription> TranscribeDetailedInternalAsync(
        byte[] audio,
        string fileName,
        string? prompt,
        bool filterNoSpeech,
        CancellationToken cancellationToken)
    {
        const int freeTierUploadLimit = 25 * 1024 * 1024;
        if (audio.Length <= 44)
            throw new InvalidOperationException("La captura no contiene audio PCM suficiente.");
        if (audio.Length > freeTierUploadLimit)
            throw new InvalidOperationException("La grabación supera 25 MB, el límite de subida de Groq para la cuenta gratuita.");

        var apiKey = CredentialStore.Read("Flow/GroqApiKey")
            ?? Environment.GetEnvironmentVariable("FLOW_GROQ_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Falta la credencial Flow/GroqApiKey en Credential Manager (o FLOW_GROQ_API_KEY solo para una prueba temporal).");

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.groq.com/openai/v1/audio/transcriptions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.UserAgent.ParseAdd("Flow.Windows/0.1");
        using var content = new MultipartFormDataContent();
        var audioContent = new ByteArrayContent(audio);
        audioContent.Headers.ContentType = new MediaTypeHeaderValue(ContentType(fileName));
        audioContent.Headers.ContentLength = audio.LongLength;
        content.Add(audioContent, "file", Path.GetFileName(fileName));
        content.Add(new StringContent(Model), "model");
        content.Add(new StringContent("verbose_json"), "response_format");
        content.Add(new StringContent("segment"), "timestamp_granularities[]");
        content.Add(new StringContent("es"), "language");
        if (!string.IsNullOrWhiteSpace(prompt))
            content.Add(new StringContent(prompt), "prompt");
        content.Add(new StringContent("0.0", System.Text.Encoding.UTF8), "temperature");
        request.Content = content;

        using var response = await http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new GroqRateLimitException($"Groq limitó temporalmente la tasa (429): {body[..Math.Min(body.Length, 180)]}");
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Groq devolvió {(int)response.StatusCode}: {body[..Math.Min(body.Length, 240)]}");
        using var json = JsonDocument.Parse(body);
        var text = json.RootElement.GetProperty("text").GetString()?.Trim() ?? string.Empty;
        var segments = json.RootElement.TryGetProperty("segments", out var segmentsElement) && segmentsElement.ValueKind == JsonValueKind.Array
            ? segmentsElement.EnumerateArray()
                .Where(item => !filterNoSpeech ||
                               !item.TryGetProperty("no_speech_prob", out var noSpeech) ||
                               noSpeech.GetDouble() < 0.75)
                .Select((item, index) => new MeetingGroqSegment(
                    index.ToString(), "Persona 1",
                    (long)Math.Round(item.TryGetProperty("start", out var start) ? start.GetDouble() * 1_000 : 0),
                    (long)Math.Round(item.TryGetProperty("end", out var end) ? end.GetDouble() * 1_000 : 0),
                    item.TryGetProperty("text", out var segmentText) ? segmentText.GetString()?.Trim() ?? string.Empty : string.Empty))
                .Where(item => !string.IsNullOrWhiteSpace(item.Text)).ToArray()
            : [];
        var resolvedText = filterNoSpeech && segments.Length > 0
            ? string.Join(" ", segments.Select(segment => segment.Text)).Trim()
            : filterNoSpeech
                ? string.Empty
                : text;
        return new MeetingGroqTranscription(resolvedText, segments);
    }

    private static string ContentType(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".mp3" => "audio/mpeg",
        ".m4a" => "audio/mp4",
        ".ogg" => "audio/ogg",
        ".flac" => "audio/flac",
        ".webm" => "audio/webm",
        _ => "audio/wav"
    };
}
