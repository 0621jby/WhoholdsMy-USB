using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using EjectScope.Core.Native;

namespace EjectScope.Core
{
    /// <summary>
    /// Result of inspecting one duplicated handle.
    /// </summary>
    internal struct HandleProbe
    {
        /// <summary>Volume serial, when the object is something the filesystem can describe.</summary>
        internal uint Serial;
        internal bool SerialKnown;

        /// <summary>True when the object lives on the volume we are diagnosing.</summary>
        internal bool OnTarget;

        /// <summary>NT object name; null when it could not be read.</summary>
        internal string Name;

        /// <summary>
        /// True when the object is categorically not a file on a volume
        /// (a pipe, a console, a device). Such a handle can be dismissed
        /// without costing coverage.
        /// </summary>
        internal bool NotAVolumeObject;
    }

    /// <summary>
    /// Inspects handles on a worker thread so that a blocking kernel call can be
    /// abandoned instead of hanging the scan.
    ///
    /// Why this exists (design section 3.2, pitfall A): NtQueryObject on a
    /// synchronous named pipe blocks forever waiting for a pending operation
    /// to complete. Sysinternals' handle.exe hit this for years. The same is
    /// true of GetFileInformationByHandle on some device handles, which is why
    /// *both* calls happen here and not on the scan thread.
    ///
    /// Strategy: a single sacrificial worker thread. If a call does not come
    /// back in time, we stop trusting that thread, spawn a fresh one, and mark
    /// the scan as incomplete. The stuck thread is left to die with the process.
    /// </summary>
    internal sealed class NameQueryService
    {
        private sealed class Request
        {
            internal IntPtr Handle;
            internal HandleProbe Result;
            internal readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
        }

        private readonly uint _targetSerial;
        private readonly string _targetDeviceName;

        private BlockingCollection<Request> _queue;
        private Thread _worker;
        private int _timeouts;

        internal NameQueryService(uint targetSerial)
            : this(targetSerial, null)
        {
        }

        internal NameQueryService(uint targetSerial, string targetDeviceName)
        {
            _targetSerial = targetSerial;
            _targetDeviceName = targetDeviceName;
            Spawn();
        }

        internal int TimeoutCount
        {
            get { return _timeouts; }
        }

        /// <summary>
        /// Asks the worker what this handle is: which volume it belongs to, and
        /// its object name when a name is needed to place it.
        ///
        /// On timeout the handle is intentionally leaked: the abandoned worker
        /// may still be using it, and closing it underneath would be worse.
        /// </summary>
        internal HandleProbe Query(IntPtr handle, int timeoutMs, out bool timedOut)
        {
            Request req = new Request();
            req.Handle = handle;

            BlockingCollection<Request> queue = _queue;
            try
            {
                queue.Add(req);
            }
            catch (InvalidOperationException)
            {
                // Queue was completed by a racing restart.
                timedOut = true;
                return new HandleProbe();
            }

            if (req.Done.Wait(timeoutMs))
            {
                timedOut = false;
                return req.Result;
            }

            timedOut = true;
            Interlocked.Increment(ref _timeouts);
            Restart();
            return new HandleProbe();
        }

        private void Restart()
        {
            // The old worker keeps its own queue and will never touch the new one.
            BlockingCollection<Request> old = _queue;
            if (old != null) old.CompleteAdding();
            Spawn();
        }

        private void Spawn()
        {
            BlockingCollection<Request> queue = new BlockingCollection<Request>();
            _queue = queue;

            Thread t = new Thread(delegate()
            {
                foreach (Request req in queue.GetConsumingEnumerable())
                {
                    try
                    {
                        req.Result = Inspect(req.Handle);
                    }
                    catch
                    {
                        req.Result = new HandleProbe();
                    }
                    finally
                    {
                        req.Done.Set();
                    }
                }
            });

            t.IsBackground = true;
            t.Name = "EjectScope.HandleProbe";
            _worker = t;
            t.Start();
        }

        private HandleProbe Inspect(IntPtr handle)
        {
            HandleProbe probe = new HandleProbe();

            // A pipe or a console handle cannot hold a file on a volume. This
            // also keeps the classic NtQueryObject-on-a-pipe hang out of the
            // picture for the most common case.
            uint fileType = Win32.GetFileType(handle);
            if (fileType != Win32.FILE_TYPE_DISK)
            {
                probe.NotAVolumeObject = true;
                return probe;
            }

            uint serial;
            probe.SerialKnown = Win32.TryGetVolumeSerial(handle, out serial);
            probe.Serial = serial;

            if (probe.SerialKnown)
            {
                // Cheap placement: a serial mismatch means "some other drive",
                // and we never pay for a name lookup in that case.
                probe.OnTarget = _targetSerial != 0 && serial == _targetSerial;
                if (probe.OnTarget) probe.Name = ResolveName(handle);
                return probe;
            }

            // Not something the filesystem can describe: raw volume handles
            // (Everything, chkdsk, a backup tool) end up here and only a name
            // can place them.
            probe.Name = ResolveName(handle);
            probe.OnTarget = _targetDeviceName != null &&
                             HandleEnumerator.MatchesVolume(probe.Name, _targetDeviceName);
            return probe;
        }

        private static string ResolveName(IntPtr handle)
        {
            // 4 KB is enough for any path; ObjectNameInformation returns a
            // UNICODE_STRING whose Buffer points into a buffer we could not
            // size in advance, so retry once with a larger block.
            int size = 4096;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                IntPtr buffer = Marshal.AllocHGlobal(size);
                try
                {
                    int returned;
                    int status = Nt.NtQueryObject(handle, Nt.ObjectNameInformation,
                        buffer, size, out returned);

                    if (status == Nt.STATUS_INFO_LENGTH_MISMATCH)
                    {
                        size = Math.Max(returned + 1024, size * 2);
                        continue;
                    }
                    if (status != Nt.STATUS_SUCCESS) return null;

                    Nt.UNICODE_STRING us = (Nt.UNICODE_STRING)Marshal.PtrToStructure(
                        buffer, typeof(Nt.UNICODE_STRING));

                    if (us.Length == 0 || us.Buffer == IntPtr.Zero) return null;
                    return Marshal.PtrToStringUni(us.Buffer, us.Length / 2);
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }

            return null;
        }
    }
}
