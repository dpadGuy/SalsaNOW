using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SalsaNOW
{
    internal static class AppInstaller
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

        // Parallel installation of user-defined apps from remote and local JSON sources
        public static async Task AppsInstallAsync(string globalDirectory, string customAppsJsonPath)
        {
            const string jsonUrl = "https://salsanowfiles.work/jsons/apps.json";
            try
            {
                List<Apps> apps;
                using (var wc = new WebClient())
                {
                    string json = await wc.DownloadStringTaskAsync(jsonUrl);
                    apps = JsonConvert.DeserializeObject<List<Apps>>(json);
                }

                // Load custom apps from local JSON if provided via arguments
                if (!string.IsNullOrEmpty(customAppsJsonPath) && System.IO.File.Exists(customAppsJsonPath))
                {
                    try
                    {
                        var customApps = JsonConvert.DeserializeObject<List<Apps>>(System.IO.File.ReadAllText(customAppsJsonPath));
                        if (customApps != null) apps.AddRange(customApps);
                    }
                    catch (Exception ex) { SalsaLogger.Error($"Custom JSON Error: {ex.Message}"); }
                }

                var tasks = apps.Select(app => Task.Run(async () =>
                {
                    using (var webClient = new WebClient())
                    {
                        webClient.Headers.Add("Cache-Control", "no-cache");
                        webClient.Headers.Add("Pragma", "no-cache");

                        string desktopPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), $"{app.name}.lnk");
                        string appDir = Path.Combine(globalDirectory, app.name);
                        string appExePath = Path.Combine(globalDirectory, app.exeName);
                        string appZipExe = Path.Combine(appDir, app.exeName);

                        bool isZip = app.fileExtension == "zip";
                        bool isExe = app.fileExtension == "exe";
                        
                        bool alreadyExists = (isZip && Directory.Exists(appDir)) || (isExe && System.IO.File.Exists(appExePath));

                        // Initial installation for missing applications
                        if (!alreadyExists)
                        {
                            SalsaLogger.Info("Installing " + app.name);
                            if (isZip)
                            {
                                string zipPath = $"{appDir}.zip";
                                await webClient.DownloadFileTaskAsync(new Uri(app.url), zipPath);
                                ZipFile.ExtractToDirectory(zipPath, appDir);
                                System.IO.File.Delete(zipPath);

                                CreateShortcut(app.name, desktopPath, appZipExe, Path.GetDirectoryName(appZipExe));
                                if (app.run == "true") Process.Start(appZipExe);
                            }
                            else if (isExe)
                            {
                                await webClient.DownloadFileTaskAsync(new Uri(app.url), appExePath);
                                CreateShortcut(app.name, desktopPath, appExePath, globalDirectory);
                                if (app.run == "true") Process.Start(appExePath);
                            }
                        }
                        else
                        {
                            SalsaLogger.Info($"{app.name} already exists. Skipping download and respecting user desktop layout.");
                            
                          
                            if (isZip)
                            {
                                if (app.run == "true") Process.Start(appZipExe);
                            }
                            else if (isExe) // We install exe anyway to ensure everything is up to date
                            {
                                await webClient.DownloadFileTaskAsync(new Uri(app.url), appExePath);
                                if (app.run == "true") Process.Start(appExePath);
                            }
                        }
                    }
                })).ToList();

                await Task.WhenAll(tasks);
            }
            catch (Exception ex) { SalsaLogger.Error(ex.Message); }
        }

        // Silent background app deployment with automated cleanup of obsolete files/folders
        public static async Task AppsInstallSilentAsync(string globalDirectory)
        {
            // New url for silent apps due to the change to the new explorer desktop, older versions use the old url
            const string jsonUrl = "https://salsanowfiles.work/ExplorerContents/jsons/silentapps.json";
            string silentAppsPath = Path.Combine(globalDirectory, "SilentApps");

            try
            {
                Directory.CreateDirectory(silentAppsPath);
                List<SilentApps> apps;
                using (var wc = new WebClient())
                {
                    string json = await wc.DownloadStringTaskAsync(jsonUrl);
                    apps = JsonConvert.DeserializeObject<List<SilentApps>>(json);
                }

                // Clean up folders and files that are no longer present in the JSON definition
                var allowedFolders = new HashSet<string>(apps.Where(a => a.archive == "true").Select(a => a.name), StringComparer.OrdinalIgnoreCase);
                var allowedFiles = new HashSet<string>(apps.Where(a => a.fileExtension == "exe" || a.fileExtension == "bat").Select(a => $"{a.fileName}.{a.fileExtension}"), StringComparer.OrdinalIgnoreCase);

                foreach (var dir in Directory.GetDirectories(silentAppsPath))
                {
                    if (!allowedFolders.Contains(Path.GetFileName(dir))) try { Directory.Delete(dir, true); } catch { }
                }
                foreach (var file in Directory.GetFiles(silentAppsPath))
                {
                    if (!allowedFiles.Contains(Path.GetFileName(file))) try { System.IO.File.Delete(file); } catch { }
                }

                var tasks = apps.Select(app => Task.Run(async () =>
                {
                    using (var webClient = new WebClient())
                    {
                        webClient.Headers.Add("Cache-Control", "no-cache");
                        webClient.Headers.Add("Pragma", "no-cache");

                        string appFolder = Path.Combine(silentAppsPath, app.name);
                        string appPath = Path.Combine(silentAppsPath, $"{app.fileName}.{app.fileExtension}");
                        string appZipPath = Path.Combine(appFolder, $"{app.fileName}.{app.fileExtension}");

                        if (app.archive == "true")
                        {
                            // 1. FORCE REINSTALL OVERRIDE: 
                            // If it's Open-Shell, we delete the directory immediately so it's always "fresh"
                            if (app.name.Equals("Open-Shell", StringComparison.OrdinalIgnoreCase))
                            {
                                SafeDeleteDirectory(appFolder);
                            }

                            // 2. STANDARD GUARD:
                            // If it's not the special app, only then do we check if it already exists
                            else if (System.IO.File.Exists(appZipPath)) return;

                            // 3. EXECUTION
                            string zip = $"{appFolder}.zip";

                            // Create directory if it was deleted
                            if (!Directory.Exists(appFolder)) Directory.CreateDirectory(appFolder);

                            await webClient.DownloadFileTaskAsync(new Uri(app.url), zip);
                            ZipFile.ExtractToDirectory(zip, appFolder);
                            System.IO.File.Delete(zip);

                            // Only run if specifically told to
                            if (app.run == "true") Process.Start(appZipPath);
                        }
                        else
                        {
                            await webClient.DownloadFileTaskAsync(new Uri(app.url), appPath);

                            if (app.run == "true")
                            {
                                Process.Start(new ProcessStartInfo
                                {
                                    FileName = appPath,
                                    UseShellExecute = false,
                                    CreateNoWindow = true,
                                    WindowStyle = ProcessWindowStyle.Hidden
                                });
                            }
                        }
                    }
                })).ToList();

                await Task.WhenAll(tasks);
            }
            catch (Exception ex) { SalsaLogger.Error(ex.ToString()); }
        }

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

                    // Logic for specific apps (e.g., WinXShell)
                    if (SalsaSettings.BingWallpaperEnabled)
                    {
                        await DownloadBingWallpaper(userWallpaperDir);
                    }

                    // Universal Launch Logic
                    if (string.Equals(desktop.run, "true", StringComparison.OrdinalIgnoreCase))
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = Path.Combine(appDir, desktop.exeName),
                            WorkingDirectory = appDir,
                            UseShellExecute = true
                        });
                    }
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

        // Generates Windows shortcuts, deleting existing dead shortcuts first to ensure proper VM binding
        public static void CreateShortcut(string name, string path, string target, string workDir)
        {
            // Attempt to remove dead/corrupt shortcut to enforce generation of a new Volume GUID
            for (int i = 0; i < 5; i++)
            {
                try 
                { 
                    if (System.IO.File.Exists(path)) System.IO.File.Delete(path); 
                    break; 
                } 
                catch { Thread.Sleep(200); }
            }

            try 
            {
                // Instantiate WScript.Shell without Interop dependencies to prevent COM thread crashes
                Type tWsh = Type.GetTypeFromProgID("WScript.Shell");
                dynamic shell = Activator.CreateInstance(tWsh);
                var lnk = shell.CreateShortcut(path);
                lnk.TargetPath = target;
                lnk.WorkingDirectory = workDir;
                lnk.Save();
            }
            catch (Exception ex) { SalsaLogger.Error($"Shortcut creation failed for {name}: {ex.Message}"); }
        }
    }
}
