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
    private bool _wordByWordPreview = true;
    private string _lastPreviewText = string.Empty;
    private int _visualGeneration;
    private volatile bool _acceptAudioLevels;
    private string _indicatorState = "idle";

    public event Action? CancelRequested;
    public event Action? FinishRequested;

    public RecordingIndicatorWindow()
    {
        InitializeComponent();
        _durationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _durationTimer.Tick += (_, _) => UpdateTimer();
        SourceInitialized += OnSourceInitialized;
        ThemeManager.ThemeChanged += OnThemeChanged;
        Closed += (_, _) =>
        {
            ThemeManager.ThemeChanged -= OnThemeChanged;
            StopVisuals();
        };
        ApplyTheme(ThemeManager.IsLightActive);
        PositionBottomCenter();
    }

    private void OnThemeChanged(AppTheme _, bool isLight)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => ApplyTheme(isLight));
            return;
        }
        ApplyTheme(isLight);
    }

    public void ApplyTheme(bool isLight)
    {
        PillShadow.Opacity = isLight ? 0.18 : 0.38;
        WaveBarsContainer.ApplyTheme(isLight);
        var indicatorBrushKey = _indicatorState switch
        {
            "processing" => "BrushAccentWarning",
            "success" => "BrushAccentSuccess",
            _ => "BrushIslandInk"
        };
        var indicatorBrush = (SolidColorBrush)FindResource(indicatorBrushKey);
        PulseDot.Fill = indicatorBrush;
        PulseRing.Fill = indicatorBrush;
        if (IsVisible && _lastPreviewText.Length > 0)
            RenderTranscriptPreview(_lastPreviewText, _lastPreviewText, settled: _indicatorState == "success");
    }

    public void ShowRecording(bool showTranscriptPreview = false, bool wordByWordPreview = true)
    {
        ++_visualGeneration;
        StopVisuals();
        _indicatorState = "recording";
        PillBorder.BeginAnimation(FrameworkElement.WidthProperty, null);
        PillScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        PillScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        PillTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        BeginAnimation(OpacityProperty, null);
        _recordStartTime = DateTime.UtcNow;
        _showTranscriptPreview = showTranscriptPreview;
        _wordByWordPreview = wordByWordPreview;
        _lastPreviewText = string.Empty;
        DurationLabel.Text = "0:00";
        Width = 384;
        Height = 184;
        PillBorder.Width = 344;
        PillBorder.MinHeight = 52;
        TranscriptPreviewText.Inlines.Clear();
        TranscriptPreviewText.BeginAnimation(OpacityProperty, null);
        TranscriptRise.BeginAnimation(TranslateTransform.YProperty, null);
        TranscriptPreviewScroll.Visibility = Visibility.Collapsed;
        WaveBarsContainer.Visibility = Visibility.Visible;
        WaveBarsContainer.Opacity = 1;
        WaveBarsContainer.Width = 224;
        ApplyTheme(ThemeManager.IsLightActive);

        PositionBottomCenter();
        AnimateIn();
        StartPulseAnimation();
        WaveBarsContainer.BeginRecording();
        _acceptAudioLevels = true;
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
        if (preview == _lastPreviewText) return;
        var wasExpanded = TranscriptPreviewScroll.Visibility == Visibility.Visible;
        RenderTranscriptPreview(preview, _lastPreviewText);
        _lastPreviewText = preview;

        if (!wasExpanded)
            ExpandForTranscriptPreview();

        if (!wasExpanded && SystemParameters.ClientAreaAnimation)
            AnimateTranscriptEntry();

        TranscriptPreviewScroll.ScrollToEnd();
    }

    public void ShowProcessing(bool localTranscription = false)
    {
        _indicatorState = "processing";
        _acceptAudioLevels = false;
        _durationTimer.Stop();
        DurationLabel.Text = localTranscription ? "Procesando" : "Transcribiendo";
        WaveBarsContainer.Width = 124;
        WaveBarsContainer.BeginProcessing();
        PulseDot.Fill = (SolidColorBrush)FindResource("BrushAccentWarning");
        PulseRing.Fill = (SolidColorBrush)FindResource("BrushAccentWarning");
    }

    public async Task ShowSuccessAsync()
    {
        var generation = _visualGeneration;
        _indicatorState = "success";
        StopVisuals();
        _showTranscriptPreview = false;
        if (!IsVisible) return;
        if (_lastPreviewText.Length > 0)
            RenderTranscriptPreview(_lastPreviewText, _lastPreviewText, settled: true);
        DurationLabel.Text = "[OK]";
        WaveBarsContainer.Visibility = Visibility.Collapsed;
        PulseDot.Fill = (SolidColorBrush)FindResource("BrushAccentSuccess");
        PulseRing.Fill = (SolidColorBrush)FindResource("BrushAccentSuccess");

        await Task.Delay(750);
        if (generation == _visualGeneration) AnimateOut();
    }

    public void HideImmediate()
    {
        ++_visualGeneration;
        StopVisuals();
        _showTranscriptPreview = false;
        Hide();
    }

    private void AnimateIn()
    {
        Show();
        if (!SystemParameters.ClientAreaAnimation)
        {
            PillScale.ScaleX = PillScale.ScaleY = Opacity = 1;
            PillTranslate.Y = 0;
            return;
        }
        var scaleAnim = new DoubleAnimation(0.52, 1.0, new Duration(TimeSpan.FromMilliseconds(350)))
        {
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.34 }
        };
        var heightAnim = new DoubleAnimation(0.85, 1.0, new Duration(TimeSpan.FromMilliseconds(300)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var opacityAnim = new DoubleAnimation(0.55, 1.0, new Duration(TimeSpan.FromMilliseconds(180)));
        var transAnim = new DoubleAnimation(5, 0, new Duration(TimeSpan.FromMilliseconds(300)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        PillScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
        PillScale.BeginAnimation(ScaleTransform.ScaleYProperty, heightAnim);
        PillTranslate.BeginAnimation(TranslateTransform.YProperty, transAnim);
        BeginAnimation(OpacityProperty, opacityAnim);
    }

    public void AnimateOut()
    {
        var generation = ++_visualGeneration;
        StopVisuals();
        _showTranscriptPreview = false;
        if (!IsVisible) return;
        if (!SystemParameters.ClientAreaAnimation)
        {
            Hide();
            return;
        }
        var scaleAnim = new DoubleAnimation(PillScale.ScaleX, 0.55, new Duration(TimeSpan.FromMilliseconds(190)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        var heightAnim = new DoubleAnimation(PillScale.ScaleY, 0.84, new Duration(TimeSpan.FromMilliseconds(190)));
        var opacityAnim = new DoubleAnimation(Opacity, 0.0, new Duration(TimeSpan.FromMilliseconds(190)));
        opacityAnim.Completed += (_, _) =>
        {
            if (generation == _visualGeneration) Hide();
        };

        PillScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
        PillScale.BeginAnimation(ScaleTransform.ScaleYProperty, heightAnim);
        BeginAnimation(OpacityProperty, opacityAnim);
    }

    private void StopVisuals()
    {
        _acceptAudioLevels = false;
        _durationTimer.Stop();
        WaveBarsContainer.Stop();
        PulseRingScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        PulseRingScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        PulseRing.BeginAnimation(OpacityProperty, null);
    }

    private void UpdateTimer()
    {
        var elapsed = DateTime.UtcNow - _recordStartTime;
        DurationLabel.Text = $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}";
    }

    private void StartPulseAnimation()
    {
        if (!SystemParameters.ClientAreaAnimation) return;
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
        if (_acceptAudioLevels) WaveBarsContainer.PushLevel(level);
    }

    private void RenderTranscriptPreview(string text, string previous, bool settled = false)
    {
        TranscriptPreviewText.Inlines.Clear();
        var end = text.TrimEnd().Length;
        if (!_wordByWordPreview)
        {
            var blockCommon = 0;
            while (blockCommon < Math.Min(previous.Length, end) && previous[blockCommon] == text[blockCommon])
                blockCommon++;
            while (blockCommon > 0 && blockCommon < end && !char.IsWhiteSpace(text[blockCommon - 1]))
                blockCommon--;
            if (blockCommon > 0) AppendPreviewRun(text[..blockCommon], false);
            if (blockCommon < end) AppendPreviewRun(text[blockCommon..end], false, settled ? -1 : 0);
            return;
        }
        var pendingStart = end;
        if (!settled)
        {
            while (pendingStart > 0 && !char.IsWhiteSpace(text[pendingStart - 1]))
                pendingStart--;
        }

        // Keep unchanged words entirely still, including when a local model
        // revises the last sentence. Only new or edited words fade in.
        var common = 0;
        while (common < Math.Min(previous.Length, pendingStart) && previous[common] == text[common])
            common++;
        while (common > 0 && common < pendingStart && !char.IsWhiteSpace(text[common - 1]))
            common--;
        if (common > 0) AppendPreviewRun(text[..common], false);

        var start = common;
        var word = 0;
        while (start < pendingStart)
        {
            var next = start;
            while (next < pendingStart && !char.IsWhiteSpace(text[next])) next++;
            while (next < pendingStart && char.IsWhiteSpace(text[next])) next++;
            AppendPreviewRun(text[start..next], false, settled || word >= 6 ? -1 : word);
            word++;
            start = next;
        }
        if (pendingStart < end)
        {
            var pending = text[pendingStart..end];
            var previousTrimmed = previous.TrimEnd();
            var previousStart = previousTrimmed.Length;
            while (previousStart > 0 && !char.IsWhiteSpace(previousTrimmed[previousStart - 1]))
                previousStart--;
            var unchanged = previousTrimmed[previousStart..] == pending;
            AppendPreviewRun(pending, true, unchanged ? -1 : Math.Min(word, 5));
        }
    }

    private void AppendPreviewRun(string text, bool pending, int animationIndex = -1)
    {
        var resource = (SolidColorBrush)FindResource(pending ? "BrushIslandPending" : "BrushIslandText");
        if (animationIndex < 0 || !SystemParameters.ClientAreaAnimation)
        {
            TranscriptPreviewText.Inlines.Add(new Run(text) { Foreground = resource });
            return;
        }
        var brush = new SolidColorBrush(resource.Color) { Opacity = 0.16 };
        TranscriptPreviewText.Inlines.Add(new Run(text) { Foreground = brush });
        var animation = new DoubleAnimation(0.16, 1, new Duration(TimeSpan.FromMilliseconds(220)))
        {
            BeginTime = TimeSpan.FromMilliseconds(animationIndex * 38),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        animation.Completed += (_, _) =>
        {
            brush.Opacity = 1;
            brush.BeginAnimation(SolidColorBrush.OpacityProperty, null);
        };
        brush.BeginAnimation(SolidColorBrush.OpacityProperty, animation);
    }

    private void ExpandForTranscriptPreview()
    {
        TranscriptPreviewScroll.Visibility = Visibility.Visible;
        PillBorder.MinHeight = 80;
        if (SystemParameters.ClientAreaAnimation)
        {
            var scaleY = new DoubleAnimation(0.76, 1, new Duration(TimeSpan.FromMilliseconds(240)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            PillScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleY);
        }
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
