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

            string salsaNowTitle = "SalsaNOW V1.6.9.3-hotfix5 - by dpadGuy";

            Console.Title = salsaNowTitle;

            for (int i = 0; i < args.Length; i++)
            {
                if ((args[i] == "--apps-json" || args[i] == "-a") && i + 1 < args.Length)
                {
                    customAppsJsonPath = args[i + 1]; i++;
                }
            }

            Console.WriteLine(salsaNowTitle);
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

            // One SalsaNOW per session. A second one would re-download conhost.exe, server.exe and
            // SalsaSteamStartup.exe while the first one's copies are running, and start Steam a second time.
            // Checked after the recovery prompt so recovery mode stays reachable while SalsaNOW runs.
            if (!TryAcquireSingleInstance())
            {
                Console.WriteLine("[!] SalsaNOW is already running. Exiting...");
                await Task.Delay(3000); Environment.Exit(0);
            }

            await Startup();

            // Load configuration once to share settings across modules
            SalsaSettings.Load();

            _ = Task.Run(() => BackgroundTasks.EnvironmentSetup());

            // Apply registry changes and backup desktop registry
            _ = AutoPersist.BackupDesktopRegistry(cts.Token, globalDirectory);
            _ = AutoPersist.ApplyCustomRegistryFiles(globalDirectory);
            _ = AutoPersist.SetupGameSavesAsync(globalDirectory);

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
                StartSteamAsync(globalDirectory)
            );

            await installs;
            await FinalBackgroundTasks.FinalTasks(globalDirectory, cts.Token);

            try { await Task.Delay(Timeout.Infinite, cts.Token); } catch (TaskCanceledException) { }
        }

        // salsaSteam off (default): points Steam registry entries back at the original Steam if they still name
        // SalsaSteam, leaves SalsaSteam games where they are, then starts normal Steam the usual way
        // (conhost.exe / bleh.exe, modified server, steamui swap).
        // salsaSteam on: none of that. SalsaSteam's games and registry are left alone and SalsaSteamLauncher
        // updates (when needed) and starts SalsaSteamStartup.exe, which launches 64-bit SalsaSteam.
        static async Task StartSteamAsync(string globalDirectory)
        {
            if (SalsaSettings.SalsaSteam)
            {
                await SalsaSteamLauncher.RunAsync(globalDirectory);
                return;
            }

            await SalsaSteamRevert.RunAsync();
            await SteamManager.ShutdownServerAsync(globalDirectory);
        }

        // Held for the whole run so a second SalsaNOW can tell one is already running.
        private const string InstanceMutexName = @"Local\SalsaNOW.SingleInstance";
        private static Mutex instanceMutex;

        static bool TryAcquireSingleInstance()
        {
            try
            {
                instanceMutex = new Mutex(false, InstanceMutexName);
                return instanceMutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                return true; // the previous SalsaNOW crashed without releasing it; this run owns it now
            }
            catch
            {
                return true; // can't check: run normally rather than never starting
            }
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