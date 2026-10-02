using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace SalsaNOW
{
    internal static class CloudSaveManager
    {
        public static async Task<bool> SetupAsync(string globalDirectory)
        {
            try
            {
                string toolsDir = Path.Combine(globalDirectory, "Tools");

                string rcloneExe = await EnsureDownloadedAsync(toolsDir, "rclone",
                    "https://downloads.rclone.org/rclone-current-windows-amd64.zip", "rclone.exe");

                string ludusaviExe = await EnsureDownloadedAsync(toolsDir, "ludusavi",
                    "https://github.com/mtkennerly/ludusavi/releases/latest/download/ludusavi-v0.31.0-win64.zip", "ludusavi.exe");

                if (string.IsNullOrEmpty(ludusaviExe)) return false;

                string userRoot = DetectUserSavesRoot();
                if (string.IsNullOrEmpty(userRoot))
                {
                    SalsaLogger.Error("CloudSave: could not resolve the user profile root, aborting.");
                    return false;
                }

                PatchLudusaviConfig(userRoot, rcloneExe);
                CreateLudusaviShortcut(ludusaviExe);

                SalsaLogger.Info($"CloudSave: environment ready, using {userRoot}. Open Ludusavi normally to set up the cloud remote and back up.");
                return true;
            }
            catch (Exception ex)
            {
                SalsaLogger.Error("CloudSave: setup failed: " + ex.Message);
                return false;
            }
        }

        // Returns e.g. "C:/Users/8f0...abc" — no subst, no junction resolution.
        // Works whether the folder name is a real username or a kiosk hash.
        private static string DetectUserSavesRoot()
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(userProfile) || !Directory.Exists(userProfile))
                return null;

            return userProfile.Replace('\\', '/').TrimEnd('/');
        }

        private static void PatchLudusaviConfig(string userRoot, string rcloneExePath)
        {
            try
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string configPath = Path.Combine(appData, "ludusavi", "config.yaml");
                Directory.CreateDirectory(Path.GetDirectoryName(configPath));

                string normalizedRclone = !string.IsNullOrEmpty(rcloneExePath)
                    ? rcloneExePath.Replace('\\', '/')
                    : "";

                var lines = File.Exists(configPath)
                    ? File.ReadAllLines(configPath).ToList()
                    : new List<string>();

                if (!string.IsNullOrEmpty(normalizedRclone))
                {
                    UpsertRcloneBlock(lines, normalizedRclone);
                }

                // userRoot is currently informational only; uncomment below to also set
                // an explicit roots entry pointing at this user.
                // UpsertRootsEntry(lines, userRoot);

                File.WriteAllLines(configPath, lines);
                SalsaLogger.Info($"CloudSave: rclone configured at {normalizedRclone}");
            }
            catch (Exception ex)
            {
                SalsaLogger.Error("CloudSave: failed to patch Ludusavi config: " + ex.Message);
            }
        }

        private static void UpsertRcloneBlock(List<string> lines, string rclonePath)
        {
            int appsIndex = lines.FindIndex(l => l.TrimEnd() == "apps:");
            if (appsIndex == -1)
            {
                lines.Add("apps:");
                lines.Add("  rclone:");
                lines.Add($"    path: \"{rclonePath}\"");
                lines.Add("    arguments: \"--fast-list --ignore-checksum\"");
                return;
            }

            int rcloneIndex = lines.FindIndex(appsIndex + 1, l => l.Trim() == "rclone:");
            if (rcloneIndex == -1)
            {
                lines.Insert(appsIndex + 1, "  rclone:");
                lines.Insert(appsIndex + 2, $"    path: \"{rclonePath}\"");
                lines.Insert(appsIndex + 3, "    arguments: \"--fast-list --ignore-checksum\"");
                return;
            }

            int pathIndex = lines.FindIndex(rcloneIndex + 1, l => l.Trim().StartsWith("path:"));
            if (pathIndex != -1 && lines[pathIndex].StartsWith("    "))
            {
                lines[pathIndex] = $"    path: \"{rclonePath}\"";
            }
            else
            {
                lines.Insert(rcloneIndex + 1, $"    path: \"{rclonePath}\"");
            }
        }

        private static void CreateLudusaviShortcut(string exePath)
        {
            try
            {
                string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                string shortcutPath = Path.Combine(desktopPath, "Ludusavi.lnk");
                if (File.Exists(shortcutPath)) return;

                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return;

                dynamic shell = Activator.CreateInstance(shellType);
                var shortcut = shell.CreateShortcut(shortcutPath);
                shortcut.TargetPath = exePath;
                shortcut.WorkingDirectory = Path.GetDirectoryName(exePath);
                shortcut.Description = "Ludusavi - Backup Tool";
                shortcut.Save();
                SalsaLogger.Info("CloudSave: created Ludusavi desktop shortcut.");
            }
            catch (Exception ex)
            {
                SalsaLogger.Error($"CloudSave: failed to create desktop shortcut: {ex.Message}");
            }
        }

        private static async Task<string> EnsureDownloadedAsync(string toolsRoot, string name, string url, string exeName)
        {
            string toolsDir = Path.Combine(toolsRoot, name);
            string exePath = Path.Combine(toolsDir, exeName);
            if (File.Exists(exePath)) return exePath;

            try
            {
                Directory.CreateDirectory(toolsDir);
                string zipPath = Path.Combine(toolsDir, name + ".zip");

                SalsaLogger.Info($"CloudSave: downloading {name}...");
                await DownloadFileAsync(url, zipPath);

                string extractDir = Path.Combine(toolsDir, "extract");
                if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
                ZipFile.ExtractToDirectory(zipPath, extractDir);
                File.Delete(zipPath);

                string found = Directory.GetFiles(extractDir, exeName, SearchOption.AllDirectories).FirstOrDefault();
                if (found == null)
                {
                    SalsaLogger.Error($"CloudSave: {exeName} not found inside downloaded archive.");
                    return null;
                }

                File.Copy(found, exePath, true);
                Directory.Delete(extractDir, true);
                return exePath;
            }
            catch (Exception ex)
            {
                SalsaLogger.Error($"CloudSave: failed to install {name}: {ex.Message}");
                return null;
            }
        }

        private static async Task DownloadFileAsync(string url, string destinationPath, int maxRetries = 3)
        {
            using (var handler = new HttpClientHandler { AllowAutoRedirect = true })
            using (var client = new HttpClient(handler))
            {
                client.DefaultRequestHeaders.Add("User-Agent", "SalsaNOW-CloudSave");
                client.Timeout = TimeSpan.FromMinutes(5);

                for (int attempt = 1; attempt <= maxRetries; attempt++)
                {
                    try
                    {
                        using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
                        {
                            response.EnsureSuccessStatusCode();
                            using (var stream = await response.Content.ReadAsStreamAsync())
                            using (var file = File.Open(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None))
                            {
                                await stream.CopyToAsync(file);
                                return;
                            }
                        }
                    }
                    catch
                    {
                        if (attempt == maxRetries) throw;
                        await Task.Delay(2000);
                    }
                }
            }
        }
    }
}
