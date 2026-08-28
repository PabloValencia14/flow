using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Flow.Windows;

/// <summary>
/// Recognizes Ctrl+Win as either push-to-talk or a true double-tap gesture.
/// The short first tap is held pending briefly so it cannot become a second
/// recording before the gesture has been classified.
/// </summary>
public sealed class ShortcutHook : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeydown = 0x0100;
    private const int WmKeyup = 0x0101;
    private const int WmSyskeydown = 0x0104;
    private const int WmSyskeyup = 0x0105;

    private const int VkLcontrol = 0xA2;
    private const int VkRcontrol = 0xA3;
    private const int VkControl = 0x11;
    private const int VkLwin = 0x5B;
    private const int VkRwin = 0x5C;
    private const int VkEscape = 0x1B;

    private const int DoubleTapWindowMs = 450;
    private const int HoldThresholdMs = 320;

    private readonly LowLevelKeyboardProc _proc;
    private readonly object _stateLock = new();
    private nint _hookId = nint.Zero;
    private System.Threading.Timer? _gestureTimer;

    private bool _ctrlDown;
    private bool _winDown;
    private bool _comboActive;
    private bool _holdCandidate;
    private bool _continuousRecording;
    private bool _recordingSession;
    private bool _stopping;
    private bool _pendingStartTap;
    private bool _pendingStopTap;
    private bool _ignoreNextRelease;
    private DateTime _comboStartTime;
    private DateTime _pendingGestureDeadline;
    private bool _disposed;

    public event Action? StartRecording;
    public event Action? StopRecording;
    public event Action? CancelRecording;

    public ShortcutHook()
    {
        _proc = HookCallback;
        _hookId = SetHook(_proc);
        if (_hookId == nint.Zero)
            throw new InvalidOperationException("No se pudo activar el atajo global Ctrl+Win.");
    }

    private nint SetHook(LowLevelKeyboardProc proc)
    {
        using var currentProcess = Process.GetCurrentProcess();
        using var currentModule = currentProcess.MainModule;
        return SetWindowsHookEx(WhKeyboardLl, proc, GetModuleHandle(currentModule?.ModuleName), 0);
    }

    private nint HookCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0)
        {
            var vkCode = Marshal.ReadInt32(lParam);
            var isDown = wParam == WmKeydown || wParam == WmSyskeydown;
            var isUp = wParam == WmKeyup || wParam == WmSyskeyup;

            if (vkCode is VkControl or VkLcontrol or VkRcontrol)
            {
                ProcessModifier(isControl: true, isDown, isUp);
            }
            else if (vkCode is VkLwin or VkRwin)
            {
                ProcessModifier(isControl: false, isDown, isUp);
            }
            else if (isDown && vkCode == VkEscape)
            {
                RaiseCancelIfRecording();
            }
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private void ProcessModifier(bool isControl, bool isDown, bool isUp)
    {
        Action? callback = null;
        lock (_stateLock)
        {
            if (_disposed) return;

            var previous = isControl ? _ctrlDown : _winDown;
            if (isDown == previous) return; // Ignore keyboard auto-repeat.
            if (isControl) _ctrlDown = isDown;
            else _winDown = isDown;

            var bothPressed = _ctrlDown && _winDown;
            if (bothPressed && !_comboActive)
            {
                callback = ComboPressedLocked(DateTime.UtcNow);
            }
            else if (!bothPressed && _comboActive && isUp)
            {
                callback = ComboReleasedLocked(DateTime.UtcNow);
            }
        }

        callback?.Invoke();
    }

    private Action? ComboPressedLocked(DateTime now)
    {
        _comboActive = true;
        _comboStartTime = now;

        if (_stopping) return null;

        if (_continuousRecording)
        {
            // In hands-free mode, only a second tap within the window stops.
            if (_pendingStopTap && now <= _pendingGestureDeadline)
            {
                _pendingStopTap = false;
                _continuousRecording = false;
                _stopping = true;
                CancelGestureTimerLocked();
                _ignoreNextRelease = true;
                return StopRecording;
            }

            _pendingStopTap = true;
            _pendingGestureDeadline = now.AddMilliseconds(DoubleTapWindowMs);
            ScheduleGestureTimerLocked();
            return null;
        }

        if (_pendingStartTap && now <= _pendingGestureDeadline && _recordingSession)
        {
            // The first short tap already started capture; this second tap
            // confirms hands-free mode and must not start another capture.
            _pendingStartTap = false;
            _continuousRecording = true;
            CancelGestureTimerLocked();
            return null;
        }

        _pendingStartTap = false;
        _holdCandidate = true;
        _recordingSession = true;
        _continuousRecording = false;
        return StartRecording;
    }

    private Action? ComboReleasedLocked(DateTime now)
    {
        _comboActive = false;

        if (_ignoreNextRelease)
        {
            _ignoreNextRelease = false;
            return null;
        }

        if (_stopping || _continuousRecording) return null;
        if (!_holdCandidate) return null;

        _holdCandidate = false;
        var holdDuration = (now - _comboStartTime).TotalMilliseconds;
        if (holdDuration >= HoldThresholdMs)
        {
            _stopping = true;
            _pendingStartTap = false;
            CancelGestureTimerLocked();
            return StopRecording;
        }

        // A short first tap remains a single recording while we wait to see
        // whether the user completes a double tap.
        _pendingStartTap = true;
        _pendingGestureDeadline = now.AddMilliseconds(DoubleTapWindowMs);
        ScheduleGestureTimerLocked();
        return null;
    }

    private void OnGestureTimer()
    {
        Action? callback = null;
        lock (_stateLock)
        {
            if (_disposed) return;

            var now = DateTime.UtcNow;
            if (_pendingStartTap && now >= _pendingGestureDeadline)
            {
                _pendingStartTap = false;
                if (!_comboActive && !_continuousRecording && _recordingSession && !_stopping)
                {
                    _stopping = true;
                    callback = StopRecording;
                }
            }
            else if (_pendingStopTap && now >= _pendingGestureDeadline)
            {
                // No second tap: keep the hands-free recording active.
                _pendingStopTap = false;
            }

            if (_pendingStartTap || _pendingStopTap)
                ScheduleGestureTimerLocked();
            else
                CancelGestureTimerLocked();
        }

        callback?.Invoke();
    }

    private void ScheduleGestureTimerLocked()
    {
        _gestureTimer?.Dispose();
        var delay = Math.Max(1, (int)Math.Ceiling((_pendingGestureDeadline - DateTime.UtcNow).TotalMilliseconds));
        _gestureTimer = new Timer(_ => OnGestureTimer(), null, delay, Timeout.Infinite);
    }

    private void CancelGestureTimerLocked()
    {
        _gestureTimer?.Dispose();
        _gestureTimer = null;
    }

    private void RaiseCancelIfRecording()
    {
        Action? callback = null;
        lock (_stateLock)
        {
            if (_disposed || !_recordingSession || _stopping) return;
            _pendingStartTap = false;
            _pendingStopTap = false;
            _continuousRecording = false;
            _stopping = true;
            CancelGestureTimerLocked();
            callback = CancelRecording;
        }
        callback?.Invoke();
    }

    /// <summary>Synchronizes the gesture state after Flow finishes/cancels.</summary>
    public void NotifyExternalStop()
    {
        lock (_stateLock)
        {
            _recordingSession = false;
            _continuousRecording = false;
            _stopping = false;
            _holdCandidate = false;
            _pendingStartTap = false;
            _pendingStopTap = false;
            _ignoreNextRelease = false;
            CancelGestureTimerLocked();
        }
    }

    /// <summary>Marks a manually started capture as an active hands-free session.</summary>
    public void NotifyExternalStart()
    {
        lock (_stateLock)
        {
            _recordingSession = true;
            _continuousRecording = true;
            _stopping = false;
            _pendingStartTap = false;
            _pendingStopTap = false;
            CancelGestureTimerLocked();
        }
    }

    public void Dispose()
    {
        lock (_stateLock)
        {
            if (_disposed) return;
            _disposed = true;
            CancelGestureTimerLocked();
            _ctrlDown = false;
            _winDown = false;
            _comboActive = false;
        }

        if (_hookId != nint.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = nint.Zero;
        }
    }

    private delegate nint LowLevelKeyboardProc(int nCode, nint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern nint SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern nint GetModuleHandle(string? lpModuleName);
}
