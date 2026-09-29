using System;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace OnePlusBuds4;

/// <summary>Raw SOCKADDR_BTH endpoint for an RFCOMM channel (mirrors Python's winsock connect).</summary>
internal sealed class BtEndPoint : EndPoint
{
    private readonly ulong _addr;
    private readonly int _port;
    public BtEndPoint(ulong addr, int port) { _addr = addr; _port = port; }
    public override AddressFamily AddressFamily => (AddressFamily)32; // AF_BTH

    public override SocketAddress Serialize()
    {
        var sa = new SocketAddress((AddressFamily)32, 30); // SOCKADDR_BTH = 30 bytes
        for (int i = 0; i < 8; i++) sa[2 + i] = (byte)((_addr >> (8 * i)) & 0xFF); // BTH_ADDR (LE)
        // bytes 10..25 = serviceClassId GUID (left zero)
        sa[26] = (byte)(_port & 0xFF);          // port (RFCOMM channel), ULONG LE
        sa[27] = (byte)((_port >> 8) & 0xFF);
        return sa;
    }

    public override EndPoint Create(SocketAddress socketAddress) => this;

    public static ulong ParseMac(string mac)
    {
        var hex = mac.Replace(":", "").Replace("-", "").Trim();
        return Convert.ToUInt64(hex, 16);
    }
}

/// <summary>
/// Persistent RFCOMM connection to OnePlus Buds 4 (Bluetooth Classic, channel 15).
/// </summary>
public class BudsConnection : IDisposable
{
    public const int CHANNEL = 15;
    private readonly string _mac;
    private Socket? _sock;
    private readonly object _lock = new();

    // Windows raw Bluetooth socket constants
    private const AddressFamily AF_BTH = (AddressFamily)32;        // AF_BTH
    private const ProtocolType BTHPROTO_RFCOMM = (ProtocolType)3;  // BTHPROTO_RFCOMM

    public BudsConnection(string mac) => _mac = mac;

    public string Mac => _mac;
    public bool IsConnected => _sock?.Connected ?? false;

