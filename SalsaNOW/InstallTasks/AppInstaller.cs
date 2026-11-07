using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SalsaNOW
{
    internal static class AppInstaller
    {
        // Parallel installation of user-defined apps from remote and local JSON sources
        public static async Task AppsInstallAsync(string globalDirectory, string customAppsJsonPath)
        {
            const string jsonUrl = "https://salsanowfiles.work/jsons/appsV2.json";
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

                // SalsaSteamStartup.exe is downloaded and started by SalsaSteamLauncher (only with 64-bit Steam on);
                // here it only hands over the entry's url and version.
                Apps salsaSteamEntry = apps.FirstOrDefault(SalsaSteamLauncher.IsSalsaSteamStartup);
                SalsaSteamLauncher.SetAppEntry(salsaSteamEntry);
                apps = apps.Where(app => !SalsaSteamLauncher.IsSalsaSteamStartup(app)).ToList();

                var tasks = apps.Select(app => Task.Run(async () =>
                {
                    // Each app gets its own error so one failed download names itself and doesn't hide the others.
                    try
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

                        if (isExe)
                        {
                            SalsaLogger.Info("Downloading " + app.name);
                            await DownloadAndSwapAsync(webClient, app.name, app.url, appExePath);
                            EnsureDesktopShortcut(app.name, globalDirectory, desktopPath, appExePath, globalDirectory);
                            if (app.run == "true") Process.Start(appExePath);
                            return;
                        }

                        if (!isZip)
                            return;

                        string versionKey = GetVersionKey(app.version);
                        string versionMarkerFile = Path.Combine(appDir, ".version");
                        bool alreadyExists = Directory.Exists(appDir);
                        bool hasNewerVersion = HasRemoteUpdate(versionMarkerFile, versionKey);

                        if (!alreadyExists || hasNewerVersion)
                        {
                            if (alreadyExists && hasNewerVersion)
                                SalsaLogger.Info($"Updating {app.name} to {versionKey}");
                            else
                                SalsaLogger.Info("Installing " + app.name);

                            if (alreadyExists)
                                SafeDeleteDirectory(appDir);

                            string zipPath = $"{appDir}.zip";
                            await webClient.DownloadFileTaskAsync(new Uri(app.url), zipPath);
                            ZipFile.ExtractToDirectory(zipPath, appDir);
                            System.IO.File.Delete(zipPath);

                            WriteVersionMarker(versionMarkerFile, versionKey);
                            EnsureDesktopShortcut(app.name, globalDirectory, desktopPath, appZipExe, Path.GetDirectoryName(appZipExe));
                            if (app.run == "true") Process.Start(appZipExe);
                        }
                        else
                        {
                            SalsaLogger.Info($"{app.name} already exists. Skipping download and respecting user desktop layout.");
                            EnsureDesktopShortcut(app.name, globalDirectory, desktopPath, appZipExe, Path.GetDirectoryName(appZipExe));
                            if (app.run == "true") Process.Start(appZipExe);
                        }
                    }
                    }
                    catch (Exception ex)
                    {
                        SalsaLogger.Error($"{app.name} install failed ({app.url}): {DescribeError(ex)}");
                    }
                })).ToList();

                await Task.WhenAll(tasks);
            }
            catch (Exception ex) { SalsaLogger.Error($"Apps list ({jsonUrl}) failed: {DescribeError(ex)}"); }
            finally
            {
                // Apps list failed or has no SalsaSteam entry: don't keep SalsaSteamLauncher waiting.
                SalsaSteamLauncher.SetAppEntry(null);
            }
        }

        // Downloads next to the file first and swaps it in, so a failed or blocked download never leaves a broken file.
        // If the current file is in use (running, or held open by the shell or antivirus) the current one is kept.
        // validate (optional) checks the downloaded file before it replaces anything.
        // Returns true when the new file is in place, false when the current one was kept because it's in use.
        internal static async Task<bool> DownloadAndSwapAsync(WebClient webClient, string name, string url, string exePath, Func<string, bool> validate = null)
        {
            string downloaded = exePath + ".download";
            await webClient.DownloadFileTaskAsync(new Uri(url), downloaded);

            if (validate != null && !validate(downloaded))
            {
                try { System.IO.File.Delete(downloaded); } catch { }
                throw new InvalidDataException("the downloaded file is not valid (wrong size or not an exe)");
            }

            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    if (System.IO.File.Exists(exePath))
                        System.IO.File.Delete(exePath);
                    System.IO.File.Move(downloaded, exePath);
                    return true;
                }
                catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && attempt < 5)
                {
                    await Task.Delay(1000);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    try { System.IO.File.Delete(downloaded); } catch { }

                    if (!System.IO.File.Exists(exePath))
                        throw;

                    SalsaLogger.Warn($"{name} is in use, kept the current {Path.GetFileName(exePath)}. It updates next launch. ({ex.Message})");
                    return false;
                }
            }
        }

        // WebClient wraps the real cause ("file in use", "404", "no space") inside a generic WebException;
        // include the inner messages and the HTTP status so the log says what actually went wrong.
        internal static string DescribeError(Exception ex)
        {
            var parts = new List<string>();
            for (Exception e = ex; e != null; e = e.InnerException)
            {
                string message = e.Message;
                if (e is WebException web && web.Response is HttpWebResponse response)
                    message += $" (HTTP {(int)response.StatusCode} {response.StatusDescription})";
                if (!parts.Contains(message))
                    parts.Add(message);
            }
            return string.Join(" -> ", parts);
        }

        // Silent background app deployment with automated cleanup of obsolete files/folders
        public static async Task AppsInstallSilentAsync(string globalDirectory)
        {
            const string jsonUrl = "https://salsanowfiles.work/jsons/silentappsV2.json";
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
                    string name = Path.GetFileName(file);
                    if (name.EndsWith(".version", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!allowedFiles.Contains(name)) try { System.IO.File.Delete(file); } catch { }
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
                        string versionKey = GetVersionKey(app.version);
                        bool isArchive = app.archive == "true";
                        bool isOpenShell = app.name.Equals("Open-Shell", StringComparison.OrdinalIgnoreCase);

                        string versionMarkerFile = isArchive
                            ? Path.Combine(appFolder, ".version")
                            : Path.Combine(silentAppsPath, $"{app.fileName}.version");

                        bool alreadyExists = isArchive
                            ? System.IO.File.Exists(appZipPath)
                            : System.IO.File.Exists(appPath);
                        bool hasNewerVersion = HasRemoteUpdate(versionMarkerFile, versionKey);

                        if (!isOpenShell && alreadyExists && !hasNewerVersion)
                        {
                            if (app.run == "true")
                            {
                                string runPath = isArchive ? appZipPath : appPath;
                                StartSilentApp(runPath, isArchive);
                            }
                            return;
                        }

                        if (alreadyExists && hasNewerVersion)
                            SalsaLogger.Info($"Updating silent app {app.name} to {versionKey}");
                        else if (!alreadyExists)
                            SalsaLogger.Info("Installing silent app " + app.name);

                        if (isArchive)
                        {
                            if (isOpenShell || hasNewerVersion)
                                SafeDeleteDirectory(appFolder);

                            string zip = $"{appFolder}.zip";
                            if (!Directory.Exists(appFolder)) Directory.CreateDirectory(appFolder);

                            await webClient.DownloadFileTaskAsync(new Uri(app.url), zip);
                            ZipFile.ExtractToDirectory(zip, appFolder);
                            System.IO.File.Delete(zip);
                            WriteVersionMarker(versionMarkerFile, versionKey);

                            if (app.run == "true") Process.Start(appZipPath);
                        }
                        else
                        {
                            await webClient.DownloadFileTaskAsync(new Uri(app.url), appPath);
                            WriteVersionMarker(versionMarkerFile, versionKey);

                            if (app.run == "true")
                                StartSilentApp(appPath, false);
                        }
                    }
                })).ToList();

                await Task.WhenAll(tasks);
            }
            catch (Exception ex) { SalsaLogger.Error(ex.ToString()); }
        }

        private static string GetVersionKey(string version)
        {
            if (!string.IsNullOrWhiteSpace(version))
                return version.Trim();

            return "1.0.0";
        }

        private static string GetRemoteFileName(string url)
        {
            try
            {
                return Uri.UnescapeDataString(Path.GetFileName(new Uri(url).AbsolutePath));
            }
            catch
            {
                return Path.GetFileName(url ?? string.Empty);
            }
        }

        // Reads dotted versions from names like EZManifest-1.2.0.zip
        private static bool TryParseFileVersion(string fileName, out Version version)
        {
            version = null;
            if (string.IsNullOrEmpty(fileName)) return false;

            var match = Regex.Match(fileName, @"\d+\.\d+(?:\.\d+){0,2}");
            if (!match.Success) return false;

            if (!Version.TryParse(match.Value, out var parsed)) return false;

            version = NormalizeVersion(parsed);
            return true;
        }

        private static Version NormalizeVersion(Version version)
        {
            return new Version(
                version.Major,
                version.Minor,
                version.Build < 0 ? 0 : version.Build,
                version.Revision < 0 ? 0 : version.Revision);
        }

        private static void StartSilentApp(string path, bool useShellExecute)
        {
            if (!System.IO.File.Exists(path))
                return;

            if (useShellExecute)
            {
                Process.Start(path);
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }

        private static readonly Version BaselineVersion = new Version(1, 0, 0, 0);

        private static bool HasRemoteUpdate(string versionMarkerFile, string versionKey)
        {
            if (string.IsNullOrEmpty(versionKey))
                return false;

            if (!TryParseFileVersion(versionKey, out var remoteVersion))
                return false;

            if (remoteVersion <= BaselineVersion)
                return false;

            if (!System.IO.File.Exists(versionMarkerFile))
                return true;

            string localText = System.IO.File.ReadAllText(versionMarkerFile).Trim();
            if (!TryParseFileVersion(localText, out var localVersion))
                return true;

            return remoteVersion > localVersion;
        }

        private static void WriteVersionMarker(string versionMarkerFile, string remoteFileName)
        {
            try
            {
                string directory = Path.GetDirectoryName(versionMarkerFile);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                System.IO.File.WriteAllText(versionMarkerFile, remoteFileName);
            }
            catch (Exception ex) { SalsaLogger.Error($"Failed to write version marker: {ex.Message}"); }
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

        // Makes sure a desktop shortcut that is needed exists and points at the right exe, without touching good ones:
        // - On the desktop with the right path: left alone (recreating it would move the icon to a new spot).
        // - On the desktop with a different path, or unreadable: recreated with the right path.
        // - Not on the desktop but in the saved shortcuts (Shortcuts or Backup Shortcuts): left alone. Saved ones are
        //   restored by the shortcut sync, and a shortcut the user deleted stays deleted.
        // - Nowhere: created.
        // acceptedTargets lists extra paths that also count as right (e.g. the GFN real path of the Steam folder).
        public static void EnsureDesktopShortcut(string name, string globalDirectory, string desktopPath, string target, string workDir, params string[] acceptedTargets)
        {
            if (System.IO.File.Exists(desktopPath))
            {
                if (ShortcutPointsTo(desktopPath, target, acceptedTargets))
                    return;

                CreateShortcut(name, desktopPath, target, workDir, false);
                SalsaLogger.Info($"{name} shortcut pointed somewhere else. Recreated it.");
                return;
            }

            if (BackgroundTasks.HasSavedShortcut(globalDirectory, Path.GetFileName(desktopPath)))
                return;

            CreateShortcut(name, desktopPath, target, workDir, false);
            SalsaLogger.Info($"{name} shortcut created.");
        }

        private static bool ShortcutPointsTo(string lnkPath, string target, string[] acceptedTargets)
        {
            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                dynamic shell = Activator.CreateInstance(shellType);
                dynamic lnk = shell.CreateShortcut(lnkPath);
                string current = Convert.ToString(lnk.TargetPath) ?? "";
                if (current.Length == 0)
                    return false;

                string normalized = Path.GetFullPath(current).TrimEnd('\\');
                foreach (string candidate in new[] { target }.Concat(acceptedTargets ?? new string[0]))
                {
                    if (string.Equals(normalized, Path.GetFullPath(candidate).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        // Generates Windows shortcuts. Existing icon, arguments, and hotkey are kept unless keepExisting is false.
        public static void CreateShortcut(string name, string path, string target, string workDir)
        {
            CreateShortcut(name, path, target, workDir, true);
        }

        public static void CreateShortcut(string name, string path, string target, string workDir, bool keepExisting)
        {
            string arguments = "";
            string iconLocation = "";
            string description = "";
            string hotkey = "";
            int windowStyle = 1;

            try
            {
                if (keepExisting && System.IO.File.Exists(path))
                {
                    Type readerType = Type.GetTypeFromProgID("WScript.Shell");
                    dynamic readerShell = Activator.CreateInstance(readerType);
                    dynamic existing = readerShell.CreateShortcut(path);
                    arguments = Convert.ToString(existing.Arguments) ?? "";
                    iconLocation = Convert.ToString(existing.IconLocation) ?? "";
                    description = Convert.ToString(existing.Description) ?? "";
                    hotkey = Convert.ToString(existing.Hotkey) ?? "";
                    windowStyle = Convert.ToInt32(existing.WindowStyle);
                }
            }
            catch { }

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
                lnk.Arguments = arguments;
                lnk.WorkingDirectory = workDir;
                lnk.WindowStyle = windowStyle;
                if (!string.IsNullOrEmpty(description))
                    lnk.Description = description;
                if (!string.IsNullOrEmpty(hotkey))
                    lnk.Hotkey = hotkey;
                if (!string.IsNullOrEmpty(iconLocation))
                    lnk.IconLocation = iconLocation;
                lnk.Save();
            }
            catch (Exception ex) { SalsaLogger.Error($"Shortcut creation failed for {name}: {ex.Message}"); }
        }
    }
}
