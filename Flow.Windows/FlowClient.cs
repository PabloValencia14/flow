using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Flow.Windows;

public sealed class FlowClient : IAsyncDisposable
{
    private const string ServerUrlSetting = "flowhub_server_url";
    private const long WordPreviewWindowMilliseconds = 1_200;
    private const long WordPreviewStepMilliseconds = 900;
    private const long WordPreviewOverlapMilliseconds = 1_100;
    private const long WordPreviewPollMilliseconds = 350;
    private const long BlockPreviewWindowMilliseconds = 3_200;
    private const long BlockPreviewStepMilliseconds = 2_200;
    private const long BlockPreviewOverlapMilliseconds = 1_800;
    private const long BlockPreviewPollMilliseconds = 1_200;
    private const long PreviewCorrectionIntervalMilliseconds = 8_000;
    private readonly Action<string> _status;
    private readonly AudioCapture _audio;
    private readonly GroqTranscriber _transcriber = new(new HttpClient());
    private readonly GroqContextCorrector _contextCorrector = new(new HttpClient());
    private readonly LocalOllamaCorrector _localCorrector = new();
    private readonly GroqMeetingAnalyzer _meetingAnalyzer = new(new HttpClient());
    private readonly ClipboardDelivery _delivery = new();
    private readonly LocalOutbox _outbox = new();
    private readonly HttpClient _syncHttp = new();
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly Stopwatch _recordingWatch = new();
    private LocalWhisperSession? _localWhisperSession;
    private Task<LocalWhisperSession>? _localSessionTask;
    private CancellationTokenSource? _localPreviewCancellation;
    private Task? _localPreviewTask;
    private Task? _captureCleanupTask;
    private readonly SemaphoreSlim _localPreviewLock = new(1, 1);
    private DictationCorrectionContext? _activeCorrectionContext;
    private string _activeTranscriptionProvider = "groq";
    private string _activeTranscriptionModel = GroqTranscriber.Model;
    private string _activePreviewMode = "word";
    private string _activeCorrectionProvider = "groq";
    private string _activeCorrectionModel = GroqContextCorrector.Model;
    private long _localProcessedEndBytes;
    private long _localNextEndBytes;
    private long _localLastCorrectionEndBytes;
    private string _localRawTranscript = string.Empty;
    private bool _localPreviewCorrectionUnavailable;
    private bool _isFinishing;
    private nint _pasteTargetWindow;
    private string? _lastTargetAppName;

    public FlowClient(Action<string> status)
    {
        _status = status;
        // Load the selected endpoint before the global shortcut can be used,
        // so a resident/background launch records with the saved microphone.
        var savedDeviceId = _outbox.GetSettingAsync(FlowSettingKeys.AudioInputDevice)
            .GetAwaiter()
            .GetResult();
        _audio = new AudioCapture(savedDeviceId);
    }

    public bool IsRecording { get; private set; }
    public string LocalDatabasePath => _outbox.DatabasePath;
    public string CurrentMicrophoneName => _audio.InputDeviceName;
    public TimeSpan CurrentRecordingDuration => _recordingWatch.Elapsed;
    public LocalOutbox Outbox => _outbox;
    public bool UsesLocalTranscription => _activeTranscriptionProvider == "whisper";
    public string ActiveTranscriptPreviewMode => _activePreviewMode;

    public event Action<string>? TranscriptPreviewChanged;

    public Task<List<MeetingHistoryItem>> GetMeetingsAsync() => _outbox.GetMeetingsAsync();

    public Task<MeetingHistoryItem> ImportMeetingAsync(string path, string? title, CancellationToken cancellationToken = default) =>
        new MeetingProcessor(_outbox, _transcriber, _meetingAnalyzer)
            .ImportAsync(path, title, DeviceId, _status, cancellationToken);

    public void SetPasteTarget(nint windowHandle, string? appName = null)
    {
        _pasteTargetWindow = windowHandle;
        _lastTargetAppName = appName;
    }

    public void ChangeMicrophone(string? deviceId) => _audio.ChangeDevice(deviceId);

    public event Action<float>? AudioLevelChanged
    {
        add => _audio.LevelChanged += value;
        remove => _audio.LevelChanged -= value;
    }

