using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using EjectScope.Core;

namespace EjectScope.Cli
{
    /// <summary>
    /// Console front end. Deliberately plain ASCII: it is the CI smoke test and
    /// the "I just want an answer" path, not the product UI.
    /// </summary>
    internal static class Program
    {
        private const string Version = "0.0.1-p0";

        private static int Main(string[] args)
        {
            try
            {
                if (args.Length == 0)
                {
                    PrintUsage();
                    return 2;
                }

                string cmd = args[0].ToLowerInvariant();

                if (cmd == "--list" || cmd == "-l") { ListVolumes(); return 0; }
                if (cmd == "--selftest") return SelfTest();
                if (cmd == "--dump-types")
                {
                    Console.Write(HandleEnumerator.DumpTypes());
                    return 0;
                }
                if (cmd == "--help" || cmd == "-h" || cmd == "/?") { PrintUsage(); return 0; }

                return Diagnose(args[0]);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("error: " + ex.Message);
                return 1;
            }
        }

        private static void PrintUsage()
        {
            Console.WriteLine("EjectScope " + Version + " - find out what is holding a USB drive");
            Console.WriteLine();
            Console.WriteLine("  ejectscope E:          diagnose drive E:");
            Console.WriteLine("  ejectscope --list      list mounted volumes");
            Console.WriteLine("  ejectscope --selftest  verify handle detection works on this machine");
            Console.WriteLine();
            Console.WriteLine("Run as administrator for complete results. Without it the handle");
            Console.WriteLine("scan cannot see protected processes and the answer stays 'unknown'.");
        }

        private static void ListVolumes()
        {
            Console.WriteLine("EjectScope " + Version);
            Console.WriteLine();
            Console.WriteLine("Drv  Type       Bus              Size      Free   Label");
            Console.WriteLine("---  ---------  ---------------  --------  -----  -----");

            foreach (string spec in VolumeLocator.EnumerateDriveLetters())
            {
                VolumeInfo v;
                try
                {
                    v = VolumeLocator.Describe(spec);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(spec + "  <error: " + ex.Message + ">");
                    continue;
                }

                Console.WriteLine(string.Format("{0,-3}  {1,-9}  {2,-15}  {3,8}  {4,5}  {5}",
                    spec,
                    DriveTypeName(v.DriveType),
                    Truncate(v.BusTypeName, 15),
                    v.SizeText(),
                    FreeText(v),
                    v.Label));
            }

            Console.WriteLine();
            Console.WriteLine("Type 'Type' comes from GetDriveType; 'Bus' is the real transport.");
            Console.WriteLine("A USB hard disk reports 'Fixed' above but 'USB' in the Bus column.");
        }

        private static string FreeText(VolumeInfo v)
        {
            if (v.TotalBytes <= 0) return "?";
            double pct = 100.0 * v.FreeBytes / v.TotalBytes;
            return string.Format("{0:0}%", pct);
        }

