using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Flow.Windows;

public sealed class GroqContextCorrector(HttpClient http)
{
    public const string Model = "openai/gpt-oss-20b";

    public async Task<string?> TryCorrectAsync(string transcript, DictationCorrectionContext? context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(transcript)) return transcript;

        var apiKey = CredentialStore.Read("Flow/GroqApiKey")
            ?? Environment.GetEnvironmentVariable("FLOW_GROQ_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey)) return null;

        var correctionContext = context ?? new DictationCorrectionContext(null, [], new DictationCorrectionOptions());

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.groq.com/openai/v1/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.UserAgent.ParseAdd("Flow.Windows/0.1");
        request.Content = JsonContent.Create(new
        {
            model = Model,
            temperature = 0.0,
            reasoning_effort = "low",
            reasoning_format = "hidden",
            max_completion_tokens = 2048,
            messages = new[]
            {
                new
                {
                    role = "system",
                    content = DictationCorrectionPrompt.BuildSystemPrompt(correctionContext)
                },
                new
                {
                    role = "user",
                    content = DictationCorrectionPrompt.BuildUserPrompt(transcript)
                }
            }
        });

        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var content = json.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();
            return string.IsNullOrWhiteSpace(content)
                ? null
                : DictationTextProcessor.TryAcceptModelCorrection(transcript, content, correctionContext.Options);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
