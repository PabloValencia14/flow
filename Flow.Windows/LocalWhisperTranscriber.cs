using System.IO;
using Whisper.net;
using Whisper.net.Ggml;

namespace Flow.Windows;

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
        try
        {
            var processor = factory.CreateBuilder().WithLanguage("es").Build();
            return new LocalWhisperSession(factory, processor, NormalizeModel(model));
        }
        catch
        {
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
    private readonly WhisperProcessor _processor;
    private readonly SemaphoreSlim _processing = new(1, 1);

    internal LocalWhisperSession(WhisperFactory factory, WhisperProcessor processor, string model)
    {
        _factory = factory;
        _processor = processor;
        Model = model;
    }

    public string Model { get; }

    public async Task<string> TranscribeAsync(byte[] wav, CancellationToken cancellationToken, Action<string>? onPartial = null)
    {
        if (wav.Length <= 44) return string.Empty;
        await _processing.WaitAsync(cancellationToken);
        try
        {
            using var stream = new MemoryStream(wav, writable: false);
            var segments = new List<string>();
            await foreach (var result in _processor.ProcessAsync(stream).WithCancellation(cancellationToken))
            {
                if (!string.IsNullOrWhiteSpace(result.Text))
                {
                    segments.Add(result.Text.Trim());
                    onPartial?.Invoke(string.Join(" ", segments).Trim());
                }
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
        _processor.Dispose();
        _factory.Dispose();
        _processing.Dispose();
    }
}
