using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Flow.Windows;

public sealed record AudioDeviceInfo(string Id, string Name, bool IsDefault);
public sealed record AudioCaptureSnapshot(byte[] Raw, WaveFormat Format, long TotalLengthBytes, long StartByteOffset);

public sealed class AudioCapture : IDisposable
{
    private WasapiCapture? _input;
    private string _inputDeviceName;
    private readonly MemoryStream _audio = new();
    private readonly object _audioLock = new();
    private bool _started;

    public event Action<float>? LevelChanged;

    public string InputDeviceName => _inputDeviceName;

    public AudioCapture(string? deviceId = null)
    {
        _inputDeviceName = "Micrófono predeterminado";
        SetupDevice(deviceId);
    }

    public static List<AudioDeviceInfo> GetInputDevices()
    {
        var devices = new List<AudioDeviceInfo>();
        try
        {
            var enumerator = new MMDeviceEnumerator();
            MMDevice? defaultDevice = null;
            try
            {
                defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            }
            catch
            {
                try { defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console); }
                catch { }
            }

            var endpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
            foreach (var endpoint in endpoints)
            {
                var isDefault = defaultDevice != null && endpoint.ID == defaultDevice.ID;
                devices.Add(new AudioDeviceInfo(endpoint.ID, endpoint.FriendlyName, isDefault));
            }
        }
        catch
        {
            // Fallback if core audio enumeration encounters restrictions
        }
        return devices;
    }

    public void ChangeDevice(string? deviceId)
    {
        lock (_audioLock)
        {
            var wasRecording = _started;
            if (wasRecording)
            {
                _input?.StopRecording();
                _started = false;
            }

            _input?.Dispose();
            _input = null;

            SetupDevice(deviceId);

            if (wasRecording)
            {
                Start();
            }
        }
    }

    private void SetupDevice(string? deviceId)
    {
        try
        {
            var enumerator = new MMDeviceEnumerator();
            MMDevice? device = null;

            if (!string.IsNullOrEmpty(deviceId))
            {
                try
                {
                    device = enumerator.GetDevice(deviceId);
                }
                catch
                {
                    // Fall back to default if specified device is disconnected
                    device = null;
                }
            }

            device ??= enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            _inputDeviceName = device.FriendlyName;
            _input = new WasapiCapture(device, useEventSync: false, audioBufferMillisecondsLength: 80);
            _input.DataAvailable += (_, args) =>
            {
                lock (_audioLock)
                {
                    if (_started)
                    {
                        _audio.Write(args.Buffer, 0, args.BytesRecorded);
                    }
                }
                LevelChanged?.Invoke(MeasurePeak(args.Buffer, args.BytesRecorded, _input.WaveFormat));
            };
        }
        catch (Exception)
        {
            // Fallback to legacy default WasapiCapture
            var defDevice = WasapiCapture.GetDefaultCaptureDevice();
            _inputDeviceName = defDevice.FriendlyName;
            _input = new WasapiCapture(defDevice, useEventSync: false, audioBufferMillisecondsLength: 100);
            _input.DataAvailable += (_, args) =>
            {
                lock (_audioLock)
                {
                    if (_started)
                    {
                        _audio.Write(args.Buffer, 0, args.BytesRecorded);
                    }
                }
                LevelChanged?.Invoke(MeasurePeak(args.Buffer, args.BytesRecorded, _input.WaveFormat));
            };
        }
    }

    public void Start()
    {
        if (_started || _input == null) return;
        lock (_audioLock)
        {
            _audio.SetLength(0);
            _audio.Position = 0;
        }
        _input.StartRecording();
        _started = true;
    }

    public async Task<byte[]> StopAsWaveAsync()
    {
        var snapshot = await StopAsSnapshotAsync();
        if (snapshot.Raw.Length == 0)
            return [];

        var pcm = ConvertToPcm16(snapshot.Raw, snapshot.Format);
        if (pcm.Length == 0)
            return [];

        using var output = new MemoryStream();
        using (var writer = new WaveFileWriter(output, CreateOutputFormat(snapshot.Format)))
        {
            writer.Write(pcm, 0, pcm.Length);
        }
        var wav = output.ToArray();
        return wav.Length <= 44 ? [] : wav;
    }

    public async Task<AudioCaptureSnapshot> StopAsSnapshotAsync(long startByte = 0, long? endByte = null)
    {
        if (!_started || _input == null)
            return GetSnapshot(startByte, endByte);

        var recordingStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<StoppedEventArgs> stoppedHandler = (_, _) => recordingStopped.TrySetResult();
        _input.RecordingStopped += stoppedHandler;
        try
        {
            _input.StopRecording();
            await recordingStopped.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            _input.RecordingStopped -= stoppedHandler;
            _started = false;
        }

        return GetSnapshot(startByte, endByte);
    }

    public AudioCaptureSnapshot GetSnapshot(long startByte = 0, long? endByte = null)
    {
        lock (_audioLock)
        {
            var format = _input?.WaveFormat ?? new WaveFormat(16_000, 16, 1);
            var totalLength = _audio.Length;
            var start = Math.Clamp(startByte, 0, totalLength);
            var end = Math.Clamp(endByte ?? totalLength, start, totalLength);
            var count = checked((int)(end - start));
            var raw = new byte[count];
            if (count > 0)
                Buffer.BlockCopy(_audio.GetBuffer(), checked((int)start), raw, 0, count);
            return new AudioCaptureSnapshot(raw, format, totalLength, start);
        }
    }

    public static byte[] ToWhisperWave(AudioCaptureSnapshot snapshot, int startByte, int endByte)
    {
        var blockAlign = Math.Max(1, snapshot.Format.BlockAlign);
        var start = Math.Clamp(startByte, 0, snapshot.Raw.Length);
        var end = Math.Clamp(endByte, start, snapshot.Raw.Length);
        start -= start % blockAlign;
        end -= end % blockAlign;
        if (end - start < blockAlign) return [];

        var raw = snapshot.Raw.AsSpan(start, end - start).ToArray();
        using var sourceStream = new RawSourceWaveStream(new MemoryStream(raw, writable: false), snapshot.Format);
        ISampleProvider mono = new MonoDownmixSampleProvider(sourceStream.ToSampleProvider());
        ISampleProvider resampled = mono.WaveFormat.SampleRate == 16_000
            ? mono
            : new WdlResamplingSampleProvider(mono, 16_000);

        var samples = new List<float>();
        var buffer = new float[4096];
        int read;
        while ((read = resampled.Read(buffer, 0, buffer.Length)) > 0)
            for (var index = 0; index < read; index++) samples.Add(buffer[index]);
        if (samples.Count == 0) return [];

        var pcm = new byte[samples.Count * sizeof(short)];
        for (var index = 0; index < samples.Count; index++)
        {
            var value = (short)Math.Round(Math.Clamp(samples[index], -1f, 1f) * short.MaxValue);
            BitConverter.TryWriteBytes(pcm.AsSpan(index * sizeof(short), sizeof(short)), value);
        }
        using var output = new MemoryStream();
        using (var writer = new WaveFileWriter(output, new WaveFormat(16_000, 16, 1)))
        {
            writer.Write(pcm, 0, pcm.Length);
        }
        var wav = output.ToArray();
        return wav.Length <= 44 ? [] : wav;
    }

    public static bool HasSpeechEnergy(byte[] wav)
        => TryAnalyzeWaveSpeech(wav, rangeStart: null, rangeEnd: null, out var hasSpeech) && hasSpeech;

    public static bool HasSpeechEnergyInRange(byte[] wav, TimeSpan start, TimeSpan end)
        => TryAnalyzeWaveSpeech(wav, start, end, out var hasSpeech) && hasSpeech;

    public static bool TryHasSpeechEnergyInRange(byte[] audio, TimeSpan start, TimeSpan end, out bool hasSpeech)
        => TryAnalyzeWaveSpeech(audio, start, end, out hasSpeech);

    private static bool TryAnalyzeWaveSpeech(byte[] data, TimeSpan? rangeStart, TimeSpan? rangeEnd, out bool hasSpeech)
    {
        hasSpeech = false;
        if (!TryParsePcm16Wave(data, out var channels, out var sampleRate, out var dataOffset, out var dataLength))
            return false;

        var bytesPerSampleFrame = channels * sizeof(short);
        var totalSampleFrames = dataLength / bytesPerSampleFrame;
        if (totalSampleFrames <= 0)
            return true;

        var framesPerWindow = Math.Max(1, sampleRate / 50); // 20 ms windows
        var windowCount = totalSampleFrames / framesPerWindow;
        if (windowCount < 4)
            return true;

        var windowRms = new double[windowCount];
        var windowPeak = new double[windowCount];
        for (var windowIndex = 0; windowIndex < windowCount; windowIndex++)
        {
            var baseByte = dataOffset + windowIndex * framesPerWindow * bytesPerSampleFrame;
            double sumSquares = 0;
            double maxSample = 0;
            for (var sampleIndex = 0; sampleIndex < framesPerWindow; sampleIndex++)
            {
                var frameOffset = baseByte + sampleIndex * bytesPerSampleFrame;
                double frameMax = 0;
                for (var channel = 0; channel < channels; channel++)
                {
                    var sample = BitConverter.ToInt16(data, frameOffset + channel * sizeof(short));
                    var normalized = Math.Abs(sample / 32768d);
                    if (normalized > frameMax) frameMax = normalized;
                }
                sumSquares += frameMax * frameMax;
                if (frameMax > maxSample) maxSample = frameMax;
            }
            windowRms[windowIndex] = Math.Sqrt(sumSquares / framesPerWindow);
            windowPeak[windowIndex] = maxSample;
        }

        // Only treat flat, low-level constant energy as stationary room/fan noise;
        // never let speech itself raise the threshold on continuous dictations.
        var sortedRms = (double[])windowRms.Clone();
        Array.Sort(sortedRms);
        var baselineRms = sortedRms[Math.Min(sortedRms.Length - 1, Math.Max(0, (int)Math.Floor(sortedRms.Length * 0.10)))];
        const double rmsThreshold = 0.0022;
        const double peakThreshold = 0.0085;

        var startWindow = 0;
        var endWindow = windowCount - 1;
        if (rangeStart.HasValue && rangeEnd.HasValue && rangeEnd.Value > rangeStart.Value)
        {
            const double paddingMs = 250d;
            var startMs = Math.Max(0d, rangeStart.Value.TotalMilliseconds - paddingMs);
            var endMs = rangeEnd.Value.TotalMilliseconds + paddingMs;
            if (endMs - startMs < 160d)
            {
                var mid = (startMs + endMs) / 2d;
                startMs = Math.Max(0d, mid - 80d);
                endMs = startMs + 160d;
            }

            startWindow = Math.Clamp((int)Math.Floor(startMs / 20d), 0, windowCount - 1);
            endWindow = Math.Clamp((int)Math.Ceiling(endMs / 20d), startWindow, windowCount - 1);
        }

        var activeWindows = 0;
        double rangeMaxRms = 0;
        double rangeMaxPeak = 0;
        for (var i = startWindow; i <= endWindow; i++)
        {
            if (windowRms[i] > rangeMaxRms) rangeMaxRms = windowRms[i];
            if (windowPeak[i] > rangeMaxPeak) rangeMaxPeak = windowPeak[i];
            if (windowRms[i] >= rmsThreshold && windowPeak[i] >= peakThreshold)
                activeWindows++;
        }

        if (activeWindows < 3)
        {
            hasSpeech = false;
            return true;
        }

        var isStationaryFlatNoise = rangeMaxPeak < 0.022 && rangeMaxRms <= Math.Max(rmsThreshold, baselineRms * 1.30);
        hasSpeech = !isStationaryFlatNoise;
        return true;
    }

    private static bool TryParsePcm16Wave(
        byte[] data,
        out int channels,
        out int sampleRate,
        out int dataOffset,
        out int dataLength)
    {
        channels = 0;
        sampleRate = 0;
        dataOffset = 0;
        dataLength = 0;

        if (data.Length < 44 ||
            data[0] != 'R' || data[1] != 'I' || data[2] != 'F' || data[3] != 'F' ||
            data[8] != 'W' || data[9] != 'A' || data[10] != 'V' || data[11] != 'E')
        {
            return false;
        }

        var foundFmt = false;
        var foundData = false;
        var audioFormat = 0;
        var bitsPerSample = 0;
        var cursor = 12;

        while (cursor + 8 <= data.Length)
        {
            var chunkSize = BitConverter.ToInt32(data, cursor + 4);
            if (chunkSize < 0) return false;
            var payloadOffset = cursor + 8;

            if (data[cursor] == 'f' && data[cursor + 1] == 'm' && data[cursor + 2] == 't' && data[cursor + 3] == ' ')
            {
                if (payloadOffset + 16 > data.Length || chunkSize < 16) return false;
                audioFormat = BitConverter.ToUInt16(data, payloadOffset);
                channels = BitConverter.ToUInt16(data, payloadOffset + 2);
                sampleRate = BitConverter.ToInt32(data, payloadOffset + 4);
                bitsPerSample = BitConverter.ToUInt16(data, payloadOffset + 14);
                foundFmt = true;
            }
            else if (data[cursor] == 'd' && data[cursor + 1] == 'a' && data[cursor + 2] == 't' && data[cursor + 3] == 'a')
            {
                dataOffset = payloadOffset;
                dataLength = Math.Min(chunkSize, Math.Max(0, data.Length - payloadOffset));
                foundData = true;
                break;
            }

            var paddedSize = chunkSize + (chunkSize & 1);
            var next = payloadOffset + paddedSize;
            if (next <= cursor) return false;
            cursor = next;
        }

        return foundFmt &&
               foundData &&
               audioFormat == 1 &&
               channels >= 1 &&
               sampleRate >= 8_000 &&
               bitsPerSample == 16;
    }

    private static WaveFormat CreateOutputFormat(WaveFormat input)
        => new(input.SampleRate, 16, input.Channels);

    private static float MeasurePeak(byte[] buffer, int bytesRecorded, WaveFormat format)
    {
        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            var peak = 0f;
            for (var offset = 0; offset + sizeof(float) <= bytesRecorded; offset += sizeof(float))
                peak = Math.Max(peak, Math.Abs(BitConverter.ToSingle(buffer, offset)));
            return Math.Clamp(peak, 0f, 1f);
        }

        if (format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 16)
        {
            var peak = 0f;
            for (var offset = 0; offset + sizeof(short) <= bytesRecorded; offset += sizeof(short))
                peak = Math.Max(peak, Math.Abs(BitConverter.ToInt16(buffer, offset) / (float)short.MaxValue));
            return Math.Clamp(peak, 0f, 1f);
        }

        return 0f;
    }

    private static byte[] ConvertToPcm16(byte[] raw, WaveFormat input)
    {
        if (input.Encoding == WaveFormatEncoding.Pcm && input.BitsPerSample == 16)
            return raw;

        if (input.Encoding != WaveFormatEncoding.IeeeFloat || input.BitsPerSample != 32)
            throw new InvalidOperationException($"Formato de micrófono no compatible: {input.Encoding}, {input.BitsPerSample} bits.");

        var sampleCount = raw.Length / sizeof(float);
        var pcm = new byte[sampleCount * sizeof(short)];
        for (var i = 0; i < sampleCount; i++)
        {
            var sample = Math.Clamp(BitConverter.ToSingle(raw, i * sizeof(float)), -1f, 1f);
            var value = (short)Math.Round(sample * short.MaxValue);
            BitConverter.TryWriteBytes(pcm.AsSpan(i * sizeof(short), sizeof(short)), value);
        }
        return pcm;
    }

    private sealed class MonoDownmixSampleProvider(ISampleProvider source) : ISampleProvider
    {
        private readonly int _channels = source.WaveFormat.Channels;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            if (_channels == 1) return source.Read(buffer, offset, count);
            var interleaved = new float[count * _channels];
            var sourceRead = source.Read(interleaved, 0, interleaved.Length);
            var frames = sourceRead / _channels;
            for (var frame = 0; frame < frames; frame++)
            {
                var total = 0f;
                for (var channel = 0; channel < _channels; channel++)
                    total += interleaved[frame * _channels + channel];
                buffer[offset + frame] = total / _channels;
            }
            return frames;
        }
    }

    public void Dispose() => _input?.Dispose();
}
