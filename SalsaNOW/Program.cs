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

            Console.Title = "SalsaNOW V1.6.9.1 - by dpadGuy";

            for (int i = 0; i < args.Length; i++)
            {
                if ((args[i] == "--apps-json" || args[i] == "-a") && i + 1 < args.Length)
                {
                    customAppsJsonPath = args[i + 1]; i++;
                }
            }

            Console.WriteLine("SalsaNOW V1.6.9.1");
            Console.WriteLine("IF YOU HAVE PAID FOR SALSANOW ACCESS THEN IT MEANS YOU GOT SCAMMED AND SHOULD DEMAND YOUR MONEY BACK IMMEDIATELY.");
            Console.WriteLine("");

            if (!Directory.Exists(@"C:\Asgard"))
            {
                Console.WriteLine("[!] Not a GeForce NOW environment. Exiting...");
                await Task.Delay(5000); Environment.Exit(0);
            }

            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls13;
            ServicePointManager.ServerCertificateValidationCallback += (sender, cert, chain, errors) => true;
            ServicePointManager.DefaultConnectionLimit = 32;
            ServicePointManager.Expect100Continue = false;
            ServicePointManager.UseNagleAlgorithm = false;

            // Recovery mode prompt
            const string text = "Press DEL key for recovery mode";
            DateTime start = DateTime.Now;
            DateTime end = start.AddSeconds(1.5);

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

            await Startup();

            // Load configuration once to share settings across modules
            SalsaSettings.Load();

            _ = Task.Run(() => BackgroundTasks.EnvironmentSetup());

            // Apply registry changes and backup desktop registry
            _ = AutoPersist.BackupDesktopRegistry(cts.Token, globalDirectory);
            _ = AutoPersist.ApplyCustomRegistryFiles(globalDirectory);
            _ = Task.Run(async () =>
            {
                await AutoPersist.SetupGameSavesAsync(globalDirectory);
                await CloudSaveManager.SetupAsync(globalDirectory);
            });

            // Fire and forget non-blocking background services
            _ = BackgroundTasks.StartShortcutsSavingAsync(globalDirectory, cts.Token);
            _ = BackgroundTasks.StartTerminateGFNExplorerShellAsync(cts.Token);
            _ = BackgroundTasks.StartEacWatcherAsync(cts.Token);
            _ = BackgroundTasks.StartBrickPreventionAsync(cts.Token);
            _ = DotNetInstaller.StartDotNetInstallAsync(cts.Token);
            _ = Task.Run(() => NvidiaManager.EnableRTX());

            Task installs = Task.WhenAll(
                DesktopInstaller.DesktopInstallAsync(globalDirectory),
                AppInstaller.AppsInstallAsync(globalDirectory, customAppsJsonPath),
                AppInstaller.AppsInstallSilentAsync(globalDirectory),
                SteamManager.ShutdownServerAsync(globalDirectory)
            );

            await installs;
            await FinalBackgroundTasks.FinalTasks(globalDirectory, cts.Token);

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