using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace Mtaalam.V2V
{
    // Recommended Proxmox sizes from what the guest really uses (agent report
    // or VMware Tools): headroom on top of the peak, never more than VMware had.
    static class Sizing
    {
        const long GiB = 1L << 30;
        const double CpuTarget = 0.70;      // aim for a peak of ~70% of the new vCPUs
        const double MemHeadroom = 1.30;
        const double DiskHeadroom = 1.25;

        public static int Cores(VmxInfo vm)
        {
            if (vm.Usage == null || vm.Usage.CpuPeakPct < 0) return -1;
            int cores = (int)Math.Ceiling(vm.Cpus * vm.Usage.CpuPeakPct / 100 / CpuTarget);
            cores = Math.Max(cores, vm.IsWindows ? 2 : 1);
            return Math.Min(cores, vm.Cpus);
        }

        public static int MemoryMb(VmxInfo vm)
        {
            if (vm.Usage == null || vm.Usage.MemPeakUsedMb < 0) return -1;
            int mb = (int)(Math.Ceiling(vm.Usage.MemPeakUsedMb * MemHeadroom / 512) * 512);
            mb = Math.Max(mb, vm.IsWindows ? 2048 : 1024);
            return Math.Min(mb, vm.MemoryMb);
        }

        // -1 = unknown. Never below the end of the last partition.
        public static long DiskBytes(VmDisk d)
        {
            if (d.UsedBytes < 0) return -1;
            long rec = CeilGiB(d.UsedBytes * DiskHeadroom + 2 * GiB);
            if (rec > d.CapacityBytes)
                rec = d.UsedBytes < d.CapacityBytes * 0.85 ? d.CapacityBytes : CeilGiB(d.UsedBytes / 0.75);
            if (d.MinBytes <= 0) return Math.Max(rec, d.CapacityBytes);   // partitions unknown: don't shrink
            return Math.Max(rec, MinBytes(d));
        }

        // Smallest size that keeps every partition, in whole GB (never above the VMware size).
        public static long MinBytes(VmDisk d)
        {
            return Math.Min(CeilGiB(d.MinBytes), d.CapacityBytes);
        }

        public static long CeilGiB(double bytes)
        {
            return (long)Math.Ceiling(bytes / GiB) * GiB;
        }

        public static string Gb(long bytes)
        {
            return (bytes / (double)GiB).ToString(bytes % GiB == 0 ? "0" : "0.0", CultureInfo.InvariantCulture) + " GB";
        }

        public static string Describe(VmxInfo vm)
        {
            var u = vm.Usage;
            if (u == null)
                return "No usage data. Connect the GeuzaVirt guest agent (Guest agent > Connect), or start VMware Tools " +
                       "in the VM, to get recommended sizes.";
            var sb = new StringBuilder("Measured by " + u.Source + ":\n");
            int cores = Cores(vm), mem = MemoryMb(vm);
            if (cores > 0)
                sb.AppendFormat("  CPU     peak {0:0}% of {1} vCPU{2}  ->  {3} vCPU\n", u.CpuPeakPct, vm.Cpus,
                    u.CpuAvgPct >= 0 && u.CpuAvgPct != u.CpuPeakPct ? string.Format(" (average {0:0}%)", u.CpuAvgPct) : "", cores);
            if (mem > 0)
                sb.AppendFormat("  Memory  {0} used of {1}  ->  {2}\n",
                    Gb(u.MemPeakUsedMb << 20), Gb((long)vm.MemoryMb << 20), Gb((long)mem << 20));
            foreach (var d in vm.Disks)
            {
                long rec = DiskBytes(d);
                if (rec < 0) continue;
                sb.AppendFormat("  Disk {0}  {1} used of {2}{3}  ->  {4}\n", d.Slot, Gb(d.UsedBytes), Gb(d.CapacityBytes),
                    d.MinBytes > 0 ? ", partitions need " + Gb(MinBytes(d)) : "", Gb(rec));
                if (!string.IsNullOrEmpty(d.GuestLayout)) sb.AppendFormat("      {0}\n", d.GuestLayout);
                long goal = CeilGiB(d.UsedBytes * DiskHeadroom + 2 * GiB);
                if (d.MinBytes > 0 && rec > goal && goal < d.CapacityBytes)
                    sb.AppendFormat(vm.Agent != null && !vm.Agent.IsLinux && d.GuestDisk >= 0
                        ? "      (to go down to ~{0}, enter it under Target settings - the agent shrinks the volume in the guest)\n"
                        : "      (to go down to ~{0}, first shrink the volume inside the guest, then measure again)\n", Gb(goal));
            }
            if (vm.Disks.Count > 1 && u.DiskUsedBytes >= 0 && vm.Disks.TrueForAll(d => d.UsedBytes < 0))
                sb.AppendFormat("  Disks   {0} used in total (run the agent for per-disk sizes)\n", Gb(u.DiskUsedBytes));
            return sb.ToString().TrimEnd();
        }

        // Applies the recommendations; returns false if there were none.
        public static bool Apply(VmxInfo vm)
        {
            bool any = false;
            int cores = Cores(vm), mem = MemoryMb(vm);
            if (cores > 0) { vm.TargetCores = cores == vm.Cpus ? 0 : cores; any = true; }
            if (mem > 0) { vm.TargetMemoryMb = mem == vm.MemoryMb ? 0 : mem; any = true; }
            foreach (var d in vm.Disks)
            {
                long rec = DiskBytes(d);
                if (rec > 0) { d.TargetBytes = rec == d.CapacityBytes ? 0 : rec; any = true; }
            }
            return any;
        }
    }

    // A disk as the guest sees it.
    class GuestDisk
    {
        public int Number = -1;         // Windows disk number (agent service), -1 = unknown
        public long Size, Used, PartEnd;
        public string Style = "";       // gpt, mbr
        public string Layout = "";      // e.g. "C: NTFS 59.5 GB (21.0 GB used), recovery 0.5 GB"
        public readonly List<GuestPart> Parts = new List<GuestPart>();
    }

    // A partition / volume as the agent reports it. Volume-based copies (like VMware
    // Converter) give each NTFS volume its own destination size.
    class GuestPart
    {
        const long GiB = 1L << 30;
        public long Offset, Size, Used = -1;
        public string Kind = "", Letter = "", Fs = "", Label = "";
        public long TargetBytes;        // 0 = maintain size

        // NTFS (Windows), ext2/3/4 and XFS (Linux, created new on the Proxmox host), Linux swap
        // and LVM physical volumes (their LVs share the new size).
        public bool Resizable
        {
            get
            {
                if (Kind == "swap") return true;
                if (Kind == "lvm") return Used >= 0;
                return Kind == "data" && Used >= 0 && Array.IndexOf(new[] { "ntfs", "ext2", "ext3", "ext4", "xfs" }, Fs.ToLowerInvariant()) >= 0;
            }
        }

        public long NewBytes { get { return TargetBytes > 0 ? TargetBytes : Size; } }

        // Smallest size ntfsresize can be expected to reach: used space + 3% + 0.5 GB, whole GB.
        public long MinBytes { get { return Math.Min(Sizing.CeilGiB(Used * 1.03 + GiB / 2), Size); } }

        // Same headroom as the disk recommendation: used + 25% + 2 GB.
        public long RecommendedBytes { get { return Math.Min(Sizing.CeilGiB(Used * 1.25 + 2 * GiB), Size); } }

        public string Name
        {
            get
            {
                if (Letter.Length > 0) return Letter + (Label.Length > 0 ? " " + Label : "");
                if (Label.Length > 0) return Label;
                switch (Kind)
                {
                    case "system": return "EFI system partition";
                    case "msr": return "Microsoft reserved";
                    case "recovery": return "Recovery partition";
                    case "data": return Fs.Length > 0 ? Fs + " volume" : "Volume";
                    default: return Kind.Length > 0 ? Kind + " partition" : "Partition";
                }
            }
        }
    }

    // What the sizing agent measured inside a running VM: a report file written by
    // agent\MtaalamV2V-Agent.ps1 / mtaalam-v2v-agent.sh, or a live answer from the
    // agent service (Windows) or the script run over SSH (Linux).
    class AgentReport
    {
        public string Hostname;
        public string Os;
        public string Time;
        public string Since = "";
        public bool Live;
        public int Seconds;
        public int Cpus;
        public double CpuAvg = -1, CpuPeak = -1, CpuP95 = -1;
        public long MemTotalMb = -1, MemPeakUsedMb = -1;
        public readonly List<string> Macs = new List<string>();
        public readonly List<GuestDisk> Disks = new List<GuestDisk>();
        public readonly List<string> Network = new List<string>();   // "Ethernet0 00:50:56:..: static 10.0.0.20/24 gw ..."

        public static AgentReport Load(string path)
        {
            var json = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path)) as Dictionary<string, object>;
            if (json == null || Str(json, "agent") != "mtaalam-v2v")
                throw new Exception(Path.GetFileName(path) + " is not a GeuzaVirt agent report");
            return FromJson(json);
        }

        public static AgentReport FromJson(Dictionary<string, object> json)
        {
            var r = new AgentReport
            {
                Hostname = Str(json, "hostname"),
                Os = Str(json, "os"),
                Time = Str(json, "time"),
                Since = Str(json, "since"),
                Seconds = (int)Num(json, "seconds"),
                Cpus = (int)Num(json, "cpus"),
                CpuAvg = Num(json, "cpu_avg"),
                CpuPeak = Num(json, "cpu_peak"),
                CpuP95 = Num(json, "cpu_p95"),
                MemTotalMb = (long)Num(json, "mem_total_mb"),
                MemPeakUsedMb = (long)Num(json, "mem_used_peak_mb"),
            };
            foreach (object m in Items(json, "macs"))
                if (m != null) r.Macs.Add(m.ToString().Replace('-', ':').ToLowerInvariant());
            foreach (object o in Items(json, "disks"))
            {
                var d = o as Dictionary<string, object>;
                if (d == null) continue;
                var g = new GuestDisk
                {
                    Number = d.ContainsKey("number") ? (int)Num(d, "number") : -1,
                    Size = (long)Num(d, "size"),
                    Used = (long)Num(d, "used"),
                    PartEnd = (long)Num(d, "part_end"),
                    Style = Str(d, "style"),
                };
                var parts = new List<string>();
                foreach (object po in Items(d, "partitions"))
                {
                    var p = po as Dictionary<string, object>;
                    if (p == null) continue;
                    string kind = Str(p, "kind"), letter = Str(p, "letter");
                    long size = (long)Num(p, "size"), used = (long)Num(p, "used");
                    if (p.ContainsKey("offset"))
                        g.Parts.Add(new GuestPart
                        {
                            Offset = (long)Num(p, "offset"), Size = size, Used = used, Kind = kind, Letter = letter,
                            Fs = Str(p, "fs"), Label = Str(p, "label"),
                        });
                    if (kind == "msr" || kind == "extended") continue;
                    parts.Add(letter.Length > 0
                        ? letter + " " + Str(p, "fs") + " " + Sizing.Gb(size) + (used >= 0 ? " (" + Sizing.Gb(used) + " used)" : "")
                        : kind + " " + Sizing.Gb(size));
                }
                g.Layout = string.Join(", ", parts.ToArray());
                r.Disks.Add(g);
            }
            foreach (object o in Items(json, "network"))
            {
                var n = o as Dictionary<string, object>;
                if (n == null) continue;
                var ips = new List<string>();
                foreach (object ip in Items(n, "ips")) ips.Add(ip.ToString());
                var gws = new List<string>();
                foreach (object gw in Items(n, "gateways")) gws.Add(gw.ToString());
                bool dhcp = n.ContainsKey("dhcp") && n["dhcp"] is bool && (bool)n["dhcp"];
                r.Network.Add(Str(n, "name") + " " + Str(n, "mac") + ": " + (dhcp ? "DHCP" : "static") +
                    (ips.Count > 0 ? " " + string.Join(", ", ips.ToArray()) : "") + (gws.Count > 0 ? " gw " + string.Join(", ", gws.ToArray()) : ""));
            }
            return r;
        }

        static IEnumerable Items(Dictionary<string, object> d, string key)
        {
            object v;
            if (d.TryGetValue(key, out v) && v is IEnumerable && !(v is string)) return (IEnumerable)v;
            return new object[0];
        }

        public bool Matches(VmxInfo vm)
        {
            foreach (string m in Macs) if (vm.Macs.Contains(m)) return true;
            return Hostname.Length > 0 &&
                   (Hostname.Equals(vm.DisplayName, StringComparison.OrdinalIgnoreCase) ||
                    Hostname.Equals(vm.ProxmoxName, StringComparison.OrdinalIgnoreCase));
        }

        // Copies the measurements onto the VM; guest disks are matched to VMware disks by size.
        public void ApplyTo(VmxInfo vm)
        {
            vm.Usage = new GuestUsage
            {
                Source = Live
                    ? string.Format("the agent on {0}, measured over {1}{2}", Hostname, Duration(Seconds), Since.Length > 0 ? " since " + Since : "")
                    : string.Format("the agent on {0} ({1} sample, {2})", Hostname, Duration(Seconds), Time),
                // The service keeps one-minute averages: size on the 95th percentile, not a single busy minute.
                CpuPeakPct = CpuP95 >= 0 && Seconds >= 3600 ? CpuP95 : CpuPeak,
                CpuAvgPct = CpuAvg,
                MemPeakUsedMb = MemPeakUsedMb,
            };
            long total = 0;
            var free = new List<VmDisk>(vm.Disks);
            foreach (var g in Disks)
            {
                if (g.Used >= 0) total += g.Used;
                long tolerance = Math.Max(1L << 20, g.Size / 1000);
                VmDisk best = null;
                foreach (var d in free)
                    if (Math.Abs(d.CapacityBytes - g.Size) <= tolerance) { best = d; break; }
                if (best == null) continue;
                free.Remove(best);
                best.UsedBytes = g.Used;
                best.MinBytes = g.PartEnd;
                best.GuestDisk = g.Number;
                best.GuestLayout = g.Layout;
                best.SetParts(g);
            }
            vm.Usage.DiskUsedBytes = total;
        }

        static string Duration(int seconds)
        {
            if (seconds < 120) return seconds + " s";
            if (seconds < 7200) return (seconds / 60) + " min";
            if (seconds < 172800) return (seconds / 3600) + " h";
            return (seconds / 86400) + " days";
        }

        public static string Str(Dictionary<string, object> d, string key)
        {
            object v;
            return d.TryGetValue(key, out v) && v != null ? v.ToString() : "";
        }

        public static double Num(Dictionary<string, object> d, string key)
        {
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return -1;
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); }
            catch (Exception) { return -1; }
        }
    }
}
