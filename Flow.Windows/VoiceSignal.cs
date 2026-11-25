using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Flow.Windows;

/// <summary>
/// A small, self-contained visualizer. Capture threads only publish the peak;
/// the UI thread owns the entire animation and its drawing resources.
/// </summary>
public sealed class VoiceSignal : FrameworkElement
{
    private const int ShadeCount = 16;
    private readonly DispatcherTimer _frameTimer;
    private readonly Stopwatch _clock = new();
    private readonly SolidColorBrush[] _cores = new SolidColorBrush[ShadeCount];
    private readonly SolidColorBrush[] _trails = new SolidColorBrush[ShadeCount];
    private readonly RadialGradientBrush[] _halos = new RadialGradientBrush[ShadeCount];
    private float _target;
    private long _lastSample;
    private double _energy;
    private double _lastFrame;
    private double _phase;
    private bool _processing;
    private bool _reducedMotion;

    public Color SignalColor { get; set; } = Color.FromRgb(185, 233, 87);
    public Color TrailColor { get; set; } = Color.FromRgb(120, 217, 157);
    public Color PeakColor { get; set; } = Colors.White;
    public bool IsLightTheme { get; private set; }

    public VoiceSignal()
    {
        _frameTimer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(1000d / 60d)
        };
        _frameTimer.Tick += (_, _) => AdvanceFrame();
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) Stop();
        };
        IsHitTestVisible = false;
        BuildPalette();
    }

    public void BeginRecording()
    {
        _reducedMotion = !SystemParameters.ClientAreaAnimation;
        _processing = false;
        _energy = 0;
        _phase = 0;
        _lastFrame = 0;
        Volatile.Write(ref _target, 0);
        Interlocked.Exchange(ref _lastSample, 0);
        BuildPalette();
        _clock.Restart();
        _frameTimer.Start();
        InvalidateVisual();
    }

    public void PushLevel(float level)
    {
        // No Dispatcher work or WPF allocations on the NAudio callback.
        Volatile.Write(ref _target, float.IsFinite(level) ? Math.Clamp(level, 0f, 1f) : 0f);
        Interlocked.Exchange(ref _lastSample, Stopwatch.GetTimestamp());
    }

    public void BeginProcessing()
    {
        _processing = true;
        Volatile.Write(ref _target, 0f);
        if (!_clock.IsRunning)
        {
            _lastFrame = 0;
            _clock.Restart();
        }
        if (IsVisible) _frameTimer.Start();
        InvalidateVisual();
    }

    public void Stop()
    {
        _frameTimer.Stop();
        _clock.Stop();
        Volatile.Write(ref _target, 0f);
        _energy = 0;
        InvalidateVisual();
    }

    private void AdvanceFrame()
    {
        if (!IsVisible)
        {
            Stop();
            return;
        }

        var now = _clock.Elapsed.TotalSeconds;
        var dt = Math.Clamp(now - _lastFrame, 0.001, 0.1);
        _lastFrame = now;
        var lastSample = Interlocked.Read(ref _lastSample);
        var fresh = lastSample != 0 && Stopwatch.GetElapsedTime(lastSample).TotalSeconds < 0.24;
        var target = _processing || !fresh ? 0 : Math.Min(1.0, Math.Sqrt(Volatile.Read(ref _target)) * 1.65);
        // Fast attack, slower release: the envelope follows speech without jitter.
        var timeConstant = target > _energy ? 0.045 : 0.18;
        _energy += (target - _energy) * (1 - Math.Exp(-dt / timeConstant));
        if (!_reducedMotion) _phase += dt * (_processing ? 2.1 : 3.3 + 4.5 * _energy);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 8 || height < 8) return;

        var count = Math.Max(10, (int)(width / 5.1));
        var centerY = height / 2;
        var spread = _processing ? 1.0 + _energy * 4 : 1.3 + _energy * (_reducedMotion ? 4 : 11);
        for (var i = 0; i < count; i++)
        {
            var u = (i + 0.5) / count;
            var x = 3 + u * (width - 6);
            var envelope = Math.Pow(Math.Sin(Math.PI * u), 0.65);
            // Two offset streams of tabular light, with a travelling highlight.
            var wave = u * Math.Tau * 2.8 - _phase;
            var variation = 0.72 + 0.28 * Math.Sin(u * 12 + _phase * 0.36);
            var travel = 0.5 + 0.5 * Math.Cos(u * Math.Tau * 1.7 - _phase * 1.25);
            for (var strand = 0; strand < 2; strand++)
            {
                var angle = wave + strand * Math.PI + strand * 0.28;
                var depth = 0.5 + 0.5 * Math.Cos(angle);
                var y = centerY + Math.Sin(angle) * spread * envelope * variation;
                y += strand == 0 ? -0.35 : 0.35;
                var intensity = _processing
                    ? 0.19 + 0.58 * Math.Pow(travel, 4)
                    : (0.26 + 0.74 * depth) * (0.68 + 0.32 * travel) * (0.68 + 0.32 * _energy);
                var shade = Math.Clamp((int)Math.Round(intensity * (ShadeCount - 1)), 0, ShadeCount - 1);
                var size = 1.1 + depth * (0.75 + 1.25 * _energy);
                if (strand != 0) size *= 0.73;
                if (shade >= 6)
                {
                    var halo = size * 2.3;
                    dc.DrawEllipse(_halos[shade], null, new System.Windows.Point(x, y), halo, halo);
                }
                dc.DrawRoundedRectangle(strand == 0 ? _cores[shade] : _trails[shade], null,
                    new Rect(x - size / 2, y - size / 2, size, size), 0.35, 0.35);
            }
        }
    }

    public void ApplyTheme(bool isLight)
    {
        IsLightTheme = isLight;
        if (isLight)
        {
            SignalColor = Color.FromRgb(42, 115, 72);
            TrailColor = Color.FromRgb(85, 153, 111);
            PeakColor = Color.FromRgb(20, 59, 36);
        }
        else
        {
            SignalColor = Color.FromRgb(185, 233, 87);
            TrailColor = Color.FromRgb(120, 217, 157);
            PeakColor = Colors.White;
        }
        BuildPalette();
        InvalidateVisual();
    }

    private void BuildPalette()
    {
        var coreMinOpacity = IsLightTheme ? 0.30 : 0.18;
        var trailMinOpacity = IsLightTheme ? 0.18 : 0.10;
        var haloMaxAlpha = IsLightTheme ? 48.0 : 100.0;
        for (var i = 0; i < ShadeCount; i++)
        {
            var t = i / (double)(ShadeCount - 1);
            _cores[i] = Freeze(new SolidColorBrush(Mix(SignalColor, PeakColor, t * t * 0.86))
            {
                Opacity = coreMinOpacity + t * (1.0 - coreMinOpacity)
            });
            _trails[i] = Freeze(new SolidColorBrush(TrailColor)
            {
                Opacity = trailMinOpacity + t * (0.90 - trailMinOpacity)
            });
            _halos[i] = Freeze(new RadialGradientBrush(
                Color.FromArgb((byte)(t * haloMaxAlpha), SignalColor.R, SignalColor.G, SignalColor.B),
                Color.FromArgb(0, SignalColor.R, SignalColor.G, SignalColor.B)));
        }
    }

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t),
        (byte)(a.G + (b.G - a.G) * t),
        (byte)(a.B + (b.B - a.B) * t));

    private static T Freeze<T>(T value) where T : Freezable
    {
        value.Freeze();
        return value;
    }
}
