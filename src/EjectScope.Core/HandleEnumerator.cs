using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Runtime.InteropServices;
using EjectScope.Core.Native;

namespace EjectScope.Core
{
    /// <summary>
    /// L3: enumerate every open handle on the machine and keep the ones that
    /// point into the target volume.
    ///
    /// This is the only approach that sees background processes with no window
    /// and no file path in their command line — which is exactly the case that
    /// makes "U盘弹不出" so confusing for users.
    ///
    /// Requires administrator rights. When a process cannot be opened we count
    /// it and say so; we never mark it "not holding the volume".
    /// </summary>
    public static class HandleEnumerator
    {
        private const int MaxSamplesPerHolder = 32;
        private const int DefaultNameTimeoutMs = 250;

        // Version-to-version the handle table changes owners; the only process we
        // are guaranteed to be able to open is our own, so type-name probes
        // prefer our handles.
        private static readonly int CurrentPid = Process.GetCurrentProcess().Id;

        private struct Candidate
        {
            internal int Pid;
            internal IntPtr Handle;
            internal uint GrantedAccess;
        }

        public static void Scan(VolumeInfo volume, Diagnosis diag)
        {
            Scan(volume, diag, DefaultNameTimeoutMs);
        }

        public static void Scan(VolumeInfo volume, Diagnosis diag, int nameTimeoutMs)
        {
            if (volume == null) throw new ArgumentNullException("volume");
            if (diag == null) throw new ArgumentNullException("diag");
            if (string.IsNullOrEmpty(volume.NtDeviceName))
            {
                // Without the NT name every comparison would silently fail.
                // That is the worst possible failure mode: a confident all-clear.
                diag.Verdict = Verdict.Unknown;
                diag.ExclusiveOpenDetail = "Cannot resolve the NT device name for " + volume.RootPath;
                return;
            }

            List<Candidate> candidates = SnapshotFileHandles(diag);
            if (candidates.Count == 0) return;

            NameQueryService names = new NameQueryService(volume.SerialNumber, volume.NtDeviceName);
            Dictionary<int, Holder> holders = new Dictionary<int, Holder>();
            Dictionary<int, IntPtr> processes = new Dictionary<int, IntPtr>();

            try
            {
                foreach (Candidate c in candidates)
                {
                    IntPtr proc;
                    if (!processes.TryGetValue(c.Pid, out proc))
                    {
                        proc = Win32.OpenProcess(Win32.PROCESS_DUP_HANDLE, false, (uint)c.Pid);
                        processes[c.Pid] = proc;

                        if (proc == IntPtr.Zero)
                        {
                            // Protected process (PPL), System, or gone.
                            // Count it once per process, never per handle.
                            diag.InaccessibleProcessCount++;
                            continue;
                        }
                    }

                    if (proc == IntPtr.Zero) continue;

                    IntPtr dup;
                    if (!Win32.DuplicateHandle(proc, c.Handle, Win32.GetCurrentProcess(),
                            out dup, 0, false, Win32.DUPLICATE_SAME_ACCESS))
                    {
                        diag.UnresolvedHandleCount++;
                        continue;
                    }

                    // One guarded round trip per handle: the worker asks the
                    // kernel which volume the object belongs to, and only pays
                    // for a name lookup when a name is needed to place it.
                    bool timedOut;
                    HandleProbe probe = names.Query(dup, nameTimeoutMs, out timedOut);

                    if (timedOut)
                    {
                        diag.HadNameTimeout = true;
                        // dup is intentionally not closed; the stuck worker may own it.
                        continue;
                    }

                    if (probe.SerialKnown)
                    {
                        if (!probe.OnTarget)
                        {
                            Win32.CloseHandle(dup);
                            continue;
                        }

                        // Proven to be on our volume. A missing name costs us
                        // only the pretty path, never the finding.
                        Record(holders, c, probe.Name, dup, volume);
                        Win32.CloseHandle(dup);
                        continue;
                    }

                    if (probe.NotAVolumeObject)
                    {
                        // Definitely not a file on a volume - pipes, consoles,
                        // devices. Dismissing these costs no coverage.
                        Win32.CloseHandle(dup);
                        continue;
                    }

                    if (probe.Name == null)
                    {
                        // The object refused both a name and a volume. We cannot
                        // say it is irrelevant, so it counts against coverage.
                        diag.UnresolvedHandleCount++;
                        Win32.CloseHandle(dup);
                        continue;
                    }

                    if (!probe.OnTarget)
                    {
                        Win32.CloseHandle(dup);
                        continue;
                    }

                    Record(holders, c, probe.Name, dup, volume);
                    Win32.CloseHandle(dup);

                }
            }
            finally
            {
                foreach (KeyValuePair<int, IntPtr> kv in processes)
                {
                    if (kv.Value != IntPtr.Zero) Win32.CloseHandle(kv.Value);
                }
            }

            diag.UnresolvedHandleCount += names.TimeoutCount;

            List<Holder> list = new List<Holder>(holders.Values);
            list.Sort(delegate(Holder a, Holder b)
            {
                int byAccess = ((int)b.Access).CompareTo((int)a.Access);
                if (byAccess != 0) return byAccess;
                return string.Compare(a.ProcessName, b.ProcessName, StringComparison.OrdinalIgnoreCase);
            });
            diag.Holders.Clear();
            diag.Holders.AddRange(list);
        }

