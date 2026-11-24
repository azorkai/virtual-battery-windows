using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace VirtualBattery
{
    sealed class Installer
    {
        const string StateKey = @"SOFTWARE\VirtualBattery";
        const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\VirtualBattery";
        static readonly string[] DriverFiles = { "VirtualBattery.inf", "VirtualBattery.dll", "VirtualBattery.cat" };

        static readonly string InstallDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Virtual Battery");
        static readonly string DriverDir = Path.Combine(InstallDir, "driver");
        static readonly string InstalledExe = Path.Combine(InstallDir, "VirtualBatterySetup.exe");

        readonly Action<string> log;

        public Installer(Action<string> log) { this.log = log; }

        public static bool IsInstalled => Device.Find().Count > 0;

        /// <summary>Returns null when this PC is supported, otherwise the reason.</summary>
        public static string GetBlocker()
        {
            if (Environment.OSVersion.Version.Build < 19041) return Strings.NeedsNewerWindows;
            string arch = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Environment",
                "PROCESSOR_ARCHITECTURE", "") as string;
            return arch == "AMD64" ? null : Strings.NeedsX64;
        }

        /// <summary>Installs and returns true if Windows now reports a battery.</summary>
        public bool Install(int percent)
        {
            string blocker = GetBlocker();
            if (blocker != null) throw new InvalidOperationException(blocker);

            RemoveDriver();
            try
            {
                log(Strings.StepFiles);
                Directory.CreateDirectory(DriverDir);
                foreach (string name in DriverFiles) Extract(name, Path.Combine(DriverDir, name));
                string exe = Assembly.GetExecutingAssembly().Location;
                if (!exe.Equals(InstalledExe, StringComparison.OrdinalIgnoreCase))
                {
                    ClearReadOnly(InstalledExe);
                    File.Copy(exe, InstalledExe, true);
                    ClearReadOnly(InstalledExe);
                }

                log(Strings.StepSign);
                using (X509Certificate2 certificate = CatalogSigner.Sign(Path.Combine(DriverDir, "VirtualBattery.cat")))
                {
                    AddCertificate(StoreName.Root, certificate);
                    AddCertificate(StoreName.TrustedPublisher, certificate);
                }

                log(Strings.StepDriver);
                string inf = Path.Combine(DriverDir, "VirtualBattery.inf");
                var published = new StringBuilder(260);
                if (!SetupCopyOEMInfW(inf, null, 1 /* path */, 0, published, published.Capacity, IntPtr.Zero, IntPtr.Zero))
                    throw new Win32Exception();
                using (RegistryKey state = Registry.LocalMachine.CreateSubKey(StateKey))
                    state.SetValue("Inf", Path.GetFileName(published.ToString()));

                log(Strings.StepDevice);
                Device.Create(percent);
                Device.InstallDriver(inf, out bool reboot);
                RegisterUninstaller();

                log(Strings.StepWait);
                for (int i = 0; i < 60; i++)
                {
                    if (PowerStatus.Read().HasBattery) { log(Strings.Done); return true; }
                    Thread.Sleep(500);
                }
                log(reboot ? Strings.NeedsReboot : Strings.NoBatteryYet);
                return false;
            }
            catch
            {
                log(Strings.RollingBack);
                try { Uninstall(); } catch (Exception ex) { log(ex.Message); }
                throw;
            }
        }

        public void Uninstall()
        {
            RemoveDriver();
            Registry.LocalMachine.DeleteSubKeyTree(StateKey, false);
            Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, false);

            if (Directory.Exists(DriverDir)) Directory.Delete(DriverDir, true);
            if (File.Exists(InstalledExe))
            {
                ClearReadOnly(InstalledExe);
                try { File.Delete(InstalledExe); }
                catch (UnauthorizedAccessException)
                {
                    // Running from the install folder: Windows removes it at the next start.
                    MoveFileExW(InstalledExe, null, 4);
                    MoveFileExW(InstallDir, null, 4);
                }
            }
            if (Directory.Exists(InstallDir) && Directory.GetFileSystemEntries(InstallDir).Length == 0)
                Directory.Delete(InstallDir);
            log(Strings.Removed);
        }

        void RemoveDriver()
        {
            foreach (string id in Device.Find())
            {
                log(Strings.StepRemoveDevice);
                Run("pnputil.exe", "/remove-device \"" + id + "\" /subtree");
            }

            using (RegistryKey state = Registry.LocalMachine.OpenSubKey(StateKey, true))
            {
                string inf = state?.GetValue("Inf") as string;
                if (inf != null && inf.StartsWith("oem", StringComparison.OrdinalIgnoreCase) &&
                    !SetupUninstallOEMInfW(inf, 1 /* force */, IntPtr.Zero))
                    log(inf + ": " + new Win32Exception().Message);
                state?.DeleteValue("Inf", false);
            }
            RemoveCertificates(StoreName.TrustedPublisher);
            RemoveCertificates(StoreName.Root);
        }

        /// <summary>A copy from read-only media would otherwise block repair and removal.</summary>
        static void ClearReadOnly(string path)
        {
            if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
        }

        static void Extract(string resource, string path)
        {
            using (Stream input = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
            using (FileStream output = File.Create(path))
                input.CopyTo(output);
        }

        static void AddCertificate(StoreName name, X509Certificate2 certificate)
        {
            var store = new X509Store(name, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadWrite);
            try { store.Add(certificate); } finally { store.Close(); }
        }

        static void RemoveCertificates(StoreName name)
        {
            var store = new X509Store(name, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadWrite);
            try
            {
                var ours = new List<X509Certificate2>();
                foreach (X509Certificate2 c in store.Certificates)
                    if (c.Subject == CatalogSigner.Subject && c.Issuer == CatalogSigner.Subject) ours.Add(c);
                foreach (X509Certificate2 c in ours) store.Remove(c);
            }
            finally { store.Close(); }
        }

        static void RegisterUninstaller()
        {
            using (RegistryKey key = Registry.LocalMachine.CreateSubKey(UninstallKey))
            {
                key.SetValue("DisplayName", "Virtual Battery");
                key.SetValue("DisplayVersion", Assembly.GetExecutingAssembly().GetName().Version.ToString(3));
                key.SetValue("Publisher", "azorkai");
                key.SetValue("DisplayIcon", InstalledExe);
                key.SetValue("InstallLocation", InstallDir);
                key.SetValue("URLInfoAbout", "https://github.com/azorkai/virtual-battery-windows");
                key.SetValue("ModifyPath", "\"" + InstalledExe + "\"");
                key.SetValue("UninstallString", "\"" + InstalledExe + "\" /uninstall");
                key.SetValue("QuietUninstallString", "\"" + InstalledExe + "\" /uninstall /quiet");
                key.SetValue("NoRepair", 1);
            }
        }

        void Run(string system32Exe, string arguments)
        {
            var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, system32Exe), arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using (Process process = Process.Start(info))
            {
                if (!process.WaitForExit(60000)) throw new TimeoutException(system32Exe);
                if (process.ExitCode != 0 && process.ExitCode != 3010) log(system32Exe + ": " + process.ExitCode);
            }
        }

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern bool SetupCopyOEMInfW(string source, string location, int mediaType, int style, StringBuilder destination, int size, IntPtr required, IntPtr component);
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern bool SetupUninstallOEMInfW(string inf, int flags, IntPtr reserved);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern bool MoveFileExW(string from, string to, int flags);
    }
}
