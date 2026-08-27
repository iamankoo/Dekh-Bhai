using System.Windows;
using System.Windows.Controls;
using Hardcodet.Wpf.TaskbarNotification;

namespace DekhBhai.App;

public partial class App : Application
{
    private TaskbarIcon? _trayIcon;
    private MainWindow? _mainWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _trayIcon = new TaskbarIcon
        {
            IconSource = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/DekhBhai.ico")),
            ToolTipText = "Dekh Bhai - Screen Sharing",
            ContextMenu = (ContextMenu)FindResource("TrayContextMenu"),
            Visibility = Visibility.Collapsed
        };

        // Created explicitly here (rather than via App.xaml's StartupUri) because StartupUri
        // creates/shows the window only after OnStartup returns - MainWindow was still null at
        // this point, so wiring up TrayIcon below threw a NullReferenceException on every launch.
        _mainWindow = new MainWindow();
        MainWindow = _mainWindow;
        _mainWindow.TrayIcon = _trayIcon;
        _mainWindow.Show();
    }

    private void TrayOpen_Click(object sender, RoutedEventArgs e)
    {
        _mainWindow?.RestoreFromTray();
    }

    private void TrayStop_Click(object sender, RoutedEventArgs e)
    {
        _mainWindow?.StopFromTray();
    }

    private void TrayExit_Click(object sender, RoutedEventArgs e)
    {
        _mainWindow?.ExitFromTray();
    }

    public void SetTrayIconVisible(bool visible)
    {
        if (_trayIcon != null)
        {
            _trayIcon.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    public void UpdateTrayStopMenuItem(bool enabled)
    {
        if (_trayIcon?.ContextMenu is ContextMenu menu)
        {
            var stopItem = menu.FindName("TrayStopMenuItem") as MenuItem;
            if (stopItem != null)
            {
                stopItem.IsEnabled = enabled;
            }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        base.OnExit(e);
    }
}