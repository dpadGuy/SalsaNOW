using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SalsaNOW
{
    internal static class BackgroundTasks
    {
        // Polls for Easy Anti-Cheat processes and terminates them to prevent GFN session shutdowns
        public static async Task StartEacWatcherAsync(CancellationToken token)
        {
            var eacProcessNames = new[] { "EasyAntiCheat_EOS_Setup", "EasyAntiCheat_Setup", "EasyAntiCheat", "EasyAntiCheat_EOS" };

            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(5000, token);
                    
                    bool eacTerminated = false;

                    // Iterate by specific name instead of polling ALL Windows processes
                    foreach (var processName in eacProcessNames)
                    {
                        var runningProcs = Process.GetProcessesByName(processName);

                        if (runningProcs.Length == 0) break;

                        foreach (var proc in runningProcs)
                        {
                            try 
                            { 
                                if (!proc.HasExited) 
                                {
                                    proc.Kill(); 
                                    SalsaLogger.Warn($"Terminated blocked process: {proc.ProcessName}");
                                    eacTerminated = true;
                                }
                            } 
                            catch { }
                            finally 
                            { 
                                proc.Dispose(); // Release OS handles immediately to prevent memory leaks in the loop
                            }
                        }
                    }

                    if (eacTerminated)
                    {
                        _ = Task.Run(() => MessageBox.Show("Easy Anti-Cheat processes have been terminated to prevent session issues. Anti-Cheat games don't work.", "SalsaNOW", MessageBoxButtons.OK, MessageBoxIcon.Information));

                        eacTerminated = false;
                    }
                }
            }
            catch (TaskCanceledException) { }
        }
        
        // Monitors Desktop and Start Menu shortcuts, syncing them to the persistent SalsaNOW directory
        public static async Task StartShortcutsSavingAsync(string globalDirectory, CancellationToken token)
        {
            string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string startMenuPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Start Menu\Programs");
            string shortcutsDir = Path.Combine(globalDirectory, "Shortcuts");
            string backupDir = Path.Combine(globalDirectory, "Backup Shortcuts");

            Directory.CreateDirectory(shortcutsDir);
            Directory.CreateDirectory(backupDir);

            // 1. Initial Sync: Throw the newest saved shortcut for each name onto the Desktop
            try
            {
                foreach (KeyValuePair<string, string> shortcut in GetNewestShortcutPerName(shortcutsDir))
                    CopyShortcut(shortcut.Value, Path.Combine(desktopPath, shortcut.Key));

                SalsaLogger.Info("Initial Desktop shortcut sync completed.");
            }
            catch (Exception ex) { SalsaLogger.Error($"Initial shortcut sync failed: {ex.Message}"); }

            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(5000, token);

                    // Sync Desktop to Shortcuts, overwriting saved copies
                    try
                    {
                        if (Directory.Exists(desktopPath))
                        {
                            foreach (string glob in ShortcutGlobs)
                            {
                                foreach (string file in Directory.GetFiles(desktopPath, glob, SearchOption.TopDirectoryOnly))
                                    CopyShortcut(file, Path.Combine(shortcutsDir, Path.GetFileName(file)));
                            }
                        }
                    }
                    catch { }

                    // Sync Shortcuts To Start Menu from the newest saved copy of each shortcut
                    try
                    {
                        foreach (KeyValuePair<string, string> shortcut in GetNewestShortcutPerName(shortcutsDir))
                            CopyShortcut(shortcut.Value, Path.Combine(startMenuPath, shortcut.Key));
                    }
                    catch { }

                    // 5. Cleanup: Move deleted shortcuts from the primary folder to the long-term backup
                    try
                    {
                        foreach (var backupFile in GetShortcutFiles(shortcutsDir))
                        {
                            string fileName = Path.GetFileName(backupFile);
                            string originalPath = Path.Combine(desktopPath, fileName);

                            if (!File.Exists(originalPath))
                            {
                                if (File.Exists(Path.Combine(backupDir, fileName)))
                                {
                                    File.Delete(backupFile);
                                }
                                else
                                {
                                    File.Move(backupFile, Path.Combine(backupDir, fileName));
                                    SalsaLogger.Info($"Moved deleted shortcut to long-term backup: {fileName}");
                                }
                            }
                        }
                    }
                    catch { }
                }
            }
            catch (TaskCanceledException) { }
        }

        private static readonly string[] ShortcutGlobs = { "*.lnk", "*.url" };

        private static IEnumerable<string> GetShortcutFiles(string directory)
        {
            if (!Directory.Exists(directory))
                yield break;

            foreach (string glob in ShortcutGlobs)
            {
                foreach (string file in Directory.GetFiles(directory, glob, SearchOption.AllDirectories))
                    yield return file;
            }
        }

        private static Dictionary<string, string> GetNewestShortcutPerName(string directory)
        {
            var newest = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in GetShortcutFiles(directory))
            {
                string name = Path.GetFileName(file);
                string existing;
                if (!newest.TryGetValue(name, out existing)
                    || File.GetLastWriteTimeUtc(file) >= File.GetLastWriteTimeUtc(existing))
                {
                    newest[name] = file;
                }
            }

            return newest;
        }

        private static void CopyShortcut(string source, string dest)
        {
            try
            {
                string destDirectory = Path.GetDirectoryName(dest);
                if (!string.IsNullOrEmpty(destDirectory))
                    Directory.CreateDirectory(destDirectory);

                if (File.Exists(dest))
                    File.SetAttributes(dest, FileAttributes.Normal);

                File.Copy(source, dest, true);
                File.SetLastWriteTimeUtc(dest, DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                SalsaLogger.Error("Shortcut copy failed for " + Path.GetFileName(source) + ": " + ex.Message);
            }
        }

        // Continuously monitors and terminates the default GFN CustomExplorer shell
        public static async Task StartTerminateGFNExplorerShellAsync(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(500, token);
                    IntPtr windowPtr = NativeMethods.FindWindowByCaption(IntPtr.Zero, "CustomExplorer");
                    if (windowPtr != IntPtr.Zero)
                    {
                        NativeMethods.SendMessage(windowPtr, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                        SalsaLogger.Info("CustomExplorer has been closed.");
                    }
                }
            }
            catch (TaskCanceledException) { }
        }

        public static Task StartBrickPreventionAsync(CancellationToken token)
        {
            // HOTFIX1: Continue execution even if something goes wrong (will be improved in the near future)
            try
            {
                string userData = @"C:\Program Files (x86)\Steam\userdata";
                string blackListed = "\"LaunchOptions\"";

                if (!Directory.Exists(userData))
                    return Task.CompletedTask;

                var watcher = new FileSystemWatcher
                {
                    Path = userData,
                    Filter = "localconfig.vdf",
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName
                };

                FileSystemEventHandler handler = (s, e) => HandleFile(e.FullPath, blackListed);
                RenamedEventHandler renameHandler = (s, e) => HandleFile(e.FullPath, blackListed);

                watcher.Created += handler;
                watcher.Changed += handler;
                watcher.Renamed += renameHandler;

                watcher.EnableRaisingEvents = true;

                // Initial scan (important)
                foreach (var file in Directory.EnumerateFiles(userData, "localconfig.vdf", SearchOption.AllDirectories))
                {
                    HandleFile(file, blackListed);
                }

                // Keep alive until cancelled
                return Task.Run(() =>
                {
                    try
                    {
                        token.WaitHandle.WaitOne();
                    }
                    finally
                    {
                        watcher.EnableRaisingEvents = false;
                        watcher.Dispose();
                    }
                }, token);
            }
            catch (Exception ex)
            {
                SalsaLogger.Error($"Brick prevention task failed: {ex.Message}, MAKE SURE YOU DO NOT USE THE STEAM LAUNCH OPTIONS");
                return Task.CompletedTask;
            }
        }
        private static void HandleFile(string path, string blackListed)
        {
            Task.Run(async () =>
            {
                for (int i = 0; i < 3; i++)
                {
                    try
                    {
                        if (!File.Exists(path))
                            return;

                        string content;
                        using (var reader = new StreamReader(path))
                        {
                            content = await reader.ReadToEndAsync();
                        }

                        if (content.Contains(blackListed))
                        {
                            File.Delete(path);

                            NativeMethods.ShowWindow(
                                NativeMethods.GetConsoleWindow(),
                                NativeMethods.SW_SHOW);

                            SalsaLogger.Error("STEAM LAUNCH OPTIONS DETECTED. Session terminated.");

                            foreach (var p in Process.GetProcessesByName("steam"))
                                p.Kill();
                        }

                        return;
                    }
                    catch (IOException)
                    {
                        await Task.Delay(200);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        return;
                    }
                }
            });
        }

        public static void EnvironmentSetup()
        {
            try
            {
                SalsaLogger.Info("Setting up environment variables...");

                string dotnetRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Microsoft",
                    "dotnet");

                string powershellRoot = @"I:\Apps\SalsaNOW\SilentApps\PowerShell";

                using (RegistryKey key = Registry.CurrentUser.CreateSubKey("Environment"))
                {
                    if (key == null)
                    {
                        SalsaLogger.Error("Could not open HKCU\\Environment.");
                        return;
                    }

                    key.SetValue("DOTNET_ROOT", dotnetRoot, RegistryValueKind.String);
                    key.SetValue("POWERSHELL_ROOT", powershellRoot, RegistryValueKind.String);

                    string path = key.GetValue("Path", "").ToString();
                    path = AddPathIfMissing(path, dotnetRoot);
                    path = AddPathIfMissing(path, powershellRoot);

                    key.SetValue("Path", path, RegistryValueKind.ExpandString);
                    key.Flush();
                }

                SalsaLogger.Info("Registry updated.");

                Environment.SetEnvironmentVariable("DOTNET_ROOT", dotnetRoot);
                Environment.SetEnvironmentVariable("POWERSHELL_ROOT", powershellRoot);

                string currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
                currentPath = AddPathIfMissing(currentPath, dotnetRoot);
                currentPath = AddPathIfMissing(currentPath, powershellRoot);

                Environment.SetEnvironmentVariable("PATH", currentPath);

                SalsaLogger.Info("Current process environment updated.");

                IntPtr result;
                NativeMethods.SendMessageTimeout(
                    (IntPtr)NativeMethods.HWND_BROADCAST,
                    NativeMethods.WM_SETTINGCHANGE,
                    IntPtr.Zero,
                    "Environment",
                    NativeMethods.SMTO_ABORTIFHUNG,
                    5000,
                    out result);

                SalsaLogger.Info("Environment change broadcast sent.");

                using (RegistryKey verify = Registry.CurrentUser.OpenSubKey("Environment"))
                {
                    SalsaLogger.Info("Verification:");
                    SalsaLogger.Info("DOTNET_ROOT = " + verify.GetValue("DOTNET_ROOT", ""));
                    SalsaLogger.Info("POWERSHELL_ROOT = " + verify.GetValue("POWERSHELL_ROOT", ""));
                }

                SalsaLogger.Info("Environment setup complete.");
            }
            catch (Exception ex)
            {
                SalsaLogger.Error("Environment setup failed: " + ex.Message);
                SalsaLogger.Error(ex.StackTrace ?? "");
            }
        }

        private static string AddPathIfMissing(string currentPath, string directory)
        {
            if (string.IsNullOrWhiteSpace(currentPath))
                return directory;

            if (ContainsPath(currentPath, directory))
                return currentPath;

            return currentPath.TrimEnd(';') + ";" + directory;
        }

        private static bool ContainsPath(string path, string directory)
        {
            return path
                .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Any(p => string.Equals(
                    p.TrimEnd('\\'),
                    directory.TrimEnd('\\'),
                    StringComparison.OrdinalIgnoreCase));
        }

    }
}
