using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace EjectScope.Core
{
    /// <summary>
    /// Rule R1: a write handle alone does not mean data is moving.
    ///
    /// A program can open a file for writing and then sit idle for hours; calling
    /// that "being written" would train the user to ignore the red warning. So we
    /// measure the sampled files twice, 1.5 s apart, and only raise the alarm when
    /// the size or the write time actually changed in that window.
    ///
    /// This never reads file *contents* (invariant I1) — only the metadata the
    /// filesystem already exposes.
    /// </summary>
    public static class WriteActivityProbe
    {
        /// <summary>Files we are willing to watch; each costs a stat, not a read.</summary>
        private const int SampleBudget = 12;

        /// <summary>Long enough for a copy operation to grow a file measurably.</summary>
        private const int SettleMs = 1500;

        private struct Sample
        {
            public string Path;
            public long Length;
            public DateTime WrittenUtc;
        }

        public static void Probe(Diagnosis diag)
        {
            List<Sample> before = TakeSnapshot(diag);

            if (before.Count == 0)
            {
                // We hold write handles we cannot measure (volume handle, deleted
                // file, directory we may not stat). Say so instead of guessing.
                diag.WriteActivityDetail =
                    "write handles present, but none of the sampled paths could be measured";
                return;
            }

            Thread.Sleep(SettleMs);

            foreach (Sample s in before)
            {
                long length;
                DateTime written;
                if (!TryRead(s.Path, out length, out written)) continue;

                if (length != s.Length)
                {
                    diag.WriteActivityConfirmed = true;
                    diag.WriteActivityDetail = string.Format(
                        "{0} grew from {1} to {2} bytes while we watched",
                        s.Path, s.Length, length);
                    return;
                }

                if (written != s.WrittenUtc)
                {
                    diag.WriteActivityConfirmed = true;
                    diag.WriteActivityDetail =
                        s.Path + " was modified while we watched";
                    return;
                }
            }

            diag.WriteActivityDetail = string.Format(
                "checked {0} file(s) over {1} ms - none changed", before.Count, SettleMs);
        }

        private static List<Sample> TakeSnapshot(Diagnosis diag)
        {
            List<Sample> result = new List<Sample>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (Holder h in diag.Holders)
            {
                if (h.Access != HolderAccess.ReadWrite && h.Access != HolderAccess.WriteOnly)
                    continue;

                foreach (HandleRef r in h.Samples)
                {
                    if (r.DosPath == null) continue;
                    if (seen.Contains(r.DosPath)) continue;

                    long length;
                    DateTime written;
                    if (!TryRead(r.DosPath, out length, out written)) continue;

                    seen.Add(r.DosPath);
                    Sample s = new Sample();
                    s.Path = r.DosPath;
                    s.Length = length;
                    s.WrittenUtc = written;
                    result.Add(s);

                    if (result.Count >= SampleBudget) return result;
                }
            }

            return result;
        }

        private static bool TryRead(string path, out long length, out DateTime writtenUtc)
        {
            length = 0;
            writtenUtc = DateTime.MinValue;

            try
            {
                if (File.Exists(path))
                {
                    FileInfo fi = new FileInfo(path);
                    length = fi.Length;
                    writtenUtc = fi.LastWriteTimeUtc;
                    return true;
                }

                if (Directory.Exists(path))
                {
                    // A directory's write time moves when an entry is created or
                    // renamed inside it, which is exactly what a copy produces.
                    DirectoryInfo di2 = new DirectoryInfo(path);
                    writtenUtc = di2.LastWriteTimeUtc;
                    return true;
                }
            }
            catch
            {
                // Permission or a race with the holder; skip this sample.
            }

            return false;
        }
    }
}
