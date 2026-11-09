using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using PadBridge.Native;
using PadBridge.ViGEm;
using PadBridge.Wii;

namespace PadBridge
{
    /// <summary>
    /// "PadBridge.exe --selftest": checks the virtual pad pipeline end to end (ViGEm -> XInput) and lists
    /// Bluetooth / HID state. Output goes to padbridge-selftest.txt next to the exe.
    /// </summary>
    internal static class SelfTest
    {
        [DllImport("xinput1_4.dll")]
        private static extern int XInputGetState(int index, byte[] state); // XINPUT_STATE = 16 bytes

        public static int Run()
        {
            var sb = new StringBuilder();
            void W(string s) => sb.AppendLine(s);
            int failures = 0;

            W("== Bluetooth ==");
            try
            {
                foreach (var r in Bluetooth.OpenRadios())
                    using (r)
                    {
                        W($"Radio: {r.Name} [{Bluetooth.Format(r.Address)}]");
                        foreach (var d in Bluetooth.FindDevices(r, false, 0))
                            W($"  {(Bluetooth.IsWiiController(d) ? "*" : " ")} {d.szName} [{Bluetooth.Format(d.Address)}] " +
                              $"cod=0x{d.ulClassofDevice:X6} connected={d.Connected} remembered={d.Remembered} authenticated={d.Authenticated}");
                    }
            }
            catch (Exception ex) { W("Bluetooth error: " + ex); failures++; }

            W("== Wii HID devices ==");
            foreach (var dev in SetupApi.Enumerate(Hid.HidGuid))
            {
                if (!WiiController.IsWiiDevice(dev.Path)) continue;
                var parent = SetupApi.GetParentInstanceId(dev.DevInst);
                var addr = Bluetooth.AddressFromInstanceId(parent);
                W($"  {dev.Path}\n    parent={parent} addr={(addr.HasValue ? Bluetooth.Format(addr.Value) : "?")}");
            }

            W("== ViGEm -> XInput ==");
            try
            {
                var before = ConnectedXInput();
                using (var bus = ViGEmBus.Open())
                {
                    W("Bus opened, version check OK.");
                    using (var pad = bus.PlugXbox360())
                    {
                        W($"Plugged virtual pad serial {pad.Serial}.");
                        int feedbacks = 0;
                        pad.Feedback += (l, s, led) => Interlocked.Increment(ref feedbacks);
                        Thread.Sleep(1500);
                        var report = new XReport { Buttons = XButtons.A | XButtons.DPadUp, LeftTrigger = 200, LX = 12345, RY = -23456 };
                        pad.Submit(report);
                        Thread.Sleep(300);

                        int found = -1;
                        var st = new byte[16];
                        for (int i = 0; i < 4; i++)
                        {
                            if ((before & (1 << i)) != 0) continue;
                            if (XInputGetState(i, st) != 0) continue;
                            ushort buttons = BitConverter.ToUInt16(st, 4);
                            short lx = BitConverter.ToInt16(st, 8);
                            short ry = BitConverter.ToInt16(st, 14);
                            W($"  XInput slot {i}: buttons=0x{buttons:X4} LT={st[6]} LX={lx} RY={ry}");
                            if (buttons == 0x1001 && st[6] == 200 && lx == 12345 && ry == -23456) found = i;
                        }
                        if (found >= 0) W($"PASS: report arrived on XInput slot {found}. Feedback notifications: {feedbacks}");
                        else { W("FAIL: report not seen through XInput."); failures++; }
                    }
                    W("Unplugged.");
                }
            }
            catch (Exception ex) { W("ViGEm error: " + ex); failures++; }

            W("== Mapping ==");
            var p = new PadInput();
            p.Set(PadButton.A, true); p.Set(PadButton.ZR, true); p.LX = 1f; p.LY = 0.05f;
            var x = Mapper.ToXbox(p, new MappingOptions { PositionalLayout = true });
            bool mapOk = x.Buttons == XButtons.B && x.RightTrigger == 255 && x.LX > 32000 && Math.Abs((int)x.LY) < 3000;
            W($"{(mapOk ? "PASS" : "FAIL")}: A->{x.Buttons}, RT={x.RightTrigger}, LX={x.LX}, LY={x.LY}");
            if (!mapOk) failures++;

            W(failures == 0 ? "ALL OK" : $"{failures} FAILURE(S)");
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "padbridge-selftest.txt");
            File.WriteAllText(path, sb.ToString());
            return failures;
        }

        private static int ConnectedXInput()
        {
            int mask = 0;
            var st = new byte[16];
            for (int i = 0; i < 4; i++) if (XInputGetState(i, st) == 0) mask |= 1 << i;
            return mask;
        }
    }
}
