using System.Threading;
using System.Windows;

namespace OnePlusBuds4;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    private static Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, "OnePlusBuds_SingleInstance", out bool isNew);
        if (!isNew)
        {
            System.Windows.MessageBox.Show("OnePlus Buds is already running (check the system tray).",
                "Already running", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
        base.OnExit(e);
    }
}

