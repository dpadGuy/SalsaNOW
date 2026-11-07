using Microsoft.Win32;
using System;
using System.Threading.Tasks;

namespace SalsaNOW
{
    // Points steam:// and HKCU\Software\Valve\Steam back at the original Steam when they still name SalsaSteam.
    // Games in I:\Apps\SalsaNOW\SalsaSteam\steamapps are left where they are.
    internal static class SalsaSteamRevert
    {
        private const string SystemSteamDir = @"C:\Program Files (x86)\Steam";

        public static Task RunAsync()
        {
            RestoreSteamRegistry();
            return Task.CompletedTask;
        }

        // SalsaSteam pointed steam:// and the Valve\Steam keys at itself under HKCU. Remove that so Windows and
        // games find the original Steam again. Only entries that point at SalsaSteam are touched.
        private static void RestoreSteamRegistry()
        {
            try
            {
                string command = null;
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\steam\Shell\Open\Command"))
                    command = key?.GetValue("") as string;

                if (command != null && command.IndexOf("SalsaSteam", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\steam", false);
                    SalsaLogger.Steam("steam:// protocol restored to the original Steam.");
                }
            }
            catch (Exception ex) { SalsaLogger.Error($"steam:// protocol restore error: {ex.Message}"); }

            try
            {
                using (RegistryKey valve = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam", true))
                {
                    string steamExe = valve?.GetValue("SteamExe") as string;
                    if (steamExe != null && steamExe.IndexOf("SalsaSteam", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        valve.SetValue("SteamExe", (SystemSteamDir + @"\steam.exe").Replace('\\', '/').ToLowerInvariant());
                        valve.SetValue("SteamPath", SystemSteamDir.Replace('\\', '/').ToLowerInvariant());
                        SalsaLogger.Steam("Valve\\Steam SteamExe/SteamPath restored to the original Steam.");
                    }
                }
            }
            catch (Exception ex) { SalsaLogger.Error($"Valve\\Steam registry restore error: {ex.Message}"); }
        }
    }
}
