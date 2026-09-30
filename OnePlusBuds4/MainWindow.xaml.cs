using System;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Windows;
using WinForms = System.Windows.Forms;

namespace OnePlusBuds4;

public partial class MainWindow : Window
{
    // ── Custom title bar ──
    private void OnTitleBarDrag(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            // double-click: minimize
            WindowState = WindowState.Minimized;
        }
        else
        {
            DragMove();
        }
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnClose(object sender, RoutedEventArgs e)
    {
        _wasConnected = false;
        _reconnectTimer?.Stop();
        _pollTimer?.Stop();
        _buds?.Close();
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }
        _logWindow.AllowClose = true;
        _logWindow.Close();
        Environment.Exit(0);
    }
    private void OnAbout(object sender, RoutedEventArgs e) => new AboutWindow { Owner = this }.ShowDialog();
    private void OnOpenLog(object sender, RoutedEventArgs e)
    {
        _logWindow.Owner = this;
        _logWindow.Show();
        _logWindow.Activate();
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames();
        var res = Array.Find(name, n => n.EndsWith("app.ico", StringComparison.OrdinalIgnoreCase));
        if (res != null)
        {
            using var stream = asm.GetManifestResourceStream(res)!;
            return new System.Drawing.Icon(stream);
        }
        return System.Drawing.SystemIcons.Application;
    }

    private BudsConnection? _buds;
    private bool _ready;
    private bool _connecting;
    private bool _isPro3Mode;
    private bool _suppressBassEvents; // true while a poll is syncing the BassWave UI, to avoid echoing commands back
    private bool? _bothInCase;        // both earbuds in the case? null = unknown yet (locks ANC when true)
    private bool? _anyInEar;          // at least one earbud in an ear? drives the "Worn" label
    private bool _wasConnected;
    private bool _batteryNotificationShown;
    private string? _lastBatterySummary;
    private WinForms.NotifyIcon? _tray;
    private System.Windows.Threading.DispatcherTimer? _pollTimer;
    private System.Windows.Threading.DispatcherTimer? _reconnectTimer;
    private readonly PacketLogWindow _logWindow = new();

    [System.Runtime.InteropServices.DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    internal static void EnableDarkMode(Window window)
    {
        try
        {
            var helper = new System.Windows.Interop.WindowInteropHelper(window);
            if (helper.Handle != IntPtr.Zero)
            {
                int dark = 1;
                DwmSetWindowAttribute(helper.Handle, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref dark, sizeof(int));
                DwmSetWindowAttribute(helper.Handle, 19 /* DWMWA_USE_IMMERSIVE_DARK_MODE older */, ref dark, sizeof(int));
            }
        }
        catch { }
    }

    private record DeviceItem(string Name, string Mac)
    {
        public override string ToString() => Name;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    private static extern uint RegisterWindowMessage(string lpString);

    private uint _showWindowMsg;

    private const int WM_DEVICECHANGE = 0x0219;
    private const int DBT_DEVNODES_CHANGED = 0x0007;
    private const int DBT_DEVICEARRIVAL = 0x8000;
    private uint _taskbarCreatedMsg;

    public MainWindow() : this(false) { }

    public MainWindow(bool startMinimized)
    {
        InitializeComponent();
        SetupTray();

        var helper = new System.Windows.Interop.WindowInteropHelper(this);
        helper.EnsureHandle();

        EnableDarkMode(this);

        var source = System.Windows.Interop.HwndSource.FromHwnd(helper.Handle);
        _showWindowMsg = RegisterWindowMessage(App.ShowWindowMessageName);
        _taskbarCreatedMsg = RegisterWindowMessage("TaskbarCreated");
        source?.AddHook(WndProc);

        StateChanged += OnStateChanged;
        Closed += (_, _) =>
        {
            _wasConnected = false;
            _reconnectTimer?.Stop();
            _pollTimer?.Stop();
            _buds?.Close();
            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
                _tray = null;
            }
            _logWindow.AllowClose = true;
            _logWindow.Close();
            Environment.Exit(0);
        };

        if (startMinimized)
        {
            WindowState = WindowState.Minimized;
            Hide();
        }

        _ = InitAppAsync();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_showWindowMsg != 0 && (uint)msg == _showWindowMsg)
        {
            ShowFromTray();
            handled = true;
        }
        else if (_taskbarCreatedMsg != 0 && (uint)msg == _taskbarCreatedMsg)
        {
            if (_tray != null) _tray.Visible = true;
        }
        else if (msg == WM_DEVICECHANGE)
        {
            int wp = wParam.ToInt32();
            if (wp == DBT_DEVNODES_CHANGED || wp == DBT_DEVICEARRIVAL)
            {
                if (!_ready && !_connecting)
                {
                    var mac = SelectedMac();
                    if (mac != null)
                    {
                        string? devName = (DeviceCombo.SelectedItem as DeviceItem)?.Name;
                        if (BudsConnection.IsDeviceConnected(mac, devName))
                        {
                            _reconnectTimer?.Stop();
                            _ = ConnectAsync(mac);
                        }
                    }
                }
            }
        }
        return IntPtr.Zero;
    }

    private async Task InitAppAsync()
    {
        LoadDevices();
        BuildBands();
        PopulateSlotCombo();
        InitGestureCombos();
        SetControlsEnabled(false);
        UpdateStartupCheckState();
        await AutoConnectAsync();
    }

    // ── Device list ──
    private void LoadDevices()
    {
        var devices = BudsConnection.PairedDevices();
        DeviceCombo.Items.Clear();
        DeviceItem? preferred = null;
        foreach (var (name, mac) in devices)
        {
            var n = name.ToLowerInvariant();
            if (!(n.Contains("oneplus") && n.Contains("buds")))
                continue;
            var item = new DeviceItem(name, mac);
            DeviceCombo.Items.Add(item);
            if (preferred == null || n.Contains("buds 4")) preferred = item;
        }
        if (preferred != null)
        {
            DeviceCombo.SelectedItem = preferred;
            UpdateProfileForDevice(preferred.Name);
            SetStatus("Select a device and press Connect");
        }
        else
        {
            // No auto-detected device: user can paste a MAC if needed
            DeviceCombo.Text = "";
            UpdateProfileForDevice(null);
            SetStatus(devices.Count > 0
                ? "Pick a device or type a MAC, then Connect"
                : "No paired OnePlus Buds found — pair in Windows Bluetooth settings or paste MAC");
        }
    }

    private void OnDeviceSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        string? name = null;
        if (DeviceCombo.SelectedItem is DeviceItem item)
        {
            name = item.Name;
        }
        else if (!string.IsNullOrWhiteSpace(DeviceCombo.Text))
        {
            name = DeviceCombo.Text;
        }
        UpdateProfileForDevice(name);
    }

    private void UpdateProfileForDevice(string? name)
    {
        bool isPro3 = !string.IsNullOrEmpty(name) && name.Contains("3 Pro", StringComparison.OrdinalIgnoreCase);
        ApplyDeviceProfile(isPro3);
    }

    private void ApplyDeviceProfile(bool isPro3)
    {
        _isPro3Mode = isPro3;

        if (TitleText != null) TitleText.Text = isPro3 ? "OnePlus Buds Pro 3" : "OnePlus Buds";
        Title = isPro3 ? "OnePlus Buds Pro 3" : "OnePlus Buds";

        if (NoisePanelBuds4 != null) NoisePanelBuds4.Visibility = isPro3 ? Visibility.Collapsed : Visibility.Visible;
        if (NoisePanelPro3 != null) NoisePanelPro3.Visibility = isPro3 ? Visibility.Visible : Visibility.Collapsed;

        if (EarbudControlsCard != null) EarbudControlsCard.Visibility = isPro3 ? Visibility.Collapsed : Visibility.Visible;

        if (EqPanelBuds4 != null) EqPanelBuds4.Visibility = isPro3 ? Visibility.Collapsed : Visibility.Visible;
        if (EqPanelPro3 != null) EqPanelPro3.Visibility = isPro3 ? Visibility.Visible : Visibility.Collapsed;

        UpdateTrayTooltip(isPro3 ? "OnePlus Buds Pro 3" : "OnePlus Buds");

        UpdateAncEnabled();
    }

    // Returns the MAC from the selected item, or the typed text.
    private string? SelectedMac()
    {
        if (DeviceCombo.SelectedItem is DeviceItem d) return d.Mac;
        var t = DeviceCombo.Text?.Trim();
        return string.IsNullOrWhiteSpace(t) ? null : t;
    }

    private void OnRefreshDevices(object s, RoutedEventArgs e) => LoadDevices();

    private async void OnConnectClick(object s, RoutedEventArgs e) => await ConnectAsync(SelectedMac());

    private async Task AutoConnectAsync()
    {
        // Auto-connect to the preferred device if already connected to Windows,
        // otherwise start the watcher to catch the moment the user connects them.
        var mac = SelectedMac();
        if (mac == null) return;

        string? devName = (DeviceCombo.SelectedItem as DeviceItem)?.Name;
        if (BudsConnection.IsDeviceConnected(mac, devName))
        {
            await ConnectAsync(mac);
        }
        else
        {
            SetStatus("Waiting for earbuds to connect to Windows…");
            StartReconnectWatcher();
        }
    }

    private async Task ConnectAsync(string? mac)
    {
        if (mac == null) { SetStatus("Pick a device or type a MAC first"); return; }
        if (_connecting) return;

        string? devName = (DeviceCombo.SelectedItem as DeviceItem)?.Name ?? DeviceCombo.Text;
        if (string.IsNullOrWhiteSpace(devName))
        {
            foreach (var (pName, pMac) in BudsConnection.PairedDevices())
            {
                if (pMac.Equals(mac, StringComparison.OrdinalIgnoreCase))
                {
                    devName = pName;
                    break;
                }
            }
        }
        UpdateProfileForDevice(devName);
        _connecting = true;
        _ready = false;
        _bothInCase = null; _anyInEar = null; // unknown until the earbuds report it
        SetControlsEnabled(false);
        ConnectBtn.IsEnabled = false;
        _reconnectTimer?.Stop();
        _pollTimer?.Stop();
        _buds?.Close();
        _buds = new BudsConnection(mac);
        _buds.PacketReceived += OnPacketReceived; // instant updates pushed by the earbuds
        _buds.PacketReceived += d => _logWindow.LogPacket(d, true);
        _buds.PacketSent += d => _logWindow.LogPacket(d, false);
        _buds.Disconnected += OnDisconnected;
        _logWindow.SetSender(p => { try { _buds?.Send(p); } catch { } });
        SetStatus($"Connecting to {mac}…");
        try
        {
            var connectTask = Task.Run(() => _buds.Connect());
            var timeout = Task.Delay(TimeSpan.FromSeconds(6));
            if (await Task.WhenAny(connectTask, timeout) == timeout)
            {
                _buds.Close();
                SetStatus("Waiting for earbuds to connect…");
                StartReconnectWatcher();
                return;
            }
            await connectTask;
            _ready = true;
            _wasConnected = true;
            _batteryNotificationShown = false;
            _reconnectTimer?.Stop();
            SetControlsEnabled(true);
            UpdateWornText();              // reflect any wear state captured during the init broadcast
            SetStatus("Connected to " + mac);
            _buds.RequestBattery();        // Immediate priority query!
            _buds.RequestFullState();      // ANC, EQ, BassWave, wear, custom EQ list, gestures
            StartBatteryTimer();           // battery isn't pushed, so we poll it gently

            // Fallback notification in case battery packet is delayed
            _ = Task.Delay(5000).ContinueWith(_ => Dispatcher.BeginInvoke(() =>
            {
                if (!_batteryNotificationShown && _ready)
                {
                    _batteryNotificationShown = true;
                    string title = _isPro3Mode ? "OnePlus Buds Pro 3 Connected" : "OnePlus Buds Connected";
                    string msg = !string.IsNullOrWhiteSpace(_lastBatterySummary)
                        ? _lastBatterySummary.Replace("   ", "  •  ")
                        : "Connected successfully";
                    ShowNotification(title, msg, WinForms.ToolTipIcon.Info);
                }
            }));
        }
        catch (SocketException ex)
        {
            SetStatus($"Waiting for earbuds to connect… ({ex.SocketErrorCode})");
            StartReconnectWatcher();
        }
        catch (Exception)
        {
            SetStatus("Waiting for earbuds to connect…");
            StartReconnectWatcher();
        }
        finally
        {
            _connecting = false;
            ConnectBtn.IsEnabled = true;
        }
    }

    private void SetControlsEnabled(bool enabled)
    {
        // Buds 4 EQ
        EqBalancedBtn.IsEnabled = enabled;
        EqVocalsBtn.IsEnabled = enabled;
        EqBassBtn.IsEnabled = enabled;

        // Buds Pro 3 EQ
        EqBalancedBtnPro3.IsEnabled = enabled;
        EqBoldBtnPro3.IsEnabled = enabled;
        EqSerenadeBtnPro3.IsEnabled = enabled;
        EqBassBtnPro3.IsEnabled = enabled;
        EqDynBtnPro3.IsEnabled = enabled;

        BassToggle.IsEnabled = enabled;
        ApplyEqBtn.IsEnabled = enabled;
        SlotCombo.IsEnabled = enabled;
        GestureLeftBtn.IsEnabled = enabled;
        GestureRightBtn.IsEnabled = enabled;
        SingleTapCombo.IsEnabled = enabled;
        DoubleTapCombo.IsEnabled = enabled;
        TripleTapCombo.IsEnabled = enabled;
        SlideCombo.IsEnabled = enabled;
        TouchHoldCombo.IsEnabled = enabled;
        CallDoubleTapCombo.IsEnabled = enabled;
        CallTouchHoldCombo.IsEnabled = enabled;
        UpdateAncEnabled(); // ANC also depends on whether the earbuds are worn
    }

    // ANC can only be set while wearing the earbuds (and connected). While the
    // ANC is locked when neither earbud is in an ear (like the phone, which shows
    // "wear the earphones" in that case). While it's still unknown we keep it on.
    private void UpdateAncEnabled()
    {
        bool en = _ready && _anyInEar != false;
        // Buds 4
        if (AncBtn != null) AncBtn.IsEnabled = en;
        if (AdaptiveBtn != null) AdaptiveBtn.IsEnabled = en;
        if (TransBtn != null) TransBtn.IsEnabled = en;
        if (OffBtn != null) OffBtn.IsEnabled = en;
        if (HighBtn != null) HighBtn.IsEnabled = en;
        if (MidBtn != null) MidBtn.IsEnabled = en;
        if (LowBtn != null) LowBtn.IsEnabled = en;
        if (AutoBtn != null) AutoBtn.IsEnabled = en;

        // Buds Pro 3
        if (AncBtnPro3 != null) AncBtnPro3.IsEnabled = en;
        if (TransBtnPro3 != null) TransBtnPro3.IsEnabled = en;
        if (OffBtnPro3 != null) OffBtnPro3.IsEnabled = en;
        if (HighBtnPro3 != null) HighBtnPro3.IsEnabled = en;
        if (MidBtnPro3 != null) MidBtnPro3.IsEnabled = en;
        if (LowBtnPro3 != null) LowBtnPro3.IsEnabled = en;
        if (AutoBtnPro3 != null) AutoBtnPro3.IsEnabled = en;
    }

    // Reacts to the earbuds' wear status. Putting an earbud in / out of an ear
    // makes the earbuds pause or resume ANC, so re-read the state on any change.
    private void ApplyWear(BudsConnection.WearState w)
    {
        bool? was = _anyInEar;
        _bothInCase = w.BothInCase;
        _anyInEar = w.AnyInEar;
        UpdateAncEnabled();
        UpdateWornText();
        MarkOffWhenNotWorn();
        if (was != _anyInEar) RequestFullStateThrottled();
    }

    private void UpdateWornText()
    {
        if (!_ready) { WornText.Text = ""; return; }
        WornText.Text = _anyInEar switch { true => "Worn", false => "Not worn", null => "" };
    }

    // ANC/EQ/BassWave changes are pushed by the earbuds instantly (see
    // OnPacketReceived). Battery level isn't pushed, so we poll that and
    // keep ANC state continuously synchronized as a reliable heartbeat.
    private void StartBatteryTimer()
    {
        _pollTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(4)
        };
        _pollTimer.Tick += (_, _) =>
        {
            if (!_ready || _buds == null) return;
            try
            {
                _buds.RequestBattery();
                _buds.RequestAncNow();
            }
            catch { }
        };
        _pollTimer.Start();
    }

    // ── Incoming packets (reader thread → UI thread) ─────────────────
    // Every packet the earbuds send lands here. We route by response marker
    // and update only the matching part of the UI; unrelated packets fall
    // through untouched. Runs on the reader thread, so hop to the dispatcher.
    private void OnPacketReceived(byte[] d) => Dispatcher.BeginInvoke(() => HandlePacket(d));

    private void HandlePacket(byte[] d)
    {
        // Command acks carry high byte 0x84 and a status byte (0 = success).
        // Surface a failure if the earbuds rejected the command.
        if (d.Length >= 10 && d[5] == 0x84)
        {
            byte cmd = d[4];
            if (_pendingAck.TryGetValue(cmd, out var label))
            {
                _pendingAck.Remove(cmd);
                SetStatus(d[9] == 0 ? $"{label} ✓" : $"{label} failed (error {d[9]})");
            }
        }

        // Replies to our own queries carry high byte 0x81 — decode them directly.
        // (ANC has no usable getter: the 04 81 reply is static, so we rely solely
        // on the live 04 02 mask below for ANC mode/level.)
        if (BudsConnection.HasMarker(d, 0x0F, 0x81)) ApplyEq(d);
        if (BudsConnection.HasMarker(d, 0x24, 0x81)) ApplyBassValue(d);
        if (BudsConnection.HasMarker(d, 0x0D, 0x81)) ApplyBassOn(d);
        if (BudsConnection.HasMarker(d, 0x81, 0x25)) ApplyBattery(d);
        if (BudsConnection.HasMarker(d, 0x22, 0x81)) ApplyCustomEqList(d);
        if (BudsConnection.HasMarker(d, 0x08, 0x81)) ApplyGestures(d);

        // ANC mode: the connect-time reply (0C 81) and the live push (04 02),
        // plus a separate "current level" packet while Auto is adapting.
        var ancReply = BudsConnection.DecodeAncReply(d);
        if (ancReply.valid) ApplyAncState(ancReply.off, ancReply.trans, ancReply.anc, ancReply.auto, ancReply.adaptive, ancReply.level);
        var anc = BudsConnection.DecodeAncMode(d);
        if (anc.valid) ApplyAncState(anc.off, anc.trans, anc.anc, anc.auto, anc.adaptive, anc.level);
        var autoLevel = BudsConnection.DecodeAncAutoLevel(d) ?? BudsConnection.DecodeAncReplyAutoLevel(d);
        if (autoLevel != null) ApplyAutoLevel(autoLevel);

        // Wear status: the getEarBudsStatus reply (09 81, at connect) and the live
        // notification (cmd 04 02 / subtype 02). ANC is locked only when both
        // earbuds are in the case, matching the phone.
        var wear = BudsConnection.DecodeWear(d);
        if (wear.HasValue) ApplyWear(wear.Value);

        // Change notifications from the phone (cmd 0x05) or hardware gesture
        // events from the earbuds (cmd 04 02 with setting 0xF1) signal that
        // a setting or gesture (such as pinch to toggle ANC mode) occurred.
        // Re-query state immediately and shortly after to capture DSP mode changes.
        bool isGestureEvent = d.Length >= 10 && d[4] == 0x04 && d[5] == 0x02 && d[9] == 0xF1;
        bool isChangeNotification = d.Length >= 6 && d[5] == 0x05;
        if (isGestureEvent || isChangeNotification)
        {
            RequestFullStateThrottled();
            // Earbuds need ~200-350ms to finish their DSP tone/transition after a pinch;
            // query ANC again shortly after so the new mode is reliably captured.
            _ = Task.Delay(350).ContinueWith(_ => Dispatcher.BeginInvoke(() =>
            {
                if (_ready && _buds != null)
                {
                    try { _buds.RequestAncNow(); } catch { }
                }
            }));
        }
    }

    private DateTime _lastStateRequest;
    private void RequestFullStateThrottled()
    {
        if (_buds == null || !_ready) return;
        var now = DateTime.UtcNow;
        if ((now - _lastStateRequest).TotalMilliseconds < 250) return; // coalesce bursts
        _lastStateRequest = now;
        try { _buds.RequestFullState(); } catch { }
    }

    private void OnDisconnected()
    {
        Dispatcher.BeginInvoke(async () =>
        {
            if (_wasConnected)
            {
                _wasConnected = false;
                _batteryNotificationShown = false;
                string title = _isPro3Mode ? "OnePlus Buds Pro 3 Disconnected" : "OnePlus Buds Disconnected";
                string msg = !string.IsNullOrWhiteSpace(_lastBatterySummary)
                    ? $"Last battery: {_lastBatterySummary.Replace("   ", "  •  ")}"
                    : "Bluetooth connection lost.";
                ShowNotification(title, msg, WinForms.ToolTipIcon.Warning);
                UpdateTrayTooltip(title);
            }
            _ready = false;
            SetControlsEnabled(false);
            BatteryText.Text = "Battery   —";
            await TryReconnectAsync();
        });
    }

    // ── Command helper ──
    // We send, show a pending "…", and confirm with ✓ only when the earbuds ack
    // the command (or show the error if they reject it). Matched by command byte.
    private readonly System.Collections.Generic.Dictionary<byte, string> _pendingAck = new();

    private void Do(byte[] packet, string label)
    {
        if (!_ready || _buds == null) { SetStatus("Not connected"); return; }
        try
        {
            if (packet.Length > 4) _pendingAck[packet[4]] = label; // expect ack on this command byte
            _buds.Send(packet);
            SetStatus(label + "…");
        }
        catch (Exception ex) { SetStatus(label + " failed: " + ex.Message); }
    }

    // ── Apply a pushed state packet to the UI ────────────────────────
    private bool _ancAuto; // true while ANC is in Auto mode (level is adaptive)

    private void ApplyAncState(bool off, bool trans, bool anc, bool auto, bool adaptive, string level)
    {
        _ancAuto = auto;
        if (off)
        {
            Select(NoiseGroup, ActiveOffBtn);
            if (!_isPro3Mode) AncLevelsGrid.Visibility = Visibility.Collapsed;
            foreach (var b in LevelGroup) b.Tag = null;
        }
        else if (trans)
        {
            Select(NoiseGroup, _isPro3Mode ? TransBtnPro3 : TransBtn);
            if (!_isPro3Mode) AncLevelsGrid.Visibility = Visibility.Collapsed;
            foreach (var b in LevelGroup) b.Tag = null;
        }
        else if (adaptive && !_isPro3Mode)
        {
            Select(NoiseGroup, AdaptiveBtn);
            AncLevelsGrid.Visibility = Visibility.Collapsed;
            foreach (var b in LevelGroup) b.Tag = null;
        }
        else if (anc)
        {
            Select(NoiseGroup, _isPro3Mode ? AncBtnPro3 : AncBtn);
            if (!_isPro3Mode) AncLevelsGrid.Visibility = Visibility.Visible;
            var levelBtn = level switch
            {
                "High"     => _isPro3Mode ? HighBtnPro3 : HighBtn,
                "Moderate" => _isPro3Mode ? MidBtnPro3 : MidBtn,
                "Low"      => _isPro3Mode ? LowBtnPro3 : LowBtn,
                "Auto"     => _isPro3Mode ? AutoBtnPro3 : AutoBtn,
                _          => null
            };
            if (levelBtn != null) Select(LevelGroup, levelBtn);
        }
        MarkOffWhenNotWorn();
    }

    // In Auto mode, keep Auto highlighted and mark the level Auto is currently
    // applying with a softer "sub" highlight (mirrors the phone's "Auto: Low").
    private void ApplyAutoLevel(string level)
    {
        if (!_ancAuto) return;
        var autoBtn = _isPro3Mode ? AutoBtnPro3 : AutoBtn;
        var btn = level switch
        {
            "High"     => _isPro3Mode ? HighBtnPro3 : HighBtn,
            "Moderate" => _isPro3Mode ? MidBtnPro3 : MidBtn,
            "Low"      => _isPro3Mode ? LowBtnPro3 : LowBtn,
            _          => null
        };
        foreach (var b in LevelGroup)
            if (b != autoBtn) b.Tag = (b == btn) ? "sub" : null;
    }

    // While the earbuds aren't worn ANC is auto-off; show the Off pill in the
    // "auto" (light-yellow) style instead of the normal blue selection.
    private void MarkOffWhenNotWorn()
    {
        if (_anyInEar == false)
        {
            foreach (var b in NoiseGroup) b.Tag = null;
            foreach (var b in LevelGroup) b.Tag = null;
            if (!_isPro3Mode) AncLevelsGrid.Visibility = Visibility.Collapsed;
            ActiveOffBtn.Tag = "auto";
        }
    }

    // EQ preset highlight.
    private void ApplyEq(byte[] d)
    {
        var preset = BudsConnection.DecodeEqPreset(d);
        if (!preset.HasValue) return;
        System.Windows.Controls.Button? eqBtn;
        if (_isPro3Mode)
        {
            eqBtn = preset.Value switch
            {
                0x00 => EqBalancedBtnPro3,
                0x01 => EqBoldBtnPro3,
                0x02 => EqSerenadeBtnPro3,
                0x03 => EqBassBtnPro3,
                0x07 => EqDynBtnPro3,
                _    => null
            };
        }
        else
        {
            eqBtn = preset.Value switch
            {
                0x00 => EqBalancedBtn,
                0x01 => EqVocalsBtn,
                0x02 => EqBassBtn,
                _    => null
            };
        }
        if (eqBtn != null) Select(EqGroup, eqBtn);
    }

    // BassWave level. The slider's change handler sends commands, so suppress it.
    private void ApplyBassValue(byte[] d)
    {
        int? value = BudsConnection.DecodeBassWaveValue(d);
        if (!value.HasValue) return;
        _suppressBassEvents = true;
        BassSlider.Value = value.Value;
        BassValue.Text = (value.Value > 0 ? "+" : "") + value.Value;
        _suppressBassEvents = false;
    }

    // BassWave on/off. The toggle's handler sends commands, so suppress it.
    private void ApplyBassOn(byte[] d)
    {
        bool on = BudsConnection.DecodeBassWaveOn(d) == true;
        _suppressBassEvents = true;
        BassToggle.IsChecked = on;
        BassSlider.Visibility = on ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        _suppressBassEvents = false;
    }

    private static readonly System.Windows.Media.Brush ChargeGreen =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xC9, 0x5A));

    private void ApplyBattery(byte[] d)
    {
        var b = BudsConnection.DecodeBattery(d);
        if (b == null) return;
        _lastBatterySummary = b;

        // Build the line manually so the charging bolt (⚡) can be coloured green.
        BatteryText.Inlines.Clear();
        BatteryText.Inlines.Add(new System.Windows.Documents.Run("Battery   "));
        var pieces = b.Split('⚡');
        for (int i = 0; i < pieces.Length; i++)
        {
            BatteryText.Inlines.Add(new System.Windows.Documents.Run(pieces[i]));
            if (i < pieces.Length - 1)
                BatteryText.Inlines.Add(new System.Windows.Documents.Run("⚡") { Foreground = ChargeGreen });
        }

        string formatted = b.Replace("   ", "  •  ");
        UpdateTrayTooltip(formatted);

        if (!_batteryNotificationShown)
        {
            _batteryNotificationShown = true;
            string title = _isPro3Mode ? "OnePlus Buds Pro 3 Connected" : "OnePlus Buds Connected";
            ShowNotification(title, formatted, WinForms.ToolTipIcon.Info);
        }
    }

    private void ApplyCustomEqList(byte[] d)
    {
        var list = BudsConnection.DecodeCustomEqList(d);
        // The periodic refresh re-reads this list; skip the repopulate when it
        // hasn't changed (or while the user has the dropdown open), otherwise
        // clearing/refilling the combo would close it mid-interaction.
        if (SlotCombo.IsDropDownOpen) return;
        if (SameSlots(list, _customEqSlots)) return;
        _customEqSlots = list;
        PopulateSlotCombo();
    }

    private static bool SameSlots(System.Collections.Generic.List<BudsConnection.EqEntry> a,
                                  System.Collections.Generic.List<BudsConnection.EqEntry> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (a[i].Id != b[i].Id || a[i].Name != b[i].Name || a[i].IsSelected != b[i].IsSelected)
                return false;
        return true;
    }

    // One-shot silent reconnect when the channel drops (e.g. earbuds back in case).
    private async Task TryReconnectAsync()
    {
        if (_buds == null) return;
        var mac = _buds.Mac;
        _ready = false;
        SetStatus($"Reconnecting to {mac}…");
        try
        {
            await Task.Run(() => _buds.Connect());
            _ready = true;
            _wasConnected = true;
            _batteryNotificationShown = false;
            _reconnectTimer?.Stop();
            SetControlsEnabled(true);
            UpdateWornText();
            SetStatus("Connected to " + mac);
            _buds.RequestBattery();
            _buds.RequestFullState();
            StartBatteryTimer();

            // Fallback notification in case battery packet is delayed
            _ = Task.Delay(5000).ContinueWith(_ => Dispatcher.BeginInvoke(() =>
            {
                if (!_batteryNotificationShown && _ready)
                {
                    _batteryNotificationShown = true;
                    string title = _isPro3Mode ? "OnePlus Buds Pro 3 Connected" : "OnePlus Buds Connected";
                    string msg = !string.IsNullOrWhiteSpace(_lastBatterySummary)
                        ? _lastBatterySummary.Replace("   ", "  •  ")
                        : "Connected successfully";
                    ShowNotification(title, msg, WinForms.ToolTipIcon.Info);
                }
            }));
        }
        catch
        {
            SetStatus("Earbuds unavailable — press Connect to retry.");
            StartReconnectWatcher();
        }
    }

    private void StartReconnectWatcher()
    {
        if (_reconnectTimer == null)
        {
            _reconnectTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            _reconnectTimer.Tick += async (_, _) =>
            {
                if (_ready || _connecting) return;
                var mac = SelectedMac();
                if (mac == null) return;
                string? devName = (DeviceCombo.SelectedItem as DeviceItem)?.Name;
                if (BudsConnection.IsDeviceConnected(mac, devName))
                {
                    _reconnectTimer.Stop();
                    await ConnectAsync(mac);
                }
            };
        }
        _reconnectTimer.Start();
    }

    private void SetStatus(string s) => StatusText.Text = s;

    // ── Selection highlight ──
    private System.Windows.Controls.Button[] NoiseGroup => _isPro3Mode
        ? new[] { AncBtnPro3, TransBtnPro3, OffBtnPro3 }
        : new[] { AncBtn, AdaptiveBtn, TransBtn, OffBtn };

    private System.Windows.Controls.Button[] LevelGroup => _isPro3Mode
        ? new[] { HighBtnPro3, MidBtnPro3, LowBtnPro3, AutoBtnPro3 }
        : new[] { HighBtn, MidBtn, LowBtn, AutoBtn };

    private System.Windows.Controls.Button[] EqGroup => _isPro3Mode
        ? new[] { EqBalancedBtnPro3, EqBoldBtnPro3, EqSerenadeBtnPro3, EqBassBtnPro3, EqDynBtnPro3 }
        : new[] { EqBalancedBtn, EqVocalsBtn, EqBassBtn };

    private System.Windows.Controls.Button ActiveOffBtn => _isPro3Mode ? OffBtnPro3 : OffBtn;

    private static void Select(System.Windows.Controls.Button[] group, System.Windows.Controls.Button active)
    {
        foreach (var b in group) b.Tag = (b == active) ? "sel" : null;
    }

    // ── Noise Control Modes & Levels (Buds 4) ──
    private void OnAnc(object s, RoutedEventArgs e)
    {
        Select(NoiseGroup, AncBtn);
        AncLevelsGrid.Visibility = Visibility.Visible;
        bool hasLevel = false;
        foreach (var b in LevelGroup) if (b.Tag as string == "sel") { hasLevel = true; break; }
        if (!hasLevel) Select(LevelGroup, AutoBtn);
        Do(BudsConnection.Anc(0x02), "Noise Cancellation");
    }

    private void OnAdaptive(object s, RoutedEventArgs e)
    {
        Select(NoiseGroup, AdaptiveBtn);
        AncLevelsGrid.Visibility = Visibility.Collapsed;
        foreach (var b in LevelGroup) b.Tag = null;
        Do(BudsConnection.AncAdaptive(), "Adaptive");
    }

    private void OnTrans(object s, RoutedEventArgs e)
    {
        Select(NoiseGroup, TransBtn);
        AncLevelsGrid.Visibility = Visibility.Collapsed;
        foreach (var b in LevelGroup) b.Tag = null;
        Do(BudsConnection.Anc(0x04), "Transparency");
    }

    private void OnOff(object s, RoutedEventArgs e)
    {
        Select(NoiseGroup, OffBtn);
        AncLevelsGrid.Visibility = Visibility.Collapsed;
        foreach (var b in LevelGroup) b.Tag = null;
        Do(BudsConnection.Anc(0x01), "ANC off");
    }

    private void OnHigh(object s, RoutedEventArgs e)
    {
        Select(LevelGroup, HighBtn);
        Select(NoiseGroup, AncBtn);
        Do(BudsConnection.AncLevel(0x10), "ANC High");
    }

    private void OnMid(object s, RoutedEventArgs e)
    {
        Select(LevelGroup, MidBtn);
        Select(NoiseGroup, AncBtn);
        Do(BudsConnection.AncLevel(0x20), "ANC Moderate");
    }

    private void OnLow(object s, RoutedEventArgs e)
    {
        Select(LevelGroup, LowBtn);
        Select(NoiseGroup, AncBtn);
        Do(BudsConnection.AncLevel(0x40), "ANC Low");
    }

    private void OnAuto(object s, RoutedEventArgs e)
    {
        Select(LevelGroup, AutoBtn);
        Select(NoiseGroup, AncBtn);
        Do(BudsConnection.AncLevel(0x80), "ANC Auto");
    }

    // ── Noise Control (Buds Pro 3) ──
    private void OnAncPro3(object s, RoutedEventArgs e) { Select(NoiseGroup, AncBtnPro3); Do(BudsConnection.Anc(0x02), "ANC on"); }
    private void OnTransPro3(object s, RoutedEventArgs e) { Select(NoiseGroup, TransBtnPro3); foreach (var b in LevelGroup) b.Tag = null; Do(BudsConnection.Anc(0x04), "Transparency"); }
    private void OnOffPro3(object s, RoutedEventArgs e) { Select(NoiseGroup, OffBtnPro3); foreach (var b in LevelGroup) b.Tag = null; Do(BudsConnection.Anc(0x01), "ANC off"); }
    private void OnHighPro3(object s, RoutedEventArgs e) { Select(LevelGroup, HighBtnPro3); Select(NoiseGroup, AncBtnPro3); Do(BudsConnection.AncLevel(0x10), "ANC High"); }
    private void OnMidPro3(object s, RoutedEventArgs e) { Select(LevelGroup, MidBtnPro3); Select(NoiseGroup, AncBtnPro3); Do(BudsConnection.AncLevel(0x20), "ANC Moderate"); }
    private void OnLowPro3(object s, RoutedEventArgs e) { Select(LevelGroup, LowBtnPro3); Select(NoiseGroup, AncBtnPro3); Do(BudsConnection.AncLevel(0x40), "ANC Low"); }
    private void OnAutoPro3(object s, RoutedEventArgs e) { Select(LevelGroup, AutoBtnPro3); Select(NoiseGroup, AncBtnPro3); Do(BudsConnection.AncLevel(0x80), "ANC Auto"); }

    // ── EQ (Buds 4) ──
    private void OnEqBalanced(object s, RoutedEventArgs e) { Select(EqGroup, EqBalancedBtn); Do(BudsConnection.Eq(0x00), "EQ Balanced"); }
    private void OnEqVocals(object s, RoutedEventArgs e) { Select(EqGroup, EqVocalsBtn); Do(BudsConnection.Eq(0x01), "EQ Clear Vocals"); }
    private void OnEqBass(object s, RoutedEventArgs e) { Select(EqGroup, EqBassBtn); Do(BudsConnection.Eq(0x02), "EQ Bass"); }

    // ── EQ (Buds Pro 3) ──
    private void OnEqBalancedPro3(object s, RoutedEventArgs e) { Select(EqGroup, EqBalancedBtnPro3); Do(BudsConnection.Eq(0x00), "EQ Balanced"); }
    private void OnEqBoldPro3(object s, RoutedEventArgs e) { Select(EqGroup, EqBoldBtnPro3); Do(BudsConnection.Eq(0x01), "EQ Bold"); }
    private void OnEqSerenadePro3(object s, RoutedEventArgs e) { Select(EqGroup, EqSerenadeBtnPro3); Do(BudsConnection.Eq(0x02), "EQ Serenade"); }
    private void OnEqBassPro3(object s, RoutedEventArgs e) { Select(EqGroup, EqBassBtnPro3); Do(BudsConnection.Eq(0x03), "EQ Bass"); }
    private void OnEqDynPro3(object s, RoutedEventArgs e) { Select(EqGroup, EqDynBtnPro3); Do(BudsConnection.Eq(0x07), "EQ DynAudio"); }

    // ── Custom EQ (6 bands) ──
    private readonly System.Windows.Controls.Slider[] _bands = new System.Windows.Controls.Slider[6];

    private void BuildBands()
    {
        for (int i = 0; i < BudsConnection.EqBands.Length; i++)
        {
            int hz = BudsConnection.EqBands[i];
            var grid = new System.Windows.Controls.Grid { Margin = new Thickness(2, 3, 2, 3) };
            grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(58) });
            grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(34) });

            var lbl = new System.Windows.Controls.TextBlock
            {
                Text = hz >= 1000 ? $"{hz / 1000}k" : $"{hz}",
                Foreground = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFrom("#9AA0B0")!,
                VerticalAlignment = System.Windows.VerticalAlignment.Center, FontSize = 12
            };
            System.Windows.Controls.Grid.SetColumn(lbl, 0);

            var slider = new System.Windows.Controls.Slider
            {
                Minimum = -6, Maximum = 6, Value = 0,
                IsSnapToTickEnabled = true, TickFrequency = 1,
                VerticalAlignment = System.Windows.VerticalAlignment.Center
            };
            System.Windows.Controls.Grid.SetColumn(slider, 1);
            _bands[i] = slider;

            var val = new System.Windows.Controls.TextBlock
            {
                Text = "0", Foreground = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFrom("#4C77F0")!,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right, VerticalAlignment = System.Windows.VerticalAlignment.Center,
                FontSize = 12, FontWeight = FontWeights.SemiBold
            };
            System.Windows.Controls.Grid.SetColumn(val, 2);
            slider.ValueChanged += (_, ev) => { int v = (int)Math.Round(ev.NewValue); val.Text = (v > 0 ? "+" : "") + v; };

            grid.Children.Add(lbl); grid.Children.Add(slider); grid.Children.Add(val);
            BandRows.Children.Add(grid);
        }
    }

    private int[] BandValues()
    {
        var db = new int[6];
        for (int i = 0; i < 6; i++) db[i] = (int)Math.Round(_bands[i].Value);
        return db;
    }

    // ── Custom EQ slot management ──
    private System.Collections.Generic.List<BudsConnection.EqEntry> _customEqSlots = new();

    private record SlotItem(byte Id, string Label) { public override string ToString() => Label; }

    private string EqName() => string.IsNullOrWhiteSpace(EqNameBox.Text) ? "Custom" : EqNameBox.Text.Trim();

    private byte SelectedSlotId()
    {
        if (SlotCombo.SelectedItem is SlotItem s) return s.Id;
        return (byte)(SlotCombo.SelectedIndex + 1);
    }

    private void PopulateSlotCombo()
    {
        int prevIndex = SlotCombo.SelectedIndex;
        SlotCombo.Items.Clear();
        foreach (var eq in _customEqSlots)
            SlotCombo.Items.Add(new SlotItem(eq.Id, $"{eq.Name}{(eq.IsSelected ? " ✓" : "")}"));
        SlotCombo.SelectedIndex = prevIndex >= 0 && prevIndex < SlotCombo.Items.Count ? prevIndex : 0;
    }

    // Create/delete change the slot list, so we re-request it afterwards;
    // the refreshed list arrives as a packet and repopulates the combo.
    private void OnApplyCustomEq(object sender, RoutedEventArgs e)
    {
        if (!_ready || _buds == null) { SetStatus("Not connected"); return; }
        Do(BudsConnection.CustomEq(EqName(), BandValues(), op: 1, eqId: 0), $"Create EQ '{EqName()}'");
        _buds.RequestCustomEqList();
    }

    private void OnSelectEq(object sender, RoutedEventArgs e)
    {
        if (!_ready || _buds == null) { SetStatus("Not connected"); return; }
        byte id = SelectedSlotId();
        var eq = _customEqSlots.Find(x => x.Id == id);
        if (eq == null) { SetStatus($"Slot {id} is empty — create an EQ first"); return; }
        Do(BudsConnection.CustomEq(eq.Name, eq.DbValues, op: 2, eqId: id), $"Select EQ '{eq.Name}' (slot {id})");
    }

    private void OnDeleteEq(object sender, RoutedEventArgs e)
    {
        if (!_ready || _buds == null) { SetStatus("Not connected"); return; }
        byte id = SelectedSlotId();
        var eq = _customEqSlots.Find(x => x.Id == id);
        if (eq == null) { SetStatus($"Slot {id} is empty — nothing to delete"); return; }
        Do(BudsConnection.CustomEq(eq.Name, eq.DbValues, op: 3, eqId: id), $"Delete EQ '{eq.Name}' (slot {id})");
        _buds.RequestCustomEqList();
    }

    // ── BassWave ──
    // BassWave has two commands: a feature on/off switch (feature id 0x1D) and a
    // level (-5..+5). Turning it on also re-sends the current level.
    private void OnBassToggle(object sender, RoutedEventArgs e)
    {
        if (_suppressBassEvents) return; // change came from a poll, not the user
        bool on = BassToggle.IsChecked == true;
        BassSlider.Visibility = on ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        if (!_ready || _buds == null) return;
        int v = (int)Math.Round(BassSlider.Value);
        // The on/off switch is the command we confirm; turning on also re-sends the level.
        Do(BudsConnection.Switch(0x1D, on), on ? $"BassWave on ({(v > 0 ? "+" : "")}{v})" : "BassWave off");
        if (on) { try { _buds.Send(BudsConnection.BassWave(v)); } catch { } }
    }

    private void OnBassChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        int v = (int)Math.Round(e.NewValue);
        if (BassValue != null) BassValue.Text = (v > 0 ? "+" : "") + v;
        if (_suppressBassEvents) return; // change came from a poll, not the user
        if (!_ready || _buds == null || BassToggle.IsChecked != true) return;
        Do(BudsConnection.BassWave(v), $"BassWave {(v > 0 ? "+" : "")}{v}");
    }

    // ── System tray ──
    private WinForms.ToolStripMenuItem? _trayStartupItem;

    private void SetupTray()
    {
        _tray = new WinForms.NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = _isPro3Mode ? "OnePlus Buds Pro 3" : "OnePlus Buds",
            Visible = true
        };
        _tray.DoubleClick += (_, _) => ShowFromTray();
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Show", null, (_, _) => ShowFromTray());

        _trayStartupItem = new WinForms.ToolStripMenuItem("Start with Windows")
        {
            CheckOnClick = true,
            Checked = IsRunOnStartupEnabled()
        };
        _trayStartupItem.Click += (_, _) =>
        {
            SetRunOnStartup(_trayStartupItem.Checked);
            UpdateStartupCheckState();
        };
        menu.Items.Add(_trayStartupItem);

        menu.Items.Add(new WinForms.ToolStripSeparator());

        menu.Items.Add("Exit", null, (_, _) =>
        {
            _wasConnected = false;
            _reconnectTimer?.Stop();
            _pollTimer?.Stop();
            _buds?.Close();
            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
                _tray = null;
            }
            _logWindow.AllowClose = true;
            _logWindow.Close();
            Environment.Exit(0);
        });
        _tray.ContextMenuStrip = menu;
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            Hide();
        }
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Focus();
    }

    // ── Startup with Windows ──
    private const string StartupRegKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupValueName = "OnePlusBuds";

    public static bool IsRunOnStartupEnabled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(StartupRegKey, false);
            return key?.GetValue(StartupValueName) != null;
        }
        catch
        {
            return false;
        }
    }

    public static void SetRunOnStartup(bool enable)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(StartupRegKey, true);
            if (key == null) return;
            if (enable)
            {
                string exePath = Environment.ProcessPath ?? "";
                if (string.IsNullOrEmpty(exePath))
                {
                    try { exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? ""; } catch { }
                }
                if (!string.IsNullOrEmpty(exePath))
                {
                    key.SetValue(StartupValueName, $"\"{exePath}\" --minimized");
                }
            }
            else
            {
                key.DeleteValue(StartupValueName, false);
            }
        }
        catch { }
    }

    internal void UpdateStartupCheckState()
    {
        bool enabled = IsRunOnStartupEnabled();
        if (_trayStartupItem != null) _trayStartupItem.Checked = enabled;
    }

    private static bool _aumidRegistered;
    private static void RegisterAumid()
    {
        if (_aumidRegistered) return;
        _aumidRegistered = true;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes\AppUserModelId\OnePlusBuds");
            key?.SetValue("DisplayName", "OnePlus Buds");
            key?.SetValue("ShowInSettings", 1, Microsoft.Win32.RegistryValueKind.DWord);

            using var notifKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\OnePlusBuds");
            notifKey?.SetValue("Enabled", 1, Microsoft.Win32.RegistryValueKind.DWord);
            notifKey?.SetValue("ShowInActionCenter", 1, Microsoft.Win32.RegistryValueKind.DWord);
        }
        catch { }
    }

    private static void ShowToastNotification(string title, string message)
    {
        Task.Run(() =>
        {
            try
            {
                RegisterAumid();

                string t = title.Replace("'", "''");
                string m = message.Replace("'", "''");

                string script = @"
[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null
$xml = [Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent([Windows.UI.Notifications.ToastTemplateType]::ToastText02)
$nodes = $xml.GetElementsByTagName('text')
$nodes.Item(0).AppendChild($xml.CreateTextNode('" + t + @"')) | Out-Null
$nodes.Item(1).AppendChild($xml.CreateTextNode('" + m + @"')) | Out-Null
$toast = [Windows.UI.Notifications.ToastNotification]::new($xml)
[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('OnePlusBuds').Show($toast)
";
                byte[] bytes = System.Text.Encoding.Unicode.GetBytes(script);
                string encoded = Convert.ToBase64String(bytes);

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand " + encoded,
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                using var p = System.Diagnostics.Process.Start(psi);
                p?.WaitForExit(4000);
            }
            catch { }
        });
    }

    private DateTime _lastNotificationTime = DateTime.MinValue;
    private string _lastNotificationContent = "";

    private void ShowNotification(string title, string message, WinForms.ToolTipIcon icon)
    {
        var now = DateTime.UtcNow;
        string key = $"{title}::{message}";
        if (key == _lastNotificationContent && (now - _lastNotificationTime).TotalSeconds < 3) return;
        _lastNotificationTime = now;
        _lastNotificationContent = key;

        if (_tray != null)
        {
            Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    if (!_tray.Visible) _tray.Visible = true;
                    _tray.BalloonTipTitle = title;
                    _tray.BalloonTipText = message;
                    _tray.BalloonTipIcon = icon;
                    _tray.ShowBalloonTip(4000);
                }
                catch { }
            });
        }
    }

    private void UpdateTrayTooltip(string text)
    {
        if (_tray == null) return;
        try
        {
            if (text.Length > 63) text = text.Substring(0, 60) + "...";
            _tray.Text = text;
        }
        catch { }
    }

    // ── Earbud Controls (Gestures) ──
    private byte _selectedGestureSide = 1; // 1 = Left, 2 = Right
    private bool _suppressGestureEvents;
    private readonly System.Collections.Generic.Dictionary<(byte side, byte cat, byte gid), byte> _gestureMap = new();

    private record GestureActionItem(byte ActionId, string Label)
    {
        public override string ToString() => Label;
    }

    private static readonly GestureActionItem[] SingleTapActions =
    {
        new(0x01, "Play / Pause"),
        new(0x00, "None")
    };

    private static readonly GestureActionItem[] DoubleTapActions =
    {
        new(0x06, "Next track"),
        new(0x05, "Previous track"),
        new(0x01, "Play / Pause"),
        new(0x03, "Voice Assistant"),
        new(0x00, "None")
    };

    private static readonly GestureActionItem[] TripleTapActions =
    {
        new(0x03, "Voice Assistant"),
        new(0x05, "Previous track"),
        new(0x06, "Next track"),
        new(0x00, "None")
    };

    private static readonly GestureActionItem[] SlideActions =
    {
        new(0x08, "Volume control"),
        new(0x00, "None")
    };

    private static readonly GestureActionItem[] TouchHoldActions =
    {
        new(0x07, "Noise control"),
        new(0x03, "Voice Assistant"),
        new(0x00, "None")
    };

    private static readonly GestureActionItem[] CallDoubleTapActions =
    {
        new(0x1D, "Answer / End call"),
        new(0x00, "None")
    };

    private static readonly GestureActionItem[] CallTouchHoldActions =
    {
        new(0x1C, "Decline call"),
        new(0x00, "None")
    };

    private void InitGestureCombos()
    {
        SingleTapCombo.ItemsSource = SingleTapActions;
        DoubleTapCombo.ItemsSource = DoubleTapActions;
        TripleTapCombo.ItemsSource = TripleTapActions;
        SlideCombo.ItemsSource = SlideActions;
        TouchHoldCombo.ItemsSource = TouchHoldActions;
        CallDoubleTapCombo.ItemsSource = CallDoubleTapActions;
        CallTouchHoldCombo.ItemsSource = CallTouchHoldActions;
        UpdateGestureCombos();
    }

    private void OnGestureLeftTab(object sender, RoutedEventArgs e)
    {
        _selectedGestureSide = 1;
        GestureLeftBtn.Tag = "sel";
        GestureRightBtn.Tag = null;
        UpdateGestureCombos();
    }

    private void OnGestureRightTab(object sender, RoutedEventArgs e)
    {
        _selectedGestureSide = 2;
        GestureRightBtn.Tag = "sel";
        GestureLeftBtn.Tag = null;
        UpdateGestureCombos();
    }

    private byte GetAction(byte side, byte cat, byte gid, byte defaultAct) =>
        _gestureMap.TryGetValue((side, cat, gid), out var act) ? act : defaultAct;

    private static void SelectComboAction(System.Windows.Controls.ComboBox cb, GestureActionItem[] items, byte actionId)
    {
        for (int i = 0; i < items.Length; i++)
        {
            if (items[i].ActionId == actionId)
            {
                cb.SelectedIndex = i;
                return;
            }
        }
        if (items.Length > 0 && cb.SelectedIndex < 0) cb.SelectedIndex = 0;
    }

    private void UpdateGestureCombos()
    {
        _suppressGestureEvents = true;
        try
        {
            SelectComboAction(SingleTapCombo, SingleTapActions, GetAction(_selectedGestureSide, 1, 1, 0x01));
            SelectComboAction(DoubleTapCombo, DoubleTapActions, GetAction(_selectedGestureSide, 1, 2, 0x06));
            SelectComboAction(TripleTapCombo, TripleTapActions, GetAction(_selectedGestureSide, 1, 3, _selectedGestureSide == 1 ? (byte)0x03 : (byte)0x05));
            SelectComboAction(SlideCombo, SlideActions, GetAction(_selectedGestureSide, 1, 4, 0x08));
            SelectComboAction(TouchHoldCombo, TouchHoldActions, GetAction(_selectedGestureSide, 1, 5, 0x07));
            SelectComboAction(CallDoubleTapCombo, CallDoubleTapActions, GetAction(_selectedGestureSide, 6, 2, 0x1D));
            SelectComboAction(CallTouchHoldCombo, CallTouchHoldActions, GetAction(_selectedGestureSide, 6, 6, 0x1C));
        }
        finally
        {
            _suppressGestureEvents = false;
        }
    }

    private void ApplyGestures(byte[] d)
    {
        var entries = BudsConnection.DecodeGestures(d);
        if (entries.Count == 0) return;
        foreach (var e in entries)
        {
            _gestureMap[(e.Side, e.Category, e.GestureId)] = e.ActionId;
        }
        UpdateGestureCombos();
    }

    private void OnGestureComboChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_suppressGestureEvents || !_ready || _buds == null) return;
        if (sender is not System.Windows.Controls.ComboBox cb || cb.SelectedItem is not GestureActionItem item) return;

        byte cat = 1;
        byte gid = 1;
        string name = "Gesture";

        if (cb == SingleTapCombo)          { cat = 1; gid = 1; name = "Single-tap"; }
        else if (cb == DoubleTapCombo)     { cat = 1; gid = 2; name = "Double-tap"; }
        else if (cb == TripleTapCombo)     { cat = 1; gid = 3; name = "Triple-tap"; }
        else if (cb == SlideCombo)         { cat = 1; gid = 4; name = "Slide"; }
        else if (cb == TouchHoldCombo)     { cat = 1; gid = 5; name = "Touch & hold"; }
        else if (cb == CallDoubleTapCombo) { cat = 6; gid = 2; name = "Call Double-tap"; }
        else if (cb == CallTouchHoldCombo) { cat = 6; gid = 6; name = "Call Long touch"; }

        _gestureMap[(_selectedGestureSide, cat, gid)] = item.ActionId;
        string sideName = _selectedGestureSide == 1 ? "Left" : "Right";
        Do(BudsConnection.SetGesture(_selectedGestureSide, cat, gid, item.ActionId), $"{sideName} {name} -> {item.Label}");
    }
}
