using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace SalsaNOW
{
    // Owns SalsaSteamStartup.exe when 64-bit Steam (salsaSteam) is on: SalsaNOW starts it, so SalsaNOW alone decides
    // when it is downloaded. The apps list only supplies its url and version (the "SalsaSteam 64-bit" entry) and
    // never downloads it itself.
    //
    // Every launch with the toggle on:
    // 1. Already running (e.g. started by hand): leave it alone, don't download over it, don't start a second one.
    // 2. Download only when the exe is missing, there is no version marker yet, or the apps list has a newer version.
    //    The download lands in a temp file, must be a real exe (MZ header, sane size), and only then replaces the old one.
    // 3. A failed or blocked update keeps the exe already on disk.
    // 4. Start it.
    // With the toggle off, nothing here runs: SalsaSteamStartup.exe is not downloaded, updated or started.
    internal static class SalsaSteamLauncher
    {
        public const string ExeName = "SalsaSteamStartup.exe";
        private const string ProcessName = "SalsaSteamStartup";
        private const string VersionMarker = "SalsaSteamStartup.version";
        private const string InstanceMutexName = @"Local\SalsaSteamStartup.SingleInstance"; // held by SalsaSteamStartup
        private const int MinimumExeSize = 16 * 1024;
        private static readonly TimeSpan EntryTimeout = TimeSpan.FromSeconds(60);

        private static readonly TaskCompletionSource<Apps> entrySource =
            new TaskCompletionSource<Apps>(TaskCreationOptions.RunContinuationsAsynchronously);

        public static bool IsSalsaSteamStartup(Apps app) =>
            app != null && string.Equals(app.exeName, ExeName, StringComparison.OrdinalIgnoreCase);

        // Called by AppInstaller with the "SalsaSteam 64-bit" entry, or null once the apps list is done (or failed)
        // without one. Only the first call counts.
        public static void SetAppEntry(Apps entry) => entrySource.TrySetResult(entry);

        public static async Task RunAsync(string globalDirectory)
        {
            string exe = Path.Combine(globalDirectory, ExeName);

            try
            {
                if (IsRunning())
                {
                    SalsaLogger.Steam("64-bit Steam enabled. SalsaSteamStartup is already running, leaving it alone.");
                    return;
                }

                Apps entry = await WaitForEntryAsync();
                if (entry == null)
                    SalsaLogger.Warn("SalsaSteamStartup is not in the apps list (or the list failed). Using the copy on disk.");
                else
                    await UpdateIfNeededAsync(entry, globalDirectory, exe);

                if (!File.Exists(exe))
                {
                    SalsaLogger.Error("64-bit Steam is enabled, but " + exe + " is missing and could not be downloaded.");
                    return;
                }

                if (entry != null)
                {
                    string desktopPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), entry.name + ".lnk");
                    AppInstaller.EnsureDesktopShortcut(entry.name, globalDirectory, desktopPath, exe, globalDirectory);
                }

                // Checked again: the user may have started it from the shortcut while the update ran.
                if (IsRunning())
                {
                    SalsaLogger.Steam("64-bit Steam enabled. SalsaSteamStartup was started meanwhile, leaving it alone.");
                    return;
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    WorkingDirectory = globalDirectory,
                    UseShellExecute = true
                });
                SalsaLogger.Steam("64-bit Steam enabled. Started " + exe);
            }
            catch (Exception ex)
            {
                SalsaLogger.Error("SalsaSteamStartup failed to start: " + AppInstaller.DescribeError(ex));
            }
            finally
            {
                // Same as the normal Steam path: hide the console and run the user's startup batch.
                NativeMethods.ShowWindow(NativeMethods.GetConsoleWindow(), NativeMethods.SW_HIDE);

                string batch = Path.Combine(globalDirectory, "StartupBatch.bat");
                if (File.Exists(batch))
                {
                    try { Process.Start(new ProcessStartInfo { FileName = batch, UseShellExecute = true }); }
                    catch (Exception ex) { SalsaLogger.Error("StartupBatch.bat failed to start: " + ex.Message); }
                }
            }
        }

        private static async Task<Apps> WaitForEntryAsync()
        {
            Task<Apps> entry = entrySource.Task;
            if (await Task.WhenAny(entry, Task.Delay(EntryTimeout)) == entry)
                return entry.Result;

            SalsaLogger.Warn("The apps list is taking long. Starting SalsaSteamStartup without checking for an update.");
            return null;
        }

        private static async Task UpdateIfNeededAsync(Apps entry, string globalDirectory, string exe)
        {
            string markerPath = Path.Combine(globalDirectory, VersionMarker);
            string remote = (entry.version ?? "").Trim();
            string local = ReadMarker(markerPath);

            string reason = DownloadReason(exe, local, remote);
            if (reason == null)
            {
                SalsaLogger.Steam($"SalsaSteamStartup {local} is up to date.");
                return;
            }

            if (string.IsNullOrWhiteSpace(entry.url))
            {
                SalsaLogger.Error("SalsaSteamStartup has no url in the apps list.");
                return;
            }

            SalsaLogger.Steam($"Downloading SalsaSteamStartup {remote} ({reason}).");
            try
            {
                using (var client = new WebClient())
                {
                    client.Headers.Add("Cache-Control", "no-cache");
                    client.Headers.Add("Pragma", "no-cache");
                    bool replaced = await AppInstaller.DownloadAndSwapAsync(client, "SalsaSteamStartup", entry.url, exe, IsValidExe);
                    if (!replaced)
                        return; // in use: the warning is logged, the current exe is kept and the marker left as is
                }

                WriteMarker(markerPath, remote);
                SalsaLogger.Steam($"SalsaSteamStartup {remote} installed.");
            }
            catch (Exception ex)
            {
                string error = AppInstaller.DescribeError(ex);
                if (File.Exists(exe))
                    SalsaLogger.Warn($"SalsaSteamStartup update failed ({entry.url}): {error}. Using the copy on disk.");
                else
                    SalsaLogger.Error($"SalsaSteamStartup download failed ({entry.url}): {error}");
            }
        }

        // Null when no download is needed, otherwise why.
        private static string DownloadReason(string exe, string local, string remote)
        {
            if (!File.Exists(exe))
                return "not installed";
            if (local == null)
                return "no version marker yet";

            Version remoteVersion, localVersion;
            if (!Version.TryParse(remote, out remoteVersion))
                return null; // unreadable version in the apps list: keep what works
            if (!Version.TryParse(local, out localVersion))
                return "unreadable version marker";

            return Normalize(remoteVersion) > Normalize(localVersion) ? $"update from {local}" : null;
        }

        private static Version Normalize(Version v) =>
            new Version(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

        // A real Windows exe, not an HTML error page or a cut-off download.
        private static bool IsValidExe(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length < MinimumExeSize)
                    return false;

                using (var stream = File.OpenRead(path))
                    return stream.ReadByte() == 'M' && stream.ReadByte() == 'Z';
            }
            catch
            {
                return false;
            }
        }

        private static bool IsRunning()
        {
            try
            {
                Mutex mutex;
                if (Mutex.TryOpenExisting(InstanceMutexName, out mutex))
                {
                    try
                    {
                        // The mutex still exists after a crash. Only a live owner means SalsaSteamStartup is running.
                        if (!mutex.WaitOne(0))
                            return true;

                        mutex.ReleaseMutex();
                    }
                    catch (AbandonedMutexException)
                    {
                        try { mutex.ReleaseMutex(); } catch { }
                    }
                    finally
                    {
                        mutex.Dispose();
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                return true; // exists, just not ours to open
            }
            catch { }

            // Older SalsaSteamStartup builds have no mutex.
            Process[] running = Process.GetProcessesByName(ProcessName);
            foreach (Process p in running) p.Dispose();
            return running.Length > 0;
        }

        private static string ReadMarker(string path)
        {
            try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
            catch { return null; }
        }

        private static void WriteMarker(string path, string version)
        {
            try { File.WriteAllText(path, version); }
            catch (Exception ex) { SalsaLogger.Error($"Could not write {path}: {ex.Message}"); }
        }
    }
}
