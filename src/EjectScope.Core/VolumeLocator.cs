using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using EjectScope.Core.Native;

namespace EjectScope.Core
{
    /// <summary>
    /// L0 + L1: identify the volume, prove whether it is USB, and resolve the
    /// drive letter to its NT device name.
    ///
    /// The NT device name is the foundation of the whole tool: every handle we
    /// find later is named \Device\HarddiskVolumeN\..., so without a correct
    /// mapping we would silently match nothing and report a false all-clear.
    /// </summary>
    public static class VolumeLocator
    {
        /// <summary>Accept "E", "E:", "E:\", "e:/" and return "E:".</summary>
        public static string NormalizeDriveSpec(string spec)
        {
            if (spec == null) throw new ArgumentNullException("spec");
            string s = spec.Trim().Trim('"');
            if (s.Length == 0) throw new ArgumentException("Empty drive specification.", "spec");

            // Full path form: E:\foo\bar
            if (s.Length >= 2 && s[1] == ':')
                s = s.Substring(0, 2);

            if (s.Length == 1) s = s + ":";
            if (s.Length != 2 || s[1] != ':')
                throw new ArgumentException("Expected a drive letter such as E or E:.", "spec");

            char letter = char.ToUpperInvariant(s[0]);
            if (letter < 'A' || letter > 'Z')
                throw new ArgumentException("Not a drive letter: " + spec, "spec");

            return letter.ToString() + ":";
        }

        /// <summary>All drive letters that currently have a mounted volume.</summary>
        public static List<string> EnumerateDriveLetters()
        {
            List<string> result = new List<string>();
            uint mask = Win32.GetLogicalDrives();
            for (int i = 0; i < 26; i++)
            {
                if ((mask & (1u << i)) == 0) continue;
                string root = ((char)('A' + i)).ToString() + ":\\";
                if (Win32.GetDriveTypeW(root) == Win32.DRIVE_NO_ROOT_DIR) continue;
                result.Add(((char)('A' + i)).ToString() + ":");
            }
            return result;
        }

        /// <summary>Describe one volume. Never throws for a valid but odd drive.</summary>
        public static VolumeInfo Describe(string driveSpec)
        {
            string spec = NormalizeDriveSpec(driveSpec);          // "E:"
            string root = spec + "\\";                            // "E:\"

            VolumeInfo info = new VolumeInfo();
            info.Letter = spec[0];
            info.RootPath = root;
            info.DriveType = Win32.GetDriveTypeW(root);
            info.NtDeviceName = QueryDosDevice(spec);

            ReadVolumeInformation(info, root);

            try
            {
                DriveInfo di = new DriveInfo(spec);
                if (di.IsReady)
                {
                    info.TotalBytes = di.TotalSize;
                    info.FreeBytes = di.TotalFreeSpace;
                    if (string.IsNullOrEmpty(info.Label)) info.Label = di.VolumeLabel;
                }
            }
            catch (IOException) { }
            catch (ArgumentException) { }

            // Bus type is the only reliable "is this really USB" signal:
            // a USB hard disk reports DRIVE_FIXED, so GetDriveType alone lies.
            BusTypeResult bus = QueryBusType(spec);
            info.BusTypeName = bus.Name;
            info.IsUsb = bus.IsUsb;

            if (!bus.Queried)
            {
                // No admin rights (or a very unusual device): fall back to the
                // weaker signal and say so through BusTypeName.
                info.IsUsb = info.DriveType == Win32.DRIVE_REMOVABLE;
            }

            info.DisplayName = BuildDisplayName(info);
            return info;
        }

        private static void ReadVolumeInformation(VolumeInfo info, string root)
        {
            char[] label = new char[261];
            char[] fsName = new char[261];
            uint serial, maxComponent, fsFlags;
            bool ok = Win32.GetVolumeInformationW(root, label, 261,
                out serial, out maxComponent, out fsFlags, fsName, 261);
            if (!ok) return;

            info.Label = TrimAtNul(label);
            info.FileSystem = TrimAtNul(fsName);
            info.SerialNumber = serial;
        }

        private static string QueryDosDevice(string spec)
        {
            char[] buffer = new char[1024];
            uint len = Win32.QueryDosDeviceW(spec, buffer, (uint)buffer.Length);
            if (len == 0) return null;
            return TrimAtNul(buffer);
        }

        private sealed class BusTypeResult
        {
            internal bool Queried;
            internal bool IsUsb;
            internal string Name;
        }

