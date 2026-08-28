using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using NAudio.Wave;

namespace Flow.Windows;

public partial class MainWindow : Window
{
    private readonly FlowClient _client;
    private readonly ShortcutHook _shortcut;
    private readonly DispatcherTimer _syncTimer;
    private readonly DispatcherTimer _foregroundTimer;
    private readonly RecordingIndicatorWindow _flowBar;
    private readonly System.Windows.Forms.NotifyIcon _trayIcon;
    private readonly bool _startInBackground;
    private nint _lastExternalWindow;
    private string? _lastExternalAppName;
    private string _currentNoteFilter = "all";
    private bool _isClosingFromApp;
    private bool _loadingTheme = true;
    private bool _loadingMicrophone = true;
    private bool _loadingSounds = true;
    // XAML can raise Checked/Unchecked while InitializeComponent is still
    // wiring named controls. Ignore those early events until Loaded.
    private bool _loadingCorrectionOptions = true;
    private bool _loadingStyleOptions = true;
    private DictationStyleSettings _styleSettings = new();
    private MeetingHistoryItem? _selectedMeeting;
    private AudioFileReader? _meetingReader;
    private WaveOutEvent? _meetingOutput;

    public MainWindow()
    {
        _client = new FlowClient(UpdateStatus);
        _shortcut = new ShortcutHook();
        _flowBar = new RecordingIndicatorWindow();

        InitializeComponent();
        _startInBackground = Environment.GetCommandLineArgs().Any(a =>
            a.Equals("--background", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("-b", StringComparison.OrdinalIgnoreCase));
        ShowInTaskbar = !_startInBackground;
        if (_startInBackground)
        {
            // Hide before the asynchronous Loaded work starts so startup never
            // flashes the full panel when launched by Windows.
            SourceInitialized += (_, _) => HideToTray();
        }
        _trayIcon = CreateTrayIcon();

        // Connect audio levels to floating Dynamic Island recorder graph
        _client.AudioLevelChanged += level => _flowBar.SetAudioLevel(level);

        // Connect shortcut hook events (Push-to-Talk and Double-Tap with Ctrl + Win)
        _shortcut.StartRecording += () =>
        {
            Dispatcher.InvokeAsync(async () => await StartDictationFlowAsync(fromShortcut: true));
        };

        _shortcut.StopRecording += () =>
        {
            Dispatcher.InvokeAsync(async () => await FinishDictationAsync());
        };

        _shortcut.CancelRecording += () =>
        {
            Dispatcher.Invoke(() =>
            {
                _client.CancelDictation();
                _flowBar.AnimateOut();
                _shortcut.NotifyExternalStop();
            });
        };

        // Connect Flow Bar manual buttons
        _flowBar.CancelRequested += () =>
        {
            _client.CancelDictation();
            _flowBar.AnimateOut();
            _shortcut.NotifyExternalStop();
        };

        _flowBar.FinishRequested += async () =>
        {
            await FinishDictationAsync();
            _shortcut.NotifyExternalStop();
        };

        ThemeManager.ThemeChanged += (mode, isLight) =>
        {
            Dispatcher.Invoke(async () =>
            {
                UpdateThemeRadios(mode);
                UpdateThemeIcon(isLight);
                await LoadNotesAsync();
                await LoadSnippetsAsync();
                await LoadDictionaryAsync();
                await LoadMeetingsAsync();
            });
        };

        _syncTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _syncTimer.Tick += async (_, _) =>
        {
            var sync = await _client.SyncPendingAsync();
            if (sync.Pulled > 0)
            {
                await LoadNotesAsync();
                await LoadSnippetsAsync();
                await LoadCorrectionOptionsAsync();
                await LoadStyleSettingsAsync();
                await UpdateStatsAsync();
            }
        };

        _foregroundTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _foregroundTimer.Tick += (_, _) => TrackExternalForegroundWindow();

        DbPathLabel.Text = _client.LocalDatabasePath;

        Loaded += async (_, _) =>
        {
            var savedTheme = await _client.Outbox.GetSettingAsync(FlowSettingKeys.AppTheme, "dark");
            var mode = savedTheme switch
            {
                "light" => AppTheme.Light,
                "system" => AppTheme.System,
                _ => AppTheme.Dark
            };
            _loadingTheme = true;
            try
            {
                ThemeManager.ApplyTheme(mode);
                UpdateThemeRadios(mode);
            }
            finally { _loadingTheme = false; }
            UpdateThemeIcon(ThemeManager.IsLightActive);

            var savedMicrophone = await _client.Outbox.GetSettingAsync(FlowSettingKeys.AudioInputDevice);
            PopulateMicrophones(savedMicrophone);
            ToggleAutoStart.IsChecked = StartupHelper.IsAutoStartEnabled();
            _loadingSounds = true;
            try
            {
                var savedSounds = await _client.Outbox.GetSettingAsync(FlowSettingKeys.SoundsEnabled, "true");
                ToggleSounds.IsChecked = !string.Equals(savedSounds, "false", StringComparison.OrdinalIgnoreCase);
                SoundManager.SoundEnabled = ToggleSounds.IsChecked == true;
            }
            finally { _loadingSounds = false; }

            await LoadCorrectionOptionsAsync();
            await LoadStyleSettingsAsync();

            ServerUrlInput.Text = await _client.GetSyncServerUrlAsync() ?? string.Empty;
            ServerTokenInput.Text = _client.HasFlowHubToken ? "●●●●●●●●" : string.Empty;

            await LoadNotesAsync();
            await LoadSnippetsAsync();
            await LoadDictionaryAsync();
            await LoadMeetingsAsync();
            await UpdateStatsAsync();
            var startupSync = await _client.SyncPendingAsync();
            if (startupSync.Pulled > 0)
            {
                await LoadNotesAsync();
                await LoadSnippetsAsync();
                await LoadCorrectionOptionsAsync();
                await LoadStyleSettingsAsync();
                await UpdateStatsAsync();
                await LoadMeetingsAsync();
            }

            ShowInTaskbar = !_startInBackground;
            if (_startInBackground)
            {
                // Keep the process, global shortcut, tray icon and recorder
                // bar alive without leaving a visible window or taskbar button.
                _ = Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(HideToTray));
            }
        };

        Closed += async (_, _) =>
        {
            _isClosingFromApp = true;
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _syncTimer.Stop();
            _foregroundTimer.Stop();
            _shortcut.Dispose();
            StopMeetingPlayback();
            _flowBar.Close();
            await _client.DisposeAsync();
        };

        _syncTimer.Start();
        _foregroundTimer.Start();
    }

