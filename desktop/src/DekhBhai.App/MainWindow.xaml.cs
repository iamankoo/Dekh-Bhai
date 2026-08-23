using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DekhBhai.Core.Capture;
using DekhBhai.Core.Session;

namespace DekhBhai.App;

public partial class MainWindow : Window
{
    private readonly SessionController _session;
    private SessionDuration? _selectedDuration;
    private DateTimeOffset _startedAt;
    private DateTimeOffset? _expiresAt;
    private readonly DispatcherTimer _elapsedTimer;

    public MainWindow()
    {
        InitializeComponent();

        _session = new SessionController(AppConfig.SignalingWsUrl, AppConfig.ViewerBaseUrl);

        _session.StateChanged += (state, reason) => Dispatcher.Invoke(() => OnStateChanged(state, reason));
        _session.ShareUrlReady += url => Dispatcher.Invoke(() => OnShareUrlReady(url));
        _session.SessionTimingReady += (startedAt, expiresAt) => Dispatcher.Invoke(() => OnSessionTimingReady(startedAt, expiresAt));
        _session.ViewerCountChanged += count => Dispatcher.Invoke(() => ViewerStatusText.Text = $"viewers: {count}");
        _session.CaptureStatusChanged += text => Dispatcher.Invoke(() =>
        {
            CaptureStatusText.Text = $"capture: {text}";
            if (_session.State is SessionState.Idle) IdleStatusText.Text = text;
            if (_session.State is SessionState.Starting) DurationStatusText.Text = text;
        });
        _session.AudioStatusChanged += text => Dispatcher.Invoke(() => AudioStatusText.Text = $"audio: {text}");

        _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _elapsedTimer.Tick += (_, _) => UpdateElapsedText();

        Closing += MainWindow_Closing;
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
        Clipboard.SetText(ShareUrlBox.Text);
        CopyConfirmText.Visibility = Visibility.Visible;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) => { CopyConfirmText.Visibility = Visibility.Hidden; timer.Stop(); };
        timer.Start();
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
        // Recomputed from the server-issued startedAt every tick (never incremented locally) so
        // the displayed time cannot drift from UI thread jitter or a missed tick.
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
                break;

            case SessionState.Starting:
                DurationPanel.Visibility = Visibility.Visible;
                break;

            case SessionState.Live:
                LivePanel.Visibility = Visibility.Visible;
                StopButton.IsEnabled = true;
                ConnectionStatusText.Text = "signaling: connected";
                // Capture engine is already running independent of this window - now hide the
                // control UI from the captured monitor without touching the capture pipeline.
                MinimizeAndExcludeFromCapture();
                break;

            case SessionState.Stopping:
                StoppingPanel.Visibility = Visibility.Visible;
                break;

            case SessionState.Stopped:
                RestoreWindow();
                PostSessionPanel.Visibility = Visibility.Visible;
                break;

            case SessionState.Error:
                RestoreWindow();
                DurationPanel.Visibility = Visibility.Visible;
                DurationStatusText.Text = "Something went wrong stopping the previous session. Check diagnostics and try again.";
                break;
        }
    }

    private void MinimizeAndExcludeFromCapture()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        WindowCaptureExclusion.Exclude(hwnd);
        WindowState = WindowState.Minimized;
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