    /// <summary>List paired Bluetooth audio devices from the Windows registry as (name, MAC).</summary>
    public static System.Collections.Generic.List<(string Name, string Mac)> PairedDevices()
    {
        var list = new System.Collections.Generic.List<(string, string)>();
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices");
            if (key == null) return list;
            foreach (var sub in key.GetSubKeyNames())
            {
                if (sub.Length != 12) continue; // MAC without separators
                string pretty = string.Join(":", System.Linq.Enumerable.Range(0, 6)
                    .Select(i => sub.Substring(i * 2, 2).ToUpperInvariant()));
                string name = pretty;
                using (var dk = key.OpenSubKey(sub))
                {
                    var nm = dk?.GetValue("Name");
                    if (nm is byte[] b) name = Encoding.UTF8.GetString(b).TrimEnd('\0');
                    else if (nm is string sx) name = sx;
                }
                list.Add((name, pretty));
            }
        }
        catch { /* needs admin for some keys; fall back to manual MAC */ }
        return list;
    }

    // ── Fixed packets ───────────────────────────────────────────────
    static readonly byte[] HELLO    = { 0xAA, 0x07, 0x00, 0x00, 0x00, 0x01, 0x23, 0x00, 0x00, 0x12 };
    static readonly byte[] REGISTER = { 0xAA, 0x0C, 0x00, 0x00, 0x00, 0x85, 0x41, 0x05, 0x00, 0x00, 0xB5, 0x50, 0xA0, 0x69 };

    // ── Packet builders ─────────────────────────────────────────────
    public static byte[] Anc(byte mode) => new byte[] { 0xAA, 0x0A, 0x00, 0x00, 0x04, 0x04, 0x42, 0x03, 0x00, 0x01, 0x01, mode };
    // mode: 0x02=ANC on, 0x01=off, 0x04=transparency
    public static byte[] AncMask(ushort mask) => new byte[] {
        0xAA, 0x0B, 0x00, 0x00, 0x04, 0x04, 0x42, 0x04, 0x00, 0x01, 0x01, (byte)(mask & 0xFF), (byte)((mask >> 8) & 0xFF)
    };
    public static byte[] AncLevel(byte hi) => AncMask(hi);
    // hi: 0x10=High, 0x20=Moderate, 0x40=Low, 0x80=Auto
    public static byte[] AncAdaptive() => AncMask(0x0800);
    public static byte[] Eq(byte preset) => new byte[] { 0xAA, 0x08, 0x00, 0x00, 0x06, 0x04, 0x00, 0x01, 0x00, preset };
    public static byte[] BassWave(int value)
    {
        value = Math.Clamp(value, -5, 5);
        return new byte[] { 0xAA, 0x0A, 0x00, 0x00, 0x1B, 0x04, 0x00, 0x03, 0x00, 0xFB, 0x05, (byte)value };
    }
    public static byte[] Switch(byte feature, bool on) =>
        new byte[] { 0xAA, 0x09, 0x00, 0x00, 0x03, 0x04, 0x00, 0x02, 0x00, feature, (byte)(on ? 1 : 0) };
    public static byte[] SetGesture(byte side, byte category, byte gestureId, byte actionId) =>
        new byte[] { 0xAA, 0x0B, 0x00, 0x00, 0x08, 0x04, 0x00, 0x04, 0x00, side, category, gestureId, actionId };
    // ── State queries (GET) ─────────────────────────────────────────
    // Function codes come from the HeyMelody app (Protocol.java). The high
    // byte 0x01 marks a request; the earbuds reply with the same code but
    // high byte 0x81, which is how the decoders below find their payload.
    static readonly byte[] BATTERY       = { 0xAA, 0x07, 0x00, 0x00, 0x06, 0x01, 0x25, 0x00, 0x00 };
    static readonly byte[] GET_CUSTOM_EQ = { 0xAA, 0x07, 0x00, 0x00, 0x22, 0x01, 0x00, 0x00, 0x00 };
    static readonly byte[] GET_EQ        = { 0xAA, 0x07, 0x00, 0x00, 0x0F, 0x01, 0x00, 0x00, 0x00 }; // fn 271
    static readonly byte[] GET_BW_VALUE  = { 0xAA, 0x07, 0x00, 0x00, 0x24, 0x01, 0x00, 0x00, 0x00 }; // fn 292 (BassWave level)
    static readonly byte[] GET_GESTURES  = { 0xAA, 0x07, 0x00, 0x00, 0x08, 0x01, 0x00, 0x00, 0x00 }; // fn 264 (Key mapping / gestures)
    public void RequestGestures() => Send(GET_GESTURES);
    // fn 269 = getFeatureSwitchStatus. Unlike the others this needs a payload:
    // [count][featureId...]. BassWave is feature id 0x1D (29), so we ask for
    // exactly that one. The on/off state lives here, not in fn 291 as one might expect.
    static readonly byte[] GET_BW_SWITCH = { 0xAA, 0x09, 0x00, 0x00, 0x0D, 0x01, 0x00, 0x02, 0x00, 0x01, 0x1D };
    // fn 265 (0x0109) = getEarBudsStatus → reply 09 81 with the wear/box status.
    // This is how we learn the worn state at connect (live changes come as 04 02).
    static readonly byte[] GET_WEAR = { 0xAA, 0x07, 0x00, 0x00, 0x09, 0x01, 0x00, 0x00, 0x00 };
    public void RequestWear() => Send(GET_WEAR);

    // fn 268 (0x010C): payload {1,1} = current mode, {4,1} = the level Auto is
    // currently using. Both reply with marker 0C 81 (field byte tells them apart).
    static readonly byte[] GET_ANC_NOW  = { 0xAA, 0x09, 0x00, 0x00, 0x0C, 0x01, 0x00, 0x02, 0x00, 0x01, 0x01 };
    static readonly byte[] GET_ANC_AUTO = { 0xAA, 0x09, 0x00, 0x00, 0x0C, 0x01, 0x00, 0x02, 0x00, 0x04, 0x01 };
    public void RequestAncNow() { Send(GET_ANC_NOW); Send(GET_ANC_AUTO); }

    // Custom EQ (function 1048 = cmd16 18 04). 6 fixed bands, gain -6..+6 dB.
    public static readonly int[] EqBands = { 62, 250, 1000, 4000, 8000, 16000 };
    public static byte[] CustomEq(string name, int[] db, byte op = 1, byte eqId = 0)
    {
        var nameB = Encoding.UTF8.GetBytes(name ?? "");
        var pl = new System.Collections.Generic.List<byte>
        {
            op, unchecked((byte)-6), 6, eqId, (byte)nameB.Length
        };
        pl.AddRange(nameB);
        pl.Add((byte)EqBands.Length);
        for (int i = 0; i < EqBands.Length; i++)
        {
            int f = EqBands[i];
            pl.Add((byte)(f & 0xFF));
            pl.Add((byte)((f >> 8) & 0xFF));
            pl.Add(unchecked((byte)Math.Clamp(db[i], -6, 6)));
        }
        int plen = pl.Count;
        var body = new System.Collections.Generic.List<byte>
        {
            0x00, 0x00, 0x18, 0x04, 0x00, (byte)(plen & 0xFF), (byte)((plen >> 8) & 0xFF)
        };
        body.AddRange(pl);
        var frame = new System.Collections.Generic.List<byte> { 0xAA, (byte)body.Count };
        frame.AddRange(body);
        return frame.ToArray();
    }

    // ── Connection & notification listener ──────────────────────────
    // The earbuds push a packet whenever a setting changes — from this app,
    // from the phone, or from a touch gesture. We mirror the HeyMelody model:
    // a background thread reads the socket continuously and raises one event
    // per complete packet. Commands are write-only; their replies (and any
    // unsolicited change notifications) arrive through that same event.

    /// <summary>Raised on the reader thread for every complete packet received.</summary>
    public event Action<byte[]>? PacketReceived;
    /// <summary>Raised whenever a packet is transmitted.</summary>
    public event Action<byte[]>? PacketSent;
    /// <summary>Raised on the reader thread when the channel drops.</summary>
    public event Action? Disconnected;

    private Thread? _reader;
    private volatile bool _readerRun;

    public void Connect()
    {
        lock (_lock)
        {
            StopReader();
            Close();
            var ep = new BtEndPoint(BtEndPoint.ParseMac(_mac), CHANNEL);
            _sock = new Socket(AF_BTH, SocketType.Stream, BTHPROTO_RFCOMM);
            try { _sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true); } catch { }

            _sock.Connect(ep);

            // During the handshake we read inline (timeouts tell us "no more data").
            _sock.ReceiveTimeout = 1500;
            _sock.SendTimeout = 2000;
            WriteRaw(HELLO); Thread.Sleep(700); DrainQuiet();
            WriteRaw(REGISTER);
        }
        // Start the reader right away (without draining) so it captures the
        // initial state broadcast the earbuds send after REGISTER — that's how
        // we learn the current wear/ANC/EQ state immediately, like the phone.
        StartReader();
        Thread.Sleep(600); // give that broadcast a moment before the caller queries
    }

    /// <summary>Send a command. Fire-and-forget — any reply comes back via <see cref="PacketReceived"/>.</summary>
    public void Send(byte[] packet)
    {
        lock (_lock)
        {
            if (_sock == null) throw new InvalidOperationException("Not connected");
            WriteRaw(packet);
        }
    }

    // Convenience wrappers so callers read like intentions, not byte arrays.
    public void RequestBattery()      => Send(BATTERY);
    public void RequestCustomEqList() => Send(GET_CUSTOM_EQ);
    public void RequestEqState()      => Send(GET_EQ);
    public void RequestBassWaveValue()=> Send(GET_BW_VALUE);
    public void RequestBassWaveOn()   => Send(GET_BW_SWITCH);

    // Asks the earbuds for every readable setting at once.
    public void RequestFullState()
    {
        RequestAncNow();
        RequestEqState();
        RequestBassWaveValue();
        RequestBassWaveOn();
        RequestWear();
        RequestCustomEqList();
        RequestGestures();
        RequestBattery();
    }

    private void WriteRaw(byte[] data)
    {
        _sock!.Send(data);
        PacketSent?.Invoke(data);
    }

    public static string FormatHex(byte[] d) => BitConverter.ToString(d).Replace("-", " ");

    public static string AnnotatePacket(byte[] d, bool incoming)
    {
        if (d == null || d.Length < 6) return "";
        byte cmdLo = d[4], cmdHi = d[5];
        int cmd16 = cmdLo | (cmdHi << 8);

        if (!incoming)
        {
            return cmd16 switch
            {
                0x0100 => "Hello handshake",
                0x8500 => "Register handshake",
                0x0404 => "Set ANC (cmd 0x0404)",
                0x0408 => $"Set Gesture (side {d[^4]}, cat {d[^3]}, gid {d[^2]} -> act {d[^1]})",
                0x0406 => $"Set EQ preset {d[^1]}",
                0x041B => $"Set BassWave {(sbyte)d[^1]}",
                0x0403 => $"Set Switch {d[^2]}={(d[^1] == 1 ? "on" : "off")}",
                0x0418 => "Set Custom EQ",
                0x0106 => "Query Battery",
                0x0108 => "Query Gestures",
                0x0109 => "Query Wear",
                0x010C => "Query ANC State",
                0x010F => "Query EQ Preset",
                0x010D => "Query Switch Status",
                0x0122 => "Query Custom EQ",
                0x0124 => "Query BassWave Value",
                _ => $"Cmd 0x{cmd16:X4}"
            };
        }

        // Incoming packets
        if (cmdHi == 0x84 && d.Length >= 10)
        {
            return $"Ack for cmd 0x{cmdLo:X2} (status={d[9]}{(d[9] == 0 ? " OK" : " ERR")})";
        }
        if (cmdHi == 0x05)
        {
            var st = DecodeStatus(d);
            return st != null ? $"Change Notification: {st}" : "Change Notification (0x05)";
        }
        if (cmdHi == 0x02 && cmdLo == 0x04)
        {
            if (d.Length >= 10 && d[9] == 0xF1)
            {
                string side = (d.Length > 10 && d[10] == 1) ? "Left" : (d.Length > 10 && d[10] == 2 ? "Right" : "");
                return $"Gesture Event ({side}): {FormatHex(d[9..])}";
            }
            if (d.Length >= 14 && (d[7] | (d[8] << 8)) == 5 && d[9] == 0x03 && d[10] == 0x01)
            {
                int v = d[12] | (d[13] << 8);
                string desc = v switch
                {
                    0x0008 => "Off",
                    0x0100 => "Transparency",
                    0x0800 => "Adaptive",
                    0x0010 => "ANC High",
                    0x0020 => "ANC Moderate",
                    0x0040 => "ANC Low",
                    0x0080 => "ANC Auto",
                    _ => $"0x{v:X4}"
                };
                return $"Live ANC Mode: {desc}";
            }
            if (d.Length >= 14 && (d[7] | (d[8] << 8)) == 5 && d[9] == 0x03 && d[10] == 0x04)
            {
                int v = d[12] | (d[13] << 8);
                string desc = v switch
                {
                    0x0010 => "High",
                    0x0020 => "Moderate",
                    0x0040 => "Low",
                    _ => $"0x{v:X4}"
                };
                return $"Live Auto Level: {desc}";
            }
            return "Live Status Notification (0x0402)";
        }
        if (cmdHi == 0x81)
        {
            return cmdLo switch
            {
                0x06 => "Reply Battery",
                0x08 => "Reply Gestures",
                0x09 => "Reply Wear",
                0x0C => "Reply ANC State",
                0x0F => $"Reply EQ Preset ({d[^1]})",
                0x0D => "Reply Switch Status",
                0x22 => "Reply Custom EQ",
                0x24 => $"Reply BassWave Value ({(sbyte)d[^1]})",
                _ => $"Reply 0x{cmdLo:X2}81"
            };
        }
        if (cmdHi == 0x25 && cmdLo == 0x81) return "Reply Battery (0x8125)";

        return $"Packet 0x{cmd16:X4}";
    }

    // ── Background reader ────────────────────────────────────────────
    private void StartReader()
    {
        StopReader();
        if (_sock == null) return;
        _sock.ReceiveTimeout = 0; // block until data arrives (no timeouts on the listener)
        _readerRun = true;
        _reader = new Thread(ReaderLoop) { IsBackground = true, Name = "BudsReader" };
        _reader.Start();
    }

    private void StopReader()
    {
        _readerRun = false;
        _reader = null; // never join: the loop may be us, and it exits on its own
    }

    // Splits the incoming byte stream into AA-framed packets. A frame is
    // [0xAA][len][len bytes], so the total size is len + 2.
    private void ReaderLoop()
    {
        var acc = new System.Collections.Generic.List<byte>();
        var buf = new byte[2048];
        var sock = _sock;
        while (_readerRun && sock != null)
        {
            int n;
            try { n = sock.Receive(buf); }
            catch { break; }
            if (n <= 0) break;
            acc.AddRange(new ArraySegment<byte>(buf, 0, n));

            while (true)
            {
                while (acc.Count > 0 && acc[0] != 0xAA) acc.RemoveAt(0); // resync on noise
                if (acc.Count < 2) break;
                int total = acc[1] + 2;
                if (acc.Count < total) break;                            // wait for the rest
                var frame = acc.GetRange(0, total).ToArray();
                acc.RemoveRange(0, total);
                PacketReceived?.Invoke(frame);
            }
        }
        if (_readerRun) Disconnected?.Invoke(); // exited unexpectedly → channel lost
    }

    private void DrainQuiet()
    {
        try { var b = new byte[2048]; _sock!.Receive(b); } catch { }
    }

    // ── Decoders ────────────────────────────────────────────────────
    public static string? DecodeStatus(byte[] d)
    {
        byte[] marker = { 0x05, 0x00, 0x03 };
        string? level = null, onoff = null;
        for (int i = 0; i + 7 <= d.Length; i++)
        {
            if (d[i] != marker[0] || d[i + 1] != marker[1] || d[i + 2] != marker[2]) continue;
            byte key = d[i + 3];
            int tail = (d[i + 5] << 8) | d[i + 6];
            string name = tail switch
            {
                0x0800 => "OFF",
                0x0001 => "Transparency",
                0x1000 => "ANC High",
                0x2000 => "ANC Moderate",
                0x4000 => "ANC Low",
                0x8000 => "ANC (Auto)",
                _ => $"0x{tail:X4}"
            };
            if (key == 0x04) level = "Auto → " + name;
            else if (key == 0x01) level = name;
            else if (key == 0x02) onoff = (tail == 0x0200) ? "ANC on" : "ANC off";
        }
        return level ?? onoff;
    }

    public static string? DecodeBattery(byte[] d)
    {
        int i = IndexOf(d, new byte[] { 0x81, 0x25 });
        if (i < 0 || i + 4 > d.Length) return null;
        int plen = d[i + 2] | (d[i + 3] << 8);
        int start = i + 4;
        if (start + plen > d.Length || plen < 2) return null;
        int count = d[start + 1];
        var names = new[] { "", "Left", "Right", "Case" };
        var parts = new System.Collections.Generic.List<string>();
        for (int k = 0; k < count; k++)
        {
            int p = start + 2 + k * 2;
            if (p + 1 >= d.Length) break;
            int id = d[p], raw = d[p + 1];
            int pct = raw & 0x7F;          // low 7 bits = percentage
            bool charging = (raw & 0x80) != 0; // high bit = charging
            string nm = id < names.Length ? names[id] : $"id{id}";
            parts.Add($"{nm}: {pct}%{(charging ? " ⚡" : "")}");
        }
        return parts.Count > 0 ? string.Join("   ", parts) : null;
    }

    public record EqEntry(byte Id, string Name, bool IsSelected, int[] DbValues);

    public static System.Collections.Generic.List<EqEntry> DecodeCustomEqList(byte[] d)
    {
        var result = new System.Collections.Generic.List<EqEntry>();
        // Response: ...[cmd_lo=0x22][cmd_hi_resp=0x81][seq=0x00][plen_lo][plen_hi][payload]
        int i = IndexOf(d, new byte[] { 0x22, 0x81, 0x00 });
        if (i < 0 || i + 6 > d.Length) return result;
        int pos = i + 6; // skip marker(3) + plen(2) + status(1)
        if (pos >= d.Length) return result;
        int count = d[pos++];
        for (int k = 0; k < count && pos < d.Length; k++)
        {
            if (pos + 4 > d.Length) break;
            int isSelected = d[pos++];
            pos++;            // minValue (skip)
            pos++;            // maxValue (skip)
            byte eqId = d[pos++];
            if (pos >= d.Length) break;
            int nameLen = d[pos++];
            string name = nameLen > 0 && pos + nameLen <= d.Length
                ? Encoding.UTF8.GetString(d, pos, nameLen) : "";
            pos += nameLen;
            if (pos >= d.Length) break;
            int freqCount = d[pos++];
            var db = new int[freqCount];
            for (int f = 0; f < freqCount; f++)
            {
                if (pos + 2 >= d.Length) break;
                pos += 2; // skip 2-byte frequency
                db[f] = (sbyte)d[pos++]; // signed dB value
            }
            result.Add(new EqEntry(eqId, string.IsNullOrEmpty(name) ? $"Slot {eqId}" : name, isSelected == 1, db));
        }
        return result;
    }

    // Note: the ANC GET reply (fn 260 → 04 81) is static on these earbuds — it
    // does not reflect the live ANC state — so we don't query it. ANC mode/level
    // comes only from the pushed 04 02 mask (DecodeAncMask).

    // fn 292 → marker 24 81; payload = [status][min][max][current] (all sbyte)
    public static int? DecodeBassWaveValue(byte[] d)
    {
        int i = IndexOf(d, new byte[] { 0x24, 0x81 });
        if (i < 0 || i + 9 > d.Length) return null;
        return (sbyte)d[i + 8];
    }

    // fn 269 → marker 0D 81; payload e.g. 00 01 1D <status> ([status][count][featureType][status]).
    // BassEngine feature id = 0x1D; status: 1=on, 0=off. Scan for the feature id, read the byte after it.
    public static bool? DecodeBassWaveOn(byte[] d)
    {
        int i = IndexOf(d, new byte[] { 0x0D, 0x81 });
        if (i < 0 || i + 5 >= d.Length) return null;
        int plen = d[i + 3] | (d[i + 4] << 8);
        int start = i + 5;
        int end = Math.Min(start + plen, d.Length);
        for (int p = start; p + 1 < end; p++)
            if (d[p] == 0x1D) return d[p + 1] == 1;
        return null;
    }

    // fn 271 → marker 0F 81; payload = [status][preset]
    // preset: 00=Balanced, 01=Clear Vocals, 02=Bass
    public static byte? DecodeEqPreset(byte[] d)
    {
        int i = IndexOf(d, new byte[] { 0x0F, 0x81 });
        if (i < 0 || i + 7 > d.Length) return null;
        return d[i + 6];
    }

    // Live ANC state: cmd 04 02, 5-byte payload "03 <field> 01 <lo> <hi>".
    // The byte at d[10] selects which field this packet carries:
    //   0x01 = the chosen mode   (off / transparency / a fixed level / Auto)
    //   0x04 = the level Auto is currently using (only sent while in Auto mode)
    // The 16-bit little-endian value at d[12..13] is a mode/level mask.
    private static bool IsAncMask(byte[] d, byte field) =>
        d.Length >= 14 && d[4] == 0x04 && d[5] == 0x02
        && (d[7] | (d[8] << 8)) == 5 && d[10] == field;

    public static (bool valid, bool off, bool trans, bool anc, bool auto, bool adaptive, string level) DecodeAncMode(byte[] d)
    {
        if (!IsAncMask(d, 0x01)) return (false, false, false, false, false, false, "");
        return MapAncValue(d[12] | (d[13] << 8));
    }

    // Reply to fn 268 → marker 0C 81. The field byte (d[i+6]) says what it is:
    // 01 = current mode, 04 = the level Auto is currently using. Same mask value
    // format as the live push, so we read the ANC state at connect from here.
    public static (bool valid, bool off, bool trans, bool anc, bool auto, bool adaptive, string level) DecodeAncReply(byte[] d)
    {
        int i = IndexOf(d, new byte[] { 0x0C, 0x81 });
        if (i < 0 || i + 10 > d.Length || d[i + 6] != 0x01) return (false, false, false, false, false, false, "");
        return MapAncValue(d[i + 8] | (d[i + 9] << 8));
    }

    public static string? DecodeAncReplyAutoLevel(byte[] d)
    {
        int i = IndexOf(d, new byte[] { 0x0C, 0x81 });
        if (i < 0 || i + 10 > d.Length || d[i + 6] != 0x04) return null;
        return (d[i + 8] | (d[i + 9] << 8)) switch
        {
            0x0010 => "High",
            0x0020 => "Moderate",
            0x0040 => "Low",
            _      => null
        };
    }

    private static (bool valid, bool off, bool trans, bool anc, bool auto, bool adaptive, string level) MapAncValue(int v) => v switch
    {
        0x0008 => (true, true,  false, false, false, false, ""),         // Off
        0x0100 => (true, false, true,  false, false, false, ""),         // Transparency
        0x0800 => (true, false, false, false, false, true,  "Adaptive"), // Adaptive
        0x0010 => (true, false, false, true,  false, false, "High"),
        0x0020 => (true, false, false, true,  false, false, "Moderate"),
        0x0040 => (true, false, false, true,  false, false, "Low"),
        0x0080 => (true, false, false, true,  true,  false, "Auto"),
        _      => (false, false, false, false, false, false, "")
    };

    // The level Auto is currently applying (e.g. "Auto: Low" on the phone).
    public static string? DecodeAncAutoLevel(byte[] d)
    {
        if (!IsAncMask(d, 0x04)) return null;
        return (d[12] | (d[13] << 8)) switch
        {
            0x0010 => "High",
            0x0020 => "Moderate",
            0x0040 => "Low",
            _      => null
        };
    }

    // Per-earbud status flags carried by both the getEarBudsStatus reply (09 81)
    // and the wear notification (04 02 / subtype 02): body "...03 01 <L> 02 <R> 03 04".
    // In each L/R byte: bit 0x01 clear = that earbud is in the case; bit 0x02 set = in an ear.
    public readonly record struct WearState(bool BothInCase, bool AnyInEar);

    private static WearState FromFlags(byte left, byte right) => new(
        BothInCase: (left & 0x01) == 0 && (right & 0x01) == 0,
        AnyInEar:   (left & 0x02) != 0 || (right & 0x02) != 0);

    public static WearState? DecodeWear(byte[] d)
    {
        // getEarBudsStatus reply
        int i = IndexOf(d, new byte[] { 0x09, 0x81 });
        if (i >= 0 && i + 11 <= d.Length) return FromFlags(d[i + 8], d[i + 10]);

        // Live notification: cmd 04 02, subtype 0x02 (d[9]), 8-byte payload.
        // (The 04 02 packets with subtype 0x01 carry unrelated telemetry — skip them.)
        if (d.Length >= 17 && d[4] == 0x04 && d[5] == 0x02
            && (d[7] | (d[8] << 8)) == 8 && d[9] == 0x02)
            return FromFlags(d[12], d[14]);

        return null;
    }

    // Gesture entry: side (1=Left, 2=Right), category (1=Media, 6=Call), gestureId, actionId
    public record GestureEntry(byte Side, byte Category, byte GestureId, byte ActionId);

    public static System.Collections.Generic.List<GestureEntry> DecodeGestures(byte[] d)
    {
        var list = new System.Collections.Generic.List<GestureEntry>();
        int i = IndexOf(d, new byte[] { 0x08, 0x81 });
        if (i < 0 || i + 7 > d.Length) return list;
        // Format: [08 81][seq][plenLo][plenHi][status][count] [side][cat][gid][act]...
        int count = d[i + 6];
        int start = i + 7;
        for (int k = 0; k < count; k++)
        {
            int p = start + k * 4;
            if (p + 3 >= d.Length) break;
            list.Add(new GestureEntry(d[p], d[p + 1], d[p + 2], d[p + 3]));
        }
        return list;
    }

    private static int IndexOf(byte[] hay, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= hay.Length; i++)
        {
            bool ok = true;
            for (int j = 0; j < needle.Length; j++) if (hay[i + j] != needle[j]) { ok = false; break; }
            if (ok) return i;
        }
        return -1;
    }

    /// <summary>True if the packet contains the given two-byte response marker.</summary>
    public static bool HasMarker(byte[] d, byte a, byte b) => IndexOf(d, new[] { a, b }) >= 0;

    public void Close()
    {
        _readerRun = false;
        try { _sock?.Close(); } catch { }
        _sock = null;
    }

    public void Dispose() => Close();

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BLUETOOTH_DEVICE_INFO
    {
        public int dwSize;
        public ulong Address;
        public uint ulClassofDevice;
        [MarshalAs(UnmanagedType.Bool)] public bool fConnected;
        [MarshalAs(UnmanagedType.Bool)] public bool fRemembered;
        [MarshalAs(UnmanagedType.Bool)] public bool fAuthenticated;
        public ushort stLastSeen_wYear, stLastSeen_wMonth, stLastSeen_wDayOfWeek, stLastSeen_wDay, stLastSeen_wHour, stLastSeen_wMinute, stLastSeen_wSecond, stLastSeen_wMilliseconds;
        public ushort stLastUsed_wYear, stLastUsed_wMonth, stLastUsed_wDayOfWeek, stLastUsed_wDay, stLastUsed_wHour, stLastUsed_wMinute, stLastUsed_wSecond, stLastUsed_wMilliseconds;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)]
        public string szName;
    }

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern uint BluetoothGetDeviceInfo(IntPtr hRadio, ref BLUETOOTH_DEVICE_INFO pbdi);

    /// <summary>
    /// Checks whether Windows reports this Bluetooth device as currently connected (e.g. A2DP/HFP profile).
    /// </summary>
    public static bool IsDeviceConnected(string mac)
    {
        try
        {
            var bdi = new BLUETOOTH_DEVICE_INFO();
            bdi.dwSize = Marshal.SizeOf<BLUETOOTH_DEVICE_INFO>();
            bdi.Address = BtEndPoint.ParseMac(mac);
            uint ret = BluetoothGetDeviceInfo(IntPtr.Zero, ref bdi);
            return ret == 0 && bdi.fConnected;
        }
        catch
        {
            return false;
        }
    }
}
