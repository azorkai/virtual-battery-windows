using System.Globalization;

namespace VirtualBattery
{
    static class Strings
    {
        static readonly bool Tr = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "tr";
        static string T(string tr, string en) => Tr ? tr : en;

        public static string NeedsNewerWindows => T("Windows 10 2004 (19041) veya daha yeni bir sürüm gerekiyor.", "Windows 10 2004 (19041) or newer is required.");
        public static string NeedsX64 => T("Yalnızca 64 bit Intel/AMD (x64) Windows destekleniyor.", "Only 64-bit Intel/AMD (x64) Windows is supported.");
        public static string StepFiles => T("Dosyalar hazırlanıyor…", "Preparing files…");
        public static string StepSign => T("Bu bilgisayara özel imza oluşturuluyor (anahtar hemen siliniyor)…", "Creating a signature for this PC (its key is destroyed immediately)…");
        public static string StepDriver => T("Kullanıcı modu sürücüsü ekleniyor…", "Adding the user-mode driver…");
        public static string StepDevice => T("Sanal pil oluşturuluyor…", "Creating the virtual battery…");
        public static string StepWait => T("Windows'un pili görmesi bekleniyor…", "Waiting for Windows to see the battery…");
        public static string StepRemoveDevice => T("Sanal pil kaldırılıyor…", "Removing the virtual battery…");
        public static string Done => T("Tamam. Windows pili görüyor.", "Done. Windows sees the battery.");
        public static string Removed => T("Kaldırıldı. Sürücü ve sertifika silindi.", "Removed. The driver and its certificate were deleted.");
        public static string NeedsReboot => T("Kuruldu. Bilgisayarı yeniden başlatın.", "Installed. Please restart the PC.");
        public static string NoBatteryYet => T("Kuruldu, ancak Windows pili henüz göstermiyor. Bilgisayarı yeniden başlatın.", "Installed, but Windows does not show the battery yet. Please restart the PC.");
        public static string RollingBack => T("Hata oluştu, yapılanlar geri alınıyor…", "Something failed, rolling back…");
        public static string Subtitle => T("Bu bilgisayarı uygulamalara pilli (dizüstü) olarak gösterir.", "Makes this PC look battery powered (laptop) to applications.");
        public static string StatusNotInstalled => T("Kurulu değil", "Not installed");
        public static string StatusWaiting => T("Kurulu, Windows henüz pil göstermiyor", "Installed, Windows does not show a battery yet");
        public static string StatusActive(int percent) => Tr ? $"Etkin: Windows pili görüyor (%{percent})" : $"Active: Windows sees a battery ({percent}%)";
        public static string Install => T("Kur", "Install");
        public static string Reinstall => T("Onar / yeniden kur", "Repair / reinstall");
        public static string Uninstall => T("Kaldır", "Uninstall");
        public static string Level(int percent) => Tr ? $"Pil seviyesi: %{percent}" : $"Battery level: {percent}%";
        public static string Apply => T("Uygula", "Apply");
        public static string Applied => T("Ayar kaydedildi, birkaç saniye içinde geçerli olur.", "Saved, takes effect within a few seconds.");
        public static string ConfirmUninstall => T("Sanal pil, sürücüsü ve sertifikası kaldırılsın mı?", "Remove the virtual battery, its driver and its certificate?");
        public static string RealBattery => T("Bu bilgisayarda zaten bir pil var. Yine de sanal pil kurulsun mu?", "This PC already has a battery. Install the virtual battery anyway?");
        public static string Error => T("Hata: ", "Error: ");
    }
}
