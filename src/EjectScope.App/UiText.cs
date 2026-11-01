using System;
using System.Collections.Generic;
using System.Windows.Media;
using EjectScope.Core;

namespace EjectScope.App
{
    /// <summary>
    /// Every word this window says, plus the colour that goes with the verdict.
    /// Kept in one place so the wording can be reviewed as a whole: a diagnosis
    /// tool is only as trustworthy as the sentences it prints.
    /// </summary>
    internal static class UiText
    {
        internal static readonly Color Green = Color.FromRgb(0x0F, 0x7B, 0x0F);
        internal static readonly Color Amber = Color.FromRgb(0x9D, 0x5D, 0x00);
        internal static readonly Color Red = Color.FromRgb(0xC4, 0x2B, 0x1C);
        internal static readonly Color Grey = Color.FromRgb(0x5D, 0x5D, 0x5D);
        internal static readonly Color Blue = Color.FromRgb(0x0F, 0x6C, 0xBD);
        internal static readonly Color Ink = Color.FromRgb(0x1F, 0x1F, 0x1F);
        internal static readonly Color Muted = Color.FromRgb(0x60, 0x60, 0x60);
        internal static readonly Color Rule = Color.FromRgb(0xE4, 0xE4, 0xE4);

        internal static Color ColorFor(Verdict v)
        {
            switch (v)
            {
                case Verdict.NotInUse: return Green;
                case Verdict.NotSoftware: return Blue;
                case Verdict.InUse: return Amber;
                case Verdict.Writing: return Red;
                default: return Grey;
            }
        }

        /// <summary>Headline. Deliberately never a bare "safe to unplug".</summary>
        internal static string Headline(Diagnosis d)
        {
            switch (d.Verdict)
            {
                case Verdict.NotInUse:
                    return "可以正常弹出";
                case Verdict.NotSoftware:
                    return "没有程序占用，问题在硬件或驱动层";
                case Verdict.InUse:
                    return string.Format("{0} 个程序正在使用这个盘", d.Holders.Count);
                case Verdict.Writing:
                    return "正在写入数据，现在拔会丢东西";
                default:
                    return "无法确认，别急着拔";
            }
        }

        /// <summary>Second line: what we actually measured, in plain words.</summary>
        internal static string Subline(Diagnosis d)
        {
            switch (d.Verdict)
            {
                case Verdict.NotInUse:
                    return "已完整扫描文件句柄，并且卷级独占打开成功——系统和驱动层都没有人握着它。";
                case Verdict.NotSoftware:
                    return "明细里没有可归因的进程，但卷被独占打开拒绝：这种锁通常来自驱动、杀软、卷影复制或硬件层。";
                case Verdict.InUse:
                    return "下面列出的程序持有这个盘上的文件。关掉它们的窗口或退出程序，通常就够弹出了。";
                case Verdict.Writing:
                    return "检测到写句柄，并且相关文件在观察窗口内确实变大了。等它写完再弹，现在的每一秒都在写盘。";
                default:
                    return "扫描没有做到全覆盖，所以既不能说安全，也不能说被占用。";
            }
        }

        /// <summary>One concrete next step, or empty when the headline is enough.</summary>
        internal static string ActionHint(Diagnosis d)
        {
            switch (d.Verdict)
            {
                case Verdict.InUse:
                    {
                        List<string> names = TopNames(d, 3);
                        if (names.Count == 0) return "优先关窗口，而不是结束进程——关窗口不会丢未保存的内容。";
                        return "建议先处理：" + string.Join("、", names.ToArray()) + "。先试关窗口，不行再退出程序。";
                    }
                case Verdict.Writing:
                    {
                        string detail = d.WriteActivityDetail;
                        if (string.IsNullOrEmpty(detail)) return "等文件大小不再变化，再重新扫描一次。";
                        return detail;
                    }
                case Verdict.NotSoftware:
                    return "换一个 USB 口、换根线、避开无供电的集线器；移动硬盘还要确认供电是否足够。";
                case Verdict.Unknown:
                    {
                        List<string> why = new List<string>();
                        if (d.InaccessibleProcessCount > 0)
                            why.Add(string.Format("{0} 个进程无权查看", d.InaccessibleProcessCount));
                        if (d.UnresolvedHandleCount > 0)
                            why.Add(string.Format("{0} 个句柄读不到归属", d.UnresolvedHandleCount));
                        if (d.HadNameTimeout) why.Add("有句柄的名字查询超时");
                        string joined = why.Count == 0 ? "扫描覆盖不完整" : string.Join("，", why.ToArray());
                        return joined + "。以管理员身份重新运行，通常能把这些补齐。";
                    }
                default:
                    return "可以拔了。";
            }
        }

        private static List<string> TopNames(Diagnosis d, int max)
        {
            List<string> names = new List<string>();
            foreach (Holder h in d.Holders)
            {
                string name = string.IsNullOrEmpty(h.ProcessName) ? "未知进程" : h.ProcessName;
                if (!names.Contains(name)) names.Add(name);
                if (names.Count >= max) break;
            }
            return names;
        }

        internal static string AccessText(HolderAccess a)
        {
            switch (a)
            {
                case HolderAccess.ReadOnly: return "只读";
                case HolderAccess.WriteOnly: return "写";
                case HolderAccess.ReadWrite: return "读写";
                default: return "未知";
            }
        }

        internal static string HolderName(Holder h)
        {
            string name = string.IsNullOrEmpty(h.ProcessName) ? "(未知)" : h.ProcessName;
            return string.Format("{0}  ({1})", name, h.ProcessId);
        }

        internal static string Company(Holder h)
        {
            if (h.ProcessInaccessible) return "(无权查看)";
            if (string.IsNullOrEmpty(h.CompanyName)) return "—";
            return h.CompanyName;
        }

        internal static string SamplePath(Holder h)
        {
            if (h.Samples.Count == 0) return "—";
            HandleRef first = h.Samples[0];
            if (!string.IsNullOrEmpty(first.DosPath)) return first.DosPath;
            if (!string.IsNullOrEmpty(first.NtPath)) return first.NtPath;
            return "—";
        }
    }
}
