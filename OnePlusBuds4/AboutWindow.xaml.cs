using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;

namespace OnePlusBuds4;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => MainWindow.EnableDarkMode(this);
        StartupCheck.IsChecked = MainWindow.IsRunOnStartupEnabled();
    }

    private void OnStartupCheckClick(object sender, RoutedEventArgs e)
    {
        bool enable = StartupCheck.IsChecked == true;
        MainWindow.SetRunOnStartup(enable);
        (Owner as MainWindow)?.UpdateStartupCheckState();
    }

    private void OnDrag(object sender, System.Windows.Input.MouseButtonEventArgs e) => DragMove();
    private void OnClose(object sender, RoutedEventArgs e) => Close();
    private void OnNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
