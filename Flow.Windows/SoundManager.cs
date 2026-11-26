using System.IO;
using System.Media;

namespace Flow.Windows;

public static class SoundManager
{
    private const int SampleRate = 44_100;
    private static readonly Lazy<byte[]> StartBubbleWave = new(CreateStartBubbleWave);
    private static readonly Lazy<byte[]> StopBubbleWave = new(CreateStopBubbleWave);
    private static readonly Lazy<byte[]> PasteBubbleWave = new(CreatePasteBubbleWave);

    private static string? _startSoundPath;
    private static string? _stopSoundPath;
    private static string? _pasteSoundPath;
    private static bool _initialized;

    public static bool SoundEnabled { get; set; } = true;

    private static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var localSoundDir = Path.Combine(baseDir, "Assets", "Sounds");
        if (!Directory.Exists(localSoundDir)) return;

        _startSoundPath = Path.Combine(localSoundDir, "dictation-start.wav");
        _stopSoundPath = Path.Combine(localSoundDir, "dictation-stop.wav");
        _pasteSoundPath = Path.Combine(localSoundDir, "paste.wav");
    }

    public static void PlayStart()
    {
        if (!SoundEnabled) return;
        Initialize();
        PlaySound(_startSoundPath, StartBubbleWave.Value);
    }

    public static void PlayStop()
    {
        if (!SoundEnabled) return;
        Initialize();
        PlaySound(_stopSoundPath, StopBubbleWave.Value);
    }

    public static void PlayPaste()
    {
        if (!SoundEnabled) return;
        Initialize();
        PlaySound(_pasteSoundPath, PasteBubbleWave.Value);
    }

    public static byte[] CreateStartBubbleWave() => SynthesizeWave(96, t =>
    {
        // Liquid upward bubble pop: quick Minnaert pitch glide + soft harmonic shimmer.
        var env = AttackEnvelope(t, 0.004) * Math.Exp(-t * 36d);
        var main = Math.Sin(2d * Math.PI * IntegralSweep(390d, 390d, 0.068, t)) * env;

        var shimmerT = Math.Max(0d, t - 0.022);
        var shimmerEnv = t < 0.022 ? 0d : AttackEnvelope(shimmerT, 0.004) * Math.Exp(-shimmerT * 42d);
        var shimmer = Math.Sin(2d * Math.PI * IntegralLinear(780d, 1_120d, 0.065, shimmerT)) * shimmerEnv * 0.34;
        return (main + shimmer) * 0.28;
    });

    public static byte[] CreateStopBubbleWave() => SynthesizeWave(92, t =>
    {
        // Soft downward closing bubble pop: mirror gesture to PlayStart.
        var env = AttackEnvelope(t, 0.004) * Math.Exp(-t * 38d);
        var main = Math.Sin(2d * Math.PI * IntegralLinear(680d, 350d, 0.078, t)) * env;
        var overtone = Math.Sin(2d * Math.PI * IntegralLinear(1_020d, 560d, 0.055, t)) * env * 0.16;
        return (main + overtone) * 0.27;
    });

    public static byte[] CreatePasteBubbleWave() => SynthesizeWave(124, t =>
    {
        // Crisp double water droplet confirmation without harsh alert chimes.
        var firstEnv = t < 0.052 ? AttackEnvelope(t, 0.003) * Math.Exp(-t * 62d) : 0d;
        var first = Math.Sin(2d * Math.PI * IntegralLinear(640d, 860d, 0.048, t)) * firstEnv * 0.65;

        var secondT = t - 0.044;
        var secondEnv = secondT >= 0d ? AttackEnvelope(secondT, 0.003) * Math.Exp(-secondT * 44d) : 0d;
        var second = secondT >= 0d
            ? Math.Sin(2d * Math.PI * IntegralLinear(860d, 1_160d, 0.065, secondT)) * secondEnv
            : 0d;
        return (first + second) * 0.22;
    });

    private static double AttackEnvelope(double t, double attackSeconds)
        => t <= 0d ? 0d : t >= attackSeconds ? 1d : 0.5d * (1d - Math.Cos(Math.PI * t / attackSeconds));

    private static double IntegralLinear(double f0, double f1, double duration, double t)
    {
        var clamped = Math.Clamp(t, 0d, duration);
        var slope = (f1 - f0) / duration;
        var phase = f0 * clamped + 0.5d * slope * clamped * clamped;
        return t > duration ? phase + f1 * (t - duration) : phase;
    }

    private static double IntegralSweep(double f0, double delta, double duration, double t)
    {
        // Integral of f0 + delta * (t/duration)^0.72
        var clamped = Math.Clamp(t, 0d, duration);
        const double power = 0.72;
        var phase = f0 * clamped + delta * Math.Pow(clamped, power + 1d) / ((power + 1d) * Math.Pow(duration, power));
        return t > duration ? phase + (f0 + delta) * (t - duration) : phase;
    }

    private static byte[] SynthesizeWave(int durationMs, Func<double, double> sampleAt)
    {
        var sampleCount = SampleRate * durationMs / 1_000;
        var dataSize = sampleCount * sizeof(short);
        var wav = new byte[44 + dataSize];

        WriteAscii(wav, 0, "RIFF");
        BitConverter.TryWriteBytes(wav.AsSpan(4, 4), 36 + dataSize);
        WriteAscii(wav, 8, "WAVE");
        WriteAscii(wav, 12, "fmt ");
        BitConverter.TryWriteBytes(wav.AsSpan(16, 4), 16);
        BitConverter.TryWriteBytes(wav.AsSpan(20, 2), (short)1);
        BitConverter.TryWriteBytes(wav.AsSpan(22, 2), (short)1);
        BitConverter.TryWriteBytes(wav.AsSpan(24, 4), SampleRate);
        BitConverter.TryWriteBytes(wav.AsSpan(28, 4), SampleRate * sizeof(short));
        BitConverter.TryWriteBytes(wav.AsSpan(32, 2), (short)sizeof(short));
        BitConverter.TryWriteBytes(wav.AsSpan(34, 2), (short)16);
        WriteAscii(wav, 36, "data");
        BitConverter.TryWriteBytes(wav.AsSpan(40, 4), dataSize);

        for (var i = 0; i < sampleCount; i++)
        {
            var t = i / (double)SampleRate;
            // Smooth 4 ms fade-out at end to guarantee zero tail click.
            var tailFade = i >= sampleCount - 176 ? (sampleCount - 1 - i) / 176d : 1d;
            var amplitude = Math.Clamp(sampleAt(t) * tailFade, -1d, 1d);
            var pcm = (short)Math.Round(amplitude * short.MaxValue);
            BitConverter.TryWriteBytes(wav.AsSpan(44 + i * sizeof(short), sizeof(short)), pcm);
        }

        return wav;
    }

    private static void WriteAscii(byte[] buffer, int offset, string text)
    {
        for (var i = 0; i < text.Length; i++)
            buffer[offset + i] = (byte)text[i];
    }

    private static void PlaySound(string? overridePath, byte[] synthesizedWave)
    {
        Task.Run(() =>
        {
            try
            {
                if (!string.IsNullOrEmpty(overridePath) && File.Exists(overridePath))
                {
                    using var player = new SoundPlayer(overridePath);
                    player.PlaySync();
                    return;
                }

                using var stream = new MemoryStream(synthesizedWave, writable: false);
                using var synthesizedPlayer = new SoundPlayer(stream);
                synthesizedPlayer.PlaySync();
            }
            catch
            {
                // Silently handle any audio device playback issues
            }
        });
    }
}
