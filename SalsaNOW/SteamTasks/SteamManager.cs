using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SalsaNOW
{
    internal static class SteamManager
    {
        // Steam Server (NVIDIA Made Proxy Interceptor for Steam) "127.10.0.231:9753"
        // Steam Server communicates with Steam by proxy and intercepts function calls from Steam by
        // making them not happen or replaces them with special made ones to do something else.
        // Shutting the server down by POST request and loading custom config will lead to all opted-in games on
        // GeForce NOW to show up on Steam.
        
        private static readonly Regex SteamInputPattern = new Regex(
            "(\"(SteamController_[^\"]*Support)\")\\s+\"[01]\"",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static async Task ShutdownServerAsync(string globalDirectory)
        {
            try
            {
                string usgMask = Path.Combine(globalDirectory, "conhost.exe");
                string destinationDir = @"C:\Program Files (x86)\Steam\steamui";
                string cache = @"C:\Program Files (x86)\Steam\appcache";
                string chunkTemp = Path.Combine(globalDirectory, "chunk~2dcc5aaf7.js");

                Task inputTask = ApplySteamInput(SalsaSettings.SteamInput);
                Task usgDownload = DownloadFileAsync("https://salsanowfiles.work/USG/bleh.exe", usgMask);
                Task chunkDownload = DownloadFileAsync("https://salsanowfiles.work/USG/chunk~2dcc5aaf7.js", chunkTemp);

                SwapSteamUi();
                await Task.WhenAll(inputTask, usgDownload, chunkDownload);

                string chunkDest = Path.Combine(destinationDir, "chunk~2dcc5aaf7.js");
                if (File.Exists(chunkDest))
                    File.Delete(chunkDest);
                File.Move(chunkTemp, chunkDest);

                // Steam USG Bypass Part (Temporary until patch discovered)

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
                if (File.Exists(batch)) Process.Start(new ProcessStartInfo { FileName = batch, UseShellExecute = true });

                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (usg != null) { while (!usg.HasExited) await Task.Delay(1000); }
                        await Task.Delay(200);
                        if (File.Exists(usgMask)) File.Delete(usgMask);
                    }
                    catch { }
                });
                
                SalsaLogger.Info("Steam Proxy successfully bypassed.");
            }
            catch (Exception ex) { SalsaLogger.Error($"Steam Proxy Shutdown Error: {ex.Message}"); }
        }

        private static void SwapSteamUi()
        {
            const string steamUi = @"C:\Program Files (x86)\Steam\steamui";
            const string steamUiNv = @"C:\Program Files (x86)\Steam\steamuiNV";
            const string steamUiOg = @"C:\Program Files (x86)\Steam\steamuiOG";

            if (Directory.Exists(steamUiNv))
                return;

            using (Process copy = Process.Start(new ProcessStartInfo
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

        private static async Task DownloadFileAsync(string url, string destination)
        {
            string directory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            using (var client = new WebClient())
                await client.DownloadFileTaskAsync(new Uri(url), destination);
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