using System;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace VirtualBattery
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            using (new Mutex(true, @"Global\VirtualBatterySetup", out bool first))
            {
                if (!first) return 2;

                bool Has(string name) => args.Contains(name, StringComparer.OrdinalIgnoreCase);
                if (Has("/quiet"))
                {
                    if (Has("/install")) return RunQuiet(i => i.Install(100));
                    if (Has("/uninstall")) return RunQuiet(i => { i.Uninstall(); return true; });
                    return 1;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm(Has("/uninstall")));
                return 0;
            }
        }

        // 0: success, 1: failure, 3010: installed but Windows shows no battery yet.
        static int RunQuiet(Func<Installer, bool> action)
        {
            try { return action(new Installer(Console.WriteLine)) ? 0 : 3010; }
            catch (Exception ex)
            {
                Console.WriteLine(Strings.Error + ex);
                return 1;
            }
        }
    }
}
