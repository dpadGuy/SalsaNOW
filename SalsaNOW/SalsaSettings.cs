using System;
using System.IO;
using System.Linq;

namespace SalsaNOW
{
    internal static class SalsaSettings
    {
        public static bool SkipSeelenUiExecution { get; private set; }
        public static bool BingWallpaperEnabled { get; private set; }
        public static bool SteamSilentLaunch { get; private set; }
        public static void Load(string globalDirectory)
        {
            string path = Path.Combine(globalDirectory, "SalsaNOWConfig.ini");
            if (!File.Exists(path)) return;

            var lines = File.ReadAllLines(path).ToList();

            bool changed = false;

            void EnsureLine(string key)
            {
                bool exists = lines.Any(l => l.TrimStart().StartsWith(key + " ="));

                if (!exists)
                {
                    lines.Add($"{key} = \"0\"");
                    changed = true;
                }
            }

            // Ensure settings exist (default = 0)
            EnsureLine("BingPhotoOfTheDayWallpaper");
            EnsureLine("SteamSilentLaunch");

            if (changed)
            {
                File.WriteAllLines(path, lines);
            }

            // Now parse values (your original logic, unchanged style)
            SkipSeelenUiExecution = lines.Any(l => l.Contains("SkipSeelenUiExecution = \"0\""));
            BingWallpaperEnabled = lines.Any(l => l.Contains("BingPhotoOfTheDayWallpaper = \"1\""));
            SteamSilentLaunch = lines.Any(l => l.Contains("SteamSilentLaunch = \"1\""));
        }
    }
}