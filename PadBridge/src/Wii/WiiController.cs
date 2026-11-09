using System;
using System.ComponentModel;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using PadBridge.Native;

namespace PadBridge.Wii
{
    internal enum WiiType { Unknown, Wiimote, WiimoteNunchuk, WiimoteClassic, WiimoteClassicPro, ProController }

    /// <summary>
    /// One Wii Remote / Wii U Pro Controller over HID. Handles the handshake, extension detection,
    /// data reporting, LEDs, rumble and disconnect detection.
    /// </summary>
    internal sealed class WiiController : IDisposable
    {
        public const ushort NintendoVid = 0x057E;
        public const ushort PidWiimote = 0x0306;
        public const ushort PidWiimotePlus = 0x0330; // also Wii U Pro Controller

        private const byte DataMode = 0x34; // core buttons + 19 extension bytes

        public string Path { get; }
        public ushort ProductId { get; }
        public ulong? BtAddress { get; }
        public WiiType Type { get; private set; } = WiiType.Unknown;
        public int BatteryPercent { get; private set; } = -1;
        public bool Charging { get; private set; }
        public bool IsAlive => !_stopped;

        public event Action<WiiController, PadInput> Input;
        public event Action<WiiController> InfoChanged;
        public event Action<WiiController, string> Disconnected;
        public event Action<string> Log;

        private readonly SafeFileHandle _h;
        private readonly int _inLen, _outLen;
        private readonly AsyncIo _readIo, _writeIo;
        private readonly object _writeLock = new object();
        private Thread _reader;
        private volatile bool _stopped;
        private volatile bool _rumble;
        private byte _leds = 0x10;
        private int _writeFailures;
        private bool _useSetOutputReport;

        // Response waiter (one at a time, used by configuration thread)
        private readonly object _waitLock = new object();
        private Func<byte[], bool> _waitPredicate;
        private byte[] _waitResult;

        private readonly object _configLock = new object();
        private int _configGeneration;
        private bool _extConnected;

        private readonly StickCalibration _lx = new StickCalibration(), _ly = new StickCalibration(),
            _rx = new StickCalibration(), _ry = new StickCalibration();

        private WiiController(string path, ushort pid, ulong? addr, SafeFileHandle h, int inLen, int outLen)
        {
            Path = path; ProductId = pid; BtAddress = addr; _h = h;
            _inLen = Math.Max(inLen, 22);
            _outLen = Math.Max(outLen, 22);
            _readIo = new AsyncIo(_inLen);
            _writeIo = new AsyncIo(_outLen);
        }

