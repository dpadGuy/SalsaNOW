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
            const string jsonUrl = "https://salsanowfiles.work/jsons/silentapps.json";
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
                            if (System.IO.File.Exists(appZipPath)) return;
                            string zip = $"{appFolder}.zip";
                            await webClient.DownloadFileTaskAsync(new Uri(app.url), zip);
                            ZipFile.ExtractToDirectory(zip, appFolder);
                            System.IO.File.Delete(zip);
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
            const string jsonUrl = "https://salsanowfiles.work/jsons/desktop.json";
            
            // Enforce Dark Mode for Windows Apps
            Process.Start(new ProcessStartInfo("cmd.exe", "/c reg add \"HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize\" /v AppsUseLightTheme /t REG_DWORD /d 0 /f") { UseShellExecute = true });

            try
            {
                List<DesktopInfo> desktopInfo;
                using (var wc = new WebClient())
                {
                    string json = await wc.DownloadStringTaskAsync(jsonUrl);
                    desktopInfo = JsonConvert.DeserializeObject<List<DesktopInfo>>(json);
                }

                bool skipSeelen = SalsaSettings.SkipSeelenUiExecution;
                bool bingWall = SalsaSettings.BingWallpaperEnabled;

                // Terminate original explorer shells to prepare for custom shell injection
                IntPtr hWndSeelen = NativeMethods.FindWindow(null, "CustomExplorer");
                if (hWndSeelen != IntPtr.Zero) NativeMethods.PostMessage(hWndSeelen, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

                foreach (var desktop in desktopInfo)
                {
                    string appDir = Path.Combine(globalDirectory, desktop.name);
                    string zipFile = Path.Combine(globalDirectory, $"{desktop.name}.zip");
                    string exePath = Path.Combine(appDir, desktop.exeName);

                    bool needsInstall = !Directory.Exists(appDir) || !File.Exists(exePath);

                    if (needsInstall)
                    {
                        // Clean up broken install if folder exists but exe is missing
                        if (Directory.Exists(appDir))
                        {
                            try
                            {
                                Directory.Delete(appDir, true);
                            }
                            catch
                            {
                                // Optional: retry or log
                            }
                        }

                        using (var wc = new WebClient())
                        {
                            wc.Headers.Add("Cache-Control", "no-cache");
                            wc.Headers.Add("Pragma", "no-cache");

                            await wc.DownloadFileTaskAsync(new Uri(desktop.url), zipFile);
                            ZipFile.ExtractToDirectory(zipFile, appDir);
                            File.Delete(zipFile);

                            // Run after fresh install
                            if (desktop.name.Contains("WinXShell"))
                            {
                                var psi = new ProcessStartInfo
                                {
                                    FileName = exePath,
                                    WorkingDirectory = appDir
                                };

                                Process.Start(psi);
                            }
                        }
                    }
                    else
                    {
                        // Existing valid install
                        if (desktop.name.Contains("WinXShell"))
                        {
                            if (bingWall)
                                await DownloadBingWallpaper(appDir);

                            var psi = new ProcessStartInfo
                            {
                                FileName = exePath,
                                WorkingDirectory = appDir
                            };

                            Process.Start(psi);
                        }
                    }
                }
            }
            catch (Exception ex) { SalsaLogger.Error(ex.ToString()); }
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
                }
            }
            catch { }
        }

        // Generates Windows shortcuts, deleting existing dead shortcuts first to ensure proper VM binding
        private static void CreateShortcut(string name, string path, string target, string workDir)
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
