using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace SalsaNOW
{
    internal class SteamDetach
    {
        public static void RemoveSteamEnvironments()
        {
            IDictionary variables =
                Environment.GetEnvironmentVariables(
                    EnvironmentVariableTarget.Process);

            var namesToRemove = new List<string>();

            foreach (DictionaryEntry variable in variables)
            {
                string name = variable.Key.ToString();
                string value = variable.Value as string ?? "";

                if (name.Equals("PATH", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (IsSteamVariable(name) || ValuePointsAtSteam(value))
                    namesToRemove.Add(name);
            }

            foreach (string name in namesToRemove)
            {
                Environment.SetEnvironmentVariable(
                    name,
                    null,
                    EnvironmentVariableTarget.Process);
            }

            StripSteamFromPath();

            var overlay = new Thread(WatchOverlay);
            overlay.IsBackground = true;
            overlay.Name = "Steam overlay disarm";
            overlay.Start();
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
                if (HooksRemoved > 0)
                    SalsaLogger.Steam("Steam overlay will not attach to new processes");
                else
                    SalsaLogger.Warn("Steam overlay is loaded, but its process hooks were not found.");
            }
        }

        private static void StripSteamFromPath()
        {
            string path = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Process);
            if (string.IsNullOrEmpty(path))
                return;

            string[] kept = path
                .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(entry => !ValuePointsAtSteam(entry))
                .ToArray();

            Environment.SetEnvironmentVariable(
                "PATH",
                string.Join(";", kept),
                EnvironmentVariableTarget.Process);
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

            string filePath = path.ToString();
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
        private static extern bool VirtualProtect(IntPtr address, UIntPtr size, uint newProtect, out uint oldProtect);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern UIntPtr VirtualQuery(IntPtr address, out MemoryBasicInformation buffer, UIntPtr length);

        [DllImport("kernel32.dll")]
        private static extern bool FlushInstructionCache(IntPtr process, IntPtr address, UIntPtr size);

        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationThread(IntPtr threadHandle, int threadInformationClass, out IntPtr threadInformation, IntPtr threadInformationLength, IntPtr returnLength);

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
