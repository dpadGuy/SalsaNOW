using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace SalsaNOW
{
    internal class SteamDetach
    {
        // WARNING: Do not modify the environment of Steam or SalsaSteam, or of any program they start,
        // unless it is really, really needed. They inherit SalsaNOW's environment unchanged, including
        // the shader cache, PATH, and every Steam variable. Wiping that makes games such as CS2 run
        // extremely badly. Only programs that are not Steam or SalsaSteam get a cleaned environment,
        // and only through StartClean.
        public static Process StartClean(ProcessStartInfo info)
        {
            ApplyCleanEnvironment(info);
            return Process.Start(info);
        }

        public static void ApplyCleanEnvironment(ProcessStartInfo info)
        {
            info.UseShellExecute = false;

            var env = info.EnvironmentVariables;
            var remove = new List<string>();
            foreach (string key in env.Keys)
            {
                if (key.Equals("PATH", StringComparison.OrdinalIgnoreCase))
                    continue;

                string value = env[key] ?? "";
                if (IsSteamVariable(key) || ValuePointsAtSteam(value))
                    remove.Add(key);
            }

            foreach (string key in remove)
                env.Remove(key);

            string pathKey = null;
            foreach (string key in env.Keys)
            {
                if (key.Equals("PATH", StringComparison.OrdinalIgnoreCase))
                {
                    pathKey = key;
                    break;
                }
            }

            if (pathKey == null)
                return;

            string path = env[pathKey] ?? "";
            string[] kept = path
                .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(entry => !ValuePointsAtSteam(entry))
                .ToArray();
            env[pathKey] = string.Join(";", kept);
        }

        public static void RemoveSteamEnvironments()
        {
            var overlay = new Thread(WatchOverlay);
            overlay.IsBackground = true;
            overlay.Name = "Steam overlay disarm";
            overlay.Start();

            // Disarming SalsaNOW stops the overlay from following every child. These exes still need it.
            var allow = new Thread(WatchAllowedOverlays);
            allow.IsBackground = true;
            allow.Name = "Steam overlay whitelist";
            allow.Start();
        }

        public static void DisarmOverlay()
        {
            ModuleSpan[] spans = FindOverlayModules();
            if (spans.Length == 0)
                return;

            lock (DisarmGate)
            {
                SawOverlay = true;
                IntPtr[] paused = SuspendOverlayThreads(spans);
                int removed = 0;
                try
                {
                    removed += RestoreInlineHooks(spans);
                    removed += RestoreImportHooks(spans);
                }
                finally
                {
                    ResumeThreads(paused);
                }

                if (removed > 0)
                    HooksRemoved += removed;
            }
        }

        public static void LogDisarmResult()
        {
            DisarmOverlay();
            lock (DisarmGate)
            {
                if (ResultLogged || !SawOverlay)
                    return;

                ResultLogged = true;
                string whitelist = DescribeWhitelist();
                if (HooksRemoved > 0)
                    SalsaLogger.Steam("Steam overlay will not load into programs SalsaNOW starts. Whitelist: " + whitelist + ".");
                else
                    SalsaLogger.Warn("Steam overlay is loaded, but its process hooks were not found. Whitelist: " + whitelist + ".");
            }
        }

        // Used until the remote list is read. An empty or broken file keeps this list.
        // A JSON list, including [], replaces it.
        private static readonly string[] DefaultOverlayWhitelist =
        {
            "Game Manager.exe",
            "PlayGTAV.exe",
            "GTA5.exe",
            "GTA5_Enhanced.exe"
        };

        private const string OverlayWhitelistUrl = "https://salsanowfiles.work/jsons/SteamOverlayWhitelist.txt";
        private static readonly object WhitelistGate = new object();
        private static string[] OverlayWhitelist = DefaultOverlayWhitelist;
        private static string WhitelistSignature = string.Join("\n", DefaultOverlayWhitelist);
        private static DateTime WhitelistFetchedUtc = DateTime.MinValue;
        private static bool WhitelistFallbackLogged;
        private static readonly TimeSpan WhitelistRefreshInterval = TimeSpan.FromSeconds(30);

        private static void WatchAllowedOverlays()
        {
            while (true)
            {
                try
                {
                    RefreshOverlayWhitelist();
                    string[] names = SnapshotWhitelist();
                    for (int i = 0; i < names.Length; i++)
                        LoadOverlayInto(names[i]);
                    ForgetExitedOverlays();
                }
                catch (Exception ex)
                {
                    SalsaLogger.Error("Steam overlay whitelist check failed: " + ex.Message);
                }

                Thread.Sleep(500);
            }
        }

        private static string[] SnapshotWhitelist()
        {
            lock (WhitelistGate)
                return OverlayWhitelist;
        }

        private static string DescribeWhitelist()
        {
            string[] names = SnapshotWhitelist();
            return names.Length == 0 ? "none" : string.Join(", ", names);
        }

        private static void RefreshOverlayWhitelist()
        {
            if (WhitelistFetchedUtc != DateTime.MinValue &&
                DateTime.UtcNow - WhitelistFetchedUtc < WhitelistRefreshInterval)
                return;

            WhitelistFetchedUtc = DateTime.UtcNow;
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12 | SecurityProtocolType.Tls13;
                string json;
                using (var client = new WebClient())
                {
                    client.Headers.Add("Cache-Control", "no-cache");
                    client.Headers.Add("Pragma", "no-cache");
                    json = client.DownloadString(OverlayWhitelistUrl + "?nocache=" + DateTime.UtcNow.Ticks);
                }

                string[] parsed = ParseOverlayWhitelist(json);
                if (parsed == null)
                {
                    if (!WhitelistFallbackLogged)
                    {
                        WhitelistFallbackLogged = true;
                        SalsaLogger.Warn("Steam overlay whitelist was empty or not a list. Keeping " + DescribeWhitelist() + ".");
                    }
                    return;
                }

                WhitelistFallbackLogged = false;
                ApplyOverlayWhitelist(parsed);
            }
            catch (Exception ex)
            {
                if (!WhitelistFallbackLogged)
                {
                    WhitelistFallbackLogged = true;
                    SalsaLogger.Warn("Steam overlay whitelist could not be read. Keeping " + DescribeWhitelist() + ". " + ex.Message);
                }
            }
        }

        private static void ApplyOverlayWhitelist(string[] parsed)
        {
            string signature = string.Join("\n", parsed);
            bool announce;
            lock (WhitelistGate)
            {
                if (string.Equals(signature, WhitelistSignature, StringComparison.OrdinalIgnoreCase))
                    return;
                OverlayWhitelist = parsed;
                WhitelistSignature = signature;
                // The startup line already prints this list. Only a later change gets its own line.
                announce = ResultLogged;
            }

            if (announce)
                SalsaLogger.Steam("Steam overlay whitelist: " + (parsed.Length == 0 ? "none" : string.Join(", ", parsed)) + ".");
        }

        // Null means "keep the list we already have". An array, including an empty one, is the new list.
        // JSON array of exe names, or { "exes": [ ... ] }. A plain text file is one exe name per line.
        private static string[] ParseOverlayWhitelist(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            string trimmed = text.Trim();
            if (trimmed.StartsWith("[") || trimmed.StartsWith("{"))
                return ParseOverlayWhitelistJson(trimmed);

            var names = new List<string>();
            string[] lines = trimmed.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("//"))
                    continue;
                AddWhitelistName(names, line);
            }

            return names.ToArray();
        }

        private static string[] ParseOverlayWhitelistJson(string json)
        {
            JToken token = JToken.Parse(json);
            JArray array = token as JArray;
            if (array == null && token.Type == JTokenType.Object)
            {
                JObject obj = (JObject)token;
                array = (obj["exes"] ?? obj["apps"] ?? obj["whitelist"]) as JArray;
            }
            if (array == null)
                return null;

            var names = new List<string>();
            foreach (JToken item in array)
            {
                string name = null;
                if (item.Type == JTokenType.String)
                    name = (string)item;
                else if (item.Type == JTokenType.Object)
                    name = (string)(item["exe"] ?? item["exeName"] ?? item["name"]);
                AddWhitelistName(names, name);
            }

            return names.ToArray();
        }

        private static void AddWhitelistName(List<string> names, string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return;

            name = Path.GetFileName(name.Trim().Trim('"'));
            if (name.Length == 0)
                return;
            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                name += ".exe";
            for (int i = 0; i < names.Count; i++)
            {
                if (names[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                    return;
            }

            names.Add(name);
        }

        private static bool IsOverlayWhitelisted(Process process, string exeName)
        {
            try
            {
                string path = process.MainModule != null ? process.MainModule.FileName : null;
                if (!string.IsNullOrEmpty(path))
                    return Path.GetFileName(path).Equals(exeName, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
            }

            string name = process.ProcessName ?? "";
            return name.Equals(Path.GetFileNameWithoutExtension(exeName), StringComparison.OrdinalIgnoreCase);
        }

        private static void LoadOverlayInto(string exeName)
        {
            string processName = Path.GetFileNameWithoutExtension(exeName);
            Process[] processes = Process.GetProcessesByName(processName);
            try
            {
                foreach (Process process in processes)
                {
                    uint pid;
                    try
                    {
                        if (process.HasExited || !IsOverlayWhitelisted(process, exeName))
                            continue;
                        pid = (uint)process.Id;
                    }
                    catch
                    {
                        continue;
                    }

                    if (OverlayLoaded.Contains(pid))
                        continue;
                    if (LoadOverlay(pid))
                    {
                        OverlayLoaded.Add(pid);
                        SalsaLogger.Steam("Loaded the Steam overlay into " + exeName + ".");
                    }
                    else if (OverlayLoadFailed.Add(pid))
                    {
                        SalsaLogger.Warn("Could not load the Steam overlay into " + exeName + ".");
                    }
                }
            }
            finally
            {
                foreach (Process process in processes)
                    process.Dispose();
            }
        }

        private static void ForgetExitedOverlays()
        {
            OverlayLoaded.RemoveWhere(PidHasExited);
            OverlayLoadFailed.RemoveWhere(PidHasExited);
            HooksDisarmed.RemoveWhere(PidHasExited);
        }

        private static bool PidHasExited(uint pid)
        {
            try
            {
                Process.GetProcessById((int)pid).Dispose();
                return false;
            }
            catch
            {
                return true;
            }
        }

        private static void StopOverlaySpread(string processName)
        {
            Process[] processes = Process.GetProcessesByName(processName);
            try
            {
                foreach (Process process in processes)
                {
                    uint pid;
                    try
                    {
                        if (process.HasExited)
                            continue;
                        pid = (uint)process.Id;
                    }
                    catch
                    {
                        continue;
                    }

                    if (HooksDisarmed.Contains(pid) || !ProcessHasOverlay(pid))
                        continue;

                    int removed = DisarmRemoteHooks(pid);
                    if (removed < 0)
                        continue;

                    HooksDisarmed.Add(pid);
                    if (removed > 0)
                        SalsaLogger.Steam("Steam overlay in " + processName + " will not attach to new processes.");
                }
            }
            finally
            {
                foreach (Process process in processes)
                    process.Dispose();
            }
        }

        private static int DisarmRemoteHooks(uint pid)
        {
            if (IsSteamOrSalsaSteamProcess(pid))
                return 0;

            IntPtr process = OpenProcess(ProcessInjectAccess, false, pid);
            if (process == IntPtr.Zero)
                return -1;

            try
            {
                ModuleSpan[] overlay = RemoteOverlaySpans(pid);
                if (overlay.Length == 0)
                    return -1;

                var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
                int removed = 0;
                foreach (ModuleEntry module in RemoteModules(pid))
                {
                    if (!IsHookModule(module.Module))
                        continue;

                    byte[] file = ReadModuleFile(module.ExePath, files);
                    if (file == null)
                        continue;

                    long moduleStart = module.BaseAddress.ToInt64();
                    foreach (string procName in HookedProcs)
                    {
                        IntPtr proc = FindExportInRemoteModule(process, module.BaseAddress, procName);
                        if (proc == IntPtr.Zero)
                            continue;

                        long procAddress = proc.ToInt64();
                        if (procAddress < moduleStart)
                            continue;

                        uint rva = (uint)(procAddress - moduleStart);
                        byte[] live = ReadRemote(process, procAddress, 32);
                        byte[] original = ReadFileRange(file, rva, 32);
                        if (live == null || original == null || live.Length < 16 || original.Length < 16)
                            continue;
                        if (live[0] == original[0] && live[1] == original[1])
                            continue;
                        if (!RemoteLeadsToOverlay(process, procAddress, overlay, 3, 0))
                            continue;

                        int length = HookLength(live);
                        if (length > original.Length)
                            continue;

                        byte[] restore = new byte[length];
                        Buffer.BlockCopy(original, 0, restore, 0, length);
                        if (WriteRemote(process, procAddress, restore))
                            removed++;
                    }
                }

                return removed;
            }
            finally
            {
                CloseHandle(process);
            }
        }

        private static bool LoadOverlay(uint pid)
        {
            if (IsSteamOrSalsaSteamProcess(pid))
                return false;

            IntPtr process = OpenProcess(ProcessInjectAccess, false, pid);
            if (process == IntPtr.Zero)
                return false;

            try
            {
                bool wow64;
                if (!IsWow64Process(process, out wow64))
                    wow64 = false;
                if (ProcessHasOverlay(pid))
                    return true;

                string dll = OverlayDllPath(wow64);
                if (string.IsNullOrEmpty(dll))
                    return false;

                IntPtr loadLibrary = wow64
                    ? FindRemoteLoadLibrary(process, pid)
                    : GetProcAddress(GetModuleHandle("kernel32.dll"), "LoadLibraryW");
                if (loadLibrary == IntPtr.Zero)
                    return false;

                byte[] path = Encoding.Unicode.GetBytes(dll + "\0");
                IntPtr remote = VirtualAllocEx(process, IntPtr.Zero, (UIntPtr)path.Length, 0x3000, PageReadWrite);
                if (remote == IntPtr.Zero)
                    return false;

                try
                {
                    IntPtr written;
                    if (!WriteProcessMemory(process, remote, path, path.Length, out written))
                        return false;

                    IntPtr thread = CreateRemoteThread(process, IntPtr.Zero, UIntPtr.Zero, loadLibrary, remote, 0, IntPtr.Zero);
                    if (thread == IntPtr.Zero)
                        return false;

                    try
                    {
                        WaitForSingleObject(thread, 8000);
                        uint exitCode;
                        if (GetExitCodeThread(thread, out exitCode) && exitCode != 0)
                            return true;
                        // LoadLibrary's module base is 64-bit. The thread exit code is only the low half,
                        // so a successful load can look like 0. The module list is the real check.
                        return ProcessHasOverlay(pid);
                    }
                    finally
                    {
                        CloseHandle(thread);
                    }
                }
                finally
                {
                    VirtualFreeEx(process, remote, UIntPtr.Zero, 0x8000);
                }
            }
            finally
            {
                CloseHandle(process);
            }
        }

        private static bool ProcessHasOverlay(uint pid)
        {
            IntPtr snapshot = CreateToolhelp32Snapshot(0x18, pid);
            if (snapshot == InvalidHandle)
                return false;

            try
            {
                ModuleEntry entry = new ModuleEntry();
                entry.Size = (uint)Marshal.SizeOf(typeof(ModuleEntry));
                if (!Module32First(snapshot, ref entry))
                    return false;

                do
                {
                    if (entry.Module.Equals("GameOverlayRenderer64.dll", StringComparison.OrdinalIgnoreCase) ||
                        entry.Module.Equals("GameOverlayRenderer.dll", StringComparison.OrdinalIgnoreCase))
                        return true;
                    entry.Size = (uint)Marshal.SizeOf(typeof(ModuleEntry));
                }
                while (Module32Next(snapshot, ref entry));
            }
            finally
            {
                CloseHandle(snapshot);
            }

            return false;
        }

        private static string OverlayDllPath(bool wow64)
        {
            string fileName = wow64 ? "GameOverlayRenderer.dll" : "GameOverlayRenderer64.dll";
            IntPtr loaded = GetModuleHandle(fileName);
            if (loaded != IntPtr.Zero)
            {
                var path = new StringBuilder(1024);
                if (GetModuleFileName(loaded, path, path.Capacity) > 0 && File.Exists(path.ToString()))
                    return path.ToString();
            }

            string steam = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", fileName);
            if (File.Exists(steam))
                return steam;
            return null;
        }

        private static bool IsHookModule(string name)
        {
            for (int i = 0; i < HookedModules.Length; i++)
            {
                if (name.Equals(HookedModules[i], StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static List<ModuleEntry> RemoteModules(uint pid)
        {
            var modules = new List<ModuleEntry>();
            IntPtr snapshot = CreateToolhelp32Snapshot(0x18, pid);
            if (snapshot == InvalidHandle)
                return modules;

            try
            {
                ModuleEntry entry = new ModuleEntry();
                entry.Size = (uint)Marshal.SizeOf(typeof(ModuleEntry));
                if (!Module32First(snapshot, ref entry))
                    return modules;

                do
                {
                    modules.Add(entry);
                    entry.Size = (uint)Marshal.SizeOf(typeof(ModuleEntry));
                }
                while (Module32Next(snapshot, ref entry));
            }
            finally
            {
                CloseHandle(snapshot);
            }

            return modules;
        }

        private static ModuleSpan[] RemoteOverlaySpans(uint pid)
        {
            var spans = new List<ModuleSpan>();
            foreach (ModuleEntry entry in RemoteModules(pid))
            {
                if (!entry.Module.Equals("GameOverlayRenderer64.dll", StringComparison.OrdinalIgnoreCase) &&
                    !entry.Module.Equals("GameOverlayRenderer.dll", StringComparison.OrdinalIgnoreCase))
                    continue;

                ulong start = (ulong)entry.BaseAddress.ToInt64();
                spans.Add(new ModuleSpan
                {
                    Start = start,
                    End = start + entry.BaseSize
                });
            }

            return spans.ToArray();
        }

        private static bool RemoteLeadsToOverlay(IntPtr process, long address, ModuleSpan[] overlay, int depth, int guard)
        {
            if (address == 0 || guard > 8)
                return false;
            if (AddressInSpans((ulong)address, overlay))
                return true;
            if (depth < 0)
                return false;

            byte[] code = ReadRemote(process, address, 48);
            if (code == null || code.Length < 8)
                return false;

            for (int i = 0; i + 8 <= code.Length; i++)
            {
                ulong candidate = (ulong)BitConverter.ToInt64(code, i);
                if (AddressInSpans(candidate, overlay))
                    return true;
            }

            for (int i = 0; i + 5 <= code.Length; i++)
            {
                if (code[i] == 0xE8 || code[i] == 0xE9)
                {
                    int relative = BitConverter.ToInt32(code, i + 1);
                    long destination = address + i + 5 + relative;
                    if (RemoteLeadsToOverlay(process, destination, overlay, depth - 1, guard + 1))
                        return true;
                }
            }

            return false;
        }

        private static bool WriteRemote(IntPtr process, long address, byte[] data)
        {
            uint oldProtect;
            IntPtr target = new IntPtr(address);
            if (!VirtualProtectEx(process, target, (UIntPtr)data.Length, PageExecuteReadWrite, out oldProtect))
                return false;

            IntPtr written;
            bool wrote = WriteProcessMemory(process, target, data, data.Length, out written);
            FlushInstructionCache(process, target, (UIntPtr)data.Length);
            uint restored;
            VirtualProtectEx(process, target, (UIntPtr)data.Length, oldProtect, out restored);
            return wrote && written.ToInt64() >= data.Length;
        }

        private static IntPtr FindRemoteLoadLibrary(IntPtr process, uint pid)
        {
            IntPtr kernel32 = IntPtr.Zero;
            IntPtr snapshot = CreateToolhelp32Snapshot(0x18, pid);
            if (snapshot == InvalidHandle)
                return IntPtr.Zero;

            try
            {
                ModuleEntry entry = new ModuleEntry();
                entry.Size = (uint)Marshal.SizeOf(typeof(ModuleEntry));
                if (!Module32First(snapshot, ref entry))
                    return IntPtr.Zero;

                do
                {
                    if (entry.Module.Equals("kernel32.dll", StringComparison.OrdinalIgnoreCase))
                    {
                        kernel32 = entry.BaseAddress;
                        break;
                    }
                    entry.Size = (uint)Marshal.SizeOf(typeof(ModuleEntry));
                }
                while (Module32Next(snapshot, ref entry));
            }
            finally
            {
                CloseHandle(snapshot);
            }

            if (kernel32 == IntPtr.Zero)
                return IntPtr.Zero;

            return FindExportInRemoteModule(process, kernel32, "LoadLibraryW");
        }

        private static IntPtr FindExportInRemoteModule(IntPtr process, IntPtr moduleBase, string exportName)
        {
            byte[] dos = ReadRemote(process, moduleBase.ToInt64(), 64);
            if (dos == null || BitConverter.ToUInt16(dos, 0) != 0x5A4D)
                return IntPtr.Zero;

            int peOffset = BitConverter.ToInt32(dos, 0x3C);
            if (peOffset <= 0 || peOffset > 4096)
                return IntPtr.Zero;

            byte[] pe = ReadRemote(process, moduleBase.ToInt64() + peOffset, 248);
            if (pe == null || BitConverter.ToUInt32(pe, 0) != 0x00004550)
                return IntPtr.Zero;

            ushort magic = BitConverter.ToUInt16(pe, 24);
            int exportOffset = magic == 0x20B ? 136 : magic == 0x10B ? 120 : -1;
            if (exportOffset < 0 || exportOffset + 8 > pe.Length)
                return IntPtr.Zero;

            uint exportRva = BitConverter.ToUInt32(pe, exportOffset);
            if (exportRva == 0)
                return IntPtr.Zero;

            byte[] directory = ReadRemote(process, moduleBase.ToInt64() + exportRva, 40);
            if (directory == null)
                return IntPtr.Zero;

            uint functionCount = BitConverter.ToUInt32(directory, 20);
            uint nameCount = BitConverter.ToUInt32(directory, 24);
            uint functionRva = BitConverter.ToUInt32(directory, 28);
            uint nameRva = BitConverter.ToUInt32(directory, 32);
            uint ordinalRva = BitConverter.ToUInt32(directory, 36);
            if (nameCount == 0 || nameCount > 8192 || functionCount == 0 || functionCount > 8192)
                return IntPtr.Zero;

            byte[] names = ReadRemote(process, moduleBase.ToInt64() + nameRva, (int)nameCount * 4);
            byte[] ordinals = ReadRemote(process, moduleBase.ToInt64() + ordinalRva, (int)nameCount * 2);
            byte[] functions = ReadRemote(process, moduleBase.ToInt64() + functionRva, (int)functionCount * 4);
            if (names == null || ordinals == null || functions == null)
                return IntPtr.Zero;

            for (int i = 0; i < nameCount; i++)
            {
                uint nameEntry = BitConverter.ToUInt32(names, i * 4);
                string name = ReadRemoteAnsi(process, moduleBase.ToInt64() + nameEntry, 64);
                if (!string.Equals(name, exportName, StringComparison.Ordinal))
                    continue;

                ushort ordinal = BitConverter.ToUInt16(ordinals, i * 2);
                if (ordinal >= functionCount)
                    return IntPtr.Zero;

                uint function = BitConverter.ToUInt32(functions, ordinal * 4);
                return new IntPtr(moduleBase.ToInt64() + function);
            }

            return IntPtr.Zero;
        }

        private static byte[] ReadRemote(IntPtr process, long address, int count)
        {
            if (count <= 0)
                return null;

            byte[] buffer = new byte[count];
            IntPtr read;
            if (!ReadProcessMemory(process, new IntPtr(address), buffer, count, out read))
                return null;
            if (read.ToInt64() < count)
                return null;
            return buffer;
        }

        private static string ReadRemoteAnsi(IntPtr process, long address, int max)
        {
            byte[] buffer = ReadRemote(process, address, max);
            if (buffer == null)
                return null;

            int length = 0;
            while (length < buffer.Length && buffer[length] != 0)
                length++;
            return Encoding.ASCII.GetString(buffer, 0, length);
        }

        private static void WatchOverlay()
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < deadline && HooksRemoved == 0)
            {
                DisarmOverlay();
                if (HooksRemoved > 0)
                    return;
                Thread.Sleep(200);
            }

        }

        private static int RestoreInlineHooks(ModuleSpan[] overlay)
        {
            int removed = 0;
            var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

            for (int moduleIndex = 0; moduleIndex < HookedModules.Length; moduleIndex++)
            {
                IntPtr module = GetModuleHandle(HookedModules[moduleIndex]);
                if (module == IntPtr.Zero)
                    continue;

                ModuleInfo info;
                if (!GetModuleInformation(GetCurrentProcess(), module, out info, (uint)Marshal.SizeOf(typeof(ModuleInfo))))
                    continue;

                long moduleStart = module.ToInt64();
                long moduleEnd = moduleStart + info.SizeOfImage;
                byte[] file = ReadModuleFile(module, files);
                if (file == null)
                    continue;

                for (int procIndex = 0; procIndex < HookedProcs.Length; procIndex++)
                {
                    IntPtr proc = GetProcAddress(module, HookedProcs[procIndex]);
                    if (proc == IntPtr.Zero)
                        continue;

                    long procAddress = proc.ToInt64();
                    if (procAddress < moduleStart || procAddress >= moduleEnd)
                        continue;

                    uint rva = (uint)(procAddress - moduleStart);
                    byte[] live = ReadBytes(proc, 32);
                    byte[] original = ReadFileRange(file, rva, 32);
                    if (live == null || original == null || live.Length < 16 || original.Length < 16)
                        continue;
                    if (live[0] == original[0] && live[1] == original[1])
                        continue;
                    if (!LeadsToOverlay((ulong)procAddress, overlay, 3, 0))
                        continue;

                    int length = HookLength(live);
                    if (length > original.Length)
                        continue;

                    byte[] restore = new byte[length];
                    Buffer.BlockCopy(original, 0, restore, 0, length);
                    if (WriteBytes(proc, restore))
                        removed++;
                }
            }

            return removed;
        }

        private static int RestoreImportHooks(ModuleSpan[] overlay)
        {
            IntPtr[] modules = new IntPtr[512];
            uint needed;
            if (!EnumProcessModules(GetCurrentProcess(), modules, (uint)(modules.Length * IntPtr.Size), out needed))
                return 0;

            int count = (int)(needed / (uint)IntPtr.Size);
            if (count > modules.Length)
                count = modules.Length;

            var names = new HashSet<string>(HookedProcs, StringComparer.OrdinalIgnoreCase);
            int removed = 0;
            for (int i = 0; i < count; i++)
            {
                if (AddressInSpans((ulong)modules[i].ToInt64(), overlay))
                    continue;
                removed += RestoreModuleImports(modules[i], names, overlay);
            }

            return removed;
        }

        private static int RestoreModuleImports(IntPtr module, HashSet<string> names, ModuleSpan[] overlay)
        {
            ModuleInfo info;
            if (!GetModuleInformation(GetCurrentProcess(), module, out info, (uint)Marshal.SizeOf(typeof(ModuleInfo))))
                return 0;

            long baseAddress = module.ToInt64();
            long end = baseAddress + info.SizeOfImage;
            int peOffset;
            if (!TryReadInt32(baseAddress + 0x3C, end, out peOffset) || peOffset <= 0)
                return 0;

            long pe = baseAddress + peOffset;
            ushort magic;
            if (!TryReadUInt16(pe + 24, end, out magic) || magic != 0x20B)
                return 0;

            uint importRva;
            if (!TryReadUInt32(pe + 144, end, out importRva) || importRva == 0)
                return 0;

            int removed = 0;
            for (int descriptor = 0; descriptor < 4096; descriptor++)
            {
                long descriptorAddress = baseAddress + importRva + (descriptor * 20);
                uint nameTableRva;
                uint addressTableRva;
                if (!TryReadUInt32(descriptorAddress, end, out nameTableRva) ||
                    !TryReadUInt32(descriptorAddress + 16, end, out addressTableRva))
                    break;
                if (nameTableRva == 0 && addressTableRva == 0)
                    break;
                if (nameTableRva == 0)
                    nameTableRva = addressTableRva;

                for (int index = 0; index < 16384; index++)
                {
                    long nameEntry = baseAddress + nameTableRva + (index * 8L);
                    long addressEntry = baseAddress + addressTableRva + (index * 8L);
                    long thunk;
                    if (!TryReadInt64(nameEntry, end, out thunk) || thunk == 0)
                        break;
                    if ((ulong)thunk >= 0x8000000000000000UL)
                        continue;

                    string procName = ReadAnsi(baseAddress + (thunk & 0x7FFFFFFF) + 2, end, 64);
                    if (procName == null || !names.Contains(procName))
                        continue;

                    long currentValue;
                    if (!TryReadInt64(addressEntry, end, out currentValue))
                        continue;
                    if (!LeadsToOverlay((ulong)currentValue, overlay, 2, 0))
                        continue;

                    IntPtr real = ResolveReal(procName);
                    if (real == IntPtr.Zero || real.ToInt64() == currentValue)
                        continue;
                    if (LeadsToOverlay((ulong)real.ToInt64(), overlay, 1, 0))
                        continue;
                    if (WritePointer(new IntPtr(addressEntry), real))
                        removed++;
                }
            }

            return removed;
        }

        private static IntPtr ResolveReal(string procName)
        {
            for (int i = 0; i < HookedModules.Length; i++)
            {
                IntPtr module = GetModuleHandle(HookedModules[i]);
                if (module == IntPtr.Zero)
                    continue;
                IntPtr proc = GetProcAddress(module, procName);
                if (proc != IntPtr.Zero)
                    return proc;
            }

            return IntPtr.Zero;
        }

        private static byte[] ReadModuleFile(IntPtr module, Dictionary<string, byte[]> files)
        {
            var path = new StringBuilder(1024);
            if (GetModuleFileName(module, path, path.Capacity) == 0)
                return null;
            return ReadModuleFile(path.ToString(), files);
        }

        private static byte[] ReadModuleFile(string filePath, Dictionary<string, byte[]> files)
        {
            if (string.IsNullOrEmpty(filePath))
                return null;

            byte[] file;
            if (files.TryGetValue(filePath, out file))
                return file;

            try
            {
                using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (stream.Length <= 0 || stream.Length > 32 * 1024 * 1024)
                        return null;
                    file = new byte[stream.Length];
                    int read = 0;
                    while (read < file.Length)
                    {
                        int got = stream.Read(file, read, file.Length - read);
                        if (got <= 0)
                            return null;
                        read += got;
                    }
                }
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }

            files[filePath] = file;
            return file;
        }

        private static byte[] ReadFileRange(byte[] file, uint rva, int count)
        {
            if (file.Length < 0x40)
                return null;

            int pe = BitConverter.ToInt32(file, 0x3C);
            if (pe <= 0 || pe + 24 >= file.Length)
                return null;
            if (BitConverter.ToUInt32(file, pe) != 0x00004550)
                return null;

            ushort sections = BitConverter.ToUInt16(file, pe + 6);
            ushort optionalSize = BitConverter.ToUInt16(file, pe + 20);
            int sectionTable = pe + 24 + optionalSize;
            for (int i = 0; i < sections; i++)
            {
                int offset = sectionTable + (i * 40);
                if (offset < 0 || offset + 24 > file.Length)
                    return null;

                uint virtualSize = BitConverter.ToUInt32(file, offset + 8);
                uint virtualAddress = BitConverter.ToUInt32(file, offset + 12);
                uint rawSize = BitConverter.ToUInt32(file, offset + 16);
                uint rawPointer = BitConverter.ToUInt32(file, offset + 20);
                uint span = Math.Max(virtualSize, rawSize);
                if (rva < virtualAddress || rva >= virtualAddress + span)
                    continue;

                uint delta = rva - virtualAddress;
                if (delta >= rawSize)
                    return null;

                int filePosition = (int)(rawPointer + delta);
                if (filePosition < 0 || filePosition + count > file.Length)
                    return null;

                byte[] data = new byte[count];
                Buffer.BlockCopy(file, filePosition, data, 0, count);
                return data;
            }

            return null;
        }

        private static int HookLength(byte[] mem)
        {
            if (mem.Length >= 12 && mem[0] == 0x48 && mem[1] == 0xB8 && mem[10] == 0xFF && mem[11] == 0xE0)
                return 12;
            if (mem.Length >= 13 && mem[0] == 0x49 && mem[1] == 0xBB && mem[10] == 0x41 && mem[11] == 0xFF && mem[12] == 0xE3)
                return 13;
            if (mem.Length >= 14 && mem[0] == 0xFF && mem[1] == 0x25 && mem[2] == 0 && mem[3] == 0 && mem[4] == 0 && mem[5] == 0)
                return 14;
            if (mem.Length >= 5 && mem[0] == 0xE9)
                return 5;
            if (mem.Length >= 6 && mem[0] == 0xFF && mem[1] == 0x25)
                return 6;
            return 16;
        }

        private static bool LeadsToOverlay(ulong address, ModuleSpan[] overlay, int depth, int guard)
        {
            if (address == 0 || guard > 8)
                return false;
            if (AddressInSpans(address, overlay))
                return true;
            if (depth < 0)
                return false;

            byte[] code = ReadBytes(new IntPtr((long)address), 48);
            if (code == null || code.Length < 8)
                return false;

            for (int i = 0; i + 8 <= code.Length; i++)
            {
                ulong candidate = (ulong)BitConverter.ToInt64(code, i);
                if (AddressInSpans(candidate, overlay))
                    return true;
            }

            for (int i = 0; i + 5 <= code.Length; i++)
            {
                if (code[i] == 0xE8 || code[i] == 0xE9)
                {
                    int relative = BitConverter.ToInt32(code, i + 1);
                    ulong destination = (ulong)((long)address + i + 5 + relative);
                    if (LeadsToOverlay(destination, overlay, depth - 1, guard + 1))
                        return true;
                }

                if (i + 6 <= code.Length && code[i] == 0xFF && code[i + 1] == 0x25)
                {
                    int relative = BitConverter.ToInt32(code, i + 2);
                    long slot = (long)address + i + 6 + relative;
                    byte[] pointer = ReadBytes(new IntPtr(slot), 8);
                    if (pointer != null && pointer.Length == 8)
                    {
                        ulong destination = (ulong)BitConverter.ToInt64(pointer, 0);
                        if (LeadsToOverlay(destination, overlay, depth - 1, guard + 1))
                            return true;
                    }
                }
            }

            return false;
        }

        private static byte[] ReadBytes(IntPtr address, int count)
        {
            if (address == IntPtr.Zero || count <= 0)
                return null;

            MemoryBasicInformation info;
            if (VirtualQuery(address, out info, (UIntPtr)Marshal.SizeOf(typeof(MemoryBasicInformation))) == UIntPtr.Zero)
                return null;
            if (info.State != MemCommit || !IsReadable(info.Protect))
                return null;

            long regionEnd = info.BaseAddress.ToInt64() + info.RegionSize.ToInt64();
            long available = regionEnd - address.ToInt64();
            if (available <= 0)
                return null;
            if (available < count)
                count = (int)available;

            byte[] buffer = new byte[count];
            Marshal.Copy(address, buffer, 0, count);
            return buffer;
        }

        private static bool IsReadable(uint protect)
        {
            if ((protect & PageGuard) != 0)
                return false;

            uint page = protect & 0xFF;
            return page == 0x02 || page == 0x04 || page == 0x08 ||
                   page == 0x10 || page == 0x20 || page == 0x40 || page == 0x80;
        }

        private static bool WriteBytes(IntPtr address, byte[] data)
        {
            uint oldProtect;
            if (!VirtualProtect(address, (UIntPtr)data.Length, PageExecuteReadWrite, out oldProtect))
                return false;

            Marshal.Copy(data, 0, address, data.Length);
            FlushInstructionCache(GetCurrentProcess(), address, (UIntPtr)data.Length);
            VirtualProtect(address, (UIntPtr)data.Length, oldProtect, out oldProtect);
            return true;
        }

        private static bool WritePointer(IntPtr slot, IntPtr value)
        {
            uint oldProtect;
            if (!VirtualProtect(slot, (UIntPtr)IntPtr.Size, PageReadWrite, out oldProtect))
                return false;

            Marshal.WriteIntPtr(slot, value);
            VirtualProtect(slot, (UIntPtr)IntPtr.Size, oldProtect, out oldProtect);
            return true;
        }

        private static bool TryReadInt32(long address, long end, out int value)
        {
            value = 0;
            if (address < 0 || address + 4 > end)
                return false;
            value = Marshal.ReadInt32(new IntPtr(address));
            return true;
        }

        private static bool TryReadUInt16(long address, long end, out ushort value)
        {
            value = 0;
            if (address < 0 || address + 2 > end)
                return false;
            value = (ushort)Marshal.ReadInt16(new IntPtr(address));
            return true;
        }

        private static bool TryReadUInt32(long address, long end, out uint value)
        {
            value = 0;
            if (address < 0 || address + 4 > end)
                return false;
            value = (uint)Marshal.ReadInt32(new IntPtr(address));
            return true;
        }

        private static bool TryReadInt64(long address, long end, out long value)
        {
            value = 0;
            if (address < 0 || address + 8 > end)
                return false;
            value = Marshal.ReadInt64(new IntPtr(address));
            return true;
        }

        private static string ReadAnsi(long address, long end, int max)
        {
            var chars = new char[max];
            int length = 0;
            for (int i = 0; i < max; i++)
            {
                if (address + i >= end)
                    return null;
                byte value = Marshal.ReadByte(new IntPtr(address + i));
                if (value == 0)
                    return new string(chars, 0, length);
                if (value < 32 || value > 126)
                    return null;
                chars[length++] = (char)value;
            }

            return null;
        }

        private static IntPtr[] SuspendOverlayThreads(ModuleSpan[] spans)
        {
            var paused = new List<IntPtr>();
            uint self = GetCurrentThreadId();
            uint[] threadIds = SnapshotThreadIds();
            for (int i = 0; i < threadIds.Length; i++)
            {
                if (threadIds[i] == self)
                    continue;

                IntPtr handle = OpenThread(ThreadSuspendAccess, false, threadIds[i]);
                if (handle == IntPtr.Zero)
                    continue;

                ulong start;
                if (!TryGetStartAddress(handle, out start) || !AddressInSpans(start, spans))
                {
                    CloseHandle(handle);
                    continue;
                }

                if (SuspendThread(handle) == 0xFFFFFFFF)
                {
                    CloseHandle(handle);
                    continue;
                }

                paused.Add(handle);
            }

            return paused.ToArray();
        }

        private static void ResumeThreads(IntPtr[] handles)
        {
            for (int i = 0; i < handles.Length; i++)
            {
                ResumeThread(handles[i]);
                CloseHandle(handles[i]);
            }
        }

        private static ModuleSpan[] FindOverlayModules()
        {
            string[] names = { "GameOverlayRenderer64.dll", "GameOverlayRenderer.dll" };
            var found = new List<ModuleSpan>();
            foreach (string name in names)
            {
                IntPtr module = GetModuleHandle(name);
                if (module == IntPtr.Zero)
                    continue;

                ModuleInfo info;
                if (!GetModuleInformation(GetCurrentProcess(), module, out info, (uint)Marshal.SizeOf(typeof(ModuleInfo))))
                    continue;

                ulong start = (ulong)info.BaseOfDll.ToInt64();
                found.Add(new ModuleSpan
                {
                    Start = start,
                    End = start + info.SizeOfImage
                });
            }

            return found.ToArray();
        }

        private static uint[] SnapshotThreadIds()
        {
            var ids = new List<uint>();
            IntPtr snapshot = CreateToolhelp32Snapshot(Th32CsSnapThread, 0);
            if (snapshot == InvalidHandle)
                return ids.ToArray();

            try
            {
                uint processId = GetCurrentProcessId();
                ThreadEntry entry = new ThreadEntry();
                entry.Size = (uint)Marshal.SizeOf(typeof(ThreadEntry));
                if (!Thread32First(snapshot, ref entry))
                    return ids.ToArray();

                do
                {
                    if (entry.OwnerProcessId == processId)
                        ids.Add(entry.ThreadId);
                    entry.Size = (uint)Marshal.SizeOf(typeof(ThreadEntry));
                }
                while (Thread32Next(snapshot, ref entry));
            }
            finally
            {
                CloseHandle(snapshot);
            }

            return ids.ToArray();
        }

        private static bool TryGetStartAddress(IntPtr thread, out ulong start)
        {
            start = 0;
            IntPtr value;
            int status = NtQueryInformationThread(thread, ThreadQuerySetWin32StartAddress, out value, (IntPtr)IntPtr.Size, IntPtr.Zero);
            if (status != 0)
                return false;
            start = (ulong)value.ToInt64();
            return true;
        }

        private static bool AddressInSpans(ulong address, ModuleSpan[] spans)
        {
            for (int i = 0; i < spans.Length; i++)
            {
                if (address >= spans[i].Start && address < spans[i].End)
                    return true;
            }

            return false;
        }

        private static readonly object DisarmGate = new object();
        private static readonly HashSet<uint> OverlayLoaded = new HashSet<uint>();
        private static readonly HashSet<uint> OverlayLoadFailed = new HashSet<uint>();
        private static readonly HashSet<uint> HooksDisarmed = new HashSet<uint>();
        private static int HooksRemoved;
        private static bool SawOverlay;
        private static bool ResultLogged;
        private static readonly string[] HookedModules = { "ntdll.dll", "kernelbase.dll", "kernel32.dll" };
        private static readonly string[] HookedProcs = {
            "NtCreateUserProcess",
            "NtCreateUserProcessEx",
            "CreateProcessInternalW",
            "CreateProcessInternalA",
            "CreateProcessW",
            "CreateProcessA",
            "CreateProcessAsUserW",
            "CreateProcessAsUserA",
            "WinExec",
            "GetProcAddress"
        };

        private const uint Th32CsSnapThread = 0x00000004;
        private const uint ThreadSuspendAccess = 0x0042;
        private const uint ProcessInjectAccess = 0x043A;
        private const uint MemCommit = 0x1000;
        private const uint PageGuard = 0x100;
        private const uint PageReadWrite = 0x04;
        private const uint PageExecuteReadWrite = 0x40;
        private const int ThreadQuerySetWin32StartAddress = 9;
        private static readonly IntPtr InvalidHandle = new IntPtr(-1);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetModuleFileName(IntPtr hModule, StringBuilder lpFilename, int nSize);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentProcessId();

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenThread(uint dwDesiredAccess, bool bInheritHandle, uint dwThreadId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint SuspendThread(IntPtr hThread);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint ResumeThread(IntPtr hThread);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualProtectEx(IntPtr hProcess, IntPtr lpAddress, UIntPtr dwSize, uint flNewProtect, out uint lpflOldProtect);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualProtect(IntPtr address, UIntPtr size, uint newProtect, out uint oldProtect);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern UIntPtr VirtualQuery(IntPtr address, out MemoryBasicInformation buffer, UIntPtr length);

        [DllImport("kernel32.dll")]
        private static extern bool FlushInstructionCache(IntPtr process, IntPtr address, UIntPtr size);

        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationThread(IntPtr threadHandle, int threadInformationClass, out IntPtr threadInformation, IntPtr threadInformationLength, IntPtr returnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool IsWow64Process(IntPtr hProcess, out bool wow64Process);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, UIntPtr dwSize, uint flAllocationType, uint flProtect);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualFreeEx(IntPtr hProcess, IntPtr lpAddress, UIntPtr dwSize, uint dwFreeType);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int nSize, out IntPtr lpNumberOfBytesWritten);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int nSize, out IntPtr lpNumberOfBytesRead);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateRemoteThread(IntPtr hProcess, IntPtr lpThreadAttributes, UIntPtr dwStackSize, IntPtr lpStartAddress, IntPtr lpParameter, uint dwCreationFlags, IntPtr lpThreadId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeThread(IntPtr hThread, out uint lpExitCode);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Module32First(IntPtr hSnapshot, ref ModuleEntry lpme);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Module32Next(IntPtr hSnapshot, ref ModuleEntry lpme);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool Thread32First(IntPtr hSnapshot, ref ThreadEntry lpte);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool Thread32Next(IntPtr hSnapshot, ref ThreadEntry lpte);

        [DllImport("psapi.dll", SetLastError = true)]
        private static extern bool GetModuleInformation(IntPtr hProcess, IntPtr hModule, out ModuleInfo lpmodinfo, uint cb);

        [DllImport("psapi.dll", SetLastError = true)]
        private static extern bool EnumProcessModules(IntPtr hProcess, [Out] IntPtr[] lphModule, uint cb, out uint lpcbNeeded);

        [StructLayout(LayoutKind.Sequential)]
        private struct ModuleInfo
        {
            public IntPtr BaseOfDll;
            public uint SizeOfImage;
            public IntPtr EntryPoint;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryBasicInformation
        {
            public IntPtr BaseAddress;
            public IntPtr AllocationBase;
            public uint AllocationProtect;
            public IntPtr RegionSize;
            public uint State;
            public uint Protect;
            public uint Type;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ModuleEntry
        {
            public uint Size;
            public uint ModuleId;
            public uint ProcessId;
            public uint GlobalUsage;
            public uint ProcessUsage;
            public IntPtr BaseAddress;
            public uint BaseSize;
            public IntPtr ModuleHandle;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string Module;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string ExePath;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ThreadEntry
        {
            public uint Size;
            public uint Usage;
            public uint ThreadId;
            public uint OwnerProcessId;
            public int BasePriority;
            public int DeltaPriority;
            public uint Flags;
        }

        private struct ModuleSpan
        {
            public ulong Start;
            public ulong End;
        }

        private static bool IsSteamOrSalsaSteamProcess(uint pid)
        {
            try
            {
                using (Process process = Process.GetProcessById((int)pid))
                {
                    string name = process.ProcessName ?? "";
                    if (name.Equals("steam", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("steamwebhelper", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("steamservice", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("steamerrorreporter", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("steamerrorreporter64", StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith("SalsaSteam", StringComparison.OrdinalIgnoreCase))
                        return true;

                    try
                    {
                        string path = process.MainModule != null ? process.MainModule.FileName : "";
                        if (path.IndexOf("\\SalsaSteam\\", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            path.IndexOf("SalsaSteamStartup", StringComparison.OrdinalIgnoreCase) >= 0)
                            return true;
                        if (path.EndsWith("\\steam.exe", StringComparison.OrdinalIgnoreCase) &&
                            path.IndexOf("\\steamapps\\", StringComparison.OrdinalIgnoreCase) < 0)
                            return true;
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }

            return false;
        }

        private static bool IsSteamVariable(string name)
        {
            string upper = name.ToUpperInvariant();

            return upper.Contains("STEAM") ||
                   upper.Contains("VALVE") ||
                   upper.StartsWith("FOSSILIZE_") ||
                   upper.StartsWith("MESA_") ||
                   upper.StartsWith("AMD_VK_") ||
                   upper.StartsWith("__GL_SHADER_") ||
                   upper.StartsWith("DXVK_STATE_CACHE") ||
                   upper.StartsWith("SDL_GAMECONTROLLER_") ||
                   upper.StartsWith("FPS_BROWSER_");
        }

        private static bool ValuePointsAtSteam(string value)
        {
            if (string.IsNullOrEmpty(value))
                return false;

            return value.IndexOf("\\steam", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   value.IndexOf("/steam", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   value.IndexOf("\\valve", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   value.IndexOf("/valve", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
