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
    private const long WordMinInitialMilliseconds = 450;
    private const long WordMinDeltaMilliseconds = 240;
    private const long CloudWordMinInitialMilliseconds = 700;
    private const long CloudWordMinDeltaMilliseconds = 900;
    private const long WordMaxTailMilliseconds = 4_500;
    private const long WordCommitKeepTailMilliseconds = 1_500;
    private const long WordPollMilliseconds = 120;
    private const long CloudWordPollMilliseconds = 250;
    private const long BlockPreviewWindowMilliseconds = 3_200;
    private const long BlockPreviewStepMilliseconds = 2_200;
    private const long BlockPreviewOverlapMilliseconds = 1_800;
    private const long BlockPreviewPollMilliseconds = 800;
    private const long CloudBlockStepMilliseconds = 2_600;
    private const long CloudBlockOverlapMilliseconds = 1_400;
    private const long CloudBlockPollMilliseconds = 900;
    private const long FinalFullWindowMilliseconds = 28_000;
    private const long FinalTailSliceMilliseconds = 22_000;
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
    private readonly SemaphoreSlim _whisperCacheLock = new(1, 1);
    private readonly Stopwatch _recordingWatch = new();
    private LocalWhisperSession? _cachedWhisperSession;
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
    private long _localTailStartByte;
    private long _localLastWordSampleEndBytes;
    private string _localCommittedText = string.Empty;
    private string _localRawTranscript = string.Empty;
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
            ? GetOrOpenLocalSessionAsync(_activeTranscriptionModel)
            : null;

        _recordingWatch.Restart();
        try
        {
            _audio.Start();
            IsRecording = true;
            SoundManager.PlayStart();
            _status(_activeTranscriptionProvider == "whisper"
                ? $"Escuchando con «{_audio.InputDeviceName}» · Whisper local ({(_activePreviewMode == "block" ? "en bloque" : "palabra por palabra")})."
                : $"Escuchando con «{_audio.InputDeviceName}» · Groq ({(_activePreviewMode == "block" ? "en bloque" : "palabra por palabra")}).");
            _localProcessedEndBytes = 0;
            _localNextEndBytes = 0;
            _localTailStartByte = 0;
            _localLastWordSampleEndBytes = 0;
            _localCommittedText = string.Empty;
            _localRawTranscript = string.Empty;
            _localPreviewCancellation = new CancellationTokenSource();
            _localPreviewTask = RunLocalPreviewAsync(_localPreviewCancellation.Token);
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
                if (snapshot.TotalLengthBytes < 1_100)
                {
                    _status("No se ha capturado audio suficiente.");
                    return null;
                }
                text = await TranscribeFinalLocalAsync(snapshot, CancellationToken.None);
                _activeTranscriptionModel = $"whisper.net/{_localWhisperSession?.Model ?? _activeTranscriptionModel}";
            }
            else
            {
                _localPreviewCancellation?.Cancel();
                var wav = await _audio.StopAsWaveAsync();
                if (_localPreviewTask is not null)
                {
                    try { await _localPreviewTask; }
                    catch (OperationCanceledException) { }
                }
                if (wav.Length < 1_100)
                {
                    LogDiagnostic($"finish provider=groq skipped=short_wav bytes={wav.Length}");
                    _status("No se ha capturado audio suficiente.");
                    return null;
                }
                var previewFallback = DictationTextProcessor.RemoveAsrArtifacts(_localRawTranscript);
                if (!AudioCapture.HasSpeechEnergy(wav) && string.IsNullOrWhiteSpace(previewFallback))
                {
                    LogDiagnostic($"finish provider=groq skipped=no_speech_energy bytes={wav.Length}");
                    _status("No se detectó voz en el audio.");
                    return null;
                }

                _status($"Audio capturado ({wav.Length / 1024d:0.0} KB). Transcribiendo con Groq…");
                text = await TranscribeGroqFinalWithRecoveryAsync(wav, previewFallback);
            }
            if (string.IsNullOrWhiteSpace(text))
            {
                LogDiagnostic($"finish provider={_activeTranscriptionProvider} empty_transcript=true preview_len={_localRawTranscript.Length}");
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
                    await LocalOllamaCorrector.EnsureServerRunningAsync(CancellationToken.None);
                    correctedText = await _localCorrector.TryCorrectAsync(
                        correctionInput, _activeCorrectionContext, _activeCorrectionModel, CancellationToken.None);
                    if (correctedText is not null) _activeCorrectionModel = $"ollama:{_activeCorrectionModel}";
                }
                catch (Exception error) when (error is HttpRequestException or InvalidOperationException or JsonException or TaskCanceledException)
                {
                    _status(error.Message.Contains("404", StringComparison.Ordinal)
                        ? $"Falta el modelo Ollama «{_activeCorrectionModel}» (descárgalo en Ajustes); se insertará la transcripción sin corregir."
                        : "Ollama no está disponible; se insertará la transcripción local sin corregir.");
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
                LogDiagnostic($"finish provider={_activeTranscriptionProvider} empty_final_text=true");
                _status("No se obtuvo texto utilizable del dictado.");
                return null;
            }

            var dictationId = Guid.NewGuid().ToString();

            // 1. Persist locally first so history is never lost if pasting hits transient focus/clipboard lock.
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

            try
            {
                await _delivery.PasteAsync(finalText, _pasteTargetWindow);
                SoundManager.PlayPaste();
                _status("Texto insertado y guardado en la memoria local.");
            }
            catch (Exception pasteError)
            {
                LogDiagnostic($"paste_failed id={dictationId} err={pasteError.GetType().Name}");
                _status($"Guardado en el historial (no se pudo pegar: {pasteError.Message})");
            }

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

    public async Task PrewarmLocalWhisperAsync(string model)
    {
        if (!LocalWhisperTranscriber.IsModelInstalled(model)) return;
        try { await GetOrOpenLocalSessionAsync(model); }
        catch { }
    }

    private async Task<LocalWhisperSession> GetOrOpenLocalSessionAsync(string model)
    {
        await _whisperCacheLock.WaitAsync();
        try
        {
            if (_cachedWhisperSession is not null &&
                string.Equals(_cachedWhisperSession.Model, model, StringComparison.OrdinalIgnoreCase))
            {
                return _cachedWhisperSession;
            }

            _cachedWhisperSession?.Dispose();
            _cachedWhisperSession = null;
            var created = await Task.Run(() => LocalWhisperTranscriber.OpenSession(model));
            _cachedWhisperSession = created;
            return created;
        }
        finally
        {
            _whisperCacheLock.Release();
        }
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
            if (_activeTranscriptionProvider == "whisper")
                await EnsureLocalSessionAsync(cancellationToken);

            var isCloud = _activeTranscriptionProvider != "whisper";
            if (_activePreviewMode == "block")
            {
                var blockPollMs = isCloud ? CloudBlockPollMilliseconds : BlockPreviewPollMilliseconds;
                while (true)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(blockPollMs), cancellationToken);
                    try
                    {
                        await ProcessLocalBlocksAsync(_audio.GetSnapshot(0, 0), cancellationToken);
                    }
                    catch (GroqRateLimitException)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(3_000), cancellationToken);
                    }
                    catch (Exception error) when (isCloud && error is HttpRequestException or InvalidOperationException or JsonException)
                    {
                        if (error.Message.Contains("Falta la credencial", StringComparison.Ordinal)) return;
                        await Task.Delay(TimeSpan.FromMilliseconds(1_500), cancellationToken);
                    }
                }
            }

            var wordPollMs = isCloud ? CloudWordPollMilliseconds : WordPollMilliseconds;
            while (true)
            {
                try
                {
                    await ProcessLocalWordTailAsync(cancellationToken);
                }
                catch (GroqRateLimitException)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(3_000), cancellationToken);
                }
                catch (Exception error) when (isCloud && error is HttpRequestException or InvalidOperationException or JsonException)
                {
                    if (error.Message.Contains("Falta la credencial", StringComparison.Ordinal)) return;
                    await Task.Delay(TimeSpan.FromMilliseconds(1_500), cancellationToken);
                }
                await Task.Delay(TimeSpan.FromMilliseconds(wordPollMs), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task<IReadOnlyList<LocalWhisperSegment>> TranscribePreviewSliceAsync(byte[] wav, CancellationToken cancellationToken)
    {
        if (!AudioCapture.HasSpeechEnergy(wav)) return [];
        if (_activeTranscriptionProvider == "whisper")
        {
            var session = await EnsureLocalSessionAsync(cancellationToken);
            return session is null ? [] : await session.TranscribePreviewAsync(wav, cancellationToken);
        }
        return await _transcriber.TranscribePreviewAsync(wav, cancellationToken);
    }

    private async Task ProcessLocalWordTailAsync(CancellationToken cancellationToken)
    {
        if (_activeTranscriptionProvider == "whisper" && await EnsureLocalSessionAsync(cancellationToken) is null)
            return;
        await _localPreviewLock.WaitAsync(cancellationToken);
        try
        {
            var probe = _audio.GetSnapshot(_localTailStartByte, null);
            if (probe.TotalLengthBytes == 0) return;

            var bytesPerSecond = Math.Max(probe.Format.AverageBytesPerSecond,
                probe.Format.SampleRate * Math.Max(1, probe.Format.BlockAlign));
            var blockAlign = Math.Max(1, probe.Format.BlockAlign);
            long ToAlignedBytes(long milliseconds)
            {
                var bytes = (long)Math.Round(bytesPerSecond * milliseconds / 1_000d);
                return Math.Max(blockAlign, bytes - bytes % blockAlign);
            }

            var isCloud = _activeTranscriptionProvider != "whisper";
            var minInitialBytes = ToAlignedBytes(isCloud ? CloudWordMinInitialMilliseconds : WordMinInitialMilliseconds);
            var minDeltaBytes = ToAlignedBytes(isCloud ? CloudWordMinDeltaMilliseconds : WordMinDeltaMilliseconds);
            var maxTailBytes = ToAlignedBytes(WordMaxTailMilliseconds);
            var keepTailBytes = ToAlignedBytes(WordCommitKeepTailMilliseconds);

            var totalBytes = probe.TotalLengthBytes - probe.TotalLengthBytes % blockAlign;
            var availableTailBytes = totalBytes - _localTailStartByte;
            if (availableTailBytes < minInitialBytes) return;
            if (_localLastWordSampleEndBytes > 0 && totalBytes - _localLastWordSampleEndBytes < minDeltaBytes) return;

            var windowEndByte = Math.Min(totalBytes, _localTailStartByte + maxTailBytes);
            windowEndByte -= windowEndByte % blockAlign;
            if (windowEndByte <= _localTailStartByte) return;

            var windowSlice = _audio.GetSnapshot(_localTailStartByte, windowEndByte);
            var wav = AudioCapture.ToWhisperWave(windowSlice, 0, windowSlice.Raw.Length);
            if (wav.Length <= 44) return;

            var segments = await TranscribePreviewSliceAsync(wav, cancellationToken);
            _localLastWordSampleEndBytes = windowEndByte;

            var tailText = string.Join(" ", segments.Select(segment => segment.Text)).Trim();
            if (!string.IsNullOrWhiteSpace(tailText))
            {
                _localRawTranscript = CombineCommittedAndTail(_localCommittedText, tailText);
                TranscriptPreviewChanged?.Invoke(_localRawTranscript);
                _status(isCloud ? "Transcripción en curso (Groq)…" : "Transcripción local en curso…");
            }

            // Keep the active phrase tail <= ~4.5 s so preview stays responsive and bounded.
            var processedTailBytes = windowEndByte - _localTailStartByte;
            if (processedTailBytes >= maxTailBytes)
            {
                var windowDurationMs = processedTailBytes * 1_000d / bytesPerSecond;
                var commitThresholdMs = Math.Max(1_000d, windowDurationMs - WordCommitKeepTailMilliseconds);
                var commitSegments = segments
                    .Where(segment => segment.End.TotalMilliseconds > 250 && segment.End.TotalMilliseconds <= commitThresholdMs)
                    .ToList();

                if (commitSegments.Count > 0)
                {
                    var committedChunk = string.Join(" ", commitSegments.Select(segment => segment.Text)).Trim();
                    _localCommittedText = CombineCommittedAndTail(_localCommittedText, committedChunk);
                    var commitAdvanceBytes = ToAlignedBytes((long)Math.Round(commitSegments[^1].End.TotalMilliseconds));
                    _localTailStartByte = Math.Min(windowEndByte - keepTailBytes, _localTailStartByte + commitAdvanceBytes);
                    _localTailStartByte -= _localTailStartByte % blockAlign;
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(tailText))
                        _localCommittedText = CombineCommittedAndTail(_localCommittedText, tailText);
                    _localTailStartByte = Math.Max(_localTailStartByte, windowEndByte - keepTailBytes);
                    _localTailStartByte -= _localTailStartByte % blockAlign;
                }
            }
        }
        finally
        {
            _localPreviewLock.Release();
        }
    }

    private async Task ProcessLocalBlocksAsync(AudioCaptureSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (_activeTranscriptionProvider == "whisper" && await EnsureLocalSessionAsync(cancellationToken) is null)
            return;
        if (snapshot.TotalLengthBytes == 0) return;
        await _localPreviewLock.WaitAsync(cancellationToken);
        try
        {
            var bytesPerSecond = Math.Max(snapshot.Format.AverageBytesPerSecond,
                snapshot.Format.SampleRate * Math.Max(1, snapshot.Format.BlockAlign));
            var blockAlign = Math.Max(1, snapshot.Format.BlockAlign);
            long ToAlignedBytes(long milliseconds)
            {
                var bytes = (long)Math.Round(bytesPerSecond * milliseconds / 1_000d);
                return Math.Max(blockAlign, bytes - bytes % blockAlign);
            }

            var isCloud = _activeTranscriptionProvider != "whisper";
            var firstWindowBytes = ToAlignedBytes(BlockPreviewWindowMilliseconds);
            var stepBytes = ToAlignedBytes(isCloud ? CloudBlockStepMilliseconds : BlockPreviewStepMilliseconds);
            var overlapBytes = ToAlignedBytes(isCloud ? CloudBlockOverlapMilliseconds : BlockPreviewOverlapMilliseconds);
            if (_localNextEndBytes == 0) _localNextEndBytes = firstWindowBytes;

            while (_localProcessedEndBytes < snapshot.TotalLengthBytes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var totalBytes = snapshot.TotalLengthBytes - snapshot.TotalLengthBytes % blockAlign;
                if (totalBytes < _localNextEndBytes) break;

                var endByte = _localNextEndBytes;
                if (endByte <= _localProcessedEndBytes) break;
                var startByte = Math.Max(0, _localProcessedEndBytes - overlapBytes);
                startByte -= startByte % blockAlign;
                var window = _audio.GetSnapshot(startByte, endByte);
                var wav = AudioCapture.ToWhisperWave(window, 0, window.Raw.Length);
                var segments = await TranscribePreviewSliceAsync(wav, cancellationToken);
                _localProcessedEndBytes = endByte;
                _localNextEndBytes = endByte + stepBytes;
                var chunkText = string.Join(" ", segments.Select(segment => segment.Text)).Trim();
                if (!string.IsNullOrWhiteSpace(chunkText))
                {
                    _localRawTranscript = MergeTranscriptChunks(_localRawTranscript, chunkText);
                    TranscriptPreviewChanged?.Invoke(_localRawTranscript);
                    _status(isCloud ? "Transcripción en curso (Groq)…" : "Transcripción local en curso…");
                }
            }
        }
        finally
        {
            _localPreviewLock.Release();
        }
    }

    private async Task<string> TranscribeGroqFinalWithRecoveryAsync(byte[] wav, string previewFallback)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var transcript = await _transcriber.TranscribeAsync(wav, CancellationToken.None);
                if (!string.IsNullOrWhiteSpace(transcript))
                    return transcript;

                if (!string.IsNullOrWhiteSpace(previewFallback))
                {
                    LogDiagnostic($"groq_final_empty_fallback bytes={wav.Length} preview_len={previewFallback.Length}");
                    return previewFallback;
                }
                return string.Empty;
            }
            catch (GroqRateLimitException) when (attempt == 1)
            {
                LogDiagnostic("groq_final_429_retry");
                await Task.Delay(TimeSpan.FromMilliseconds(1_500));
            }
            catch (Exception error) when (!string.IsNullOrWhiteSpace(previewFallback) &&
                                          error is GroqRateLimitException or HttpRequestException or TaskCanceledException)
            {
                LogDiagnostic($"groq_final_error_fallback type={error.GetType().Name}");
                return previewFallback;
            }
        }
    }

    private void LogDiagnostic(string entry)
    {
        try
        {
            var dir = Path.GetDirectoryName(_outbox.DatabasePath);
            if (string.IsNullOrWhiteSpace(dir)) return;
            Directory.CreateDirectory(dir);
            var logPath = Path.Combine(dir, "flow-diag.log");
            if (File.Exists(logPath) && new FileInfo(logPath).Length > 64 * 1024)
                File.WriteAllText(logPath, string.Empty);
            File.AppendAllText(logPath, $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {entry}{Environment.NewLine}");
        }
        catch { }
    }

    private async Task<string> TranscribeFinalLocalAsync(AudioCaptureSnapshot snapshot, CancellationToken cancellationToken)
    {
        var session = await EnsureLocalSessionAsync(cancellationToken);
        var previewFallback = DictationTextProcessor.RemoveAsrArtifacts(_localRawTranscript);
        if (session is null || snapshot.TotalLengthBytes == 0) return previewFallback;
        await _localPreviewLock.WaitAsync(cancellationToken);
        try
        {
            var bytesPerSecond = Math.Max(snapshot.Format.AverageBytesPerSecond,
                snapshot.Format.SampleRate * Math.Max(1, snapshot.Format.BlockAlign));
            var blockAlign = Math.Max(1, snapshot.Format.BlockAlign);
            var totalBytes = snapshot.TotalLengthBytes - snapshot.TotalLengthBytes % blockAlign;
            var sliceLimitBytes = (long)Math.Round(bytesPerSecond * FinalFullWindowMilliseconds / 1_000d);
            sliceLimitBytes -= sliceLimitBytes % blockAlign;

            if (totalBytes <= sliceLimitBytes)
            {
                var fullWav = AudioCapture.ToWhisperWave(snapshot, 0, checked((int)totalBytes));
                if (!AudioCapture.HasSpeechEnergy(fullWav) && string.IsNullOrWhiteSpace(previewFallback))
                {
                    LogDiagnostic($"finish provider=whisper skipped=no_speech_energy bytes={fullWav.Length}");
                    return string.Empty;
                }
                var fullText = DictationTextProcessor.RemoveAsrArtifacts(await session.TranscribeFinalAsync(fullWav, cancellationToken));
                return string.IsNullOrWhiteSpace(fullText) ? previewFallback : fullText;
            }

            var sliceStepBytes = (long)Math.Round(bytesPerSecond * FinalTailSliceMilliseconds / 1_000d);
            sliceStepBytes -= sliceStepBytes % blockAlign;
            var overlapBytes = (long)Math.Round(bytesPerSecond * 1_500d / 1_000d);
            overlapBytes -= overlapBytes % blockAlign;

            var cursor = 0L;
            var merged = string.Empty;
            while (cursor < totalBytes)
            {
                var start = Math.Max(0, cursor - (cursor == 0 ? 0 : overlapBytes));
                start -= start % blockAlign;
                var end = Math.Min(totalBytes, cursor + sliceStepBytes);
                end -= end % blockAlign;
                var wav = AudioCapture.ToWhisperWave(snapshot, checked((int)start), checked((int)end));
                if (AudioCapture.HasSpeechEnergy(wav))
                {
                    var chunk = await session.TranscribeFinalAsync(wav, cancellationToken);
                    if (!string.IsNullOrWhiteSpace(chunk))
                        merged = MergeTranscriptChunks(merged, chunk);
                }
                if (end >= totalBytes) break;
                cursor = end;
            }
            var resolved = DictationTextProcessor.RemoveAsrArtifacts(merged);
            return string.IsNullOrWhiteSpace(resolved) ? previewFallback : resolved;
        }
        finally
        {
            _localPreviewLock.Release();
        }
    }

    private static string CombineCommittedAndTail(string committed, string tail)
    {
        if (string.IsNullOrWhiteSpace(committed)) return tail.Trim();
        if (string.IsNullOrWhiteSpace(tail)) return committed.Trim();
        return MergeTranscriptChunks(committed, tail);
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
        _cachedWhisperSession?.Dispose();
        _cachedWhisperSession = null;
        _whisperCacheLock.Dispose();
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
