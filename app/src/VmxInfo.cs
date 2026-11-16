using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Mtaalam.V2V
{
    enum VmState { Ready, Warning, Blocked, Running, Done, Failed, Cancelled }

    class VmDisk
    {
        public string Slot;             // .vmx slot, e.g. scsi0:0
        public long CapacityBytes;
        public long TargetBytes;        // 0 = keep the VMware size
        public long UsedBytes = -1;     // from the agent / VMware Tools, -1 = unknown
        public long MinBytes;           // end of the last partition (agent), 0 = unknown
        public int GuestDisk = -1;      // disk number inside Windows (agent service), -1 = unknown
        public string GuestLayout;      // partitions as the guest sees them

        public long FinalBytes { get { return TargetBytes > 0 ? TargetBytes : CapacityBytes; } }

        // Partitions from the agent (offset order) for volume-based resizing; empty = unknown.
        public readonly List<GuestPart> Parts = new List<GuestPart>();
        public string Style = "";
        public bool Linux;              // Linux finds its file systems by UUID: partitions may move on MBR disks too

        public void SetParts(GuestDisk g)
        {
            var keep = new Dictionary<long, long>();   // a re-measure keeps the chosen sizes
            foreach (var p in Parts) if (p.TargetBytes > 0) keep[p.Offset] = p.TargetBytes;
            Parts.Clear();
            Parts.AddRange(g.Parts);
            Parts.Sort((a, b) => a.Offset.CompareTo(b.Offset));
            foreach (var p in Parts) { long t; if (keep.TryGetValue(p.Offset, out t) && p.Resizable) p.TargetBytes = t; }
            Style = g.Style;
        }

        // True when at least one volume gets a new size (volume-based copy).
        public bool VolumeResize
        {
            get
            {
                foreach (var p in Parts) if (p.TargetBytes > 0 && p.TargetBytes != p.Size) return true;
                return false;
            }
        }

        // Disk size after the volume resize, with the same rules as resize_volumes in
        // vmware2pve.sh: partitions behind a changed one move up (1 MiB aligned), the
        // disk ends 4 MiB-aligned behind the last one. null error = possible.
        public long PlanVolumes(out string error)
        {
            const long MiB = 1L << 20;
            error = null;
            long end = 0;
            bool changed = false;
            foreach (var p in Parts)
            {
                if (p.Kind == "extended") { error = "the disk has logical drives (extended partition)"; return CapacityBytes; }
                if (p.Kind == "dynamic") { error = "it is a dynamic disk"; return CapacityBytes; }
                long start = changed ? (end + MiB - 1) / MiB * MiB : p.Offset;
                long len = p.TargetBytes > 0 ? (p.TargetBytes + MiB - 1) / MiB * MiB : p.Size;
                if (p.TargetBytes > 0 && !p.Resizable) { error = p.Name + " cannot change size"; return CapacityBytes; }
                if (Style == "mbr" && !Linux && start != p.Offset && p.Kind != "recovery")
                {
                    error = "on an MBR disk Windows finds its volumes by their position, so only the last volume " +
                            "(or one followed only by a recovery partition) can change size - " + p.Name + " would move";
                    return CapacityBytes;
                }
                if (len != p.Size || start != p.Offset) changed = true;
                end = start + len;
            }
            long tail = Style == "gpt" ? 33 * 512 : 0;
            return (end + tail + 4 * MiB - 1) / (4 * MiB) * (4 * MiB);
        }
    }

    // What the guest really uses, measured live (agent report or VMware Tools).
    class GuestUsage
    {
        public string Source;
        public double CpuPeakPct = -1;
        public double CpuAvgPct = -1;
        public long MemPeakUsedMb = -1;
        public long DiskUsedBytes = -1;
    }

    class UploadFile
    {
        public string LocalPath;
        public string RemoteName;
        public long Size;
    }

    // A VMware VM as described by its .vmx, plus every file that has to travel
    // to Proxmox (descriptors, -flat/-sNNN extents, snapshot parents).
    class VmxInfo
    {
        static readonly Regex KeyValue = new Regex("^\\s*([^#=\\s]+)\\s*=\\s*\"(.*)\"\\s*$");
        static readonly Regex DiskKey = new Regex("^((scsi|sata|nvme|ide)\\d+:\\d+)\\.filename$");
        static readonly Regex Extent = new Regex("^\\s*(RW|RDONLY|NOACCESS)\\s+(\\d+)\\s+(\\S+)(?:\\s+\"([^\"]+)\")?", RegexOptions.IgnoreCase);
        static readonly Regex ParentHint = new Regex("^\\s*parentFileNameHint\\s*=\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);

        public string VmxPath;
        public string Dir;
        public string DisplayName;
        public string GuestOS;
        public bool IsWindows;
        public bool ShrinkOnBoot;       // live copy: the copy's agent shrinks volumes the host could not resize
        public bool NeedsFinalize;      // Windows copy still on SATA, or its disk still has to be cut: shows "Finalize Windows VM"
        public bool VirtioOnCopy;       // live copy: the agent found the VirtIO drivers, the copy starts on VirtIO
        public bool FileCopyReady;      // live copy: the agent can send volumes file by file (wimlib, agent 1.4+)
        public bool Uefi;
        public bool Tpm;
        public int Cpus;
        public int MemoryMb;
        public int DiskCount;
        public long VirtualBytes;
        public long TotalBytes;
        public readonly List<UploadFile> Files = new List<UploadFile>();
        public readonly List<VmDisk> Disks = new List<VmDisk>();
        public readonly List<string> Macs = new List<string>();      // lowercase aa:bb:cc:dd:ee:ff
        public GuestUsage Usage;
        public GuestAgent Agent;        // live connection to the agent in this VM, if any
        public readonly List<string> Warnings = new List<string>();
        public string Error;

        public VmState State;
        public string StatusText;
        public int Vmid;

        // A failed attempt left this Proxmox VM behind (created by the import, then the copy
        // failed); "Retry" removes it first - only while it still has this name.
        public int PartialVmid;
        public string PartialName = "";

        // Set for VMs listed from an ESXi host (imported by Proxmox straight from the host).
        public EsxiVm Esxi;

        // "VM settings" overrides; 0 / null = take from the VMware config.
        public string TargetName;
        public int TargetVmid;
        public int TargetCores;
        public int TargetMemoryMb;
        public string TargetStorage;      // null = the storage picked under "Proxmox VE target"
        public bool? TargetThick;         // null = the default disk allocation

        public string ProxmoxName { get { return string.IsNullOrEmpty(TargetName) ? DisplayName : TargetName; } }
        public int ProxmoxCores { get { return TargetCores > 0 ? TargetCores : Cpus; } }
        public int ProxmoxMemoryMb { get { return TargetMemoryMb > 0 ? TargetMemoryMb : MemoryMb; } }

        // Total size of the disks on Proxmox (after any resize).
        public long ProxmoxDiskBytes
        {
            get
            {
                if (Disks.Count == 0) return VirtualBytes;
                long sum = 0;
                foreach (var d in Disks) sum += d.FinalBytes;
                return sum;
            }
        }

        public bool Resized
        {
            get
            {
                foreach (var d in Disks) if (d.TargetBytes > 0 && d.TargetBytes != d.CapacityBytes) return true;
                return false;
            }
        }

        // Running machine copied live through the Mtaalam V2V Agent (VSS snapshot).
        public bool HotClone;
        public string AgentOs = "";

        // Running Linux machine (file-level copy over SSH) / VM on a Proxmox VE host.
        public LinuxSource Linux;
        public PveSourceVm Pve;

        // Short description of where the machine comes from (task list, summary).
        public string SourceLabel
        {
            get
            {
                if (HotClone) return (Agent != null ? Agent.Host : "") + " (live, Windows)";
                if (Linux != null) return Linux.Target.Host + " (live, Linux)";
                if (Pve != null) return "Proxmox " + Pve.Host.Host + " · VM " + Pve.Vmid;
                if (Esxi != null) return "ESXi VM";
                return Path.GetFileName(VmxPath);
            }
        }

        public static VmxInfo FromAgent(GuestAgent agent, Dictionary<string, object> info)
        {
            var report = AgentReport.FromJson(info);
            report.Live = true;
            var vm = new VmxInfo
            {
                Agent = agent,
                HotClone = true,
                DisplayName = report.Hostname.Length > 0 ? report.Hostname : agent.Host,
                AgentOs = report.Os,
                GuestOS = report.Os,
                IsWindows = true,
                Uefi = AgentReport.Str(info, "firmware") == "efi",
                Cpus = Math.Max(report.Cpus, 1),
                MemoryMb = (int)Math.Max(Math.Min(report.MemTotalMb, int.MaxValue), 512),
                VmxPath = "agent://" + agent.Host,
            };
            object vr;
            vm.VirtioOnCopy = info.TryGetValue("virtio_ready", out vr) && vr is bool && (bool)vr;
            vm.FileCopyReady = info.TryGetValue("file_copy", out vr) && vr is bool && (bool)vr;
            // round RAM to what the machine was built with (Windows reports a little less)
            vm.MemoryMb = (int)(Math.Ceiling(vm.MemoryMb / 512.0) * 512);
            int system = (int)AgentReport.Num(info, "system_disk");
            var disks = new List<GuestDisk>(report.Disks);
            // boot disk first (it becomes scsi0:0 / the first disk on the new VM)
            disks.Sort((a, b) =>
            {
                int pa = a.Number == system ? 0 : 1, pb = b.Number == system ? 0 : 1;
                return pa != pb ? pa.CompareTo(pb) : a.Number.CompareTo(b.Number);
            });
            disks.RemoveAll(g => g.Size <= 0);
            int unit = 0;
            foreach (var g in disks)
            {
                if (unit == 7) unit++;   // SCSI unit 7 is the controller
                vm.Disks.Add(new VmDisk { Slot = "scsi0:" + unit++, CapacityBytes = g.Size });
                vm.VirtualBytes += g.Size;
                vm.TotalBytes += g.Used > 0 ? g.Used : g.Size;
            }
            vm.DiskCount = vm.Disks.Count;
            foreach (string m in report.Macs) vm.Macs.Add(m);
            report.ApplyTo(vm);
            for (int i = 0; i < disks.Count; i++)   // exact mapping (ApplyTo matches by size only)
            {
                vm.Disks[i].GuestDisk = disks[i].Number;
                vm.Disks[i].UsedBytes = disks[i].Used;
                vm.Disks[i].MinBytes = disks[i].PartEnd;
                vm.Disks[i].GuestLayout = disks[i].Layout;
                vm.Disks[i].SetParts(disks[i]);
            }
            if (vm.DiskCount == 0) vm.Error = "The agent reported no disks (it needs to run as a service with administrator rights)";
            vm.ResetState();
            return vm;
        }

        // Guest OS id for the Proxmox import script (.vmx guestOS) and for VMware (OVF osType).
        public string VmxGuestOs { get { return OsIds(false); } }
        public string VmwareGuestId
        {
            get
            {
                if (Esxi != null && !string.IsNullOrEmpty(Esxi.GuestId)) return Esxi.GuestId;
                return OsIds(true);
            }
        }

        string OsIds(bool vmware)
        {
            string os = (AgentOs.Length > 0 ? AgentOs : GuestOS ?? "").ToLowerInvariant();
            bool server = os.Contains("server");
            string[] ids;   // { vmx guestOS, VMware guestId }
            if (!IsWindows) ids = new[] { "other-64", "other3xLinux64Guest" };
            else if (server && (os.Contains("2022") || os.Contains("2025"))) ids = new[] { "windows2019srvnext-64", "windows2019srvNext_64Guest" };
            else if (server && os.Contains("2019")) ids = new[] { "windows2019srv-64", "windows2019srv_64Guest" };
            else if (server && os.Contains("2016")) ids = new[] { "windows9srv-64", "windows9Server64Guest" };
            else if (server && os.Contains("2012")) ids = new[] { "windows8srv-64", "windows8Server64Guest" };
            else if (server && os.Contains("2008")) ids = new[] { "windows7srv-64", "windows7Server64Guest" };
            else if (server) ids = new[] { "windows2019srv-64", "windows2019srv_64Guest" };
            else if (os.Contains("windows 11")) ids = new[] { "windows11-64", "windows11_64Guest" };
            else if (os.Contains("windows 10")) ids = new[] { "windows9-64", "windows9_64Guest" };
            else if (os.Contains("windows 8")) ids = new[] { "windows8-64", "windows8_64Guest" };
            else if (os.Contains("windows 7")) ids = new[] { "windows7-64", "windows7_64Guest" };
            else ids = new[] { "windows9-64", "windows9_64Guest" };
            return vmware ? ids[1] : ids[0];
        }

        // .vmx describing a hot-clone source, for vmware2pve.sh (its disks are streamed from the agent).
        public string SyntheticVmx(bool newMac)
        {
            var sb = new StringBuilder();
            Action<string, string> kv = (k, v) => sb.Append(k).Append(" = \"").Append(v.Replace("\"", "")).Append("\"\n");
            kv(".encoding", "UTF-8");
            kv("displayName", DisplayName);
            kv("guestOS", VmxGuestOs);
            kv("numvcpus", Cpus.ToString());
            kv("memsize", MemoryMb.ToString());
            if (Uefi) kv("firmware", "efi");
            kv("scsi0.present", "TRUE");
            kv("scsi0.virtualDev", "lsisas1068");
            for (int i = 0; i < Disks.Count; i++)
            {
                kv(Disks[i].Slot + ".present", "TRUE");
                kv(Disks[i].Slot + ".fileName", DiskFileName(i));
            }
            if (Disks.Count > 0) kv("bios.hddOrder", Disks[0].Slot);
            for (int i = 0; i < Math.Max(Macs.Count, 1); i++)
            {
                kv("ethernet" + i + ".present", "TRUE");
                if (!newMac && i < Macs.Count)
                {
                    kv("ethernet" + i + ".addressType", "static");
                    kv("ethernet" + i + ".address", Macs[i]);
                }
            }
            return sb.ToString();
        }

        public string DiskFileName(int index) { return "disk" + index + ".vmdk"; }
        public string DiskFlatName(int index) { return "disk" + index + "-flat.vmdk"; }

        public string DiskDescriptor(int index)
        {
            long sectors = (Disks[index].CapacityBytes + 511) / 512;
            return "# Disk DescriptorFile\nversion=1\nCID=fffffffe\nparentCID=ffffffff\ncreateType=\"vmfs\"\n\n" +
                   "RW " + sectors + " VMFS \"" + DiskFlatName(index) + "\"\n";
        }

        public string PowerLabel
        {
            get
            {
                if (HotClone) return "Running · agent (live copy)";
                if (Linux != null) return "Running · Linux (live copy)";
                if (Pve != null) return "Proxmox VE · " + (Pve.Template ? "template" : Pve.Running ? "running" : "off");
                if (Esxi == null) return "Local files";
                switch (Esxi.PowerState)
                {
                    case "poweredOn": return "ESXi · running";
                    case "poweredOff": return "ESXi · off";
                    case "suspended": return "ESXi · suspended";
                    default: return "ESXi";
                }
            }
        }

        static readonly Regex DatastorePath = new Regex("^\\[([^\\]]+)\\]\\s*(.+)$");

        public static VmxInfo FromEsxi(EsxiVm e)
        {
            var vm = new VmxInfo
            {
                Esxi = e,
                VmxPath = e.VmxPath ?? "",
                DisplayName = e.Name,
                GuestOS = string.IsNullOrEmpty(e.GuestName) ? e.GuestId : e.GuestName,
                IsWindows = (e.GuestId ?? "").StartsWith("win", StringComparison.OrdinalIgnoreCase),
                Uefi = e.Uefi,
                Tpm = e.Tpm,
                Cpus = Math.Max(e.Cpus, 1),
                MemoryMb = Math.Max(e.MemoryMb, 1),
                DiskCount = e.DiskFiles.Count,
                VirtualBytes = e.CapacityBytes,
                TotalBytes = e.DiskUsedBytes > 0 ? e.DiskUsedBytes : e.CommittedBytes,
            };
            foreach (var d in e.Disks) vm.Disks.Add(new VmDisk { Slot = d.Key, CapacityBytes = d.Value });
            foreach (string mac in e.Macs) vm.Macs.Add(mac.ToLowerInvariant());
            vm.Usage = e.ToolsUsage();
            if (vm.Usage != null && vm.Usage.DiskUsedBytes >= 0 && vm.Disks.Count == 1)
                vm.Disks[0].UsedBytes = vm.Usage.DiskUsedBytes;
            if (!DatastorePath.IsMatch(vm.VmxPath)) vm.Error = "VM configuration is not accessible on the ESXi host";
            else if (e.DiskFiles.Count == 0) vm.Error = "VM has no virtual disks";
            else if (e.PowerState == "suspended") vm.Error = "VM is suspended on ESXi - resume and shut it down first";
            foreach (string f in e.DiskFiles)
            {
                if (Regex.IsMatch(f, "-\\d{6}\\.vmdk$", RegexOptions.IgnoreCase))
                {
                    vm.Warnings.Add("Has snapshots - they will be merged; consolidating them on ESXi first is faster");
                    break;
                }
            }
            if (e.PowerState == "poweredOn")
                vm.Warnings.Add(e.ToolsStatus == "guestToolsRunning"
                    ? "Running - it will be shut down on ESXi before copying"
                    : "Running without VMware Tools - shut it down inside the guest first (otherwise it is powered off hard)");
            vm.ResetState();
            return vm;
        }

        // ESXi datastore path "[ds] dir/vm.vmx" -> "ds/dir/vm.vmx" (path inside Proxmox's ESXi import storage).
        public string EsxiRelativePath
        {
            get
            {
                var m = DatastorePath.Match(VmxPath);
                return m.Groups[1].Value + "/" + m.Groups[2].Value.Trim();
            }
        }

        readonly List<string> lines = new List<string>();
        readonly Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> diskKeyToRemote = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public string Get(string key)
        {
            string v;
            return values.TryGetValue(key, out v) ? v : "";
        }

        public string VmxRemoteName { get { return Path.GetFileName(VmxPath); } }

        // Stable per-.vmx folder name on the Proxmox side, so re-runs resume uploads.
        public string SafeName
        {
            get
            {
                string name = Regex.Replace(DisplayName ?? "", "[^A-Za-z0-9._-]+", "-").Trim('-', '.');
                if (name.Length == 0) name = "vm";
                uint hash = 2166136261;
                foreach (char c in VmxPath.ToLowerInvariant()) { hash ^= c; hash *= 16777619; }
                return name + "-" + hash.ToString("x8").Substring(0, 6);
            }
        }

        public string FirmwareLabel
        {
            get { return (Uefi ? "UEFI" : "BIOS") + (Tpm ? " + TPM" : ""); }
        }

        public static VmxInfo Load(string vmxPath)
        {
            var vm = new VmxInfo { VmxPath = Path.GetFullPath(vmxPath) };
            vm.Dir = Path.GetDirectoryName(vm.VmxPath);
            try
            {
                vm.Parse();
            }
            catch (Exception ex)
            {
                vm.Error = ex.Message;
            }
            vm.ResetState();
            return vm;
        }

        public void ResetState()
        {
            Vmid = 0;
            if (Error != null) { State = VmState.Blocked; StatusText = Error; }
            else if (Warnings.Count > 0) { State = VmState.Warning; StatusText = string.Join("; ", Warnings); }
            else { State = VmState.Ready; StatusText = "Ready"; }
        }

        void Parse()
        {
            foreach (string raw in File.ReadAllLines(VmxPath, Encoding.UTF8))
            {
                lines.Add(raw);
                var m = KeyValue.Match(raw);
                if (m.Success) values[m.Groups[1].Value] = m.Groups[2].Value;
            }

            DisplayName = Get("displayName");
            if (DisplayName.Length == 0) DisplayName = Path.GetFileNameWithoutExtension(VmxPath);
            GuestOS = Get("guestOS");
            string guest = GuestOS.ToLowerInvariant();
            IsWindows = guest.StartsWith("win") || guest.StartsWith("longhorn");
            Uefi = Get("firmware").Equals("efi", StringComparison.OrdinalIgnoreCase);
            Tpm = Get("vtpm.present").Equals("TRUE", StringComparison.OrdinalIgnoreCase);
            int n;
            Cpus = int.TryParse(Get("numvcpus"), out n) && n > 0 ? n : 1;
            MemoryMb = int.TryParse(Get("memsize"), out n) && n > 0 ? n : 1024;

            if (Directory.GetDirectories(Dir, "*.lck").Length > 0 || Directory.GetFiles(Dir, "*.lck").Length > 0)
                throw new Exception("VM is running or locked in VMware - shut it down first");
            if (Get("encryption.keySafe").Length > 0)
                Warnings.Add("Encrypted VM - remove encryption in VMware if the import fails");

            var keys = new List<string>(values.Keys);
            keys.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (string key in keys)
            {
                var m = DiskKey.Match(key.ToLowerInvariant());
                if (!m.Success) continue;
                string slot = m.Groups[1].Value;
                if (Get(slot + ".present").Equals("FALSE", StringComparison.OrdinalIgnoreCase)) continue;
                if (Get(slot + ".deviceType").IndexOf("cdrom", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                string file = values[key];
                if (!file.EndsWith(".vmdk", StringComparison.OrdinalIgnoreCase)) continue;

                string local = Resolve(Dir, file);
                if (local == null) throw new Exception("Disk " + slot + " not found: " + file);
                diskKeyToRemote[key] = AddFile(local);
                DiskCount++;
                long capacity = CollectVmdk(local, 0);
                VirtualBytes += capacity;
                Disks.Add(new VmDisk { Slot = slot, CapacityBytes = capacity });
            }
            for (int i = 0; i < 10; i++)
            {
                if (!Get("ethernet" + i + ".present").Equals("TRUE", StringComparison.OrdinalIgnoreCase)) continue;
                string mac = Get("ethernet" + i + ".address");
                if (mac.Length == 0) mac = Get("ethernet" + i + ".generatedAddress");
                if (mac.Length > 0) Macs.Add(mac.ToLowerInvariant());
            }
            if (DiskCount == 0) throw new Exception("No virtual disks found in the .vmx");

            AddFile(VmxPath);
            foreach (var f in Files)
                if (f.RemoteName.IndexOf(',') >= 0)
                    throw new Exception("File name contains a comma, rename it in VMware: " + f.RemoteName);
            if (System.Text.RegularExpressions.Regex.IsMatch(string.Join(" ", diskKeyToRemote.Values), "-\\d{6}\\.vmdk", RegexOptions.IgnoreCase))
                Warnings.Add("Has snapshots - they will be merged; deleting them in VMware first is cleaner");
        }

        static string Resolve(string dir, string file)
        {
            if (Path.IsPathRooted(file) && File.Exists(file)) return Path.GetFullPath(file);
            try
            {
                string rel = Path.Combine(dir, file);
                if (File.Exists(rel)) return Path.GetFullPath(rel);
            }
            catch (ArgumentException) { }
            // ESXi datastore paths (/vmfs/volumes/...) - look next to the .vmx.
            string baseName = file.Substring(Math.Max(file.LastIndexOf('/'), file.LastIndexOf('\\')) + 1);
            string local = Path.Combine(dir, baseName);
            return File.Exists(local) ? local : null;
        }

        string AddFile(string local)
        {
            string name = Path.GetFileName(local);
            foreach (var f in Files)
            {
                if (!f.RemoteName.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                if (f.LocalPath.Equals(local, StringComparison.OrdinalIgnoreCase)) return f.RemoteName;
                throw new Exception("Two different disk files are both named " + name + " - rename one in VMware");
            }
            long size = new FileInfo(local).Length;
            Files.Add(new UploadFile { LocalPath = local, RemoteName = name, Size = size });
            TotalBytes += size;
            return name;
        }

        // Adds a VMDK and everything it depends on; returns its virtual size in bytes.
        long CollectVmdk(string path, int depth)
        {
            if (depth > 64) throw new Exception("Snapshot chain too deep: " + path);
            AddFile(path);
            string dir = Path.GetDirectoryName(path);
            long capacitySectors;
            string descriptor = ReadDescriptor(path, out capacitySectors);
            if (descriptor == null) return capacitySectors * 512;

            long extentSectors = 0;
            foreach (string line in descriptor.Split('\n'))
            {
                var m = Extent.Match(line);
                if (m.Success)
                {
                    extentSectors += long.Parse(m.Groups[2].Value);
                    string name = m.Groups[4].Value;
                    if (name.Length == 0 || m.Groups[3].Value.Equals("ZERO", StringComparison.OrdinalIgnoreCase)) continue;
                    string extent = Path.Combine(dir, Path.GetFileName(name));
                    if (extent.Equals(path, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!File.Exists(extent)) throw new Exception("Missing disk extent file: " + Path.GetFileName(name));
                    AddFile(extent);
                    continue;
                }
                m = ParentHint.Match(line);
                if (m.Success)
                {
                    string hint = m.Groups[1].Value;
                    string parent = Resolve(dir, hint);
                    if (parent == null) throw new Exception("Snapshot parent disk not found: " + hint);
                    if (hint.IndexOf('/') >= 0 || hint.IndexOf('\\') >= 0)
                        Warnings.Add("Snapshot parent uses a path (" + hint + ") - consolidate snapshots in VMware first");
                    CollectVmdk(parent, depth + 1);
                }
            }
            return (capacitySectors > 0 ? capacitySectors : extentSectors) * 512;
        }

        // Text descriptor, or the descriptor embedded in a monolithic sparse VMDK.
        static string ReadDescriptor(string path, out long capacitySectors)
        {
            capacitySectors = 0;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var br = new BinaryReader(fs))
            {
                if (fs.Length >= 512)
                {
                    uint magic = br.ReadUInt32();
                    if (magic == 0x564D444B) // "KDMV"
                    {
                        fs.Position = 12;
                        capacitySectors = br.ReadInt64();
                        fs.Position = 28;
                        long descOffset = br.ReadInt64();
                        long descSize = br.ReadInt64();
                        if (descOffset <= 0 || descSize <= 0 || descSize > 2048) return null;
                        fs.Position = descOffset * 512;
                        byte[] buf = br.ReadBytes((int)(descSize * 512));
                        return Encoding.UTF8.GetString(buf).TrimEnd('\0');
                    }
                }
                if (fs.Length > 2 * 1024 * 1024) return null; // flat/raw data, not a descriptor
                fs.Position = 0;
                string text = Encoding.UTF8.GetString(br.ReadBytes((int)fs.Length));
                return text.IndexOf("createType", StringComparison.OrdinalIgnoreCase) >= 0 ? text : null;
            }
        }

        // The .vmx as uploaded: disk paths reduced to file names, because all
        // files land in one folder on the Proxmox host.
        public byte[] RewrittenVmx()
        {
            var sb = new StringBuilder();
            foreach (string line in lines)
            {
                var m = KeyValue.Match(line);
                string remote;
                if (m.Success && diskKeyToRemote.TryGetValue(m.Groups[1].Value, out remote))
                    sb.Append(m.Groups[1].Value).Append(" = \"").Append(remote).Append("\"\n");
                else
                    sb.Append(line).Append('\n');
            }
            return new UTF8Encoding(false).GetBytes(sb.ToString());
        }
    }
}
