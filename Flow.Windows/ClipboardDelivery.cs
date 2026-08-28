using System.Runtime.InteropServices;
using System.Windows;

namespace Flow.Windows;

public sealed class ClipboardDelivery
{
    public async Task PasteAsync(string text, nint targetWindow = 0)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        IDataObject? previous = null;
        try
        {
            if (targetWindow != 0 && IsWindow(targetWindow))
            {
                ShowWindow(targetWindow, 9);
                SetForegroundWindow(targetWindow);
                await Task.Delay(80);
            }
            previous = Clipboard.GetDataObject();
            Clipboard.SetText(text);
            await Task.Delay(50);
            SendCtrlV();
            await Task.Delay(120);
        }
        finally
        {
            if (previous is not null)
            {
                try { Clipboard.SetDataObject(previous, true); }
                catch (COMException) { }
            }
        }
    }

    private static void SendCtrlV()
    {
        var inputs = new[]
        {
            KeyboardInput(0x11, 0), KeyboardInput(0x56, 0),
            KeyboardInput(0x56, 0x0002), KeyboardInput(0x11, 0x0002)
        };
        var inserted = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (inserted != inputs.Length)
        {
            var error = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"Windows no aceptó la inserción del texto (SendInput={inserted}, error={error}, INPUT={Marshal.SizeOf<INPUT>()} bytes).");
        }
    }

    private static INPUT KeyboardInput(ushort key, uint flags) => new()
    {
        type = 1,
        U = new INPUTUNION { ki = new KEYBDINPUT { wVk = key, dwFlags = flags } }
    };

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsWindow(nint handle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(nint handle);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint handle, int command);

    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public INPUTUNION U; }
    // En Win64 la unión nativa INPUT ocupa 32 bytes aunque KEYBDINPUT sea menor.
    [StructLayout(LayoutKind.Explicit, Size = 32)] private struct INPUTUNION { [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public nint dwExtraInfo; }
}
