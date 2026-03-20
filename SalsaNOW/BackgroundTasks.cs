using System;
using System.Diagnostics;
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

            // 1. Initial Sync: Throw saved icons onto the fresh Desktop immediately
            try
            {
                var allFiles = Directory.GetFiles(shortcutsDir, "*.lnk", SearchOption.AllDirectories);
                foreach (string shortcut in allFiles)
                {
                    File.Copy(shortcut, Path.Combine(desktopPath, Path.GetFileName(shortcut)), true);
                }
                SalsaLogger.Info("Initial Desktop shortcut sync completed.");
            }
            catch (Exception ex) { SalsaLogger.Error($"Initial shortcut sync failed: {ex.Message}"); }

            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(5000, token);

                    // 2. Protect core components from user deletion
                    RestoreShortcut(desktopPath, shortcutsDir, backupDir, "PeaZip File Explorer Archiver.lnk");
                    RestoreShortcut(desktopPath, shortcutsDir, backupDir, "System Informer.lnk");

                    // 3. Sync Desktop to Shortcuts (Overwrite MUST be false to prevent corrupting existing backups)
                    try
                    {
                        var lnkFilesDesktop = Directory.GetFiles(desktopPath, "*.lnk", SearchOption.AllDirectories);
                        foreach (var file in lnkFilesDesktop)
                        {
                            string destPath = Path.Combine(shortcutsDir, Path.GetFileName(file));
                            if (!File.Exists(destPath))
                            {
                                try 
                                { 
                                    File.Copy(file, destPath, false); 
                                    SalsaLogger.Info($"Backed up new shortcut: {Path.GetFileName(file)}");
                                } 
                                catch { }
                            }
                        }
                    }
                    catch { }

                    // 4. Sync Shortcuts To Start Menu
                    try
                    {
                        var lnkFilesStart = Directory.GetFiles(shortcutsDir, "*.lnk", SearchOption.AllDirectories);
                        foreach (var file in lnkFilesStart)
                        {
                            string destPath = Path.Combine(startMenuPath, Path.GetFileName(file));
                            if (!File.Exists(destPath))
                            {
                                try 
                                { 
                                    if (!Directory.Exists(startMenuPath)) Directory.CreateDirectory(startMenuPath);
                                    File.Copy(file, destPath, false); 
                                    SalsaLogger.Info($"Copied shortcut over to Start Menu: {Path.GetFileName(file)}");
                                } 
                                catch { }
                            }
                        }
                    }
                    catch { }

                    // 5. Cleanup: Move deleted shortcuts from the primary folder to the long-term backup
                    try
                    {
                        var lnkFilesBackup = Directory.GetFiles(shortcutsDir, "*.lnk", SearchOption.AllDirectories);
                        foreach (var backupFile in lnkFilesBackup)
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

        // Restores a specific shortcut from either the primary or backup directory
        private static void RestoreShortcut(string desktop, string shortcuts, string backup, string name)
        {
            string targetDesktopPath = Path.Combine(desktop, name);
            if (!File.Exists(targetDesktopPath))
            {
                string sourcePath = Path.Combine(shortcuts, name);
                if (!File.Exists(sourcePath)) sourcePath = Path.Combine(backup, name);

                if (File.Exists(sourcePath))
                {
                    try 
                    { 
                        File.Copy(sourcePath, targetDesktopPath); 
                        SalsaLogger.Warn($"Restored missing core component: {name}");
                        new Thread(() => MessageBox.Show($"{Path.GetFileNameWithoutExtension(name)} is a core component and cannot be removed.", "SalsaNOW", MessageBoxButtons.OK, MessageBoxIcon.Information)).Start();
                    } 
                    catch { }
                }
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
        
        // Monitors Steam userdata for file "localconfig.vdf" and removes it if available in order to prevent accidental bricking
        public static async Task StartBrickPreventionAsync(CancellationToken token)
        {
            string userData = @"C:\Program Files (x86)\Steam\userdata";

            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(1000, token);
                    if (!Directory.Exists(userData)) continue;

                    var files = Directory.EnumerateFiles(userData, "localconfig.vdf", SearchOption.AllDirectories);
                    foreach (var file in files)
                    {
                        if (File.Exists(file))
                        {
                            File.Delete(file);
                        }
                    }
                }
            }
            catch (TaskCanceledException) { }
        }
    }
}
