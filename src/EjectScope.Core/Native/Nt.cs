using System;
using System.Runtime.InteropServices;

namespace EjectScope.Core.Native
{
    /// <summary>
    /// ntdll surface: system-wide handle table + object naming.
    /// These are undocumented but stable since Vista; documented in
    /// "System Informer" / Process Hacker phnt headers.
    /// </summary>
    internal static class Nt
    {
        internal const int STATUS_SUCCESS = 0;
        internal const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);

        /// <summary>SystemExtendedHandleInformation - preferred over 16 on 64-bit.</summary>
        internal const int SystemExtendedHandleInformation = 64;

        internal const int ObjectBasicInformation = 0;
        internal const int ObjectNameInformation = 1;
        internal const int ObjectTypeInformation = 2;

        [StructLayout(LayoutKind.Sequential)]
        internal struct SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX
        {
            internal IntPtr Object;
            internal IntPtr UniqueProcessId;
            internal IntPtr HandleValue;
            internal uint GrantedAccess;
            internal ushort CreatorBackTraceIndex;
            internal ushort ObjectTypeIndex;
            internal uint HandleAttributes;
            internal uint Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct UNICODE_STRING
        {
            internal ushort Length;
            internal ushort MaximumLength;
            internal IntPtr Buffer;
        }

        [DllImport("ntdll.dll")]
        internal static extern int NtQuerySystemInformation(
            int SystemInformationClass,
            IntPtr SystemInformation,
            int SystemInformationLength,
            out int ReturnLength);

        [DllImport("ntdll.dll")]
        internal static extern int NtQueryObject(
            IntPtr Handle,
            int ObjectInformationClass,
            IntPtr ObjectInformation,
            int ObjectInformationLength,
            out int ReturnLength);

        [DllImport("ntdll.dll")]
        internal static extern int NtQueryInformationProcess(
            IntPtr ProcessHandle,
            int ProcessInformationClass,
            IntPtr ProcessInformation,
            int ProcessInformationLength,
            out int ReturnLength);
    }
}