    public async Task StartDictationAsync()
    {
        if (IsRecording || _isFinishing) return;
        if (_captureCleanupTask is { IsCompleted: false }) await _captureCleanupTask;

        _activeTranscriptionProvider = await _outbox.GetSettingAsync(FlowSettingKeys.TranscriptionProvider, "groq") == "whisper"
            ? "whisper"
            : "groq";
        _activeTranscriptionModel = _activeTranscriptionProvider == "whisper"
            ? await _outbox.GetSettingAsync(FlowSettingKeys.TranscriptionModel, LocalWhisperTranscriber.DefaultModel) ?? LocalWhisperTranscriber.DefaultModel
            : GroqTranscriber.Model;
        _activePreviewMode = string.Equals(
            await _outbox.GetSettingAsync(FlowSettingKeys.TranscriptPreviewMode, "word"),
            "block",
            StringComparison.OrdinalIgnoreCase)
            ? "block"
            : "word";
        _activeCorrectionProvider = await _outbox.GetSettingAsync(FlowSettingKeys.CorrectionProvider, "groq") == "ollama"
            ? "ollama"
            : "groq";
        _activeCorrectionModel = _activeCorrectionProvider == "ollama"
            ? await _outbox.GetSettingAsync(FlowSettingKeys.OllamaModel, LocalOllamaCorrector.DefaultModel) ?? LocalOllamaCorrector.DefaultModel
            : GroqContextCorrector.Model;
        _activeCorrectionContext = await BuildCorrectionContextAsync();

        if (_activeTranscriptionProvider == "whisper" && !LocalWhisperTranscriber.IsModelInstalled(_activeTranscriptionModel))
            throw new InvalidOperationException($"Falta el modelo Whisper «{_activeTranscriptionModel}». Descárgalo en Ajustes > Modelos locales.");

        _localSessionTask = _activeTranscriptionProvider == "whisper"
            ? Task.Run(() => LocalWhisperTranscriber.OpenSession(_activeTranscriptionModel))
            : null;

        _recordingWatch.Restart();
        try
        {
            _audio.Start();
            IsRecording = true;
            SoundManager.PlayStart();
            _status(_activeTranscriptionProvider == "whisper"
                ? $"Escuchando con «{_audio.InputDeviceName}» · Whisper local ({(_activePreviewMode == "block" ? "en bloque" : "palabra por palabra")})."
                : $"Escuchando con «{_audio.InputDeviceName}»…");
            if (_activeTranscriptionProvider == "whisper")
            {
                _localProcessedEndBytes = 0;
                _localNextEndBytes = 0;
                _localLastCorrectionEndBytes = 0;
                _localRawTranscript = string.Empty;
                _localPreviewCorrectionUnavailable = false;
                _localPreviewCancellation = new CancellationTokenSource();
                _localPreviewTask = RunLocalPreviewAsync(_localPreviewCancellation.Token);
            }
        }
        catch
        {
            await CleanupLocalSessionAsync();
            _recordingWatch.Reset();
            throw;
        }
    }

