using System.Net.Http.Headers;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace Flow.Windows;

public sealed record MeetingAnalysis(
    string Title,
    IReadOnlyList<string> Participants,
    string? Summary,
    IReadOnlyList<string> Agreements,
    IReadOnlyList<string> Tasks);

public sealed class GroqMeetingAnalyzer(HttpClient http)
{
    private const string Model = "openai/gpt-oss-20b";
    private const int ContextWindowTokens = 131_072;
    private const int SummaryMaxCompletionTokens = 2_048;
    private const int CorrectionMaxCompletionTokens = 65_536;
    private const string MeetingCorrectionPrompt = "Corrige la transcripción completa de una reunión en español de España. " +
        "Devuelve únicamente un objeto JSON con la propiedad segments. Debe existir exactamente un elemento por cada segmento de entrada " +
        "y debes conservar cada id exactamente. Solo puedes cambiar text; no cambies id, speaker, startMs ni endMs, y no unas ni elimines segmentos. " +
        "Corrige errores fonéticos cuando el contexto sea inequívoco, elimina muletillas, repeticiones de tartamudeo y palabras abandonadas, " +
        "resuelve autocorrecciones conservando la intención y mejora puntuación y mayúsculas. No inventes información, no resumas, no añadas comentarios " +
        "y no uses puntos suspensivos para pausas o dudas. Si una corrección no es segura, conserva el texto original. " +
        "Formato obligatorio: {\"segments\":[{\"id\":\"id-original\",\"text\":\"texto corregido\"}]}";

    public async Task<MeetingAnalysis?> AnalyzeAsync(string transcript, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(transcript)) return null;
        var apiKey = CredentialStore.Read("Flow/GroqApiKey") ?? Environment.GetEnvironmentVariable("FLOW_GROQ_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey)) return null;

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.groq.com/openai/v1/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.UserAgent.ParseAdd("Flow.Windows/0.1");
        request.Content = JsonContent.Create(new
        {
            model = Model,
            temperature = 0.1,
            max_completion_tokens = SummaryMaxCompletionTokens,
            messages = new[]
            {
                new { role = "system", content = "Analiza una transcripción de una reunión o clase en español. Devuelve únicamente JSON válido con exactamente estos campos: title (string), participants (array de strings con Persona 1, Persona 2 u otros identificadores presentes), summary (string), agreements (array de strings), tasks (array de strings). No inventes nombres ni hechos. Mantén la intención y usa solo la información de la transcripción." },
                // No recortamos por caracteres: el límite real lo determina la
                // ventana de contexto del modelo (131k tokens), no 60.000
                // caracteres. Así no se pierde automáticamente el final de una
                // reunión larga. Si una entrada supera la ventana del modelo,
                // Groq la rechazará y el procesador podrá aplicar chunking en una
                // futura ruta específica para reuniones muy extensas.
                new { role = "user", content = transcript }
            }
        });

        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var content = json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
            if (string.IsNullOrWhiteSpace(content)) return null;
            var clean = content.Trim().Trim('`');
            if (clean.StartsWith("json", StringComparison.OrdinalIgnoreCase)) clean = clean[4..].Trim();
            using var data = JsonDocument.Parse(clean);
            var root = data.RootElement;
            return new MeetingAnalysis(
                root.TryGetProperty("title", out var title) ? title.GetString() ?? "Reunión" : "Reunión",
                ReadStrings(root, "participants"),
                root.TryGetProperty("summary", out var summary) ? summary.GetString() : null,
                ReadStrings(root, "agreements"), ReadStrings(root, "tasks"));
        }
        catch (HttpRequestException) { return null; }
        catch (TaskCanceledException) { return null; }
        catch (JsonException) { return null; }
    }

    public async Task<IReadOnlyDictionary<string, string>?> CorrectSegmentsAsync(
        IReadOnlyList<MeetingTranscriptSegmentItem> segments,
        CancellationToken cancellationToken)
    {
        if (segments.Count == 0) return new Dictionary<string, string>(StringComparer.Ordinal);
        var apiKey = CredentialStore.Read("Flow/GroqApiKey") ?? Environment.GetEnvironmentVariable("FLOW_GROQ_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey)) return null;

        var source = JsonSerializer.Serialize(segments.Select(item => new
        {
            id = item.Id,
            speaker = item.Speaker,
            startMs = item.StartMs,
            endMs = item.EndMs,
            text = item.Text
        }));
        var estimatedInputTokens = Math.Max(1, (source.Length + 2) / 3);
        var availableCompletionTokens = Math.Max(4_096, ContextWindowTokens - estimatedInputTokens - 1_024);
        var maxCompletionTokens = Math.Min(CorrectionMaxCompletionTokens, availableCompletionTokens);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.groq.com/openai/v1/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.UserAgent.ParseAdd("Flow.Windows/0.1");
        request.Content = JsonContent.Create(new
        {
            model = Model,
            temperature = 0.0,
            max_completion_tokens = maxCompletionTokens,
            response_format = new { type = "json_object" },
            messages = new[]
            {
                new { role = "system", content = MeetingCorrectionPrompt },
                new { role = "user", content = source }
            }
        });

        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var content = json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
            if (string.IsNullOrWhiteSpace(content)) return null;
            using var corrected = JsonDocument.Parse(CleanJson(content));
            if (!corrected.RootElement.TryGetProperty("segments", out var correctedSegments) ||
                correctedSegments.ValueKind != JsonValueKind.Array || correctedSegments.GetArrayLength() != segments.Count)
                return null;

            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var item in correctedSegments.EnumerateArray())
            {
                if (!item.TryGetProperty("id", out var idProperty) || !item.TryGetProperty("text", out var textProperty)) return null;
                var id = idProperty.GetString();
                var text = textProperty.GetString()?.Trim();
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(text) || !result.TryAdd(id, text)) return null;
            }

            return result.Count == segments.Count && segments.All(item => result.ContainsKey(item.Id)) ? result : null;
        }
        catch (HttpRequestException) { return null; }
        catch (TaskCanceledException) { return null; }
        catch (JsonException) { return null; }
    }

    private static string[] ReadStrings(JsonElement root, string name) =>
        root.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Select(item => item.GetString()).Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => item!).ToArray()
            : [];

    private static string CleanJson(string content)
    {
        var clean = content.Trim();
        if (clean.StartsWith("```", StringComparison.Ordinal)) clean = clean[3..].TrimStart();
        if (clean.StartsWith("json", StringComparison.OrdinalIgnoreCase)) clean = clean[4..].TrimStart();
        if (clean.EndsWith("```", StringComparison.Ordinal)) clean = clean[..^3].TrimEnd();
        return clean;
    }
}
