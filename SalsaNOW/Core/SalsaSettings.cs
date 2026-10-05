using System;

namespace SalsaNOW
{
    internal static class SalsaSettings
    {
        public static bool SkipSeelenUiExecution { get; private set; }
        public static bool BingWallpaperEnabled { get; private set; }
        public static bool SteamSilentLaunch { get; private set; }
        public static bool SteamInput { get; private set; }

        public static void Load()
        {
            bool steamSilent;
            bool bingWallpaper;
            bool steamInput;
            if (!SalsaNOWSettingsApply.TryReadHostFlags(out steamSilent, out bingWallpaper, out steamInput))
                return;

            SteamSilentLaunch = steamSilent;
            BingWallpaperEnabled = bingWallpaper;
            SteamInput = steamInput;

            if (steamSilent)
                SalsaLogger.Info("Steam silent launch enabled from SalsaNOWSettings.json.");
            if (steamInput)
                SalsaLogger.Info("Steam Input left enabled from SalsaNOWSettings.json.");
            if (bingWallpaper)
                SalsaLogger.Info("Bing wallpaper of the day enabled from SalsaNOWSettings.json.");
        }
    }
}