        /// <summary>Opens the HID device. Returns null if it isn't a Wii controller or can't be opened.</summary>
        public static WiiController Open(DeviceInterface dev, out string error)
        {
            error = null;
            var h = Kernel32.CreateFile(dev.Path, Kernel32.GENERIC_READ | Kernel32.GENERIC_WRITE,
                Kernel32.FILE_SHARE_READ | Kernel32.FILE_SHARE_WRITE, IntPtr.Zero, Kernel32.OPEN_EXISTING,
                Kernel32.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (h.IsInvalid) { error = new Win32Exception().Message; return null; }

            var attrs = new Hid.HIDD_ATTRIBUTES { Size = 10 };
            if (!Hid.HidD_GetAttributes(h, ref attrs) || attrs.VendorID != NintendoVid ||
                (attrs.ProductID != PidWiimote && attrs.ProductID != PidWiimotePlus))
            {
                h.Dispose();
                return null;
            }
            var lens = Hid.GetReportLengths(h) ?? (22, 22);
            ulong? addr = Bluetooth.AddressFromInstanceId(SetupApi.GetParentInstanceId(dev.DevInst));
            return new WiiController(dev.Path, attrs.ProductID, addr, h, lens.input, lens.output);
        }

        /// <summary>Fast check without opening for I/O.</summary>
        public static bool IsWiiDevice(string path)
        {
            using (var h = Kernel32.CreateFile(path, 0, Kernel32.FILE_SHARE_READ | Kernel32.FILE_SHARE_WRITE,
                IntPtr.Zero, Kernel32.OPEN_EXISTING, 0, IntPtr.Zero))
            {
                if (h.IsInvalid) return false;
                var attrs = new Hid.HIDD_ATTRIBUTES { Size = 10 };
                return Hid.HidD_GetAttributes(h, ref attrs) && attrs.VendorID == NintendoVid &&
                       (attrs.ProductID == PidWiimote || attrs.ProductID == PidWiimotePlus);
            }
        }

        /// <summary>
        /// Starts reading and performs the handshake. Returns false if the controller doesn't answer
        /// (Windows keeps HID nodes of paired-but-sleeping controllers around; those never answer).
        /// </summary>
        public bool Start()
        {
            _reader = new Thread(ReadLoop) { IsBackground = true, Name = "Wii reader" };
            _reader.Start();

            byte[] status = null;
            for (int attempt = 0; attempt < 3 && status == null && !_stopped; attempt++)
            {
                status = SendAndWait(new byte[] { 0x15, 0x00 }, r => r[0] == 0x20, 800);
            }
            if (status == null) { Stop(null); return false; }

            SendLeds();
            HandleStatus(status, solicited: true);
            return !_stopped;
        }

        public void SetPlayer(int index)
        {
            _leds = (byte)(0x10 << Math.Max(0, Math.Min(3, index)));
            SendLeds();
        }

        public void SetRumble(bool on)
        {
            if (_rumble == on) return;
            _rumble = on;
            Send(new byte[] { 0x10, 0x00 });
        }

        private void SendLeds() => Send(new byte[] { 0x11, _leds });

        // ---------------------------------------------------------------- I/O

        private bool Send(byte[] report)
        {
            if (_stopped) return false;
            var buf = new byte[_outLen];
            Array.Copy(report, buf, Math.Min(report.Length, buf.Length));
            if (_rumble) buf[1] |= 0x01; else buf[1] &= 0xFE;

            lock (_writeLock)
            {
                if (_stopped) return false;
                bool ok = false;
                if (!_useSetOutputReport)
                {
                    try
                    {
                        _writeIo.Load(buf);
                        ok = _writeIo.Run(_h, ov => Kernel32.WriteFile(_h, _writeIo.Buffer, buf.Length, IntPtr.Zero, ov), 1000) > 0;
                    }
                    catch (Win32Exception) { ok = false; }
                }
                if (!ok)
                {
                    // Some Bluetooth stacks only accept output reports through the control channel.
                    ok = Hid.HidD_SetOutputReport(_h, buf, buf.Length);
                    if (ok && !_useSetOutputReport)
                    {
                        _useSetOutputReport = true;
                        Log?.Invoke("Using HidD_SetOutputReport for output.");
                    }
                }
                _writeFailures = ok ? 0 : _writeFailures + 1;
            }
            if (_writeFailures >= 5) Stop("output failed");
            return true;
        }

        private byte[] SendAndWait(byte[] report, Func<byte[], bool> predicate, int timeoutMs)
        {
            lock (_waitLock)
            {
                _waitResult = null;
                _waitPredicate = predicate;
            }
            Send(report);
            var deadline = Environment.TickCount + timeoutMs;
            lock (_waitLock)
            {
                while (_waitResult == null && !_stopped)
                {
                    int left = deadline - Environment.TickCount;
                    if (left <= 0) break;
                    Monitor.Wait(_waitLock, left);
                }
                var r = _waitResult;
                _waitPredicate = null;
                _waitResult = null;
                return r;
            }
        }

        private void ReadLoop()
        {
            int silentSlices = 0;
            while (!_stopped)
            {
                int n;
                try
                {
                    n = _readIo.Run(_h, ov => Kernel32.ReadFile(_h, _readIo.Buffer, _inLen, IntPtr.Zero, ov), 1000, () => _stopped);
                }
                catch (Win32Exception ex)
                {
                    Stop($"read error: {ex.Message}");
                    return;
                }
                catch (ObjectDisposedException) { return; }

                if (n <= 0)
                {
                    // Continuous reporting is on, so silence means the controller went away.
                    silentSlices++;
                    if (silentSlices == 3) Send(new byte[] { 0x15, 0x00 });
                    if (silentSlices >= 6) { Stop("controller stopped responding"); return; }
                    continue;
                }
                silentSlices = 0;
                var r = _readIo.Read(n);
                try { Dispatch(r); }
                catch (Exception ex) { Log?.Invoke("Input parse error: " + ex.Message); }
            }
        }

        private void Dispatch(byte[] r)
        {
            lock (_waitLock)
            {
                if (_waitPredicate != null && _waitResult == null && _waitPredicate(r))
                {
                    _waitResult = r;
                    Monitor.PulseAll(_waitLock);
                    if (r[0] == 0x20) return; // the requester handles it
                }
            }

            switch (r[0])
            {
                case 0x20:
                    HandleStatus(r, solicited: false);
                    break;
                case 0x30: ParseData(r, -1); break;
                case 0x31: ParseData(r, -1); break;
                case 0x32: ParseData(r, 3); break;
                case 0x34: ParseData(r, 3); break;
                case 0x35: ParseData(r, 6); break;
                case 0x36: ParseData(r, 13); break;
                case 0x37: ParseData(r, 16); break;
                case 0x3D: ParseData(r, 1, coreButtons: false); break;
            }
        }

        // ---------------------------------------------------------------- configuration

        private void HandleStatus(byte[] r, bool solicited)
        {
            bool ext = (r[3] & 0x02) != 0;
            if (ProductId != PidWiimotePlus || Type != WiiType.ProController)
            {
                BatteryPercent = Math.Min(100, r[6] * 100 / 0xC8);
            }
            int gen = Interlocked.Increment(ref _configGeneration);
            _extConnected = ext;
            // An unsolicited status report resets the reporting mode, so always reconfigure.
            if (solicited) Configure(gen);
            else ThreadPool.QueueUserWorkItem(_ => Configure(gen));
        }

        private void Configure(int gen)
        {
            lock (_configLock)
            {
                if (gen != _configGeneration || _stopped) return;
                var type = WiiType.Wiimote;
                if (_extConnected)
                {
                    type = IdentifyExtension() ?? WiiType.Wiimote;
                }
                if (gen != _configGeneration || _stopped) return;

                bool changed = type != Type;
                Type = type;
                if (changed) ResetCalibration();
                Send(new byte[] { 0x12, 0x04, DataMode }); // continuous reporting
                InfoChanged?.Invoke(this);
            }
        }

        private WiiType? IdentifyExtension()
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                // Initialise without encryption
                if (!WriteRegister(0xA400F0, 0x55)) continue;
                if (!WriteRegister(0xA400FB, 0x00)) continue;
                var id = ReadRegister(0xA400FA, 6);
                if (id == null) continue;

                int kind = (id[4] << 8) | id[5];
                switch (kind)
                {
                    case 0x0000: return WiiType.WiimoteNunchuk;
                    case 0x0101: return id[0] == 0x01 ? WiiType.WiimoteClassicPro : WiiType.WiimoteClassic;
                    case 0x0120: return WiiType.ProController;
                    default:
                        Log?.Invoke($"Unsupported extension id {BitConverter.ToString(id)}; using the Wii Remote alone.");
                        return null;
                }
            }
            Log?.Invoke("Extension did not answer; using the Wii Remote alone.");
            return null;
        }