        /// <summary>
        /// Match \Device\HarddiskVolume3 against names like
        /// \Device\HarddiskVolume3\Users\... , never against \Device\HarddiskVolume31.
        /// </summary>
        internal static bool MatchesVolume(string ntName, string deviceName)
        {
            if (string.IsNullOrEmpty(ntName) || string.IsNullOrEmpty(deviceName)) return false;

            if (ntName.Length < deviceName.Length) return false;
            if (string.Compare(ntName, 0, deviceName, 0, deviceName.Length,
                    StringComparison.OrdinalIgnoreCase) != 0)
                return false;

            if (ntName.Length == deviceName.Length) return true;   // the volume itself
            return ntName[deviceName.Length] == '\\';
        }

        private static void Record(Dictionary<int, Holder> holders, Candidate c,
            string ntName, IntPtr dupHandle, VolumeInfo volume)
        {
            Holder holder;
            if (!holders.TryGetValue(c.Pid, out holder))
            {
                holder = CreateHolder(c.Pid);
                holders[c.Pid] = holder;
            }

            holder.HandleCount++;

            HolderAccess access = MapAccess(c.GrantedAccess);
            if (access > holder.Access) holder.Access = access;

            if (holder.Samples.Count < MaxSamplesPerHolder)
            {
                HandleRef hr = new HandleRef();
                hr.NtPath = ntName;
                hr.Access = access;
                hr.IsVolumeRoot = ntName != null &&
                    ntName.Length == volume.NtDeviceName.Length;
                hr.DosPath = TryGetDosPath(dupHandle);
                holder.Samples.Add(hr);
            }
        }

        private static Holder CreateHolder(int pid)
        {
            Holder h = new Holder();
            h.ProcessId = pid;
            h.ProcessName = "pid " + pid.ToString();
            h.ProcessPath = null;

            try
            {
                using (Process p = Process.GetProcessById(pid))
                {
                    h.ProcessName = p.ProcessName;
                    try
                    {
                        h.ProcessPath = p.MainModule.FileName;
                    }
                    catch
                    {
                        h.ProcessPath = null; // protected process: name only
                    }
                }
            }
            catch
            {
                // Process exited between snapshot and lookup.
            }

            if (!string.IsNullOrEmpty(h.ProcessPath))
            {
                try
                {
                    FileVersionInfo vi = FileVersionInfo.GetVersionInfo(h.ProcessPath);
                    h.CompanyName = vi.CompanyName;
                }
                catch { }
            }

            return h;
        }

