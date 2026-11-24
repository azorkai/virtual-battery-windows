using System.Runtime.InteropServices;

namespace VirtualBattery
{
    /// <summary>What Windows reports to every application.</summary>
    struct PowerStatus
    {
        public bool HasBattery;
        public int Percent;

        public static PowerStatus Read()
        {
            if (!GetSystemPowerStatus(out Native s)) return default(PowerStatus);
            return new PowerStatus
            {
                HasBattery = s.BatteryFlag != 255 && (s.BatteryFlag & 128) == 0,
                Percent = s.Percent,
            };
        }

        [StructLayout(LayoutKind.Sequential)]
        struct Native
        {
            public byte AcLine, BatteryFlag, Percent, Saver;
            public int LifeTime, FullLifeTime;
        }

        [DllImport("kernel32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern bool GetSystemPowerStatus(out Native status);
    }
}