        private bool WriteRegister(int address, byte value)
        {
            var rep = new byte[22];
            rep[0] = 0x16;
            rep[1] = 0x04;
            rep[2] = (byte)(address >> 16);
            rep[3] = (byte)(address >> 8);
            rep[4] = (byte)address;
            rep[5] = 1;
            rep[6] = value;
            var ack = SendAndWait(rep, r => r[0] == 0x22 && r[3] == 0x16, 600);
            return ack != null && ack[4] == 0;
        }

        private byte[] ReadRegister(int address, int size)
        {
            var rep = new byte[]
            {
                0x17, 0x04, (byte)(address >> 16), (byte)(address >> 8), (byte)address,
                (byte)(size >> 8), (byte)size
            };
            ushort low = (ushort)(address & 0xFFFF);
            var res = SendAndWait(rep, r => r[0] == 0x21 && ((r[4] << 8) | r[5]) == low, 800);
            if (res == null || (res[3] & 0x0F) != 0) return null;
            var data = new byte[size];
            Array.Copy(res, 6, data, 0, size);
            return data;
        }

        // ---------------------------------------------------------------- parsing

        private void ResetCalibration()
        {
            switch (Type)
            {
                case WiiType.ProController:
                    _lx.Reset(2048, 900); _ly.Reset(2048, 900); _rx.Reset(2048, 900); _ry.Reset(2048, 900); break;
                case WiiType.WiimoteNunchuk:
                    _lx.Reset(128, 90); _ly.Reset(128, 90); break;
                case WiiType.WiimoteClassic:
                case WiiType.WiimoteClassicPro:
                    _lx.Reset(32, 25); _ly.Reset(32, 25); _rx.Reset(16, 12); _ry.Reset(16, 12); break;
            }
        }

