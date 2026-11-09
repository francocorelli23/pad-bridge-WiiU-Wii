using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using PadBridge.Native;
using PadBridge.ViGEm;
using PadBridge.Wii;

namespace PadBridge.Core
{
    internal sealed class ControllerInfo
    {
        public string Key;
        public int Player;          // 1-4, 0 = unknown yet
        public string Type;
        public string Battery;
        public string Address;
        public string Status;
    }

    /// <summary>
    /// Watches for Wii controllers appearing as HID devices, connects each one to its own virtual
    /// Xbox 360 controller, and cleans up when they disappear.
    /// </summary>
    internal sealed class ControllerManager : IDisposable
    {
        public event Action<string> Log;
        public event Action Changed;

        private readonly Settings _settings;
        private readonly MappingOptions _mapping = new MappingOptions();
        private ViGEmBus _bus;
        private readonly object _lock = new object();
        private readonly Dictionary<string, Session> _sessions = new Dictionary<string, Session>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _connecting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _retryAfter = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<ulong, DateTime> _btConnectedSince = new Dictionary<ulong, DateTime>();
        private readonly Dictionary<ulong, DateTime> _lastKick = new Dictionary<ulong, DateTime>();
        private Timer _timer;
        private int _scanBusy;
        private int _tick;
        private volatile bool _disposed;
        public volatile bool PairingActive;

        private sealed class Session
        {
            public WiiController Wii;
            public Xbox360Pad Pad;
            public int Player;
            public DateTime LastActivity = DateTime.UtcNow;
            public XReport LastReport;
            public bool HasReport;
            public string Status = "Connected";
        }

        public ControllerManager(Settings settings)
        {
            _settings = settings;
            ApplySettings();
        }

        public string BusStatus { get; private set; } = "Starting…";
        public string BluetoothStatus { get; private set; } = "Bluetooth: checking…";

        public void Start()
        {
            try
            {
                _bus = ViGEmBus.Open();
                BusStatus = "ViGEmBus OK";
            }
            catch (Exception ex)
            {
                BusStatus = "ViGEmBus missing";
                L(ex.Message);
            }
            _timer = new Timer(_ => Scan(), null, 300, 1000);
        }

        public void ApplySettings()
        {
            _mapping.PositionalLayout = _settings.PositionalLayout;
            _mapping.Deadzone = _settings.DeadzonePercent / 100f;
            if (!_settings.Rumble)
                lock (_lock) foreach (var s in _sessions.Values) s.Wii.SetRumble(false);
        }

        private void L(string s) => Log?.Invoke(s);

        public List<ControllerInfo> Snapshot()
        {
            lock (_lock)
            {
                var list = _sessions.Select(kv => new ControllerInfo
                {
                    Key = kv.Key,
                    Player = kv.Value.Player,
                    Type = Describe(kv.Value.Wii.Type),
                    Battery = kv.Value.Wii.BatteryPercent < 0 ? "?" :
                        kv.Value.Wii.BatteryPercent + "%" + (kv.Value.Wii.Charging ? " ⚡" : ""),
                    Address = kv.Value.Wii.BtAddress.HasValue ? Bluetooth.Format(kv.Value.Wii.BtAddress.Value) : "",
                    Status = kv.Value.Status
                }).ToList();
                foreach (var p in _connecting)
                    list.Add(new ControllerInfo { Key = p, Type = "…", Battery = "", Address = "", Status = "Connecting…" });
                return list.OrderBy(i => i.Player == 0 ? 99 : i.Player).ToList();
            }
        }

        public static string Describe(WiiType t)
        {
            switch (t)
            {
                case WiiType.ProController: return "Wii U Pro Controller";
                case WiiType.Wiimote: return "Wii Remote (sideways)";
                case WiiType.WiimoteNunchuk: return "Wii Remote + Nunchuk";
                case WiiType.WiimoteClassic: return "Wii Remote + Classic Controller";
                case WiiType.WiimoteClassicPro: return "Wii Remote + Classic Controller Pro";
                default: return "Identifying…";
            }
        }

        public bool IsActive(ulong address)
        {
            lock (_lock) return _sessions.Values.Any(s => s.Wii.BtAddress == address);
        }

        // ------------------------------------------------------------ discovery

