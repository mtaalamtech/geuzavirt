using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace Mtaalam.V2V
{
    // ------------------------------------------------------------------------
    // Running Linux machine, copied file by file over SSH (like VMware Converter's
    // powered-on Linux source). The Proxmox host recreates the partitions, LVM and
    // file systems with the source's UUIDs and copies the files with rsync.
    class LinuxSource
    {
        public SshTarget Target;
        public string Shell = "bash -s";     // how scripts get root on the source
        public string StdinPrefix;
        public string Spec = "";             // probe output, handed to vmware2pve.sh
        public string Os = "", Kernel = "", Hostname = "";
        public readonly List<string> Network = new List<string>();
        public readonly List<string> Layout = new List<string>();   // for "View source details"
        public readonly List<string> DiskOrder = new List<string>(); // source disk names in VM order

        public SshClient Client()
        {
            return new SshClient(Target) { Shell = Shell, StdinPrefix = StdinPrefix };
        }

        // Logs in and finds a way to run scripts as root (root login, sudo without or with password).
        public static LinuxSource Connect(SshTarget target)
        {
            var src = new LinuxSource { Target = target };
            var ssh = new SshClient(target);
            int code;
            string id = ssh.Capture("id -u", out code).Trim();
            if (code != 0) throw new Exception("Cannot log in to " + target.Host + ": " + ssh.Explain(code));
            if (id == "0") return src;
            ssh.Capture("sudo -n true", out code);
            if (code == 0) { src.Shell = "sudo -n bash -s"; }
            else
            {
                if (string.IsNullOrEmpty(target.Password))
                    throw new Exception(target.User + " is not root on " + target.Host + " and sudo asks for a password. Enter the password (or log in as root).");
                // check the password first, so sudo never reads script lines as password attempts
                var check = new SshClient(target) { Shell = "sudo -S -p '' -v", StdinPrefix = target.Password + "\n" };
                check.Capture("", out code);
                if (code != 0) throw new Exception("sudo refused the password of " + target.User + " on " + target.Host + " (the user must be allowed to run commands as root).");
                src.Shell = "sudo -S -p '' bash -s";
                src.StdinPrefix = target.Password + "\n";
            }
            string root = src.Client().Capture("id -u", out code).Trim();
            if (code != 0 || root != "0") throw new Exception("Cannot become root on " + target.Host + " with sudo.");
            return src;
        }

        // Everything about the machine in "kind<TAB>fields" lines (read again by vmware2pve.sh).
        const string ProbeScript = @"export LC_ALL=C PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin:$PATH
t=$(printf '\t')
echo ""host${t}$(hostname 2>/dev/null)""
if [ -r /etc/os-release ]; then . /etc/os-release; echo ""os${t}${PRETTY_NAME:-$NAME}""
elif [ -r /etc/redhat-release ]; then echo ""os${t}$(cat /etc/redhat-release)""; else echo ""os${t}Linux""; fi
echo ""kernel${t}$(uname -r)""
echo ""arch${t}$(uname -m)""
echo ""cpus${t}$(grep -c ^processor /proc/cpuinfo)""
echo ""mem${t}$(awk '/^MemTotal:/ {printf ""%.0f"", $2 * 1024}' /proc/meminfo)""
if [ -d /sys/firmware/efi ]; then echo ""firmware${t}efi""; else echo ""firmware${t}bios""; fi
echo ""cmdline${t}$(cat /proc/cmdline)""
for c in rsync gzip dd blkid; do command -v $c >/dev/null 2>&1 || echo ""missing${t}$c""; done
if rsync --version 2>/dev/null | grep -q 'no ACLs'; then echo ""rsyncnoacl${t}1""; fi
if rsync --version 2>/dev/null | grep -q 'no xattrs'; then echo ""rsyncnoxattr${t}1""; fi
for n in /sys/class/net/*; do i=${n##*/}; [ -e ""$n/device"" ] || continue
  echo ""nic${t}$i${t}$(cat $n/address)${t}$(ip -o -4 addr show dev $i 2>/dev/null | awk '{printf ""%s "", $4}')""
done
echo ""gateway${t}$(ip route show default 2>/dev/null | awk '{print $3; exit}')""
mnt() { [ -n ""$1"" ] && awk -v d=""$1"" '$3 == d && $4 == ""/"" {print $5; exit}' /proc/self/mountinfo; }
fsparams() {
  case $2 in
    ext2|ext3|ext4) dumpe2fs -h ""$1"" 2>/dev/null | awk -F: '
      /^Filesystem features:/ {f = $2} /^Block size:/ {b = $2} /^Inode size:/ {i = $2} /^Inode count:/ {n = $2}
      END {gsub(/^ +| +$/, """", f); gsub(/ +/, "","", f); gsub(/ /, """", b); gsub(/ /, """", i); gsub(/ /, """", n)
           printf ""features=%s bs=%s isize=%s icount=%s"", f, b, i, n}' ;;
    xfs) [ -n ""$3"" ] && xfs_info ""$3"" 2>/dev/null | tr ' ,' '\n\n' |
           grep -E '^(isize|crc|finobt|sparse|rmapbt|reflink|bigtime|inobtcount|ftype|nrext64)=' | sort -u | tr '\n' ' ' ;;
  esac
}
vol() {
  ty=$(blkid -p -s TYPE -o value -- ""$1"" 2>/dev/null); uu=$(blkid -p -s UUID -o value -- ""$1"" 2>/dev/null)
  la=$(blkid -p -s LABEL -o value -- ""$1"" 2>/dev/null); m=$(mnt ""$2""); u=""""
  [ -n ""$m"" ] && u=$(df -PB1 ""$m"" 2>/dev/null | awk 'NR == 2 {print $3}')
  printf '%s\t%s\t%s\t%s\t%s\t%s\n' ""$ty"" ""$uu"" ""$la"" ""$m"" ""$u"" ""$(fsparams ""$1"" ""$ty"" ""$m"")""
}
for b in /sys/block/*; do d=${b##*/}
  case $d in loop*|ram*|sr*|zram*|nbd*|rbd*|dm-*|md*|fd*) continue ;; esac
  [ -e ""$b/device"" ] || continue
  s=$(( $(cat $b/size) * 512 )); [ ""$s"" -gt 0 ] || continue
  pt=$(blkid -p -s PTTYPE -o value -- /dev/$d 2>/dev/null)
  echo ""disk${t}$d${t}$s${t}${pt:-none}${t}$(cat $b/removable 2>/dev/null)""
  np=0
  for p in $b/$d*; do [ -e ""$p/partition"" ] || continue; np=1; k=${p##*/}
    echo ""part${t}$d${t}$k${t}$(cat $p/partition)${t}$(( $(cat $p/start) * 512 ))${t}$(( $(cat $p/size) * 512 ))${t}$(vol /dev/$k $(cat $p/dev))""
  done
  [ $np = 1 ] || echo ""whole${t}$d${t}$(vol /dev/$d $(cat $b/dev))""
done
if command -v lvs >/dev/null 2>&1; then
  vgs --noheadings --nosuffix --units b -o vg_name,vg_extent_size 2>/dev/null | while read -r v e; do echo ""vg${t}$v${t}$e""; done
  pvs --noheadings -o pv_name,vg_name 2>/dev/null | while read -r p v; do [ -n ""$v"" ] && echo ""pv${t}$(basename $(readlink -f $p))${t}$v""; done
  lvs --noheadings --nosuffix --units b -o vg_name,lv_name,lv_size,lv_attr 2>/dev/null | while read -r v l s a; do
    dv=$(readlink -f /dev/$v/$l 2>/dev/null); mm=""""
    [ -b ""$dv"" ] && mm=$(cat /sys/class/block/${dv##*/}/dev 2>/dev/null)
    echo ""lv${t}$v${t}$l${t}$s${t}$a${t}$(vol /dev/$v/$l ""$mm"")""
  done
fi
awk 'NR > 1 && $2 == ""file"" {print $1}' /proc/swaps 2>/dev/null | while read -r f; do echo ""swapfile${t}$f${t}$(stat -c %s ""$f"")""; done
grep -E '^[[:space:]]*/dev/(vd|xvd)' /etc/fstab 2>/dev/null | head -n 3 | while read -r l; do echo ""fstabdev${t}$l""; done
if [ -r /etc/selinux/config ] && grep -qE '^SELINUX=(enforcing|permissive)' /etc/selinux/config; then echo ""selinux${t}1""; fi
true
";

        static readonly string[] Unsupported = { "btrfs", "zfs_member", "crypto_LUKS", "linux_raid_member", "bcache", "ceph", "ntfs" };

        // Reads the machine; the result is a VmxInfo whose disks become scsi0:N on Proxmox.
        public VmxInfo Probe()
        {
            int code;
            var ssh = Client();
            string output = ssh.Capture(ProbeScript, out code);
            if (code != 0) throw new Exception("Cannot read the machine " + Target.Host + ": " + ssh.Explain(code));
            var spec = new StringBuilder();
            var vm = new VmxInfo { Linux = this, IsWindows = false, VmxPath = "linux://" + Target.Host.ToLowerInvariant() };
            var disks = new List<string[]>();
            var parts = new List<string[]>();     // part / whole lines
            var lvs = new List<string[]>();
            var pvs = new Dictionary<string, string>();   // pv kernel name -> vg
            var missing = new List<string>();
            var errors = new List<string>();
            long mem = 0;
            foreach (string raw in output.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (line.Length == 0 || line.IndexOf('\t') < 0) continue;
                string[] f = line.Split('\t');
                // bash "read" merges empty tab-separated fields, so the host copy marks them "-"
                for (int i = 0; i < f.Length; i++) spec.Append(i > 0 ? "\t" : "").Append(f[i].Length > 0 ? f[i] : "-");
                spec.Append('\n');
                switch (f[0])
                {
                    case "host": Hostname = f[1].Trim(); break;
                    case "os": Os = f[1].Trim(); break;
                    case "kernel": Kernel = f[1].Trim(); break;
                    case "arch": if (f[1].Trim() != "x86_64") errors.Add("only 64-bit x86 machines can be converted (this one is " + f[1].Trim() + ")"); break;
                    case "cpus": int.TryParse(f[1], out vm.Cpus); break;
                    case "mem": long.TryParse(f[1], out mem); break;
                    case "firmware": vm.Uefi = f[1].Trim() == "efi"; break;
                    case "missing": missing.Add(f[1].Trim()); break;
                    case "nic":
                        if (f.Length >= 3 && f[2].Trim().Length > 0) vm.Macs.Add(f[2].Trim().ToLowerInvariant());
                        Network.Add(f[1] + "  " + f[2] + (f.Length > 3 && f[3].Trim().Length > 0 ? "  " + f[3].Trim() : ""));
                        break;
                    case "gateway": if (f[1].Trim().Length > 0) Network.Add("default gateway " + f[1].Trim()); break;
                    case "disk": if (f.Length >= 5 && f[4].Trim() != "1") disks.Add(f); break;
                    case "part": if (f.Length >= 12) parts.Add(f); break;
                    case "whole": if (f.Length >= 8) parts.Add(f); break;
                    case "pv": if (f.Length >= 3) pvs[f[1]] = f[2]; break;
                    case "lv": if (f.Length >= 11) lvs.Add(f); break;
                    case "fstabdev": vm.Warnings.Add("/etc/fstab names a disk as " + f[1].Trim().Split(' ', '\t')[0] + " - on Proxmox it becomes /dev/sdX; use UUID= in fstab"); break;
                }
            }
            vm.DisplayName = Hostname.Length > 0 ? Hostname : Target.Host;
            vm.GuestOS = vm.AgentOs = Os;
            vm.Cpus = Math.Max(vm.Cpus, 1);
            vm.MemoryMb = (int)Math.Max(512, Math.Ceiling(mem / (double)(1L << 20) / 512) * 512);
            if (missing.Count > 0)
                errors.Add("missing on the machine: " + string.Join(", ", missing) + " (install with apt / dnf / yum / zypper)");

            // used space per source disk (file systems on its partitions, LVs on its PVs)
            var used = new Dictionary<string, long>();
            var diskNames = new Dictionary<string, string>();   // disk / partition kernel name -> disk
            foreach (var d in disks) diskNames[d[1]] = d[1];
            foreach (var p in parts) if (p[0] == "part" && diskNames.ContainsKey(p[1])) diskNames[p[2]] = p[1];
            Func<string, string> diskOf = kname => { string d; return kname != null && diskNames.TryGetValue(kname, out d) ? d : null; };
            Action<string, string, string, string, string, string> check = (what, type, mount, usedText, size, owner) =>
            {
                if (Array.IndexOf(Unsupported, type) >= 0)
                    errors.Add(what + " uses " + type + ", which this converter cannot copy yet");
                long u;
                if (mount.Length > 0 && long.TryParse(usedText, out u) && owner != null)
                {
                    long have;
                    used.TryGetValue(owner, out have);
                    used[owner] = have + u;
                }
            };
            foreach (var p in parts)
            {
                bool whole = p[0] == "whole";
                string kname = whole ? p[1] : p[2];
                int b = whole ? 2 : 6;   // TYPE UUID LABEL MOUNT USED PARAMS
                check(kname, p[b], p[b + 3], p[b + 4], whole ? "" : p[5], diskOf(kname));
                string layout = "  " + kname + ": " + (p[b].Length > 0 ? p[b] : "no file system") +
                                (whole ? "" : ", " + Sizing.Gb(long.Parse(p[5], CultureInfo.InvariantCulture))) +
                                (p[b + 3].Length > 0 ? ", mounted on " + p[b + 3] : "");
                long pu;
                if (long.TryParse(p[b + 4], out pu)) layout += ", " + Sizing.Gb(pu) + " used";
                Layout.Add(layout);
            }
            foreach (var l in lvs)
            {
                if (l[4].Length > 0 && l[4][0] != '-')
                    errors.Add("LVM volume " + l[1] + "/" + l[2] + " is a thin, RAID, mirror or snapshot volume (attr " + l[4] + "), which this converter cannot copy yet");
                string owner = null;
                foreach (var kv in pvs) if (kv.Value == l[1]) { owner = diskOf(kv.Key); if (owner != null) break; }
                check("LVM volume " + l[1] + "/" + l[2], l[5], l[8], l[9], l[3], owner);
                long s, u;
                long.TryParse(l[3], out s);
                Layout.Add("  LVM " + l[1] + "/" + l[2] + ": " + (l[5].Length > 0 ? l[5] : "no file system") + ", " + Sizing.Gb(s) +
                           (l[8].Length > 0 ? ", mounted on " + l[8] : "") + (long.TryParse(l[9], out u) ? ", " + Sizing.Gb(u) + " used" : ""));
            }
            foreach (var kv in pvs)
                if (diskOf(kv.Key) == null)
                    errors.Add("LVM physical volume " + kv.Key + " is not on a plain disk (RAID or encrypted), which this converter cannot copy yet");

            // boot disk first: the one with /boot/efi, /boot or /
            string boot = null;
            foreach (string mount in new[] { "/boot/efi", "/boot", "/" })
            {
                foreach (var p in parts)
                {
                    bool whole = p[0] == "whole";
                    if (p[whole ? 5 : 9] == mount) { boot = diskOf(whole ? p[1] : p[2]); break; }
                }
                if (boot == null && mount == "/")
                    foreach (var l in lvs)
                        if (l[8] == "/")
                            foreach (var kv in pvs) if (kv.Value == l[1]) { boot = diskOf(kv.Key); break; }
                if (boot != null) break;
            }
            disks.Sort((a, b) => a[1] == boot ? -1 : b[1] == boot ? 1 : string.CompareOrdinal(a[1], b[1]));
            int unit = 0;
            foreach (var d in disks)
            {
                long size = long.Parse(d[2], CultureInfo.InvariantCulture);
                if (unit == 7) unit++;
                long u;
                var disk = new VmDisk { Slot = "scsi0:" + unit++, CapacityBytes = size, UsedBytes = used.TryGetValue(d[1], out u) ? u : 0 };
                disk.GuestLayout = d[1] + " (" + (d[3] == "dos" ? "MBR" : d[3] == "gpt" ? "GPT" : "no partition table") + ")";
                AddParts(disk, d, parts, pvs, lvs);
                vm.Disks.Add(disk);
                vm.VirtualBytes += size;
                vm.TotalBytes += disk.UsedBytes;
                DiskOrder.Add(d[1]);
            }
            vm.DiskCount = vm.Disks.Count;
            if (vm.DiskCount == 0) errors.Add("no disks found");
            spec.Append("order\t").Append(string.Join(" ", DiskOrder)).Append('\n');
            Spec = spec.ToString();
            if (errors.Count > 0) vm.Error = string.Join("; ", errors);
            vm.ResetState();
            return vm;
        }

        // Partitions of one source disk for the volume sizes ("Data to copy"): file systems the
        // Proxmox host creates new (ext2/3/4, XFS, swap, single-disk LVM) can get a new size.
        static void AddParts(VmDisk disk, string[] d, List<string[]> parts, Dictionary<string, string> pvs, List<string[]> lvs)
        {
            const long MiB = 1L << 20;
            disk.Linux = true;
            disk.Style = d[3] == "gpt" ? "gpt" : d[3] == "dos" ? "mbr" : "";
            if (disk.Style.Length == 0) return;
            var mine = parts.FindAll(p => p[0] == "part" && p[1] == d[1]);
            foreach (var p in mine)
            {
                long start, size, used;
                int num;
                if (!long.TryParse(p[4], out start) || !long.TryParse(p[5], out size)) continue;
                int.TryParse(p[3], out num);
                string type = p[6], mount = p[9];
                var gp = new GuestPart { Offset = start, Size = size, Fs = type, Label = mount };
                bool container = disk.Style == "mbr" && num <= 4 &&
                    mine.Exists(o => o != p && long.Parse(o[4], CultureInfo.InvariantCulture) > start &&
                                     long.Parse(o[4], CultureInfo.InvariantCulture) < start + Math.Max(size, 1024));
                if (container || (disk.Style == "mbr" && num <= 4 && size <= 1024)) { gp.Kind = "extended"; gp.Fs = ""; }
                else if (type == "swap") { gp.Kind = "swap"; gp.Used = 0; gp.Label = "swap"; }
                else if (type == "LVM2_member")
                {
                    gp.Kind = "lvm"; gp.Fs = "LVM";
                    string vg;
                    if (pvs.TryGetValue(p[2], out vg))
                    {
                        gp.Label = "LVM " + vg;
                        int count = 0;
                        foreach (var kv in pvs) if (kv.Value == vg) count++;
                        if (count == 1)
                        {
                            // what its LVs need at least: used + 256 MiB per file system, full size for the rest (swap ...)
                            long need = 0;
                            foreach (var l in lvs)
                            {
                                if (l[1] != vg) continue;
                                long ls, lu;
                                long.TryParse(l[3], out ls);
                                bool fs = (l[5] == "ext2" || l[5] == "ext3" || l[5] == "ext4" || l[5] == "xfs") && l[8].Length > 0 && long.TryParse(l[9], out lu);
                                need += fs ? Math.Min(ls, Math.Max(long.Parse(l[9], CultureInfo.InvariantCulture) + 256 * MiB, l[5] == "xfs" ? 300 * MiB : 0)) : ls;
                            }
                            gp.Used = need;
                        }
                    }
                }
                else if ((type == "ext2" || type == "ext3" || type == "ext4" || type == "xfs") && mount.Length > 0 && long.TryParse(p[10], out used))
                {
                    gp.Kind = "data"; gp.Used = used;
                }
                else gp.Kind = type == "vfat" && mount == "/boot/efi" ? "system" : "other";
                if (gp.Label.Length == 0) gp.Label = p[2] + (type.Length > 0 ? " (" + type + ")" : "");
                else if (gp.Kind == "data") gp.Label = mount + "  (" + p[2] + ")";
                disk.Parts.Add(gp);
            }
            disk.Parts.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        }

        public string Details(VmxInfo vm)
        {
            var sb = new StringBuilder();
            Action<string, string> row = (k, v) => sb.Append(k.PadRight(22)).Append(v).Append("\r\n");
            row("Name:", vm.DisplayName);
            row("Machine type:", "Linux machine (powered on)");
            row("Firmware:", vm.Uefi ? "EFI" : "BIOS");
            row("Operating system:", Os + "  (kernel " + Kernel + ")");
            row("Total size:", Theme.Bytes(vm.VirtualBytes) + ", " + Theme.Bytes(vm.TotalBytes) + " used");
            row("Number of vCPUs:", vm.Cpus.ToString());
            row("RAM:", vm.MemoryMb + " MB");
            sb.Append("Network:\r\n");
            foreach (string n in Network) sb.Append("    ").Append(n).Append("\r\n");
            sb.Append("Disks:\r\n");
            foreach (var d in vm.Disks) sb.Append("    ").Append(d.GuestLayout).Append(" - ").Append(Sizing.Gb(d.CapacityBytes)).Append("\r\n");
            sb.Append("Volumes:\r\n");
            foreach (string l in Layout) sb.Append("  ").Append(l).Append("\r\n");
            if (vm.Error != null) sb.Append("\r\nCannot be converted: ").Append(vm.Error).Append("\r\n");
            foreach (string w in vm.Warnings) sb.Append("\r\nNote: ").Append(w).Append("\r\n");
            return sb.ToString();
        }
    }

    // ------------------------------------------------------------------------
    // A VM on a Proxmox VE host (source). To Proxmox it travels as a vzdump stream
    // (works while it runs), to VMware as stream-optimized VMDKs.
    class PveSourceVm
    {
        public SshTarget Host;
        public string Node;
        public int Vmid;
        public bool Running, Template, Agent;
        public string OsType = "";
        public readonly List<string[]> Drives = new List<string[]>();   // key, volume id, format, size bytes
        public readonly Dictionary<string, string> Config = new Dictionary<string, string>();

        static readonly Regex DriveKey = new Regex("^(ide|sata|scsi|virtio)\\d+$");

        public static string OsLabel(string ostype)
        {
            switch (ostype)
            {
                case "win11": return "Microsoft Windows 11 / Server 2022-2025";
                case "win10": return "Microsoft Windows 10 / Server 2016-2019";
                case "win8": return "Microsoft Windows 8 / Server 2012";
                case "win7": return "Microsoft Windows 7 / Server 2008 R2";
                case "w2k8": return "Microsoft Windows Server 2008";
                case "wvista": return "Microsoft Windows Vista";
                case "w2k3": return "Microsoft Windows Server 2003";
                case "wxp": return "Microsoft Windows XP";
                case "l26": return "Linux (kernel 2.6 - 6.x)";
                case "l24": return "Linux 2.4";
                case "solaris": return "Solaris";
                default: return "Other";
            }
        }

        static long SizeOf(string value)
        {
            var m = Regex.Match(value, "(?:^|,)size=(\\d+(?:\\.\\d+)?)([KMGT]?)");
            if (!m.Success) return 0;
            double n = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            int shift = "_KMGT".IndexOf(m.Groups[2].Value.Length > 0 ? m.Groups[2].Value[0] : '_') * 10;
            return (long)(n * (1L << Math.Max(shift, 0)));
        }

        // Lists the VMs of the node we are logged in to.
        public static List<VmxInfo> List(SshClient ssh, out string node, out string version)
        {
            const string script =
                "export LC_ALL=C\n" +
                "id -u\n" +
                "echo \"##VER $(pveversion 2>/dev/null || echo none)\"\n" +
                "echo \"##NODE $(hostname)\"\n" +
                "echo \"##TOOLS $(for c in vzdump qemu-img zstd; do command -v $c >/dev/null || printf '%s ' $c; done)\"\n" +
                "echo '##LIST'; pvesh get /nodes/$(hostname)/qemu --output-format json 2>/dev/null; echo\n" +
                "for f in /etc/pve/qemu-server/*.conf; do [ -f \"$f\" ] || continue; echo \"##CONF ${f##*/}\"; sed '/^\\[/,$d' \"$f\"; done\n" +
                "true\n";
            int code;
            string output = ssh.Capture(script, out code);
            if (code != 0) throw new Exception("Cannot connect: " + ssh.Explain(code));
            node = ""; version = "";
            var status = new Dictionary<int, Dictionary<string, object>>();
            var vms = new List<VmxInfo>();
            PveSourceVm cur = null;
            bool first = true;
            foreach (string raw in output.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (first && line.Trim().Length > 0)
                {
                    first = false;
                    if (line.Trim() != "0") throw new Exception("Log in as root - reading and exporting VMs needs root on the Proxmox host.");
                    continue;
                }
                if (line.StartsWith("##VER "))
                {
                    version = line.Substring(6).Trim();
                    if (version == "none") throw new Exception("This host is not running Proxmox VE (pveversion not found).");
                }
                else if (line.StartsWith("##NODE ")) node = line.Substring(7).Trim();
                else if (line.StartsWith("##TOOLS ") && line.Substring(8).Trim().Length > 0)
                    throw new Exception("Missing on the Proxmox host: " + line.Substring(8).Trim());
                else if (line.StartsWith("["))
                {
                    try
                    {
                        var arr = new JavaScriptSerializer().DeserializeObject(line) as object[];
                        if (arr != null)
                            foreach (var o in arr)
                            {
                                var d = o as Dictionary<string, object>;
                                if (d != null) status[(int)AgentReport.Num(d, "vmid")] = d;
                            }
                    }
                    catch (Exception) { }
                }
                else if (line.StartsWith("##CONF "))
                {
                    int id;
                    cur = int.TryParse(line.Substring(7).Replace(".conf", "").Trim(), out id) ? new PveSourceVm { Host = ssh.Target, Vmid = id } : null;
                    if (cur != null) vms.Add(new VmxInfo { Pve = cur });
                }
                else if (cur != null)
                {
                    int colon = line.IndexOf(':');
                    if (colon > 0 && !line.StartsWith("#")) cur.Config[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
                }
            }
            foreach (var vm in vms) Fill(vm, node, status);
            vms.Sort((a, b) => a.Pve.Vmid.CompareTo(b.Pve.Vmid));
            return vms;
        }

        static void Fill(VmxInfo vm, string node, Dictionary<int, Dictionary<string, object>> status)
        {
            var p = vm.Pve;
            p.Node = node;
            Func<string, string> get = k => { string v; return p.Config.TryGetValue(k, out v) ? v : ""; };
            Dictionary<string, object> st;
            if (status.TryGetValue(p.Vmid, out st)) p.Running = AgentReport.Str(st, "status") == "running";
            p.Template = get("template") == "1";
            p.OsType = get("ostype");
            p.Agent = Regex.IsMatch(get("agent"), "^(1|enabled=1)");
            vm.DisplayName = get("name").Length > 0 ? get("name") : "VM " + p.Vmid;
            vm.VmxPath = "pve://" + p.Host.Host.ToLowerInvariant() + "/" + p.Vmid;
            vm.IsWindows = p.OsType.StartsWith("w");
            vm.GuestOS = vm.AgentOs = OsLabel(p.OsType);
            vm.Uefi = get("bios") == "ovmf";
            vm.Tpm = get("tpmstate0").Length > 0;
            int cores, sockets, mem;
            if (!int.TryParse(get("cores"), out cores) || cores < 1) cores = 1;
            if (!int.TryParse(get("sockets"), out sockets) || sockets < 1) sockets = 1;
            vm.Cpus = cores * sockets;
            vm.MemoryMb = int.TryParse(get("memory"), out mem) && mem > 0 ? mem : 2048;
            var keys = new List<string>(p.Config.Keys);
            // boot disk first (boot: order=scsi0;ide2;net0 / legacy bootdisk: scsi0)
            string boot = "";
            foreach (Match bm in Regex.Matches(get("boot") + " " + get("bootdisk"), "(?:ide|sata|scsi|virtio)\\d+"))
            {
                string v;
                if (p.Config.TryGetValue(bm.Value, out v) && !v.Contains("media=cdrom")) { boot = bm.Value; break; }
            }
            keys.Sort((a, b) =>
            {
                if (a == b) return 0;
                if (a == boot) return -1;
                if (b == boot) return 1;
                return string.CompareOrdinal(a, b);
            });
            int unit = 0;
            foreach (string k in keys)
            {
                if (!DriveKey.IsMatch(k)) continue;
                string v = p.Config[k];
                if (v.Contains("media=cdrom") || v.StartsWith("none")) continue;
                string volid = v.Split(',')[0];
                long size = SizeOf(v);
                string format = Regex.Match(v, "(?:^|,)format=(\\w+)").Groups[1].Value;
                if (format.Length == 0) format = volid.EndsWith(".qcow2") ? "qcow2" : volid.EndsWith(".vmdk") ? "vmdk" : "raw";
                p.Drives.Add(new[] { k, volid, format, size.ToString(CultureInfo.InvariantCulture) });
                if (unit == 7) unit++;
                vm.Disks.Add(new VmDisk { Slot = "scsi0:" + unit++, CapacityBytes = size, GuestLayout = k + " = " + volid });
                vm.VirtualBytes += size;
            }
            vm.DiskCount = vm.Disks.Count;
            vm.TotalBytes = vm.VirtualBytes;
            foreach (var kv in p.Config)
            {
                if (!Regex.IsMatch(kv.Key, "^net\\d+$")) continue;
                var m = Regex.Match(kv.Value, "=([0-9A-Fa-f]{2}(?::[0-9A-Fa-f]{2}){5})");
                if (m.Success) vm.Macs.Add(m.Groups[1].Value.ToLowerInvariant());
            }
            if (vm.DiskCount == 0) vm.Error = "VM has no disks";
            foreach (var kv in p.Config)
                if (kv.Key.StartsWith("hostpci") || kv.Key.StartsWith("usb"))
                {
                    vm.Warnings.Add("Has PCI/USB passthrough devices - they are removed from the copy");
                    break;
                }
            vm.ResetState();
        }
    }
}
