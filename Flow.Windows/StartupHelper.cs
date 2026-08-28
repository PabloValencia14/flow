using System.Diagnostics;
using Microsoft.Win32;

namespace Flow.Windows;

public static class StartupHelper
{
    private const string KeyName = "FlowVoice";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsAutoStartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(KeyName) != null;
        }
        catch
        {
            return false;
        }
    }

    public static void SetAutoStart(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
            if (key == null) return;

            if (enable)
            {
                var exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exePath))
                {
                    key.SetValue(KeyName, $"\"{exePath}\" --background");
                }
            }
            else
            {
                key.DeleteValue(KeyName, false);
            }
        }
        catch { }
    }
}
