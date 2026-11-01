using System;
using System.Collections.Generic;

namespace EjectScope.Core
{
    /// <summary>How a holder is touching the volume.</summary>
    public enum HolderAccess
    {
        Unknown = 0,
        ReadOnly,
        ReadWrite,
        WriteOnly
    }

    /// <summary>A single open handle, reduced to what the user needs to see.</summary>
    public sealed class HandleRef
    {
        /// <summary>Raw NT object name, e.g. \Device\HarddiskVolume3\Users\me\a.txt</summary>
        public string NtPath { get; set; }

        /// <summary>DOS path when the OS can resolve it, otherwise null.</summary>
        public string DosPath { get; set; }

        public HolderAccess Access { get; set; }

        /// <summary>True when the handle names the volume itself, not a file on it.</summary>
        public bool IsVolumeRoot { get; set; }

        public override string ToString()
        {
            return string.IsNullOrEmpty(DosPath) ? NtPath : DosPath;
        }
    }

    /// <summary>One process holding at least one handle on the volume.</summary>
    public sealed class Holder
    {
        public int ProcessId { get; set; }
        public string ProcessName { get; set; }
        public string ProcessPath { get; set; }
        public string CompanyName { get; set; }

        /// <summary>Could not open the process at all (protected / PPL / System).</summary>
        public bool ProcessInaccessible { get; set; }

        /// <summary>Number of handles seen on this volume for this process.</summary>
        public int HandleCount { get; set; }

        /// <summary>Highest privilege seen across this process's handles.</summary>
        public HolderAccess Access { get; set; }

        /// <summary>A few representative handles for display; never all of them.</summary>
        public List<HandleRef> Samples { get; private set; }

        public Holder()
        {
            Samples = new List<HandleRef>();
            Access = HolderAccess.Unknown;
        }
    }

    /// <summary>What we can say about the volume itself.</summary>
    public sealed class VolumeInfo
    {
        public char Letter { get; set; }
        public string RootPath { get; set; }
        public string DisplayName { get; set; }

        public uint DriveType { get; set; }
        public bool IsUsb { get; set; }
        public string BusTypeName { get; set; }

        /// <summary>\Device\HarddiskVolumeN — the anchor for every match.</summary>
        public string NtDeviceName { get; set; }

        /// <summary>Volume serial from GetVolumeInformation. Lets us place a handle</summary>
        /// on a drive even when its name cannot be read at all.
        public uint SerialNumber { get; set; }

        public string Label { get; set; }
        public string FileSystem { get; set; }
        public long TotalBytes { get; set; }
        public long FreeBytes { get; set; }

        public VolumeInfo()
        {
            DisplayName = string.Empty;
            BusTypeName = "unknown";
            Label = string.Empty;
            FileSystem = string.Empty;
        }

        public string SizeText()
        {
            if (TotalBytes <= 0) return "?";
            double gb = TotalBytes / 1024.0 / 1024.0 / 1024.0;
            if (gb >= 1.0) return string.Format("{0:0.#} GB", gb);
            return string.Format("{0:0} MB", TotalBytes / 1024.0 / 1024.0);
        }
    }

    /// <summary>Result of trying to open the volume exclusively.</summary>
    public enum ExclusiveOpenResult
    {
        Unknown = 0,
        /// <summary>Opened with share mode 0 — nothing holds the volume.</summary>
        NoHolders,
        /// <summary>ERROR_SHARING_VIOLATION — something is holding it.</summary>
        SharingViolation,
        /// <summary>ERROR_ACCESS_DENIED — read-only media or insufficient rights.</summary>
        AccessDenied,
        /// <summary>Other failure; the message is kept verbatim.</summary>
        Failed
    }

    /// <summary>The single sentence the user came for.</summary>
    public enum Verdict
    {
        /// <summary>Nothing found, and we are confident we could have seen it.</summary>
        NotInUse,
        /// <summary>Holders found, none of them writing.</summary>
        InUse,
        /// <summary>Someone is actively writing — pull the plug and data dies.</summary>
        Writing,
        /// <summary>We could not see enough to answer (protected processes).</summary>
        Unknown,
        /// <summary>Exclusive open succeeded — the problem is not a software holder.</summary>
        NotSoftware
    }

    /// <summary>Everything one scan learned, plus how much of it we trust.</summary>
    public sealed class Diagnosis
    {
        public VolumeInfo Volume { get; set; }
        public Verdict Verdict { get; set; }
        public List<Holder> Holders { get; private set; }

        public ExclusiveOpenResult ExclusiveOpen { get; set; }
        public string ExclusiveOpenDetail { get; set; }

        /// <summary>Processes we could not inspect at all.</summary>
        public int InaccessibleProcessCount { get; set; }

        /// <summary>Handles whose name could not be resolved (the pipe hang guard).</summary>
        public int UnresolvedHandleCount { get; set; }

        /// <summary>True when a name query timed out and was abandoned.</summary>
        public bool HadNameTimeout { get; set; }

        /// <summary>Rule R1: a write handle plus observed file growth in the window.</summary>
        public bool WriteActivityConfirmed { get; set; }

        /// <summary>What we measured, or why we could not measure anything.</summary>
        public string WriteActivityDetail { get; set; }

        public long ScanDurationMs { get; set; }

        public Diagnosis()
        {
            Holders = new List<Holder>();
            Verdict = Verdict.Unknown;
            ExclusiveOpenDetail = string.Empty;
        }

        /// <summary>
        /// Invariant I5: we may only tell the user "safe to unplug" when nothing
        /// was hidden from us. Everything else degrades to Unknown, never to a
        /// cheerful green checkmark.
        /// </summary>
        public bool CoverageComplete
        {
            get
            {
                return InaccessibleProcessCount == 0
                    && UnresolvedHandleCount == 0
                    && !HadNameTimeout;
            }
        }
    }
}
