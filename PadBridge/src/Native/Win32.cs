using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PadBridge.Native
{
    internal static class Kernel32
    {
        public const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
        public const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
        public const uint FILE_ATTRIBUTE_NORMAL = 0x80, FILE_FLAG_OVERLAPPED = 0x40000000;
        public const uint FILE_FLAG_NO_BUFFERING = 0x20000000, FILE_FLAG_WRITE_THROUGH = 0x80000000;

        public const int ERROR_IO_PENDING = 997;
        public const int ERROR_OPERATION_ABORTED = 995;
        public const int ERROR_ALREADY_EXISTS = 183;
        public const int ERROR_INVALID_PARAMETER = 87;
        public const uint WAIT_OBJECT_0 = 0, WAIT_TIMEOUT = 0x102, INFINITE = 0xFFFFFFFF;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec,
            uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ReadFile(SafeFileHandle h, IntPtr buf, int len, IntPtr read, IntPtr ov);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool WriteFile(SafeFileHandle h, IntPtr buf, int len, IntPtr written, IntPtr ov);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool DeviceIoControl(SafeHandle h, uint code, IntPtr inBuf, int inLen,
            IntPtr outBuf, int outLen, IntPtr returned, IntPtr ov);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool DeviceIoControl(IntPtr h, uint code, IntPtr inBuf, int inLen,
            IntPtr outBuf, int outLen, out int returned, IntPtr ov);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetOverlappedResult(SafeHandle h, IntPtr ov, out int transferred, bool wait);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CancelIoEx(SafeHandle h, IntPtr ov);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateEvent(IntPtr sa, bool manualReset, bool initialState, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ResetEvent(IntPtr h);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WaitForSingleObject(IntPtr h, uint ms);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr h);

        [DllImport("kernel32.dll")]
        public static extern void RtlZeroMemory(IntPtr dst, UIntPtr len);
    }

    /// <summary>
    /// One reusable OVERLAPPED + event + unmanaged buffer. Not thread safe: one operation at a time.
    /// </summary>
    internal sealed class AsyncIo : IDisposable
    {
        private const int OverlappedSize = 32; // x64 OVERLAPPED
        public readonly IntPtr Overlapped;
        public readonly IntPtr Buffer;
        public readonly int BufferSize;
        private readonly IntPtr _event;
        private bool _disposed;

        public AsyncIo(int bufferSize)
        {
            BufferSize = Math.Max(bufferSize, 1);
            Overlapped = Marshal.AllocHGlobal(OverlappedSize);
            Buffer = Marshal.AllocHGlobal(BufferSize);
            _event = Kernel32.CreateEvent(IntPtr.Zero, true, false, null);
        }

        public void Load(byte[] data)
        {
            Kernel32.RtlZeroMemory(Buffer, (UIntPtr)BufferSize);
            Marshal.Copy(data, 0, Buffer, Math.Min(data.Length, BufferSize));
        }

        public byte[] Read(int count)
        {
            var b = new byte[Math.Min(count, BufferSize)];
            Marshal.Copy(Buffer, b, 0, b.Length);
            return b;
        }

        /// <summary>
        /// Starts an overlapped operation and waits for it.
        /// Returns bytes transferred, or -1 when it timed out / was aborted (the I/O is cancelled).
        /// Throws Win32Exception on real errors.
        /// </summary>
        public int Run(SafeHandle h, Func<IntPtr, bool> start, int timeoutMs, Func<bool> abort = null)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(AsyncIo));
            Kernel32.RtlZeroMemory(Overlapped, (UIntPtr)OverlappedSize);
            Marshal.WriteIntPtr(Overlapped, 24, _event);
            Kernel32.ResetEvent(_event);

            if (!start(Overlapped))
            {
                int err = Marshal.GetLastWin32Error();
                if (err != Kernel32.ERROR_IO_PENDING) throw new Win32Exception(err);
            }

            var started = Environment.TickCount;
            while (true)
            {
                uint slice = 250;
                if (timeoutMs >= 0)
                {
                    int left = timeoutMs - (Environment.TickCount - started);
                    if (left <= 0) { Cancel(h); return -1; }
                    slice = (uint)Math.Min(left, 250);
                }
                uint w = Kernel32.WaitForSingleObject(_event, slice);
                if (w == Kernel32.WAIT_OBJECT_0) break;
                if (abort != null && abort()) { Cancel(h); return -1; }
            }

            if (!Kernel32.GetOverlappedResult(h, Overlapped, out int n, false))
            {
                int err = Marshal.GetLastWin32Error();
                if (err == Kernel32.ERROR_OPERATION_ABORTED) return -1;
                throw new Win32Exception(err);
            }
            return n;
        }

        private void Cancel(SafeHandle h)
        {
            Kernel32.CancelIoEx(h, Overlapped);
            Kernel32.GetOverlappedResult(h, Overlapped, out _, true);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Kernel32.CloseHandle(_event);
            Marshal.FreeHGlobal(Overlapped);
            Marshal.FreeHGlobal(Buffer);
        }
    }

    internal struct DeviceInterface
    {
        public string Path;
        public int DevInst;
    }

    internal static class SetupApi
    {
        private const int DIGCF_PRESENT = 0x2, DIGCF_DEVICEINTERFACE = 0x10;

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVINFO_DATA
        {
            public int cbSize;
            public Guid ClassGuid;
            public int DevInst;
            public IntPtr Reserved;
        }

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr enumerator, IntPtr hwnd, int flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid g, int index,
            ref SP_DEVICE_INTERFACE_DATA data);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data,
            IntPtr detail, int size, out int required, ref SP_DEVINFO_DATA devInfo);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Get_Parent(out int parent, int devInst, int flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_Device_ID(int devInst, StringBuilder buffer, int len, int flags);

        public static List<DeviceInterface> Enumerate(Guid iface)
        {
            var result = new List<DeviceInterface>();
            IntPtr set = SetupDiGetClassDevs(ref iface, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (set == IntPtr.Zero || set == new IntPtr(-1)) return result;
            try
            {
                for (int i = 0; ; i++)
                {
                    var data = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA)) };
                    if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref iface, i, ref data)) break;

                    var info = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf(typeof(SP_DEVINFO_DATA)) };
                    SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out int required, ref info);
                    if (required <= 0) continue;
                    IntPtr detail = Marshal.AllocHGlobal(required);
                    try
                    {
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        if (SetupDiGetDeviceInterfaceDetail(set, ref data, detail, required, out _, ref info))
                        {
                            result.Add(new DeviceInterface
                            {
                                Path = Marshal.PtrToStringUni(detail + 4),
                                DevInst = info.DevInst
                            });
                        }
                    }
                    finally { Marshal.FreeHGlobal(detail); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return result;
        }

        public static string GetParentInstanceId(int devInst)
        {
            if (CM_Get_Parent(out int parent, devInst, 0) != 0) return null;
            var sb = new StringBuilder(512);
            return CM_Get_Device_ID(parent, sb, sb.Capacity, 0) == 0 ? sb.ToString() : null;
        }
    }

    internal static class Hid
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct HIDD_ATTRIBUTES
        {
            public int Size;
            public ushort VendorID;
            public ushort ProductID;
            public ushort VersionNumber;
        }

        [DllImport("hid.dll")]
        public static extern void HidD_GetHidGuid(out Guid g);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES attrs);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_SetOutputReport(SafeFileHandle h, byte[] buf, int len);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr data);

        [DllImport("hid.dll")]
        public static extern bool HidD_FreePreparsedData(IntPtr data);

        [DllImport("hid.dll")]
        public static extern int HidP_GetCaps(IntPtr preparsed, IntPtr caps);

        public static Guid HidGuid
        {
            get { HidD_GetHidGuid(out Guid g); return g; }
        }

        /// <summary>Returns (inputLen, outputLen) report byte lengths, or null.</summary>
        public static (int input, int output)? GetReportLengths(SafeFileHandle h)
        {
            if (!HidD_GetPreparsedData(h, out IntPtr pp)) return null;
            IntPtr caps = Marshal.AllocHGlobal(64);
            try
            {
                if (HidP_GetCaps(pp, caps) != 0x110000) return null; // HIDP_STATUS_SUCCESS
                return (Marshal.ReadInt16(caps, 4), Marshal.ReadInt16(caps, 6));
            }
            finally
            {
                Marshal.FreeHGlobal(caps);
                HidD_FreePreparsedData(pp);
            }
        }
    }
}
