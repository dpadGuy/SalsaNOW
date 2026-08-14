using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using Newtonsoft.Json;

namespace SalsaNOW
{
    internal class Program
    {
        private static string globalDirectory = "";
        private static string currentPath = Directory.GetCurrentDirectory();
        private static readonly CancellationTokenSource cts = new CancellationTokenSource();
        private static string customAppsJsonPath = null;

        [STAThread]
        static async Task Main(string[] args)
        {
            // Clean steam environment before everything.
            // WE LEAVE THIS MANDATORY HERE DON'T MOVE OR DELETE.
            SteamDetach.RemoveSteamEnvironments();

            Console.Title = "SalsaNOW V1.6.7.2 - by dpadGuy";

            for (int i = 0; i < args.Length; i++)
            {
                if ((args[i] == "--apps-json" || args[i] == "-a") && i + 1 < args.Length)
                {
                    customAppsJsonPath = args[i + 1]; i++;
                }
            }

            Console.WriteLine("SalsaNOW V1.6.7.2");
            Console.WriteLine("IF YOU HAVE PAID FOR SALSANOW ACCESS THEN IT MEANS YOU GOT SCAMMED AND SHOULD DEMAND YOUR MONEY BACK IMMEDIATELY.");
            Console.WriteLine("");

            if (!Directory.Exists(@"C:\Asgard"))
            {
                Console.WriteLine("[!] Not a GeForce NOW environment. Exiting...");
                await Task.Delay(5000); Environment.Exit(0);
            }

            // Recovery mode prompt
            const string text = "Press DEL key for recovery mode";
            DateTime start = DateTime.Now;
            DateTime end = start.AddSeconds(3);

            while (DateTime.Now < end)
            {
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(true);

                    if (key.Key == ConsoleKey.Delete)
                    {
                        // Clear the prompt line
                        Console.Write("\r" + new string(' ', Console.BufferWidth - 1) + "\r");

                        Console.WriteLine("Recovery mode selected.");
                        await RecoveryMode.ShowRecoveryPrompt();
                        return;
                    }
                }

                int dots = Math.Min((int)(DateTime.Now - start).TotalSeconds + 1, 3);

                Console.Write($"\r{text}{new string('.', dots)}");

                Thread.Sleep(10);
            }

            // Clear the prompt line before continuing
            Console.Write("\r" + new string(' ', Console.BufferWidth - 1) + "\r");

            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls13;
            ServicePointManager.ServerCertificateValidationCallback += (sender, cert, chain, errors) => true;

            await Startup();

            // Load configuration once to share settings across modules
            SalsaSettings.Load(globalDirectory);

            _ = Task.Run(() => BackgroundTasks.EnvironmentSetup());

            // Apply registry changes and backup desktop registry
            _ = AutoPersist.BackupDesktopRegistry(cts.Token, globalDirectory);
            _ = AutoPersist.ApplyCustomRegistryFiles(globalDirectory);

            // Fire and forget non-blocking background services
            _ = BackgroundTasks.StartShortcutsSavingAsync(globalDirectory, cts.Token);
            _ = BackgroundTasks.StartTerminateGFNExplorerShellAsync(cts.Token);
            _ = BackgroundTasks.StartEacWatcherAsync(cts.Token);
            _ = BackgroundTasks.StartBrickPreventionAsync(cts.Token);

            await SteamManager.SetupGameSavesAsync(globalDirectory);

            _ = SteamManager.ShutdownServerAsync(globalDirectory);

            // Execute deployment modules
            await AppInstaller.AppsInstallAsync(globalDirectory, customAppsJsonPath);
            await AppInstaller.AppsInstallSilentAsync(globalDirectory);
            await AppInstaller.DesktopInstallAsync(globalDirectory);

            // Apply Nvidia optimizations always.
            // Harden: RTX enablement runs against vendor drivers and can throw if the
            // GPU/NvAPI is unavailable in the GFN sandbox; wrap it so a driver failure
            // never aborts the rest of startup.
            try { NvidiaManager.EnableRTX(); }
            catch (Exception ex) { SalsaLogger.Error("NVIDIA optimization failed: " + ex.Message); }

            NativeMethods.ShowWindow(NativeMethods.GetConsoleWindow(), NativeMethods.SW_HIDE);

            await BackgroundTasks.OpenShellStartup(globalDirectory);

            try { await Task.Delay(Timeout.Infinite, cts.Token); } catch (TaskCanceledException) { }
        }

        static async Task Startup()
        {
            try
            {
                using (var wc = new WebClient())
                {
                    var dir = JsonConvert.DeserializeObject<System.Collections.Generic.List<SavePath>>(await wc.DownloadStringTaskAsync("https://salsanowfiles.work/jsons/directory.json"))[0];
                    globalDirectory = dir.directoryCreate;
                    Directory.CreateDirectory(globalDirectory);
                    
                    // Initialize Logger here so it knows the global directory path
                    SalsaLogger.Initialize(globalDirectory);
                    SalsaLogger.Info($"Main directory created {globalDirectory}");
                    
                    string cfg = Path.Combine(globalDirectory, "SalsaNOWConfig.ini");
                    if (!System.IO.File.Exists(cfg)) await wc.DownloadFileTaskAsync(new Uri("https://salsanowfiles.work/jsons/SalsaNOWConfig.ini"), cfg);
                }
            }
            // Upload Crashlogs to paste.rs and show the user a link to forward to the Devs
            catch (Exception ex) 
            { 
                SalsaLogger.UploadLogAndShowError(ex.Message);
                Environment.Exit(0);
            }
        }
    }
}