        private void ParseData(byte[] r, int extOffset, bool coreButtons = true)
        {
            var p = new PadInput();
            int b1 = coreButtons ? r[1] : 0, b2 = coreButtons ? r[2] : 0;
            var type = Type;
            bool hasExt = extOffset >= 0 && extOffset + 11 <= r.Length;

            switch (type)
            {
                case WiiType.ProController when hasExt:
                    ParsePro(r, extOffset, ref p);
                    break;
                case WiiType.WiimoteClassic when hasExt:
                case WiiType.WiimoteClassicPro when hasExt:
                    ParseClassic(r, extOffset, ref p, type == WiiType.WiimoteClassicPro);
                    break;
                case WiiType.WiimoteNunchuk when hasExt:
                    ParseWiimoteUpright(b1, b2, ref p);
                    ParseNunchuk(r, extOffset, ref p);
                    break;
                case WiiType.Unknown:
                    return;
                default:
                    ParseWiimoteSideways(b1, b2, ref p);
                    break;
            }
            Input?.Invoke(this, p);
        }

        private static bool Bit(int v, int mask) => (v & mask) != 0;

        private void ParsePro(byte[] r, int o, ref PadInput p)
        {
            int lx = r[o] | ((r[o + 1] & 0x0F) << 8);
            int rx = r[o + 2] | ((r[o + 3] & 0x0F) << 8);
            int ly = r[o + 4] | ((r[o + 5] & 0x0F) << 8);
            int ry = r[o + 6] | ((r[o + 7] & 0x0F) << 8);
            int b8 = ~r[o + 8], b9 = ~r[o + 9], b10 = r[o + 10];

            p.LX = _lx.Map(lx); p.LY = _ly.Map(ly); p.RX = _rx.Map(rx); p.RY = _ry.Map(ry);
            p.Set(PadButton.Right, Bit(b8, 0x80)); p.Set(PadButton.Down, Bit(b8, 0x40));
            p.Set(PadButton.L, Bit(b8, 0x20)); p.Set(PadButton.Minus, Bit(b8, 0x10));
            p.Set(PadButton.Home, Bit(b8, 0x08)); p.Set(PadButton.Plus, Bit(b8, 0x04));
            p.Set(PadButton.R, Bit(b8, 0x02));
            p.Set(PadButton.ZL, Bit(b9, 0x80)); p.Set(PadButton.B, Bit(b9, 0x40));
            p.Set(PadButton.Y, Bit(b9, 0x20)); p.Set(PadButton.A, Bit(b9, 0x10));
            p.Set(PadButton.X, Bit(b9, 0x08)); p.Set(PadButton.ZR, Bit(b9, 0x04));
            p.Set(PadButton.Left, Bit(b9, 0x02)); p.Set(PadButton.Up, Bit(b9, 0x01));
            p.Set(PadButton.LStick, !Bit(b10, 0x02)); p.Set(PadButton.RStick, !Bit(b10, 0x01));
            p.LT = p.Has(PadButton.ZL) ? 1f : 0f;
            p.RT = p.Has(PadButton.ZR) ? 1f : 0f;

            int level = (b10 >> 4) & 0x07;
            bool charging = !Bit(b10, 0x04);
            int pct = Math.Min(100, level * 25);
            if (pct != BatteryPercent || charging != Charging)
            {
                BatteryPercent = pct;
                Charging = charging;
                InfoChanged?.Invoke(this);
            }
        }

