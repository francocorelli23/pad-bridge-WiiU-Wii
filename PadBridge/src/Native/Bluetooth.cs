using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace PadBridge.Native
{
    /// <summary>Thin wrapper over the Microsoft Bluetooth stack (bthprops.cpl).</summary>
    internal static class Bluetooth
    {
        private const string Dll = "bthprops.cpl";

        public static readonly Guid HidServiceGuid = new Guid("00001124-0000-1000-8000-00805F9B34FB");

        [StructLayout(LayoutKind.Sequential)]
        private struct FIND_RADIO_PARAMS { public int dwSize; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct RADIO_INFO
        {
            public int dwSize;
            public ulong address;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)] public string szName;
            public uint ulClassofDevice;
            public ushort lmpSubversion;
            public ushort manufacturer;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SEARCH_PARAMS
        {
            public int dwSize;
            public int fReturnAuthenticated;
            public int fReturnRemembered;
            public int fReturnUnknown;
            public int fReturnConnected;
            public int fIssueInquiry;
            public byte cTimeoutMultiplier;
            public IntPtr hRadio;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SYSTEMTIME
        {
            public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DEVICE_INFO
        {
            public int dwSize;
            public ulong Address;
            public uint ulClassofDevice;
            public int fConnected;
            public int fRemembered;
            public int fAuthenticated;
            public SYSTEMTIME stLastSeen;
            public SYSTEMTIME stLastUsed;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)] public string szName;

            public bool Connected => fConnected != 0;
            public bool Remembered => fRemembered != 0;
            public bool Authenticated => fAuthenticated != 0;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private delegate bool AuthCallbackEx(IntPtr pvParam, IntPtr pAuthCallbackParams);

        [DllImport(Dll, SetLastError = true)] private static extern IntPtr BluetoothFindFirstRadio(ref FIND_RADIO_PARAMS p, out IntPtr hRadio);
        [DllImport(Dll, SetLastError = true)] private static extern bool BluetoothFindNextRadio(IntPtr hFind, out IntPtr hRadio);
        [DllImport(Dll, SetLastError = true)] private static extern bool BluetoothFindRadioClose(IntPtr hFind);
        [DllImport(Dll, SetLastError = true)] private static extern uint BluetoothGetRadioInfo(IntPtr hRadio, ref RADIO_INFO info);
        [DllImport(Dll, SetLastError = true)] private static extern IntPtr BluetoothFindFirstDevice(ref SEARCH_PARAMS p, ref DEVICE_INFO info);
        [DllImport(Dll, SetLastError = true)] private static extern bool BluetoothFindNextDevice(IntPtr hFind, ref DEVICE_INFO info);
        [DllImport(Dll, SetLastError = true)] private static extern bool BluetoothFindDeviceClose(IntPtr hFind);
        [DllImport(Dll, SetLastError = true)] private static extern uint BluetoothRemoveDevice(ref ulong address);
        [DllImport(Dll, SetLastError = true)] private static extern uint BluetoothGetDeviceInfo(IntPtr hRadio, ref DEVICE_INFO info);
        [DllImport(Dll, SetLastError = true)] private static extern uint BluetoothSetServiceState(IntPtr hRadio, ref DEVICE_INFO info, ref Guid service, uint flags);

        [DllImport(Dll, SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern uint BluetoothAuthenticateDevice(IntPtr hwnd, IntPtr hRadio, ref DEVICE_INFO info,
            [MarshalAs(UnmanagedType.LPArray)] char[] passkey, int passkeyLength);

        [DllImport(Dll, SetLastError = true)]
        private static extern uint BluetoothAuthenticateDeviceEx(IntPtr hwnd, IntPtr hRadio, ref DEVICE_INFO info, IntPtr oob, int requirement);

        [DllImport(Dll, SetLastError = true)]
        private static extern uint BluetoothRegisterForAuthenticationEx(ref DEVICE_INFO info, out IntPtr hReg, AuthCallbackEx cb, IntPtr param);

        [DllImport(Dll, SetLastError = true)] private static extern bool BluetoothUnregisterAuthentication(IntPtr hReg);
        [DllImport(Dll, SetLastError = true)] private static extern uint BluetoothSendAuthenticationResponseEx(IntPtr hRadio, IntPtr response);

        private const uint IOCTL_BTH_DISCONNECT_DEVICE = 0x41000C;

        public sealed class Radio : IDisposable
        {
            public IntPtr Handle { get; }
            public ulong Address { get; }
            public string Name { get; }

            internal Radio(IntPtr h)
            {
                Handle = h;
                var info = new RADIO_INFO { dwSize = Marshal.SizeOf(typeof(RADIO_INFO)) };
                if (BluetoothGetRadioInfo(h, ref info) == 0)
                {
                    Address = info.address;
                    Name = info.szName;
                }
            }

            public void Dispose() => Kernel32.CloseHandle(Handle);
        }

        public static List<Radio> OpenRadios()
        {
            var list = new List<Radio>();
            var p = new FIND_RADIO_PARAMS { dwSize = Marshal.SizeOf(typeof(FIND_RADIO_PARAMS)) };
            IntPtr find = BluetoothFindFirstRadio(ref p, out IntPtr h);
            if (find == IntPtr.Zero) return list;
            try
            {
                do { list.Add(new Radio(h)); } while (BluetoothFindNextRadio(find, out h));
            }
            finally { BluetoothFindRadioClose(find); }
            return list;
        }

        public static List<DEVICE_INFO> FindDevices(Radio radio, bool inquiry, byte timeoutMultiplier)
        {
            var list = new List<DEVICE_INFO>();
            var p = new SEARCH_PARAMS
            {
                dwSize = Marshal.SizeOf(typeof(SEARCH_PARAMS)),
                fReturnAuthenticated = 1,
                fReturnRemembered = 1,
                fReturnUnknown = 1,
                fReturnConnected = 1,
                fIssueInquiry = inquiry ? 1 : 0,
                cTimeoutMultiplier = timeoutMultiplier,
                hRadio = radio.Handle
            };
            var d = NewDeviceInfo();
            IntPtr find = BluetoothFindFirstDevice(ref p, ref d);
            if (find == IntPtr.Zero) return list;
            try
            {
                do
                {
                    list.Add(d);
                    d = NewDeviceInfo();
                } while (BluetoothFindNextDevice(find, ref d));
            }
            finally { BluetoothFindDeviceClose(find); }
            return list;
        }

        public static DEVICE_INFO NewDeviceInfo() => new DEVICE_INFO { dwSize = Marshal.SizeOf(typeof(DEVICE_INFO)) };

        public static bool Refresh(Radio radio, ref DEVICE_INFO d) => BluetoothGetDeviceInfo(radio.Handle, ref d) == 0;

        public static uint Remove(ulong address) => BluetoothRemoveDevice(ref address);

        public static uint SetHidService(Radio radio, ref DEVICE_INFO d, bool enable)
        {
            var g = HidServiceGuid;
            return BluetoothSetServiceState(radio.Handle, ref d, ref g, enable ? 1u : 0u);
        }

        /// <summary>Legacy PIN authentication with a binary 6-byte PIN.</summary>
        public static uint AuthenticateLegacy(Radio radio, ref DEVICE_INFO d, byte[] pin)
        {
            var chars = new char[pin.Length];
            for (int i = 0; i < pin.Length; i++) chars[i] = (char)pin[i];
            return BluetoothAuthenticateDevice(IntPtr.Zero, radio.Handle, ref d, chars, chars.Length);
        }

        /// <summary>
        /// Authentication through a registered callback which answers the legacy PIN request
        /// with the binary PIN. Works on stacks where the legacy call is rejected.
        /// </summary>
        public static uint AuthenticateWithCallback(Radio radio, ref DEVICE_INFO d, byte[] pin)
        {
            IntPtr radioHandle = radio.Handle;
            AuthCallbackEx cb = (param, authParams) =>
            {
                ulong addr = (ulong)Marshal.ReadInt64(authParams, 8);
                int method = Marshal.ReadInt32(authParams, Marshal.SizeOf(typeof(DEVICE_INFO)));
                IntPtr resp = Marshal.AllocHGlobal(48);
                try
                {
                    for (int i = 0; i < 48; i++) Marshal.WriteByte(resp, i, 0);
                    Marshal.WriteInt64(resp, 0, (long)addr);
                    Marshal.WriteInt32(resp, 8, method);
                    if (method == 1) // BLUETOOTH_AUTHENTICATION_METHOD_LEGACY
                    {
                        for (int i = 0; i < pin.Length; i++) Marshal.WriteByte(resp, 12 + i, pin[i]);
                        Marshal.WriteByte(resp, 28, (byte)pin.Length);
                    }
                    BluetoothSendAuthenticationResponseEx(radioHandle, resp);
                }
                finally { Marshal.FreeHGlobal(resp); }
                return true;
            };

            uint reg = BluetoothRegisterForAuthenticationEx(ref d, out IntPtr hReg, cb, IntPtr.Zero);
            if (reg != 0) return reg;
            try
            {
                return BluetoothAuthenticateDeviceEx(IntPtr.Zero, radio.Handle, ref d, IntPtr.Zero, 0);
            }
            finally
            {
                BluetoothUnregisterAuthentication(hReg);
                GC.KeepAlive(cb);
            }
        }

        /// <summary>Drops the baseband link. Wii controllers power off when this happens.</summary>
        public static bool Disconnect(ulong address)
        {
            bool ok = false;
            foreach (var r in OpenRadios())
            {
                using (r)
                {
                    IntPtr buf = Marshal.AllocHGlobal(8);
                    try
                    {
                        Marshal.WriteInt64(buf, (long)address);
                        if (Kernel32.DeviceIoControl(r.Handle, IOCTL_BTH_DISCONNECT_DEVICE, buf, 8, IntPtr.Zero, 0, out _, IntPtr.Zero))
                            ok = true;
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
            }
            return ok;
        }

        public static byte[] AddressToPin(ulong address)
        {
            // BLUETOOTH_ADDRESS.rgBytes is already little-endian ("backwards"), which is what Wii controllers expect.
            var b = BitConverter.GetBytes(address);
            return new[] { b[0], b[1], b[2], b[3], b[4], b[5] };
        }

        public static string Format(ulong address)
        {
            var b = BitConverter.GetBytes(address);
            return $"{b[5]:X2}:{b[4]:X2}:{b[3]:X2}:{b[2]:X2}:{b[1]:X2}:{b[0]:X2}";
        }

        public static bool IsWiiController(DEVICE_INFO d)
        {
            string n = d.szName ?? "";
            if (n.StartsWith("Nintendo RVL-CNT-01", StringComparison.OrdinalIgnoreCase)) return true; // Wiimote, Wiimote Plus (-TR), Wii U Pro (-UC)
            if (n.Length == 0 && (d.ulClassofDevice == 0x002504 || d.ulClassofDevice == 0x000508)) return true;
            return false;
        }

        /// <summary>Extracts the Bluetooth address from a BTHENUM instance id like "...&8CCDE87576E4_C00000000".</summary>
        public static ulong? AddressFromInstanceId(string instanceId)
        {
            if (instanceId == null) return null;
            var m = Regex.Match(instanceId, @"[&\\]([0-9A-Fa-f]{12})_", RegexOptions.RightToLeft);
            if (!m.Success) return null;
            return Convert.ToUInt64(m.Groups[1].Value, 16);
        }
    }
}
