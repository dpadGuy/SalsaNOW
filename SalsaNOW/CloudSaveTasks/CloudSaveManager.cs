using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SalsaNOW
{
    internal static class CloudSaveManager
    {
        public static async Task<bool> SetupAsync(string globalDirectory)
        {
            try
            {
                string toolsDir = Path.Combine(globalDirectory, "Tools");

                string alias = EnsureSubstDrive(globalDirectory);
                if (string.IsNullOrEmpty(alias)) 
                    return false;

                TryHideDriveSilent(alias);

                PatchLudusaviRedirect(globalDirectory, alias);

                await EnsureDownloadedAsync(toolsDir, "rclone",
                    "https://downloads.rclone.org/rclone-current-windows-amd64.zip", "rclone.exe");

                string ludusaviExe = await EnsureDownloadedAsync(toolsDir, "ludusavi",
                    "https://github.com/mtkennerly/ludusavi/releases/latest/download/ludusavi-v0.31.0-win64.zip", "ludusavi.exe");

                if (!string.IsNullOrEmpty(ludusaviExe))
                {
                    CreateLudusaviShortcut(ludusaviExe);
                }

                await InstallHydraLauncherAsync();

                SalsaLogger.Info("CloudSave: environment ready, open Ludusavi normally to set up the cloud remote and back up.");
                return true;
            }
            catch (Exception ex)
            {
                SalsaLogger.Error("CloudSave: setup failed: " + ex.Message);
                return false;
            }
        }

        private static string EnsureSubstDrive(string targetPath)
        {
            targetPath = targetPath.TrimEnd('\\');

            try
            {
                var psi = new ProcessStartInfo("cmd.exe", "/c subst")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var process = Process.Start(psi))
                {
                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();

                    string[] lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (string line in lines)
                    {
                        if (line.Contains("=>") && line.IndexOf(targetPath, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            string existingDrive = line.Substring(0, 2);
                            SalsaLogger.Info($"CloudSave: subst alias already exists at {existingDrive}");
                            return existingDrive;
                        }
                    }
                }
            }
            catch { }

            var drives = DriveInfo.GetDrives().Select(d => char.ToUpper(d.Name[0])).ToHashSet();
            char[] letters = { 'Y', 'X', 'W', 'V', 'U', 'T', 'S', 'R', 'Q', 'P', 'O', 'N', 'M', 'L', 'K', 'J', 'H', 'G' };
            
            string driveToUse = null;
            foreach (char c in letters)
            {
                if (!drives.Contains(c))
                {
                    driveToUse = c + ":";
                    break;
                }
            }

            if (driveToUse == null)
            {
                SalsaLogger.Error("CloudSave: No free drive letters available for subst.");
                return null;
            }

            try
            {
                var psi = new ProcessStartInfo("cmd.exe", $"/c subst {driveToUse} \"{targetPath}\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                Process.Start(psi)?.WaitForExit();
                SalsaLogger.Info($"CloudSave: Created subst alias {driveToUse} -> {targetPath}");
                return driveToUse;
            }
            catch (Exception ex)
            {
                SalsaLogger.Error("CloudSave: failed to create subst drive: " + ex.Message);
                return null;
            }
        }

        private static void TryHideDriveSilent(string driveLetter)
        {
            try
            {
                char letter = char.ToUpper(driveLetter[0]);
                int bit = 1 << (letter - 'A');
                using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", true))
                {
                    if (key != null)
                    {
                        int current = (int)key.GetValue("NoDrives", 0);
                        key.SetValue("NoDrives", current | bit, RegistryValueKind.DWord);
                    }
                }
            }
            catch
            {
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
            SalsaLogger.Info("CloudSave: added subst redirect to Ludusavi config.yaml.");
        }

        private static async Task<string> EnsureDownloadedAsync(string toolsRoot, string name, string url, string exeName)
        {
            string toolsDir = Path.Combine(toolsRoot, name);
            string exePath = Path.Combine(toolsDir, exeName);
            
            if (File.Exists(exePath)) 
                return exePath;

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
                if (Directory.Exists(extractDir)) 
                    Directory.Delete(extractDir, true);
                    
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

        private static void CreateLudusaviShortcut(string exePath)
        {
            try
            {
                string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                string shortcutPath = Path.Combine(desktopPath, "Ludusavi.lnk");

                if (File.Exists(shortcutPath)) return;

                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType != null)
                {
                    dynamic shell = Activator.CreateInstance(shellType);
                    var shortcut = shell.CreateShortcut(shortcutPath);
                    shortcut.TargetPath = exePath;
                    shortcut.WorkingDirectory = Path.GetDirectoryName(exePath);
                    shortcut.Description = "Ludusavi - Backup Tool";
                    shortcut.Save();
                    SalsaLogger.Info("CloudSave: created Ludusavi desktop shortcut.");
                }
            }
            catch (Exception ex)
            {
                SalsaLogger.Error($"CloudSave: failed to create desktop shortcut: {ex.Message}");
            }
        }

        private static async Task InstallHydraLauncherAsync()
        {
            try
            {
                string tempPath = Path.Combine(Path.GetTempPath(), "Hydra-Setup.exe");
                string url = "https://github.com/hydralinks/hydra/releases/latest/download/hydra-setup.exe";

                if (!File.Exists(tempPath))
                {
                    SalsaLogger.Info("CloudSave: downloading Hydra Launcher...");
                    using (var wc = new WebClient())
                    {
                        wc.Headers.Add("User-Agent", "SalsaNOW-Setup");
                        await wc.DownloadFileTaskAsync(new Uri(url), tempPath);
                    }
                }

                SalsaLogger.Info("CloudSave: starting Hydra Launcher silent installation...");
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = tempPath,
                    Arguments = "/S",
                    UseShellExecute = true
                };
                
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                SalsaLogger.Error($"CloudSave: failed to install Hydra Launcher: {ex.Message}");
            }
        }
    }
}

