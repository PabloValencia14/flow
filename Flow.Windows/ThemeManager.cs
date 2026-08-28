using System.Windows;
using System.Windows.Media;

namespace Flow.Windows;

public enum AppTheme
{
    Dark,
    Light,
    System
}

public static class ThemeManager
{
    public static AppTheme CurrentTheme { get; private set; } = AppTheme.Dark;
    public static bool IsLightActive { get; private set; }

    public static event Action<AppTheme, bool>? ThemeChanged;

    public static void ApplyTheme(AppTheme mode)
    {
        CurrentTheme = mode;
        var isLight = mode == AppTheme.Light;

        if (mode == AppTheme.System)
        {
            isLight = !IsWindowsInDarkMode();
        }

        IsLightActive = isLight;
        UpdateBrushes(isLight);
        ThemeChanged?.Invoke(CurrentTheme, isLight);
    }

    public static void ToggleTheme()
    {
        ApplyTheme(IsLightActive ? AppTheme.Dark : AppTheme.Light);
    }

    private static void UpdateBrushes(bool isLight)
    {
        var app = Application.Current;
        if (app == null) return;

        // Monochrome palette. Neutral grays keep hierarchy and contrast without
        // introducing an accent hue into the interface.
        var bgMain = isLight ? Color.FromRgb(247, 247, 247) : Color.FromRgb(11, 11, 11);
        var bgSidebar = isLight ? Color.FromRgb(238, 238, 238) : Color.FromRgb(17, 17, 17);
        var bgCard = isLight ? Color.FromRgb(255, 255, 255) : Color.FromRgb(24, 24, 24);
        var bgCardHover = isLight ? Color.FromRgb(232, 232, 232) : Color.FromRgb(36, 36, 36);
        var bgInput = isLight ? Color.FromRgb(255, 255, 255) : Color.FromRgb(18, 18, 18);
        var border = isLight ? Color.FromRgb(216, 216, 216) : Color.FromRgb(43, 43, 43);
        var borderHover = isLight ? Color.FromRgb(184, 184, 184) : Color.FromRgb(72, 72, 72);
        var textPrimary = isLight ? Color.FromRgb(17, 17, 17) : Color.FromRgb(245, 245, 245);
        var textSecondary = isLight ? Color.FromRgb(74, 74, 74) : Color.FromRgb(181, 181, 181);
        var textMuted = isLight ? Color.FromRgb(118, 118, 118) : Color.FromRgb(122, 122, 122);
        var accent = isLight ? Color.FromRgb(17, 17, 17) : Color.FromRgb(245, 245, 245);
        var accentSoft = isLight ? Color.FromRgb(64, 64, 64) : Color.FromRgb(214, 214, 214);
        var onAccent = isLight ? Color.FromRgb(255, 255, 255) : Color.FromRgb(11, 11, 11);

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
        SetResource(app.Resources, "BrushAccentRecording", new SolidColorBrush(accent));
        SetResource(app.Resources, "BrushAccentSuccess", new SolidColorBrush(accent));
        SetResource(app.Resources, "BrushAccentWarning", new SolidColorBrush(accentSoft));
        SetResource(app.Resources, "BrushOnAccent", new SolidColorBrush(onAccent));
        SetResource(app.Resources, "BrushWisprGradient", new SolidColorBrush(accent));
        SetResource(app.Resources, "BrushFlowPillBg", new SolidColorBrush(isLight ? Color.FromRgb(255, 255, 255) : Color.FromRgb(21, 21, 21)));

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
            SetResource(win.Resources, "BrushAccentRecording", new SolidColorBrush(accent));
            SetResource(win.Resources, "BrushAccentSuccess", new SolidColorBrush(accent));
            SetResource(win.Resources, "BrushAccentWarning", new SolidColorBrush(accentSoft));
            SetResource(win.Resources, "BrushOnAccent", new SolidColorBrush(onAccent));
            SetResource(win.Resources, "BrushWisprGradient", new SolidColorBrush(accent));
            SetResource(win.Resources, "BrushFlowPillBg", new SolidColorBrush(isLight ? Color.FromRgb(255, 255, 255) : Color.FromRgb(21, 21, 21)));
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
