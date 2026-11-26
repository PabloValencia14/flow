using System.IO;
using Whisper.net;
using Whisper.net.Ggml;

namespace Flow.Windows;

public sealed record LocalWhisperSegment(string Text, TimeSpan Start, TimeSpan End);

public static class LocalWhisperTranscriber
{
    public const string DefaultModel = "small";

    private static readonly string ModelDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Flow", "Models");

    public static string GetModelPath(string model) => Path.Combine(ModelDirectory, $"ggml-{NormalizeModel(model)}.bin");

    public static bool IsModelInstalled(string model) => File.Exists(GetModelPath(model));

    public static async Task DownloadModelAsync(string model, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeModel(model);
        var type = normalized switch
        {
            "tiny" => GgmlType.Tiny,
            "base" => GgmlType.Base,
            "small" => GgmlType.Small,
            _ => throw new ArgumentOutOfRangeException(nameof(model), "Modelo Whisper no compatible.")
        };

        Directory.CreateDirectory(ModelDirectory);
        var destination = GetModelPath(normalized);
        var temporary = destination + ".download";
        try
        {
            using var modelStream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(type);
            await using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true))
                await modelStream.CopyToAsync(file, 128 * 1024, cancellationToken);
            File.Move(temporary, destination, overwrite: true);
        }
        catch
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            throw;
        }
    }

    public static LocalWhisperSession OpenSession(string model)
    {
        var path = GetModelPath(model);
        if (!File.Exists(path))
            throw new InvalidOperationException($"Falta el modelo Whisper «{NormalizeModel(model)}». Descárgalo en Ajustes > Modelos locales.");

        var factory = WhisperFactory.FromPath(path);
        WhisperProcessor? previewProcessor = null;
        WhisperProcessor? finalProcessor = null;
        try
        {
            var threads = Math.Clamp(Environment.ProcessorCount / 2, 4, 8);
            previewProcessor = factory.CreateBuilder()
                .WithLanguage("es")
                .WithThreads(threads)
                .WithNoContext()
                .WithGreedySamplingStrategy()
                .WithAudioContextSize(256)
                .WithNoSpeechThreshold(0.6f)
                .WithLogProbThreshold(-1.0f)
                .WithProbabilities()
                .Build();
            finalProcessor = factory.CreateBuilder()
                .WithLanguage("es")
                .WithThreads(threads)
                .WithNoSpeechThreshold(0.6f)
                .WithLogProbThreshold(-1.0f)
                .WithProbabilities()
                .Build();
            return new LocalWhisperSession(factory, previewProcessor, finalProcessor, NormalizeModel(model));
        }
        catch
        {
            previewProcessor?.Dispose();
            finalProcessor?.Dispose();
            factory.Dispose();
            throw;
        }
    }

    private static string NormalizeModel(string model)
    {
        var normalized = model?.Trim().ToLowerInvariant();
        return normalized is "tiny" or "base" or "small"
            ? normalized
            : throw new ArgumentOutOfRangeException(nameof(model), "Elige tiny, base o small.");
    }
}

public sealed class LocalWhisperSession : IDisposable
{
    private readonly WhisperFactory _factory;
    private readonly WhisperProcessor _previewProcessor;
    private readonly WhisperProcessor _finalProcessor;
    private readonly SemaphoreSlim _processing = new(1, 1);

    internal LocalWhisperSession(
        WhisperFactory factory,
        WhisperProcessor previewProcessor,
        WhisperProcessor finalProcessor,
        string model)
    {
        _factory = factory;
        _previewProcessor = previewProcessor;
        _finalProcessor = finalProcessor;
        Model = model;
    }

    public string Model { get; }

    public async Task<IReadOnlyList<LocalWhisperSegment>> TranscribePreviewAsync(byte[] wav, CancellationToken cancellationToken)
    {
        if (wav.Length <= 44) return [];
        await _processing.WaitAsync(cancellationToken);
        try
        {
            using var stream = new MemoryStream(wav, writable: false);
            var segments = new List<LocalWhisperSegment>();
            await foreach (var result in _previewProcessor.ProcessAsync(stream).WithCancellation(cancellationToken))
            {
                var text = DictationTextProcessor.CleanAsrSegmentText(
                    result.Text,
                    result.NoSpeechProbability,
                    tokenProbability: result.Probability > 0f ? result.Probability : null);
                if (string.IsNullOrWhiteSpace(text)) continue;

                if (DictationTextProcessor.NeedsAudioEnergyConfirmation(text, result.NoSpeechProbability) &&
                    AudioCapture.TryHasSpeechEnergyInRange(wav, result.Start, result.End, out var hasSpeechInSegment) &&
                    !hasSpeechInSegment)
                {
                    continue;
                }

                segments.Add(new LocalWhisperSegment(text, result.Start, result.End));
            }
            return segments;
        }
        finally
        {
            _processing.Release();
        }
    }

    public async Task<string> TranscribeFinalAsync(byte[] wav, CancellationToken cancellationToken)
    {
        if (wav.Length <= 44) return string.Empty;
        await _processing.WaitAsync(cancellationToken);
        try
        {
            using var stream = new MemoryStream(wav, writable: false);
            var segments = new List<string>();
            await foreach (var result in _finalProcessor.ProcessAsync(stream).WithCancellation(cancellationToken))
            {
                var text = DictationTextProcessor.CleanAsrSegmentText(
                    result.Text,
                    result.NoSpeechProbability,
                    tokenProbability: result.Probability > 0f ? result.Probability : null);
                if (string.IsNullOrWhiteSpace(text)) continue;

                if (DictationTextProcessor.NeedsAudioEnergyConfirmation(text, result.NoSpeechProbability) &&
                    AudioCapture.TryHasSpeechEnergyInRange(wav, result.Start, result.End, out var hasSpeechInSegment) &&
                    !hasSpeechInSegment)
                {
                    continue;
                }

                segments.Add(text);
            }
            return string.Join(" ", segments).Trim();
        }
        finally
        {
            _processing.Release();
        }
    }

    public void Dispose()
    {
        _previewProcessor.Dispose();
        _finalProcessor.Dispose();
        _factory.Dispose();
        _processing.Dispose();
    }
}
