using System;

namespace SalsaNOW
{
    internal static class SalsaSettings
    {
        public static bool SkipSeelenUiExecution { get; private set; }
        public static bool BingWallpaperEnabled { get; private set; }
        public static bool SteamSilentLaunch { get; private set; }
        public static bool SteamInput { get; private set; }
        public static bool SalsaSteam { get; private set; }

        public static void Load()
        {
            bool steamSilent;
            bool bingWallpaper;
            bool steamInput;
            bool salsaSteam;
            if (!SalsaNOWSettingsApply.TryReadHostFlags(out steamSilent, out bingWallpaper, out steamInput, out salsaSteam))
                return;

            SteamSilentLaunch = steamSilent;
            BingWallpaperEnabled = bingWallpaper;
            SteamInput = steamInput;
            SalsaSteam = salsaSteam;

            // Always say which Steam this launch uses, so a log shows whether the toggle was read.
            if (salsaSteam)
                SalsaLogger.Info("64-bit Steam (SalsaSteam) enabled from SalsaNOWSettings.json.");
            else
                SalsaLogger.Info("64-bit Steam (SalsaSteam) is off in SalsaNOWSettings.json (salsaSteam missing or false). Using the original Steam.");

            if (steamSilent)
                SalsaLogger.Info("Steam silent launch enabled from SalsaNOWSettings.json.");
            if (steamInput)
                SalsaLogger.Info("Steam Input left enabled from SalsaNOWSettings.json.");
            if (bingWallpaper)
                SalsaLogger.Info("Bing wallpaper of the day enabled from SalsaNOWSettings.json.");
        }
    }
}