        private static BusTypeResult QueryBusType(string spec)
        {
            BusTypeResult result = new BusTypeResult();
            result.Name = "unknown";
            result.Queried = false;

            // Raw volume access needs administrator rights. Failing here is
            // expected for a non-elevated run and must never throw.
            IntPtr volume = Win32.CreateFileW(@"\\.\" + spec, Win32.GENERIC_READ,
                Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE, IntPtr.Zero,
                Win32.OPEN_EXISTING, 0, IntPtr.Zero);

            if (volume == IntPtr.Zero || volume == new IntPtr(-1))
                return result;

            try
            {
                List<int> disks = GetDiskNumbers(volume);
                if (disks.Count == 0) return result;

                int disk = disks[0];
                result.Queried = true;
                result.Name = "disk " + disk.ToString();

                uint busType;
                if (TryGetBusType(disk, out busType))
                {
                    result.IsUsb = busType == Win32.BusTypeUsb;
                    result.Name = BusTypeName(busType) + " (disk " + disk.ToString() + ")";
                }
            }
            finally
            {
                Win32.CloseHandle(volume);
            }

            return result;
        }

        private static List<int> GetDiskNumbers(IntPtr volumeHandle)
        {
            List<int> disks = new List<int>();

            // VOLUME_DISK_EXTENTS: DWORD count, then count * DISK_EXTENT (24 bytes)
            const int bufferSize = 512;
            IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
            try
            {
                uint returned;
                bool ok = Win32.DeviceIoControl(volumeHandle,
                    Win32.IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS,
                    IntPtr.Zero, 0, buffer, bufferSize, out returned, IntPtr.Zero);
                if (!ok) return disks;

                int count = Marshal.ReadInt32(buffer, 0);
                int offset = 8; // header is 8 bytes on x64 due to alignment
                for (int i = 0; i < count; i++)
                {
                    int diskNumber = Marshal.ReadInt32(buffer, offset + i * 24);
                    disks.Add(diskNumber);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            return disks;
        }

        private static bool TryGetBusType(int diskNumber, out uint busType)
        {
            busType = 0;
            IntPtr disk = Win32.CreateFileW(@"\\.\PhysicalDrive" + diskNumber.ToString(),
                0, Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE, IntPtr.Zero,
                Win32.OPEN_EXISTING, 0, IntPtr.Zero);

            if (disk == IntPtr.Zero || disk == new IntPtr(-1))
                return false;

            IntPtr query = IntPtr.Zero;
            IntPtr output = IntPtr.Zero;
            try
            {
                int querySize = Marshal.SizeOf(typeof(Win32.STORAGE_PROPERTY_QUERY));
                query = Marshal.AllocHGlobal(querySize);
                output = Marshal.AllocHGlobal(1024);

                // PropertyId = 0 (StorageDeviceProperty), QueryType = 0 (Standard)
                for (int i = 0; i < querySize; i++) Marshal.WriteByte(query, i, 0);

                uint returned;
                bool ok = Win32.DeviceIoControl(disk, Win32.IOCTL_STORAGE_QUERY_PROPERTY,
                    query, (uint)querySize, output, 1024, out returned, IntPtr.Zero);
                if (!ok) return false;

                Win32.STORAGE_DEVICE_DESCRIPTOR desc =
                    (Win32.STORAGE_DEVICE_DESCRIPTOR)Marshal.PtrToStructure(
                        output, typeof(Win32.STORAGE_DEVICE_DESCRIPTOR));

                busType = desc.BusType;
                return true;
            }
            finally
            {
                if (query != IntPtr.Zero) Marshal.FreeHGlobal(query);
                if (output != IntPtr.Zero) Marshal.FreeHGlobal(output);
                Win32.CloseHandle(disk);
            }
        }

        private static string BusTypeName(uint busType)
        {
            switch (busType)
            {
                case 0: return "unknown";
                case 1: return "SCSI";
                case 2: return "ATAPI";
                case 3: return "ATA";
                case 4: return "IEEE1394";
                case 5: return "SSA";
                case 6: return "Fibre";
                case 7: return "USB";
                case 8: return "RAID";
                case 9: return "iSCSI";
                case 10: return "SAS";
                case 11: return "SATA";
                case 12: return "SD";
                case 13: return "MMC";
                case 14: return "Virtual";
                case 15: return "FileBackedVirtual";
                case 16: return "Spaces";
                case 17: return "NVMe";
                case 18: return "SCM";
                default: return "bus " + busType.ToString();
            }
        }

        private static string BuildDisplayName(VolumeInfo info)
        {
            StringBuilder sb = new StringBuilder();
            if (!string.IsNullOrEmpty(info.Label)) sb.Append(info.Label);
            else sb.Append(info.IsUsb ? "USB drive" : "Local Disk");
            sb.Append(" (").Append(info.Letter).Append(":)");
            return sb.ToString();
        }

        private static string TrimAtNul(char[] buffer)
        {
            int end = Array.IndexOf(buffer, '\0');
            if (end < 0) end = buffer.Length;
            return new string(buffer, 0, end);
        }
    }
}
