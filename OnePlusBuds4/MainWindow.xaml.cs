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
    private void OnClose(object sender, RoutedEventArgs e) => Close();
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
    private bool _suppressBassEvents; // true while a poll is syncing the BassWave UI, to avoid echoing commands back
    private bool? _bothInCase;        // both earbuds in the case? null = unknown yet (locks ANC when true)
    private bool? _anyInEar;          // at least one earbud in an ear? drives the "Worn" label
    private WinForms.NotifyIcon? _tray;
    private System.Windows.Threading.DispatcherTimer? _pollTimer;
    private readonly PacketLogWindow _logWindow = new();

    private record DeviceItem(string Name, string Mac)
    {
        public override string ToString() => Name;
    }

    public MainWindow()
    {
        InitializeComponent();
        SetupTray();
        Loaded += async (_, _) => { LoadDevices(); BuildBands(); PopulateSlotCombo(); InitGestureCombos(); SetControlsEnabled(false); await AutoConnectAsync(); };
        StateChanged += OnStateChanged;
        Closed += (_, _) => { _pollTimer?.Stop(); _buds?.Close(); _tray?.Dispose(); _logWindow.Close(); };
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
            SetStatus("Select a device and press Connect");
        }
        else
        {
            // No auto-detected device: user can paste a MAC if needed
            DeviceCombo.Text = "";
            SetStatus(devices.Count > 0
                ? "Pick a device or type a MAC, then Connect"
                : "No paired OnePlus Buds found — pair in Windows Bluetooth settings or paste MAC");
        }
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
        // Auto-connect to the first device in the list (already filtered for OnePlus Buds)
        var mac = SelectedMac();
        if (mac != null) await ConnectAsync(mac);
    }

    private async Task ConnectAsync(string? mac)
    {
        if (mac == null) { SetStatus("Pick a device or type a MAC first"); return; }
        if (_connecting) return;
        _connecting = true;
        _ready = false;
        _bothInCase = null; _anyInEar = null; // unknown until the earbuds report it
        SetControlsEnabled(false);
        ConnectBtn.IsEnabled = false;
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
            var timeout = Task.Delay(TimeSpan.FromSeconds(10));
            if (await Task.WhenAny(connectTask, timeout) == timeout)
            {
                _buds.Close();
                SetStatus("Connection timed out — try disconnecting and reconnecting the earbuds in Windows Bluetooth settings.");
                return;
            }
            await connectTask;
            _ready = true;
            SetControlsEnabled(true);
            UpdateWornText();              // reflect any wear state captured during the init broadcast
            SetStatus("Connected to " + mac);
            _buds.RequestFullState();      // ANC, EQ, BassWave, wear, custom EQ list, gestures, battery
            StartBatteryTimer();           // battery isn't pushed, so we poll it gently
        }
        catch (SocketException ex)
        {
            SetStatus($"Connection failed ({ex.SocketErrorCode}). Make sure Bluetooth is on and the earbuds are paired.");
        }
        catch (Exception)
        {
            SetStatus("Connection failed. Check the MAC and that the earbuds are paired.");
        }
        finally
        {
            _connecting = false;
            ConnectBtn.IsEnabled = true;
        }
    }

    private void SetControlsEnabled(bool enabled)
    {
        EqBalancedBtn.IsEnabled = enabled;
        EqVocalsBtn.IsEnabled = enabled;
        EqBassBtn.IsEnabled = enabled;
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
        AncBtn.IsEnabled = en;
        AdaptiveBtn.IsEnabled = en;
        TransBtn.IsEnabled = en;
        OffBtn.IsEnabled = en;
        HighBtn.IsEnabled = en;
        MidBtn.IsEnabled = en;
        LowBtn.IsEnabled = en;
        AutoBtn.IsEnabled = en;
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
    // OnPacketReceived). Battery level isn't pushed, so we poll just that.
    private void StartBatteryTimer()
    {
        _pollTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _pollTimer.Tick += (_, _) =>
        {
            if (!_ready || _buds == null) return;
            try { _buds.RequestBattery(); } catch { }
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

        // Change notifications (from the phone or a touch gesture) carry high
        // byte 0x05 in a compact, per-feature format. Rather than decode each
        // variant, treat any of them as "something changed" and re-pull the
        // full state, which comes back in the 0x81 form we already handle.
        if (d.Length >= 6 && d[5] == 0x05) RequestFullStateThrottled();
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

    private void OnDisconnected() => Dispatcher.BeginInvoke(async () => await TryReconnectAsync());

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
            Select(NoiseGroup, OffBtn);
            AncLevelsGrid.Visibility = Visibility.Collapsed;
            foreach (var b in LevelGroup) b.Tag = null;
        }
        else if (trans)
        {
            Select(NoiseGroup, TransBtn);
            AncLevelsGrid.Visibility = Visibility.Collapsed;
            foreach (var b in LevelGroup) b.Tag = null;
        }
        else if (adaptive)
        {
            Select(NoiseGroup, AdaptiveBtn);
            AncLevelsGrid.Visibility = Visibility.Collapsed;
            foreach (var b in LevelGroup) b.Tag = null;
        }
        else if (anc)
        {
            Select(NoiseGroup, AncBtn);
            AncLevelsGrid.Visibility = Visibility.Visible;
            var levelBtn = level switch
            {
                "High"     => HighBtn,
                "Moderate" => MidBtn,
                "Low"      => LowBtn,
                "Auto"     => AutoBtn,
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
        var btn = level switch { "High" => HighBtn, "Moderate" => MidBtn, "Low" => LowBtn, _ => null };
        foreach (var b in LevelGroup)
            if (b != AutoBtn) b.Tag = (b == btn) ? "sub" : null;
    }

    // While the earbuds aren't worn ANC is auto-off; show the Off pill in the
    // "auto" (light-yellow) style instead of the normal blue selection.
    private void MarkOffWhenNotWorn()
    {
        if (_anyInEar == false)
        {
            foreach (var b in NoiseGroup) b.Tag = null;
            foreach (var b in LevelGroup) b.Tag = null;
            AncLevelsGrid.Visibility = Visibility.Collapsed;
            OffBtn.Tag = "auto";
        }
    }

    // EQ preset highlight.
    private void ApplyEq(byte[] d)
    {
        var preset = BudsConnection.DecodeEqPreset(d);
        if (!preset.HasValue) return;
        var eqBtn = preset.Value switch
        {
            0x00 => EqBalancedBtn,
            0x01 => EqBassBtn,
            0x02 => EqVocalsBtn,
            _    => null
        };
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
        BatteryText.Text = "Battery   —";
        var mac = _buds.Mac;
        _ready = false;
        SetStatus($"Reconnecting to {mac}…");
        try
        {
            await Task.Run(() => _buds.Connect());
            _ready = true;
            SetStatus("Connected to " + mac);
            _buds.RequestFullState();
        }
        catch { SetStatus("Earbuds unavailable — press Connect to retry."); }
    }

    private void SetStatus(string s) => StatusText.Text = s;

    // ── Selection highlight ──
    private System.Windows.Controls.Button[] NoiseGroup => new[] { AncBtn, AdaptiveBtn, TransBtn, OffBtn };
    private System.Windows.Controls.Button[] LevelGroup => new[] { HighBtn, MidBtn, LowBtn, AutoBtn };
    private System.Windows.Controls.Button[] EqGroup => new[] { EqBalancedBtn, EqVocalsBtn, EqBassBtn };

    private static void Select(System.Windows.Controls.Button[] group, System.Windows.Controls.Button active)
    {
        foreach (var b in group) b.Tag = (b == active) ? "sel" : null;
    }

    // ── Noise Control Modes & Levels ──
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

    // ── EQ ──
    private void OnEqBalanced(object s, RoutedEventArgs e) { Select(EqGroup, EqBalancedBtn); Do(BudsConnection.Eq(0x00), "EQ Balanced"); }
    private void OnEqVocals(object s, RoutedEventArgs e) { Select(EqGroup, EqVocalsBtn); Do(BudsConnection.Eq(0x02), "EQ Clear Vocals"); }
    private void OnEqBass(object s, RoutedEventArgs e) { Select(EqGroup, EqBassBtn); Do(BudsConnection.Eq(0x01), "EQ Bass"); }

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
    private void SetupTray()
    {
        _tray = new WinForms.NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "OnePlus Buds 4",
            Visible = false
        };
        _tray.DoubleClick += (_, _) => ShowFromTray();
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Show", null, (_, _) => ShowFromTray());
        menu.Items.Add("Exit", null, (_, _) => { _tray!.Visible = false; System.Windows.Application.Current.Shutdown(); });
        _tray.ContextMenuStrip = menu;
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            Hide();
            if (_tray != null) { _tray.Visible = true; _tray.ShowBalloonTip(1000, "OnePlus Buds 4", "Running in the tray", WinForms.ToolTipIcon.None); }
        }
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        if (_tray != null) _tray.Visible = false;
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
