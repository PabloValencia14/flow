using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Documents;
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

    private readonly DispatcherTimer _durationTimer;
    private DateTime _recordStartTime;
    private bool _showTranscriptPreview;
    private string _lastPreviewText = string.Empty;
    private int _audioFrame;
    private volatile bool _acceptAudioLevels;

    public event Action? CancelRequested;
    public event Action? FinishRequested;

    public RecordingIndicatorWindow()
    {
        InitializeComponent();
        _durationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _durationTimer.Tick += (_, _) => UpdateTimer();
        SourceInitialized += OnSourceInitialized;
        PositionBottomCenter();
    }

    public void ShowRecording(bool showTranscriptPreview = false)
    {
        _acceptAudioLevels = true;
        _durationTimer.Stop();
        PillBorder.BeginAnimation(FrameworkElement.WidthProperty, null);
        PillScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        PillScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        PillTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        BeginAnimation(OpacityProperty, null);
        _recordStartTime = DateTime.UtcNow;
        _showTranscriptPreview = showTranscriptPreview;
        _lastPreviewText = string.Empty;
        _audioFrame = 0;
        DurationLabel.Text = "0:00";
        Width = 384;
        Height = 88;
        PillBorder.Width = 344;
        PillBorder.MinHeight = 52;
        TranscriptPreviewText.Inlines.Clear();
        TranscriptPreviewScroll.Visibility = Visibility.Collapsed;
        WaveBarsContainer.Visibility = Visibility.Visible;
        WaveBarsContainer.Opacity = 1;
        WaveBarsContainer.Foreground = (SolidColorBrush)FindResource("BrushIslandGlow");
        PulseDot.Fill = (SolidColorBrush)FindResource("BrushIslandInk");
        PulseRing.Fill = (SolidColorBrush)FindResource("BrushIslandInk");

        PositionBottomCenter();
        AnimateIn();
        StartPulseAnimation();
        SetAudioLevel(0.05f);
        _durationTimer.Start();
    }

    public void SetTranscriptPreview(string text)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => SetTranscriptPreview(text));
            return;
        }

        if (!IsVisible || !_showTranscriptPreview || string.IsNullOrWhiteSpace(text)) return;
        const int previewLimit = 900;
        var preview = text.Length > previewLimit ? $"...{text[^previewLimit..]}" : text;
        var previousWordCount = CountWords(_lastPreviewText);
        var wasExpanded = TranscriptPreviewScroll.Visibility == Visibility.Visible;
        _lastPreviewText = preview;
        RenderTranscriptPreview(preview);

        if (!wasExpanded)
            ExpandForTranscriptPreview();

        if (!wasExpanded || CountWords(preview) > previousWordCount)
            AnimateTranscriptEntry();

        TranscriptPreviewScroll.ScrollToEnd();
    }

    public void ShowProcessing(bool localTranscription = false)
    {
        _acceptAudioLevels = false;
        _durationTimer.Stop();
        DurationLabel.Text = localTranscription ? "Procesando" : "Transcribiendo";
        WaveBarsContainer.Foreground = (SolidColorBrush)FindResource("BrushIslandPending");
        WaveBarsContainer.Width = 124;
        RenderAsciiRope(0.12f, 24);
        PulseDot.Fill = (SolidColorBrush)FindResource("BrushAccentWarning");
        PulseRing.Fill = (SolidColorBrush)FindResource("BrushAccentWarning");
    }

    public async Task ShowSuccessAsync()
    {
        _acceptAudioLevels = false;
        _durationTimer.Stop();
        DurationLabel.Text = "[OK]";
        WaveBarsContainer.Visibility = Visibility.Collapsed;
        PulseDot.Fill = (SolidColorBrush)FindResource("BrushAccentSuccess");
        PulseRing.Fill = (SolidColorBrush)FindResource("BrushAccentSuccess");

        await Task.Delay(750);
        AnimateOut();
    }

    public void HideImmediate()
    {
        _acceptAudioLevels = false;
        _durationTimer.Stop();
        Hide();
    }

    private void AnimateIn()
    {
        Show();
        var scaleAnim = new DoubleAnimation(0.82, 1.0, new Duration(TimeSpan.FromMilliseconds(260)))
        {
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.22 }
        };
        var opacityAnim = new DoubleAnimation(0.0, 1.0, new Duration(TimeSpan.FromMilliseconds(200)));
        var transAnim = new DoubleAnimation(12, 0, new Duration(TimeSpan.FromMilliseconds(260)))
        {
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.18 }
        };

        PillScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
        PillScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
        PillTranslate.BeginAnimation(TranslateTransform.YProperty, transAnim);
        BeginAnimation(OpacityProperty, opacityAnim);
    }

    public void AnimateOut()
    {
        _acceptAudioLevels = false;
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
            if (!_acceptAudioLevels) return;
            Dispatcher.BeginInvoke(() => SetAudioLevel(level));
            return;
        }

        if (!_acceptAudioLevels || !IsVisible || WaveBarsContainer.Visibility != Visibility.Visible) return;
        WaveBarsContainer.Width = 224;
        WaveBarsContainer.Foreground = (SolidColorBrush)FindResource("BrushIslandGlow");
        RenderAsciiRope(level, 47);
    }

    private void RenderAsciiRope(float level, int columns)
    {
        var baseColor = ((SolidColorBrush)WaveBarsContainer.Foreground).Color;
        var energy = Math.Clamp(Math.Sqrt(Math.Clamp(level, 0f, 1f)) * 2.1, 0.22, 1.0);
        var phase = ++_audioFrame * 0.37;
        WaveBarsContainer.Inlines.Clear();

        for (var i = 0; i < columns; i++)
        {
            var position = i * 0.47;
            var contour = 0.5 + 0.5 * Math.Sin(position + phase);
            var grain = 0.5 + 0.5 * Math.Sin(i * 1.83 - phase * 1.31 + Math.Sin(i * 0.17));
            var amplitude = energy * (0.14 + contour * 0.52 + grain * 0.34);
            var glyph = grain > 0.97 && amplitude > 0.3 ? '*' : amplitude > 0.54 ? ':' : '.';

            // Keep the rope airy at low volume; louder audio adds brighter
            // points and pushes them farther above or below the center line.
            if (grain < 0.15 * (1 - energy) + 0.06)
                glyph = ' ';

            var vertical = Math.Sin(i * 0.39 + phase * 1.12) * energy * (0.15 + contour * 0.85);
            var baseline = Math.Abs(vertical) < 0.18
                ? BaselineAlignment.Center
                : vertical < 0 ? BaselineAlignment.Top : BaselineAlignment.Bottom;

            var alpha = (byte)Math.Clamp(180 + amplitude * 75, 0, 255);
            var brush = new SolidColorBrush(Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B));
            brush.Freeze();

            WaveBarsContainer.Inlines.Add(new Run(glyph.ToString())
            {
                BaselineAlignment = baseline,
                FontSize = 7.3 + amplitude * 2.2,
                Foreground = brush
            });
        }
    }

    private void RenderTranscriptPreview(string text)
    {
        TranscriptPreviewText.Inlines.Clear();
        var end = text.TrimEnd().Length;
        var lastWordStart = end;
        while (lastWordStart > 0 && !char.IsWhiteSpace(text[lastWordStart - 1]))
            lastWordStart--;

        var stableText = text[..lastWordStart];
        var pendingWord = text[lastWordStart..end];
        if (stableText.Length > 0)
        {
            TranscriptPreviewText.Inlines.Add(new Run(stableText)
            {
                Foreground = (SolidColorBrush)FindResource("BrushIslandText")
            });
        }

        if (pendingWord.Length > 0)
        {
            TranscriptPreviewText.Inlines.Add(new Run(pendingWord)
            {
                Foreground = (SolidColorBrush)FindResource("BrushIslandPending")
            });
        }
    }

    private static int CountWords(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private void ExpandForTranscriptPreview()
    {
        TranscriptPreviewScroll.Visibility = Visibility.Visible;
        Width = 384;
        Height = 160;
        PillBorder.Width = 344;
        PillBorder.MinHeight = 80;
        WaveBarsContainer.Width = 224;
        PositionBottomCenter();

        var scaleY = new DoubleAnimation(0.94, 1, new Duration(TimeSpan.FromMilliseconds(260)))
        {
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.18 }
        };
        PillScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleY);
    }

    private void AnimateTranscriptEntry()
    {
        TranscriptPreviewText.BeginAnimation(OpacityProperty, new DoubleAnimation(0.62, 1, new Duration(TimeSpan.FromMilliseconds(170)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        TranscriptRise.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(4, 0, new Duration(TimeSpan.FromMilliseconds(190)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
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