    public async Task<string?> FinishDictationAsync()
    {
        if (!IsRecording) return null;
        IsRecording = false;
        _isFinishing = true;
        _recordingWatch.Stop();
        SoundManager.PlayStop();

        try
        {
            var durationSeconds = _recordingWatch.Elapsed.TotalSeconds;
            string text;
            if (_activeTranscriptionProvider == "whisper")
            {
                _localPreviewCancellation?.Cancel();
                var snapshot = await _audio.StopAsSnapshotAsync(0, 0);
                if (_localPreviewTask is not null)
                {
                    try { await _localPreviewTask; }
                    catch (OperationCanceledException) { }
                }
                await ProcessLocalChunksAsync(snapshot, flushRemaining: true, CancellationToken.None);
                text = _localRawTranscript.Trim();
                if (snapshot.TotalLengthBytes < 1_100)
                {
                    _status("No se ha capturado audio suficiente.");
                    return null;
                }
                _activeTranscriptionModel = $"whisper.net/{_localWhisperSession?.Model ?? _activeTranscriptionModel}";
            }
            else
            {
                var wav = await _audio.StopAsWaveAsync();
                if (wav.Length < 1_100)
                {
                    _status("No se ha capturado audio suficiente.");
                    return null;
                }

                _status($"Audio capturado ({wav.Length / 1024d:0.0} KB). Transcribiendo con Groq…");
                text = await _transcriber.TranscribeAsync(wav, CancellationToken.None);
            }
            if (string.IsNullOrWhiteSpace(text))
            {
                _status(_activeTranscriptionProvider == "whisper" ? "Whisper local no devolvió texto." : "Groq no devolvió texto.");
                return null;
            }

            _status(_activeCorrectionProvider == "ollama"
                ? "Corrigiendo en el equipo con Ollama…"
                : "Entendiendo el contexto y corrigiendo el dictado…");
            var correctionOptions = _activeCorrectionContext?.Options ?? await _outbox.GetCorrectionOptionsAsync();
            var correctionInput = DictationTextProcessor.PrepareForCorrection(text, correctionOptions);
            string? correctedText = null;
            if (_activeCorrectionProvider == "ollama")
            {
                try
                {
                    correctedText = await _localCorrector.TryCorrectAsync(
                        correctionInput, _activeCorrectionContext, _activeCorrectionModel, CancellationToken.None);
                    if (correctedText is not null) _activeCorrectionModel = $"ollama:{_activeCorrectionModel}";
                }
                catch (Exception error) when (error is HttpRequestException or InvalidOperationException or JsonException or TaskCanceledException)
                {
                    _status("Ollama no está disponible; se insertará la transcripción local sin corregir.");
                }
            }
            else
            {
                correctedText = await _contextCorrector.TryCorrectAsync(correctionInput, _activeCorrectionContext, CancellationToken.None);
            }

            var finalText = DictationTextProcessor.CleanFinal(correctedText ?? text, correctionOptions);
            finalText = DictationTextProcessor.ExpandSnippets(finalText, await _outbox.GetSnippetsAsync());
            if (string.IsNullOrWhiteSpace(finalText))
            {
                finalText = DictationTextProcessor.CleanFinal(text, correctionOptions);
                finalText = DictationTextProcessor.ExpandSnippets(finalText, await _outbox.GetSnippetsAsync());
            }
            if (string.IsNullOrWhiteSpace(finalText))
            {
                _status("No se obtuvo texto utilizable del dictado.");
                return null;
            }

            await _delivery.PasteAsync(finalText, _pasteTargetWindow);
            SoundManager.PlayPaste();

            var dictationId = Guid.NewGuid().ToString();

            // 1. Save to local SQLite History
            await _outbox.SaveDictationAsync(dictationId, finalText, text, durationSeconds, _lastTargetAppName);

            // 2. Enqueue for FlowHub sync
            await _outbox.EnqueueAsync("dictations", dictationId, "create", new
            {
                dictationId,
                text = finalText,
                rawTranscript = text,
                durationSeconds,
                appName = _lastTargetAppName,
                deviceId = DeviceId,
                createdAt = DateTimeOffset.UtcNow,
                transcriptionModel = _activeTranscriptionModel,
                correctionModel = correctedText is null ? null : _activeCorrectionModel
            });

            _status("Texto insertado y guardado en la memoria local.");
            await SyncPendingAsync();
            return finalText;
        }
        finally
        {
            await CleanupLocalSessionAsync();
            _isFinishing = false;
        }
    }

    public void CancelDictation()
    {
        if (!IsRecording) return;
        IsRecording = false;
        _recordingWatch.Reset();
        _localPreviewCancellation?.Cancel();
        _captureCleanupTask = CleanupCancelledCaptureAsync(_activeTranscriptionProvider == "whisper");
        SoundManager.PlayStop();
        _status("Dictado cancelado.");
    }

    private async Task<DictationCorrectionContext> BuildCorrectionContextAsync()
    {
        var options = await _outbox.GetCorrectionOptionsAsync();
        var styles = await _outbox.GetStyleSettingsAsync();
        var dictionary = await _outbox.GetDictionaryEntriesAsync();
        return new DictationCorrectionContext(_lastTargetAppName, dictionary, options, styles);
    }

    private async Task<LocalWhisperSession?> EnsureLocalSessionAsync(CancellationToken cancellationToken)
    {
        if (_localWhisperSession is not null) return _localWhisperSession;
        if (_localSessionTask is null) return null;
        _localWhisperSession = await _localSessionTask.WaitAsync(cancellationToken);
        return _localWhisperSession;
    }

