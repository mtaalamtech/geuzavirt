using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Mtaalam.V2V
{
    enum LogKind { Info, Step, Ok, Warn, Error, Remote }

    class ConvertOptions
    {
        public string Storage;
        public string Bridge;
        public string Cpu;
        public string RemoteBase;
        public bool VirtioReady;
        public bool NewMac;
        public bool Start;
        public bool Cleanup;
        public bool ShutdownRunning;
        public bool Thick;                // default disk allocation (VMs can override)
        public bool Template;             // convert the imported VM into a Proxmox template
        public bool ShutdownSource;       // live copies: shut the source machine down afterwards
        public bool RestoreNetwork = true; // live copies: the agent puts the static IPs on the new cards
        public EsxiClient Esxi;
        public bool EsxiDirect;           // Proxmox 8.2+: read disks through its ESXi import storage
        public Dictionary<string, string> StorageTypes = new Dictionary<string, string>();
        public List<TempDir> TempDirs = new List<TempDir>();
    }

    // A directory-backed Proxmox storage that can hold the temp folder.
    class TempDir
    {
        public string Storage;
        public string Path;
        public long FreeBytes;
    }

    class HostInfo
    {
        public string Version;
        public string Node;
        public bool SupportsEsxiImport;   // Proxmox VE 8.2+ ships the ESXi import storage
        public readonly List<StorageItem> Storages = new List<StorageItem>();
        public readonly List<string> Bridges = new List<string>();
        public readonly List<TempDir> TempDirs = new List<TempDir>();   // most free space first
        public string VirtioIso;
    }

    class StorageItem
    {
        public string Name;
        public string Type;
        public long FreeBytes;
        public override string ToString()
        {
            return Name + "   (" + Type + ", " + Theme.Bytes(FreeBytes) + " free)";
        }
    }

    // Turns the running totals of one copy step into increments; a smaller total means
    // a new step (next file / disk) started.
    class ByteCounter
    {
        long last;
        public void Reset() { last = 0; }
        public long Delta(long cumulative)
        {
            if (cumulative < last) last = 0;
            long d = cumulative - last;
            last = cumulative;
            return d;
        }
    }

    // Uploads each VM to the Proxmox host and runs vmware2pve.sh there.
    class Converter
    {
        public const string ToolDir = "/root/mtaalam-v2v";
        public const string ToolPath = ToolDir + "/vmware2pve.sh";

        static readonly Regex Percent = new Regex("\\((\\d+(?:\\.\\d+)?)%\\)");
        static readonly Regex QemuPercent = new Regex("^\\s*\\((\\d+(?:\\.\\d+)?)/100%\\)");
        static readonly Regex Imported = new Regex("==> VM (\\d+) imported");
        static readonly Regex Created = new Regex("^==> creating VM (\\d+) \\(([^)]*)\\)");

        public event Action<string, LogKind> Log;
        public event Action<VmxInfo> VmChanged;
        public event Action<string, double, double> Progress; // label, current VM, overall
        public event Action<long> Moved;                      // bytes copied since the last call (speed display)

        readonly SshClient ssh;
        volatile bool cancel;
        readonly ByteCounter counter = new ByteCounter();

        // cumulative byte counts of one transfer step -> deltas for Moved
        void Bytes(long cumulative)
        {
            long d = counter.Delta(cumulative);
            if (d > 0 && Moved != null) Moved(d);
        }

        public Converter(SshClient ssh)
        {
            this.ssh = ssh;
        }

        public void Cancel()
        {
            cancel = true;
            ssh.Kill();
        }

        void Emit(string text, LogKind kind)
        {
            if (Log != null) Log(text, kind);
        }

        void Report(string label, double current, double overall)
        {
            if (Progress != null) Progress(label, current, overall);
        }

        void Changed(VmxInfo vm)
        {
            if (VmChanged != null) VmChanged(vm);
        }

        public static HostInfo Probe(SshClient ssh)
        {
            const string script =
                "id -u\n" +
                "echo '##VER'; pveversion 2>/dev/null || echo none\n" +
                "echo '##NODE'; hostname\n" +
                "echo '##ST'; pvesm status --content images 2>/dev/null | awk 'NR>1 && $3==\"active\" {print $1\"\\t\"$2\"\\t\"$6}'\n" +
                "echo '##BR'; for i in /sys/class/net/*; do [ -d \"$i/bridge\" ] && basename \"$i\"; done\n" +
                "echo '##ISO'; for s in $(pvesm status --content iso 2>/dev/null | awk 'NR>1{print $1}'); do " +
                "pvesm list \"$s\" --content iso 2>/dev/null | awk '$1 ~ /virtio-win/ {print $1}'; done\n" +
                "echo '##TMP'; awk '/^[a-z]+: / {t=$1; s=$2} /^[ \\t]+path / && (t==\"dir:\"||t==\"nfs:\"||t==\"cifs:\") {print s\"\\t\"$2}' /etc/pve/storage.cfg 2>/dev/null |\n" +
                "while IFS=$'\\t' read -r s p; do [ -d \"$p\" ] && printf '%s\\t%s\\t%s\\n' \"$s\" \"$p\" \"$(df -PB1 \"$p\" | awk 'NR==2{print $4}')\"; done\n" +
                "true\n";
            int code;
            string output = ssh.Capture(script, out code);
            if (code != 0) throw new Exception("Cannot connect: " + ssh.Explain(code));

            var info = new HostInfo();
            string section = "UID";
            foreach (string raw in output.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("##")) { section = line.Substring(2); continue; }
                switch (section)
                {
                    case "UID":
                        if (line != "0") throw new Exception("Log in as root - importing VMs needs root on the Proxmox host.");
                        break;
                    case "VER":
                        if (line == "none") throw new Exception("This host is not running Proxmox VE (pveversion not found).");
                        info.Version = line;
                        var v = Regex.Match(line, "pve-manager/(\\d+)\\.(\\d+)");
                        info.SupportsEsxiImport = v.Success &&
                            int.Parse(v.Groups[1].Value) * 100 + int.Parse(v.Groups[2].Value) >= 802;
                        break;
                    case "NODE":
                        info.Node = line;
                        break;
                    case "ST":
                        string[] f = line.Split('\t');
                        long kb;
                        if (f.Length >= 3 && long.TryParse(f[2], out kb))
                            info.Storages.Add(new StorageItem { Name = f[0], Type = f[1], FreeBytes = kb * 1024 });
                        break;
                    case "BR":
                        info.Bridges.Add(line);
                        break;
                    case "ISO":
                        info.VirtioIso = line;
                        break;
                    case "TMP":
                        string[] t = line.Split('\t');
                        long free;
                        if (t.Length >= 3 && long.TryParse(t[2], out free))
                            info.TempDirs.Add(new TempDir { Storage = t[0], Path = t[1], FreeBytes = free });
                        break;
                }
            }
            info.TempDirs.Sort((a, b) => b.FreeBytes.CompareTo(a.FreeBytes));
            if (info.Storages.Count == 0) throw new Exception("No active storage for VM disks found on the host.");
            return info;
        }

        public static void InstallTool(SshClient ssh)
        {
            byte[] tool;
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("vmware2pve.sh"))
            using (var ms = new MemoryStream())
            {
                s.CopyTo(ms);
                tool = new UTF8Encoding(false).GetBytes(Encoding.UTF8.GetString(ms.ToArray()).Replace("\r\n", "\n"));
            }
            int code;
            ssh.Capture("mkdir -p " + SshClient.Q(ToolDir), out code);
            if (code != 0) throw new Exception("Cannot create " + ToolDir + ": " + ssh.Explain(code));
            ssh.Upload(new MemoryStream(tool), ToolPath, null);
            ssh.Capture("chmod 755 " + SshClient.Q(ToolPath), out code);
            string syntax = ssh.Capture("bash -n " + SshClient.Q(ToolPath) + " 2>&1", out code);
            if (code != 0) throw new Exception("The import script failed its syntax check on the Proxmox host - please send this to support:\n" + syntax.Trim());
        }

        static string EsxiStorageId(string host)
        {
            string id = "mtv2v-" + Regex.Replace(host.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
            return id.Length > 40 ? id.Substring(0, 40) : id;
        }

        // Registers the ESXi host as a Proxmox "esxi" import storage, so the
        // Proxmox node reads the disks straight from ESXi (nothing passes this PC).
        void AddEsxiStorage(EsxiClient esxi)
        {
            string id = EsxiStorageId(esxi.Host);
            Emit("Adding ESXi host " + esxi.Host + " to Proxmox as import storage '" + id + "'", LogKind.Step);
            int code;
            ssh.Capture(
                "id=" + SshClient.Q(id) + "\n" +
                "if grep -q \"^esxi: $id\\$\" /etc/pve/storage.cfg; then pvesm remove \"$id\"; fi\n" +
                "pvesm add esxi \"$id\" --server " + SshClient.Q(esxi.Host) + " --username " + SshClient.Q(esxi.User) +
                " --password " + SshClient.Q(esxi.Password) + " --skip-cert-verification 1 || exit 1\n" +
                "pvesm list \"$id\" >/dev/null", out code);
            if (code != 0)
                throw new Exception("Proxmox could not connect to the ESXi host: " + ssh.Explain(code));
        }

        void RemoveEsxiStorage(EsxiClient esxi)
        {
            string id = EsxiStorageId(esxi.Host);
            int code;
            ssh.Capture("pvesm remove " + SshClient.Q(id) + " 2>/dev/null; true", out code);
            Emit("Removed ESXi import storage '" + id + "' from Proxmox.", LogKind.Info);
        }

        public void Run(IList<VmxInfo> vms, ConvertOptions o)
        {
            ssh.Reset();
            int ok = 0;
            bool needsEsxi = false;
            if (o.EsxiDirect)
                foreach (var vm in vms) if (vm.Esxi != null) needsEsxi = true;
            if (needsEsxi)
            {
                try { AddEsxiStorage(o.Esxi); }
                catch (Exception ex)
                {
                    Emit(ex.Message, LogKind.Error);
                    foreach (var vm in vms)
                        if (vm.Esxi != null) { vm.State = VmState.Failed; vm.StatusText = "Failed: " + ex.Message; Changed(vm); }
                    needsEsxi = false;
                    var local = new List<VmxInfo>();
                    foreach (var vm in vms) if (vm.Esxi == null) local.Add(vm);
                    vms = local;
                }
            }
            try
            {
                ok = RunAll(vms, o);
            }
            finally
            {
                if (needsEsxi)
                {
                    ssh.Reset();
                    try { RemoveEsxiStorage(o.Esxi); } catch (Exception) { }
                }
            }
            Report(cancel ? "Cancelled" : "Finished", cancel ? 0 : 1, cancel ? 0 : 1);
            Emit(string.Format("Finished: {0} of {1} VM(s) imported.", ok, vms.Count), ok == vms.Count ? LogKind.Ok : LogKind.Warn);
        }

        int RunAll(IList<VmxInfo> vms, ConvertOptions o)
        {
            int ok = 0;
            for (int i = 0; i < vms.Count && !cancel; i++)
            {
                var vm = vms[i];
                vm.State = VmState.Running;
                vm.StatusText = "Starting...";
                Changed(vm);
                try
                {
                    ConvertOne(vm, o, i, vms.Count);
                    ok++;
                }
                catch (Exception ex)
                {
                    if (cancel || ex is OperationCanceledException)
                    {
                        vm.State = VmState.Cancelled;
                        vm.StatusText = "Cancelled";
                        Emit(vm.DisplayName + ": cancelled", LogKind.Warn);
                    }
                    else
                    {
                        vm.State = VmState.Failed;
                        vm.StatusText = "Failed: " + ex.Message.Split('\n')[0];
                        Emit(vm.DisplayName + ": " + ex.Message, LogKind.Error);
                    }
                }
                Changed(vm);
            }
            return ok;
        }

        // A smaller disk than its partitions need would be imported at full size after
        // hours of copying; stop before the VM is touched instead.
        void CheckResize(VmxInfo vm)
        {
            foreach (var d in vm.Disks)
            {
                if (d.VolumeResize)
                {
                    if (!vm.HotClone && vm.Linux == null)
                        throw new Exception("Disk " + d.Slot + ": volume sizes can only be changed for a live copy (running machine). The VM was not touched.");
                    string error;
                    d.PlanVolumes(out error);
                    if (error != null)
                        throw new Exception("Disk " + d.Slot + ": the volumes cannot be resized - " + error + ". Nothing was changed.");
                    foreach (var p in d.Parts)
                        if (p.TargetBytes > 0 && p.TargetBytes < p.MinBytes)
                            throw new Exception(string.Format("Disk {0}: {1} is set to {2}, but it holds {3} - at least {4} is needed. Nothing was changed.",
                                d.Slot, p.Name, Theme.Bytes(p.TargetBytes), Theme.Bytes(p.Used), Theme.Bytes(p.MinBytes)));
                    continue;
                }
                if (d.TargetBytes <= 0 || d.TargetBytes >= d.CapacityBytes) continue;
                if (d.MinBytes > d.TargetBytes)
                    throw new Exception(string.Format("Disk {0} is set to {1}, but its partitions still reach {2}. {3} The VM was not touched.",
                        d.Slot, Theme.Bytes(d.TargetBytes), Theme.Bytes(d.MinBytes),
                        vm.Agent != null && !vm.Agent.IsLinux
                            ? "Open Target settings and save the size again: the agent shrinks the volume in the guest first."
                            : "Shrink the volume inside the guest (or connect the guest agent), or choose a larger size."));
                if (d.MinBytes <= 0)
                    Emit(string.Format("  Disk {0}: the partition layout is unknown - if {1} would cut a partition, Proxmox keeps the original size {2}.",
                        d.Slot, Theme.Bytes(d.TargetBytes), Theme.Bytes(d.CapacityBytes)), LogKind.Warn);
            }
        }

        string ImportArgs(VmxInfo vm, ConvertOptions o)
        {
            var cmd = new StringBuilder();
            cmd.Append(" --storage ").Append(SshClient.Q(vm.TargetStorage ?? o.Storage));
            if (vm.TargetThick ?? o.Thick) cmd.Append(" --preallocate");
            cmd.Append(" --bridge ").Append(SshClient.Q(o.Bridge));
            cmd.Append(" --cpu ").Append(SshClient.Q(o.Cpu));
            if (!string.IsNullOrEmpty(vm.TargetName)) cmd.Append(" --name ").Append(SshClient.Q(vm.TargetName));
            if (vm.TargetVmid > 0) cmd.Append(" --vmid ").Append(vm.TargetVmid);
            if (vm.TargetCores > 0) cmd.Append(" --cores ").Append(vm.TargetCores);
            if (vm.TargetMemoryMb > 0) cmd.Append(" --memory ").Append(vm.TargetMemoryMb);
            foreach (var d in vm.Disks)
            {
                if (d.TargetBytes > 0 && d.TargetBytes != d.CapacityBytes)
                    cmd.Append(" --disk-size ").Append(SshClient.Q(d.Slot + "=" + d.TargetBytes));
                if ((!vm.HotClone && vm.Linux == null) || !d.VolumeResize) continue;
                foreach (var p in d.Parts)
                    if (p.TargetBytes > 0 && p.TargetBytes != p.Size)
                        cmd.Append(" --volume-size ").Append(SshClient.Q(d.Slot + "@" + p.Offset + "=" + p.TargetBytes));
            }
            if ((o.VirtioReady || vm.VirtioOnCopy) && vm.IsWindows) cmd.Append(" --virtio-ready");
            if (o.NewMac) cmd.Append(" --new-mac");
            if (o.Template) cmd.Append(" --template");
            else if (o.Start) cmd.Append(" --start");
            return cmd.ToString();
        }

        // Retry: the VM a failed attempt created is removed, so the copy can use its ID again.
        // Only a VM that still has the name it was created with (the user may have reused the ID).
        void RemovePartial(VmxInfo vm)
        {
            int id = vm.PartialVmid, code;
            string conf = ssh.Capture("qm config " + id + " 2>/dev/null", out code);
            if (code != 0) { vm.PartialVmid = 0; return; }   // already removed
            string name = "";
            foreach (string l in conf.Split('\n'))
                if (l.StartsWith("name:")) name = l.Substring(5).Trim();
            if (name != vm.PartialName)
                throw new Exception("VM " + id + " from the failed attempt is now named \"" + name + "\" and was not removed. " +
                                    "Remove it in Proxmox or choose another VM ID, then retry.");
            Emit("  Removing VM " + id + " (" + name + ") left over from the failed attempt...", LogKind.Info);
            string output = ssh.Capture("qm destroy " + id + " --purge 1 --destroy-unreferenced-disks 1 2>&1", out code);
            if (code != 0)
                throw new Exception("Removing VM " + id + " from the failed attempt failed: " + output.Trim() +
                                    " - remove it in Proxmox (qm destroy " + id + " --purge), then retry.");
            vm.PartialVmid = 0;
        }

        void ConvertOne(VmxInfo vm, ConvertOptions o, int index, int count)
        {
            Action<string, double> progress = (label, f) => Report(label, f, (index + f) / count);
            CheckResize(vm);
            if (vm.PartialVmid > 0) RemovePartial(vm);
            counter.Reset();
            if (vm.HotClone)
            {
                ConvertFromAgent(vm, o, index, count, progress);
                return;
            }
            if (vm.Linux != null)
            {
                ConvertFromLinux(vm, o, index, count, progress);
                return;
            }
            if (vm.Pve != null)
            {
                ConvertFromProxmox(vm, o, index, count, progress);
                return;
            }
            if (vm.Esxi != null)
            {
                ConvertFromEsxi(vm, o, index, count, progress);
                return;
            }
            string dir = o.RemoteBase.TrimEnd('/') + "/" + vm.SafeName;
            Emit(string.Format("[{0}/{1}] {2}  ->  {3}", index + 1, count, vm.DisplayName, dir), LogKind.Step);

            // 1. Free space + files already uploaded by an earlier run.
            long free;
            var existing = PrepareDir(dir, out free);

            long total = 0, needed = 0;
            foreach (var f in vm.Files)
            {
                total += f.Size;
                long have;
                if (!(existing.TryGetValue(f.RemoteName, out have) && have == f.Size)) needed += f.Size;
            }
            if (needed > free)
                throw new Exception(string.Format(
                    "Not enough space in {0}: need {1}, {2} free. Pick an upload folder with more space.",
                    o.RemoteBase, Theme.Bytes(needed), Theme.Bytes(free)));

            // 2. Upload (first half of the VM's progress bar).
            long doneBefore = 0;
            foreach (var f in vm.Files)
            {
                if (cancel) throw new OperationCanceledException();
                string remote = dir + "/" + f.RemoteName;
                bool isVmx = f.LocalPath.Equals(vm.VmxPath, StringComparison.OrdinalIgnoreCase);
                long have;
                if (!isVmx && existing.TryGetValue(f.RemoteName, out have) && have == f.Size)
                {
                    Emit("  " + f.RemoteName + " already uploaded - skipping", LogKind.Info);
                    doneBefore += f.Size;
                    continue;
                }

                Emit("  Uploading " + f.RemoteName + " (" + Theme.Bytes(f.Size) + ")", LogKind.Info);
                vm.StatusText = "Uploading " + f.RemoteName;
                Changed(vm);
                var clock = Stopwatch.StartNew();
                long lastTick = 0;
                long baseDone = doneBefore;
                Action<long> onBytes = done =>
                {
                    Bytes(done);
                    if (clock.ElapsedMilliseconds - lastTick < 250 && done < f.Size) return;
                    lastTick = clock.ElapsedMilliseconds;
                    double secs = Math.Max(clock.Elapsed.TotalSeconds, 0.001);
                    string label = string.Format("Uploading {0}: {1} / {2}  ({3}/s)",
                        f.RemoteName, Theme.Bytes(done), Theme.Bytes(f.Size), Theme.Bytes((long)(done / secs)));
                    progress(label, total == 0 ? 0 : 0.5 * (baseDone + done) / total);
                };

                if (isVmx)
                {
                    ssh.Upload(new MemoryStream(vm.RewrittenVmx()), remote, onBytes);
                }
                else
                {
                    using (var fs = new FileStream(f.LocalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20))
                        ssh.Upload(fs, remote, onBytes);
                }
                doneBefore += f.Size;
            }

            // 3. Import on the host (second half).
            string script = ToolPath + " import " + SshClient.Q(dir + "/" + vm.VmxRemoteName) + ImportArgs(vm, o) + " < /dev/null 2>&1";
            RunImport(vm, o, script, (label, f) => progress(label, 0.5 + 0.5 * f));
            if (o.Cleanup) RemoveDir(dir);
        }

        // Creates a working folder on the host; returns its files (name -> size) and the free space.
        Dictionary<string, long> PrepareDir(string dir, out long free)
        {
            int code;
            string listing = ssh.Capture(
                "set -e\nmkdir -p " + SshClient.Q(dir) + "\ncd " + SshClient.Q(dir) + "\n" +
                "df -PB1 . | awk 'NR==2{print $4}'\n" +
                "for f in *; do [ -f \"$f\" ] && stat -c '%s %n' -- \"$f\"; done\ntrue", out code);
            if (code != 0) throw new Exception("Cannot prepare " + dir + ": " + ssh.Explain(code));

            string[] rows = listing.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            free = rows.Length > 0 ? long.Parse(rows[0].Trim(), CultureInfo.InvariantCulture) : 0;
            var existing = new Dictionary<string, long>();
            for (int r = 1; r < rows.Length; r++)
            {
                string row = rows[r].TrimEnd('\r');
                int sp = row.IndexOf(' ');
                long size;
                if (sp > 0 && long.TryParse(row.Substring(0, sp), out size)) existing[row.Substring(sp + 1)] = size;
            }
            return existing;
        }

        void RemoveDir(string dir)
        {
            int code;
            ssh.Capture("rm -rf -- " + SshClient.Q(dir), out code);
            Emit("  Removed temporary files from " + dir, LogKind.Info);
        }

        void ConvertFromEsxi(VmxInfo vm, ConvertOptions o, int index, int count, Action<string, double> progress)
        {
            Emit(string.Format("[{0}/{1}] {2}  (ESXi {3}: {4})", index + 1, count, vm.DisplayName, o.Esxi.Host, vm.VmxPath), LogKind.Step);

            DownloadPlan plan = o.EsxiDirect ? null : PlanDownload(vm, o);

            var state = o.Esxi.Refresh(vm.Esxi.Moref);
            vm.Esxi.PowerState = state.PowerState;
            if (state.PowerState != "poweredOff")
            {
                if (!o.ShutdownRunning)
                    throw new Exception("VM is running on ESXi. Shut it down, or tick \"Shut down running VMs on ESXi\".");
                vm.StatusText = "Shutting down on ESXi";
                Changed(vm);
                var agent = vm.Agent;
                if (agent != null && !agent.IsLinux)
                {
                    // fresh copy of the static IP settings, restored by the agent on Proxmox
                    try { agent.SaveNetwork(); Emit("  Network settings saved in the guest (the agent restores them on Proxmox).", LogKind.Info); }
                    catch (Exception ex) { Emit("  Could not save the network settings through the agent: " + ex.Message, LogKind.Warn); }
                }
                Func<string> agentShutdown = null;
                if (agent != null)
                    agentShutdown = () =>
                    {
                        try { agent.Shutdown(); return null; }
                        catch (Exception ex) { return ex.Message; }
                    };
                o.Esxi.ShutDown(vm.Esxi.Moref, TimeSpan.FromMinutes(5), msg => Emit("  " + msg, LogKind.Info), () => cancel, agentShutdown);
                vm.Esxi.PowerState = "poweredOff";
                Emit("  VM is powered off on ESXi.", LogKind.Info);
            }

            if (plan != null)
            {
                DownloadFromEsxi(vm, o, plan, progress);
                return;
            }

            string id = EsxiStorageId(o.Esxi.Host);
            string rel = "ha-datacenter/" + vm.EsxiRelativePath;
            string script =
                "vol=" + SshClient.Q(id + ":" + rel) + "\n" +
                "p=$(pvesm path \"$vol\" 2>/dev/null || true)\n" +
                "[ -n \"$p\" ] && [ -f \"$p\" ] || p=" + SshClient.Q("/run/pve/import/esxi/" + id + "/mnt/" + rel) + "\n" +
                "[ -f \"$p\" ] || { echo \"error: Proxmox cannot read $vol from the ESXi host\"; exit 1; }\n" +
                "exec " + ToolPath + " import \"$p\"" + ImportArgs(vm, o) + " < /dev/null 2>&1";
            Emit("  Proxmox is copying the disks directly from ESXi...", LogKind.Info);
            RunImport(vm, o, script, progress);
        }

        // Running Windows machine (physical or any hypervisor): the agent snapshots its
        // volumes with VSS and the Proxmox node pulls each disk from the agent (encrypted)
        // straight into the new volume. The machine keeps running during the copy.
        void ConvertFromAgent(VmxInfo vm, ConvertOptions o, int index, int count, Action<string, double> progress)
        {
            var agent = vm.Agent;
            Emit(string.Format("[{0}/{1}] {2}  (live copy from the running machine {3})", index + 1, count, vm.DisplayName, agent.Host), LogKind.Step);
            string storage = vm.TargetStorage ?? o.Storage, stype;
            o.StorageTypes.TryGetValue(storage, out stype);
            if (Array.IndexOf(StreamableTypes, stype) < 0)
                throw new Exception("Storage '" + storage + "' (" + stype + ") cannot receive a live copy. Pick an LVM-thin, ZFS, LVM or directory storage. Nothing was changed.");
            int code;
            string missing = ssh.Capture("for c in curl openssl perl losetup; do command -v $c >/dev/null || echo $c; done", out code).Trim();
            if (missing.Length > 0)
                throw new Exception("Missing on the Proxmox host: " + missing.Replace("\n", ", ") + " (install with apt). Nothing was changed.");
            bool resize = false, fileCopy = false;
            foreach (var d in vm.Disks) resize |= d.VolumeResize;
            if (resize)
            {
                // Resized volumes are created new at their size on the Proxmox host and filled with their
                // files (wimlib + mkntfs); agents without wimlib fall back to ntfsresize after the copy.
                string tools = ssh.Capture(
                    "if ! command -v wimlib-imagex >/dev/null || ! command -v mkntfs >/dev/null || ! command -v ntfsresize >/dev/null; then " +
                    "t=$(mktemp); DEBIAN_FRONTEND=noninteractive apt-get install -y -q wimtools ntfs-3g >$t 2>&1 || " +
                    "{ apt-get update -q >$t 2>&1; DEBIAN_FRONTEND=noninteractive apt-get install -y -q wimtools ntfs-3g >>$t 2>&1 || " +
                    "grep -E '^(E|W):' $t | tail -n 4 | sed 's/^/apt: /'; }; rm -f $t; fi; " +
                    "command -v ntfsresize >/dev/null && echo ntfs; command -v wimlib-imagex >/dev/null && command -v mkntfs >/dev/null && echo wim", out code);
                // whole lines only: apt's messages contain the words "wimtools" and "ntfs-3g"
                bool hostWim = false, hostNtfs = false;
                var aptLines = new List<string>();
                foreach (string raw in tools.Split('\n'))
                {
                    string line = raw.Trim();
                    if (line == "wim") hostWim = true;
                    else if (line == "ntfs") hostNtfs = true;
                    else if (line.StartsWith("apt: ", StringComparison.Ordinal)) aptLines.Add(line.Substring(5));
                }
                string aptWhy = aptLines.Count == 0 ? "" : " The installer said: " + string.Join(" | ", aptLines.ToArray()) + ".";
                if (aptWhy.Contains("resolving") || aptWhy.Contains("Could not connect") || aptWhy.Contains("unreachable"))
                    aptWhy += " The Proxmox host cannot reach the internet: check its gateway and DNS (in Proxmox: the node > System > Network and DNS).";
                fileCopy = hostWim && vm.FileCopyReady;
                if (!fileCopy && !hostNtfs)
                    throw new Exception("Resizing volumes needs wimtools and ntfs-3g on the Proxmox host, and installing them failed." + aptWhy + " Run " +
                                        "\"apt install wimtools ntfs-3g\" on the host, or keep the volume sizes (Options > Data to copy > " +
                                        "Maintain size). Nothing was changed.");
                if (!fileCopy)
                    Emit(!hostWim
                        ? "  wimtools could not be installed on the Proxmox host (\"apt install wimtools\"):" + aptWhy + " The volumes are copied block by block and shrunk with ntfsresize afterwards."
                        : "  The agent on " + agent.Host + " has no file copy (wimlib): the volumes are copied block by block and shrunk with ntfsresize " +
                          "afterwards. Connect with the administrator password to update the agent.", LogKind.Warn);
                foreach (var d in vm.Disks)
                    if (d.VolumeResize)
                    {
                        string err;
                        Emit(string.Format(fileCopy
                            ? "  Disk {0}: {1} -> new disk {2}. Proxmox creates the disk at {2}; each resized volume is created new at its size " +
                              "and its files are copied into it, the other partitions block by block. The source machine is not changed."
                            : "  Disk {0}: {1} -> new disk {2} (volumes resized on the Proxmox host; the source machine is not changed).",
                            d.Slot, Theme.Bytes(d.CapacityBytes), Theme.Bytes(d.PlanVolumes(out err))), LogKind.Info);
                    }
            }
            string dir = o.RemoteBase.TrimEnd('/') + "/" + vm.SafeName;
            long free;
            PrepareDir(dir, out free);

            vm.StatusText = "Taking VSS snapshots in the guest";
            Changed(vm);
            Emit("  The agent prepares the boot drivers, saves the network settings and takes VSS snapshots...", LogKind.Info);
            var disks = new List<int>();
            foreach (var d in vm.Disks) disks.Add(d.GuestDisk);
            if (disks.Contains(-1)) throw new Exception("The disks of " + vm.DisplayName + " could not be matched to the guest's disks - connect the agent again.");
            // if ntfsresize fails on the host, the copy's agent shrinks these volumes with Windows
            var shrink = new Dictionary<string, long>();
            foreach (var d in vm.Disks)
                if (d.VolumeResize && !fileCopy)
                    foreach (var p in d.Parts)
                        if (p.TargetBytes > 0 && p.TargetBytes < p.Size && p.Letter.Length > 0) shrink[p.Letter.Substring(0, 1) + ":"] = p.TargetBytes;
            var prep = agent.ClonePrepare(disks, "kvm", o.RestoreNetwork && !o.Template, shrink);
            vm.ShrinkOnBoot = shrink.Count > 0 && !o.Template && !OlderThan(AgentReport.Str(prep, "agent_version"), "1.3.1");
            string session = AgentReport.Str(prep, "session");
            var snaps = prep.ContainsKey("snapshots") ? prep["snapshots"] as System.Collections.IEnumerable : null;
            var names = new List<string>();
            if (snaps != null) foreach (object s in snaps) names.Add(s.ToString());
            Emit("  Snapshots taken: " + (names.Count > 0 ? string.Join(", ", names) : "none") + ". The machine keeps running.", LogKind.Info);
            string noSnapshot = GuestAgent.NoSnapshotNote(prep);
            if (noSnapshot != null) Emit(noSnapshot, LogKind.Warn);
            object vr;
            vm.VirtioOnCopy = vm.IsWindows && prep.TryGetValue("virtio_ready", out vr) && vr is bool && (bool)vr;
            if (vm.IsWindows && !o.VirtioReady)
                Emit(vm.VirtioOnCopy
                    ? "  The VirtIO drivers are installed on the machine: the copy uses VirtIO SCSI and VirtIO network right away."
                    : "  The VirtIO drivers are not installed on the machine: the copy starts on SATA (install virtio-win-guest-tools on the " +
                      "source before converting to skip this).", LogKind.Info);
            try
            {
                var files = new StringBuilder("umask 077\ncd " + SshClient.Q(dir) + " || exit 1\n");
                files.Append("printf '%s' ").Append(SshClient.Q(vm.SyntheticVmx(o.NewMac))).Append(" > vm.vmx\n");
                var urls = new StringBuilder();
                for (int i = 0; i < vm.Disks.Count; i++)
                {
                    files.Append("printf '%s' ").Append(SshClient.Q(vm.DiskDescriptor(i))).Append(" > ").Append(SshClient.Q(vm.DiskFileName(i))).Append('\n');
                    var disk = vm.Disks[i];
                    string flat = vm.DiskFlatName(i);
                    if (fileCopy && disk.VolumeResize)
                    {
                        // NAME#head: partition table + boot sectors; NAME: the disk without the resized
                        // volumes; NAME@OFFSET: the files of each resized volume
                        var offsets = new List<string>();
                        foreach (var p in disk.Parts)
                            if (p.TargetBytes > 0 && p.TargetBytes != p.Size)
                            {
                                offsets.Add(p.Offset.ToString(CultureInfo.InvariantCulture));
                                var fs = agent.FilesFor(session, disk.GuestDisk, p.Offset);
                                string label = (p.Label ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ').Trim();
                                urls.Append(flat).Append('@').Append(p.Offset.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(fs.Url)
                                    .Append('\t').Append(fs.ScriptSpec).Append('\t').Append(label.Length > 0 ? label : "-").Append('\n');
                            }
                        var hs = agent.StreamFor(session, disk.GuestDisk, "*");
                        urls.Append(flat).Append("#head\t").Append(hs.Url).Append('\t').Append(hs.ScriptSpec).Append('\n');
                        var ms = agent.StreamFor(session, disk.GuestDisk, string.Join(",", offsets.ToArray()));
                        urls.Append(flat).Append('\t').Append(ms.Url).Append('\t').Append(ms.ScriptSpec).Append('\n');
                        continue;
                    }
                    var cs = agent.StreamFor(session, disk.GuestDisk);
                    urls.Append(flat).Append('\t').Append(cs.Url).Append('\t').Append(cs.ScriptSpec).Append('\n');
                }
                files.Append("printf '%s' ").Append(SshClient.Q(urls.ToString())).Append(" > .agent-urls\n");
                ssh.Capture(files.ToString(), out code);
                if (code != 0) throw new Exception("Cannot write the VM description to " + dir + " on the Proxmox host: " + ssh.Explain(code));

                string script = "rc=0\n" + ToolPath + " import " + SshClient.Q(dir + "/vm.vmx") + ImportArgs(vm, o) +
                    " --esxi-urls " + SshClient.Q(dir + "/.agent-urls") + " < /dev/null 2>&1 || rc=$?\n" +
                    "rm -f " + SshClient.Q(dir + "/.agent-urls") + "\nexit $rc";
                Emit("  Proxmox copies the disks from the agent on " + agent.Host + ":" + agent.Port +
                     (fileCopy ? " (encrypted; resized volumes file by file, the rest used blocks only)..." : " (encrypted, used blocks only)..."), LogKind.Info);
                RunImport(vm, o, script, progress);
            }
            finally
            {
                try { agent.CloneFinish(session); Emit("  Snapshots deleted in the guest.", LogKind.Info); }
                catch (Exception ex) { Emit("  Could not delete the snapshots in the guest (the agent removes them within 48 h): " + ex.Message, LogKind.Warn); }
            }
            AfterLiveCopy(vm, o.ShutdownSource && !o.Template);
            if (o.Cleanup) RemoveDir(dir);
        }

        // Removes the temporary key line again; rewrites the file in place, because on
        // Proxmox VE /root/.ssh/authorized_keys is a symlink into the cluster file system.
        static string RemoveKeyScript(string tag)
        {
            return "f=/root/.ssh/authorized_keys\n[ -f \"$f\" ] || exit 0\nt=$(mktemp)\n" +
                   "grep -v -- " + SshClient.Q(tag) + " \"$f\" > \"$t\" || true\ncat \"$t\" > \"$f\"\nrm -f \"$t\"\n";
        }

        static string AddKeyScript(string line)
        {
            return "set -e\numask 077\nmkdir -p /root/.ssh\nchmod 700 /root/.ssh\n" +
                   "printf '%s\\n' " + SshClient.Q(line) + " >> /root/.ssh/authorized_keys\n" +
                   "command -v restorecon >/dev/null 2>&1 && restorecon -R /root/.ssh >/dev/null 2>&1 || true\n";
        }

        static bool OlderThan(string version, string want)
        {
            System.Version have, w;
            if (!System.Version.TryParse(version ?? "", out have)) return true;
            return System.Version.TryParse(want, out w) && have < w;
        }

        static string KeyTag() { return "mtaalam-v2v-temp-" + Guid.NewGuid().ToString("N").Substring(0, 12); }

        // A key pair for the Proxmox host to reach the source; returns the public key.
        string MakeHostKey(string dir, string tag, string type)
        {
            int code;
            string pub = ssh.Capture("umask 077\ncd " + SshClient.Q(dir) + " || exit 1\nrm -f .key .key.pub\n" +
                "ssh-keygen -q -t " + type + " -N '' -C " + SshClient.Q(tag) + " -f .key >/dev/null && cat .key.pub", out code).Trim();
            if (code != 0 || !pub.StartsWith("ssh-")) throw new Exception("Cannot make a temporary SSH key on the Proxmox host: " + ssh.Explain(code));
            return pub;
        }

        void DropHostKey(string dir)
        {
            int code;
            try { ssh.Reset(); ssh.Capture("rm -f " + SshClient.Q(dir + "/.key") + " " + SshClient.Q(dir + "/.key.pub"), out code); }
            catch (Exception) { }
        }

        void RemoveSourceKey(SshClient source, string host, string tag)
        {
            int code;
            try
            {
                source.Reset();
                source.Capture(RemoveKeyScript(tag), out code);
                if (code != 0) throw new Exception(source.Explain(code));
                Emit("  Temporary key removed from " + host + ".", LogKind.Info);
            }
            catch (Exception ex)
            {
                Emit("  Could not remove the temporary key from /root/.ssh/authorized_keys on " + host + " (delete the line ending in " + tag + "): " + ex.Message, LogKind.Warn);
            }
        }

        // Running Linux machine: the Proxmox host recreates its disks (partition tables, LVM,
        // file systems with the same UUIDs) and copies the files with rsync over SSH, using
        // a temporary root key on the machine. The machine keeps running.
        void ConvertFromLinux(VmxInfo vm, ConvertOptions o, int index, int count, Action<string, double> progress)
        {
            var src = vm.Linux;
            Emit(string.Format("[{0}/{1}] {2}  (live copy of the running Linux machine {3})", index + 1, count, vm.DisplayName, src.Target.Host), LogKind.Step);
            string storage = vm.TargetStorage ?? o.Storage, stype;
            o.StorageTypes.TryGetValue(storage, out stype);
            if (Array.IndexOf(StreamableTypes, stype) < 0)
                throw new Exception("Storage '" + storage + "' (" + stype + ") cannot receive a live copy. Pick an LVM-thin, ZFS, LVM or directory storage. Nothing was changed.");
            string dir = o.RemoteBase.TrimEnd('/') + "/" + vm.SafeName;
            long free;
            PrepareDir(dir, out free);
            int code;
            var files = new StringBuilder("umask 077\ncd " + SshClient.Q(dir) + " || exit 1\n");
            files.Append("printf '%s' ").Append(SshClient.Q(vm.SyntheticVmx(o.NewMac))).Append(" > vm.vmx\n");
            for (int i = 0; i < vm.Disks.Count; i++)
                files.Append("printf '%s' ").Append(SshClient.Q(vm.DiskDescriptor(i))).Append(" > ").Append(SshClient.Q(vm.DiskFileName(i))).Append('\n');
            files.Append("printf '%s' ").Append(SshClient.Q(src.Spec)).Append(" > linux.spec\n");
            ssh.Capture(files.ToString(), out code);
            if (code != 0) throw new Exception("Cannot write the machine description to " + dir + " on the Proxmox host: " + ssh.Explain(code));

            string tag = KeyTag();
            string pub = MakeHostKey(dir, tag, "rsa -b 3072");   // RSA: also accepted by old OpenSSH on the source
            var source = src.Client();
            source.Capture(AddKeyScript("no-port-forwarding,no-agent-forwarding,no-X11-forwarding,no-pty " + pub), out code);
            if (code != 0) { DropHostKey(dir); throw new Exception("Cannot add the temporary key for root on " + src.Target.Host + ": " + source.Explain(code)); }
            Emit("  Temporary SSH key added for root on " + src.Target.Host + " (removed after the copy).", LogKind.Info);
            try
            {
                string script = ToolPath + " import " + SshClient.Q(dir + "/vm.vmx") + ImportArgs(vm, o) +
                    " --linux-spec " + SshClient.Q(dir + "/linux.spec") + " --linux-key " + SshClient.Q(dir + "/.key") +
                    " --linux-from " + SshClient.Q("root@" + src.Target.Host) + " --linux-port " + src.Target.Port + " < /dev/null 2>&1";
                Emit("  Proxmox recreates the disks and copies the files from " + src.Target.Host + " (rsync over SSH). The machine keeps running.", LogKind.Info);
                RunImport(vm, o, script, progress);
            }
            catch (Exception)
            {
                if (!cancel && ssh.LastError != null && ssh.LastError.Contains("cannot log in to"))
                    Emit("  The Proxmox host must reach " + src.Target.Host + " on SSH port " + src.Target.Port + ", and its sshd must allow root to log in " +
                         "with a key (PermitRootLogin prohibit-password or yes).", LogKind.Warn);
                throw;
            }
            finally
            {
                RemoveSourceKey(source, src.Target.Host, tag);
                DropHostKey(dir);
            }
            AfterLiveCopy(vm, o.ShutdownSource && !o.Template);
            if (o.Cleanup) RemoveDir(dir);
        }

        // VM on another Proxmox VE host: vzdump streams it (snapshot mode, it may keep
        // running) straight to this host, where qmrestore writes it. The temporary key may
        // only run that one vzdump command.
        void ConvertFromProxmox(VmxInfo vm, ConvertOptions o, int index, int count, Action<string, double> progress)
        {
            var pve = vm.Pve;
            Emit(string.Format("[{0}/{1}] {2}  (VM {3} on Proxmox VE {4}{5})", index + 1, count, vm.DisplayName, pve.Vmid, pve.Host.Host,
                pve.Running ? ", running - copied from a live backup snapshot" : ""), LogKind.Step);
            var source = new SshClient(pve.Host);
            int code;
            string srcTools = source.Capture("command -v vzdump >/dev/null || echo novzdump; command -v zstd >/dev/null && echo zstd; true", out code);
            if (code != 0) throw new Exception("Cannot connect to " + pve.Host.Host + ": " + source.Explain(code));
            if (srcTools.Contains("novzdump")) throw new Exception(pve.Host.Host + " has no vzdump - is it a Proxmox VE host?");
            string here = ssh.Capture("command -v qmrestore >/dev/null || echo noqmrestore; command -v zstd >/dev/null && echo zstd; true", out code);
            if (here.Contains("noqmrestore")) throw new Exception("qmrestore is missing on the destination Proxmox host.");
            bool zstd = srcTools.Contains("zstd") && here.Contains("zstd");
            string dir = o.RemoteBase.TrimEnd('/') + "/" + vm.SafeName;
            long free;
            PrepareDir(dir, out free);

            string tag = KeyTag();
            string pub = MakeHostKey(dir, tag, "ed25519");
            string command = "vzdump " + pve.Vmid + " --stdout --mode snapshot --compress " + (zstd ? "zstd" : "0");
            source.Capture(AddKeyScript("command=\"" + command + "\",no-port-forwarding,no-agent-forwarding,no-X11-forwarding,no-pty " + pub), out code);
            if (code != 0) { DropHostKey(dir); throw new Exception("Cannot add the temporary key on " + pve.Host.Host + ": " + source.Explain(code)); }
            try
            {
                var cmd = new StringBuilder(ToolPath + " restore");
                cmd.Append(" --from ").Append(SshClient.Q("root@" + pve.Host.Host)).Append(" --port ").Append(pve.Host.Port);
                cmd.Append(" --key ").Append(SshClient.Q(dir + "/.key")).Append(" --source-vmid ").Append(pve.Vmid);
                cmd.Append(" --total ").Append(vm.VirtualBytes);
                if (zstd) cmd.Append(" --zstd");
                cmd.Append(" --storage ").Append(SshClient.Q(vm.TargetStorage ?? o.Storage));
                cmd.Append(" --bridge ").Append(SshClient.Q(o.Bridge));
                if (!string.IsNullOrEmpty(vm.TargetName)) cmd.Append(" --name ").Append(SshClient.Q(vm.TargetName));
                if (vm.TargetVmid > 0) cmd.Append(" --vmid ").Append(vm.TargetVmid);
                if (vm.TargetCores > 0) cmd.Append(" --cores ").Append(vm.TargetCores);
                if (vm.TargetMemoryMb > 0) cmd.Append(" --memory ").Append(vm.TargetMemoryMb);
                if (o.NewMac) cmd.Append(" --new-mac");
                if (o.Template) cmd.Append(" --template");
                else if (o.Start) cmd.Append(" --start");
                cmd.Append(" < /dev/null 2>&1");
                Emit("  The destination host pulls the VM from " + pve.Host.Host + " (vzdump stream" + (zstd ? ", zstd compressed" : "") + "); nothing passes through this PC.", LogKind.Info);
                RunImport(vm, o, cmd.ToString(), progress);
            }
            finally
            {
                RemoveSourceKey(source, pve.Host.Host, tag);
                DropHostKey(dir);
            }
            if (pve.Running && !o.Template)
                AfterLiveCopy(vm, o.ShutdownSource);
            if (o.Cleanup) RemoveDir(dir);
        }

        // After a live copy the source still runs with the same IP and name: shut it
        // down if asked, so the copy can take over.
        public static string AfterLiveCopyText(VmxInfo vm, bool shutdown)
        {
            return shutdown
                ? "  Shutting down the source machine " + vm.DisplayName + " (its copy takes over its name and IP address)."
                : "  The source machine " + vm.DisplayName + " is still running. Shut it down before you start the new VM - both use the same name and IP address.";
        }

        public static void ShutdownSource(VmxInfo vm)
        {
            int code;
            if (vm.Agent != null) vm.Agent.Shutdown();
            else if (vm.Linux != null)
                vm.Linux.Client().Capture("setsid sh -c 'sleep 2; shutdown -h now || poweroff' </dev/null >/dev/null 2>&1 &", out code);
            else if (vm.Pve != null)
                new SshClient(vm.Pve.Host).Capture("setsid qm shutdown " + vm.Pve.Vmid + " --timeout 600 --forceStop 1 </dev/null >/dev/null 2>&1 &", out code);
        }

        void AfterLiveCopy(VmxInfo vm, bool shutdown)
        {
            Emit(AfterLiveCopyText(vm, shutdown), shutdown ? LogKind.Info : LogKind.Warn);
            if (!shutdown) return;
            try { ShutdownSource(vm); }
            catch (Exception ex) { Emit("  Could not shut down the source machine: " + ex.Message, LogKind.Warn); }
        }

        class DownloadPlan
        {
            public string Dir;
            public string VmxName;
            public readonly List<EsxiFile> Fetch = new List<EsxiFile>();    // copied into Dir
            public readonly List<EsxiFile> Stream = new List<EsxiFile>();   // flat extents streamed into the target disks
            public Dictionary<string, long> Existing;
        }

        static readonly string[] StreamableTypes = { "dir", "nfs", "cifs", "lvm", "lvmthin", "zfspool" };

        // Proxmox before 8.2 has no ESXi import storage. Disks without snapshots are
        // streamed from the ESXi datastore (HTTPS) straight into their Proxmox volumes;
        // only the .vmx and the small .vmdk descriptors go to the temp folder. Disks
        // with snapshots are copied to the temp folder whole, then converted.
        // Everything here is checked before the VM is shut down on ESXi.
        DownloadPlan PlanDownload(VmxInfo vm, ConvertOptions o)
        {
            var current = o.Esxi.Refresh(vm.Esxi.Moref, true);
            var files = new List<EsxiFile>();
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string vmxName = null;
            foreach (var f in current.Files)
            {
                if (!f.IsDisk && f.Type != "config") continue;
                string name = EsxiBaseName(f.Name);
                string other;
                if (names.TryGetValue(name, out other) && other != f.Name)
                    throw new Exception("Two VM files are both named " + name + " (" + other + ", " + f.Name + ") - consolidate or move them on ESXi");
                if (other != null) continue;
                names[name] = f.Name;
                files.Add(f);
                if (f.Type == "config") vmxName = name;
            }
            if (vmxName == null || files.Count < 2)
                throw new Exception("ESXi did not report the VM's configuration and disk files");

            bool snapshots = false;
            foreach (var f in files)
                if (f.Type == "diskDescriptor" && Regex.IsMatch(f.Name, "-\\d{6}\\.vmdk$", RegexOptions.IgnoreCase)) snapshots = true;
            string storage = vm.TargetStorage ?? o.Storage;
            string stype;
            o.StorageTypes.TryGetValue(storage, out stype);
            bool stream = !snapshots && Array.IndexOf(StreamableTypes, stype) >= 0;

            var plan = new DownloadPlan { Dir = o.RemoteBase.TrimEnd('/') + "/" + vm.SafeName, VmxName = vmxName };
            foreach (var f in files)
                (stream && f.Type == "diskExtent" ? plan.Stream : plan.Fetch).Add(f);

            int code;
            ssh.Capture("command -v curl >/dev/null", out code);
            if (code != 0) throw new Exception("curl is missing on the Proxmox host - install it there with: apt install curl");

            long free;
            plan.Existing = PrepareDir(plan.Dir, out free);
            long needed = 0;
            foreach (var f in plan.Fetch)
                if (f.Type == "config" || !plan.Existing.ContainsKey(EsxiBaseName(f.Name))) needed += f.Size;
            if (needed > free)
            {
                string hint = "";
                foreach (var t in o.TempDirs)
                    if (t.FreeBytes > needed + (1L << 30) && !plan.Dir.StartsWith(t.Path.TrimEnd('/') + "/"))
                    {
                        hint = " Set the temp folder to " + t.Path.TrimEnd('/') + "/mtaalam-v2v (" + Theme.Bytes(t.FreeBytes) + " free).";
                        break;
                    }
                string why = snapshots
                    ? "The VM has snapshots, so its disks must be copied whole before converting. Delete or consolidate " +
                      "its snapshots on ESXi (Snapshots > Delete all / Consolidate) and the disks are streamed straight into Proxmox with no temp space."
                    : "Storage '" + storage + "' (" + stype + ") cannot receive a stream, so the disks are copied first.";
                throw new Exception(string.Format("Not enough space in the temp folder {0}: needs {1}, {2} free. {3}{4} The VM was not touched.",
                    o.RemoteBase, Theme.Bytes(needed), Theme.Bytes(free), why, hint));
            }
            Emit(stream
                ? "  Disks will be streamed from ESXi straight into '" + storage + "' (no temp copy needed)."
                : "  Disks will be copied from ESXi into " + plan.Dir + " (" + Theme.Bytes(needed) + "), then converted" +
                  (snapshots ? " - the VM has snapshots." : "."), LogKind.Info);
            return plan;
        }

        void DownloadFromEsxi(VmxInfo vm, ConvertOptions o, DownloadPlan plan, Action<string, double> progress)
        {
            string dir = plan.Dir;
            var existing = plan.Existing;
            double share = plan.Stream.Count > 0 ? 0.02 : 0.5;   // part of the bar for the copy into the temp folder
            long total = 0;
            foreach (var f in plan.Fetch) total += f.Size;

            string auth = "user = \"" + (o.Esxi.User + ":" + o.Esxi.Password).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
            long doneBefore = 0;
            foreach (var f in plan.Fetch)
            {
                if (cancel) throw new OperationCanceledException();
                string name = EsxiBaseName(f.Name);
                if (f.Type != "config" && existing.ContainsKey(name))
                {
                    Emit("  " + name + " already downloaded - skipping", LogKind.Info);
                    doneBefore += f.Size;
                    continue;
                }
                Emit("  Downloading " + name + " (" + Theme.Bytes(f.Size) + " on the datastore)", LogKind.Info);
                vm.StatusText = "Downloading " + name;
                Changed(vm);

                string script =
                    "set -o pipefail\n" +
                    "cd " + SshClient.Q(dir) + " || exit 1\n" +
                    "f=" + SshClient.Q(name) + "\n" +
                    "url=" + SshClient.Q(o.Esxi.FileUrl(f.Name)) + "\n" +
                    "auth() { printf '%s\\n' " + SshClient.Q(auth) + "; }\n" +
                    "command -v curl >/dev/null || { echo 'error: curl is not installed on the Proxmox host (apt install curl)'; exit 1; }\n" +
                    "len=$(curl -skI -K <(auth) \"$url\" </dev/null | tr -d '\\r' | awk 'tolower($1)==\"content-length:\"{n=$2} END{print n+0}')\n" +
                    "echo \"##LEN $len\"\n" +
                    "bs=4194304\n" +
                    "trap 'for p in $(jobs -p); do pkill -P $p; kill $p; done 2>/dev/null; exit 1' PIPE HUP TERM INT\n" +
                    // $1 parallel HTTP range requests, each written at its offset; progress = bytes read by the dd's
                    "fetch() {\n" +
                    "  local segs=$1 per i a b total alive d n rc=0; local -a pids=() sizes=()\n" +
                    "  rm -f \"$f.part\"; : > \"$f.part\"\n" +
                    "  per=$(( (len / segs / bs + 1) * bs ))\n" +
                    "  for (( i = 0; i < segs; i++ )); do\n" +
                    "    a=$(( i * per )); (( a < len || i == 0 )) || break\n" +
                    "    b=$(( a + per - 1 )); (( b < len )) || b=$(( len - 1 ))\n" +
                    "    if (( segs == 1 )); then\n" +
                    "      { curl -sSk --fail -K <(auth) \"$url\" </dev/null | dd of=\"$f.part\" bs=$bs iflag=fullblock conv=sparse,notrunc status=none; } &\n" +
                    "    else\n" +
                    "      { curl -sSk --fail -K <(auth) -r \"$a-$b\" \"$url\" </dev/null | dd of=\"$f.part\" bs=$bs seek=$(( a / bs )) iflag=fullblock conv=sparse,notrunc status=none; } &\n" +
                    "    fi\n" +
                    "    pids+=($!); sizes+=($(( b - a + 1 )))\n" +
                    "  done\n" +
                    "  while :; do\n" +
                    "    alive=0; total=0\n" +
                    "    for i in \"${!pids[@]}\"; do\n" +
                    "      if kill -0 \"${pids[i]}\" 2>/dev/null; then\n" +
                    "        alive=1; d=${pids[i]}\n" +
                    "        [ \"$(cat /proc/$d/comm 2>/dev/null)\" = dd ] || d=$(pgrep -P \"$d\" -x dd | head -n1)\n" +
                    "        n=$(awk '/^rchar:/ { print $2 }' \"/proc/$d/io\" 2>/dev/null); total=$(( total + ${n:-0} ))\n" +
                    "      else total=$(( total + sizes[i] )); fi\n" +
                    "    done\n" +
                    "    echo \"##DL $total\"\n" +
                    "    (( alive )) || break\n" +
                    "    sleep 2\n" +
                    "  done\n" +
                    "  for p in \"${pids[@]}\"; do wait \"$p\" || rc=1; done\n" +
                    "  if (( rc == 0 && len > 0 )); then truncate -s \"$len\" \"$f.part\"; fi\n" +
                    "  return $rc\n" +
                    "}\n" +
                    // ESXi serves one HTTP stream slowly; several range requests in parallel are much faster
                    "if (( len >= 2147483648 )) && [ \"$(curl -sk -K <(auth) -r 0-0 -o /dev/null -w '%{http_code}' \"$url\" </dev/null)\" = 206 ]; then\n" +
                    "  echo \"downloading in 4 parallel parts\"\n" +
                    "  fetch 4 || { echo \"parallel download failed - retrying as one stream\"; fetch 1; } || { rm -f \"$f.part\"; echo \"error: download of $f from ESXi failed\"; exit 1; }\n" +
                    "else\n" +
                    "  fetch 1 || { rm -f \"$f.part\"; echo \"error: download of $f from ESXi failed\"; exit 1; }\n" +
                    "fi\n" +
                    "mv -f \"$f.part\" \"$f\"\n" +
                    // snapshot descriptors can name their parent with a full /vmfs path
                    "if [ " + (f.Type == "diskDescriptor" ? "1" : "0") + " = 1 ] && [ $(stat -c %s \"$f\") -lt 1048576 ]; then\n" +
                    "  sed -i -E 's#^(parentFileNameHint=\")[^\"]*/([^\"/]*\")#\\1\\2#' \"$f\"\n" +
                    "fi\n";

                long length = f.Size;
                long baseDone = doneBefore;
                var clock = Stopwatch.StartNew();
                int code = ssh.Run(script, line =>
                {
                    var m = DownloadLine.Match(line);
                    if (!m.Success) { Emit("  " + line, line.StartsWith("error") ? LogKind.Error : LogKind.Remote); return; }
                    long n = long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                    if (m.Groups[1].Value == "LEN") { if (n > 0) length = n; return; }
                    Bytes(n);
                    double part = length > 0 ? Math.Min(1.0, (double)n / length) : 0;
                    double secs = Math.Max(clock.Elapsed.TotalSeconds, 0.001);
                    progress(string.Format("Downloading {0} from ESXi: {1} / {2}  ({3}/s)",
                        name, Theme.Bytes(n), Theme.Bytes(length), Theme.Bytes((long)(n / secs))),
                        total == 0 ? 0 : share * (baseDone + f.Size * part) / total);
                });
                if (code != 0) throw new Exception("Download of " + name + " from ESXi failed (see log).");
                doneBefore += f.Size;
            }

            string import = ToolPath + " import " + SshClient.Q(dir + "/" + plan.VmxName) + ImportArgs(vm, o);
            if (plan.Stream.Count > 0)
            {
                // URL list + credentials (owner-only, deleted right after the import).
                var urls = new StringBuilder();
                foreach (var f in plan.Stream) urls.Append(EsxiBaseName(f.Name)).Append('\t').Append(o.Esxi.FileUrl(f.Name)).Append('\n');
                string urlFile = dir + "/.esxi-urls", authFile = dir + "/.esxi-auth";
                import = "umask 077\n" +
                    "printf '%s' " + SshClient.Q(urls.ToString()) + " > " + SshClient.Q(urlFile) + "\n" +
                    "printf '%s\\n' " + SshClient.Q(auth) + " > " + SshClient.Q(authFile) + "\n" +
                    import + " --esxi-urls " + SshClient.Q(urlFile) + " --esxi-auth " + SshClient.Q(authFile) + " < /dev/null 2>&1\n" +
                    "rc=$?\nrm -f " + SshClient.Q(authFile) + "\nexit $rc";
            }
            else import += " < /dev/null 2>&1";
            RunImport(vm, o, import, (label, f) => progress(label, share + (1 - share) * f));
            if (o.Cleanup) RemoveDir(dir);
        }

        static readonly Regex DownloadLine = new Regex("^##(LEN|DL) (\\d+)");

        // "[ds] folder/file.vmdk" -> "file.vmdk"
        static string EsxiBaseName(string datastorePath)
        {
            string s = datastorePath;
            int bracket = s.IndexOf(']');
            if (s.StartsWith("[") && bracket > 0) s = s.Substring(bracket + 1).Trim();
            return s.Substring(s.LastIndexOf('/') + 1);
        }

        static readonly Regex ByteCount = new Regex("transferred (\\d+) of (\\d+) bytes");
        static readonly Regex Received = new Regex("received (\\d+)");

        void RunImport(VmxInfo vm, ConvertOptions o, string script, Action<string, double> progress)
        {
            vm.StatusText = vm.Linux != null ? "Copying the files" : vm.Pve != null ? "Copying from Proxmox" : "Converting disks";
            Changed(vm);
            int disk = 0, vmid = 0, created = 0, parts = Math.Max(vm.DiskCount, 1);
            string createdName = "";
            double lastPct = -1;
            string what = "Converting disk";
            bool local = false;            // resized disk: staging file -> Proxmox volume (no network)
            int resized = 0;
            var notResized = new List<string>();
            int lines = 0;
            int code = ssh.Run(script, line =>
            {
                Interlocked.Increment(ref lines);
                var cm = Created.Match(line);
                if (cm.Success)
                {
                    // recorded right away: a cancelled or disconnected run never reaches the end
                    created = int.Parse(cm.Groups[1].Value); createdName = cm.Groups[2].Value;
                    vm.PartialVmid = created; vm.PartialName = createdName;
                }
                if (line.StartsWith("==> importing")) { disk++; lastPct = -1; counter.Reset(); local = false; }
                if (line.StartsWith("==> writing the smaller disk")) { lastPct = -1; local = true; }
                if (line.StartsWith("==> ") && line.Contains("volumes resized")) resized++;
                if (line.StartsWith("warning: ") && line.Contains("NOT resized")) notResized.Add(line.Substring(9));
                if (line.StartsWith("==> copying the files") || line.StartsWith("==> copying VM"))
                {
                    disk = 1; parts = 1; lastPct = -1; counter.Reset();   // one copy step for the whole machine
                    what = vm.Linux != null ? "Copying files" : "Copying";
                }
                var m = Percent.Match(line);
                if (!(m.Success && line.Contains("transferred"))) m = QemuPercent.Match(line);
                if (m.Success && (line.Contains("transferred") || line.Contains("/100%)")))
                {
                    double pct = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    // bytes for the speed display: what really travelled, else the share of the disk
                    var r = Received.Match(line);
                    var b = ByteCount.Match(line);
                    int di = Math.Max(disk, 1) - 1;
                    long size = di < vm.Disks.Count ? vm.Disks[di].FinalBytes : 0;
                    if (local) { }
                    else if (r.Success) Bytes(long.Parse(r.Groups[1].Value, CultureInfo.InvariantCulture));
                    else if (b.Success) Bytes(long.Parse(b.Groups[1].Value, CultureInfo.InvariantCulture));
                    else if (size > 0) Bytes((long)(size * pct / 100));
                    if (Math.Abs(pct - lastPct) < 0.5 && pct < 100) return;
                    lastPct = pct;
                    double f = Math.Min(1.0, (Math.Max(disk, 1) - 1 + pct / 100) / parts);
                    progress(local
                        ? string.Format("Disk {0} of {1}: making the disk smaller {2:0.0}%", Math.Max(disk, 1), parts, pct)
                        : parts == 1 && what != "Converting disk"
                        ? string.Format("{0}: {1:0.0}%", what, pct)
                        : string.Format("Converting disk {0} of {1}: {2:0.0}%", Math.Max(disk, 1), parts, pct), f);
                    return;
                }
                m = Imported.Match(line);
                if (m.Success) vmid = int.Parse(m.Groups[1].Value);
                LogKind kind = line.StartsWith("error") ? LogKind.Error
                             : line.StartsWith("warning") ? LogKind.Warn
                             : line.StartsWith("==>") ? LogKind.Ok : LogKind.Remote;
                Emit("  " + line, kind);
            });
            if (code != 0 || vmid == 0)
            {
                if (created > 0) { vm.PartialVmid = created; vm.PartialName = createdName; }
                if (lines == 0)
                    throw new Exception("The import script on the Proxmox host ended without any output (exit code " + code + ")" +
                        (string.IsNullOrEmpty(ssh.LastError) ? "" : ": " + ssh.LastError) +
                        ". On the host, run:  bash " + ToolPath + " import /nonexistent.vmx --vmid 999  - it should only say \"no such file\".");
                throw new Exception("Import failed on the Proxmox host (see log).");
            }

            vm.PartialVmid = 0;
            vm.Vmid = vmid;
            vm.State = VmState.Done;
            bool virtio = o.VirtioReady || vm.VirtioOnCopy;
            vm.StatusText = vm.IsWindows && !virtio
                ? "Imported as VM " + vmid + " - install VirtIO drivers, then Finalize"
                : "Imported as VM " + vmid;
            vm.NeedsFinalize = vm.IsWindows && !virtio;
            int wanted = 0;
            foreach (var d in vm.Disks) if (vm.HotClone && d.VolumeResize) wanted++;
            if (wanted > 0 && (notResized.Count > 0 || resized < wanted))
            {
                string why = notResized.Count > 0 ? string.Join(" / ", notResized) : "the Proxmox host did not report a resize";
                if (vm.ShrinkOnBoot)
                {
                    vm.NeedsFinalize = true;
                    vm.StatusText = "Imported as VM " + vmid + " - start it once (Windows shrinks the volumes), shut down, then Finalize";
                    Emit("  The Proxmox host could not resize the volumes of " + vm.DisplayName + " (" + why + "). The copy is complete at its " +
                         "original size. Its agent shrinks the volumes with Windows itself at the first start (this can take a while; see " +
                         "C:\\Windows\\MtaalamV2V\\agent.log in the VM). Then shut the VM down and click \"Finalize Windows VM\": " +
                         "it cuts the disk behind the last partition, so one smaller disk remains.", LogKind.Warn);
                }
                else
                {
                    vm.StatusText = "Imported as VM " + vmid + " - volumes NOT resized (see log)";
                    Emit("  The volumes of " + vm.DisplayName + " were not resized: " + why + ". The VM is complete with its original disk size.", LogKind.Error);
                }
            }
            if (vm.IsWindows && !virtio)
                Emit("  VM " + vmid + " starts on SATA + e1000e with a temporary empty 1 GB VirtIO disk (scsi30). It only lets Windows " +
                     "install the VirtIO SCSI driver: run virtio-win-guest-tools.exe from the attached CD inside Windows, shut down, " +
                     "then \"Finalize Windows VM\" moves the system disk to VirtIO and removes the 1 GB disk.", LogKind.Info);
            Changed(vm);
            progress("Imported " + vm.DisplayName + " as VM " + vmid, 1);
        }

        public int Finalize(int vmid, bool shrinkDisks, Action<string> onLine)
        {
            ssh.Reset();
            return ssh.Run(ToolPath + " finalize " + vmid + (shrinkDisks ? " --shrink-disks" : "") + " < /dev/null 2>&1", onLine);
        }
    }
}
