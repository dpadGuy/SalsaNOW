using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace SalsaNOW
{
    // Owns SalsaSteamStartup.exe. SalsaNOW installs it on every launch and puts its shortcut on the desktop.
    // The apps list only supplies its url and version (the "SalsaSteam 64-bit" entry) and never downloads it itself.
    //
    // Every launch:
    // 1. Download only when the exe is missing, there is no version marker yet, or the apps list has a newer version.
    //    The download lands in a temp file, must be a real exe (MZ header, sane size), and only then replaces the old one.
    // 2. A failed or blocked update keeps the exe already on disk.
    // 3. Make the desktop shortcut.
    // With salsaSteam on, it is also started. Already running (e.g. started by hand): leave it alone, don't start a second one.
    // With the toggle off, it is installed and the shortcut is made, but it is not started.
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

        // Downloads or updates SalsaSteamStartup.exe and makes its desktop shortcut. Does not start it.
        public static async Task InstallAsync(string globalDirectory)
        {
            string exe = Path.Combine(globalDirectory, ExeName);

            try
            {
                Apps entry = await WaitForEntryAsync();
                if (entry == null)
                    SalsaLogger.Warn("SalsaSteamStartup is not in the apps list (or the list failed). Using the copy on disk.");
                else
                    await UpdateIfNeededAsync(entry, globalDirectory, exe);

                if (!File.Exists(exe))
                {
                    SalsaLogger.Error("SalsaSteamStartup is missing and could not be downloaded: " + exe);
                    return;
                }

                string name = entry != null && !string.IsNullOrWhiteSpace(entry.name) ? entry.name : "SalsaSteam";
                string desktopPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), name + ".lnk");
                AppInstaller.EnsureDesktopShortcut(name, globalDirectory, desktopPath, exe, globalDirectory);
            }
            catch (Exception ex)
            {
                SalsaLogger.Error("SalsaSteamStartup install failed: " + AppInstaller.DescribeError(ex));
            }
        }

        public static async Task RunAsync(string globalDirectory)
        {
            string exe = Path.Combine(globalDirectory, ExeName);

            try
            {
                await InstallAsync(globalDirectory);

                if (!File.Exists(exe))
                    return;

                if (IsRunning())
                {
                    SalsaLogger.Steam("64-bit Steam enabled. SalsaSteamStartup is already running, leaving it alone.");
                    return;
                }

                // Its own console. SalsaNOW does not inherit the handles and does not read the output.
                StartInNewConsole(exe, globalDirectory);
                SalsaLogger.Steam("64-bit Steam enabled. Started " + exe + " in a new window.");
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

            SalsaLogger.Warn("The apps list is taking long. Installing SalsaSteamStartup without checking for an update.");
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

        private const uint CREATE_NEW_CONSOLE = 0x00000010;
        private const uint CREATE_NEW_PROCESS_GROUP = 0x00000200;

        // CREATE_NEW_CONSOLE gives SalsaSteamStartup its own window. Handles are not inherited, so its output
        // never arrives on SalsaNOW's console and nothing here reads it.
        private static void StartInNewConsole(string exe, string workingDirectory)
        {
            var startup = new STARTUPINFO();
            startup.cb = Marshal.SizeOf(startup);

            PROCESS_INFORMATION process;
            string commandLine = "\"" + exe + "\"";
            if (!CreateProcess(exe, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                CREATE_NEW_CONSOLE | CREATE_NEW_PROCESS_GROUP, IntPtr.Zero, workingDirectory, ref startup, out process))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            CloseHandle(process.hThread);
            CloseHandle(process.hProcess);
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcess(
            string applicationName,
            string commandLine,
            IntPtr processAttributes,
            IntPtr threadAttributes,
            bool inheritHandles,
            uint creationFlags,
            IntPtr environment,
            string currentDirectory,
            ref STARTUPINFO startupInfo,
            out PROCESS_INFORMATION processInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
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