    private async Task RunLocalPreviewAsync(CancellationToken cancellationToken)
    {
        try
        {
            await EnsureLocalSessionAsync(cancellationToken);
            var pollMs = _activePreviewMode == "block" ? BlockPreviewPollMilliseconds : WordPreviewPollMilliseconds;
            while (true)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(pollMs), cancellationToken);
                await ProcessLocalChunksAsync(_audio.GetSnapshot(0, 0), flushRemaining: false, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task ProcessLocalChunksAsync(AudioCaptureSnapshot snapshot, bool flushRemaining, CancellationToken cancellationToken)
    {
        var session = await EnsureLocalSessionAsync(cancellationToken);
        if (session is null || snapshot.TotalLengthBytes == 0) return;
        await _localPreviewLock.WaitAsync(cancellationToken);
        try
        {
            var isWordMode = _activePreviewMode != "block";
            var windowMs = isWordMode ? WordPreviewWindowMilliseconds : BlockPreviewWindowMilliseconds;
            var stepMs = isWordMode ? WordPreviewStepMilliseconds : BlockPreviewStepMilliseconds;
            var overlapMs = isWordMode ? WordPreviewOverlapMilliseconds : BlockPreviewOverlapMilliseconds;
            var bytesPerSecond = Math.Max(snapshot.Format.AverageBytesPerSecond,
                snapshot.Format.SampleRate * Math.Max(1, snapshot.Format.BlockAlign));
            var blockAlign = Math.Max(1, snapshot.Format.BlockAlign);
            long ToAlignedBytes(long milliseconds)
            {
                var bytes = (long)Math.Round(bytesPerSecond * milliseconds / 1_000d);
                return Math.Max(blockAlign, bytes - bytes % blockAlign);
            }

            var firstWindowBytes = ToAlignedBytes(windowMs);
            var stepBytes = ToAlignedBytes(stepMs);
            var overlapBytes = ToAlignedBytes(overlapMs);
            if (_localNextEndBytes == 0) _localNextEndBytes = firstWindowBytes;

            while (_localProcessedEndBytes < snapshot.TotalLengthBytes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var totalBytes = snapshot.TotalLengthBytes - snapshot.TotalLengthBytes % blockAlign;
                var hasFullWindow = totalBytes >= _localNextEndBytes;
                if (!hasFullWindow && !flushRemaining) break;

                var endByte = hasFullWindow ? _localNextEndBytes : totalBytes;
                if (endByte <= _localProcessedEndBytes) break;
                var startByte = Math.Max(0, _localProcessedEndBytes - overlapBytes);
                startByte -= startByte % blockAlign;
                var window = _audio.GetSnapshot(startByte, endByte);
                var wav = AudioCapture.ToWhisperWave(window, 0, window.Raw.Length);
                Action<string>? onPartial = isWordMode && !flushRemaining
                    ? partial =>
                    {
                        var live = MergeTranscriptChunks(_localRawTranscript, partial);
                        if (!string.IsNullOrWhiteSpace(live))
                            TranscriptPreviewChanged?.Invoke(live);
                    }
                    : null;
                var chunkText = await session.TranscribeAsync(wav, cancellationToken, onPartial);
                _localProcessedEndBytes = endByte;
                _localNextEndBytes = endByte + stepBytes;
                if (!string.IsNullOrWhiteSpace(chunkText))
                {
                    _localRawTranscript = MergeTranscriptChunks(_localRawTranscript, chunkText);
                    var preview = _localRawTranscript;
                    var correctionIntervalBytes = (long)Math.Round(bytesPerSecond * PreviewCorrectionIntervalMilliseconds / 1_000d);
                    var shouldCorrectPreview = _localLastCorrectionEndBytes == 0 ||
                                               endByte - _localLastCorrectionEndBytes >= correctionIntervalBytes;
                    if (_activeCorrectionProvider == "ollama" && !_localPreviewCorrectionUnavailable && shouldCorrectPreview)
                    {
                        var options = _activeCorrectionContext?.Options ?? new DictationCorrectionOptions();
                        var correctionInput = DictationTextProcessor.PrepareForCorrection(preview, options);
                        try
                        {
                            var corrected = await _localCorrector.TryCorrectAsync(
                                correctionInput, _activeCorrectionContext, _activeCorrectionModel, cancellationToken);
                            if (!string.IsNullOrWhiteSpace(corrected)) preview = corrected;
                            _localLastCorrectionEndBytes = endByte;
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                        catch (Exception error) when (error is HttpRequestException or InvalidOperationException or JsonException or TaskCanceledException)
                        {
                            _localPreviewCorrectionUnavailable = true;
                            _localLastCorrectionEndBytes = endByte;
                            _status("Vista previa con Whisper local; Ollama no está disponible para corregirla.");
                        }
                    }
                    TranscriptPreviewChanged?.Invoke(preview);
                    _status("Transcripción local en curso…");
                }

                if (!hasFullWindow) break;
            }
        }
        finally
        {
            _localPreviewLock.Release();
        }
    }

    private static string MergeTranscriptChunks(string existing, string incoming)
    {
        var currentWords = existing.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var newWords = incoming.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (currentWords.Length == 0) return string.Join(" ", newWords);
        if (newWords.Length == 0) return existing;

        var maxOverlap = Math.Min(24, Math.Min(currentWords.Length, newWords.Length));
        var overlap = 0;
        for (var count = maxOverlap; count >= 1; count--)
        {
            var matches = 0;
            var comparable = 0;
            for (var index = 0; index < count; index++)
            {
                var left = NormalizeTranscriptWord(currentWords[currentWords.Length - count + index]);
                var right = NormalizeTranscriptWord(newWords[index]);
                if (left.Length == 0 || right.Length == 0) continue;
                comparable++;
                if (left == right) matches++;
            }
            var required = count == 1 ? 1 : Math.Max(2, (int)Math.Ceiling(count * 0.7));
            if (comparable > 0 && matches >= required && (count > 1 || NormalizeTranscriptWord(newWords[0]).Length >= 5))
            {
                overlap = count;
                break;
            }
        }

        var remainder = newWords.Skip(overlap);
        var suffix = string.Join(" ", remainder);
        return string.IsNullOrWhiteSpace(suffix) ? existing : $"{existing.TrimEnd()} {suffix}";
    }

    private static string NormalizeTranscriptWord(string word) =>
        new(word.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private async Task CleanupCancelledCaptureAsync(bool localTranscription)
    {
        try
        {
            if (localTranscription)
                await _audio.StopAsSnapshotAsync(0, 0);
            else
                await _audio.StopAsWaveAsync();
            if (_localPreviewTask is not null)
            {
                try { await _localPreviewTask; }
                catch (OperationCanceledException) { }
            }
        }
        finally
        {
            await CleanupLocalSessionAsync();
        }
    }

    private async Task CleanupLocalSessionAsync()
    {
        _localPreviewCancellation?.Cancel();
        _localPreviewCancellation?.Dispose();
        _localPreviewCancellation = null;
        _localPreviewTask = null;
        if (_localSessionTask is not null)
        {
            try
            {
                _localWhisperSession ??= await _localSessionTask;
            }
            catch { }
            _localSessionTask = null;
        }
        _localWhisperSession?.Dispose();
        _localWhisperSession = null;
    }

    public async Task<SyncSummary> SyncPendingAsync()
    {
        if (!await _syncLock.WaitAsync(0)) return SyncSummary.NotRun;
        try
        {
            var server = await GetSyncServerUrlAsync();
            if (string.IsNullOrWhiteSpace(server)) return SyncSummary.NotConfigured;
            if (!Uri.TryCreate(server, UriKind.Absolute, out var serverUri) ||
                serverUri.Scheme is not ("http" or "https"))
            {
                _status("URL de FlowHub no válida.");
                return new SyncSummary(0, 0, "URL no válida");
            }

            await _outbox.EnsureSyncSnapshotAsync();
            var token = FlowHubToken;
            if (!await RegisterDeviceAsync(server, token))
            {
                _status("FlowHub no disponible o token inválido.");
                return new SyncSummary(0, 0, "Registro rechazado");
            }

            var uploaded = 0;
            var pending = await _outbox.PendingAsync();
            var pendingMeetingIds = pending.Where(item => item.Entity.Equals("meetings", StringComparison.OrdinalIgnoreCase))
                .Select(item => item.EntityId).ToHashSet(StringComparer.Ordinal);
            var acknowledged = new HashSet<string>(StringComparer.Ordinal);
            if (pending.Count > 0)
            {
                var operations = pending.Select(item => new
                {
                    eventId = item.EventId,
                    entity = item.Entity,
                    entityId = item.EntityId,
                    operation = item.Operation,
                    payload = JsonDocument.Parse(item.PayloadJson).RootElement.Clone()
                }).ToArray();
                using var request = new HttpRequestMessage(HttpMethod.Post, $"{server.TrimEnd('/')}/v1/sync/push");
                request.Content = JsonContent.Create(new { deviceId = DeviceId, operations });
                AddAuthorization(request, token);
                using var response = await _syncHttp.SendAsync(request);
                var body = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                {
                    _status($"Sync pendiente ({(int)response.StatusCode}); la cola local se conserva.");
                    return new SyncSummary(0, 0, $"HTTP {(int)response.StatusCode}");
                }

                acknowledged = ReadAcknowledgedEventIds(body);
                if (acknowledged.Count == 0)
                {
                    _status("FlowHub no confirmó las operaciones; la cola se conserva.");
                    return new SyncSummary(0, 0, "Confirmación vacía");
                }
                await _outbox.RemoveAsync(acknowledged);
                uploaded = acknowledged.Count;
            }

            // Primero se publica el registro y después el binario: el endpoint
            // de audio exige que exista la reunión en FlowHub. La marca local
            // solo pasa a sincronizada cuando ambos recursos están confirmados.
            foreach (var meeting in (await _outbox.GetMeetingsWithAudioAsync(50))
                .Where(item => pendingMeetingIds.Contains(item.Id) || !item.IsSynced))
            {
                var audio = await UploadMeetingAudioAsync(server, meeting, token);
                if (audio is not null)
                {
                    _status("No se pudo sincronizar el audio de una reunión; se conserva en la cola local.");
                    return new SyncSummary(uploaded, 0, audio);
                }
                await _outbox.MarkMeetingSyncedAsync(meeting.Id);
            }
            foreach (var meetingId in pending.Where(item => item.Entity == "meetings" && acknowledged.Contains(item.EventId)).Select(item => item.EntityId).Distinct())
            {
                var meeting = await _outbox.GetMeetingAsync(meetingId);
                if (meeting is null || string.IsNullOrWhiteSpace(meeting.AudioPath))
                    await _outbox.MarkMeetingSyncedAsync(meetingId);
            }

            var pulled = await PullAsync(server, token);
            if (pulled.Error is not null)
            {
                _status("Envío completado; recepción pendiente.");
                return new SyncSummary(uploaded, pulled.Count, pulled.Error);
            }
            _status(uploaded > 0 || pulled.Count > 0
                ? "Flow sincronizado entre dispositivos."
                : "FlowHub conectado; no hay cambios pendientes.");
            return new SyncSummary(uploaded, pulled.Count);
        }
        catch (HttpRequestException error)
        {
            _status("FlowHub no disponible; el dictado sigue guardado localmente.");
            return new SyncSummary(0, 0, error.Message);
        }
        catch (Exception error)
        {
            _status("No se pudo completar la sincronización.");
            return new SyncSummary(0, 0, error.Message);
        }
        finally { _syncLock.Release(); }
    }

    public async Task<string?> GetSyncServerUrlAsync() =>
        await _outbox.GetSettingAsync(ServerUrlSetting) ?? Environment.GetEnvironmentVariable("FLOW_SERVER");

    public bool HasFlowHubToken => !string.IsNullOrWhiteSpace(FlowHubToken);

    public async Task SaveSyncSettingsAsync(string? serverUrl, string? token)
    {
        var normalized = serverUrl?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            await _outbox.RemoveSettingAsync(ServerUrlSetting);
        else if (!Uri.TryCreate(normalized, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https"))
            throw new ArgumentException("La URL de FlowHub debe ser una URL HTTP o HTTPS válida.", nameof(serverUrl));
        else
            await _outbox.SetSettingAsync(ServerUrlSetting, normalized.TrimEnd('/'));

        var normalizedToken = token?.Trim();
        if (!string.IsNullOrWhiteSpace(normalizedToken) && normalizedToken.All(character => character == '●'))
            return;
        if (!string.IsNullOrWhiteSpace(normalizedToken))
            CredentialStore.Write("Flow/FlowHubAppToken", normalizedToken);
    }

    private async Task<bool> RegisterDeviceAsync(string server, string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{server.TrimEnd('/')}/v1/devices");
        request.Content = JsonContent.Create(new
        {
            deviceId = DeviceId,
            name = "MSI Pablo",
            platform = "windows",
            version = typeof(FlowClient).Assembly.GetName().Version?.ToString() ?? "unknown"
        });
        AddAuthorization(request, token);
        using var response = await _syncHttp.SendAsync(request);
        return response.IsSuccessStatusCode;
    }

    private async Task<(int Count, long ServerSequence, string? Error)> PullAsync(string server, string? token)
    {
        var sequence = await _outbox.GetServerSequenceAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{server.TrimEnd('/')}/v1/sync/pull?after={sequence}&limit=500");
        AddAuthorization(request, token);
        using var response = await _syncHttp.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) return (0, sequence, $"HTTP {(int)response.StatusCode}");

        using var document = JsonDocument.Parse(body);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return (0, sequence, "Respuesta de pull no válida");
        var newest = sequence;
        var applied = 0;
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("serverSeq", out var sequenceProperty) || !sequenceProperty.TryGetInt64(out var eventSequence))
                return (applied, sequence, "Evento sin serverSeq");
            var entity = item.TryGetProperty("entity", out var entityProperty) ? entityProperty.GetString() ?? string.Empty : string.Empty;
            var operation = item.TryGetProperty("operation", out var operationProperty) ? operationProperty.GetString() ?? string.Empty : string.Empty;
            JsonElement payload;
            if (item.TryGetProperty("payload", out var payloadProperty))
            {
                payload = payloadProperty.Clone();
            }
            else
            {
                using var emptyPayload = JsonDocument.Parse("{}");
                payload = emptyPayload.RootElement.Clone();
            }
            await _outbox.ApplyRemoteEventAsync(entity, operation, payload);
            newest = Math.Max(newest, eventSequence);
            applied++;
        }
        if (newest > sequence) await _outbox.SetServerSequenceAsync(newest);
        return (applied, newest, null);
    }

    private static HashSet<string> ReadAcknowledgedEventIds(string body)
    {
        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("acknowledgedEventIds", out var ids) || ids.ValueKind != JsonValueKind.Array)
            return [];
        return ids.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static void AddAuthorization(HttpRequestMessage request, string? token)
    {
        if (!string.IsNullOrWhiteSpace(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private async Task<string?> UploadMeetingAudioAsync(string server, MeetingHistoryItem meeting, string? token)
    {
        if (string.IsNullOrWhiteSpace(meeting.AudioPath) || !File.Exists(meeting.AudioPath)) return null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{server.TrimEnd('/')}/v1/meetings/{Uri.EscapeDataString(meeting.Id)}/audio?filename={Uri.EscapeDataString(meeting.AudioFileName ?? Path.GetFileName(meeting.AudioPath))}");
            var content = new StreamContent(File.OpenRead(meeting.AudioPath));
            content.Headers.ContentType = new MediaTypeHeaderValue(ContentType(Path.GetExtension(meeting.AudioPath)));
            request.Content = content;
            if (!string.IsNullOrWhiteSpace(meeting.AudioSha256)) request.Headers.Add("X-Flow-SHA256", meeting.AudioSha256);
            AddAuthorization(request, token);
            using var response = await _syncHttp.SendAsync(request);
            if (response.IsSuccessStatusCode) return null;
            return $"HTTP {(int)response.StatusCode}";
        }
        catch (Exception error) { return error.Message; }
    }

    private static string ContentType(string extension) => extension.ToLowerInvariant() switch
    {
        ".mp3" => "audio/mpeg", ".m4a" => "audio/mp4", ".ogg" => "audio/ogg", ".flac" => "audio/flac", _ => "audio/wav"
    };

    private string? FlowHubToken =>
        CredentialStore.Read("Flow/FlowHubAppToken") ?? Environment.GetEnvironmentVariable("FLOW_HUB_APP_TOKEN");

    private string DeviceId => Environment.GetEnvironmentVariable("FLOW_DEVICE_ID") ?? "msi-pablo";

    public async ValueTask DisposeAsync()
    {
        _localPreviewCancellation?.Cancel();
        if (_captureCleanupTask is { IsCompleted: false })
        {
            try { await _captureCleanupTask; }
            catch { }
        }
        else if (IsRecording)
        {
            try
            {
                if (_activeTranscriptionProvider == "whisper") await _audio.StopAsSnapshotAsync(0, 0);
                else await _audio.StopAsWaveAsync();
            }
            catch { }
        }
        if (_localPreviewTask is not null)
        {
            try { await _localPreviewTask; }
            catch { }
        }
        await CleanupLocalSessionAsync();
        _audio.Dispose();
        _syncHttp.Dispose();
        _syncLock.Dispose();
        _localPreviewLock.Dispose();
        _localCorrector.Dispose();
    }
}

public sealed record SyncSummary(int Uploaded, int Pulled, string? Error = null)
{
    public static SyncSummary NotConfigured { get; } = new(0, 0);
    public static SyncSummary NotRun { get; } = new(0, 0);
}

internal static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
