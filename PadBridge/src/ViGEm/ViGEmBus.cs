using System;
using System.ComponentModel;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using PadBridge.Native;

namespace PadBridge.ViGEm
{
    /// <summary>
    /// Minimal ViGEmBus client (Xbox 360 targets only), speaking the bus driver's IOCTL protocol directly.
    /// Closing the bus handle makes the driver unplug every pad we created, so a crash never leaves ghost pads.
    /// </summary>
    internal sealed class ViGEmBus : IDisposable
    {
        private static readonly Guid BusInterface = new Guid("96E42B22-F5E9-42F8-B043-ED0F932F014F");

        internal const uint IOCTL_PLUGIN_TARGET = 0x2AA004;
        internal const uint IOCTL_UNPLUG_TARGET = 0x2AA008;
        internal const uint IOCTL_CHECK_VERSION = 0x2AA00C;
        internal const uint IOCTL_WAIT_DEVICE_READY = 0x2AA010;
        internal const uint IOCTL_XUSB_REQUEST_NOTIFICATION = 0x2AE804;
        internal const uint IOCTL_XUSB_SUBMIT_REPORT = 0x2AA808;
        private const int CommonVersion = 0x0001;

        internal SafeFileHandle Handle { get; }
        private readonly object _ctlLock = new object();
        private readonly AsyncIo _ctlIo = new AsyncIo(64);

        private ViGEmBus(SafeFileHandle h) { Handle = h; }

        public static ViGEmBus Open()
        {
            Exception last = null;
            foreach (var dev in SetupApi.Enumerate(BusInterface))
            {
                var h = Kernel32.CreateFile(dev.Path, Kernel32.GENERIC_READ | Kernel32.GENERIC_WRITE,
                    Kernel32.FILE_SHARE_READ | Kernel32.FILE_SHARE_WRITE, IntPtr.Zero, Kernel32.OPEN_EXISTING,
                    Kernel32.FILE_ATTRIBUTE_NORMAL | Kernel32.FILE_FLAG_NO_BUFFERING | Kernel32.FILE_FLAG_WRITE_THROUGH | Kernel32.FILE_FLAG_OVERLAPPED,
                    IntPtr.Zero);
                if (h.IsInvalid) { last = new Win32Exception(); continue; }

                var bus = new ViGEmBus(h);
                try
                {
                    var req = new byte[8];
                    BitConverter.GetBytes(8).CopyTo(req, 0);
                    BitConverter.GetBytes(CommonVersion).CopyTo(req, 4);
                    bus.Control(IOCTL_CHECK_VERSION, req, 0, 3000);
                    return bus;
                }
                catch (Exception ex)
                {
                    last = ex;
                    bus.Dispose();
                }
            }
            throw new InvalidOperationException(
                "ViGEmBus driver not found or incompatible. Install it from https://github.com/nefarius/ViGEmBus/releases" +
                (last != null ? $" ({last.Message})" : ""));
        }

        /// <summary>Synchronous IOCTL. Returns bytes returned; throws on failure.</summary>
        internal byte[] Control(uint code, byte[] input, int outLen, int timeoutMs)
        {
            lock (_ctlLock)
            {
                _ctlIo.Load(input);
                int n = _ctlIo.Run(Handle, ov => Kernel32.DeviceIoControl(Handle, code, _ctlIo.Buffer, input.Length,
                    outLen > 0 ? _ctlIo.Buffer : IntPtr.Zero, outLen, IntPtr.Zero, ov), timeoutMs);
                if (n < 0) throw new TimeoutException($"ViGEm IOCTL 0x{code:X} timed out");
                return _ctlIo.Read(outLen);
            }
        }

        public Xbox360Pad PlugXbox360()
        {
            for (int serial = 1; serial <= 16; serial++)
            {
                var req = new byte[16];
                BitConverter.GetBytes(16).CopyTo(req, 0);
                BitConverter.GetBytes(serial).CopyTo(req, 4);
                BitConverter.GetBytes(0).CopyTo(req, 8);                 // Xbox360Wired
                BitConverter.GetBytes((ushort)0x045E).CopyTo(req, 12);   // Microsoft
                BitConverter.GetBytes((ushort)0x028E).CopyTo(req, 14);   // Xbox 360 Controller
                try
                {
                    Control(IOCTL_PLUGIN_TARGET, req, 0, 5000);
                }
                catch (Win32Exception)
                {
                    // The bus answers "invalid parameter" or "already exists" when the serial is taken
                    // (by another pad of ours or another app). Try the next one.
                    continue;
                }

                try
                {
                    var wait = new byte[8];
                    BitConverter.GetBytes(8).CopyTo(wait, 0);
                    BitConverter.GetBytes(serial).CopyTo(wait, 4);
                    Control(IOCTL_WAIT_DEVICE_READY, wait, 0, 5000);
                }
                catch { /* older bus versions don't support this; not fatal */ }

                return new Xbox360Pad(this, serial);
            }
            throw new InvalidOperationException("Could not create a virtual Xbox 360 controller (no free slot on the bus).");
        }

