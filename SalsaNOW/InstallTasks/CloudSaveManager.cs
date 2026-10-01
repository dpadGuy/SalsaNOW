using Newtonsoft.Json.Linq;
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
                string configPath = Path.Combine(globalDirectory, "CloudSaveConfig.json");
                if (!File.Exists(configPath))
                {
                    File.WriteAllText(configPath, "{\n  \"Enabled\": false,\n  \"RcloneRemote\": \"minio-saves:playnite-saves\",\n  \"RcloneConfigPath\": \"\"\n}");
                    SalsaLogger.Info("CloudSave: wrote template CloudSaveConfig.json (disabled by default).");
                    return false;
                }

                JObject cfg = JObject.Parse(File.ReadAllText(configPath));
                bool enabled = (bool?)cfg["Enabled"] ?? false;
                if (!enabled)
                {
                    SalsaLogger.Info("CloudSave: disabled in CloudSaveConfig.json.");
                    return false;
                }

                string rcloneRemote = (string)cfg["RcloneRemote"];
                string rcloneConfigPath = (string)cfg["RcloneConfigPath"];

                string toolsDir = Path.Combine(globalDirectory, "Tools");
                string configDir = Path.Combine(toolsDir, "ludusavi-config");
                string backupDir = Path.Combine(toolsDir, "ludusavi-backups");
                Directory.CreateDirectory(configDir);
                Directory.CreateDirectory(backupDir);

                string alias = HiddenDriveHelper.EnsureHiddenAlias(globalDirectory);
                if (alias == null) return false;

                string rcloneExe = await EnsureDownloadedAsync(toolsDir, "rclone",
                    "https://downloads.rclone.org/rclone-current-windows-amd64.zip", "rclone.exe");

                WriteLudusaviConfig(configDir, globalDirectory, alias, backupDir, rcloneExe, rcloneConfigPath, rcloneRemote);

                await EnsureDownloadedAsync(toolsDir, "ludusavi",
                    "https://github.com/mtkennerly/ludusavi/releases/latest/download/ludusavi-v0.31.0-win64.zip", "ludusavi.exe");

                SalsaLogger.Info("CloudSave: environment ready, open Ludusavi normally to back up or restore.");
                return true;
            }
            catch (Exception ex)
            {
                SalsaLogger.Error("CloudSave: setup failed: " + ex.Message);
                return false;
            }
        }

        private static void WriteLudusaviConfig(string configDir, string globalDirectory, string aliasDrive,
            string backupDir, string rcloneExe, string rcloneConfigPath, string rcloneRemote)
        {
            string longSource = globalDirectory.TrimEnd('\\').Replace('\\', '/');
            string rcloneExeYaml = string.IsNullOrEmpty(rcloneExe) ? "" : rcloneExe.Replace('\\', '/');
            string rcloneRemoteYaml = string.IsNullOrEmpty(rcloneRemote) ? "" : rcloneRemote;

            string yaml =
$@"manifest:
  url: ""https://raw.githubusercontent.com/mtkennerly/ludusavi-manifest/master/data/manifest.yaml""
redirects:
  - kind: bidirectional
    source: ""{longSource}""
    target: ""{aliasDrive}/""
backup:
  path: ""{backupDir.Replace('\\', '/')}""
restore:
  path: ""{backupDir.Replace('\\', '/')}""
cloud:
  rclone:
    path: ""{rcloneExeYaml}""
  remote:
    Custom:
      id: ""{rcloneRemoteYaml}""
  path: """"
  synchronize: true
";
            File.WriteAllText(Path.Combine(configDir, "config.yaml"), yaml);
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
