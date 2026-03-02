using System;
using System.IO;
using System.Linq;

namespace SalsaNOW
{
    internal static class SalsaSettings
    {
        public static bool NvidiaRaytracing { get; private set; }
        public static bool SkipSeelenUiExecution { get; private set; }
        public static bool BingWallpaperEnabled { get; private set; }

        public static void Load(string globalDirectory)
        {
            string path = Path.Combine(globalDirectory, "SalsaNOWConfig.ini");
            if (!File.Exists(path)) return;

            var lines = File.ReadAllLines(path);

            // Simple parsing logic for the .ini flags
            NvidiaRaytracing = lines.Any(l => l.Contains("NvidiaRaytracing = \"1\""));
            SkipSeelenUiExecution = lines.Any(l => l.Contains("SkipSeelenUiExecution = \"0\""));
            BingWallpaperEnabled = lines.Any(l => l.Contains("BingPhotoOfTheDayWallpaper = \"1\""));
        }
    }
}