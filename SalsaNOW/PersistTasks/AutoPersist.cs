using Microsoft.Win32;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SalsaNOW
{
    internal class AutoPersist
    {
        public static async Task BackupDesktopRegistry(CancellationToken token, string globalDirectory)
        {
            const string jsonUrl = "https://salsanowfiles.work/ExplorerContents/jsons/RegistryList.json";

            string backupDir = Path.Combine(globalDirectory, "RegistryBackups");
            Directory.CreateDirectory(backupDir);

            using (WebClient client = new WebClient())
            {
                // Download config once
                string json = await client.DownloadStringTaskAsync(jsonUrl);

                RegistryBackupConfig config =
                    JsonConvert.DeserializeObject<RegistryBackupConfig>(json);

                if (config == null)
                    return;

                // Restore existing backups ONCE
                foreach (RegistryEntry entry in config.RegistryKeys)
                {
                    string file = Path.Combine(backupDir, entry.File);

                    if (!File.Exists(file))
                        continue;

                    try
                    {
                        SalsaLogger.Info("Restoring registry: " + entry.Key);

                        using (Process process = Process.Start(new ProcessStartInfo
                        {
                            FileName = "reg.exe",
                            Arguments = $"import \"{file}\"",
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            WindowStyle = ProcessWindowStyle.Hidden
                        }))
                        {
                            process?.WaitForExit();
                        }
                    }
                    catch (Exception ex)
                    {
                        SalsaLogger.Error("Failed to restore " + entry.Key + ": " + ex.Message);
                    }
                }

                SalsaLogger.Info("Initial registry restore complete.");

                // Backup loop
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        foreach (RegistryEntry entry in config.RegistryKeys)
                        {
                            try
                            {
                                string file = Path.Combine(backupDir, entry.File);

                                using (Process process = Process.Start(new ProcessStartInfo
                                {
                                    FileName = "reg.exe",
                                    Arguments = $"export \"{entry.Key}\" \"{file}\" /y",
                                    UseShellExecute = false,
                                    CreateNoWindow = true,
                                    WindowStyle = ProcessWindowStyle.Hidden
                                }))
                                {
                                    process?.WaitForExit();
                                }
                            }
                            catch (Exception ex)
                            {
                                SalsaLogger.Error("Failed to back up " + entry.Key + ": " + ex.Message);
                            }
                        }

                        await Task.Delay(
                            TimeSpan.FromSeconds(config.BackupIntervalSeconds),
                            token);
                    }
                    catch (TaskCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        SalsaLogger.Error("Registry backup loop failed: " + ex.Message);

                        await Task.Delay(TimeSpan.FromSeconds(5), token);
                    }
                }
            }
        }

        public static async Task ApplyCustomRegistryFiles(string globalDirectory)
        {
            const string jsonUrl =
                "https://salsanowfiles.work/ExplorerContents/jsons/RegistryFiles.json";

            string downloadDir = Path.Combine(globalDirectory, "RegistryFiles");
            Directory.CreateDirectory(downloadDir);

            using (WebClient client = new WebClient())
            {
                string json = await client.DownloadStringTaskAsync(jsonUrl);

                var files =
                    JsonConvert.DeserializeObject<List<RegistryFile>>(json);

                if (files == null)
                    return;

                foreach (RegistryFile entry in files)
                {
                    string regFile = Path.Combine(
                        downloadDir,
                        Path.GetFileName(entry.File));

                    await client.DownloadFileTaskAsync(
                        entry.Url,
                        regFile);

                    using (Process process = Process.Start(
                        new ProcessStartInfo
                        {
                            FileName = "reg.exe",
                            Arguments = "import \"" + regFile + "\"",
                            UseShellExecute = false,
                            CreateNoWindow = true
                        }))
                    {
                        process?.WaitForExit();
                    }
                }
            }
        }

        // Sets up directory junctions so that the given paths persist on gfn
        public static async Task SetupGameSavesAsync(string globalDirectory)
        {
            try
            {
                SalsaLogger.Info("Setting up Cloud Save directory junctions...");
                string json;
                using (var wc = new WebClient()) json = await wc.DownloadStringTaskAsync("https://salsanowfiles.work/jsons/GameSavesPathsTest.json");
                var savePaths = JsonConvert.DeserializeObject<GamesSavePaths>(json);
                string savesRoot = Path.Combine(globalDirectory, "Game Saves");
                Directory.CreateDirectory(savesRoot);

                foreach (var dir in savePaths.paths)
                {
                    string crafted = Path.Combine(savesRoot, Path.GetFileName(dir));
                    Directory.CreateDirectory(crafted);

                    if (IsSteamGamesFolder(dir))
                        PersistExistingFolderContents(dir, crafted);

                    string parent = Path.GetDirectoryName(dir);
                    if (!string.IsNullOrEmpty(parent))
                        Directory.CreateDirectory(parent);

                    RunHiddenCmd($"/c rmdir /s /q \"{dir}\"");
                    RunHiddenCmd($"/c mklink /J \"{dir}\" \"{crafted}\"");

                    if (dir.Contains(@"C:\Users\Public\Documents")) await HandlePublicDocs(dir, crafted);
                }
                SalsaLogger.Info("Cloud Save junctions successfully created.");
            }
            catch (Exception ex) { SalsaLogger.Error($"Game Saves Setup Error: {ex.Message}"); }
        }

        private static async Task HandlePublicDocs(string dir, string crafted)
        {
            // Kill NvContainerWindowClass to release the lock on Public Documents
            foreach (var p in Process.GetProcessesByName("NVDisplay.Container"))
            {
                NativeMethods.EnumWindows((hWnd, lp) => {
                    NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
                    if (pid == p.Id)
                    {
                        var sb = new StringBuilder(256);
                        NativeMethods.GetClassName(hWnd, sb, sb.Capacity);
                        if (sb.ToString().StartsWith("NvContainerWindowClass", StringComparison.OrdinalIgnoreCase))
                            NativeMethods.PostMessage(hWnd, (uint)NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                    }
                    return true;
                }, IntPtr.Zero);
            }

            await Task.Delay(150);

            // Retry loop to ensure junction is created once the process releases the handle
            for (int i = 0; i < 20; i++)
            {
                try
                {
                    if (Directory.Exists(dir)) Directory.Delete(dir, true);
                    if (!Directory.Exists(dir)) { RunHiddenCmd($"/c mklink /J \"{dir}\" \"{crafted}\""); break; }
                }
                catch { }
                await Task.Delay(150);
            }
        }

        private static bool IsSteamGamesFolder(string dir)
        {
            try
            {
                return string.Equals(
                    Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    Path.GetFullPath(@"C:\Program Files (x86)\Steam\steam\games"),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static void PersistExistingFolderContents(string source, string dest)
        {
            if (!Directory.Exists(source))
                return;
            if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
                return;

            try
            {
                string sourceRoot = source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
                {
                    string relative = file.Substring(sourceRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    string target = Path.Combine(dest, relative);
                    string targetParent = Path.GetDirectoryName(target);
                    if (!string.IsNullOrEmpty(targetParent))
                        Directory.CreateDirectory(targetParent);
                    if (!File.Exists(target))
                        File.Copy(file, target);
                }
                SalsaLogger.Info("Persisted existing Steam steam\\games folder contents.");
            }
            catch (Exception ex)
            {
                SalsaLogger.Error("Failed to persist Steam steam\\games contents: " + ex.Message);
            }
        }

        private static void RunHiddenCmd(string args)
        {
            Process.Start(new ProcessStartInfo("cmd.exe", args)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            })?.WaitForExit();
        }
    }
}
