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
            DateTime lastPopupTime = DateTime.MinValue;

            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(1000, token);
                    
                    var runningEac = Process.GetProcesses()
                        .Where(p => eacProcessNames.Contains(p.ProcessName, StringComparer.OrdinalIgnoreCase))
                        .ToList();

                    if (runningEac.Any())
                    {
                        foreach (var proc in runningEac)
                        {
                            try 
                            { 
                                if (!proc.HasExited) 
                                {
                                    proc.Kill(); 
                                    SalsaLogger.Warn($"Terminated blocked process: {proc.ProcessName}");
                                }
                            } catch { }
                        }

                        // Prevent popup spam by enforcing a 20-second cooldown
                        if ((DateTime.Now - lastPopupTime).TotalSeconds > 20)
                        {
                            lastPopupTime = DateTime.Now;
                            var thread = new Thread(() => MessageBox.Show("Easy Anti-Cheat is not supported in SalsaNOW sessions and has been terminated to prevent a session crash.", "SalsaNOW - EAC Protection", MessageBoxButtons.OK, MessageBoxIcon.Warning));
                            thread.SetApartmentState(ApartmentState.STA);
                            thread.Start();
                        }
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

                    // 4. Sync Start Menu to Shortcuts (Overwrite MUST be false)
                    try
                    {
                        var lnkFilesStart = Directory.GetFiles(startMenuPath, "*.lnk", SearchOption.AllDirectories);
                        foreach (var file in lnkFilesStart)
                        {
                            string destPath = Path.Combine(shortcutsDir, Path.GetFileName(file));
                            if (!File.Exists(destPath))
                            {
                                try 
                                { 
                                    File.Copy(file, destPath, false); 
                                    SalsaLogger.Info($"Backed up Start Menu shortcut: {Path.GetFileName(file)}");
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
        
        // Monitors Steam userdata for invalid LaunchOptions to prevent session bricking
        public static async Task StartBrickPreventionAsync(CancellationToken token)
        {
            string userData = @"C:\Program Files (x86)\Steam\userdata";
            string blackListed = "\"LaunchOptions\"";

            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(1500, token);
                    if (!Directory.Exists(userData)) continue;

                    var files = Directory.EnumerateFiles(userData, "localconfig.vdf", SearchOption.AllDirectories);
                    foreach (var file in files)
                    {
                        if (File.ReadAllText(file).Contains(blackListed))
                        {
                            File.Delete(file);
                            NativeMethods.ShowWindow(NativeMethods.GetConsoleWindow(), NativeMethods.SW_SHOW);
                            SalsaLogger.Error("STEAM LAUNCH OPTIONS DETECTED. Session terminated.");
                            foreach (var p in Process.GetProcessesByName("steam")) p.Kill();
                        }
                    }
                }
            }
            catch (TaskCanceledException) { }
        }
    }
}