        /// <summary>
        /// Reduces the raw granted-access mask to the three levels a user can act on.
        /// The raw mask is deliberately not shown: 0x12019F means nothing to anyone.
        /// </summary>
        internal static HolderAccess MapAccess(uint granted)
        {
            const uint FILE_WRITE_DATA = 0x0002;
            const uint FILE_APPEND_DATA = 0x0004;
            const uint GENERIC_WRITE = 0x40000000;
            const uint GENERIC_ALL = 0x10000000;

            const uint FILE_READ_DATA = 0x0001;
            const uint GENERIC_READ = 0x80000000;

            bool canWriteData = (granted & (FILE_WRITE_DATA | FILE_APPEND_DATA | GENERIC_WRITE | GENERIC_ALL)) != 0;
            bool canReadData = (granted & (FILE_READ_DATA | GENERIC_READ | GENERIC_ALL)) != 0;

            if (canWriteData && canReadData) return HolderAccess.ReadWrite;
            if (canWriteData) return HolderAccess.WriteOnly;
            if (canReadData) return HolderAccess.ReadOnly;
            return HolderAccess.Unknown;
        }

        private static string TryGetDosPath(IntPtr handle)
        {
            const uint FILE_NAME_NORMALIZED = 0x0;
            const uint VOLUME_NAME_DOS = 0x0;

            char[] buffer = new char[1024];
            uint len = Win32.GetFinalPathNameByHandleW(handle, buffer,
                (uint)buffer.Length, FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);
            if (len == 0 || len >= buffer.Length) return null;

            string path = new string(buffer, 0, (int)len);
            if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
                return @"\\" + path.Substring(8);
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
                return path.Substring(4);
            return path;
        }

        private static List<Candidate> SnapshotFileHandles(Diagnosis diag)
        {
            List<Candidate> result = new List<Candidate>();

            int size = 1 << 20; // 1 MB
            IntPtr buffer = IntPtr.Zero;
            int count = 0;
            int entrySize = Marshal.SizeOf(typeof(Nt.SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX));

            try
            {
                for (int attempt = 0; attempt < 8; attempt++)
                {
                    buffer = Marshal.AllocHGlobal(size);
                    int returned;
                    int status = Nt.NtQuerySystemInformation(Nt.SystemExtendedHandleInformation,
                        buffer, size, out returned);

                    if (status == Nt.STATUS_INFO_LENGTH_MISMATCH)
                    {
                        Marshal.FreeHGlobal(buffer);
                        buffer = IntPtr.Zero;
                        size = Math.Max(returned + (256 * 1024), size * 2);
                        continue;
                    }

                    if (status != Nt.STATUS_SUCCESS)
                    {
                        diag.UnresolvedHandleCount++;
                        return result;
                    }

                    count = (int)Marshal.ReadIntPtr(buffer, 0).ToInt64();
                    break;
                }

                if (buffer == IntPtr.Zero) return result;

                int offset = IntPtr.Size * 2; // header: NumberOfHandles + Reserved

                // Resolve object type indices from the handles themselves instead of
                // hardcoding "File = 5", which differs between Windows builds.
                // Pass 1: up to 8 sample handles per distinct type index, because the
                // first handle of a type often belongs to a process we cannot open.
                Dictionary<ushort, List<KeyValuePair<IntPtr, IntPtr>>> samples =
                    new Dictionary<ushort, List<KeyValuePair<IntPtr, IntPtr>>>();

                for (int i = 0; i < count; i++)
                {
                    IntPtr entryPtr = new IntPtr(buffer.ToInt64() + offset + i * entrySize);
                    Nt.SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX e =
                        (Nt.SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX)Marshal.PtrToStructure(
                            entryPtr, typeof(Nt.SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX));

                    List<KeyValuePair<IntPtr, IntPtr>> list;
                    if (!samples.TryGetValue(e.ObjectTypeIndex, out list))
                    {
                        list = new List<KeyValuePair<IntPtr, IntPtr>>();
                        samples[e.ObjectTypeIndex] = list;
                    }

                    AddSample(samples, e.ObjectTypeIndex, e.UniqueProcessId, e.HandleValue);
                }

                ushort fileTypeIndex = 0;
                bool fileTypeKnown = false;

                foreach (KeyValuePair<ushort, List<KeyValuePair<IntPtr, IntPtr>>> kv in samples)
                {
                    string typeName = null;
                    foreach (KeyValuePair<IntPtr, IntPtr> s in kv.Value)
                    {
                        typeName = GetTypeName((int)s.Key.ToInt64(), s.Value);
                        if (typeName != null) break;
                    }

                    if (!fileTypeKnown && typeName == "File")
                    {
                        fileTypeIndex = kv.Key;
                        fileTypeKnown = true;
                    }
                }

                if (!fileTypeKnown)
                {
                    // We could not prove which type index is "File", so any answer
                    // would be a guess. Refuse to guess.
                    diag.UnresolvedHandleCount++;
                    return result;
                }

                // Pass 2: keep only file handles.
                for (int i = 0; i < count; i++)
                {
                    IntPtr entryPtr = new IntPtr(buffer.ToInt64() + offset + i * entrySize);
                    Nt.SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX e =
                        (Nt.SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX)Marshal.PtrToStructure(
                            entryPtr, typeof(Nt.SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX));

                    if (e.ObjectTypeIndex != fileTypeIndex) continue;

                    Candidate c = new Candidate();
                    c.Pid = (int)e.UniqueProcessId.ToInt64();
                    c.Handle = e.HandleValue;
                    c.GrantedAccess = e.GrantedAccess;

                    if (c.Pid <= 0) continue; // Idle
                    result.Add(c);
                }
            }
            finally
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            }