        private void Scan()
        {
            if (_disposed || Interlocked.Exchange(ref _scanBusy, 1) == 1) return;
            try
            {
                _tick++;
                var devices = SetupApi.Enumerate(Hid.HidGuid);
                var present = new HashSet<string>(devices.Select(d => d.Path), StringComparer.OrdinalIgnoreCase);

                lock (_lock)
                {
                    _ignored.RemoveWhere(p => !present.Contains(p));
                    foreach (var k in _retryAfter.Keys.Where(p => !present.Contains(p)).ToList()) _retryAfter.Remove(k);
                }

                foreach (var dev in devices)
                {
                    lock (_lock)
                    {
                        if (_sessions.ContainsKey(dev.Path) || _connecting.Contains(dev.Path) || _ignored.Contains(dev.Path)) continue;
                        if (_retryAfter.TryGetValue(dev.Path, out var t) && DateTime.UtcNow < t) continue;
                    }
                    if (!WiiController.IsWiiDevice(dev.Path))
                    {
                        lock (_lock) _ignored.Add(dev.Path);
                        continue;
                    }
                    if (_bus == null) continue;
                    lock (_lock) _connecting.Add(dev.Path);
                    var d = dev;
                    ThreadPool.QueueUserWorkItem(_ => Connect(d));
                    Changed?.Invoke();
                }

                CheckIdle();
                if ((_tick % 5 == 1) && !PairingActive) CheckStuckBluetooth();
            }
            catch (Exception ex)
            {
                L("Scan error: " + ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _scanBusy, 0);
            }
        }

        private void Connect(DeviceInterface dev)
        {
            WiiController wii = null;
            try
            {
                wii = WiiController.Open(dev, out string err);
                if (wii == null)
                {
                    if (err != null) Backoff(dev.Path, 3);
                    else lock (_lock) _ignored.Add(dev.Path);
                    return;
                }
                wii.Log += s => L(s);
                if (!wii.Start())
                {
                    // Paired controller that is asleep: Windows still lists it. Retry quietly.
                    wii.Dispose();
                    Backoff(dev.Path, 3);
                    return;
                }

                var pad = _bus.PlugXbox360();
                var session = new Session { Wii = wii, Pad = pad };
                lock (_lock)
                {
                    session.Player = Enumerable.Range(1, 4).FirstOrDefault(n => _sessions.Values.All(s => s.Player != n));
                    _sessions[dev.Path] = session;
                }
                if (session.Player > 0) wii.SetPlayer(session.Player - 1);

                pad.Feedback += (large, small, led) => OnFeedback(session, large, small, led);
                wii.Input += (w, input) => OnInput(session, input);
                wii.InfoChanged += w => Changed?.Invoke();
                wii.Disconnected += (w, reason) => Remove(dev.Path, reason);
                if (!wii.IsAlive)
                {
                    wii = null;
                    Remove(dev.Path, "lost during setup");
                    return;
                }

                string addr = wii.BtAddress.HasValue ? $" [{Bluetooth.Format(wii.BtAddress.Value)}]" : "";
                L($"Connected {Describe(wii.Type)}{addr} as player {session.Player}.");
                wii = null; // owned by session now
            }
            catch (Exception ex)
            {
                L("Connect failed: " + ex.Message);
                Backoff(dev.Path, 5);
            }
            finally
            {
                wii?.Dispose();
                lock (_lock) _connecting.Remove(dev.Path);
                Changed?.Invoke();
            }
        }

        private void Backoff(string path, int seconds)
        {
            lock (_lock) _retryAfter[path] = DateTime.UtcNow.AddSeconds(seconds);
        }

        private void Remove(string path, string reason)
        {
            Session s;
            lock (_lock)
            {
                if (!_sessions.TryGetValue(path, out s)) return;
                _sessions.Remove(path);
                _retryAfter[path] = DateTime.UtcNow.AddSeconds(2);
            }
            try { s.Pad.Dispose(); } catch { }
            try { s.Wii.Dispose(); } catch { }
            L($"Player {s.Player} disconnected ({reason}).");
            Changed?.Invoke();
        }

        // ------------------------------------------------------------ data flow

        private void OnInput(Session s, PadInput input)
        {
            var r = Mapper.ToXbox(input, _mapping);
            if (s.HasReport && r.Equals(s.LastReport)) return;
            bool meaningful = !s.HasReport || r.Buttons != s.LastReport.Buttons ||
                              Math.Abs(r.LX - s.LastReport.LX) > 3000 || Math.Abs(r.LY - s.LastReport.LY) > 3000 ||
                              Math.Abs(r.RX - s.LastReport.RX) > 3000 || Math.Abs(r.RY - s.LastReport.RY) > 3000 ||
                              r.LeftTrigger != s.LastReport.LeftTrigger || r.RightTrigger != s.LastReport.RightTrigger;
            if (meaningful) s.LastActivity = DateTime.UtcNow;
            s.LastReport = r;
            s.HasReport = true;
            s.Pad.Submit(r);
        }

