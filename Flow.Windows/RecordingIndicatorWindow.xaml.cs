using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Flow.Windows;

public partial class RecordingIndicatorWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;

    private readonly ScaleTransform[] _bars;
    private readonly DispatcherTimer _durationTimer;
    private DateTime _recordStartTime;
    private int _levelTick;

    public event Action? CancelRequested;
    public event Action? FinishRequested;

    public RecordingIndicatorWindow()
    {
        InitializeComponent();
        _bars = [Bar1Scale, Bar2Scale, Bar3Scale, Bar4Scale, Bar5Scale, Bar6Scale, Bar7Scale];
        _durationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _durationTimer.Tick += (_, _) => UpdateTimer();
        SourceInitialized += OnSourceInitialized;
        PositionBottomCenter();
    }

    public void ShowRecording()
    {
        _durationTimer.Stop();
        _recordStartTime = DateTime.UtcNow;
        DurationLabel.Text = "0:00";
        WaveBarsContainer.Visibility = Visibility.Visible;
        PulseDot.Fill = (SolidColorBrush)FindResource("BrushAccentRecording");
        PulseRing.Fill = (SolidColorBrush)FindResource("BrushAccentRecording");

        PositionBottomCenter();
        StartPulseAnimation();
        SetAudioLevel(0.05f);
        _durationTimer.Start();

        AnimateIn();
    }

    public void ShowProcessing()
    {
        _durationTimer.Stop();
        DurationLabel.Text = "Transcribiendo…";
        WaveBarsContainer.Visibility = Visibility.Collapsed;
        PulseDot.Fill = (SolidColorBrush)FindResource("BrushAccentWarning");
        PulseRing.Fill = (SolidColorBrush)FindResource("BrushAccentWarning");
    }

    public async Task ShowSuccessAsync()
    {
        _durationTimer.Stop();
        DurationLabel.Text = "Insertado ✓";
        WaveBarsContainer.Visibility = Visibility.Collapsed;
        PulseDot.Fill = (SolidColorBrush)FindResource("BrushAccentSuccess");
        PulseRing.Fill = (SolidColorBrush)FindResource("BrushAccentSuccess");

        await Task.Delay(750);
        AnimateOut();
    }

    public void HideImmediate()
    {
        _durationTimer.Stop();
        Hide();
    }

    private void AnimateIn()
    {
        Show();
        var scaleAnim = new DoubleAnimation(0.7, 1.0, new Duration(TimeSpan.FromMilliseconds(220)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var opacityAnim = new DoubleAnimation(0.0, 1.0, new Duration(TimeSpan.FromMilliseconds(180)));
        var transAnim = new DoubleAnimation(18, 0, new Duration(TimeSpan.FromMilliseconds(220)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        PillScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
        PillScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
        PillTranslate.BeginAnimation(TranslateTransform.YProperty, transAnim);
        BeginAnimation(OpacityProperty, opacityAnim);
    }

    public void AnimateOut()
    {
        var scaleAnim = new DoubleAnimation(1.0, 0.7, new Duration(TimeSpan.FromMilliseconds(180)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        var opacityAnim = new DoubleAnimation(1.0, 0.0, new Duration(TimeSpan.FromMilliseconds(180)));
        opacityAnim.Completed += (_, _) => Hide();

        PillScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
        PillScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
        BeginAnimation(OpacityProperty, opacityAnim);
    }

    private void UpdateTimer()
    {
        var elapsed = DateTime.UtcNow - _recordStartTime;
        DurationLabel.Text = $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}";
    }

    private void StartPulseAnimation()
    {
        var scaleAnim = new DoubleAnimation(1.0, 1.8, new Duration(TimeSpan.FromMilliseconds(800)))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        };
        var opacityAnim = new DoubleAnimation(0.4, 0.05, new Duration(TimeSpan.FromMilliseconds(800)))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        };
        PulseRingScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
        PulseRingScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
        PulseRing.BeginAnimation(OpacityProperty, opacityAnim);
    }

    public void SetAudioLevel(float level)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => SetAudioLevel(level));
            return;
        }

        if (!IsVisible) return;
        var intensity = Math.Clamp((float)Math.Sqrt(Math.Clamp(level, 0f, 1f)) * 2.8f, 0f, 1.3f);
        var phase = ++_levelTick;
        var factors = new[] { 0.45f, 0.75f, 1.05f, 1.25f, 1.05f, 0.75f, 0.45f };
        var motion = new[]
        {
            0.03f * (float)Math.Sin(phase * 0.71),
            0.05f * (float)Math.Sin(phase * 0.53 + 1.1),
            0.06f * (float)Math.Sin(phase * 0.43 + 2.0),
            0.08f * (float)Math.Sin(phase * 0.61 + 0.5),
            0.06f * (float)Math.Sin(phase * 0.43 + 1.8),
            0.05f * (float)Math.Sin(phase * 0.53 + 2.4),
            0.03f * (float)Math.Sin(phase * 0.71 + 3.1)
        };

        for (var i = 0; i < _bars.Length; i++)
            _bars[i].ScaleY = Math.Clamp(0.2f + intensity * factors[i] + motion[i], 0.2f, 1.3f);
    }

    private void PositionBottomCenter()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Left + (workArea.Width - Width) / 2;
        Top = workArea.Top + workArea.Height - Height - 12;
    }

    private void PillBorder_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e) => CancelRequested?.Invoke();
    private void FinishBtn_Click(object sender, RoutedEventArgs e) => FinishRequested?.Invoke();

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var style = GetWindowLong(handle, GwlExStyle);
        SetWindowLong(handle, GwlExStyle, style | WsExNoActivate | WsExToolWindow);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
    private static extern nint GetWindowLong(nint handle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static extern nint SetWindowLong(nint handle, int index, nint value);
}
