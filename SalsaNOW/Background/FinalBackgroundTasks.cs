using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SalsaNOW
{
    internal static class FinalBackgroundTasks
    {
        public static async Task OpenShellStartup(string globalDirectory)
        {
            await CloseStartHookWindowAsync();
            await Task.Delay(3000);

            try
            {
                ApplyFileAssociations(globalDirectory);
                ApplyDesktopContextMenus(globalDirectory);
                CreateSteamDesktopShortcut(globalDirectory);
                PinOpenShellShortcuts();
                ResetExplorerPlusPlus();
                StartOpenShell(globalDirectory);
                ShowFirstRunNotice(globalDirectory);
            }
            catch (Exception ex)
            {
                SalsaLogger.Error("Desktop finalization failed: " + ex.Message);
            }
        }

        private static async Task CloseStartHookWindowAsync()
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(30);

            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    await Task.Delay(500);
                    IntPtr window = NativeMethods.FindWindowByCaption(IntPtr.Zero, "StartHookWindow");
                    if (window != IntPtr.Zero)
                    {
                        NativeMethods.SendMessage(window, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                        SalsaLogger.Info("StartHookWindow has been closed.");
                        return;
                    }
                }
                catch { }
            }

            SalsaLogger.Warn("StartHookWindow not found.");
        }

        private static void ApplyFileAssociations(string globalDirectory)
        {
            string nppPath = Path.Combine(globalDirectory, "Notepad++", "notepad++.exe");
            string peazipPath = Path.Combine(globalDirectory, "PeaZip File Explorer Archiver", "PeaZip File Explorer Archiver.exe");

            string setUserFta = Path.Combine(globalDirectory, "SilentApps", "SetUserFTA.exe");
            if (!File.Exists(setUserFta))
            {
                SalsaLogger.Warn("SetUserFTA.exe not found. Skipping file associations.");
                return;
            }

            RegisterOpenCommand(@"Software\Classes\NPP.Custom\shell\open\command", nppPath);
            RegisterOpenCommand(@"Software\Classes\PZ.Custom\shell\open\command", peazipPath);

            string[] notepadExtensions =
            {
                ".txt", ".log", ".err",
                ".ini", ".conf", ".cfg",
                ".xml", ".json", ".yaml", ".yml",
                ".md"
            };

            string[] peazipExtensions =
            {
                ".zip", ".7z", ".rar",
                ".tar", ".gz", ".tgz", ".bz2", ".xz"
            };

            foreach (string extension in notepadExtensions)
                SetUserFta(setUserFta, extension, "NPP.Custom");

            foreach (string extension in peazipExtensions)
                SetUserFta(setUserFta, extension, "PZ.Custom");

            SalsaLogger.Info("File associations applied.");
        }

        private static void RegisterOpenCommand(string keyPath, string exePath)
        {
            if (!File.Exists(exePath))
            {
                SalsaLogger.Warn("Skipping file association command, missing: " + exePath);
                return;
            }

            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(keyPath))
            {
                if (key == null)
                    return;

                key.SetValue("", "\"" + exePath + "\" \"%1\"", RegistryValueKind.String);
            }
        }

        private static void SetUserFta(string setUserFta, string extension, string progId)
        {
            try
            {
                using (var process = Process.Start(new ProcessStartInfo
                {
                    FileName = setUserFta,
                    Arguments = extension + " " + progId,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                }))
                {
                    process?.WaitForExit();
                }
            }
            catch (Exception ex)
            {
                SalsaLogger.Error("SetUserFTA " + extension + " failed: " + ex.Message);
            }
        }

        private static void ApplyDesktopContextMenus(string globalDirectory)
        {
            AddBackgroundMenuCommand(
                "ChangeWallpaper",
                "Change Wallpaper (USE THIS)",
                Path.Combine(globalDirectory, "SilentApps", "SalsaChangeWallpaper.exe"));

            AddBackgroundMenuCommand(
                "RestartSteam",
                "Restart Steam",
                Path.Combine(globalDirectory, "SilentApps", "RestartSteam.exe"));

            SalsaLogger.Info("Desktop context menus applied.");
        }

        private static void AddBackgroundMenuCommand(string keyName, string menuName, string exePath)
        {
            if (!File.Exists(exePath))
            {
                SalsaLogger.Warn("Skipping context menu " + menuName + ", missing: " + exePath);
                return;
            }

            string keyPath = @"Software\Classes\Directory\Background\shell\" + keyName;

            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(keyPath))
            {
                if (key == null)
                    return;

                key.SetValue("MUIVerb", menuName, RegistryValueKind.String);
                key.SetValue("Position", "Bottom", RegistryValueKind.String);
            }

            using (RegistryKey command = Registry.CurrentUser.CreateSubKey(keyPath + @"\command"))
            {
                command?.SetValue("", "\"" + exePath + "\"", RegistryValueKind.String);
            }
        }

        private static void CreateSteamDesktopShortcut(string globalDirectory)
        {
            if (File.Exists(Path.Combine(globalDirectory, "Backup Shortcuts", "Steam.lnk"))
                || File.Exists(Path.Combine(globalDirectory, "Shortcuts", "Steam.lnk")))
            {
                SalsaLogger.Info("Steam shortcut already saved. Skipping desktop create.");
                return;
            }

            const string steamExe = @"C:\Program Files (x86)\Steam\steam.exe";
            const string steamDir = @"C:\Program Files (x86)\Steam";
            string desktopLnk = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                "Steam.lnk");

            AppInstaller.CreateShortcut("Steam", desktopLnk, steamExe, steamDir);
            SalsaLogger.Info("Steam desktop shortcut created.");
        }

        private static void PinOpenShellShortcuts()
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string pinned = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "OpenShell",
                "Pinned");

            Directory.CreateDirectory(pinned);

            string[] shortcuts =
            {
                "7-Zip.lnk",
                "Brave.lnk",
                "Explorer++.lnk",
                "Notepad++.lnk",
                "PeaZip File Explorer Archiver.lnk",
                "Steam.lnk",
                "System Informer.lnk"
            };

            foreach (string shortcut in shortcuts)
            {
                string source = Path.Combine(desktop, shortcut);
                string dest = Path.Combine(pinned, shortcut);

                if (!File.Exists(source) || File.Exists(dest))
                    continue;

                try
                {
                    File.Copy(source, dest);
                    SalsaLogger.Info("Pinned Open-Shell shortcut: " + shortcut);
                }
                catch (Exception ex)
                {
                    SalsaLogger.Error("Failed to pin " + shortcut + ": " + ex.Message);
                }
            }
        }

        private static void ResetExplorerPlusPlus()
        {
            const string keyPath = @"Software\Explorer++";

            using (RegistryKey existing = Registry.CurrentUser.OpenSubKey(keyPath))
            {
                if (existing == null)
                    return;
            }

            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(keyPath);
                SalsaLogger.Info("Removed Explorer++ registry key.");
            }
            catch (Exception ex)
            {
                SalsaLogger.Error("Failed to remove Explorer++ registry: " + ex.Message);
            }
        }

        private static void StartOpenShell(string globalDirectory)
        {
            string startMenu = Path.Combine(globalDirectory, "SilentApps", "Open-Shell", "StartMenu.exe");
            if (!File.Exists(startMenu))
            {
                SalsaLogger.Warn("Open-Shell StartMenu.exe not found.");
                return;
            }

            string menuXml = Path.Combine(globalDirectory, "SilentApps", "Open-Shell", "Menu Settings.xml");
            if (File.Exists(menuXml))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = startMenu,
                    Arguments = "-xml \"" + menuXml + "\"",
                    UseShellExecute = true
                });
            }

            Process.Start(startMenu);
            SalsaLogger.Info("Open-Shell started.");
        }

        private static void ShowFirstRunNotice(string globalDirectory)
        {
            string marker = Path.Combine(globalDirectory, "SalsaNOWWelcome.shown");
            if (File.Exists(marker))
                return;

            var thread = new Thread(() =>
            {
                try
                {
                    Application.EnableVisualStyles();
                    ShowWelcomeDialog();

                    File.WriteAllText(marker, DateTime.Now.ToString("o"));
                    SalsaLogger.Info("First-run notice acknowledged.");
                }
                catch (Exception ex)
                {
                    SalsaLogger.Error("First-run notice failed: " + ex.Message);
                }
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
        }

        private const string WelcomeTitle = "SalsaNOW";
        private const string WelcomeInstruction = "Always close the session from the Windows Start menu button \"Shutdown Session\".";
        private const string WelcomeContent =
            "The following paths persist if you have persistent storage:\n" +
            "• C:\\Users\\kiosk\\Documents\n" +
            "• C:\\Users\\kiosk\\AppData (all AppData)\n" +
            "• C:\\Users\\public\\Documents\n" +
            "• I:\\ (the whole drive persists)\n\n" +
            "ANTICHEAT GAMES ARE NOT SUPPORTED\n" +
            "SOFTWARE THAT REQUIRES ADMIN RIGHTS AT ALL TIMES IS NOT SUPPORTED";

        private static void ShowWelcomeDialog()
        {
            if (TryShowTaskDialog())
                return;

            using (Form form = CreateWelcomeForm())
                form.ShowDialog();
        }

        private static bool TryShowTaskDialog()
        {
            IntPtr buttonsPtr = IntPtr.Zero;
            IntPtr textPtr = IntPtr.Zero;

            try
            {
                textPtr = Marshal.StringToHGlobalUni("I understand");
                var button = new TaskDialogButton
                {
                    nButtonID = 100,
                    pszButtonText = textPtr
                };
                buttonsPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(TaskDialogButton)));
                Marshal.StructureToPtr(button, buttonsPtr, false);

                var config = new TaskDialogConfig
                {
                    cbSize = (uint)Marshal.SizeOf(typeof(TaskDialogConfig)),
                    dwFlags = 0x0008 | 0x01000000,
                    pszWindowTitle = WelcomeTitle,
                    mainIcon = new IntPtr(-1),
                    pszMainInstruction = WelcomeInstruction,
                    pszContent = WelcomeContent,
                    cButtons = 1,
                    pButtons = buttonsPtr,
                    nDefaultButton = 100,
                    cxWidth = 240
                };

                int hr = TaskDialogIndirect(ref config, out _, out _, out _);
                return hr == 0;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (buttonsPtr != IntPtr.Zero)
                    Marshal.FreeHGlobal(buttonsPtr);
                if (textPtr != IntPtr.Zero)
                    Marshal.FreeHGlobal(textPtr);
            }
        }

        private static Form CreateWelcomeForm()
        {
            var form = new Form
            {
                Text = WelcomeTitle,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterScreen,
                MaximizeBox = false,
                MinimizeBox = false,
                ShowIcon = false,
                ShowInTaskbar = false,
                TopMost = true,
                Font = new Font("Segoe UI", 9.75f),
                BackColor = Color.White,
                ClientSize = new Size(460, 280)
            };

            var footer = new Panel
            {
                Height = 52,
                Dock = DockStyle.Bottom,
                BackColor = Color.FromArgb(243, 243, 243)
            };

            var button = new Button
            {
                Text = "I understand",
                DialogResult = DialogResult.OK,
                FlatStyle = FlatStyle.System,
                Size = new Size(112, 32)
            };
            footer.Controls.Add(button);
            footer.Layout += (s, e) =>
            {
                button.Location = new Point(
                    footer.ClientSize.Width - button.Width - 20,
                    (footer.ClientSize.Height - button.Height) / 2);
            };

            var sep = new Panel
            {
                Height = 1,
                Dock = DockStyle.Bottom,
                BackColor = Color.FromArgb(229, 229, 229)
            };

            var body = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.White
            };

            var icon = new PictureBox
            {
                Image = SystemIcons.Warning.ToBitmap(),
                Size = new Size(32, 32),
                Location = new Point(20, 20),
                SizeMode = PictureBoxSizeMode.StretchImage
            };

            var instruction = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(360, 0),
                Location = new Point(64, 18),
                Font = new Font("Segoe UI", 12f),
                Text = WelcomeInstruction
            };

            var content = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(360, 0),
                Font = new Font("Segoe UI", 9.75f),
                Text = WelcomeContent
            };

            body.Controls.Add(icon);
            body.Controls.Add(instruction);
            body.Controls.Add(content);

            form.Controls.Add(body);
            form.Controls.Add(sep);
            form.Controls.Add(footer);
            form.AcceptButton = button;

            form.Load += (s, e) =>
            {
                content.Location = new Point(64, instruction.Bottom + 10);
                int bottom = Math.Max(icon.Bottom, content.Bottom) + 20;
                form.ClientSize = new Size(form.ClientSize.Width, bottom + sep.Height + footer.Height);
                TryRoundWindow(form.Handle);
            };

            form.FormClosed += (s, e) =>
            {
                if (icon.Image != null)
                    icon.Image.Dispose();
            };

            return form;
        }

        private static void TryRoundWindow(IntPtr handle)
        {
            try
            {
                int preference = 2;
                DwmSetWindowAttribute(handle, 33, ref preference, sizeof(int));
            }
            catch { }
        }

        [DllImport("comctl32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int TaskDialogIndirect(
            ref TaskDialogConfig pTaskConfig,
            out int pnButton,
            out int pnRadioButton,
            [MarshalAs(UnmanagedType.Bool)] out bool pfVerificationFlagChecked);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int pvAttribute, int cbAttribute);

        [StructLayout(LayoutKind.Sequential, Pack = 4, CharSet = CharSet.Unicode)]
        private struct TaskDialogConfig
        {
            public uint cbSize;
            public IntPtr hwndParent;
            public IntPtr hInstance;
            public int dwFlags;
            public int dwCommonButtons;
            [MarshalAs(UnmanagedType.LPWStr)] public string pszWindowTitle;
            public IntPtr mainIcon;
            [MarshalAs(UnmanagedType.LPWStr)] public string pszMainInstruction;
            [MarshalAs(UnmanagedType.LPWStr)] public string pszContent;
            public uint cButtons;
            public IntPtr pButtons;
            public int nDefaultButton;
            public uint cRadioButtons;
            public IntPtr pRadioButtons;
            public int nDefaultRadioButton;
            [MarshalAs(UnmanagedType.LPWStr)] public string pszVerificationText;
            [MarshalAs(UnmanagedType.LPWStr)] public string pszExpandedInformation;
            [MarshalAs(UnmanagedType.LPWStr)] public string pszExpandedControlText;
            [MarshalAs(UnmanagedType.LPWStr)] public string pszCollapsedControlText;
            public IntPtr footerIcon;
            [MarshalAs(UnmanagedType.LPWStr)] public string pszFooter;
            public IntPtr pfCallback;
            public IntPtr lpCallbackData;
            public uint cxWidth;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4, CharSet = CharSet.Unicode)]
        private struct TaskDialogButton
        {
            public int nButtonID;
            public IntPtr pszButtonText;
        }
    }
}
