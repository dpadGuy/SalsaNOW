using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
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

                string alias = HiddenDriveHelper.EnsureHiddenAlias(globalDirectory);
                if (alias == null) return false;

                PatchLudusaviRedirect(globalDirectory, alias);

                await EnsureDownloadedAsync(toolsDir, "rclone",
                    "https://downloads.rclone.org/rclone-current-windows-amd64.zip", "rclone.exe");

                await EnsureDownloadedAsync(toolsDir, "ludusavi",
                    "https://github.com/mtkennerly/ludusavi/releases/latest/download/ludusavi-v0.31.0-win64.zip", "ludusavi.exe");

                SalsaLogger.Info("CloudSave: environment ready, open Ludusavi normally to set up the cloud remote and back up.");
                return true;
            }
            catch (Exception ex)
            {
                SalsaLogger.Error("CloudSave: setup failed: " + ex.Message);
                return false;
            }
        }

        private static void PatchLudusaviRedirect(string globalDirectory, string aliasDrive)
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string configPath = Path.Combine(appData, "ludusavi", "config.yaml");
            Directory.CreateDirectory(Path.GetDirectoryName(configPath));

            string longSource = globalDirectory.TrimEnd('\\').Replace('\\', '/');
            string targetValue = aliasDrive + "/";
            string marker = $"source: \"{longSource}\"";

            var lines = File.Exists(configPath)
                ? File.ReadAllLines(configPath).ToList()
                : new System.Collections.Generic.List<string>();

            if (lines.Any(l => l.Contains(marker)))
            {
                SalsaLogger.Info("CloudSave: redirect already present in Ludusavi config.yaml.");
                return;
            }

            int redirectsIndex = lines.FindIndex(l => l.TrimEnd() == "redirects:");
            var newEntry = new[]
            {
                "  - kind: bidirectional",
                $"    {marker}",
                $"    target: \"{targetValue}\""
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
            SalsaLogger.Info("CloudSave: added long-path redirect to Ludusavi config.yaml.");
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

                using (var wc = new WebClient())
                {
                    wc.Headers.Add("User-Agent", "SalsaNOW-CloudSave");
                    SalsaLogger.Info($"CloudSave: downloading {name}...");
                    await wc.DownloadFileTaskAsync(new Uri(url), zipPath);
                }

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
    }
}
