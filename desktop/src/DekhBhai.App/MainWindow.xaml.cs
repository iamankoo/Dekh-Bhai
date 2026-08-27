using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DekhBhai.Core.Capture;
using DekhBhai.Core.Session;
using Hardcodet.Wpf.TaskbarNotification;

namespace DekhBhai.App;

public partial class MainWindow : Window
{
    private readonly SessionController _session;
    private SessionDuration? _selectedDuration;
    private DateTimeOffset _startedAt;
    private DateTimeOffset? _expiresAt;
    private readonly DispatcherTimer _elapsedTimer;
    private bool _isInBackgroundMode;

    public TaskbarIcon? TrayIcon { get; set; }

    public MainWindow()
    {
        InitializeComponent();

        _session = new SessionController(AppConfig.SignalingWsUrl, AppConfig.ViewerBaseUrl, AppConfig.LanIp);

        _session.StateChanged += (state, reason) => Dispatcher.Invoke(() => OnStateChanged(state, reason));
        _session.ShareUrlReady += url => Dispatcher.Invoke(() => OnShareUrlReady(url));
        // Fires a moment after ShareUrlReady, once the control session (mouse/keyboard access)
        // is ready - replaces the view-only link/QR with the control-capable one so the single
        // link the user shares/scans grants both viewing and remote control.
        _session.ControlUrlReady += url => Dispatcher.Invoke(() => OnShareUrlReady(url));
        _session.SessionTimingReady += (startedAt, expiresAt) => Dispatcher.Invoke(() => OnSessionTimingReady(startedAt, expiresAt));
        _session.ViewerCountChanged += count => Dispatcher.Invoke(() => ViewerStatusText.Text = $"viewers: {count}");
        _session.CaptureStatusChanged += text => Dispatcher.Invoke(() =>
        {
            CaptureStatusText.Text = $"capture: {text}";
            if (_session.State is SessionState.Idle) IdleStatusText.Text = text;
            if (_session.State is SessionState.Starting) DurationStatusText.Text = text;
        });
        _session.AudioStatusChanged += text => Dispatcher.Invoke(() => AudioStatusText.Text = $"audio: {text}");
        _session.SignalingStatusChanged += text => Dispatcher.Invoke(() => ConnectionStatusText.Text = $"signaling: {text}");

        _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _elapsedTimer.Tick += (_, _) => UpdateElapsedText();

        Closing += MainWindow_Closing;
        StateChanged += MainWindow_StateChanged;
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && _isInBackgroundMode)
        {
            HideFromTaskbar();
        }
    }

    private void DurationOption_Click(object sender, RoutedEventArgs e)
    {
        var button = (System.Windows.Controls.Button)sender;
        _selectedDuration = Enum.Parse<SessionDuration>((string)button.Tag);

        foreach (var b in new[] { Duration15Button, Duration1hButton, Duration5hButton, DurationUntilStoppedButton })
        {
            b.Background = new SolidColorBrush(Color.FromRgb(0x16, 0x18, 0x1D));
            b.BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x2D, 0x34));
        }
        button.Background = new SolidColorBrush(Color.FromRgb(0x20, 0x3A, 0x2C));
        button.BorderBrush = new SolidColorBrush(Color.FromRgb(0x3D, 0xDC, 0x84));

        StartButton.IsEnabled = true;
    }

    private void StartSharingButton_Click(object sender, RoutedEventArgs e)
    {
        IdlePanel.Visibility = Visibility.Collapsed;
        DurationPanel.Visibility = Visibility.Visible;
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedDuration is not { } duration) return;

        StartButton.IsEnabled = false;
        DurationStatusText.Text = "Starting...";

        var settings = new CaptureSettings
        {
            TargetWidth = 1920,
            TargetHeight = 1080,
            TargetFramesPerSecond = 30,
        };

        await _session.StartAsync(settings, duration);

        StartButton.IsEnabled = true;
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        StopButton.IsEnabled = false;
        await _session.StopAsync();
    }

    private void StartAgainButton_Click(object sender, RoutedEventArgs e)
    {
        _selectedDuration = null;
        StartButton.IsEnabled = false;
        DurationStatusText.Text = "";
        foreach (var b in new[] { Duration15Button, Duration1hButton, Duration5hButton, DurationUntilStoppedButton })
        {
            b.Background = new SolidColorBrush(Color.FromRgb(0x16, 0x18, 0x1D));
            b.BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x2D, 0x34));
        }

        PostSessionPanel.Visibility = Visibility.Collapsed;
        DurationPanel.Visibility = Visibility.Visible;
    }

    private void CopyLinkButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(ShareUrlBox.Text)) return;

        bool copied = TryCopyToClipboard(ShareUrlBox.Text);
        CopyConfirmText.Text = copied ? "Link copied" : "Couldn't copy - try again";
        CopyConfirmText.Foreground = new SolidColorBrush(copied
            ? Color.FromRgb(0x3D, 0xDC, 0x84)
            : Color.FromRgb(0xE5, 0x48, 0x4D));
        CopyConfirmText.Visibility = Visibility.Visible;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) => { CopyConfirmText.Visibility = Visibility.Hidden; timer.Stop(); };
        timer.Start();
    }

    /// <summary>
    /// Windows' clipboard is a single, transiently-lockable OS resource - OpenClipboard
    /// legitimately fails with CLIPBRD_E_CANT_OPEN if another process (a clipboard manager, an
    /// RDP session, a screen reader, etc.) holds it at that exact instant. This is a normal,
    /// expected race, not a bug in this app - found crashing the entire application with an
    /// unhandled COMException during Phase 3 testing (Clipboard.SetText's single-arg overload
    /// requests a flush, which is what actually threw - see
    /// docs/architecture/phase-3-technology-decision.md). A short retry resolves the
    /// overwhelming majority of real occurrences; if it still fails, this must degrade to a
    /// visible "couldn't copy" message, never crash the whole app over a copy-link click.
    /// </summary>
    private static bool TryCopyToClipboard(string text)
    {
        const int maxAttempts = 3;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(text, copy: false); // no flush - avoids the failure point above
                return true;
            }
            catch (System.Runtime.InteropServices.COMException) when (attempt < maxAttempts)
            {
                System.Threading.Thread.Sleep(50);
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                return false;
            }
        }
        return false;
    }

    private void OnShareUrlReady(string url)
    {
        ShareUrlBox.Text = url;
        QrImage.Source = QrCodeGenerator.Generate(url);
    }

    private void OnSessionTimingReady(DateTimeOffset startedAt, DateTimeOffset? expiresAt)
    {
        _startedAt = startedAt;
        _expiresAt = expiresAt;
        UpdateElapsedText();
        _elapsedTimer.Start();
    }

    private void UpdateElapsedText()
    {
        var elapsed = DateTimeOffset.UtcNow - _startedAt;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;

        ElapsedText.Text = _expiresAt is { } expiresAt
            ? $"{Format(elapsed)} / {Format(expiresAt - _startedAt)}"
            : Format(elapsed);
    }

    private static string Format(TimeSpan t) => t.ToString(t.TotalHours >= 1 ? @"hh\:mm\:ss" : @"mm\:ss");

    private void OnStateChanged(SessionState state, SessionStopReason? reason)
    {
        IdlePanel.Visibility = Visibility.Collapsed;
        DurationPanel.Visibility = Visibility.Collapsed;
        LivePanel.Visibility = Visibility.Collapsed;
        StoppingPanel.Visibility = Visibility.Collapsed;
        PostSessionPanel.Visibility = Visibility.Collapsed;
        _elapsedTimer.Stop();

        switch (state)
        {
            case SessionState.Idle:
                IdlePanel.Visibility = Visibility.Visible;
                ExitBackgroundMode();
                break;

            case SessionState.Starting:
                DurationPanel.Visibility = Visibility.Visible;
                break;

            case SessionState.Live:
                LivePanel.Visibility = Visibility.Visible;
                StopButton.IsEnabled = true;
                EnterBackgroundMode();
                break;

            case SessionState.Stopping:
                StoppingPanel.Visibility = Visibility.Visible;
                break;

            case SessionState.Stopped:
                ExitBackgroundMode();
                RestoreWindow();
                PostSessionPanel.Visibility = Visibility.Visible;
                break;

            case SessionState.Error:
                ExitBackgroundMode();
                RestoreWindow();
                DurationPanel.Visibility = Visibility.Visible;
                if (string.IsNullOrEmpty(DurationStatusText.Text))
                {
                    DurationStatusText.Text = "Something went wrong stopping the previous session. Check diagnostics and try again.";
                }
                break;
        }
    }

    private void EnterBackgroundMode()
    {
        _isInBackgroundMode = true;

        var app = (App)Application.Current;
        app.SetTrayIconVisible(true);
        app.UpdateTrayStopMenuItem(true);

        // Exclude the app's own window from any screen capture (ours or anyone else's) the
        // moment sharing goes live, so the host UI is never visible to a viewer - but do NOT
        // minimize or hide it here. SetWindowDisplayAffinity works regardless of window state,
        // so the popup can stay open showing the share link/QR until the user minimizes it
        // themselves; only that user action (handled by MainWindow_StateChanged) tucks it into
        // the tray.
        var hwnd = new WindowInteropHelper(this).Handle;
        WindowCaptureExclusion.Exclude(hwnd);
    }

    private void ExitBackgroundMode()
    {
        _isInBackgroundMode = false;

        var app = (App)Application.Current;
        app.SetTrayIconVisible(false);
        app.UpdateTrayStopMenuItem(false);

        ShowInTaskbar = true;
        Visibility = Visibility.Visible;
    }

    private void HideFromTaskbar()
    {
        ShowInTaskbar = false;
        Visibility = Visibility.Hidden;
    }

    private void ShowInTaskbarAndRestore()
    {
        ShowInTaskbar = true;
        Visibility = Visibility.Visible;
        WindowState = WindowState.Normal;
        Activate();
    }

    public void RestoreFromTray()
    {
        ExitBackgroundMode();
        ShowInTaskbarAndRestore();
    }

    public async void StopFromTray()
    {
        if (_session.State is SessionState.Live or SessionState.Starting)
        {
            StopButton.IsEnabled = false;
            await _session.StopAsync();
        }
    }

    public void ExitFromTray()
    {
        if (_session.State is SessionState.Live or SessionState.Starting)
        {
            _ = _session.StopAsync();
        }
        Application.Current.Shutdown();
    }

    private void RestoreWindow()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        WindowCaptureExclusion.Restore(hwnd);
        WindowState = WindowState.Normal;
        Activate();
    }

    private async void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_session.State is SessionState.Live or SessionState.Starting)
        {
            e.Cancel = true;
            Closing -= MainWindow_Closing;
            await _session.StopAsync();
            Close();
        }
    }
}