        internal void Unplug(int serial)
        {
            var req = new byte[8];
            BitConverter.GetBytes(8).CopyTo(req, 0);
            BitConverter.GetBytes(serial).CopyTo(req, 4);
            try { Control(IOCTL_UNPLUG_TARGET, req, 0, 3000); } catch { }
        }

        public void Dispose()
        {
            Handle.Dispose();
            _ctlIo.Dispose();
        }
    }

    [Flags]
    internal enum XButtons : ushort
    {
        None = 0,
        DPadUp = 0x0001, DPadDown = 0x0002, DPadLeft = 0x0004, DPadRight = 0x0008,
        Start = 0x0010, Back = 0x0020, LeftThumb = 0x0040, RightThumb = 0x0080,
        LeftShoulder = 0x0100, RightShoulder = 0x0200, Guide = 0x0400,
        A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000
    }

    internal struct XReport
    {
        public XButtons Buttons;
        public byte LeftTrigger, RightTrigger;
        public short LX, LY, RX, RY;

        public bool Equals(XReport o) =>
            Buttons == o.Buttons && LeftTrigger == o.LeftTrigger && RightTrigger == o.RightTrigger &&
            LX == o.LX && LY == o.LY && RX == o.RX && RY == o.RY;
    }

    internal sealed class Xbox360Pad : IDisposable
    {
        private readonly ViGEmBus _bus;
        public int Serial { get; }
        private readonly AsyncIo _submitIo = new AsyncIo(32);
        private readonly object _submitLock = new object();
        private readonly Thread _notifyThread;
        private volatile bool _disposed;

        /// <summary>(largeMotor, smallMotor, ledNumber 0-3)</summary>
        public event Action<byte, byte, byte> Feedback;

        internal Xbox360Pad(ViGEmBus bus, int serial)
        {
            _bus = bus;
            Serial = serial;
            _notifyThread = new Thread(NotifyLoop) { IsBackground = true, Name = $"ViGEm notify {serial}" };
            _notifyThread.Start();
        }

        public void Submit(XReport r)
        {
            if (_disposed) return;
            var b = new byte[20];
            BitConverter.GetBytes(20).CopyTo(b, 0);
            BitConverter.GetBytes(Serial).CopyTo(b, 4);
            BitConverter.GetBytes((ushort)r.Buttons).CopyTo(b, 8);
            b[10] = r.LeftTrigger;
            b[11] = r.RightTrigger;
            BitConverter.GetBytes(r.LX).CopyTo(b, 12);
            BitConverter.GetBytes(r.LY).CopyTo(b, 14);
            BitConverter.GetBytes(r.RX).CopyTo(b, 16);
            BitConverter.GetBytes(r.RY).CopyTo(b, 18);
            lock (_submitLock)
            {
                if (_disposed) return;
                _submitIo.Load(b);
                try
                {
                    _submitIo.Run(_bus.Handle, ov => Kernel32.DeviceIoControl(_bus.Handle, ViGEmBus.IOCTL_XUSB_SUBMIT_REPORT,
                        _submitIo.Buffer, 20, IntPtr.Zero, 0, IntPtr.Zero, ov), 1000);
                }
                catch (Win32Exception) { }
            }
        }

        private void NotifyLoop()
        {
            using (var io = new AsyncIo(16))
            {
                while (!_disposed)
                {
                    var req = new byte[12];
                    BitConverter.GetBytes(12).CopyTo(req, 0);
                    BitConverter.GetBytes(Serial).CopyTo(req, 4);
                    io.Load(req);
                    int n;
                    try
                    {
                        n = io.Run(_bus.Handle, ov => Kernel32.DeviceIoControl(_bus.Handle, ViGEmBus.IOCTL_XUSB_REQUEST_NOTIFICATION,
                            io.Buffer, 12, io.Buffer, 12, IntPtr.Zero, ov), -1, () => _disposed);
                    }
                    catch (Exception)
                    {
                        if (_disposed) return;
                        Thread.Sleep(500);
                        continue;
                    }
                    if (n < 0 || _disposed) continue;
                    var res = io.Read(12);
                    try { Feedback?.Invoke(res[8], res[9], res[10]); } catch { }
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _notifyThread.Join(2000);
            lock (_submitLock)
            {
                _bus.Unplug(Serial);
                _submitIo.Dispose();
            }
        }
    }
}
