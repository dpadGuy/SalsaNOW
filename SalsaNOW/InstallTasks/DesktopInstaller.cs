using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
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
            const string jsonUrl = "https://salsanowfiles.work/dev/jsons/ExplorerDesktop.json";

            // 1. Enforce Dark Mode
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    key?.SetValue("AppsUseLightTheme", 0, Microsoft.Win32.RegistryValueKind.DWord);
                    key?.SetValue("SystemUsesLightTheme", 0, Microsoft.Win32.RegistryValueKind.DWord);
                }
            }
            catch (Exception ex) { SalsaLogger.Error("Failed to set Dark Mode: " + ex.Message); }

            // 2. Use the user's wallpaper when one is set. Otherwise always download and apply the default.
            Directory.CreateDirectory(userWallpaperDir);
            Directory.CreateDirectory(defaultWallpaperDir);

            string wallpaper = FindUserWallpaper(userWallpaperDir);

            if (wallpaper == null)
            {
                string defaultWallpaper = Path.Combine(defaultWallpaperDir, "WallpaperWin11.jpg");
                try
                {
                    using (var webClient = new WebClient())
                    {
                        await DownloadWallpaperFile(
                            webClient,
                            new Uri("https://salsanowfiles.work/ExplorerContents/Wallpaper/WallpaperWin11.jpg"),
                            defaultWallpaper);
                    }

                    SalsaLogger.Info("No user wallpaper set. Downloaded default wallpaper.");
                }
                catch (Exception ex)
                {
                    SalsaLogger.Error("Failed to download default wallpaper: " + ex.Message);
                }

                wallpaper = File.Exists(defaultWallpaper) ? defaultWallpaper : null;
            }

            if (wallpaper != null)
                ApplyWallpaper(wallpaper);

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
                        FinalBackgroundTasks.EnableClassicContextMenu();
                        StartDesktopProcess(exePath, appDir);
                        _ = EnsureDesktopAvailableAsync(exePath, appDir);
                    }
                }

                if (SalsaSettings.BingWallpaperEnabled)
                {
                    string bingWallpaper = await DownloadBingWallpaper(userWallpaperDir);
                    if (bingWallpaper != null)
                        wallpaper = bingWallpaper;
                }
            }
            catch (Exception ex) { SalsaLogger.Error(ex.ToString()); }

            if (wallpaper != null)
                _ = KeepWallpaperAppliedAsync(userWallpaperDir, wallpaper);
        }

        private static void StartDesktopProcess(string exePath, string workingDirectory)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false
            });
        }

        private static async Task EnsureDesktopAvailableAsync(string exePath, string workingDirectory)
        {
            bool announced = false;
            while (true)
            {
                await Task.Delay(5000);

                try
                {
                    if (IsDesktopAvailable())
                    {
                        if (!announced)
                        {
                            SalsaLogger.Info("Desktop is available.");
                            announced = true;
                        }

                        continue;
                    }

                    announced = false;
                    SalsaLogger.Warn("Desktop is not available. Starting " + exePath);
                    StartDesktopProcess(exePath, workingDirectory);
                }
                catch (Exception ex)
                {
                    SalsaLogger.Error("Desktop start failed: " + ex.Message);
                }
            }
        }

        private static bool IsDesktopAvailable()
        {
            var explorerIds = new HashSet<uint>();
            foreach (Process process in Process.GetProcessesByName("explorer"))
            {
                try
                {
                    if (!process.HasExited)
                        explorerIds.Add((uint)process.Id);
                }
                catch
                {
                }
                finally
                {
                    process.Dispose();
                }
            }

            if (explorerIds.Count == 0)
                return false;

            return IsExplorerWindow("Progman", explorerIds) && IsExplorerWindow("Shell_TrayWnd", explorerIds);
        }

        private static bool IsExplorerWindow(string className, HashSet<uint> explorerIds)
        {
            IntPtr window = NativeMethods.FindWindow(className, null);
            if (window == IntPtr.Zero || !IsWindowVisible(window))
                return false;

            uint processId;
            NativeMethods.GetWindowThreadProcessId(window, out processId);
            return explorerIds.Contains(processId);
        }

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        // Newest first, so a wallpaper chosen later wins over an older one left in the folder.
        private static string FindUserWallpaper(string dir)
        {
            return new DirectoryInfo(dir)
                .EnumerateFiles()
                .Where(f =>
                    f.Length > 0
                    && SupportedExtensions.Contains(f.Extension, StringComparer.OrdinalIgnoreCase))
                .OrderByDescending(f => f.CreationTimeUtc > f.LastWriteTimeUtc ? f.CreationTimeUtc : f.LastWriteTimeUtc)
                .Select(f => f.FullName)
                .FirstOrDefault();
        }

        private static void ApplyWallpaper(string path)
        {
            if (!File.Exists(path))
            {
                SalsaLogger.Error("Wallpaper file is missing: " + path);
                return;
            }

            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", true))
                {
                    key?.SetValue("Wallpaper", path);
                    key?.SetValue("WallpaperStyle", "10");
                    key?.SetValue("TileWallpaper", "0");
                }
            }
            catch (Exception ex)
            {
                SalsaLogger.Error("Failed to set wallpaper registry: " + ex.Message);
            }

            bool applied = NativeMethods.SystemParametersInfo(
                SPI_SETDESKWALLPAPER,
                0,
                path,
                SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);

            if (!applied)
                applied = SetShellWallpaper(path);

            if (applied)
                SalsaLogger.Info("Wallpaper applied: " + path);
            else
                SalsaLogger.Error("Wallpaper was not applied: " + path);
        }

        private static bool SetShellWallpaper(string path)
        {
            try
            {
                var desktop = (IDesktopWallpaper)new DesktopWallpaper();
                desktop.SetWallpaper(null, path);
                return true;
            }
            catch (Exception ex)
            {
                SalsaLogger.Warn("Shell wallpaper call failed: " + ex.Message);
                return false;
            }
        }

        private static bool IsShellWallpaperSet(string path)
        {
            try
            {
                var desktop = (IDesktopWallpaper)new DesktopWallpaper();
                return string.Equals(desktop.GetWallpaper(null), path, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        // SystemParametersInfo reports success even with no shell running, and Explorer can start
        // late or be restarted, so apply once per Explorer instance and confirm the shell took it.
        private static async Task KeepWallpaperAppliedAsync(string userWallpaperDir, string fallbackWallpaper)
        {
            uint appliedShellId = 0;
            while (true)
            {
                await Task.Delay(1000);

                try
                {
                    uint shellId = GetDesktopShellId();
                    if (shellId == 0 || shellId == appliedShellId)
                        continue;

                    await Task.Delay(1000);

                    string path = FindUserWallpaper(userWallpaperDir) ?? fallbackWallpaper;
                    ApplyWallpaper(path);

                    bool confirmed = false;
                    for (int attempt = 0; attempt < 5 && !confirmed; attempt++)
                    {
                        await Task.Delay(2000);

                        confirmed = IsShellWallpaperSet(path);
                        if (!confirmed)
                            SetShellWallpaper(path);
                    }

                    if (confirmed)
                        SalsaLogger.Info("Wallpaper confirmed by the shell: " + path);
                    else
                        SalsaLogger.Error("Wallpaper could not be confirmed: " + path);

                    appliedShellId = shellId;
                }
                catch (Exception ex)
                {
                    SalsaLogger.Error("Wallpaper watcher failed: " + ex.Message);
                }
            }
        }

        private static uint GetDesktopShellId()
        {
            if (!IsDesktopAvailable())
                return 0;

            uint processId;
            NativeMethods.GetWindowThreadProcessId(NativeMethods.FindWindow("Progman", null), out processId);
            return processId;
        }

        [ComImport, Guid("C2CF3110-460E-4fc1-B9D0-8A1C0C9CC4BD")]
        private class DesktopWallpaper
        {
        }

        [ComImport, Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDesktopWallpaper
        {
            void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitorID, [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);

            [return: MarshalAs(UnmanagedType.LPWStr)]
            string GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitorID);
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

        // Fetches the UHD Bing Photo of the Day. The caller applies it after the desktop is ready.
        private static async Task<string> DownloadBingWallpaper(string dir)
        {
            try
            {
                using (var wc = new WebClient())
                {
                    string market = CultureInfo.CurrentUICulture.Name;
                    if (string.IsNullOrWhiteSpace(market))
                        market = "en-US";
                    string json = await wc.DownloadStringTaskAsync("https://www.bing.com/HPImageArchive.aspx?format=js&idx=0&n=1&mkt=" + market);
                    var url = JObject.Parse(json)["images"][0]["urlbase"].ToString();
                    string path = Path.Combine(dir, "wallpaper.jpg");
                    await DownloadWallpaperFile(wc, new Uri("https://www.bing.com" + url + "_UHD.jpg"), path);
                    return path;
                }
            }
            catch
            {
                return null;
            }
        }

        // Downloads to a temp file first so a failed download can't replace a good wallpaper.
        private static async Task DownloadWallpaperFile(WebClient wc, Uri uri, string path)
        {
            string tempPath = path + ".tmp";
            try
            {
                await wc.DownloadFileTaskAsync(uri, tempPath);
                File.Copy(tempPath, path, true);
            }
            finally
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
        }
    }
}
