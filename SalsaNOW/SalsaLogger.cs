using System;
using System.IO;
using System.Net;
using System.Windows.Forms;
using System.Threading;

namespace SalsaNOW
{
    internal static class SalsaLogger
    {
        private static string _logFilePath;

        // Initializes the local log file in the global directory
        public static void Initialize(string globalDirectory)
        {
            _logFilePath = Path.Combine(globalDirectory, "SalsaNOW.log");
            try
            {
                File.WriteAllText(_logFilePath, $"--- SalsaNOW Session Log [{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ---\n");
            }
            catch { }
        }

        public static void Info(string message) => Write("[+]", message, ConsoleColor.Gray);
        public static void Warn(string message) => Write("[!]", message, ConsoleColor.Yellow);
        public static void Error(string message) => Write("[ERR]", message, ConsoleColor.Red);

        // Outputs formatted messages to the console and appends them to the local log file
        private static void Write(string prefix, string message, ConsoleColor color)
        {
            var original = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine($"{prefix} {message}");
            Console.ForegroundColor = original;

            if (!string.IsNullOrEmpty(_logFilePath))
            {
                try { File.AppendAllText(_logFilePath, $"[{DateTime.Now:HH:mm:ss}] {prefix} {message}\n"); }
                catch { } 
            }
        }

        // Uploads crash logs to paste.rs upon fatal exceptions
        public static void UploadLogAndShowError(string fatalErrorMessage)
        {
            // Ensure the fatal error is written to the console and local log
            Error(fatalErrorMessage);

            var crashThread = new Thread(() =>
            {
                string pasteUrl = "Could not be uploaded.";
                try
                {
                    // 1. Construct the crash report payload
                    string logContent = $"--- SalsaNOW CRASH REPORT ---\nTime: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\nError: {fatalErrorMessage}\n\n";
                    
                    if (!string.IsNullOrEmpty(_logFilePath) && File.Exists(_logFilePath))
                    {
                        logContent += "--- LOG HISTORY ---\n" + File.ReadAllText(_logFilePath);
                    }

                    // 2. Upload to paste.rs
                    using (var wc = new WebClient())
                    {
                        // Adding a User-Agent is necessary to prevent the API from blocking the request as spam
                        wc.Headers.Add("User-Agent", "SalsaNOW-CrashReporter");
                        wc.Headers.Add("Content-Type", "text/plain");
                        pasteUrl = wc.UploadString("https://paste.rs/", "POST", logContent).Trim();
                    }
                }
                catch { }

                // Display the fatal error and the crash log link to the user
                string msg = $"SalsaNOW just crashed!\n\nError:\n{fatalErrorMessage}\n\nPlease send this Crashlog to the SalsaNOW Devs:\n{pasteUrl}";
                MessageBox.Show(msg, "SalsaNOW - Fatal Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                
            });
            
            crashThread.SetApartmentState(ApartmentState.STA);
            crashThread.Start();
            crashThread.Join(); // Pauses the execution here until the user closes the popup
        }
    }
}