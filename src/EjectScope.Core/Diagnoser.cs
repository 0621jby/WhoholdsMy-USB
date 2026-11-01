using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace EjectScope.Core
{
    /// <summary>
    /// Wires the five layers together and turns raw observations into one verdict.
    ///
    /// The ordering matters: the exclusive open is done *after* the handle scan
    /// would be too slow, so we do it first — it is cheap, it is authoritative
    /// when it succeeds, and it tells us early whether this is even a software
    /// problem (design case 12).
    /// </summary>
    public static class Diagnoser
    {
        public static Diagnosis Run(string driveSpec)
        {
            Stopwatch sw = Stopwatch.StartNew();

            Diagnosis diag = new Diagnosis();

            VolumeInfo volume = VolumeLocator.Describe(driveSpec);
            diag.Volume = volume;

            string detail;
            diag.ExclusiveOpen = ExclusiveOpenProbe.Probe(driveSpec, out detail);
            diag.ExclusiveOpenDetail = detail;

            // Do NOT treat a successful exclusive open as "nothing is using this
            // drive". It only proves that no other *volume-level* handle exists
            // (Everything, chkdsk, VSS). A plain text file open in Notepad is
            // invisible at this level - measured on a real USB disk, where the
            // volume opened exclusively while a file on it was still held. So
            // the handle scan always runs; the probe is one more signal.

            HandleEnumerator.Scan(volume, diag);

            // Rule R1: only measure growth when there is something to measure.
            if (HasWrites(diag)) WriteActivityProbe.Probe(diag);

            Decide(diag);

            sw.Stop();
            diag.ScanDurationMs = sw.ElapsedMilliseconds;
            return diag;
        }

        private static void Decide(Diagnosis diag)
        {
            // R1 first: data is actually moving. Nothing else can outrank that.
            if (diag.WriteActivityConfirmed)
            {
                diag.Verdict = Verdict.Writing;
                return;
            }

            // Any handle we can see at all - read or write - means "in use".
            // A write handle with no growth lands here too: still in use, but
            // not an emergency, so the UI must not scream.
            if (diag.Holders.Count > 0)
            {
                diag.Verdict = Verdict.InUse;
                return;
            }

            // Nobody visible. Now the coverage question decides whether we are
            // allowed to say anything reassuring (invariant I5).
            if (!diag.CoverageComplete)
            {
                diag.Verdict = Verdict.Unknown;
                return;
            }

            // Full coverage and no holders: the exclusive-open probe is the only
            // remaining signal. Succeeding means no volume-level holder either,
            // so the drive is genuinely idle. Failing while nothing holds a file
            // points below us - hardware, cable, port, or a driver-level lock.
            if (diag.ExclusiveOpen == ExclusiveOpenResult.NoHolders)
            {
                diag.Verdict = Verdict.NotInUse;
                return;
            }

            if (diag.ExclusiveOpen == ExclusiveOpenResult.SharingViolation)
            {
                diag.Verdict = Verdict.NotSoftware;
                return;
            }

            diag.Verdict = Verdict.Unknown;
        }

        public static bool HasWrites(Diagnosis diag)
        {
            foreach (Holder h in diag.Holders)
            {
                if (h.Access == HolderAccess.ReadWrite || h.Access == HolderAccess.WriteOnly)
                    return true;
            }
            return false;
        }

        /// <summary>One line the UI can show without further interpretation.</summary>
        public static string VerdictText(Diagnosis diag)
        {
            switch (diag.Verdict)
            {
                case Verdict.NotInUse:
                    return "Nothing is using this drive. It should eject normally.";
                case Verdict.NotSoftware:
                    return "No process is holding this drive. Look at hardware, cable or driver instead.";
                case Verdict.InUse:
                    return string.Format("{0} process(es) are using this drive.", diag.Holders.Count);
                case Verdict.Writing:
                    return "Data is being written right now. Unplugging will lose it.";
                default:
                    return "Could not see enough to answer. See the notes below.";
            }
        }
    }
}