        private void ParseClassic(byte[] r, int o, ref PadInput p, bool isPro)
        {
            byte b0 = r[o], b1 = r[o + 1], b2 = r[o + 2], b3 = r[o + 3];
            int lx = b0 & 0x3F, ly = b1 & 0x3F;
            int rx = ((b0 & 0xC0) >> 3) | ((b1 & 0xC0) >> 5) | ((b2 & 0x80) >> 7);
            int ry = b2 & 0x1F;
            int lt = ((b2 & 0x60) >> 2) | ((b3 & 0xE0) >> 5);
            int rt = b3 & 0x1F;
            int b4 = ~r[o + 4], b5 = ~r[o + 5];

            p.LX = _lx.Map(lx); p.LY = _ly.Map(ly); p.RX = _rx.Map(rx); p.RY = _ry.Map(ry);
            p.Set(PadButton.Right, Bit(b4, 0x80)); p.Set(PadButton.Down, Bit(b4, 0x40));
            p.Set(PadButton.Minus, Bit(b4, 0x10)); p.Set(PadButton.Home, Bit(b4, 0x08));
            p.Set(PadButton.Plus, Bit(b4, 0x04));
            p.Set(PadButton.B, Bit(b5, 0x40)); p.Set(PadButton.Y, Bit(b5, 0x20));
            p.Set(PadButton.A, Bit(b5, 0x10)); p.Set(PadButton.X, Bit(b5, 0x08));
            p.Set(PadButton.Left, Bit(b5, 0x02)); p.Set(PadButton.Up, Bit(b5, 0x01));

            bool digL = Bit(b4, 0x20), digR = Bit(b4, 0x02), zl = Bit(b5, 0x80), zr = Bit(b5, 0x04);
            if (isPro)
            {
                // Classic Controller Pro: L/R are shoulders, ZL/ZR are the triggers
                p.Set(PadButton.L, digL); p.Set(PadButton.R, digR);
                p.Set(PadButton.ZL, zl); p.Set(PadButton.ZR, zr);
                p.LT = zl ? 1f : 0f; p.RT = zr ? 1f : 0f;
            }
            else
            {
                // Original Classic Controller: analog L/R triggers, ZL/ZR act as shoulders
                p.Set(PadButton.L, zl); p.Set(PadButton.R, zr);
                p.Set(PadButton.ZL, digL); p.Set(PadButton.ZR, digR);
                p.LT = digL ? 1f : Math.Min(1f, lt / 28f);
                p.RT = digR ? 1f : Math.Min(1f, rt / 28f);
            }
        }

        private void ParseNunchuk(byte[] r, int o, ref PadInput p)
        {
            p.LX = _lx.Map(r[o]);
            p.LY = _ly.Map(r[o + 1]);
            int b = r[o + 5];
            bool z = !Bit(b, 0x01), c = !Bit(b, 0x02);
            p.Set(PadButton.ZL, z);
            p.LT = z ? 1f : 0f;
            p.Set(PadButton.L, c);
        }

