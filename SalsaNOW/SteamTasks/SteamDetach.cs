using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SalsaNOW
{
    internal class SteamDetach
    {
        private const string MarkerFileName =
            ".salsanow-detached";

        public static void RelaunchIfNeeded(string[] originalArguments)
        {
            string executablePath =
                Process.GetCurrentProcess().MainModule.FileName;

            if (string.IsNullOrEmpty(executablePath))
            {
                throw new InvalidOperationException(
                    "Could not determine the executable path.");
            }

            string executableDirectory =
                Path.GetDirectoryName(executablePath);

            if (string.IsNullOrEmpty(executableDirectory))
            {
                throw new InvalidOperationException(
                    "Could not determine the executable directory.");
            }

            string markerPath = Path.Combine(
                executableDirectory,
                MarkerFileName);

            /*
             * If the marker exists, this is the restarted instance.
             * Remove the marker and continue normal execution.
             */
            if (File.Exists(markerPath))
            {
                File.Delete(markerPath);
                return;
            }

            /*
             * Create and completely close the marker before restarting.
             */
            using (FileStream marker = new FileStream(
                markerPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read))
            {
                byte[] contents = Encoding.UTF8.GetBytes(
                    DateTime.UtcNow.ToString("O"));

                marker.Write(contents, 0, contents.Length);
                marker.Flush(true);
            }

            try
            {
                RelaunchFromCommandShell(
                    executablePath,
                    executableDirectory,
                    originalArguments);

                /*
                 * Give cmd.exe time to receive and process the command
                 * before terminating this instance.
                 */
                Thread.Sleep(750);
            }
            catch
            {
                TryDeleteMarker(markerPath);
                throw;
            }

            /*
             * Terminate the original Steam-owned instance.
             */
            Environment.Exit(0);
        }

        private static void RelaunchFromCommandShell(
            string executablePath,
            string executableDirectory,
            string[] originalArguments)
        {
            string commandProcessor =
                Environment.GetEnvironmentVariable("ComSpec");

            if (string.IsNullOrEmpty(commandProcessor))
            {
                commandProcessor = Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.Windows),
                    "System32",
                    "cmd.exe");
            }

            var command = new StringBuilder();

            command.Append("start \"\" ");
            command.Append("/d ");
            command.Append(QuoteForCmd(executableDirectory));
            command.Append(" ");
            command.Append(QuoteForCmd(executablePath));

            /*
             * Forward the original SalsaNOW arguments.
             */
            if (originalArguments != null)
            {
                foreach (string argument in originalArguments)
                {
                    command.Append(" ");
                    command.Append(QuoteForCmd(argument));
                }
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = commandProcessor,
                Arguments = "/d /c " + command,
                WorkingDirectory = executableDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            Process shellProcess = Process.Start(startInfo);

            if (shellProcess == null)
            {
                throw new InvalidOperationException(
                    "Could not start the command shell.");
            }
        }

        private static string QuoteForCmd(string value)
        {
            if (value == null)
                value = "";

            /*
             * Escape characters interpreted by cmd.exe.
             * Percent signs must be doubled to prevent environment-variable
             * expansion. Quotes are escaped for the command line.
             */
            value = value
                .Replace("%", "%%")
                .Replace("\"", "\\\"");

            return "\"" + value + "\"";
        }

        private static void TryDeleteMarker(string markerPath)
        {
            try
            {
                if (File.Exists(markerPath))
                    File.Delete(markerPath);
            }
            catch
            {
                // Ignore cleanup errors while preserving the launch error.
            }
        }
    }
}
