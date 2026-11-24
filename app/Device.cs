using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace VirtualBattery
{
    /// <summary>SetupAPI wrappers for the single root-enumerated device.</summary>
    static class Device
    {
        const string HardwareId = @"Root\VirtualBatteryHid";
        const string DeviceName = "VIRTUALBATTERY";
        const string SetupApi = "setupapi.dll";
        const int HardwareIdProperty = 1;   // SPDRP_HARDWAREID
        const int GlobalScope = 1;          // DICS_FLAG_GLOBAL
        const int DeviceKey = 1;            // DIREG_DEV
        static readonly IntPtr Invalid = new IntPtr(-1);
        static Guid hidClass = new Guid("745a17a0-74d3-11d0-b6fe-00a0c90f57da");

        public static List<string> Find()
        {
            var result = new List<string>();
            ForEach((set, data, id) => { result.Add(id); return true; });
            return result;
        }

        public static void Create(int percent)
        {
            IntPtr set = SetupDiCreateDeviceInfoList(ref hidClass, IntPtr.Zero);
            if (set == Invalid) throw new Win32Exception();
            try
            {
                DevInfo data = DevInfo.New();
                Check(SetupDiCreateDeviceInfoW(set, DeviceName, ref hidClass, "Virtual Battery", IntPtr.Zero, 1 /* generate id */, ref data));
                byte[] id = Encoding.Unicode.GetBytes(HardwareId + "\0\0");
                Check(SetupDiSetDeviceRegistryPropertyW(set, ref data, HardwareIdProperty, id, id.Length));
                Check(SetupDiCallClassInstaller(0x19 /* DIF_REGISTERDEVICE */, set, ref data));
                SetLevel(set, ref data, percent);
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
        }

        public static void InstallDriver(string infPath, out bool reboot)
        {
            Check(UpdateDriverForPlugAndPlayDevicesW(IntPtr.Zero, HardwareId, infPath, 1 /* force */, out reboot));
        }

        public static bool SetLevel(int percent)
        {
            bool found = false;
            ForEach((set, data, id) =>
            {
                SetLevel(set, ref data, percent);
                found = true;
                return true;
            });
            return found;
        }

        public static int GetLevel()
        {
            int percent = 100;
            ForEach((set, data, id) =>
            {
                IntPtr handle = SetupDiOpenDevRegKey(set, ref data, GlobalScope, 0, DeviceKey, 0x20019 /* KEY_READ */);
                if (handle == Invalid) return true;
                using (RegistryKey key = RegistryKey.FromHandle(new SafeRegistryHandle(handle, true)))
                {
                    percent = Convert.ToInt32(key.GetValue("BatteryPercent", 100));
                }
                return false;
            });
            return Math.Max(40, Math.Min(100, percent));
        }

        static void SetLevel(IntPtr set, ref DevInfo data, int percent)
        {
            IntPtr handle = SetupDiCreateDevRegKeyW(set, ref data, GlobalScope, 0, DeviceKey, IntPtr.Zero, null);
            if (handle == Invalid) throw new Win32Exception();
            using (RegistryKey key = RegistryKey.FromHandle(new SafeRegistryHandle(handle, true)))
            {
                key.SetValue("BatteryPercent", percent, RegistryValueKind.DWord);
            }
        }

        /// <summary>Visits our devices, including ones that are not started, until the visitor returns false.</summary>
        static void ForEach(Visitor visit)
        {
            IntPtr set = SetupDiGetClassDevsW(IntPtr.Zero, "ROOT", IntPtr.Zero, 4 /* DIGCF_ALLCLASSES */);
            if (set == Invalid) throw new Win32Exception();
            try
            {
                DevInfo data = DevInfo.New();
                for (int i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
                {
                    var buffer = new byte[2048];
                    if (!SetupDiGetDeviceRegistryPropertyW(set, ref data, HardwareIdProperty, IntPtr.Zero, buffer, buffer.Length, out int size))
                        continue;
                    string first = Encoding.Unicode.GetString(buffer, 0, size).Split('\0')[0];
                    if (!first.Equals(HardwareId, StringComparison.OrdinalIgnoreCase)) continue;
                    var id = new StringBuilder(512);
                    if (!SetupDiGetDeviceInstanceIdW(set, ref data, id, id.Capacity, IntPtr.Zero)) continue;
                    if (!visit(set, data, id.ToString())) break;
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
        }

        static void Check(bool ok)
        {
            if (!ok) throw new Win32Exception();
        }

        delegate bool Visitor(IntPtr set, DevInfo data, string instanceId);

        [StructLayout(LayoutKind.Sequential)]
        struct DevInfo
        {
            public int Size;
            public Guid Class;
            public int DevInst;
            public IntPtr Reserved;
            public static DevInfo New() => new DevInfo { Size = Marshal.SizeOf(typeof(DevInfo)) };
        }

        [DllImport(SetupApi, SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid guid, IntPtr parent);
        [DllImport(SetupApi, SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport(SetupApi, SetLastError = true, CharSet = CharSet.Unicode), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern bool SetupDiCreateDeviceInfoW(IntPtr set, string name, ref Guid guid, string description, IntPtr parent, int flags, ref DevInfo data);
        [DllImport(SetupApi, SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern bool SetupDiSetDeviceRegistryPropertyW(IntPtr set, ref DevInfo data, int property, byte[] buffer, int size);
        [DllImport(SetupApi, SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr set, ref DevInfo data, int property, IntPtr type, byte[] buffer, int size, out int required);
        [DllImport(SetupApi, SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern bool SetupDiCallClassInstaller(int function, IntPtr set, ref DevInfo data);
        [DllImport(SetupApi, SetLastError = true, CharSet = CharSet.Unicode), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern IntPtr SetupDiGetClassDevsW(IntPtr guid, string enumerator, IntPtr parent, int flags);
        [DllImport(SetupApi, SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern bool SetupDiEnumDeviceInfo(IntPtr set, int index, ref DevInfo data);
        [DllImport(SetupApi, SetLastError = true, CharSet = CharSet.Unicode), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern bool SetupDiGetDeviceInstanceIdW(IntPtr set, ref DevInfo data, StringBuilder id, int size, IntPtr required);
        [DllImport(SetupApi, SetLastError = true, CharSet = CharSet.Unicode), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern IntPtr SetupDiCreateDevRegKeyW(IntPtr set, ref DevInfo data, int scope, int profile, int type, IntPtr inf, string section);
        [DllImport(SetupApi, SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern IntPtr SetupDiOpenDevRegKey(IntPtr set, ref DevInfo data, int scope, int profile, int type, int access);
        [DllImport("newdev.dll", SetLastError = true, CharSet = CharSet.Unicode), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern bool UpdateDriverForPlugAndPlayDevicesW(IntPtr parent, string hardwareId, string inf, int flags, out bool reboot);
    }
}