        /// <summary>Wii Remote held upright (with Nunchuk).</summary>
        private static void ParseWiimoteUpright(int b1, int b2, ref PadInput p)
        {
            p.Set(PadButton.Left, Bit(b1, 0x01)); p.Set(PadButton.Right, Bit(b1, 0x02));
            p.Set(PadButton.Down, Bit(b1, 0x04)); p.Set(PadButton.Up, Bit(b1, 0x08));
            p.Set(PadButton.Plus, Bit(b1, 0x10));
            p.Set(PadButton.X, Bit(b2, 0x01));      // 2
            p.Set(PadButton.Y, Bit(b2, 0x02));      // 1
            p.Set(PadButton.ZR, Bit(b2, 0x04));     // B trigger
            p.RT = Bit(b2, 0x04) ? 1f : 0f;
            p.Set(PadButton.B, Bit(b2, 0x08));      // A -> bottom face button (Xbox A)
            p.Set(PadButton.Minus, Bit(b2, 0x10));
            p.Set(PadButton.Home, Bit(b2, 0x80));
        }

        /// <summary>Wii Remote held sideways (D-pad on the left), NES style.</summary>
        private static void ParseWiimoteSideways(int b1, int b2, ref PadInput p)
        {
            p.Set(PadButton.Down, Bit(b1, 0x01));   // physical Left
            p.Set(PadButton.Up, Bit(b1, 0x02));     // physical Right
            p.Set(PadButton.Right, Bit(b1, 0x04));  // physical Down
            p.Set(PadButton.Left, Bit(b1, 0x08));   // physical Up
            p.Set(PadButton.Plus, Bit(b1, 0x10));
            p.Set(PadButton.B, Bit(b2, 0x01));      // 2 -> bottom face button (Xbox A)
            p.Set(PadButton.A, Bit(b2, 0x02));      // 1 -> right face button (Xbox B)
            p.Set(PadButton.Y, Bit(b2, 0x04));      // B trigger
            p.Set(PadButton.X, Bit(b2, 0x08));      // A
            p.Set(PadButton.Minus, Bit(b2, 0x10));
            p.Set(PadButton.Home, Bit(b2, 0x80));
        }

        // ---------------------------------------------------------------- lifetime

        private void Stop(string reason)
        {
            if (_stopped) return;
            _stopped = true;
            lock (_waitLock) Monitor.PulseAll(_waitLock);
            if (reason != null)
                ThreadPool.QueueUserWorkItem(_ => Disconnected?.Invoke(this, reason));
        }

        public void Dispose()
        {
            if (!_stopped && _rumble)
            {
                _rumble = false;
                try { Send(new byte[] { 0x10, 0x00 }); } catch { }
            }
            _stopped = true;
            lock (_waitLock) Monitor.PulseAll(_waitLock);
            if (_reader != null && _reader != Thread.CurrentThread) _reader.Join(2000);
            lock (_writeLock)
            {
                _h.Dispose();
                _readIo.Dispose();
                _writeIo.Dispose();
            }
        }
    }

    /// <summary>Auto-expanding stick calibration: starts with a conservative range and grows to what the stick reaches.</summary>
    internal sealed class StickCalibration
    {
        private float _center, _neg, _pos;
        private bool _centered;

        public void Reset(float center, float halfRange)
        {
            _center = center; _neg = halfRange; _pos = halfRange; _centered = false;
        }

        public float Map(int raw)
        {
            if (!_centered)
            {
                // Take the first resting sample as center if it is plausibly centered.
                if (Math.Abs(raw - _center) < _neg * 0.25f) _center = raw;
                _centered = true;
            }
            float d = raw - _center;
            if (d > _pos) _pos = d;
            if (-d > _neg) _neg = -d;
            float v = d >= 0 ? d / _pos : d / _neg;
            return Math.Max(-1f, Math.Min(1f, v));
        }
    }
}
