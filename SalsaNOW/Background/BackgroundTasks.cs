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
        
        // Saves a desktop shortcut into Shortcuts only when its contents changed.
        // test.lnk is stored as test.lnk.bak, and test.url as test.url.bak.
        public static async Task StartShortcutsSavingAsync(string globalDirectory, CancellationToken token)
        {
            string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string startMenuPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Start Menu\Programs");
            string shortcutsDir = Path.Combine(globalDirectory, "Shortcuts");
            string backupDir = Path.Combine(globalDirectory, "Backup Shortcuts");

            Directory.CreateDirectory(shortcutsDir);
            Directory.CreateDirectory(backupDir);
            Directory.CreateDirectory(startMenuPath);

            try
            {
                RenameSavedShortcutsToBak(shortcutsDir);
                RenameSavedShortcutsToBak(backupDir);
                RestoreBakShortcuts(shortcutsDir, desktopPath);
                NormalizeDesktopShortcuts(desktopPath);
                SalsaLogger.Info("Initial Desktop shortcut sync completed.");
            }
            catch (Exception ex) { SalsaLogger.Error($"Initial shortcut sync failed: {ex.Message}"); }

            try
            {
                bool steamWasRunning = false;
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(1000, token);

                    bool steamRunning = IsSteamRunning();
                    if (!steamRunning)
                    {
                        if (steamWasRunning)
                            SalsaLogger.Info("Steam is not running. Shortcut sync paused.");
                        steamWasRunning = false;
                        continue;
                    }

                    if (!steamWasRunning)
                        SalsaLogger.Info("Steam is running. Shortcut sync continued.");
                    steamWasRunning = true;

                    try
                    {
                        NormalizeDesktopShortcuts(desktopPath);
                        SyncModifiedShortcuts(desktopPath, startMenuPath, shortcutsDir, backupDir);
                    }
                    catch (Exception ex)
                    {
                        SalsaLogger.Error("Shortcut sync failed: " + ex.Message);
                    }
                }
            }
            catch (TaskCanceledException) { }
        }

        private static bool IsSteamRunning()
        {
            Process[] processes = Process.GetProcesses();
            try
            {
                foreach (Process process in processes)
                {
                    try
                    {
                        if (process.ProcessName.StartsWith("steam", StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                    catch
                    {
                    }
                }

                return false;
            }
            finally
            {
                foreach (Process process in processes)
                    process.Dispose();
            }
        }

        public static bool HasSavedShortcut(string globalDirectory, string fileName)
        {
            if (string.IsNullOrEmpty(globalDirectory) || string.IsNullOrEmpty(fileName))
                return false;

            string bakName = fileName + ".bak";
            if (File.Exists(Path.Combine(globalDirectory, "Shortcuts", fileName)))
                return true;
            if (File.Exists(Path.Combine(globalDirectory, "Shortcuts", bakName)))
                return true;
            if (File.Exists(Path.Combine(globalDirectory, "Backup Shortcuts", fileName)))
                return true;
            if (File.Exists(Path.Combine(globalDirectory, "Backup Shortcuts", bakName)))
                return true;

            return false;
        }

        public static void ClearSavedShortcut(string globalDirectory, string fileName)
        {
            if (string.IsNullOrEmpty(globalDirectory) || string.IsNullOrEmpty(fileName))
                return;

            string bakName = fileName + ".bak";
            TryDeleteFile(Path.Combine(globalDirectory, "Shortcuts", fileName));
            TryDeleteFile(Path.Combine(globalDirectory, "Shortcuts", bakName));
            TryDeleteFile(Path.Combine(globalDirectory, "Backup Shortcuts", fileName));
            TryDeleteFile(Path.Combine(globalDirectory, "Backup Shortcuts", bakName));
        }

        public static void RememberDesktopShortcut(string globalDirectory, string desktopPath)
        {
            if (string.IsNullOrEmpty(globalDirectory) || string.IsNullOrEmpty(desktopPath) || !File.Exists(desktopPath))
                return;

            string fileName = Path.GetFileName(desktopPath);
            ClearSavedShortcut(globalDirectory, fileName);

            string shortcutsDir = Path.Combine(globalDirectory, "Shortcuts");
            Directory.CreateDirectory(shortcutsDir);
            File.Copy(desktopPath, Path.Combine(shortcutsDir, fileName + ".bak"), true);
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return;

                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
            catch
            {
            }
        }

        private static readonly string[] ShortcutGlobs = { "*.lnk", "*.url" };
        private static readonly byte[] ShortcutClsid =
        {
            0x01, 0x14, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00,
            0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46
        };

        private static void RenameSavedShortcutsToBak(string directory)
        {
            if (!Directory.Exists(directory))
                return;

            foreach (string glob in ShortcutGlobs)
            {
                foreach (string file in Directory.GetFiles(directory, glob, SearchOption.TopDirectoryOnly))
                {
                    if (file.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string bak = file + ".bak";
                    if (File.Exists(bak))
                        continue;

                    File.Move(file, bak);
                }
            }
        }

        private static void RestoreBakShortcuts(string shortcutsDir, string desktopPath)
        {
            foreach (string bak in Directory.GetFiles(shortcutsDir, "*.bak", SearchOption.TopDirectoryOnly))
            {
                string name = ShortcutNameFromBak(bak);
                if (name == null)
                    continue;

                byte[] bytes = ReadShortcutBytes(bak);
                if (bytes == null)
                    continue;

                PlaceLiveShortcut(desktopPath, name, bytes);
            }
        }

        private static void NormalizeDesktopShortcuts(string desktopPath)
        {
            if (!Directory.Exists(desktopPath))
                return;

            foreach (string file in Directory.GetFiles(desktopPath))
            {
                string liveName = ShortcutNameFromBak(file);
                if (liveName == null)
                    continue;

                byte[] bytes = ReadShortcutBytes(file);
                if (bytes != null)
                    PlaceLiveShortcut(desktopPath, liveName, bytes);

                try
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                    File.Delete(file);
                }
                catch (Exception ex)
                {
                    SalsaLogger.Error("Shortcut copy failed for " + Path.GetFileName(file) + ": " + ex.Message);
                }
            }
        }

        private static void PlaceLiveShortcut(string folder, string liveName, byte[] bytes)
        {
            if (string.IsNullOrEmpty(liveName) || bytes == null)
                return;

            if (liveName.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
                liveName = ShortcutNameFromBak(liveName);
            if (liveName == null)
                return;

            string dest = Path.Combine(folder, liveName);
            if (SameShortcutFile(dest, bytes))
                return;

            WriteShortcutFile(dest, bytes);
        }

        private static void SyncModifiedShortcuts(string desktopPath, string startMenuPath, string shortcutsDir, string backupDir)
        {
            var onDesktop = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (Directory.Exists(desktopPath))
            {
                foreach (string glob in ShortcutGlobs)
                {
                    foreach (string file in Directory.GetFiles(desktopPath, glob, SearchOption.TopDirectoryOnly))
                    {
                        string name = Path.GetFileName(file);
                        if (name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
                            continue;

                        onDesktop.Add(name);
                        string bak = Path.Combine(shortcutsDir, name + ".bak");
                        byte[] bytes = ReadShortcutBytes(file);
                        if (bytes == null)
                        {
                            byte[] saved = ReadShortcutBytes(bak);
                            if (saved != null)
                            {
                                PlaceLiveShortcut(desktopPath, name, saved);
                                SalsaLogger.Info("Restored shortcut: " + name);
                            }
                            continue;
                        }

                        if (!SameShortcutFile(bak, bytes))
                        {
                            WriteShortcutFile(bak, bytes);
                            SalsaLogger.Info("Saved modified shortcut: " + name + ".bak");
                        }

                        string startMenu = Path.Combine(startMenuPath, name);
                        if (!SameShortcutFile(startMenu, bytes))
                            WriteShortcutFile(startMenu, bytes);

                        string backupBak = Path.Combine(backupDir, name + ".bak");
                        if (File.Exists(backupBak))
                        {
                            File.SetAttributes(backupBak, FileAttributes.Normal);
                            File.Delete(backupBak);
                        }
                    }
                }
            }

            if (!Directory.Exists(desktopPath))
                return;

            foreach (string bak in Directory.GetFiles(shortcutsDir, "*.bak", SearchOption.TopDirectoryOnly))
            {
                string name = ShortcutNameFromBak(bak);
                if (name == null || onDesktop.Contains(name))
                    continue;

                string backupBak = Path.Combine(backupDir, name + ".bak");
                File.SetAttributes(bak, FileAttributes.Normal);
                if (File.Exists(backupBak))
                {
                    File.Delete(bak);
                }
                else
                {
                    File.Move(bak, backupBak);
                    SalsaLogger.Info("Moved deleted shortcut to long-term backup: " + name + ".bak");
                }
            }
        }

        private static string ShortcutNameFromBak(string bakPath)
        {
            string name = Path.GetFileName(bakPath);
            if (name.Length <= 4 || !name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
                return null;

            string shortcut = name.Substring(0, name.Length - 4);
            if (shortcut.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
                || shortcut.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
                return shortcut;

            return null;
        }

        private static byte[] ReadShortcutBytes(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return null;

                byte[] bytes = File.ReadAllBytes(path);
                if (!IsShortcutBytesValid(path, bytes))
                    return null;
                return bytes;
            }
            catch
            {
                return null;
            }
        }

        private static bool IsShortcutBytesValid(string name, byte[] bytes)
        {
            if (bytes == null || bytes.Length < 20)
                return false;

            if (name.EndsWith(".url", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".url.bak", StringComparison.OrdinalIgnoreCase))
            {
                string text = System.Text.Encoding.ASCII.GetString(bytes);
                return text.IndexOf("URL=", StringComparison.OrdinalIgnoreCase) >= 0;
            }

            if (!name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
                && !name.EndsWith(".lnk.bak", StringComparison.OrdinalIgnoreCase))
                return false;

            if (BitConverter.ToUInt32(bytes, 0) != 0x4C)
                return false;

            for (int i = 0; i < ShortcutClsid.Length; i++)
            {
                if (bytes[4 + i] != ShortcutClsid[i])
                    return false;
            }

            return true;
        }

        private static bool SameShortcutFile(string path, byte[] bytes)
        {
            byte[] existing = ReadShortcutBytes(path);
            return existing != null && BytesEqual(existing, bytes);
        }

        private static bool BytesEqual(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length)
                return false;

            for (int i = 0; i < left.Length; i++)
            {
                if (left[i] != right[i])
                    return false;
            }

            return true;
        }

        private static void WriteShortcutFile(string dest, byte[] bytes)
        {
            try
            {
                string destDirectory = Path.GetDirectoryName(dest);
                if (!string.IsNullOrEmpty(destDirectory))
                    Directory.CreateDirectory(destDirectory);

                string temp = dest + ".tmp";
                File.WriteAllBytes(temp, bytes);

                if (File.Exists(dest))
                {
                    File.SetAttributes(dest, FileAttributes.Normal);
                    File.Replace(temp, dest, null);
                }
                else
                {
                    File.Move(temp, dest);
                }
            }
            catch (Exception ex)
            {
                SalsaLogger.Error("Shortcut copy failed for " + Path.GetFileName(dest) + ": " + ex.Message);
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
