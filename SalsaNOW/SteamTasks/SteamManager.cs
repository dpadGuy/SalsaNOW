using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SalsaNOW
{
    internal static class SteamManager
    {
        private static readonly Regex SteamInputPattern = new Regex(
            "(\"(SteamController_[^\"]*Support)\")\\s+\"[01]\"",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static async Task ShutdownServerAsync(string globalDirectory)
        {
            try
            {
                string usgMask = Path.Combine(globalDirectory, "UsgMethod.exe");
                string cache = @"C:\Program Files (x86)\Steam\appcache";

                Task inputTask = ApplySteamInput(SalsaSettings.SteamInput);
                Task usgDownload = DownloadFileAsync("https://salsanowfiles.work/USG/UsgMethod.exe", usgMask);

                SwapSteamUi();
                await Task.WhenAll(inputTask, usgDownload);
                await InstallSteamUiAsync(globalDirectory);
                EndSteamShadow();

                // Steam USG Bypass Part (Temporary until patch discovered)

                // WARNING: Steam must inherit the environment. Do not give this process a cleaned environment
                // unless it is really, really needed. Games it starts inherit whatever this process has.
                Process usg = null;

                if (SalsaSettings.SteamSilentLaunch)
                {
                    usg = Process.Start(new ProcessStartInfo
                    {
                        FileName = usgMask,
                        Arguments = "-silent",
                        UseShellExecute = true
                    });
                }
                else
                {
                    usg = Process.Start(usgMask);
                }

                NativeMethods.ShowWindow(NativeMethods.GetConsoleWindow(), NativeMethods.SW_HIDE);

                await Task.Delay(200);

                if (Directory.Exists(cache)) Directory.Delete(cache, true);

                // Start Startup Batch file if user has it available
                string batch = Path.Combine(globalDirectory, "StartupBatch.bat");
                if (File.Exists(batch)) SteamDetach.StartClean(new ProcessStartInfo { FileName = "cmd.exe", Arguments = "/c \"" + batch + "\"" });

                // UsgMethod.exe is kept after it exits; it is downloaded fresh over it every launch.

                SalsaLogger.Info("Steam Proxy successfully bypassed.");
            }
            catch (Exception ex) { SalsaLogger.Error($"Steam Proxy Shutdown Error: {AppInstaller.DescribeError(ex)}"); }
        }

        // steamui.zip lives at C:\Program Files (x86)\Steam\steamui.zip. Inside it, the only
        // root folder is steamui, so extracting it there fills C:\Program Files (x86)\Steam\steamui.
        // server.exe is not started.
        private static async Task InstallSteamUiAsync(string globalDirectory)
        {
            const string steamUiUrl = "https://salsanowfiles.work/USG/steamui.zip";
            const string steamRoot = @"C:\Program Files (x86)\Steam";
            const string steamUi = steamRoot + @"\steamui";
            string zipPath = Path.Combine(steamRoot, "steamui.zip");
            string extractRoot = Path.Combine(globalDirectory, "steamui.extract");

            using (var client = new WebClient())
            {
                client.Headers.Add("Cache-Control", "no-cache");
                client.Headers.Add("Pragma", "no-cache");
                string url = steamUiUrl + "?nocache=" + DateTime.UtcNow.Ticks;
                await AppInstaller.DownloadAndSwapAsync(client, "steamui.zip", url, zipPath);
            }

            if (Directory.Exists(extractRoot))
                Directory.Delete(extractRoot, true);
            Directory.CreateDirectory(extractRoot);
            ZipFile.ExtractToDirectory(zipPath, extractRoot);

            string packedUi = Path.Combine(extractRoot, "steamui");
            if (!Directory.Exists(packedUi))
                throw new DirectoryNotFoundException("steamui.zip does not contain a steamui folder.");

            CopyDirectory(packedUi, steamUi);
            Directory.Delete(extractRoot, true);
            SalsaLogger.Steam("Installed steamui from steamui.zip.");
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (string file in Directory.GetFiles(source))
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
            foreach (string directory in Directory.GetDirectories(source))
                CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }

        private static void SwapSteamUi()
        {
            const string steamUi = @"C:\Program Files (x86)\Steam\steamui";
            const string steamUiNv = @"C:\Program Files (x86)\Steam\steamuiNV";
            const string steamUiOg = @"C:\Program Files (x86)\Steam\steamuiOG";

            if (Directory.Exists(steamUiNv))
                return;

            using (Process copy = SteamDetach.StartClean(new ProcessStartInfo
            {
                FileName = "robocopy.exe",
                Arguments = "\"" + steamUi + "\" \"" + steamUiOg + "\" /E /COPY:DAT /R:0 /W:0 /MT:16 /NFL /NDL /NJH /NJS /NC /NS",
                UseShellExecute = false,
                CreateNoWindow = true
            }))
            {
                copy?.WaitForExit();
            }

            string copiedChunk = Path.Combine(steamUiOg, "chunk~2dcc5aaf7.js");
            if (File.Exists(copiedChunk))
                File.Delete(copiedChunk);

            Directory.Move(steamUi, steamUiNv);
            Directory.Move(steamUiOg, steamUi);
        }

        // Only Steam.exe whose real path is under I:\user\SteamShadow. Other steam.exe processes stay running.
        private static void EndSteamShadow()
        {
            const string root = @"I:\user\SteamShadow";
            Process[] processes = Process.GetProcessesByName("steam");
            bool ended = false;
            try
            {
                foreach (Process process in processes)
                {
                    try
                    {
                        if (process.HasExited)
                            continue;

                        string path = QueryImagePath(process.Id);
                        if (!IsSteamShadowClient(path, root))
                        {
                            if (string.IsNullOrEmpty(path))
                                SalsaLogger.Steam("Leaving steam.exe PID " + process.Id + ". Its folder could not be checked.");
                            else
                                SalsaLogger.Steam("Leaving steam.exe running from " + path + ".");
                            continue;
                        }

                        int pid = process.Id;
                        process.Kill();
                        process.WaitForExit(5000);
                        ended = true;
                        SalsaLogger.Steam("Ended Steam from SteamShadow (" + path + ", PID " + pid + ").");
                    }
                    catch (Exception ex)
                    {
                        SalsaLogger.Warn("Could not end Steam from SteamShadow: " + ex.Message);
                    }
                }
            }
            finally
            {
                foreach (Process process in processes)
                    process.Dispose();
            }

            if (!ended)
                SalsaLogger.Steam("No Steam from SteamShadow is running.");
        }

        private static bool IsSteamShadowClient(string path, string root)
        {
            if (string.IsNullOrEmpty(path))
                return false;
            if (path.IndexOf(@"\steamapps\", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            return path.StartsWith(root + @"\", StringComparison.OrdinalIgnoreCase) &&
                   path.EndsWith(@"\steam.exe", StringComparison.OrdinalIgnoreCase);
        }

        private static string QueryImagePath(int pid)
        {
            IntPtr handle = OpenProcess(0x1000, false, pid);
            if (handle == IntPtr.Zero)
                return null;
            try
            {
                var buffer = new StringBuilder(1024);
                int size = buffer.Capacity;
                if (!QueryFullProcessImageName(handle, 0, buffer, ref size))
                    return null;
                return buffer.ToString(0, size);
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(int access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        private static async Task DownloadFileAsync(string url, string destination)
        {
            string directory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            // Swapped in only after a full download; a file still in use from an earlier run is kept. If the download
            // fails but an older copy is on disk, use that instead of aborting the whole Steam start.
            string name = Path.GetFileName(destination);
            using (var client = new WebClient())
            {
                client.Headers.Add("Cache-Control", "no-cache");
                try
                {
                    await AppInstaller.DownloadAndSwapAsync(client, name, url, destination);
                }
                catch (Exception ex) when (File.Exists(destination))
                {
                    SalsaLogger.Warn($"Could not update {name} ({AppInstaller.DescribeError(ex)}). Using the copy already on disk.");
                }
            }
        }

        private static async Task ApplySteamInput(bool enabled)
        {
            string userData = @"C:\Program Files (x86)\Steam\userdata";

            if (!Directory.Exists(userData))
                return;

            var files = new List<string>();
            files.AddRange(Directory.GetFiles(userData, "localconfig.vdf", SearchOption.AllDirectories));
            files.AddRange(Directory.GetFiles(userData, "config.vdf", SearchOption.AllDirectories));

            Task[] work = new Task[files.Count];
            for (int i = 0; i < files.Count; i++)
                work[i] = ApplySteamInputFileAsync(files[i], enabled);

            await Task.WhenAll(work);
        }

        private static async Task ApplySteamInputFileAsync(string file, bool enabled)
        {
            try
            {
                string content;
                using (var reader = new StreamReader(file))
                    content = await reader.ReadToEndAsync();

                if (!SteamInputPattern.IsMatch(content))
                    return;

                string wanted = enabled ? "1" : "0";
                string updated = SteamInputPattern.Replace(content, "$1\t\t\"" + wanted + "\"");
                if (updated == content)
                    return;

                using (var writer = new StreamWriter(file, false))
                    await writer.WriteAsync(updated);

                SalsaLogger.Info(enabled
                    ? "Steam Input set to 1 from SalsaNOWSettings.json in " + file
                    : "Steam Input forced off in " + file + " because steamInput is not enabled.");
            }
            catch (Exception ex)
            {
                SalsaLogger.Error("[ERROR] " + file + " -> " + ex.Message);
            }
        }
    }
}