        private void OnFeedback(Session s, byte large, byte small, byte led)
        {
            s.Wii.SetRumble(_settings.Rumble && Math.Max(large, small) > 24);
            if (led <= 3 && led + 1 != s.Player)
            {
                // Follow the XInput slot Windows assigned, so the LED matches the player number games show.
                s.Player = led + 1;
                s.Wii.SetPlayer(led);
                Changed?.Invoke();
            }
        }

        // ------------------------------------------------------------ maintenance

        private void CheckIdle()
        {
            if (_settings.IdleOffMinutes <= 0) return;
            List<(string, Session)> idle;
            lock (_lock)
            {
                idle = _sessions.Where(kv => DateTime.UtcNow - kv.Value.LastActivity > TimeSpan.FromMinutes(_settings.IdleOffMinutes))
                    .Select(kv => (kv.Key, kv.Value)).ToList();
            }
            foreach (var (path, s) in idle)
            {
                L($"Player {s.Player} idle for {_settings.IdleOffMinutes} min, turning it off.");
                PowerOff(path);
            }
        }

        /// <summary>Windows says a Wii controller is connected but no HID device ever showed up: kick it.</summary>
        private void CheckStuckBluetooth()
        {
            var now = DateTime.UtcNow;
            var connected = new HashSet<ulong>();
            var radios = Bluetooth.OpenRadios();
            string bt = radios.Count == 0
                ? "Bluetooth is OFF (or no adapter) – turn it on in Windows Settings"
                : "Bluetooth: " + string.Join(", ", radios.Select(r => r.Name));
            if (bt != BluetoothStatus)
            {
                BluetoothStatus = bt;
                if (radios.Count == 0) L("Bluetooth is turned off or no adapter was found. Turn Bluetooth on in Windows Settings.");
                Changed?.Invoke();
            }
            try
            {
                foreach (var r in radios)
                    foreach (var d in Bluetooth.FindDevices(r, false, 0))
                        if (Bluetooth.IsWiiController(d) && d.Connected) connected.Add(d.Address);
            }
            catch { return; }
            finally { foreach (var r in radios) r.Dispose(); }

            bool busy;
            lock (_lock) busy = _connecting.Count > 0;

            foreach (var a in _btConnectedSince.Keys.Where(a => !connected.Contains(a)).ToList()) _btConnectedSince.Remove(a);
            foreach (var addr in connected)
            {
                if (IsActive(addr)) { _btConnectedSince.Remove(addr); continue; }
                if (!_btConnectedSince.TryGetValue(addr, out var since)) { _btConnectedSince[addr] = now; continue; }
                if (busy || now - since < TimeSpan.FromSeconds(12)) continue;
                if (_lastKick.TryGetValue(addr, out var k) && now - k < TimeSpan.FromSeconds(60)) continue;
                _lastKick[addr] = now;
                _btConnectedSince[addr] = now;
                L($"Controller [{Bluetooth.Format(addr)}] is connected over Bluetooth but not usable; resetting it.");
                Pairing.KickHidService(addr, L);
            }
        }

        /// <summary>Disconnects the Bluetooth link (the controller turns off).</summary>
        public void PowerOff(string key)
        {
            Session s;
            lock (_lock) _sessions.TryGetValue(key, out s);
            if (s == null) return;
            var addr = s.Wii.BtAddress;
            Remove(key, "turned off");
            L("To use it again just press a button on it (no need to pair again).");
            if (addr.HasValue && !Bluetooth.Disconnect(addr.Value))
                L("Could not drop the Bluetooth link; hold the controller's power/sync button to turn it off.");
        }

        public void Rumble(string key, int ms)
        {
            Session s;
            lock (_lock) _sessions.TryGetValue(key, out s);
            if (s == null) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                s.Wii.SetRumble(true);
                Thread.Sleep(ms);
                s.Wii.SetRumble(false);
            });
        }

        public void Dispose()
        {
            _disposed = true;
            _timer?.Dispose();
            List<Session> all;
            lock (_lock) { all = _sessions.Values.ToList(); _sessions.Clear(); }
            foreach (var s in all)
            {
                try { s.Pad.Dispose(); } catch { }
                try { s.Wii.Dispose(); } catch { }
            }
            _bus?.Dispose();
        }
    }
}