            return result;
        }

        /// <summary>
        /// Diagnostic: every object type index in the handle table, with the name
        /// we managed to resolve for it. Backs `ejectscope --dump-types`, which is
        /// how a contributor verifies the type-index probe on a Windows build we
        /// have never seen (indices are not contractual).
        /// </summary>
        public static string DumpTypes()
        {
            StringBuilder sb = new StringBuilder();
            int size = 1 << 20;
            IntPtr buffer = IntPtr.Zero;
            int count = 0;
            bool ok = false;
            int entrySize = Marshal.SizeOf(typeof(Nt.SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX));

            try
            {
                for (int attempt = 0; attempt < 8; attempt++)
                {
                    buffer = Marshal.AllocHGlobal(size);
                    int returned;
                    int status = Nt.NtQuerySystemInformation(Nt.SystemExtendedHandleInformation,
                        buffer, size, out returned);

                    if (status == Nt.STATUS_INFO_LENGTH_MISMATCH)
                    {
                        Marshal.FreeHGlobal(buffer);
                        buffer = IntPtr.Zero;
                        size = Math.Max(returned + (256 * 1024), size * 2);
                        continue;
                    }
                    if (status != Nt.STATUS_SUCCESS)
                    {
                        sb.AppendLine("NtQuerySystemInformation failed: 0x" + status.ToString("X8"));
                        return sb.ToString();
                    }

                    count = (int)Marshal.ReadIntPtr(buffer, 0).ToInt64();
                    ok = true;
                    break;
                }

                if (!ok)
                {
                    sb.AppendLine("Could not size the handle table buffer.");
                    return sb.ToString();
                }

                sb.AppendLine("total handles : " + count);
                sb.AppendLine("entry size    : " + entrySize);
                sb.AppendLine();

                Dictionary<ushort, List<KeyValuePair<IntPtr, IntPtr>>> samples =
                    new Dictionary<ushort, List<KeyValuePair<IntPtr, IntPtr>>>();
                Dictionary<ushort, int> counts = new Dictionary<ushort, int>();

                int offset = IntPtr.Size * 2;
                for (int i = 0; i < count; i++)
                {
                    IntPtr entryPtr = new IntPtr(buffer.ToInt64() + offset + i * entrySize);
                    Nt.SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX e =
                        (Nt.SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX)Marshal.PtrToStructure(
                            entryPtr, typeof(Nt.SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX));

                    int n;
                    counts.TryGetValue(e.ObjectTypeIndex, out n);
                    counts[e.ObjectTypeIndex] = n + 1;

                    List<KeyValuePair<IntPtr, IntPtr>> list;
                    if (!samples.TryGetValue(e.ObjectTypeIndex, out list))
                    {
                        list = new List<KeyValuePair<IntPtr, IntPtr>>();
                        samples[e.ObjectTypeIndex] = list;
                    }
                    AddSample(samples, e.ObjectTypeIndex, e.UniqueProcessId, e.HandleValue);
                }

                sb.AppendLine("idx    count   type name");
                sb.AppendLine("-----  ------  ------------------------------");

                List<ushort> keys = new List<ushort>(samples.Keys);
                keys.Sort();
                foreach (ushort idx in keys)
                {
                    string name = null;
                    foreach (KeyValuePair<IntPtr, IntPtr> s in samples[idx])
                    {
                        name = GetTypeName((int)s.Key.ToInt64(), s.Value);
                        if (name != null) break;
                    }

                    sb.AppendLine(string.Format("{0,-5}  {1,6}  {2}",
                        idx, counts[idx], name == null ? "<unresolved>" : name));
                }
            }
            finally
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            }

