using System;
using System.Runtime.InteropServices;

namespace EjectScope.Core.Native
{
    /// <summary>
    /// kernel32 / usermode surface. Intentionally minimal: this file is the
    /// single place where "what this program is allowed to touch" is visible.
    ///
    /// Deliberately absent, and must stay absent:
    ///   FSCTL_LOCK_VOLUME / FSCTL_DISMOUNT_VOLUME / IOCTL_STORAGE_EJECT_MEDIA
    ///   ReadFile on user data, CreateRemoteThread, WriteProcessMemory, SetWindowsHookEx
    /// See docs/DESIGN.md section 4 (safety invariants) before adding anything.
    /// </summary>
    internal static class Win32
    {
        // ---- process access ----
        internal const uint PROCESS_DUP_HANDLE = 0x0040;
        internal const uint PROCESS_QUERY_INFORMATION = 0x0400;

        // ---- handle duplication ----
        internal const uint DUPLICATE_SAME_ACCESS = 0x0002;

        // ---- CreateFile ----
        internal const uint GENERIC_READ = 0x80000000;
        internal const uint GENERIC_WRITE = 0x40000000;
        internal const uint FILE_SHARE_READ = 0x00000001;
        internal const uint FILE_SHARE_WRITE = 0x00000002;
        internal const uint OPEN_EXISTING = 3;
        internal const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;

        // ---- error codes we branch on ----
        internal const int ERROR_FILE_NOT_FOUND = 2;
        internal const int ERROR_ACCESS_DENIED = 5;
        internal const int ERROR_NOT_READY = 21;
        internal const int ERROR_SHARING_VIOLATION = 32;
        internal const int ERROR_NO_MORE_FILES = 18;
        internal const int ERROR_MORE_DATA = 234;

        // ---- volume / device ioctl ----
        internal const uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x00560000;
        internal const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;

        internal const uint IOCTL_STORAGE_QUERY_PROPERTY_BUS_TYPE = 0; // StorageDeviceProperty

        // ---- GetDriveType ----
        internal const uint DRIVE_UNKNOWN = 0;
        internal const uint DRIVE_NO_ROOT_DIR = 1;
        internal const uint DRIVE_REMOVABLE = 2;
        internal const uint DRIVE_FIXED = 3;
        internal const uint DRIVE_REMOTE = 4;
        internal const uint DRIVE_CDROM = 5;
        internal const uint DRIVE_RAMDISK = 6;

        // ---- BusType (STORAGE_DEVICE_DESCRIPTOR.BusType) ----
        internal const uint BusTypeUsb = 0x07;

        [StructLayout(LayoutKind.Sequential)]
        internal struct DISK_EXTENT
        {
            internal uint DiskNumber;
            internal int Padding;          // keeps StartingOffset 8-byte aligned
            internal long StartingOffset;
            internal long ExtentLength;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct VOLUME_DISK_EXTENTS_HEADER
        {
            internal uint NumberOfDiskExtents;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct STORAGE_DEVICE_DESCRIPTOR
        {
            internal uint Version;
            internal uint Size;
            internal byte DeviceType;
            internal byte DeviceTypeModifier;
            internal byte RemovableMedia;
            internal byte CommandQueueing;
            internal uint VendorIdOffset;
            internal uint ProductIdOffset;
            internal uint ProductRevisionOffset;
            internal uint SerialNumberOffset;
            internal uint BusType;
            internal uint RawPropertiesLength;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct STORAGE_PROPERTY_QUERY
        {
            internal uint PropertyId;
            internal uint QueryType;
            internal byte AdditionalParameters;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr CreateFileW(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DuplicateHandle(
            IntPtr hSourceProcessHandle,
            IntPtr hSourceHandle,
            IntPtr hTargetProcessHandle,
            out IntPtr lpTargetHandle,
            uint dwDesiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle,
            uint dwOptions);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr OpenProcess(uint dwDesiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeviceIoControl(
            IntPtr hDevice, uint dwIoControlCode,
            IntPtr lpInBuffer, uint nInBufferSize,
            IntPtr lpOutBuffer, uint nOutBufferSize,
            out uint lpBytesReturned, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern uint GetDriveTypeW(string lpRootPathName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern uint QueryDosDeviceW(string lpDeviceName, char[] lpTargetPath, uint ucchMax);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetVolumeInformationW(
            string lpRootPathName, char[] lpVolumeNameBuffer, uint nVolumeNameSize,
            out uint lpVolumeSerialNumber, out uint lpMaximumComponentLength,
            out uint lpFileSystemFlags, char[] lpFileSystemNameBuffer, uint nFileSystemNameSize);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern uint GetFinalPathNameByHandleW(
            IntPtr hFile, char[] lpszFilePath, uint cchFilePath, uint dwFlags);

        /// <summary>
        /// The cheap way to ask "which volume does this handle live on?".
        /// It also works for volume and directory handles, which have no
        /// path a name query can return.
        /// </summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint GetFileType(IntPtr hFile);

        internal const uint FILE_TYPE_UNKNOWN = 0x0000;
        internal const uint FILE_TYPE_DISK = 0x0001;
        internal const uint FILE_TYPE_CHAR = 0x0002;
        internal const uint FILE_TYPE_PIPE = 0x0003;

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetFileInformationByHandle(
            IntPtr hFile, out BY_HANDLE_FILE_INFORMATION lpFileInformation);

        // Pack=4 is load-bearing: the native struct is 52 bytes. Default
        // alignment pads the uint before the FILETIMEs to 8, shifting every
        // field after it - the call still succeeds and reports serial 0.
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        internal struct BY_HANDLE_FILE_INFORMATION
        {
            public uint FileAttributes;
            public long CreationTime;
            public long LastAccessTime;
            public long LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        /// <summary>
        /// Volume serial for a handle, or false when the object is not something
        /// the filesystem can describe (a raw volume handle, a pipe, ...).
        /// </summary>
        internal static bool TryGetVolumeSerial(IntPtr handle, out uint serial)
        {
            BY_HANDLE_FILE_INFORMATION info;
            if (!GetFileInformationByHandle(handle, out info))
            {
                serial = 0;
                return false;
            }

            serial = info.VolumeSerialNumber;
            return true;
        }

        [DllImport("kernel32.dll")]
        internal static extern uint GetLogicalDrives();
    }
}
