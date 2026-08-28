using System.IO;
using System.Media;

namespace Flow.Windows;

public static class SoundManager
{
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

        // Try local assets first
        if (Directory.Exists(localSoundDir))
        {
            _startSoundPath = Path.Combine(localSoundDir, "dictation-start.wav");
            _stopSoundPath = Path.Combine(localSoundDir, "dictation-stop.wav");
            _pasteSoundPath = Path.Combine(localSoundDir, "paste.wav");
        }

        // Fallback: Locate installed Wispr Flow sounds on Windows
        if (!File.Exists(_startSoundPath))
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var wisprDir = Path.Combine(localAppData, "WisprFlow");
            if (Directory.Exists(wisprDir))
            {
                var appDirs = Directory.GetDirectories(wisprDir, "app-*");
                if (appDirs.Length > 0)
                {
                    Array.Sort(appDirs);
                    var latestAppDir = appDirs[^1];
                    var soundsPath = Path.Combine(latestAppDir, "resources", "assets", "sounds");
                    if (Directory.Exists(soundsPath))
                    {
                        _startSoundPath = Path.Combine(soundsPath, "dictation-start.wav");
                        _stopSoundPath = Path.Combine(soundsPath, "dictation-stop.wav");
                        _pasteSoundPath = Path.Combine(soundsPath, "paste.wav");
                    }
                }
            }
        }
    }

    public static void PlayStart()
    {
        if (!SoundEnabled) return;
        PlayFile(_startSoundPath ?? string.Empty, SystemSounds.Asterisk);
    }

    public static void PlayStop()
    {
        if (!SoundEnabled) return;
        PlayFile(_stopSoundPath ?? string.Empty, SystemSounds.Beep);
    }

    public static void PlayPaste()
    {
        if (!SoundEnabled) return;
        PlayFile(_pasteSoundPath ?? string.Empty, SystemSounds.Hand);
    }

    private static void PlayFile(string path, SystemSound fallback)
    {
        Initialize();
        Task.Run(() =>
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    using var player = new SoundPlayer(path);
                    player.Play();
                }
                else
                {
                    fallback.Play();
                }
            }
            catch
            {
                // Silently handle any audio device playback issues
            }
        });
    }
}
