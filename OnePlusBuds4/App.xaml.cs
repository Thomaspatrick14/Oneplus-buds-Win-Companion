using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace OnePlusBuds4;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    private static Mutex? _mutex;
    private static EventWaitHandle? _showEvent;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, "OnePlusBuds_SingleInstance", out bool isNew);
        if (!isNew)
        {
            // Signal the already running instance to show its window via standard EventWaitHandle
            try
            {
                if (EventWaitHandle.TryOpenExisting("OnePlusBuds_ShowInstanceEvent", out var existing))
                {
                    existing.Set();
                    existing.Dispose();
                }
            }
            catch { }
            Shutdown();
            return;
        }

        try
        {
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "OnePlusBuds_ShowInstanceEvent");
            Task.Run(() =>
            {
                while (_showEvent.WaitOne())
                {
                    Current?.Dispatcher.InvokeAsync(() =>
                    {
                        if (Current.MainWindow is MainWindow mw)
                        {
                            mw.ShowFromTray();
                        }
                    });
                }
            });
        }
        catch { }

        base.OnStartup(e);

        bool startMinimized = false;
        foreach (var arg in e.Args)
        {
            if (arg.Equals("--minimized", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-m", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--silent", StringComparison.OrdinalIgnoreCase))
            {
                startMinimized = true;
                break;
            }
        }

        var mainWindow = new MainWindow(startMinimized);
        MainWindow = mainWindow;
        if (!startMinimized)
        {
            mainWindow.Show();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
        _showEvent?.Dispose();
        base.OnExit(e);
    }
}
