using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using PadBridge.Native;

namespace PadBridge.Core
{
    /// <summary>
    /// Pairs Wii Remotes / Wii U Pro Controllers with the Windows Bluetooth stack the way they want to be paired:
    /// with the PC's own address as PIN. Controllers paired like that reconnect by pressing any button, without
    /// visiting Windows Settings again.
    /// </summary>
    internal sealed class Pairing
    {
        public event Action<string> Log;
        public event Action<string> Status;

        private void L(string s) => Log?.Invoke(s);

        /// <summary>Runs a pairing session. Returns the number of controllers paired.</summary>
        public int Run(CancellationToken ct, int seconds, Func<ulong, bool> isActive)
        {
            var radios = Bluetooth.OpenRadios();
            if (radios.Count == 0)
            {
                L("Bluetooth is turned off or no adapter was found. Turn Bluetooth on in Windows Settings, then try again.");
                return 0;
            }

            int paired = 0;
            try
            {
                foreach (var r in radios)
                    L($"Bluetooth adapter: {r.Name} [{Bluetooth.Format(r.Address)}]");

                // A working pairing is never removed up front. A remembered controller only gets its pairing
                // replaced when it shows up in the search (it answers inquiries only while in SYNC mode, which
                // means it is not using the pairing Windows has), or as a last resort when nothing was found.
                var deadline = DateTime.UtcNow.AddSeconds(seconds);
                var fallbackAt = DateTime.UtcNow.AddSeconds(15);
                bool fallbackDone = false;
                var done = new HashSet<ulong>();
                while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
                {
                    int left = (int)Math.Ceiling((deadline - DateTime.UtcNow).TotalSeconds);
                    Status?.Invoke($"Press the red SYNC button on the controller (inside the battery cover / next to the charge port on the Pro)… {left}s");

                    foreach (var radio in radios)
                    {
                        if (ct.IsCancellationRequested) break;
                        var seenBefore = new Dictionary<ulong, Bluetooth.SYSTEMTIME>();
                        List<Bluetooth.DEVICE_INFO> found;
                        try
                        {
                            foreach (var d0 in Bluetooth.FindDevices(radio, false, 0))
                                if (Bluetooth.IsWiiController(d0)) seenBefore[d0.Address] = d0.stLastSeen;
                            found = Bluetooth.FindDevices(radio, true, 2);
                        }
                        catch (Exception ex) { L("Bluetooth search failed: " + ex.Message); continue; }

                        bool useFallback = !fallbackDone && paired == 0 && DateTime.UtcNow >= fallbackAt;
                        if (useFallback) fallbackDone = true;

                        foreach (var dev in found.Where(Bluetooth.IsWiiController))
                        {
                            if (done.Contains(dev.Address) || dev.Connected) continue;
                            if (dev.Remembered)
                            {
                                bool answered = seenBefore.TryGetValue(dev.Address, out var before) && !SameTime(before, dev.stLastSeen);
                                if (answered || useFallback)
                                {
                                    uint e = Bluetooth.Remove(dev.Address);
                                    L(e == 0
                                        ? $"{Name(dev)} [{Bluetooth.Format(dev.Address)}] had a pairing that wasn't working; removed it, pairing again…"
                                        : $"Could not remove old pairing of {Name(dev)} (error {e}).");
                                }
                                continue; // picked up as a new device on the next round
                            }
                            var d = dev;
                            if (PairOne(radio, ref d))
                            {
                                done.Add(d.Address);
                                paired++;
                                // Give other controllers a few more seconds, then finish.
                                var soon = DateTime.UtcNow.AddSeconds(6);
                                if (soon < deadline) deadline = soon;
                            }
                        }
                    }
                }
            }
            finally
            {
                foreach (var r in radios) r.Dispose();
            }
            Status?.Invoke(null);
            if (paired == 0 && !ct.IsCancellationRequested)
                L("No controller was found. Press SYNC again and keep the controller close to the PC (LEDs should blink while searching).");
            return paired;
        }

        private static string Name(Bluetooth.DEVICE_INFO d) => string.IsNullOrEmpty(d.szName) ? "Wii controller" : d.szName;

        private static bool SameTime(Bluetooth.SYSTEMTIME a, Bluetooth.SYSTEMTIME b) =>
            a.Year == b.Year && a.Month == b.Month && a.Day == b.Day && a.Hour == b.Hour &&
            a.Minute == b.Minute && a.Second == b.Second && a.Milliseconds == b.Milliseconds;

        private bool PairOne(Bluetooth.Radio radio, ref Bluetooth.DEVICE_INFO d)
        {
            string name = string.IsNullOrEmpty(d.szName) ? "Wii controller" : d.szName;
            L($"Found {name} [{Bluetooth.Format(d.Address)}], pairing…");

            // SYNC button => PIN is the PC's address; 1+2 buttons => PIN is the controller's own address.
            var attempts = new[]
            {
                ("PC address (SYNC button)", Bluetooth.AddressToPin(radio.Address)),
                ("controller address (1+2 buttons)", Bluetooth.AddressToPin(d.Address)),
            };

            bool authed = false;
            foreach (var (label, pin) in attempts)
            {
                uint e = Bluetooth.AuthenticateLegacy(radio, ref d, pin);
                if (e != 0)
                {
                    uint e2 = Bluetooth.AuthenticateWithCallback(radio, ref d, pin);
                    if (e2 == 0) e = 0;
                    else L($"  PIN = {label}: failed (errors {e}/{e2}).");
                }
                if (e == 0)
                {
                    L($"  Paired using PIN = {label}.");
                    authed = true;
                    break;
                }
            }

            uint s = Bluetooth.SetHidService(radio, ref d, true);
            if (s != 0)
            {
                L($"  Could not enable the controller's HID service (error {s}).");
                if (s == 5) L("  Access denied: try running PadBridge as administrator.");
                return false;
            }

            if (authed)
                L($"  {name} is paired. From now on, press any button on it to reconnect.");
            else
                L($"  {name} connected without pairing (temporary). It won't wake up by itself; use 'Pair controller' again next time.");
            return true;
        }

        /// <summary>
        /// Repairs controllers that Windows reports as connected but which never produced a HID device
        /// (a common state after sleep/resume). Toggling the HID service makes Windows rebuild the HID node.
        /// </summary>
        public static void KickHidService(ulong address, Action<string> log)
        {
            var radios = Bluetooth.OpenRadios();
            try
            {
                foreach (var radio in radios)
                {
                    var d = Bluetooth.NewDeviceInfo();
                    d.Address = address;
                    if (!Bluetooth.Refresh(radio, ref d)) continue;
                    Bluetooth.SetHidService(radio, ref d, false);
                    Thread.Sleep(500);
                    uint e = Bluetooth.SetHidService(radio, ref d, true);
                    log?.Invoke(e == 0
                        ? $"Reset HID service of {d.szName} [{Bluetooth.Format(address)}]."
                        : $"HID service reset failed (error {e}).");
                    return;
                }
            }
            finally { foreach (var r in radios) r.Dispose(); }
        }
    }
}
