using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace Flow.Windows;

public sealed class LocalOllamaCorrector(HttpClient? http = null) : IDisposable
{
    public const string DefaultModel = "qwen3:4b-instruct";
    private static readonly Uri OllamaBaseUri = new("http://127.0.0.1:11434/");
    private readonly HttpClient _http = http ?? new HttpClient(new SocketsHttpHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromMinutes(5)
    };
    private readonly bool _ownsClient = http is null;

    public static bool IsExecutableInstalled()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var defaultExe = Path.Combine(localAppData, "Programs", "Ollama", "ollama.exe");
        if (File.Exists(defaultExe)) return true;

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnv)) return false;
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (File.Exists(Path.Combine(dir.Trim(), "ollama.exe"))) return true;
            }
            catch { }
        }
        return false;
    }

    public async Task<IReadOnlyList<string>> GetInstalledModelsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendLoopbackAsync(HttpMethod.Get, "api/tags", null, cancellationToken);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (!json.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
            return [];
        return models.EnumerateArray()
            .Select(item => item.TryGetProperty("name", out var name) ? name.GetString() : null)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .ToArray();
    }

    public async Task PullModelAsync(string model, IProgress<(long Completed, long Total)>? progress = null, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeModel(model);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(OllamaBaseUri, "api/pull"))
        {
            Content = JsonContent.Create(new { name = normalized, stream = true })
        };
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException error)
        {
            throw new InvalidOperationException(BuildUnavailableMessage(), error);
        }
        using (response)
        {
            await EnsureSuccessAsync(response, cancellationToken);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;
                if (root.TryGetProperty("error", out var error) && !string.IsNullOrWhiteSpace(error.GetString()))
                    throw new InvalidOperationException($"Ollama: {error.GetString()}");
                var completed = ReadInt64(root, "completed");
                var total = ReadInt64(root, "total");
                if (total > 0) progress?.Report((completed, total));
            }
        }
    }

    public async Task<string?> TryCorrectAsync(string transcript, DictationCorrectionContext? context, string model, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(transcript)) return transcript;
        var normalizedModel = NormalizeModel(model);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(OllamaBaseUri, "api/chat"))
        {
            Content = JsonContent.Create(new
            {
                model = normalizedModel,
                stream = false,
                think = false,
                messages = new[]
                {
                    new { role = "system", content = DictationCorrectionPrompt.BuildSystemPrompt(context) },
                    new { role = "user", content = DictationCorrectionPrompt.BuildUserPrompt(transcript) }
                },
                options = new { temperature = 0.0 }
            })
        };

        using var response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var content = json.RootElement.TryGetProperty("message", out var message) &&
                      message.TryGetProperty("content", out var value)
            ? value.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(content)) return null;
        var correctionContext = context ?? new DictationCorrectionContext(null, [], new DictationCorrectionOptions());
        return DictationTextProcessor.TryAcceptModelCorrection(transcript, content, correctionContext.Options);
    }

    private async Task<HttpResponseMessage> SendLoopbackAsync(HttpMethod method, string path, HttpContent? content, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(OllamaBaseUri, path)) { Content = content };
        try
        {
            var response = await _http.SendAsync(request, cancellationToken);
            try
            {
                await EnsureSuccessAsync(response, cancellationToken);
                return response;
            }
            catch
            {
                response.Dispose();
                throw;
            }
        }
        catch (HttpRequestException error)
        {
            throw new InvalidOperationException(BuildUnavailableMessage(), error);
        }
    }

    private static string BuildUnavailableMessage() => IsExecutableInstalled()
        ? "Ollama está instalado, pero el servidor local (127.0.0.1:11434) no está activo. Arráncalo o ejecuta 'ollama serve'."
        : "Ollama no está disponible en este equipo. Instálalo y arráncalo para usar la corrección local.";

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException($"Ollama devolvió {(int)response.StatusCode}: {body[..Math.Min(body.Length, 240)]}");
    }

    private static long ReadInt64(JsonElement value, string property) =>
        value.TryGetProperty(property, out var element) && element.TryGetInt64(out var result) ? result : 0;

    private static string NormalizeModel(string? model)
    {
        var normalized = model?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > 120 ||
            normalized.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is ':' or '-' or '_' or '.' or '/')))
            throw new ArgumentException("Nombre de modelo Ollama no válido.", nameof(model));
        return normalized;
    }

    public void Dispose()
    {
        if (_ownsClient) _http.Dispose();
    }
}
