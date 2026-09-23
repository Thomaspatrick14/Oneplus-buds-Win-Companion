using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace OnePlusBuds4;

public partial class PacketLogWindow : Window
{
    private const int MaxLines = 2000;
    private int _packetCount = 0;
    private readonly string _logFilePath;
    private readonly object _fileLock = new();

    public PacketLogWindow()
    {
        InitializeComponent();
        _logFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "packet_log.txt");
    }

    private void OnTitleBarDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        Hide();
    }

    public bool AllowClose { get; set; }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!AllowClose)
        {
            // Hide instead of destroying so logging continues uninterrupted
            e.Cancel = true;
            Hide();
        }
    }

    public void LogPacket(byte[] data, bool incoming)
    {
        if (data == null || data.Length == 0) return;

        Dispatcher.BeginInvoke(() =>
        {
            _packetCount++;
            PacketCountText.Text = $"Packets: {_packetCount}";

            string time = DateTime.Now.ToString("HH:mm:ss.fff");
            string dir = incoming ? "[RECV]" : "[SEND]";
            string hex = BudsConnection.FormatHex(data);
            string annotation = BudsConnection.AnnotatePacket(data, incoming);

            string line = string.IsNullOrWhiteSpace(annotation)
                ? $"[{time}] {dir,-6} ({data.Length,2}B) {hex}"
                : $"[{time}] {dir,-6} ({data.Length,2}B) {hex,-36} | {annotation}";

            LogBox.AppendText(line + Environment.NewLine);

            // Cap the displayed log size if needed
            if (LogBox.LineCount > MaxLines)
            {
                int firstNewline = LogBox.Text.IndexOf(Environment.NewLine);
                if (firstNewline >= 0)
                {
                    LogBox.Text = LogBox.Text.Substring(firstNewline + Environment.NewLine.Length);
                }
            }

            if (AutoScrollCheck.IsChecked == true)
            {
                LogBox.ScrollToEnd();
            }

            // Also persist to log file asynchronously
            Task.Run(() =>
            {
                try
                {
                    lock (_fileLock)
                    {
                        File.AppendAllText(_logFilePath, line + Environment.NewLine, Encoding.UTF8);
                    }
                }
                catch { }
            });
        });
    }

    private async void OnCopyAll(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(LogBox.Text);
            CopyBtn.Content = "✓ Copied!";
            await Task.Delay(1500);
            CopyBtn.Content = "📋 Copy All";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Failed to copy to clipboard: {ex.Message}", "Clipboard", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        LogBox.Clear();
        _packetCount = 0;
        PacketCountText.Text = "Packets: 0";
    }

    private Action<byte[]>? _sender;
    public void SetSender(Action<byte[]> sender) => _sender = sender;

    private void OnSendHex(object sender, RoutedEventArgs e)
    {
        if (_sender == null)
        {
            System.Windows.MessageBox.Show("Not connected to earbuds.", "Send Hex", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        string raw = HexInputBox.Text.Replace(" ", "").Replace("-", "").Trim();
        if (raw.Length % 2 != 0 || raw.Length == 0)
        {
            System.Windows.MessageBox.Show("Invalid hex string (must have an even number of hex characters).", "Hex Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            byte[] packet = new byte[raw.Length / 2];
            for (int i = 0; i < packet.Length; i++)
                packet[i] = Convert.ToByte(raw.Substring(i * 2, 2), 16);
            _sender(packet);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Failed to send hex: {ex.Message}", "Send Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void OnProbeFeatures(object sender, RoutedEventArgs e)
    {
        if (_sender == null)
        {
            System.Windows.MessageBox.Show("Connect to earbuds first before probing.", "Probe", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        ProbeBtn.IsEnabled = false;
        ProbeBtn.Content = "Probing...";
        try
        {
            LogBox.AppendText("─── PROBE STEP 1: Querying Function Codes 0x01..0x35 ───" + Environment.NewLine);
            for (byte fn = 0x01; fn <= 0x35; fn++)
            {
                if (fn == 0x06) continue; // Battery already handled
                byte[] packet = { 0xAA, 0x07, 0x00, 0x00, fn, 0x01, 0x00, 0x00, 0x00 };
                _sender(packet);
                await Task.Delay(100);
            }

            LogBox.AppendText("─── PROBE STEP 2: Querying Feature Switches 0x01..0x25 ───" + Environment.NewLine);
            for (byte id = 0x01; id <= 0x25; id++)
            {
                if (id == 0x1D) continue; // BassWave already handled
                byte[] packet = { 0xAA, 0x09, 0x00, 0x00, 0x0D, 0x01, 0x00, 0x02, 0x00, 0x01, id };
                _sender(packet);
                await Task.Delay(100);
            }

            LogBox.AppendText("─── PROBE STEP 3: Querying Key/Gesture Mappings ───" + Environment.NewLine);
            byte[][] keyQueries = new[]
            {
                new byte[] { 0xAA, 0x08, 0x00, 0x00, 0x10, 0x01, 0x00, 0x01, 0x00, 0x01 },
                new byte[] { 0xAA, 0x08, 0x00, 0x00, 0x10, 0x01, 0x00, 0x01, 0x00, 0x02 },
                new byte[] { 0xAA, 0x08, 0x00, 0x00, 0x12, 0x01, 0x00, 0x01, 0x00, 0x01 },
                new byte[] { 0xAA, 0x08, 0x00, 0x00, 0x12, 0x01, 0x00, 0x01, 0x00, 0x02 },
                new byte[] { 0xAA, 0x08, 0x00, 0x00, 0x14, 0x01, 0x00, 0x01, 0x00, 0x01 },
                new byte[] { 0xAA, 0x08, 0x00, 0x00, 0x14, 0x01, 0x00, 0x01, 0x00, 0x02 },
            };
            foreach (var q in keyQueries)
            {
                _sender(q);
                await Task.Delay(100);
            }

            LogBox.AppendText("─── PROBE COMPLETE: Check [RECV] lines above for replies ───" + Environment.NewLine);
            if (AutoScrollCheck.IsChecked == true) LogBox.ScrollToEnd();
        }
        finally
        {
            ProbeBtn.IsEnabled = true;
            ProbeBtn.Content = "🔍 Probe Features";
        }
    }
}
