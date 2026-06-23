using System.Threading;
using System.Windows;

namespace OnePlusBudsPro3;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    private static Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, "OnePlusBudsPro3_SingleInstance", out bool isNew);
        if (!isNew)
        {
            System.Windows.MessageBox.Show("OnePlus Buds Pro 3 is already running (check the system tray).",
                "Already running", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        base.OnStartup(e);
    }
}

