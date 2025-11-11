using IWshRuntimeLibrary;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SalsaNOW
{
    internal class Program
    {
        private static string globalDirectory = "";
        private static string currentPath = Directory.GetCurrentDirectory();

        // Import the FindWindow function from user32.dll
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        // Import the PostMessage function from user32.dll
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern bool EnumChildWindows(IntPtr hwndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(Keys vKey);

        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        const int WM_CLOSE = 0x0010;

        static async Task Main(string[] args)
        {
            // Making sure no SSL/TLS issues occur
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls13;
            ServicePointManager.ServerCertificateValidationCallback += (sender, cert, chain, errors) => true;

            await Startup();
            await AppsInstall();
            await DesktopInstall();
            await SteamServerShutdown();
        }

        static async Task Startup()
        {
            try
            {
                string jsonUrl = "https://github.com/dpadGuy/SalsaNOWThings/raw/refs/heads/main/directory.json";

                if(!Directory.Exists("C:\\Asgard"))
                {
                    Console.WriteLine("[!] SalsaNOW detected the host as not being a GeForce Now enviorment. Exiting...");
                    Thread.Sleep(5000);
                    Environment.Exit(0);
                }

                using (WebClient webClient = new WebClient())
                {
                    string json = await webClient.DownloadStringTaskAsync(jsonUrl);
                    List<SavePath> directory = JsonConvert.DeserializeObject<List<SavePath>>(json);
                    var dir = directory[0];

                    globalDirectory = dir.directoryCreate;
                    Directory.CreateDirectory(dir.directoryCreate);
                    Console.WriteLine($"[!] Main directory created {dir.directoryCreate}");
                }
            }
            catch(Exception ex)
            {
                Console.WriteLine("[!] Error during the Startup process: " + ex.Message);
                Console.ReadKey();
            }
        }
        static async Task AppsInstall()
        {
            try
            {
                string jsonUrl = "https://github.com/dpadGuy/SalsaNOWThings/raw/refs/heads/main/apps.json";

                using (WebClient webClient = new WebClient())
                {
                    string json = await webClient.DownloadStringTaskAsync(jsonUrl);
                    List<Apps> apps = JsonConvert.DeserializeObject<List<Apps>>(json);

                    foreach (var app in apps)
                    {
                        string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop) + $"\\{app.name}.lnk";
                        string zipFile = Path.Combine(globalDirectory, app.name);
                        string appExePath = Path.Combine(globalDirectory, app.exeName);
                        string appZipPath = Path.Combine(globalDirectory, app.name, app.exeName);

                        if (!Directory.Exists(zipFile))
                        {
                            if (app.fileExtension == "zip")
                            {
                                Console.WriteLine("[+] Installing " + app.name);

                                await webClient.DownloadFileTaskAsync(new Uri(app.url), $"{zipFile}.zip");

                                ZipFile.ExtractToDirectory($"{zipFile}.zip", zipFile);

                                WshShell shell = new WshShell();
                                IWshShortcut shortcut = (IWshShortcut)shell.CreateShortcut(desktopPath);
                                shortcut.TargetPath = appZipPath;
                                shortcut.WorkingDirectory = System.IO.Path.GetDirectoryName(appZipPath);

                                shortcut.Save();

                                System.IO.File.Delete($"{zipFile}.zip");

                                if (app.run == "true")
                                {
                                    Process.Start(appZipPath);
                                }
                            }

                            if (app.fileExtension == "exe")
                            {
                                Console.WriteLine("[+] Installing " + app.name);

                                await webClient.DownloadFileTaskAsync(new Uri(app.url), appExePath);

                                WshShell shell = new WshShell();
                                IWshShortcut shortcut = (IWshShortcut)shell.CreateShortcut(desktopPath);
                                shortcut.TargetPath = appExePath;
                                shortcut.WorkingDirectory = System.IO.Path.GetDirectoryName(globalDirectory);

                                shortcut.Save();

                                if (app.run == "true")
                                {
                                    Process.Start(appExePath);
                                }
                            }
                        }
                        else
                        {
                            Console.WriteLine("[!] " + app.name + " Already exists.");

                            if (app.fileExtension == "zip")
                            {
                                WshShell shell = new WshShell();
                                IWshShortcut shortcut = (IWshShortcut)shell.CreateShortcut(desktopPath);
                                shortcut.TargetPath = appZipPath;
                                shortcut.WorkingDirectory = System.IO.Path.GetDirectoryName(globalDirectory);

                                shortcut.Save();

                                if (app.run == "true")
                                {
                                    Process.Start(appZipPath);
                                }
                            }

                            if (app.fileExtension == "exe")
                            {
                                WshShell shell = new WshShell();
                                IWshShortcut shortcut = (IWshShortcut)shell.CreateShortcut(desktopPath);
                                shortcut.TargetPath = appExePath;
                                shortcut.WorkingDirectory = System.IO.Path.GetDirectoryName(globalDirectory);

                                shortcut.Save();

                                if (app.run == "true")
                                {
                                    Process.Start(appExePath);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[!] Error during AppsInstall: " + ex.Message);
                Console.ReadKey();
            }
        }
        static async Task DesktopInstall()
        {
            try
            {
                string jsonUrl = "https://github.com/dpadGuy/SalsaNOWThings/raw/refs/heads/main/desktop.json";
                string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                string shortcutsDir = Path.Combine(globalDirectory, "Shortcuts");

                Directory.CreateDirectory(shortcutsDir);

                using (WebClient webClient = new WebClient())
                {
                    string json = await webClient.DownloadStringTaskAsync(jsonUrl);
                    List<DesktopInfo> desktopInfo = JsonConvert.DeserializeObject<List<DesktopInfo>>(json);

                    IntPtr hWndSeelen = FindWindow(null, "CustomExplorer");

                    // Check if the window handle is valid
                    if (hWndSeelen != IntPtr.Zero)
                    {
                        PostMessage(hWndSeelen, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                    }

                    foreach (var desktops in desktopInfo)
                    {
                        string appDir = Path.Combine(globalDirectory, desktops.name);
                        string zipFile = Path.Combine(globalDirectory, desktops.name + ".zip");
                        string exePath = Path.Combine(appDir, desktops.exeName);
                        string taskbarFixerPath = string.IsNullOrEmpty(desktops.taskbarFixer) ? "" : Path.Combine(appDir, desktops.taskbarFixer);
                        string roamingPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

                        if (!Directory.Exists(appDir))
                        {
                            await webClient.DownloadFileTaskAsync(new Uri(desktops.url), zipFile);

                            ZipFile.ExtractToDirectory(zipFile, appDir);

                            if (desktops.name == "WinXShell_x64")
                            {
                                Process.Start(exePath);
                                Process.Start(taskbarFixerPath);
                            }

                            if (desktops.name == "seelenui")
                            {
                                WebClient webClientConfig = new WebClient();
                                await webClientConfig.DownloadFileTaskAsync(new Uri(desktops.zipConfig), zipFile);

                                try
                                {
                                    ZipFile.ExtractToDirectory(zipFile, $"{roamingPath}\\com.seelen.seelen-ui");
                                }
                                catch
                                {
                                }

                                Process.Start(exePath);
                            }
                        }
                        else
                        {
                            Console.WriteLine("[!] " + desktops.name + " Already exists.");

                            if (desktops.name == "WinXShell_x64")
                            {
                                Process.Start(exePath);
                                Process.Start(taskbarFixerPath);
                            }

                            if (desktops.name == "seelenui")
                            {
                                Process.Start(exePath);
                            }
                        }
                    }
                }

                Thread.Sleep(3000);

                while (true)
                {
                    bool foundAndClosed = false;

                    EnumWindows((hWnd, lParam) =>
                    {
                        EnumChildWindows(hWnd, (child, lp) =>
                        {
                            var sb = new StringBuilder(512);
                            GetWindowText(child, sb, sb.Capacity);
                            if (sb.ToString().Equals("tauri.localhost/settings/index.html", StringComparison.OrdinalIgnoreCase))
                            {
                                SendMessage(child, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                                foundAndClosed = true;
                                Console.WriteLine("Seelen UI startup window has been closed.");
                                return false; // stop enumerating children
                            }
                            return true;
                        }, IntPtr.Zero);

                        return !foundAndClosed; // stop enumerating top-levels if found
                    }, IntPtr.Zero);

                    if (foundAndClosed)
                        break; // exit the loop

                    Thread.Sleep(500); // wait before retrying 
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[!] Error during DesktopInstall: " + ex.Message);
                Console.ReadKey();
            }
        }
        static async Task SteamServerShutdown()
        {
            /* Steam Server (NVIDIA Made Proxy Interceptor for Steam) "127.10.0.231:9753"
             * Steam Server communicates with Steam by proxy and intercepts function calls from Steam by
             * making them not happen or replaces them with special made ones to do something else.
             * Shutting the server down by POST request will lead to all opted-in games on
             * GeForce Now to show up on Steam.
             */

            try 
            {
                string dummyJsonLink = "https://github.com/dpadGuy/SalsaNOWThings/raw/refs/heads/main/kaka.json";
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

                using (WebClient webClient = new WebClient())
                {
                    try
                    {
                        Console.WriteLine("[+] Shutting Down Steam Server");
                        string response = webClient.UploadString("http://127.10.0.231:9753/shutdown", "POST");
                        Console.WriteLine(response);
                    }
                    catch
                    {
                        Console.WriteLine("[!] Steam Server is not running.");
                    }

                    await webClient.DownloadFileTaskAsync(new Uri(dummyJsonLink), $"{globalDirectory}\\kaka.json");
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = @"C:\Program Files (x86)\Steam\lockdown\server\server.exe",
                    Arguments = $"{globalDirectory}\\kaka.json",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                Process.Start(startInfo);

                foreach (var process in Process.GetProcessesByName("steam"))
                {
                    process.Kill();
                }

                Process.Start("steam://open/library");
            }
            catch (Exception ex)
            {
                Console.WriteLine("[!] Error during SteamServerShutdown: " + ex.Message);
                Console.ReadKey();
            }
        }

        public class SavePath
        {
            public string configName { get; set; }
            public string directoryCreate { get; set; }
        }
        public class Apps
        {
            public string name { get; set; }
            public string fileExtension { get; set; }
            public string exeName { get; set; }
            public string run { get; set; }
            public string url { get; set; }
        }
        public class DesktopInfo
        {
            public string name { get; set; }
            public string exeName { get; set; }
            public string taskbarFixer { get; set; }
            public string zipConfig { get; set; }
            public string run { get; set; }
            public string url { get; set; }
        }
    }
}
