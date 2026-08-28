using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Flow.Windows;

public sealed class GlobalHotkey(Action callback) : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int ModAlt = 0x0001;
    private const int ModControl = 0x0002;
    private const int VkF12 = 0x7B;
    private const int HotkeyId = 0x464C;
    private HwndSource? _source;

    public void Register(nint handle)
    {
        if (!RegisterHotKey(handle, HotkeyId, ModControl | ModAlt, VkF12))
            throw new InvalidOperationException("Ctrl+Alt+F12 ya está siendo usado por otra aplicación.");
        _source = HwndSource.FromHwnd(handle);
        _source.AddHook(WndProc);
    }

    private nint WndProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            callback();
            handled = true;
        }
        return nint.Zero;
    }

    public void Dispose()
    {
        if (_source is not null)
        {
            UnregisterHotKey(_source.Handle, HotkeyId);
            _source.RemoveHook(WndProc);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);
}
