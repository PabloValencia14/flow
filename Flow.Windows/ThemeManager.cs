using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Flow.Windows;

public enum AppTheme
{
    Dark,
    Light,
    System
}

public static class ThemeManager
{
    private static readonly object TrackingLock = new();
    private static bool _systemTrackingRegistered;

    public static AppTheme CurrentTheme { get; private set; } = AppTheme.Dark;
    public static bool IsLightActive { get; private set; }

    public static event Action<AppTheme, bool>? ThemeChanged;

    public static bool ResolveIsLight(AppTheme mode, bool windowsInDarkMode) => mode switch
    {
        AppTheme.Light => true,
        AppTheme.System => !windowsInDarkMode,
        _ => false
    };

    public static void ApplyTheme(AppTheme mode)
    {
        CurrentTheme = mode;
        var isLight = ResolveIsLight(mode, IsWindowsInDarkMode());

        IsLightActive = isLight;
        if (Application.Current is not null)
            EnsureSystemThemeTracking();
        UpdateBrushes(isLight);
        ThemeChanged?.Invoke(CurrentTheme, isLight);
    }

    public static bool SyncSystemThemeIfNeeded()
    {
        if (CurrentTheme != AppTheme.System) return false;
        var shouldBeLight = !IsWindowsInDarkMode();
        if (shouldBeLight == IsLightActive) return false;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => SyncSystemThemeIfNeeded());
            return true;
        }

        ApplyTheme(AppTheme.System);
        return true;
    }

    public static void EnsureSystemThemeTracking()
    {
        lock (TrackingLock)
        {
            if (_systemTrackingRegistered) return;
            SystemEvents.UserPreferenceChanged += OnSystemUserPreferenceChanged;
            SystemEvents.PowerModeChanged += OnSystemPowerModeChanged;
            _systemTrackingRegistered = true;
        }
    }

    public static void StopSystemThemeTracking()
    {
        lock (TrackingLock)
        {
            if (!_systemTrackingRegistered) return;
            SystemEvents.UserPreferenceChanged -= OnSystemUserPreferenceChanged;
            SystemEvents.PowerModeChanged -= OnSystemPowerModeChanged;
            _systemTrackingRegistered = false;
        }
    }

    private static void OnSystemUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or
            UserPreferenceCategory.Color or
            UserPreferenceCategory.VisualStyle or
            UserPreferenceCategory.Desktop)
        {
            SyncSystemThemeIfNeeded();
        }
    }

    private static void OnSystemPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
            SyncSystemThemeIfNeeded();
    }

    public static void ToggleTheme()
    {
        ApplyTheme(IsLightActive ? AppTheme.Dark : AppTheme.Light);
    }

    private static void UpdateBrushes(bool isLight)
    {
        var app = Application.Current;
        if (app == null) return;

        // Evergreen surfaces keep the interface calm; each operational state has
        // its own color so recording, completion, warnings, and sync are distinct.
        var bgMain = isLight ? Color.FromRgb(240, 244, 235) : Color.FromRgb(18, 25, 22);
        var bgSidebar = isLight ? Color.FromRgb(221, 233, 219) : Color.FromRgb(27, 39, 32);
        var bgCard = isLight ? Color.FromRgb(249, 251, 246) : Color.FromRgb(32, 46, 37);
        var bgCardHover = isLight ? Color.FromRgb(229, 239, 222) : Color.FromRgb(43, 59, 48);
        var bgInput = isLight ? Color.FromRgb(251, 252, 248) : Color.FromRgb(23, 31, 26);
        var border = isLight ? Color.FromRgb(193, 210, 190) : Color.FromRgb(60, 80, 65);
        var borderHover = isLight ? Color.FromRgb(119, 150, 120) : Color.FromRgb(98, 122, 101);
        var textPrimary = isLight ? Color.FromRgb(30, 48, 36) : Color.FromRgb(241, 244, 232);
        var textSecondary = isLight ? Color.FromRgb(73, 94, 77) : Color.FromRgb(193, 202, 187);
        var textMuted = isLight ? Color.FromRgb(104, 125, 108) : Color.FromRgb(145, 160, 145);
        var accent = isLight ? Color.FromRgb(42, 115, 72) : Color.FromRgb(185, 233, 87);
        var accentSoft = isLight ? Color.FromRgb(177, 214, 179) : Color.FromRgb(89, 111, 56);
        var accentIndigo = isLight ? Color.FromRgb(53, 101, 184) : Color.FromRgb(128, 170, 255);
        var accentRecording = isLight ? Color.FromRgb(196, 68, 58) : Color.FromRgb(255, 116, 100);
        var accentSuccess = isLight ? Color.FromRgb(34, 122, 73) : Color.FromRgb(120, 217, 157);
        var accentWarning = isLight ? Color.FromRgb(159, 98, 15) : Color.FromRgb(240, 190, 89);
        var onAccent = isLight ? Color.FromRgb(255, 255, 255) : Color.FromRgb(24, 33, 15);
        var islandBg = isLight ? Color.FromRgb(249, 251, 246) : Color.FromRgb(16, 16, 18);
        var islandBorder = isLight ? Color.FromRgb(184, 203, 181) : Color.FromRgb(43, 43, 47);
        var islandInk = isLight ? Color.FromRgb(30, 48, 36) : Color.FromRgb(242, 242, 244);
        var islandText = isLight ? Color.FromRgb(30, 48, 36) : Color.FromRgb(237, 237, 238);
        var islandPending = isLight ? Color.FromRgb(100, 122, 105) : Color.FromRgb(140, 140, 144);

        // Update application level
        SetResource(app.Resources, "BrushBgMain", new SolidColorBrush(bgMain));
        SetResource(app.Resources, "BrushBgSidebar", new SolidColorBrush(bgSidebar));
        SetResource(app.Resources, "BrushBgCard", new SolidColorBrush(bgCard));
        SetResource(app.Resources, "BrushBgCardHover", new SolidColorBrush(bgCardHover));
        SetResource(app.Resources, "BrushBgInput", new SolidColorBrush(bgInput));
        SetResource(app.Resources, "BrushBorder", new SolidColorBrush(border));
        SetResource(app.Resources, "BrushBorderHover", new SolidColorBrush(borderHover));
        SetResource(app.Resources, "BrushTextPrimary", new SolidColorBrush(textPrimary));
        SetResource(app.Resources, "BrushTextSecondary", new SolidColorBrush(textSecondary));
        SetResource(app.Resources, "BrushTextMuted", new SolidColorBrush(textMuted));
        SetResource(app.Resources, "BrushAccentPurple", new SolidColorBrush(accent));
        SetResource(app.Resources, "BrushAccentPurpleDark", new SolidColorBrush(accentSoft));
        SetResource(app.Resources, "BrushAccentIndigo", new SolidColorBrush(accentIndigo));
        SetResource(app.Resources, "BrushAccentRecording", new SolidColorBrush(accentRecording));
        SetResource(app.Resources, "BrushAccentSuccess", new SolidColorBrush(accentSuccess));
        SetResource(app.Resources, "BrushAccentWarning", new SolidColorBrush(accentWarning));
        SetResource(app.Resources, "BrushOnAccent", new SolidColorBrush(onAccent));
        SetResource(app.Resources, "BrushIslandBg", new SolidColorBrush(islandBg));
        SetResource(app.Resources, "BrushIslandBorder", new SolidColorBrush(islandBorder));
        SetResource(app.Resources, "BrushIslandInk", new SolidColorBrush(islandInk));
        SetResource(app.Resources, "BrushIslandText", new SolidColorBrush(islandText));
        SetResource(app.Resources, "BrushIslandPending", new SolidColorBrush(islandPending));
        SetResource(app.Resources, "BrushWisprGradient", new SolidColorBrush(accent));
        SetResource(app.Resources, "BrushFlowPillBg", new SolidColorBrush(bgCard));

        // Also update each open window's resources in case of local overrides
        foreach (Window win in app.Windows)
        {
            SetResource(win.Resources, "BrushBgMain", new SolidColorBrush(bgMain));
            SetResource(win.Resources, "BrushBgSidebar", new SolidColorBrush(bgSidebar));
            SetResource(win.Resources, "BrushBgCard", new SolidColorBrush(bgCard));
            SetResource(win.Resources, "BrushBgCardHover", new SolidColorBrush(bgCardHover));
            SetResource(win.Resources, "BrushBgInput", new SolidColorBrush(bgInput));
            SetResource(win.Resources, "BrushBorder", new SolidColorBrush(border));
            SetResource(win.Resources, "BrushBorderHover", new SolidColorBrush(borderHover));
            SetResource(win.Resources, "BrushTextPrimary", new SolidColorBrush(textPrimary));
            SetResource(win.Resources, "BrushTextSecondary", new SolidColorBrush(textSecondary));
            SetResource(win.Resources, "BrushTextMuted", new SolidColorBrush(textMuted));
            SetResource(win.Resources, "BrushAccentPurple", new SolidColorBrush(accent));
            SetResource(win.Resources, "BrushAccentPurpleDark", new SolidColorBrush(accentSoft));
            SetResource(win.Resources, "BrushAccentIndigo", new SolidColorBrush(accentIndigo));
            SetResource(win.Resources, "BrushAccentRecording", new SolidColorBrush(accentRecording));
            SetResource(win.Resources, "BrushAccentSuccess", new SolidColorBrush(accentSuccess));
            SetResource(win.Resources, "BrushAccentWarning", new SolidColorBrush(accentWarning));
            SetResource(win.Resources, "BrushOnAccent", new SolidColorBrush(onAccent));
            SetResource(win.Resources, "BrushIslandBg", new SolidColorBrush(islandBg));
            SetResource(win.Resources, "BrushIslandBorder", new SolidColorBrush(islandBorder));
            SetResource(win.Resources, "BrushIslandInk", new SolidColorBrush(islandInk));
            SetResource(win.Resources, "BrushIslandText", new SolidColorBrush(islandText));
            SetResource(win.Resources, "BrushIslandPending", new SolidColorBrush(islandPending));
            SetResource(win.Resources, "BrushWisprGradient", new SolidColorBrush(accent));
            SetResource(win.Resources, "BrushFlowPillBg", new SolidColorBrush(bgCard));
        }
    }

    private static void SetResource(ResourceDictionary resources, string key, object value)
    {
        resources[key] = value;
    }

    private static bool IsWindowsInDarkMode()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            return value is int val && val == 0;
        }
        catch
        {
            return true;
        }
    }
}
