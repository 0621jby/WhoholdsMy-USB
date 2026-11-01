using System;
using System.Runtime.InteropServices;
using EjectScope.Core.Native;

namespace EjectScope.Core
{
    /// <summary>
    /// L4: try to take the volume exclusively.
    ///
    /// This is the only probe that can honestly answer "is anything holding it?"
    /// without enumerating processes, and it is also the probe that lets the
    /// tool say "this is not a software problem" (design case 12) — a conclusion
    /// that is worthless if we are too timid to ever return it.
    ///
    /// NOTE: no FSCTL_LOCK_VOLUME / FSCTL_DISMOUNT_VOLUME anywhere in this file,
    /// by design. We open, we look, we close.
    /// </summary>
    public static class ExclusiveOpenProbe
    {
        private const int ERROR_WRITE_PROTECT = 19;

        public static ExclusiveOpenResult Probe(string driveSpec, out string detail)
        {
            string spec = VolumeLocator.NormalizeDriveSpec(driveSpec);
            string devicePath = @"\\.\" + spec;

            // Pass 1: read+write, share mode 0. Success means nobody holds it.
            IntPtr h = Win32.CreateFileW(devicePath,
                Win32.GENERIC_READ | Win32.GENERIC_WRITE,
                0, IntPtr.Zero, Win32.OPEN_EXISTING, 0, IntPtr.Zero);

            if (h != IntPtr.Zero && h != new IntPtr(-1))
            {
                Win32.CloseHandle(h);
                detail = "Opened " + devicePath + " exclusively with read/write access.";
                return ExclusiveOpenResult.NoHolders;
            }

            int err = Marshal.GetLastWin32Error();

            if (err == Win32.ERROR_SHARING_VIOLATION)
            {
                detail = "ERROR_SHARING_VIOLATION (32) on " + devicePath + ": something holds this volume.";
                return ExclusiveOpenResult.SharingViolation;
            }

            // ERROR_ACCESS_DENIED / ERROR_WRITE_PROTECT can mean either
            //   (a) read-only media, or (b) lack of rights — not necessarily "in use".
            // Retry read-only-exclusive to tell them apart.
            if (err == Win32.ERROR_ACCESS_DENIED || err == ERROR_WRITE_PROTECT)
            {
                IntPtr ro = Win32.CreateFileW(devicePath, Win32.GENERIC_READ,
                    0, IntPtr.Zero, Win32.OPEN_EXISTING, 0, IntPtr.Zero);

                if (ro != IntPtr.Zero && ro != new IntPtr(-1))
                {
                    Win32.CloseHandle(ro);
                    detail = "Read-only exclusive open succeeded (media is write-protected, but not in use).";
                    return ExclusiveOpenResult.NoHolders;
                }

                int err2 = Marshal.GetLastWin32Error();
                if (err2 == Win32.ERROR_SHARING_VIOLATION)
                {
                    detail = "ERROR_SHARING_VIOLATION (32) on read-only probe: volume is in use.";
                    return ExclusiveOpenResult.SharingViolation;
                }

                detail = "ERROR_ACCESS_DENIED (" + err2.ToString() + "). Run as administrator for a reliable answer.";
                return ExclusiveOpenResult.AccessDenied;
            }

            detail = "CreateFile failed with error " + err.ToString() + ".";
            return ExclusiveOpenResult.Failed;
        }
    }
}