        private static int Diagnose(string spec)
        {
            string normalized = VolumeLocator.NormalizeDriveSpec(spec);

            Console.WriteLine("EjectScope " + Version);
            Console.WriteLine(new string('-', 68));

            Diagnosis d = Diagnoser.Run(normalized);
            VolumeInfo v = d.Volume;

            Console.WriteLine(string.Format("Drive        {0}  {1}", v.RootPath, v.DisplayName));
            Console.WriteLine(string.Format("Filesystem   {0}   {1} total, {2} free",
                string.IsNullOrEmpty(v.FileSystem) ? "?" : v.FileSystem,
                v.SizeText(), SizeText(v.FreeBytes)));
            Console.WriteLine(string.Format("Bus          {0}", v.BusTypeName));
            Console.WriteLine(string.Format("USB device   {0}", v.IsUsb ? "yes" : "no"));
            Console.WriteLine(string.Format("Device path  {0}", v.NtDeviceName ?? "<unresolved>"));

            Console.WriteLine();
            Console.WriteLine("Exclusive open");
            Console.WriteLine("  " + ExclusiveText(d));
            Console.WriteLine("  " + d.ExclusiveOpenDetail);

            if (d.Holders.Count == 0)
            {
                Console.WriteLine();
                Console.WriteLine("Holders      none visible");
            }
            else
            {
                Console.WriteLine();
                Console.WriteLine(string.Format("Holders ({0})", d.Holders.Count));
                foreach (Holder h in d.Holders)
                {
                    PrintHolder(h);
                }
            }

            Console.WriteLine();
            Console.WriteLine("Coverage");
            if (d.InaccessibleProcessCount > 0)
            {
                Console.WriteLine(string.Format(
                    "  {0} process(es) could not be opened - their handles are invisible to us.",
                    d.InaccessibleProcessCount));
            }
            if (d.UnresolvedHandleCount > 0)
            {
                Console.WriteLine(string.Format(
                    "  {0} handle(s) could not be named.", d.UnresolvedHandleCount));
            }
            if (d.HadNameTimeout)
            {
                Console.WriteLine("  A name lookup timed out and was abandoned (named pipe guard).");
            }
            Console.WriteLine(string.Format("  Complete: {0}   scan took {1} ms",
                d.CoverageComplete ? "yes" : "NO", d.ScanDurationMs));

            Console.WriteLine();
            Console.WriteLine("Verdict");
            Console.WriteLine("  " + VerdictLabel(d.Verdict) + " - " + Diagnoser.VerdictText(d));
            if (!string.IsNullOrEmpty(d.WriteActivityDetail))
                Console.WriteLine("  " + d.WriteActivityDetail);
            if (d.Verdict == Verdict.NotInUse || d.Verdict == Verdict.NotSoftware)
            {
                Console.WriteLine("  You can unplug it.");
            }
            if (d.Verdict == Verdict.Writing)
            {
                Console.WriteLine("  Wait. Do not unplug yet.");
            }

            return d.Verdict == Verdict.Writing ? 3 : 0;
        }

        private static void PrintHolder(Holder h)
        {
            string access = AccessLabel(h.Access);
            string name = h.ProcessName;
            if (!string.IsNullOrEmpty(h.CompanyName)) name = name + "  (" + h.CompanyName + ")";

            Console.WriteLine(string.Format("  [{0}] {1}  pid {2}  {3} handle(s)",
                access, name, h.ProcessId, h.HandleCount));

            if (!string.IsNullOrEmpty(h.ProcessPath))
                Console.WriteLine("          " + h.ProcessPath);

            int shown = 0;
            foreach (HandleRef r in h.Samples)
            {
                if (shown == 8)
                {
                    Console.WriteLine(string.Format("          ... +{0} more",
                        h.Samples.Count - shown));
                    break;
                }
                Console.WriteLine("          -> " + r);
                shown++;
            }
        }

        private static string AccessLabel(HolderAccess a)
        {
            switch (a)
            {
                case HolderAccess.ReadWrite: return "READ+WRITE";
                case HolderAccess.WriteOnly: return "WRITE     ";
                case HolderAccess.ReadOnly: return "read      ";
                default: return "?         ";
            }
        }

        private static string ExclusiveText(Diagnosis d)
        {
            switch (d.ExclusiveOpen)
            {
                case ExclusiveOpenResult.NoHolders: return "SUCCEEDED - nothing holds this volume";
                case ExclusiveOpenResult.SharingViolation: return "FAILED (sharing violation) - in use";
                case ExclusiveOpenResult.AccessDenied: return "DENIED - need administrator rights";
                case ExclusiveOpenResult.Failed: return "FAILED";
                default: return "unknown";
            }
        }

        private static string VerdictLabel(Verdict v)
        {
            switch (v)
            {
                case Verdict.NotInUse: return "NOT IN USE      ";
                case Verdict.NotSoftware: return "NOT A SOFTWARE HOLDER";
                case Verdict.InUse: return "IN USE          ";
                case Verdict.Writing: return "BEING WRITTEN   ";
                default: return "UNKNOWN         ";
            }
        }

        private static string DriveTypeName(uint t)
        {
            switch (t)
            {
                case 2: return "Removable";
                case 3: return "Fixed";
                case 4: return "Network";
                case 5: return "CD-ROM";
                case 6: return "RAM disk";
                default: return "Other";
            }
        }