    #region Window Chrome & Navigation
    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private async void ThemeToggleBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ThemeManager.ToggleTheme();
            if (_client?.Outbox != null)
                await _client.Outbox.SetSettingAsync(FlowSettingKeys.AppTheme, ThemeManager.CurrentTheme.ToString().ToLowerInvariant());
        }
        catch (Exception error) { UpdateStatus($"No se pudo guardar el tema: {error.Message}"); }
    }

    private async void ThemeRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_loadingTheme || _client?.Outbox == null || sender is not System.Windows.Controls.RadioButton radio || radio.IsChecked != true) return;
        var mode = ReferenceEquals(radio, ThemeLightRadio)
            ? AppTheme.Light
            : ReferenceEquals(radio, ThemeSystemRadio)
                ? AppTheme.System
                : AppTheme.Dark;
        try
        {
            ThemeManager.ApplyTheme(mode);
            await _client.Outbox.SetSettingAsync(FlowSettingKeys.AppTheme, mode.ToString().ToLowerInvariant());
        }
        catch (Exception error) { UpdateStatus($"No se pudo guardar el tema: {error.Message}"); }
    }

    private void UpdateThemeRadios(AppTheme mode)
    {
        if (ThemeDarkRadio == null || ThemeLightRadio == null || ThemeSystemRadio == null) return;
        _loadingTheme = true;
        try
        {
            ThemeDarkRadio.IsChecked = mode == AppTheme.Dark;
            ThemeLightRadio.IsChecked = mode == AppTheme.Light;
            ThemeSystemRadio.IsChecked = mode == AppTheme.System;
        }
        finally { _loadingTheme = false; }
    }

    private void UpdateThemeIcon(bool isLight)
    {
        if (ThemeToggleBtn?.Template?.FindName("ThemeIconPath", ThemeToggleBtn) is System.Windows.Shapes.Path icon)
        {
            icon.Data = isLight ? (Geometry)FindResource("IconMoon") : (Geometry)FindResource("IconSun");
            ThemeToggleBtn.ToolTip = isLight ? "Cambiar a modo oscuro" : "Cambiar a modo claro";
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => HideToTray();

    private void HideToTray()
    {
        ShowInTaskbar = false;
        WindowState = WindowState.Normal;
        Hide();
    }

    private System.Windows.Forms.NotifyIcon CreateTrayIcon()
    {
        var icon = LoadTrayIcon();
        var tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = icon,
            Visible = true,
            Text = "Flow · Dictado por voz"
        };

        tray.MouseClick += (_, args) =>
        {
            if (args.Button == System.Windows.Forms.MouseButtons.Left)
                Dispatcher.BeginInvoke(new Action(OpenPanelFromTray));
        };

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Abrir Flow", null, (_, _) => Dispatcher.BeginInvoke(new Action(OpenPanelFromTray)));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Salir de Flow", null, (_, _) => Dispatcher.BeginInvoke(new Action(ExitFromTray)));
        tray.ContextMenuStrip = menu;
        return tray;
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var resource = System.Windows.Application.GetResourceStream(new Uri(
                "pack://application:,,,/Flow.Windows;component/FlowLogo.ico", UriKind.Absolute));
            if (resource is not null)
            {
                using var stream = resource.Stream;
                using var source = new System.Drawing.Icon(stream);
                return (System.Drawing.Icon)source.Clone();
            }
        }
        catch { }

        var executable = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(executable))
        {
            var fallback = System.Drawing.Icon.ExtractAssociatedIcon(executable);
            if (fallback is not null) return fallback;
        }
        throw new InvalidOperationException("No se pudo cargar el icono de Flow.");
    }

    private void OpenPanelFromTray()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(OpenPanelFromTray));
            return;
        }

        ShowInTaskbar = true;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void ExitFromTray()
    {
        if (_client.IsRecording)
        {
            MessageBox.Show(this, "Termina o cancela el dictado antes de salir.", "Flow", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _isClosingFromApp = true;
        Application.Current.Shutdown();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_client.IsRecording)
        {
            MessageBox.Show(this, "Termina o cancela el dictado antes de cerrar.", "Flow", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        HideToTray(); // Keep Flow resident without a taskbar button.
    }

    private async void ToggleBarBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_client.IsRecording)
        {
            await FinishDictationAsync();
        }
        else
        {
            await StartDictationFlowAsync();
        }
    }

    private void NavTab_Checked(object sender, RoutedEventArgs e)
    {
        if (ViewNotes == null || ViewMeetings == null || ViewSnippets == null || ViewDictionary == null || ViewStyles == null || ViewInsights == null || ViewSettings == null)
            return;

        ViewNotes.Visibility = NavNotes.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ViewMeetings.Visibility = NavMeetings.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ViewSnippets.Visibility = NavSnippets.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ViewDictionary.Visibility = NavDictionary.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ViewStyles.Visibility = NavStyles.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ViewInsights.Visibility = NavInsights.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ViewSettings.Visibility = NavSettings.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        if (NavNotes.IsChecked == true) _ = LoadNotesAsync();
        if (NavMeetings.IsChecked == true) _ = LoadMeetingsAsync();
        if (NavSnippets.IsChecked == true) _ = LoadSnippetsAsync();
        if (NavDictionary.IsChecked == true) _ = LoadDictionaryAsync();
        if (NavStyles.IsChecked == true) _ = LoadStyleSettingsAsync();
        if (NavInsights.IsChecked == true) _ = UpdateStatsAsync();
    }

    private async Task LoadMeetingsAsync()
    {
        if (MeetingListContainer == null) return;
        var meetings = await _client.GetMeetingsAsync();
        MeetingListContainer.Children.Clear();
        if (meetings.Count == 0)
        {
            MeetingListContainer.Children.Add(new TextBlock
            {
                Text = "Todavía no hay reuniones.", Foreground = (System.Windows.Media.Brush)FindResource("BrushTextSecondary"),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16)
            });
            return;
        }

        foreach (var meeting in meetings)
        {
            var content = new StackPanel();
            content.Children.Add(new TextBlock { Text = meeting.Title, FontWeight = FontWeights.SemiBold, Foreground = (System.Windows.Media.Brush)FindResource("BrushTextPrimary"), TextTrimming = TextTrimming.CharacterEllipsis });
            content.Children.Add(new TextBlock { Text = $"{meeting.StartedAt:dd/MM/yyyy HH:mm} · {FormatDuration(meeting.DurationMs)}", FontSize = 11, Foreground = (System.Windows.Media.Brush)FindResource("BrushTextMuted"), Margin = new Thickness(0, 4, 0, 0) });
            var button = new Button
            {
                Content = content, Tag = meeting, HorizontalContentAlignment = HorizontalAlignment.Left,
                HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(6), Padding = new Thickness(10, 9, 10, 9),
                Style = (Style)FindResource("WisprGhostButton")
            };
            button.Click += (_, _) => SelectMeeting((MeetingHistoryItem)button.Tag);
            MeetingListContainer.Children.Add(button);
        }
        if (_selectedMeeting is not null)
        {
            var refreshed = meetings.FirstOrDefault(item => item.Id == _selectedMeeting.Id);
            if (refreshed is not null) SelectMeeting(refreshed);
        }
    }

    private void SelectMeeting(MeetingHistoryItem meeting)
    {
        _selectedMeeting = meeting;
        MeetingTitleLabel.Text = meeting.Title;
        MeetingSummaryLabel.Text = string.IsNullOrWhiteSpace(meeting.Summary)
            ? $"{meeting.StartedAt:dd/MM/yyyy HH:mm} · {FormatDuration(meeting.DurationMs)}"
            : meeting.Summary;
        var hasAudio = !string.IsNullOrWhiteSpace(meeting.AudioPath) && File.Exists(meeting.AudioPath);
        MeetingPlayBtn.IsEnabled = hasAudio;
        MeetingExportMdBtn.IsEnabled = true;
        MeetingExportTxtBtn.IsEnabled = true;
        MeetingTimelineContainer.Children.Clear();
        if (meeting.Segments.Count == 0)
        {
            MeetingTimelineContainer.Children.Add(new TextBlock { Text = meeting.Transcript ?? "Sin transcripción disponible.", Foreground = (System.Windows.Media.Brush)FindResource("BrushTextPrimary"), TextWrapping = TextWrapping.Wrap });
            return;
        }
        foreach (var segment in meeting.Segments.OrderBy(item => item.StartMs))
        {
            var content = new StackPanel();
            content.Children.Add(new TextBlock { Text = $"{FormatTimestamp(segment.StartMs)} · {segment.Speaker}", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = (System.Windows.Media.Brush)FindResource("BrushAccentPurple") });
            content.Children.Add(new TextBlock { Text = segment.Text, TextWrapping = TextWrapping.Wrap, Foreground = (System.Windows.Media.Brush)FindResource("BrushTextPrimary"), Margin = new Thickness(0, 3, 0, 0) });
            var button = new Button
            {
                Content = content, Tag = segment, HorizontalContentAlignment = HorizontalAlignment.Left,
                HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(8), Margin = new Thickness(0, 0, 0, 5),
                Style = (Style)FindResource("WisprGhostButton")
            };
            button.Click += (_, _) => SeekMeeting(((MeetingTranscriptSegmentItem)button.Tag).StartMs);
            MeetingTimelineContainer.Children.Add(button);
        }
    }

    private async void ImportMeetingBtn_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Importar audio de reunión",
            Filter = "Audio y vídeo|*.wav;*.mp3;*.m4a;*.flac;*.ogg;*.webm;*.mp4|Todos los archivos|*.*",
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            MeetingStatusLabel.Text = "Procesando audio…";
            ImportMeetingBtn.IsEnabled = false;
            var meeting = await _client.ImportMeetingAsync(dialog.FileName, Path.GetFileNameWithoutExtension(dialog.FileName));
            await LoadMeetingsAsync();
            SelectMeeting(meeting);
            MeetingStatusLabel.Text = "Reunión procesada y guardada localmente. La sincronización se ejecuta en segundo plano.";
        }
        catch (Exception error)
        {
            MeetingStatusLabel.Text = $"No se pudo procesar el audio: {error.Message}";
        }
        finally { ImportMeetingBtn.IsEnabled = true; }
    }

    private async void RefreshMeetingsBtn_Click(object sender, RoutedEventArgs e)
    {
        await _client.SyncPendingAsync();
        await LoadMeetingsAsync();
    }

    private void MeetingPlayBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedMeeting is null || string.IsNullOrWhiteSpace(_selectedMeeting.AudioPath) || !File.Exists(_selectedMeeting.AudioPath)) return;
        if (_meetingOutput?.PlaybackState == PlaybackState.Playing)
        {
            StopMeetingPlayback();
            MeetingPlayBtn.Content = "Reproducir audio";
            return;
        }
        StopMeetingPlayback();
        _meetingReader = new AudioFileReader(_selectedMeeting.AudioPath);
        _meetingOutput = new WaveOutEvent();
        _meetingOutput.Init(_meetingReader);
        _meetingOutput.PlaybackStopped += (_, _) => Dispatcher.Invoke(() => MeetingPlayBtn.Content = "Reproducir audio");
        _meetingOutput.Play();
        MeetingPlayBtn.Content = "Detener audio";
    }

    private void SeekMeeting(long startMs)
    {
        if (_selectedMeeting is null || string.IsNullOrWhiteSpace(_selectedMeeting.AudioPath) || !File.Exists(_selectedMeeting.AudioPath)) return;
        if (_meetingOutput?.PlaybackState != PlaybackState.Playing) MeetingPlayBtn_Click(this, new RoutedEventArgs());
        if (_meetingReader is not null) _meetingReader.CurrentTime = TimeSpan.FromMilliseconds(Math.Max(0, startMs));
    }

    private void MeetingExportMdBtn_Click(object sender, RoutedEventArgs e) => ExportSelectedMeeting(markdown: true);
    private void MeetingExportTxtBtn_Click(object sender, RoutedEventArgs e) => ExportSelectedMeeting(markdown: false);

    private void ExportSelectedMeeting(bool markdown)
    {
        if (_selectedMeeting is null) return;
        var extension = markdown ? "md" : "txt";
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = markdown ? "Exportar Markdown" : "Exportar texto", Filter = markdown ? "Markdown|*.md" : "Texto|*.txt", FileName = $"{MeetingExportFormatter.SafeName(_selectedMeeting.Title)}.{extension}" };
        if (dialog.ShowDialog(this) != true) return;
        File.WriteAllText(dialog.FileName, markdown ? MeetingExportFormatter.Markdown(_selectedMeeting) : MeetingExportFormatter.Text(_selectedMeeting), System.Text.Encoding.UTF8);
        MeetingStatusLabel.Text = $"Exportado: {dialog.FileName}";
    }

    private void StopMeetingPlayback()
    {
        _meetingOutput?.Stop();
        _meetingOutput?.Dispose();
        _meetingOutput = null;
        _meetingReader?.Dispose();
        _meetingReader = null;
    }

    private static string FormatTimestamp(long milliseconds)
    {
        var seconds = Math.Max(0, milliseconds / 1_000);
        return $"{seconds / 60:00}:{seconds % 60:00}";
    }

    private static string FormatDuration(long milliseconds) => FormatTimestamp(milliseconds);

    private async Task LoadCorrectionOptionsAsync()
    {
        if (ToggleRemoveFillers == null || ToggleRemoveRepetitions == null ||
            ToggleResolveCorrections == null || ToggleFormatText == null) return;
        _loadingCorrectionOptions = true;
        try
        {
            var correctionOptions = await _client.Outbox.GetCorrectionOptionsAsync();
            ToggleRemoveFillers.IsChecked = correctionOptions.RemoveFillers;
            ToggleRemoveRepetitions.IsChecked = correctionOptions.RemoveRepetitions;
            ToggleResolveCorrections.IsChecked = correctionOptions.ResolveSelfCorrections;
            ToggleFormatText.IsChecked = correctionOptions.FormatText;
        }
        finally { _loadingCorrectionOptions = false; }
    }

    private async Task LoadStyleSettingsAsync()
    {
        if (WorkStyleComboBox == null || EmailStyleComboBox == null || CodeStyleComboBox == null || PersonalStyleComboBox == null)
            return;
        _loadingStyleOptions = true;
        try
        {
            _styleSettings = await _client.Outbox.GetStyleSettingsAsync();
            WorkStyleComboBox.SelectedValue = _styleSettings.Work;
            EmailStyleComboBox.SelectedValue = _styleSettings.Email;
            CodeStyleComboBox.SelectedValue = _styleSettings.Code;
            PersonalStyleComboBox.SelectedValue = _styleSettings.Personal;
        }
        finally { _loadingStyleOptions = false; }
    }
    #endregion

    #region Dictation Lifecycle
    private async Task StartDictationFlowAsync(bool fromShortcut = false)
    {
        if (_client.IsRecording) return;
        try
        {
            _client.SetPasteTarget(_lastExternalWindow, _lastExternalAppName);
            await _client.StartDictationAsync();
            if (!fromShortcut) _shortcut.NotifyExternalStart();
            _flowBar.ShowRecording();
        }
        catch (Exception error)
        {
            _shortcut.NotifyExternalStop();
            UpdateStatus($"No se pudo iniciar: {error.Message}");
        }
    }

    private async Task FinishDictationAsync()
    {
        if (!_client.IsRecording)
        {
            _shortcut.NotifyExternalStop();
            return;
        }
        try
        {
            _flowBar.ShowProcessing();
            var result = await _client.FinishDictationAsync();
            if (!string.IsNullOrEmpty(result))
            {
                await _flowBar.ShowSuccessAsync();
            }
            else
            {
                _flowBar.AnimateOut();
            }
            _shortcut.NotifyExternalStop();
            await LoadNotesAsync();
            await UpdateStatsAsync();
        }
        catch (Exception error)
        {
            _flowBar.AnimateOut();
            _shortcut.NotifyExternalStop();
            UpdateStatus($"Error al transcribir: {error.Message}");
        }
    }
    #endregion

    #region Notes & History Logic
    private void Filter_Checked(object sender, RoutedEventArgs e)
    {
        if (FilterAll?.IsChecked == true) _currentNoteFilter = "all";
        else if (FilterToday?.IsChecked == true) _currentNoteFilter = "today";
        else if (FilterFavs?.IsChecked == true) _currentNoteFilter = "favorites";
        _ = LoadNotesAsync();
    }

    private async Task LoadNotesAsync()
    {
        if (NotesContainer == null) return;
        var search = NotesSearchBox.Text?.Trim();
        var items = await _client.Outbox.GetHistoryAsync(search, _currentNoteFilter);

        NotesContainer.Children.Clear();
        if (items.Count == 0)
        {
            var emptyText = new TextBlock
            {
                Text = string.IsNullOrEmpty(search) ? "No hay notas o dictados guardados todavía. ¡Empieza a hablar con Ctrl + Win!" : "No se encontraron notas para esta búsqueda.",
                FontSize = 13,
                Margin = new Thickness(0, 40, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            emptyText.SetResourceReference(TextBlock.ForegroundProperty, "BrushTextMuted");
            NotesContainer.Children.Add(emptyText);
            return;
        }

        foreach (var item in items)
        {
            var card = CreateNoteCard(item);
            NotesContainer.Children.Add(card);
        }
    }

    private Border CreateNoteCard(DictationHistoryItem item)
    {
        var border = new Border
        {
            Style = (Style)FindResource("WisprCard"),
            Margin = new Thickness(0, 0, 0, 10),
            Padding = new Thickness(14)
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Header Row: Metadata & Actions
        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var metaStack = new StackPanel { Orientation = Orientation.Horizontal };
        var timeLabel = new TextBlock
        {
            Text = item.CreatedAt.ToLocalTime().ToString("dd MMM, HH:mm"),
            FontSize = 11.5,
            FontWeight = FontWeights.Medium,
            Margin = new Thickness(0, 0, 10, 0)
        };
        timeLabel.SetResourceReference(TextBlock.ForegroundProperty, "BrushTextMuted");

        var wordsLabel = new TextBlock
        {
            Text = $"{item.WordCount} palabras",
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 10, 0)
        };
        wordsLabel.SetResourceReference(TextBlock.ForegroundProperty, "BrushAccentPurple");

        var durationLabel = new TextBlock
        {
            Text = $"{item.DurationSeconds:0.0}s",
            FontSize = 11.5,
            Margin = new Thickness(0, 0, 10, 0)
        };
        durationLabel.SetResourceReference(TextBlock.ForegroundProperty, "BrushTextSecondary");

        metaStack.Children.Add(timeLabel);
        metaStack.Children.Add(wordsLabel);
        metaStack.Children.Add(durationLabel);

        if (!string.IsNullOrEmpty(item.AppName))
        {
            var appBadge = new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2, 6, 2)
            };
            appBadge.SetResourceReference(Border.BackgroundProperty, "BrushBgCardHover");
            var appText = new TextBlock { Text = item.AppName, FontSize = 10.5 };
            appText.SetResourceReference(TextBlock.ForegroundProperty, "BrushTextSecondary");
            appBadge.Child = appText;
            metaStack.Children.Add(appBadge);
        }

        headerGrid.Children.Add(metaStack);

        // Actions: Star, Copy, Delete
        var actionsStack = new StackPanel { Orientation = Orientation.Horizontal };

        var favBtn = new Button
        {
            Content = item.IsFavorite ? "★" : "☆",
            Style = (Style)FindResource("WisprGhostButton"),
            Padding = new Thickness(6, 3, 6, 3),
            FontSize = 12,
            Margin = new Thickness(0, 0, 6, 0)
        };
        favBtn.SetResourceReference(Button.ForegroundProperty, item.IsFavorite ? "BrushAccentWarning" : "BrushTextMuted");

        favBtn.Click += async (_, _) =>
        {
            await _client.Outbox.ToggleFavoriteAsync(item.Id);
            await _client.SyncPendingAsync();
            await LoadNotesAsync();
        };

        var copyBtn = new Button
        {
            Content = "Copiar",
            Style = (Style)FindResource("WisprGhostButton"),
            Padding = new Thickness(8, 3, 8, 3),
            FontSize = 11.5,
            Margin = new Thickness(0, 0, 6, 0)
        };
        copyBtn.Click += (_, _) =>
        {
            Clipboard.SetText(item.Text);
            copyBtn.Content = "¡Copiado!";
            Task.Delay(1500).ContinueWith(_ => Dispatcher.Invoke(() => copyBtn.Content = "Copiar"));
        };

        var deleteBtn = new Button
        {
            Content = "✕",
            Style = (Style)FindResource("WisprGhostButton"),
            Padding = new Thickness(6, 3, 6, 3),
            FontSize = 11.5
        };
        deleteBtn.SetResourceReference(Control.ForegroundProperty, "BrushTextSecondary");
        deleteBtn.Click += async (_, _) =>
        {
            await _client.Outbox.DeleteDictationAsync(item.Id);
            await _client.SyncPendingAsync();
            await LoadNotesAsync();
            await UpdateStatsAsync();
        };

        actionsStack.Children.Add(favBtn);
        actionsStack.Children.Add(copyBtn);
        actionsStack.Children.Add(deleteBtn);
        Grid.SetColumn(actionsStack, 1);
        headerGrid.Children.Add(actionsStack);

        // Content Row
        var textBlock = new TextBlock
        {
            Text = item.Text,
            FontSize = 13.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
            LineHeight = 20
        };
        textBlock.SetResourceReference(TextBlock.ForegroundProperty, "BrushTextPrimary");

        Grid.SetRow(headerGrid, 0);
        Grid.SetRow(textBlock, 1);
        grid.Children.Add(headerGrid);
        grid.Children.Add(textBlock);
        border.Child = grid;
        return border;
    }

    private void NotesSearchBox_TextChanged(object sender, TextChangedEventArgs e) => _ = LoadNotesAsync();

    private async void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(this, "¿Estás seguro de que deseas vaciar todas las notas e historial?", "Vaciar Notas", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result == MessageBoxResult.Yes)
        {
            await _client.Outbox.ClearAllHistoryAsync();
            await _client.SyncPendingAsync();
            await LoadNotesAsync();
            await UpdateStatsAsync();
        }
    }
    #endregion

    #region Snippets Logic
    private async Task LoadSnippetsAsync()
    {
        if (SnippetsContainer == null) return;
        var items = await _client.Outbox.GetSnippetsAsync();
        SnippetsContainer.Children.Clear();

        foreach (var item in items)
        {
            var card = new Border
            {
                Style = (Style)FindResource("WisprCard"),
                Margin = new Thickness(0, 0, 0, 8),
                Padding = new Thickness(14, 12, 14, 12)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.5, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var triggerText = new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 4, 8, 4),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };
            triggerText.SetResourceReference(Border.BackgroundProperty, "BrushBgCardHover");
            triggerText.SetResourceReference(Border.BorderBrushProperty, "BrushAccentPurple");
            triggerText.BorderThickness = new Thickness(1);

            var tBlock = new TextBlock { Text = $"«{item.Trigger}»", FontWeight = FontWeights.SemiBold, FontSize = 12.5 };
            tBlock.SetResourceReference(TextBlock.ForegroundProperty, "BrushTextPrimary");
            triggerText.Child = tBlock;

            var expText = new TextBlock { Text = item.Expansion, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0) };
            expText.SetResourceReference(TextBlock.ForegroundProperty, "BrushTextSecondary");

            var catText = new TextBlock { Text = item.Category ?? "General", FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
            catText.SetResourceReference(TextBlock.ForegroundProperty, "BrushTextMuted");

            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            var copyBtn = new Button
            {
                Content = "Copiar",
                Style = (Style)FindResource("WisprGhostButton"),
                Padding = new Thickness(8, 4, 8, 4),
                FontSize = 11.5,
                Margin = new Thickness(0, 0, 6, 0)
            };
            copyBtn.Click += (_, _) => Clipboard.SetText(item.Expansion);

            var deleteBtn = new Button
            {
                Content = "✕",
                Style = (Style)FindResource("WisprGhostButton"),
                Padding = new Thickness(6, 4, 6, 4),
                FontSize = 11.5
            };
            deleteBtn.SetResourceReference(Control.ForegroundProperty, "BrushTextSecondary");
            deleteBtn.Click += async (_, _) =>
            {
                await _client.Outbox.DeleteSnippetAsync(item.Id);
                await _client.SyncPendingAsync();
                await LoadSnippetsAsync();
            };

            actions.Children.Add(copyBtn);
            actions.Children.Add(deleteBtn);

            Grid.SetColumn(triggerText, 0);
            Grid.SetColumn(expText, 1);
            Grid.SetColumn(catText, 2);
            Grid.SetColumn(actions, 3);
            grid.Children.Add(triggerText);
            grid.Children.Add(expText);
            grid.Children.Add(catText);
            grid.Children.Add(actions);
            card.Child = grid;
            SnippetsContainer.Children.Add(card);
        }
    }

    private async void AddSnippetBtn_Click(object sender, RoutedEventArgs e)
    {
        var trigger = SnippetTriggerInput.Text?.Trim();
        var exp = SnippetExpansionInput.Text?.Trim();
        var cat = SnippetCategoryInput.Text?.Trim();

        if (string.IsNullOrWhiteSpace(trigger) || string.IsNullOrWhiteSpace(exp)) return;

        await _client.Outbox.AddSnippetAsync(trigger, exp, cat);
        await _client.SyncPendingAsync();
        SnippetTriggerInput.Clear();
        SnippetExpansionInput.Clear();
        await LoadSnippetsAsync();
    }
    #endregion

    #region Dictionary Logic
    private async Task LoadDictionaryAsync()
    {
        if (DictionaryContainer == null) return;
        var items = await _client.Outbox.GetDictionaryEntriesAsync();
        DictionaryContainer.Children.Clear();

        if (items.Count == 0)
        {
            var emptyText = new TextBlock
            {
                Text = "No hay términos en el diccionario personal todavía.",
                FontSize = 13,
                Margin = new Thickness(0, 24, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            emptyText.SetResourceReference(TextBlock.ForegroundProperty, "BrushTextMuted");
            DictionaryContainer.Children.Add(emptyText);
            return;
        }

        foreach (var item in items)
        {
            var card = new Border
            {
                Style = (Style)FindResource("WisprCard"),
                Margin = new Thickness(0, 0, 0, 8),
                Padding = new Thickness(12, 10, 12, 10)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var wordLabel = new TextBlock { Text = item.Word, FontWeight = FontWeights.SemiBold, FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center };
            wordLabel.SetResourceReference(TextBlock.ForegroundProperty, "BrushTextPrimary");

            var repLabel = new TextBlock { Text = string.IsNullOrEmpty(item.Replacement) ? "—" : item.Replacement, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
            repLabel.SetResourceReference(TextBlock.ForegroundProperty, "BrushTextSecondary");

            var catLabel = new TextBlock { Text = item.Category ?? "General", FontSize = 11.5, FontWeight = FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center };
            catLabel.SetResourceReference(TextBlock.ForegroundProperty, "BrushAccentPurple");

            var deleteBtn = new Button
            {
                Content = "Eliminar",
                Style = (Style)FindResource("WisprGhostButton"),
                Padding = new Thickness(8, 4, 8, 4),
                FontSize = 11.5
            };
            deleteBtn.SetResourceReference(Control.ForegroundProperty, "BrushTextSecondary");
            deleteBtn.Click += async (_, _) =>
            {
                await _client.Outbox.DeleteDictionaryEntryAsync(item.Id);
                await _client.SyncPendingAsync();
                await LoadDictionaryAsync();
            };

            Grid.SetColumn(wordLabel, 0);
            Grid.SetColumn(repLabel, 1);
            Grid.SetColumn(catLabel, 2);
            Grid.SetColumn(deleteBtn, 3);
            grid.Children.Add(wordLabel);
            grid.Children.Add(repLabel);
            grid.Children.Add(catLabel);
            grid.Children.Add(deleteBtn);
            card.Child = grid;
            DictionaryContainer.Children.Add(card);
        }
    }

    private async void AddWordBtn_Click(object sender, RoutedEventArgs e)
    {
        var word = NewWordInput.Text?.Trim();
        if (string.IsNullOrWhiteSpace(word)) return;
        var rep = NewWordRepInput.Text?.Trim();
        var cat = NewWordCatInput.Text?.Trim();

        await _client.Outbox.AddDictionaryEntryAsync(word, rep, cat);
        await _client.SyncPendingAsync();
        NewWordInput.Clear();
        NewWordRepInput.Clear();
        await LoadDictionaryAsync();
    }
    #endregion

    #region Settings Logic
    private void PopulateMicrophones(string? savedDeviceId)
    {
        _loadingMicrophone = true;
        try
        {
            var devices = AudioCapture.GetInputDevices();
            MicrophoneComboBox.ItemsSource = devices;
            MicrophoneComboBox.DisplayMemberPath = "Name";
            MicrophoneComboBox.SelectedValuePath = "Id";

            if (devices.Count > 0)
            {
                var selectedDevice = devices.FirstOrDefault(d =>
                    !string.IsNullOrWhiteSpace(savedDeviceId) &&
                    string.Equals(d.Id, savedDeviceId, StringComparison.Ordinal));
                MicrophoneComboBox.SelectedItem = selectedDevice ?? devices.FirstOrDefault(d => d.IsDefault) ?? devices[0];
            }
        }
        catch { }
        finally { _loadingMicrophone = false; }
    }

    private async void MicrophoneComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingMicrophone || MicrophoneComboBox.SelectedItem is not AudioDeviceInfo device) return;
        try
        {
            _client.ChangeMicrophone(device.Id);
            await _client.Outbox.SetSettingAsync(FlowSettingKeys.AudioInputDevice, device.Id);
            UpdateStatus($"Micrófono guardado: {device.Name}.");
        }
        catch (Exception error) { UpdateStatus($"No se pudo guardar el micrófono: {error.Message}"); }
    }

    private async void ToggleSounds_Changed(object sender, RoutedEventArgs e)
    {
        var enabled = ToggleSounds.IsChecked == true;
        SoundManager.SoundEnabled = enabled;
        if (_loadingSounds || _client?.Outbox == null) return;
        try { await _client.Outbox.SetSettingAsync(FlowSettingKeys.SoundsEnabled, enabled ? "true" : "false"); }
        catch (Exception error) { UpdateStatus($"No se pudo guardar el sonido: {error.Message}"); }
    }

    private async void CorrectionOption_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingCorrectionOptions || _client?.Outbox == null ||
            ToggleRemoveFillers == null || ToggleRemoveRepetitions == null ||
            ToggleResolveCorrections == null || ToggleFormatText == null) return;
        try
        {
            await _client.Outbox.SaveCorrectionOptionsAsync(new DictationCorrectionOptions(
                RemoveFillers: ToggleRemoveFillers.IsChecked == true,
                RemoveRepetitions: ToggleRemoveRepetitions.IsChecked == true,
                ResolveSelfCorrections: ToggleResolveCorrections.IsChecked == true,
                FormatText: ToggleFormatText.IsChecked == true));
            await _client.SyncPendingAsync();
            UpdateStatus("Preferencias de corrección guardadas.");
        }
        catch (Exception error)
        {
            UpdateStatus($"No se pudieron guardar las preferencias: {error.Message}");
        }
    }

    private async void StyleSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingStyleOptions || _client?.Outbox == null || e.AddedItems.Count == 0) return;

        var category = ReferenceEquals(sender, WorkStyleComboBox)
            ? DictationStyleCatalog.WorkCategory
            : ReferenceEquals(sender, EmailStyleComboBox)
                ? DictationStyleCatalog.EmailCategory
                : ReferenceEquals(sender, CodeStyleComboBox)
                    ? DictationStyleCatalog.CodeCategory
                    : ReferenceEquals(sender, PersonalStyleComboBox)
                        ? DictationStyleCatalog.PersonalCategory
                        : null;
        if (category == null) return;

        try
        {
            var selected = (sender as ComboBox)?.SelectedValue?.ToString();
            _styleSettings = _styleSettings.WithCategory(category, selected);
            await _client.Outbox.SaveStyleSettingsAsync(_styleSettings);
            await _client.SyncPendingAsync();
            UpdateStatus($"Estilo {DictationStyleCatalog.DisplayName(selected)} guardado para {DictationStyleCatalog.CategoryLabel(category)}.");
        }
        catch (Exception error)
        {
            UpdateStatus($"No se pudo guardar el estilo: {error.Message}");
        }
    }

    private async void TestSyncBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _client.SaveSyncSettingsAsync(ServerUrlInput.Text, ServerTokenInput.Text);
            ServerTokenInput.Text = _client.HasFlowHubToken ? "●●●●●●●●" : string.Empty;
            UpdateStatus("Sincronizando con FlowHub…");
            var sync = await _client.SyncPendingAsync();
            if (sync.Pulled > 0)
            {
                await LoadNotesAsync();
                await UpdateStatsAsync();
            }
        }
        catch (Exception error)
        {
            UpdateStatus(error.Message);
        }
    }

    private void OpenDbFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var folder = System.IO.Path.GetDirectoryName(_client.LocalDatabasePath);
            if (!string.IsNullOrEmpty(folder) && System.IO.Directory.Exists(folder))
            {
                Process.Start("explorer.exe", folder);
            }
        }
        catch { }
    }
    private void ToggleAutoStart_Changed(object sender, RoutedEventArgs e)
    {
        StartupHelper.SetAutoStart(ToggleAutoStart.IsChecked == true);
    }

    private void QuitApp_Click(object sender, RoutedEventArgs e)
    {
        _isClosingFromApp = true;
        Application.Current.Shutdown();
    }
    #endregion

    #region Statistics & Window Tracking
    private async Task UpdateStatsAsync()
    {
        try
        {
            var stats = await _client.Outbox.GetStatisticsAsync();
            if (InsightsWordsToday != null) InsightsWordsToday.Text = stats.WordsToday.ToString();
            if (InsightsAvgWpm != null) InsightsAvgWpm.Text = $"{stats.AverageWpm} WPM";
            if (InsightsTimeSaved != null) InsightsTimeSaved.Text = $"{stats.MinutesSaved:0.0} min";
            if (InsightsStreak != null) InsightsStreak.Text = $"{stats.DayStreak} día";
        }
        catch { }
    }

    private void UpdateStatus(string message)
    {
        if (Dispatcher.CheckAccess())
        {
            HubStatusLabel.Text = message.Length > 24 ? message[..24] + "…" : message;
        }
        else
        {
            Dispatcher.Invoke(() =>
            {
                HubStatusLabel.Text = message.Length > 24 ? message[..24] + "…" : message;
            });
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_isClosingFromApp && _client.IsRecording)
        {
            MessageBox.Show(this, "Termina el dictado antes de cerrar Flow.", "Flow", MessageBoxButton.OK, MessageBoxImage.Information);
            e.Cancel = true;
            return;
        }
        base.OnClosing(e);
    }

    private void TrackExternalForegroundWindow()
    {
        var foreground = GetForegroundWindow();
        var ownWindow = new WindowInteropHelper(this).Handle;
        if (foreground == 0 || foreground == ownWindow || !IsWindow(foreground)) return;

        if (_flowBar.IsVisible)
        {
            var barHandle = new WindowInteropHelper(_flowBar).Handle;
            if (foreground == barHandle) return;
        }

        _lastExternalWindow = foreground;
        var windowTitle = GetWindowTitle(foreground);
        try
        {
            GetWindowThreadProcessId(foreground, out var pid);
            var processName = pid == 0 ? null : Process.GetProcessById((int)pid).ProcessName;
            _lastExternalAppName = TargetApplicationDetector.Detect(processName, windowTitle);
        }
        catch { _lastExternalAppName = TargetApplicationDetector.Detect(null, windowTitle); }
    }

    private static string GetWindowTitle(nint handle)
    {
        var length = GetWindowTextLength(handle);
        if (length <= 0) return string.Empty;
        var title = new StringBuilder(length + 1);
        _ = GetWindowText(handle, title, title.Capacity);
        return title.ToString();
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint handle);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextLength(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(nint hWnd, StringBuilder text, int count);
    #endregion
}
