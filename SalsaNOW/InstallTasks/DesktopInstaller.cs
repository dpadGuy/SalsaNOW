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

            DisableBackgroundOverrides();

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

        internal static bool IsDesktopAvailable()
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

            // SPI often returns success before Explorer is the shell and still leaves a blank desktop.
            // SetWallpaper is what actually paints the picture, including after Zxplorer replaces Explorer.
            bool applied = NativeMethods.SystemParametersInfo(
                SPI_SETDESKWALLPAPER,
                0,
                path,
                SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);

            if (SetShellWallpaper(path))
                applied = true;

            if (applied)
            {
                SalsaLogger.Info("Wallpaper applied: " + path);
                RedrawDesktop();
            }
            else
                SalsaLogger.Error("Wallpaper was not applied: " + path);
        }

        // After a wallpaper change Explorer only repaints the parts of the desktop that windows uncovered, so the
        // old and new wallpaper can show side by side in patches. Force the whole desktop to repaint.
        private static void RedrawDesktop()
        {
            const uint flags = RDW_INVALIDATE | RDW_ERASE | RDW_ALLCHILDREN | RDW_UPDATENOW | RDW_FRAME;
            try
            {
                IntPtr progman = NativeMethods.FindWindow("Progman", null);
                if (progman != IntPtr.Zero)
                    RedrawWindow(progman, IntPtr.Zero, IntPtr.Zero, flags);

                // The wallpaper can also be drawn by WorkerW windows behind the desktop icons.
                IntPtr worker = IntPtr.Zero;
                while ((worker = FindWindowEx(IntPtr.Zero, worker, "WorkerW", null)) != IntPtr.Zero)
                    RedrawWindow(worker, IntPtr.Zero, IntPtr.Zero, flags);

                // Whole screen, for anything left over.
                RedrawWindow(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, flags);
            }
            catch (Exception ex)
            {
                SalsaLogger.Warn("Desktop redraw failed: " + ex.Message);
            }
        }

        private const uint RDW_INVALIDATE = 0x0001;
        private const uint RDW_ERASE = 0x0004;
        private const uint RDW_ALLCHILDREN = 0x0080;
        private const uint RDW_UPDATENOW = 0x0100;
        private const uint RDW_FRAME = 0x0400;

        [DllImport("user32.dll")]
        private static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string windowName);

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

        // Windows copies a set picture to TranscodedWallpaper and GetWallpaper returns that path,
        // not the jpg we passed in. Comparing the two always fails and the watcher would reapply forever.
        private static bool WallpaperStillOurs(string path)
        {
            if (!LooksLikeOurWallpaper(GetRegistryWallpaper(), path))
                return false;

            string shell = TryGetShellWallpaper();
            if (shell == null)
                return true;

            return LooksLikeOurWallpaper(shell, path);
        }

        private static bool LooksLikeOurWallpaper(string current, string path)
        {
            if (string.IsNullOrEmpty(current))
                return false;

            if (string.Equals(current, path, StringComparison.OrdinalIgnoreCase))
                return true;

            string name = Path.GetFileName(current);
            return name.Equals("TranscodedWallpaper", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Transcoded_", StringComparison.OrdinalIgnoreCase);
        }

        private static string TryGetShellWallpaper()
        {
            try
            {
                var desktop = (IDesktopWallpaper)new DesktopWallpaper();
                string wallpaper;
                if (desktop.GetWallpaper(null, out wallpaper) != 0)
                    return null;
                return wallpaper ?? "";
            }
            catch
            {
                return null;
            }
        }

        private static string GetRegistryWallpaper()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop"))
                    return key?.GetValue("Wallpaper") as string;
            }
            catch
            {
                return null;
            }
        }

        // Windows Spotlight and slideshow replace the picture on their own schedule, so force a plain picture background.
        private static void DisableBackgroundOverrides()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Wallpapers"))
                {
                    object current = key?.GetValue("BackgroundType");
                    if (!(current is int type) || type != 0)
                        key?.SetValue("BackgroundType", 0, Microsoft.Win32.RegistryValueKind.DWord);
                }
            }
            catch (Exception ex)
            {
                SalsaLogger.Warn("Failed to set background type: " + ex.Message);
            }

            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Policies\Microsoft\Windows\CloudContent"))
                {
                    key?.SetValue("DisableSpotlightCollectionOnDesktop", 1, Microsoft.Win32.RegistryValueKind.DWord);
                    key?.SetValue("DisableWindowsSpotlightFeatures", 1, Microsoft.Win32.RegistryValueKind.DWord);
                }
            }
            catch (Exception ex)
            {
                SalsaLogger.Warn("Failed to disable Windows Spotlight: " + ex.Message);
            }
        }

        // Explorer can start late or be restarted, and Windows (theme sync, Spotlight, session setup)
        // can swap the wallpaper seconds after it was applied. Keep checking and put ours back whenever
        // the shell or the registry shows anything else.
        private static async Task KeepWallpaperAppliedAsync(string userWallpaperDir, string fallbackWallpaper)
        {
            uint appliedShellId = 0;
            string appliedPath = null;
            DateTime appliedWriteTime = DateTime.MinValue;
            int failedAttempts = 0;

            while (true)
            {
                await Task.Delay(failedAttempts > 5 ? 10000 : 2000);

                try
                {
                    uint shellId = GetDesktopShellId();
                    if (shellId == 0)
                        continue;

                    string path = FindUserWallpaper(userWallpaperDir) ?? fallbackWallpaper;
                    if (path == null || !File.Exists(path))
                        continue;

                    DateTime writeTime = File.GetLastWriteTimeUtc(path);

                    string reason = null;
                    if (shellId != appliedShellId)
                        reason = "desktop shell started";
                    else if (!string.Equals(path, appliedPath, StringComparison.OrdinalIgnoreCase) || writeTime != appliedWriteTime)
                        reason = "wallpaper file changed";
                    else if (!WallpaperStillOurs(path))
                        reason = "wallpaper was replaced";

                    if (reason == null)
                    {
                        failedAttempts = 0;
                        continue;
                    }

                    if (shellId != appliedShellId)
                        await Task.Delay(1000);

                    SalsaLogger.Info("Reapplying wallpaper (" + reason + "): " + path);
                    DisableBackgroundOverrides();
                    ApplyWallpaper(path);

                    await Task.Delay(1000);
                    if (!WallpaperStillOurs(path))
                        SetShellWallpaper(path);

                    if (WallpaperStillOurs(path))
                    {
                        failedAttempts = 0;
                        SalsaLogger.Info("Wallpaper confirmed by the shell: " + path);
                        RedrawDesktop();
                    }
                    else
                    {
                        failedAttempts++;
                        SalsaLogger.Warn("Wallpaper not confirmed yet (attempt " + failedAttempts + "): " + path);
                    }

                    appliedShellId = shellId;
                    appliedPath = path;
                    appliedWriteTime = writeTime;
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

            [PreserveSig]
            int GetWallpaper(
                [MarshalAs(UnmanagedType.LPWStr)] string monitorID,
                [MarshalAs(UnmanagedType.LPWStr)] out string wallpaper);
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
