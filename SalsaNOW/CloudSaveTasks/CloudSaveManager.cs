using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace SalsaNOW
{
    internal static class CloudSaveManager
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern SafeFileHandle CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(SafeFileHandle hFile, StringBuilder lpszFilePath, uint cchFilePath, uint dwFlags);

        private const uint GENERIC_READ = 0x80000000;
        private const uint FILE_SHARE_READ_WRITE = 0x00000003;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

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

                string realSavesRoot = DetectRealSavesRoot();
                if (string.IsNullOrEmpty(realSavesRoot))
                {
                    SalsaLogger.Error("CloudSave: could not resolve the real saves root, aborting.");
                    return false;
                }

                string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

                PatchLudusaviConfig(realSavesRoot, userProfile, rcloneExe);
                CreateLudusaviShortcut(ludusaviExe);

                SalsaLogger.Info("CloudSave: environment ready, open Ludusavi normally to set up the cloud remote and back up.");
                return true;
            }
            catch (Exception ex)
            {
                SalsaLogger.Error("CloudSave: setup failed: " + ex.Message);
                return false;
            }
        }

        private static string ResolveJunctionTarget(string path)
        {
            if (!Directory.Exists(path)) return null;

            using (var handle = CreateFile(path, GENERIC_READ, FILE_SHARE_READ_WRITE, IntPtr.Zero,
                OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero))
            {
                if (handle.IsInvalid) return null;

                var sb = new StringBuilder(2048);
                uint len = GetFinalPathNameByHandle(handle, sb, (uint)sb.Capacity, 0);
                if (len == 0 || len >= sb.Capacity) return null;

                string result = sb.ToString();
                if (result.StartsWith(@"\\?\")) result = result.Substring(4);
                return result;
            }
        }

        private static string DetectRealSavesRoot()
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var probes = new List<string>
            {
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Path.Combine(userProfile, "AppData", "LocalLow"),
                Path.Combine(userProfile, "Saved Games")
            };

            var resolved = new List<string>();
            foreach (var probe in probes)
            {
                string target = ResolveJunctionTarget(probe);
                if (!string.IsNullOrEmpty(target) &&
                    !target.TrimEnd('\\').Equals(probe.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                {
                    resolved.Add(target.Replace('\\', '/'));
                }
            }

            if (resolved.Count == 0) return null;

            string[] commonParts = resolved[0].Split('/');
            int commonLen = commonParts.Length;

            foreach (var r in resolved.Skip(1))
            {
                string[] parts = r.Split('/');
                int i = 0;
                while (i < commonLen && i < parts.Length && string.Equals(parts[i], commonParts[i], StringComparison.OrdinalIgnoreCase))
                    i++;
                commonLen = i;
            }

            if (commonLen == 0) return null;
            return string.Join("/", commonParts.Take(commonLen));
        }

        private static void PatchLudusaviConfig(string realSavesRoot, string userProfilePath, string rcloneExePath)
        {
            try
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string configPath = Path.Combine(appData, "ludusavi", "config.yaml");
                Directory.CreateDirectory(Path.GetDirectoryName(configPath));

                string normalizedSource = realSavesRoot.TrimEnd('/');
                string normalizedAlias = userProfilePath.Replace('\\', '/').TrimEnd('/') + "/";
                string normalizedRclone = !string.IsNullOrEmpty(rcloneExePath) ? rcloneExePath.Replace('\\', '/') : "";

                var lines = File.Exists(configPath)
                    ? File.ReadAllLines(configPath).ToList()
                    : new List<string>();

                if (!string.IsNullOrEmpty(normalizedRclone))
                {
                    int appsIndex = lines.FindIndex(l => l.TrimEnd() == "apps:");
                    if (appsIndex == -1)
                    {
                        lines.Add("apps:");
                        lines.Add("  rclone:");
                        lines.Add($"    path: \"{normalizedRclone}\"");
                        lines.Add("    arguments: \"--fast-list --ignore-checksum\"");
                    }
                    else
                    {
                        int rcloneIndex = lines.FindIndex(appsIndex + 1, l => l.Trim() == "rclone:");
                        if (rcloneIndex == -1)
                        {
                            lines.Insert(appsIndex + 1, "  rclone:");
                            lines.Insert(appsIndex + 2, $"    path: \"{normalizedRclone}\"");
                        }
                        else
                        {
                            int pathIndex = lines.FindIndex(rcloneIndex + 1, l => l.Trim().StartsWith("path:"));
                            if (pathIndex != -1 && lines[pathIndex].StartsWith("    "))
                                lines[pathIndex] = $"    path: \"{normalizedRclone}\"";
                            else
                                lines.Insert(rcloneIndex + 1, $"    path: \"{normalizedRclone}\"");
                        }
                    }
                }

                string marker = $"source: \"{normalizedSource}\"";
                lines.RemoveAll(l => l.Contains("source:") || l.Contains("target:") || l.Trim() == "- kind: bidirectional");

                int redirectsIndex = lines.FindIndex(l => l.StartsWith("redirects:"));
                var newEntry = new[]
                {
                    "  - kind: bidirectional",
                    $"    {marker}",
                    $"    target: \"{normalizedAlias}\""
                };

                if (redirectsIndex == -1)
                {
                    lines.Add("redirects:");
                    lines.AddRange(newEntry);
                }
                else
                {
                    lines.InsertRange(redirectsIndex + 1, newEntry);
                }

                File.WriteAllLines(configPath, lines);
                SalsaLogger.Info($"CloudSave: redirect set {normalizedSource} -> {normalizedAlias}");
            }
            catch (Exception ex)
            {
                SalsaLogger.Error("CloudSave: failed to patch Ludusavi config: " + ex.Message);
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
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls13;
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
                    catch (Exception)
                    {
                        if (attempt == maxRetries) throw;
                        await Task.Delay(2000);
                    }
                }
            }
        }
    }
}