            return sb.ToString();
        }

        private static void AddSample(
            Dictionary<ushort, List<KeyValuePair<IntPtr, IntPtr>>> samples,
            ushort index, IntPtr pid, IntPtr handle)
        {
            List<KeyValuePair<IntPtr, IntPtr>> list;
            if (!samples.TryGetValue(index, out list))
            {
                list = new List<KeyValuePair<IntPtr, IntPtr>>();
                samples[index] = list;
            }

            KeyValuePair<IntPtr, IntPtr> sample =
                new KeyValuePair<IntPtr, IntPtr>(pid, handle);

            // Our own handles must win over the cap: they are the only ones we
            // are guaranteed to be able to duplicate, so letting foreign samples
            // fill the list first would leave the type permanently unnamed.
            if (pid.ToInt64() == CurrentPid)
            {
                list.Insert(0, sample);
                if (list.Count > 256) list.RemoveAt(list.Count - 1);
                return;
            }

            if (list.Count < 64) list.Add(sample);
        }

        private static string GetTypeName(int pid, IntPtr handle)
        {
            IntPtr proc = Win32.OpenProcess(Win32.PROCESS_DUP_HANDLE, false, (uint)pid);
            if (proc == IntPtr.Zero) return null;

            try
            {
                IntPtr dup;
                if (!Win32.DuplicateHandle(proc, handle, Win32.GetCurrentProcess(),
                        out dup, 0, false, Win32.DUPLICATE_SAME_ACCESS))
                    return null;

                try
                {
                    IntPtr buf = Marshal.AllocHGlobal(1024);
                    try
                    {
                        int returned;
                        if (Nt.NtQueryObject(dup, Nt.ObjectTypeInformation, buf, 1024, out returned) != 0)
                            return null;

                        Nt.UNICODE_STRING us = (Nt.UNICODE_STRING)Marshal.PtrToStructure(
                            buf, typeof(Nt.UNICODE_STRING));
                        if (us.Length == 0 || us.Buffer == IntPtr.Zero) return null;
                        return Marshal.PtrToStringUni(us.Buffer, us.Length / 2);
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buf);
                    }
                }
                finally
                {
                    Win32.CloseHandle(dup);
                }
            }
            finally
            {
                Win32.CloseHandle(proc);
            }
        }
    }
}