        private static string SizeText(long bytes)
        {
            if (bytes <= 0) return "?";
            double gb = bytes / 1024.0 / 1024.0 / 1024.0;
            if (gb >= 1.0) return string.Format("{0:0.#} GB", gb);
            return string.Format("{0:0} MB", bytes / 1024.0 / 1024.0);
        }

        private static string Truncate(string s, int max)
        {
            if (s == null) return string.Empty;
            return s.Length <= max ? s : s.Substring(0, max);
        }

        /// <summary>
        /// Locked-down machines and sandboxes may refuse writes to %TEMP%. The test
        /// file only has to exist and stay open, so fall back to the current
        /// directory and then to the program's own directory.
        /// </summary>
        private static bool TryPrepareProbe(out string probeDir, out string probeFile, out string where)
        {
            probeDir = null;
            probeFile = null;
            where = null;

            string[] candidates = new string[]
            {
                Path.Combine(Path.GetTempPath(), "ejectscope-selftest"),
                Path.Combine(Directory.GetCurrentDirectory(), "ejectscope-selftest"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ejectscope-selftest")
            };
            string[] labels = new string[] { "%TEMP%", "当前目录", "程序目录" };

            for (int i = 0; i < candidates.Length; i++)
            {
                string dir = candidates[i];
                string file = Path.Combine(dir, "handled-by-selftest.txt");
                try
                {
                    Directory.CreateDirectory(dir);
                    File.WriteAllText(file, "EjectScope self test");
                }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                probeDir = dir;
                probeFile = file;
                where = labels[i];
                return true;
            }

            return false;
        }

        /// <summary>
        /// Proves on the *current* machine that a known open file is reported back
        /// with the right process and path. This is the P0 acceptance test.
        /// </summary>
        private static int SelfTest()
        {
            string probeDir;
            string probeFile;
            string where;
            if (!TryPrepareProbe(out probeDir, out probeFile, out where))
            {
                Console.Error.WriteLine("error: 找不到可写目录来放测试文件，%TEMP% 与当前目录都被拒绝。");
                return 2;
            }
            if (where != "%TEMP%")
                Console.WriteLine("note: %TEMP% 不可写，测试文件改放在 " + where + "。");

            string drive = Path.GetPathRoot(probeFile);

            Console.WriteLine("EjectScope " + Version + " self test");
            Console.WriteLine("  probe file : " + probeFile);
            Console.WriteLine("  volume     : " + drive);
            Console.WriteLine();

            int myPid = Process.GetCurrentProcess().Id;
            bool found = false;
            string foundPath = null;

            using (FileStream fs = new FileStream(probeFile, FileMode.Open,
                       FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                Diagnosis d = Diagnoser.Run(drive);

                foreach (Holder h in d.Holders)
                {
                    if (h.ProcessId != myPid) continue;
                    foreach (HandleRef r in h.Samples)
                    {
                        if (r.DosPath != null &&
                            r.DosPath.EndsWith("handled-by-selftest.txt",
                                StringComparison.OrdinalIgnoreCase))
                        {
                            found = true;
                            foundPath = r.DosPath;
                        }
                    }
                }

                Console.WriteLine("  holders seen : " + d.Holders.Count);
                Console.WriteLine("  verdict      : " + VerdictLabel(d.Verdict));
                Console.WriteLine("  scan time    : " + d.ScanDurationMs + " ms");
                Console.WriteLine();

                if (d.UnresolvedHandleCount > 0)
                    Console.WriteLine("  note: " + d.UnresolvedHandleCount + " handle(s) unnamed");

                fs.Flush();
            }

            try { File.Delete(probeFile); } catch { }
            try { Directory.Delete(probeDir); } catch { }

            if (found)
            {
                Console.WriteLine("PASS - found our own open handle");
                Console.WriteLine("       " + foundPath);
                return 0;
            }

            Console.WriteLine("FAIL - did not find the handle we are holding ourselves.");
            Console.WriteLine("       If you are not running as administrator, that is expected:");
            Console.WriteLine("       run this from an elevated prompt to see all handles.");
            return 1;
        }
    }
}
