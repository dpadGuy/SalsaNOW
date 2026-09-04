using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

namespace SalsaNOW
{
    internal static class DesktopInstaller
    {
        internal const uint SPI_SETDESKWALLPAPER = 0x0014;
        internal const uint SPIF_UPDATEINIFILE = 0x01;
        internal const uint SPIF_SENDCHANGE = 0x02;

        static readonly string[] SupportedExtensions =
        {
        ".bmp",
        ".jpg",
        ".jpeg",
        ".png",
        ".gif",
        ".tif",
        ".tiff",
        ".webp",
        ".jxr"
        };

        // Setup for Desktop shells and visual personalization
        public static async Task DesktopInstallAsync(string globalDirectory)
        {
            string defaultWallpaperDir = Path.Combine(globalDirectory, "DesktopWallpaper", "DefaultWallpaper");
            string userWallpaperDir = Path.Combine(globalDirectory, "DesktopWallpaper");
            const string jsonUrl = "https://salsanowfiles.work/jsons/ExplorerDesktop.json";

            // 1. Enforce Dark Mode
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    key?.SetValue("AppsUseLightTheme", 0, Microsoft.Win32.RegistryValueKind.DWord);
                }
            }
            catch (Exception ex) { SalsaLogger.Error("Failed to set Dark Mode: " + ex.Message); }

            // 2. Set default wallpaper from the Wallpapers user directory, if nothing is found then we apply the default wallpaper
            if (!Directory.Exists(userWallpaperDir))
            {
                Directory.CreateDirectory(userWallpaperDir);
                Directory.CreateDirectory(defaultWallpaperDir);

                using (var webClient = new WebClient())
                {
                    await webClient.DownloadFileTaskAsync(new Uri("https://salsanowfiles.work/ExplorerContents/Wallpaper/WallpaperWin11.jpg"), $"{defaultWallpaperDir}\\WallpaperWin11.jpg");
                }
            }

            string wallpaper = Directory
                .EnumerateFiles(userWallpaperDir)
                .FirstOrDefault(f =>
                    SupportedExtensions.Contains(
                        Path.GetExtension(f),
                        StringComparer.OrdinalIgnoreCase));

            if (wallpaper == null)
            {
                // Apply default wallpaper if no user-defined wallpaper is found
                bool success = NativeMethods.SystemParametersInfo(
                    SPI_SETDESKWALLPAPER,
                    0,
                    $"{defaultWallpaperDir}\\WallpaperWin11.jpg",
                    SPIF_UPDATEINIFILE | SPIF_SENDCHANGE
                );
            }
            else
            {
                // Apply user-defined wallpaper if found
                bool success = NativeMethods.SystemParametersInfo(
                    SPI_SETDESKWALLPAPER,
                    0,
                    wallpaper,
                    SPIF_UPDATEINIFILE | SPIF_SENDCHANGE
                );
            }

            // 3. Fetch and install desktop from remote JSON
            try
            {
                List<DesktopInfo> desktopInfo;
                using (var wc = new WebClient())
                {
                    string json = await wc.DownloadStringTaskAsync(jsonUrl);
                    desktopInfo = JsonConvert.DeserializeObject<List<DesktopInfo>>(json);
                }

                // Close existing shells before attempting updates
                var processes = Process.GetProcessesByName("CustomExplorer");
                foreach (var p in processes) p.Kill();

                foreach (var desktop in desktopInfo)
                {
                    string appDir = Path.Combine(globalDirectory, desktop.name);
                    string versionMarkerFile = Path.Combine(appDir, ".version");
                    string remoteFileName = Path.GetFileName(new Uri(desktop.url).AbsolutePath);

                    bool needsInstall = !Directory.Exists(appDir) || !File.Exists(versionMarkerFile);

                    // Check if current version marker matches the remote filename
                    if (!needsInstall && File.Exists(versionMarkerFile))
                    {
                        string localVersion = File.ReadAllText(versionMarkerFile);
                        if (localVersion != remoteFileName)
                            needsInstall = true; // Version mismatch, trigger re-install
                    }

                    // Perform installation/update
                    if (needsInstall)
                    {
                        SafeDeleteDirectory(appDir);
                        Directory.CreateDirectory(appDir);

                        string zipFile = Path.Combine(globalDirectory, $"{desktop.name}_temp.zip");
                        using (var wc = new WebClient())
                        {
                            wc.Headers.Add("Cache-Control", "no-cache");
                            await wc.DownloadFileTaskAsync(new Uri(desktop.url), zipFile);
                        }

                        ZipFile.ExtractToDirectory(zipFile, appDir);
                        if (File.Exists(zipFile)) File.Delete(zipFile);

                        // Save the new version marker
                        File.WriteAllText(versionMarkerFile, remoteFileName);
                    }

                    // Universal Launch Logic
                    if (string.Equals(desktop.run, "true", StringComparison.OrdinalIgnoreCase))
                    {
                        string exePath = Path.Combine(appDir, desktop.exeName);

                        SalsaLogger.Info("Starting desktop app: " + exePath);

                        Process.Start(new ProcessStartInfo
                        {
                            FileName = exePath,
                            WorkingDirectory = appDir,
                            UseShellExecute = false
                        });
                    }
                }

                if (SalsaSettings.BingWallpaperEnabled)
                {
                    await DownloadBingWallpaper(userWallpaperDir);
                }
            }
            catch (Exception ex) { SalsaLogger.Error(ex.ToString()); }
        }

        private static void SafeDeleteDirectory(string path, int retries = 3)
        {
            if (!Directory.Exists(path)) return;

            for (int i = 0; i < retries; i++)
            {
                try
                {
                    Directory.Delete(path, true);
                    return;
                }
                catch
                {
                    System.Threading.Thread.Sleep(1000);
                }
            }
        }

        // Fetches and applies the UHD Bing Photo of the Day
        private static async Task DownloadBingWallpaper(string dir)
        {
            try
            {
                using (var wc = new WebClient())
                {
                    string json = await wc.DownloadStringTaskAsync("https://www.bing.com/HPImageArchive.aspx?format=js&idx=0&n=1&mkt=en-AU");
                    var url = JObject.Parse(json)["images"][0]["urlbase"].ToString();
                    await wc.DownloadFileTaskAsync(new Uri($"https://www.bing.com{url}_UHD.jpg"), Path.Combine(dir, "wallpaper.jpg"));

                    // Apply bing wallpaper as the desktop background at users request from config file
                    bool success = NativeMethods.SystemParametersInfo(
                        SPI_SETDESKWALLPAPER,
                        0,
                        Path.Combine(dir, "wallpaper.jpg"),
                        SPIF_UPDATEINIFILE | SPIF_SENDCHANGE
                    );
                }
            }
            catch { }
        }
    }
}
