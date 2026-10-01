using Microsoft.Win32;
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace SalsaNOW
{
    internal static class HiddenDriveHelper
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool DefineDosDevice(uint dwFlags, string lpDeviceName, string lpTargetPath);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern uint QueryDosDevice(string lpDeviceName, StringBuilder lpTargetPath, int ucchMax);

        private const uint DDD_RAW_TARGET_PATH = 0x00000001;

        public static string EnsureHiddenAlias(string targetPath)
        {
            targetPath = targetPath.TrimEnd('\\');

            for (char c = 'Z'; c >= 'G'; c--)
            {
                string existing = TryResolve(c);
                if (existing != null && existing.TrimEnd('\\').Equals(@"\??\" + targetPath, StringComparison.OrdinalIgnoreCase))
                {
                    HideDriveLetter(c);
                    return c + ":";
                }
            }

            foreach (char c in new char[] { 'Y', 'X', 'W', 'V', 'U', 'T', 'S', 'R', 'Q', 'P', 'O', 'N', 'M', 'L', 'K', 'J', 'H', 'G' })
            {
                if (TryResolve(c) != null) continue;

                string deviceName = c + ":";
                bool ok = DefineDosDevice(DDD_RAW_TARGET_PATH, deviceName, @"\??\" + targetPath);
                if (!ok) continue;

                HideDriveLetter(c);
                SalsaLogger.Info($"CloudSave: hidden alias {deviceName} -> {targetPath}");
                return deviceName;
            }

            SalsaLogger.Error("CloudSave: could not allocate a drive letter for hidden alias.");
            return null;
        }

        private static string TryResolve(char letter)
        {
            var sb = new StringBuilder(1024);
            uint len = QueryDosDevice(letter + ":", sb, sb.Capacity);
            return len == 0 ? null : sb.ToString();
        }

        private static void HideDriveLetter(char letter)
        {
            try
            {
                int bit = 1 << (letter - 'A');
                using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer"))
                {
                    int current = (int)(key.GetValue("NoDrives", 0));
                    key.SetValue("NoDrives", current | bit, RegistryValueKind.DWord);
                }
            }
            catch (Exception ex)
            {
                SalsaLogger.Error("CloudSave: failed to hide drive letter: " + ex.Message);
            }
        }
    }
}
