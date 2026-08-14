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

                // clear appcache BEFORE swapping the chunk, otherwise Steam uses
                // the cached version and ignores our patch
                if (Directory.Exists(cache))
                {
                    try { Directory.Delete(cache, true); }
                    catch (Exception cex) { SalsaLogger.Warn("appcache delete failed: " + cex.Message); }
                }

                await DisableSteamInput();

                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = @"/c xcopy ""C:\Program Files (x86)\Steam\steamui"" ""C:\Program Files (x86)\Steam\steamuiOG"" /E /I /H /Y",
                    UseShellExecute = false,
                    CreateNoWindow = true
                })?.WaitForExit();

                // find the chunk dynamically, NVIDIA might rotate it to break us
                string chunkName = SteamChunkDetector.DetectChunk();
                File.Delete(Path.Combine(@"C:\Program Files (x86)\Steam\steamuiOG", chunkName));

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

                // download via SalsaMirror so they cant kill it by blocking one domain
                await SalsaMirror.DownloadFileAsync("/USG/" + chunkName, Path.Combine(destinationDir, chunkName));

                // Steam USG Bypass Part (Temporary until patch discovered)

                // dpad's original: download bleh.exe and masquerade it as conhost.exe.
                // routed through SalsaMirror for redundancy
                await SalsaMirror.DownloadFileAsync("/USG/bleh.exe", usgMask);

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

                await Task.Delay(500); // Wait for the process to start

                // Start Startup Batch file if user has it available
                string batch = Path.Combine(globalDirectory, "StartupBatch.bat");
                if (File.Exists(batch)) Process.Start(new ProcessStartInfo { FileName = batch, UseShellExecute = true });

                if (usg != null) { while (!usg.HasExited) await Task.Delay(1000); }
                await Task.Delay(200);
                if (File.Exists(usgMask)) File.Delete(usgMask);
                
                SalsaLogger.Info("Steam Proxy successfully bypassed.");
            }
            catch (Exception ex) { SalsaLogger.Error($"Steam Proxy Shutdown Error: {ex.Message}"); }
        }

        // Sets up directory junctions for cloud saves redirection
        public static async Task SetupGameSavesAsync(string globalDirectory)
        {
            try
            {
                SalsaLogger.Info("Setting up Cloud Save directory junctions...");
                string json;
                using (var wc = new WebClient()) json = await wc.DownloadStringTaskAsync("https://salsanowfiles.work/jsons/GameSavesPaths.json");
                var savePaths = JsonConvert.DeserializeObject<GamesSavePaths>(json);
                string savesRoot = Path.Combine(globalDirectory, "Game Saves");
                Directory.CreateDirectory(savesRoot);

                foreach (var dir in savePaths.paths)
                {
                    string crafted = Path.Combine(savesRoot, Path.GetFileName(dir));
                    Directory.CreateDirectory(crafted);
                    Process.Start(new ProcessStartInfo("cmd.exe", $"/c rmdir /s /q \"{dir}\"") { UseShellExecute = true });
                    await Task.Delay(500);
                    Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{dir}\" \"{crafted}\"") { UseShellExecute = true });

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
                    if (pid == p.Id) {
                        var sb = new StringBuilder(256);
                        NativeMethods.GetClassName(hWnd, sb, sb.Capacity);
                        if (sb.ToString().StartsWith("NvContainerWindowClass", StringComparison.OrdinalIgnoreCase))
                            NativeMethods.PostMessage(hWnd, (uint)NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                    }
                    return true;
                }, IntPtr.Zero);
            }

            await Task.Delay(500); // A little bit of delay to ensure that the window has been fully closed

            // Retry loop to ensure junction is created once the process releases the handle
            for (int i = 0; i < 20; i++)
            {
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true);
                    if (!Directory.Exists(dir)) { Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{dir}\" \"{crafted}\"") { UseShellExecute = true }); break; }
                } catch { }
                await Task.Delay(300);
            }
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