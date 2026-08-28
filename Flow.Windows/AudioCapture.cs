using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Flow.Windows;

public sealed record AudioDeviceInfo(string Id, string Name, bool IsDefault);

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
        if (!_started || _input == null) return [];

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

        byte[] raw;
        WaveFormat inputFormat;
        lock (_audioLock)
        {
            if (_audio.Length == 0)
                return [];
            raw = _audio.ToArray();
            inputFormat = _input.WaveFormat;
        }

        var pcm = ConvertToPcm16(raw, inputFormat);
        if (pcm.Length == 0)
            return [];

        using var output = new MemoryStream();
        using (var writer = new WaveFileWriter(output, CreateOutputFormat(inputFormat)))
        {
            writer.Write(pcm, 0, pcm.Length);
        }
        var wav = output.ToArray();
        if (wav.Length <= 44)
            return [];
        return wav;
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

    public void Dispose() => _input?.Dispose();
}
