using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;

namespace Mtaalam.V2V
{
    class EsxiVm
    {
        public string Moref;
        public string Name;
        public string GuestId;
        public string GuestName;
        public string VmxPath;          // "[datastore] folder/vm.vmx"
        public string PowerState;       // poweredOn / poweredOff / suspended
        public string ToolsStatus;      // guestToolsRunning / guestToolsNotRunning
        public bool Uefi;
        public bool Tpm;
        public bool Template;
        public int Cpus;
        public int MemoryMb;
        public long CapacityBytes;
        public long CommittedBytes;
        public readonly List<string> DiskFiles = new List<string>();
        public readonly List<EsxiFile> Files = new List<EsxiFile>();   // layoutEx.file
        public readonly List<KeyValuePair<string, long>> Disks = new List<KeyValuePair<string, long>>();   // slot -> bytes
        public readonly List<string> Macs = new List<string>();

        // Live figures from VMware Tools (-1 = not reported).
        public long CpuUsageMhz = -1, MaxCpuMhz = -1, GuestMemoryMb = -1, GuestDiskUsed = -1;

        public GuestUsage ToolsUsage()
        {
            if (PowerState != "poweredOn" || ToolsStatus != "guestToolsRunning") return null;
            var u = new GuestUsage { Source = "VMware Tools (load right now)", DiskUsedBytes = GuestDiskUsed };
            if (CpuUsageMhz >= 0 && MaxCpuMhz > 0) u.CpuPeakPct = u.CpuAvgPct = 100.0 * CpuUsageMhz / MaxCpuMhz;
            if (GuestMemoryMb >= 0) u.MemPeakUsedMb = GuestMemoryMb;
            return u;
        }

        // Bytes the disks occupy on the datastore (descriptors, extents, snapshot deltas).
        public long DiskUsedBytes
        {
            get
            {
                long sum = 0;
                foreach (var f in Files) if (f.IsDisk) sum += f.Size;
                return sum;
            }
        }
    }

    class EsxiDatastore
    {
        public string Moref, Name, Type;
        public long FreeBytes, CapacityBytes;
        public override string ToString() { return Name + "   (" + Type + ", " + Theme.Bytes(FreeBytes) + " free)"; }
    }

    class EsxiNetwork
    {
        public string Moref, Name, Type;
        public override string ToString() { return Name; }
    }

    class EsxiInventory
    {
        public string Host, ResourcePool, VmFolder;
        public readonly List<EsxiDatastore> Datastores = new List<EsxiDatastore>();
        public readonly List<EsxiNetwork> Networks = new List<EsxiNetwork>();
    }

    class EsxiFile
    {
        public string Name;     // "[datastore] folder/file.vmdk"
        public string Type;     // config, diskDescriptor, diskExtent, nvram, log, ...
        public long Size;

        public bool IsDisk { get { return Type == "diskDescriptor" || Type == "diskExtent"; } }
    }

    // Minimal vSphere SOAP client: inventory and power operations on an ESXi host.
    class EsxiClient
    {
        const string Vim = "urn:vim25";
        static readonly string[] VmProps =
        {
            "name", "config.template", "config.guestId", "config.guestFullName", "config.firmware",
            "config.hardware.numCPU", "config.hardware.memoryMB", "config.hardware.device",
            "summary.config.vmPathName", "summary.storage.committed", "runtime.powerState", "guest.toolsRunningStatus",
            "layoutEx.file", "summary.quickStats.overallCpuUsage", "summary.quickStats.guestMemoryUsage",
            "runtime.maxCpuUsage", "guest.disk",
        };

        readonly string host;
        readonly string user;
        readonly string password;
        CookieContainer cookies;
        string propertyCollector, viewManager, rootFolder, sessionManager, ovfManager;

        public string Host { get { return host; } }
        public string User { get { return user; } }
        public string Password { get { return password; } }
        public string ProductName { get; private set; }
        public bool IsVCenter { get; private set; }

        public EsxiClient(string host, string user, string password)
        {
            this.host = host;
            this.user = user;
            this.password = password;
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }

        public void Login()
        {
            cookies = new CookieContainer();
            var sc = Return(Call("<RetrieveServiceContent xmlns=\"urn:vim25\"><_this type=\"ServiceInstance\">ServiceInstance</_this></RetrieveServiceContent>"));
            rootFolder = Text(sc, "rootFolder");
            propertyCollector = Text(sc, "propertyCollector");
            viewManager = Text(sc, "viewManager");
            sessionManager = Text(sc, "sessionManager");
            ovfManager = Text(sc, "ovfManager");
            var about = Child(sc, "about");
            ProductName = about != null ? Text(about, "fullName") : "VMware";
            IsVCenter = about != null && Text(about, "apiType") == "VirtualCenter";

            Call("<Login xmlns=\"urn:vim25\"><_this type=\"SessionManager\">" + Esc(sessionManager) + "</_this>" +
                 "<userName>" + Esc(user) + "</userName><password>" + Esc(password) + "</password></Login>");
        }

        public List<EsxiVm> ListVms()
        {
            string view = Return(Call(
                "<CreateContainerView xmlns=\"urn:vim25\"><_this type=\"ViewManager\">" + Esc(viewManager) + "</_this>" +
                "<container type=\"Folder\">" + Esc(rootFolder) + "</container><type>VirtualMachine</type><recursive>true</recursive></CreateContainerView>")).InnerText;

            var spec = new StringBuilder("<specSet><propSet><type>VirtualMachine</type>");
            foreach (string p in VmProps) spec.Append("<pathSet>").Append(p).Append("</pathSet>");
            spec.Append("</propSet><objectSet><obj type=\"ContainerView\">").Append(Esc(view)).Append("</obj><skip>true</skip>")
                .Append("<selectSet xsi:type=\"TraversalSpec\"><name>view</name><type>ContainerView</type><path>view</path><skip>false</skip></selectSet>")
                .Append("</objectSet></specSet>");

            var vms = new List<EsxiVm>();
            var result = Return(Call(RetrieveProps(spec.ToString())));
            while (result != null)
            {
                foreach (var obj in Kids(result, "objects")) vms.Add(ParseVm(obj));
                string token = Text(result, "token");
                if (token.Length == 0) break;
                result = Return(Call("<ContinueRetrievePropertiesEx xmlns=\"urn:vim25\"><_this type=\"PropertyCollector\">" +
                    Esc(propertyCollector) + "</_this><token>" + Esc(token) + "</token></ContinueRetrievePropertiesEx>"));
            }
            try
            {
                Call("<DestroyView xmlns=\"urn:vim25\"><_this type=\"ContainerView\">" + Esc(view) + "</_this></DestroyView>");
            }
            catch (Exception) { }
            vms.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return vms;
        }

        public EsxiVm Refresh(string moref, bool withFiles = false)
        {
            string spec = "<specSet><propSet><type>VirtualMachine</type><pathSet>runtime.powerState</pathSet>" +
                          "<pathSet>guest.toolsRunningStatus</pathSet>" + (withFiles ? "<pathSet>layoutEx.file</pathSet>" : "") +
                          "</propSet><objectSet><obj type=\"VirtualMachine\">" +
                          Esc(moref) + "</obj><skip>false</skip></objectSet></specSet>";
            var result = Return(Call(RetrieveProps(spec)));
            foreach (var obj in Kids(result, "objects")) return ParseVm(obj);
            throw new Exception("VM " + moref + " no longer exists on the ESXi host");
        }

        // Guest OS shutdown through VMware Tools (or the Mtaalam V2V agent in the guest:
        // agentShutdown returns null once it has asked the guest to shut down, else
        // why it could not), falling back to power off.
        public void ShutDown(string moref, TimeSpan graceful, Action<string> log, Func<bool> cancelled, Func<string> agentShutdown)
        {
            Login(); // long runs outlive the 30-minute ESXi session timeout
            var vm = Refresh(moref);
            if (vm.PowerState == "poweredOff") return;
            if (vm.PowerState == "suspended")
                throw new Exception("VM is suspended on ESXi - resume it and shut it down first");

            string agentError = null;
            if (vm.ToolsStatus == "guestToolsRunning")
            {
                log("Shutting down the guest OS on ESXi (VMware Tools)...");
                Call("<ShutdownGuest xmlns=\"urn:vim25\"><_this type=\"VirtualMachine\">" + Esc(moref) + "</_this></ShutdownGuest>");
                if (WaitOff(moref, graceful, cancelled)) return;
                log("Guest did not shut down within " + (int)graceful.TotalMinutes + " minutes - powering off.");
            }
            else if (agentShutdown != null && (agentError = agentShutdown()) == null)
            {
                log("Shutting down the guest OS through the GeuzaVirt agent...");
                if (WaitOff(moref, graceful, cancelled)) return;
                log("Guest did not shut down within " + (int)graceful.TotalMinutes + " minutes - powering off.");
            }
            else
            {
                log("VMware Tools not running" + (agentError != null ? " and the agent did not answer (" + agentError + ")" : "") +
                    " - powering the VM off on ESXi.");
            }
            Call("<PowerOffVM_Task xmlns=\"urn:vim25\"><_this type=\"VirtualMachine\">" + Esc(moref) + "</_this></PowerOffVM_Task>");
            if (!WaitOff(moref, TimeSpan.FromMinutes(2), cancelled))
                throw new Exception("VM did not power off on ESXi");
        }

        bool WaitOff(string moref, TimeSpan timeout, Func<bool> cancelled)
        {
            var until = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < until)
            {
                if (cancelled()) throw new OperationCanceledException();
                if (Refresh(moref).PowerState == "poweredOff") return true;
                Thread.Sleep(3000);
            }
            return false;
        }

        // HTTPS download URL of a datastore file ("[ds] dir/file") on this host.
        public string FileUrl(string datastorePath)
        {
            var m = Regex.Match(datastorePath, "^\\[([^\\]]+)\\]\\s*(.+)$");
            if (!m.Success) throw new Exception("Not a datastore path: " + datastorePath);
            var path = new StringBuilder();
            foreach (string part in m.Groups[2].Value.Trim().Split('/'))
            {
                if (path.Length > 0) path.Append('/');
                path.Append(Uri.EscapeDataString(part));
            }
            return "https://" + host + "/folder/" + path + "?dcPath=ha-datacenter&dsName=" + Uri.EscapeDataString(m.Groups[1].Value);
        }

        // ------------------------------------------------- target side (OVF import / export)

        // Datastores and networks of the (first) host, plus where new VMs go.
        public EsxiInventory Inventory()
        {
            var inv = new EsxiInventory();
            foreach (var dc in ListObjects("Datacenter", "vmFolder"))
            {
                inv.VmFolder = dc.Value["vmFolder"].InnerText;
                break;
            }
            string parent = null;
            List<XmlElement> dsRefs = null, netRefs = null;
            foreach (var h in ListObjects("HostSystem", "parent", "datastore", "network"))
            {
                inv.Host = h.Key;
                parent = h.Value["parent"].InnerText;
                dsRefs = new List<XmlElement>(Kids(h.Value["datastore"], "ManagedObjectReference"));
                netRefs = new List<XmlElement>(Kids(h.Value["network"], "ManagedObjectReference"));
                break;
            }
            if (inv.Host == null || inv.VmFolder == null) throw new Exception("The ESXi host did not report its inventory");
            var cr = GetProps("ComputeResource", parent, "resourcePool");
            inv.ResourcePool = cr["resourcePool"].InnerText;
            foreach (var r in dsRefs)
            {
                var p = GetProps("Datastore", r.InnerText, "summary");
                var s = p["summary"];
                if (Text(s, "accessible") == "false") continue;
                inv.Datastores.Add(new EsxiDatastore
                {
                    Moref = r.InnerText,
                    Name = Text(s, "name"),
                    Type = Text(s, "type"),
                    FreeBytes = Long(Text(s, "freeSpace")),
                    CapacityBytes = Long(Text(s, "capacity")),
                });
            }
            foreach (var r in netRefs)
            {
                string type = r.GetAttribute("type");
                var p = GetProps(type.Length > 0 ? type : "Network", r.InnerText, "name");
                inv.Networks.Add(new EsxiNetwork { Moref = r.InnerText, Type = type.Length > 0 ? type : "Network", Name = p["name"].InnerText });
            }
            inv.Datastores.Sort((a, b) => b.FreeBytes.CompareTo(a.FreeBytes));
            return inv;
        }

        // Creates a VM from an OVF descriptor and uploads its stream-optimized disks
        // (files: OVF file href -> local path). Returns the new VM's moref.
        public string ImportOvf(string ovf, string name, EsxiInventory inv, EsxiDatastore ds, EsxiNetwork net, bool thin,
            Dictionary<string, string> files, Action<string> log, Action<long, long> progress, Func<bool> cancelled)
        {
            Login();
            var networks = new List<string>();
            foreach (Match m in Regex.Matches(ovf, "<(?:\\w+:)?Network\\s+(?:\\w+:)?name=\"([^\"]+)\"")) networks.Add(m.Groups[1].Value);
            var map = new StringBuilder();
            foreach (string n in networks)
                map.Append("<networkMapping><name>").Append(Esc(n)).Append("</name><network type=\"").Append(Esc(net.Type)).Append("\">")
                   .Append(Esc(net.Moref)).Append("</network></networkMapping>");
            var ret = Return(Call("<CreateImportSpec xmlns=\"urn:vim25\"><_this type=\"OvfManager\">" + Esc(ovfManager) + "</_this>" +
                "<ovfDescriptor>" + Esc(ovf) + "</ovfDescriptor>" +
                "<resourcePool type=\"ResourcePool\">" + Esc(inv.ResourcePool) + "</resourcePool>" +
                "<datastore type=\"Datastore\">" + Esc(ds.Moref) + "</datastore>" +
                "<cisp><locale></locale><deploymentOption></deploymentOption><entityName>" + Esc(name) + "</entityName>" + map +
                "<diskProvisioning>" + (thin ? "thin" : "thick") + "</diskProvisioning></cisp></CreateImportSpec>"));
            foreach (var e in Kids(ret, "error")) throw new Exception("ESXi rejected the OVF: " + FaultText(e));
            foreach (var w in Kids(ret, "warning")) log("ESXi: " + FaultText(w));
            var spec = Child(ret, "importSpec");
            if (spec == null) throw new Exception("ESXi returned no import spec for the OVF");
            string specType = spec.GetAttribute("type", "http://www.w3.org/2001/XMLSchema-instance");
            var items = new Dictionary<string, string>();   // deviceId -> file href
            foreach (var fi in Kids(ret, "fileItem")) items[Text(fi, "deviceId")] = Text(fi, "path");

            string lease = Return(Call("<ImportVApp xmlns=\"urn:vim25\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" " +
                "xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\"><_this type=\"ResourcePool\">" + Esc(inv.ResourcePool) + "</_this>" +
                "<spec xsi:type=\"" + Esc(specType.Length > 0 ? specType : "VirtualMachineImportSpec") + "\">" + spec.InnerXml + "</spec>" +
                "<folder type=\"Folder\">" + Esc(inv.VmFolder) + "</folder><host type=\"HostSystem\">" + Esc(inv.Host) + "</host></ImportVApp>")).InnerText;
            try
            {
                var info = WaitLease(lease, cancelled);
                long total = 0, done = 0;
                var uploads = new List<KeyValuePair<string, string>>();   // url -> local file
                foreach (var du in Kids(info, "deviceUrl"))
                {
                    string href, local;
                    if (!items.TryGetValue(Text(du, "importKey"), out href) || !files.TryGetValue(href, out local)) continue;
                    uploads.Add(new KeyValuePair<string, string>(Text(du, "url").Replace("://*/", "://" + host + "/"), local));
                    total += new FileInfo(local).Length;
                }
                if (uploads.Count == 0) throw new Exception("ESXi offered no upload address for the disks");
                using (var keeper = new LeaseKeeper(this, lease, () => total > 0 ? (int)(100 * done / total) : 0))
                {
                    foreach (var u in uploads)
                    {
                        log("Uploading " + Path.GetFileName(u.Value) + " (" + Theme.Bytes(new FileInfo(u.Value).Length) + ") to datastore " + ds.Name);
                        long before = done;
                        Upload(u.Key, u.Value, n => { done = before + n; progress(done, total); }, cancelled);
                        done = before + new FileInfo(u.Value).Length;
                    }
                }
                Call("<HttpNfcLeaseComplete xmlns=\"urn:vim25\"><_this type=\"HttpNfcLease\">" + Esc(lease) + "</_this></HttpNfcLeaseComplete>");
                var entity = Child(info, "entity");
                return entity != null ? entity.InnerText : null;
            }
            catch (Exception ex)
            {
                AbortLease(lease, ex.Message);
                throw;
            }
        }

        // Exports a powered-off VM: its disks as stream-optimized VMDKs into `folder`,
        // plus the OVF descriptor ESXi writes for them. Returns the .ovf path.
        public string ExportOvf(string moref, string name, string folder, Action<string> log, Action<long, long> progress, Func<bool> cancelled)
        {
            Login();
            string lease = Return(Call("<ExportVm xmlns=\"urn:vim25\"><_this type=\"VirtualMachine\">" + Esc(moref) + "</_this></ExportVm>")).InnerText;
            try
            {
                var info = WaitLease(lease, cancelled);
                long total = Long(Text(info, "totalDiskCapacityInKB")) * 1024, done = 0;
                var ovfFiles = new StringBuilder();
                using (var keeper = new LeaseKeeper(this, lease, () => total > 0 ? (int)Math.Min(99, 100 * done / total) : 0))
                {
                    int n = 0;
                    foreach (var du in Kids(info, "deviceUrl"))
                    {
                        if (Text(du, "disk") != "true") continue;
                        string file = SafeFile(name) + "-disk" + (++n) + ".vmdk";
                        string local = Path.Combine(folder, file);
                        log("Downloading disk " + n + " of " + name + " from ESXi (stream-optimized, empty blocks are skipped)");
                        long before = done;
                        long size = Download(Text(du, "url").Replace("://*/", "://" + host + "/"), local, b => { done = before + b; progress(done, total); }, cancelled);
                        ovfFiles.Append("<ovfFiles><deviceId>").Append(Esc(Text(du, "key"))).Append("</deviceId><path>").Append(Esc(file))
                                .Append("</path><size>").Append(size).Append("</size></ovfFiles>");
                    }
                }
                var desc = Return(Call("<CreateDescriptor xmlns=\"urn:vim25\"><_this type=\"OvfManager\">" + Esc(ovfManager) + "</_this>" +
                    "<obj type=\"VirtualMachine\">" + Esc(moref) + "</obj><cdp>" + ovfFiles + "<name>" + Esc(name) + "</name>" +
                    "<description>Exported by GeuzaVirt</description><includeImageFiles>false</includeImageFiles></cdp></CreateDescriptor>"));
                foreach (var e in Kids(desc, "error")) throw new Exception("ESXi could not describe the VM: " + FaultText(e));
                string ovfPath = Path.Combine(folder, SafeFile(name) + ".ovf");
                File.WriteAllText(ovfPath, Text(desc, "ovfDescriptor"), new UTF8Encoding(false));
                Call("<HttpNfcLeaseComplete xmlns=\"urn:vim25\"><_this type=\"HttpNfcLease\">" + Esc(lease) + "</_this></HttpNfcLeaseComplete>");
                return ovfPath;
            }
            catch (Exception ex)
            {
                AbortLease(lease, ex.Message);
                throw;
            }
        }

        public void PowerOn(string moref)
        {
            Call("<PowerOnVM_Task xmlns=\"urn:vim25\"><_this type=\"VirtualMachine\">" + Esc(moref) + "</_this></PowerOnVM_Task>");
        }

        public static string SafeFile(string name)
        {
            string s = Regex.Replace(name ?? "", "[^A-Za-z0-9._-]+", "-").Trim('-', '.');
            return s.Length > 0 ? s : "vm";
        }

        XmlElement WaitLease(string lease, Func<bool> cancelled)
        {
            var until = DateTime.UtcNow.AddMinutes(10);
            while (true)
            {
                if (cancelled()) throw new OperationCanceledException();
                var p = GetProps("HttpNfcLease", lease, "state");
                string state = p["state"].InnerText;
                if (state == "ready") return GetProps("HttpNfcLease", lease, "info")["info"];
                if (state == "error")
                {
                    var e = GetProps("HttpNfcLease", lease, "error");
                    throw new Exception("ESXi: " + FaultText(e.ContainsKey("error") ? e["error"] : null));
                }
                if (DateTime.UtcNow > until) throw new Exception("ESXi did not get the disk transfer ready within 10 minutes");
                Thread.Sleep(1000);
            }
        }

        void AbortLease(string lease, string why)
        {
            try
            {
                Call("<HttpNfcLeaseAbort xmlns=\"urn:vim25\"><_this type=\"HttpNfcLease\">" + Esc(lease) + "</_this>" +
                     "<fault xsi:type=\"SystemError\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\"><reason>" + Esc(why) + "</reason></fault></HttpNfcLeaseAbort>");
            }
            catch (Exception) { }
        }

        internal void LeaseProgress(string lease, int percent)
        {
            Call("<HttpNfcLeaseProgress xmlns=\"urn:vim25\"><_this type=\"HttpNfcLease\">" + Esc(lease) + "</_this><percent>" +
                 Math.Max(0, Math.Min(100, percent)) + "</percent></HttpNfcLeaseProgress>");
        }

        // Keeps an NFC lease alive (it expires after 5 idle minutes) while disks transfer.
        class LeaseKeeper : IDisposable
        {
            readonly Timer timer;

            public LeaseKeeper(EsxiClient c, string lease, Func<int> percent)
            {
                timer = new Timer(_ => { try { c.LeaseProgress(lease, percent()); } catch (Exception) { } }, null, 20000, 30000);
            }

            public void Dispose() { timer.Dispose(); }
        }

        HttpWebRequest NfcRequest(string url, string method)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = method;
            req.CookieContainer = cookies;
            req.Timeout = 120000;
            req.ReadWriteTimeout = 600000;
            req.KeepAlive = false;
            req.ServerCertificateValidationCallback = (s, cert, chain, errors) => true;
            return req;
        }

        void Upload(string url, string path, Action<long> onBytes, Func<bool> cancelled)
        {
            var req = NfcRequest(url, "POST");
            // .NET Framework aborts a streamed upload when Timeout runs out, even while the data
            // still flows ("The request was canceled"); a 90 GB disk takes far longer than any
            // fixed limit. Stalls are still caught by ReadWriteTimeout (10 minutes per write).
            req.Timeout = Timeout.Infinite;
            req.ContentType = "application/x-vnd.vmware-streamVmdk";
            req.AllowWriteStreamBuffering = false;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
            {
                req.ContentLength = fs.Length;
                var buf = new byte[1 << 20];
                long sent = 0;
                using (var rs = req.GetRequestStream())
                {
                    int n;
                    while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                    {
                        if (cancelled()) { req.Abort(); throw new OperationCanceledException(); }
                        rs.Write(buf, 0, n);
                        sent += n;
                        onBytes(sent);
                    }
                }
            }
            try { using (req.GetResponse()) { } }
            catch (WebException ex)
            {
                var resp = ex.Response as HttpWebResponse;
                throw new Exception("Upload to ESXi failed" + (resp != null ? " (HTTP " + (int)resp.StatusCode + ")" : "") + ": " + ex.Message);
            }
        }

        long Download(string url, string path, Action<long> onBytes, Func<bool> cancelled)
        {
            var req = NfcRequest(url, "GET");
            long got = 0;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var s = resp.GetResponseStream())
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                var buf = new byte[1 << 20];
                int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0)
                {
                    if (cancelled()) { req.Abort(); throw new OperationCanceledException(); }
                    fs.Write(buf, 0, n);
                    got += n;
                    onBytes(got);
                }
            }
            return got;
        }

        static string FaultText(XmlElement e)
        {
            if (e == null) return "unknown error";
            string msg = Text(e, "localizedMessage");
            if (msg.Length == 0) foreach (var c in Kids(e, "fault")) msg = Text(c, "faultMessage");
            return msg.Length > 0 ? msg : e.InnerText;
        }

        // Every object of a type (through a container view) with the given properties.
        List<KeyValuePair<string, Dictionary<string, XmlElement>>> ListObjects(string type, params string[] props)
        {
            string view = Return(Call(
                "<CreateContainerView xmlns=\"urn:vim25\"><_this type=\"ViewManager\">" + Esc(viewManager) + "</_this>" +
                "<container type=\"Folder\">" + Esc(rootFolder) + "</container><type>" + Esc(type) + "</type><recursive>true</recursive></CreateContainerView>")).InnerText;
            var spec = new StringBuilder("<specSet><propSet><type>").Append(Esc(type)).Append("</type>");
            foreach (string p in props) spec.Append("<pathSet>").Append(p).Append("</pathSet>");
            spec.Append("</propSet><objectSet><obj type=\"ContainerView\">").Append(Esc(view)).Append("</obj><skip>true</skip>")
                .Append("<selectSet xsi:type=\"TraversalSpec\"><name>view</name><type>ContainerView</type><path>view</path><skip>false</skip></selectSet>")
                .Append("</objectSet></specSet>");
            var list = new List<KeyValuePair<string, Dictionary<string, XmlElement>>>();
            var result = Return(Call(RetrieveProps(spec.ToString())));
            foreach (var obj in Kids(result, "objects")) list.Add(new KeyValuePair<string, Dictionary<string, XmlElement>>(Text(obj, "obj"), Props(obj)));
            try { Call("<DestroyView xmlns=\"urn:vim25\"><_this type=\"ContainerView\">" + Esc(view) + "</_this></DestroyView>"); }
            catch (Exception) { }
            return list;
        }

        Dictionary<string, XmlElement> GetProps(string type, string moref, params string[] props)
        {
            var spec = new StringBuilder("<specSet><propSet><type>").Append(Esc(type)).Append("</type>");
            foreach (string p in props) spec.Append("<pathSet>").Append(p).Append("</pathSet>");
            spec.Append("</propSet><objectSet><obj type=\"").Append(Esc(type)).Append("\">").Append(Esc(moref)).Append("</obj><skip>false</skip></objectSet></specSet>");
            var result = Return(Call(RetrieveProps(spec.ToString())));
            foreach (var obj in Kids(result, "objects")) return Props(obj);
            throw new Exception(type + " " + moref + " not found on the ESXi host");
        }

        static Dictionary<string, XmlElement> Props(XmlElement obj)
        {
            var d = new Dictionary<string, XmlElement>();
            foreach (var prop in Kids(obj, "propSet"))
            {
                var val = Child(prop, "val");
                if (val != null) d[Text(prop, "name")] = val;
            }
            return d;
        }

        string RetrieveProps(string specSet)
        {
            return "<RetrievePropertiesEx xmlns=\"urn:vim25\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">" +
                   "<_this type=\"PropertyCollector\">" + Esc(propertyCollector) + "</_this>" + specSet +
                   "<options/></RetrievePropertiesEx>";
        }

        static EsxiVm ParseVm(XmlElement obj)
        {
            var vm = new EsxiVm { Moref = Text(obj, "obj") };
            foreach (var prop in Kids(obj, "propSet"))
            {
                string name = Text(prop, "name");
                var val = Child(prop, "val");
                if (val == null) continue;
                string v = val.InnerText;
                switch (name)
                {
                    case "name": vm.Name = v; break;
                    case "config.template": vm.Template = v == "true"; break;
                    case "config.guestId": vm.GuestId = v; break;
                    case "config.guestFullName": vm.GuestName = v; break;
                    case "config.firmware": vm.Uefi = v == "efi"; break;
                    case "config.hardware.numCPU": vm.Cpus = int.Parse(v); break;
                    case "config.hardware.memoryMB": vm.MemoryMb = int.Parse(v); break;
                    case "summary.config.vmPathName": vm.VmxPath = v; break;
                    case "summary.storage.committed": vm.CommittedBytes = long.Parse(v); break;
                    case "runtime.powerState": vm.PowerState = v; break;
                    case "guest.toolsRunningStatus": vm.ToolsStatus = v; break;
                    case "layoutEx.file":
                        foreach (var f in Kids(val, "VirtualMachineFileLayoutExFileInfo"))
                        {
                            long size;
                            long.TryParse(Text(f, "size"), out size);
                            vm.Files.Add(new EsxiFile { Name = Text(f, "name"), Type = Text(f, "type"), Size = size });
                        }
                        break;
                    case "summary.quickStats.overallCpuUsage": vm.CpuUsageMhz = Long(v); break;
                    case "summary.quickStats.guestMemoryUsage": vm.GuestMemoryMb = Long(v); break;
                    case "runtime.maxCpuUsage": vm.MaxCpuMhz = Long(v); break;
                    case "guest.disk":
                        long used = 0;
                        bool any = false;
                        foreach (var gd in Kids(val, "GuestDiskInfo"))
                        {
                            long cap = Long(Text(gd, "capacity")), free = Long(Text(gd, "freeSpace"));
                            if (cap > 0 && free >= 0) { used += cap - free; any = true; }
                        }
                        if (any) vm.GuestDiskUsed = used;
                        break;
                    case "config.hardware.device":
                        var buses = new Dictionary<string, string>();   // controller key -> "scsi0"
                        var disks = new List<XmlElement>();
                        foreach (XmlNode dev in val.ChildNodes)
                        {
                            var el = dev as XmlElement;
                            if (el == null) continue;
                            string type = el.GetAttribute("type", "http://www.w3.org/2001/XMLSchema-instance");
                            if (type == "VirtualTPM") vm.Tpm = true;
                            string bus = type.Contains("SCSIController") || type.StartsWith("VirtualLsiLogic") || type == "VirtualBusLogicController" ? "scsi"
                                       : type == "VirtualAHCIController" ? "sata"
                                       : type == "VirtualNVMEController" ? "nvme"
                                       : type == "VirtualIDEController" ? "ide" : null;
                            if (bus != null) buses[Text(el, "key")] = bus + Text(el, "busNumber");
                            string mac = Text(el, "macAddress");
                            if (mac.Length > 0) vm.Macs.Add(mac);
                            if (type == "VirtualDisk") disks.Add(el);
                        }
                        foreach (var el in disks)
                        {
                            long bytes = Long(Text(el, "capacityInBytes"));
                            if (bytes <= 0) bytes = Long(Text(el, "capacityInKB")) * 1024;
                            if (bytes > 0) vm.CapacityBytes += bytes;
                            var backing = Child(el, "backing");
                            if (backing != null) vm.DiskFiles.Add(Text(backing, "fileName"));
                            string bus;
                            if (buses.TryGetValue(Text(el, "controllerKey"), out bus))
                                vm.Disks.Add(new KeyValuePair<string, long>(bus + ":" + Text(el, "unitNumber"), bytes));
                        }
                        break;
                }
            }
            return vm;
        }

        XmlElement Call(string body)
        {
            string envelope = "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                "<soapenv:Envelope xmlns:soapenv=\"http://schemas.xmlsoap.org/soap/envelope/\"><soapenv:Body>" +
                body + "</soapenv:Body></soapenv:Envelope>";
            var req = (HttpWebRequest)WebRequest.Create("https://" + host + "/sdk");
            req.Method = "POST";
            req.ContentType = "text/xml; charset=utf-8";
            req.Headers.Add("SOAPAction", "urn:vim25/6.0");
            req.CookieContainer = cookies;
            req.Timeout = 60000;
            req.ReadWriteTimeout = 120000;
            // ESXi hosts ship self-signed certificates.
            req.ServerCertificateValidationCallback = (s, cert, chain, errors) => true;
            byte[] bytes = Encoding.UTF8.GetBytes(envelope);
            try
            {
                using (var rs = req.GetRequestStream()) rs.Write(bytes, 0, bytes.Length);
                using (var resp = (HttpWebResponse)req.GetResponse())
                    return Body(resp);
            }
            catch (WebException ex)
            {
                var resp = ex.Response as HttpWebResponse;
                if (resp == null) throw new Exception("Cannot reach ESXi host " + host + ": " + ex.Message);
                XmlElement fault;
                try { fault = Body(resp); }
                catch (Exception) { throw new Exception("ESXi host " + host + " returned HTTP " + (int)resp.StatusCode); }
                string msg = Text(fault, "faultstring");
                if (msg.Contains("incorrect user name or password"))
                    msg = "login refused - check the ESXi username and password";
                throw new Exception("ESXi: " + (msg.Length > 0 ? msg : fault.InnerText));
            }
        }

        static XmlElement Body(HttpWebResponse resp)
        {
            var doc = new XmlDocument();
            using (var s = resp.GetResponseStream()) doc.Load(s);
            foreach (XmlNode n in doc.DocumentElement.ChildNodes)
                if (n.LocalName == "Body")
                    foreach (XmlNode c in n.ChildNodes)
                        if (c is XmlElement) return (XmlElement)c;
            throw new Exception("Unexpected response from the ESXi host");
        }

        static XmlElement Return(XmlElement response)
        {
            return Child(response, "returnval");
        }

        static IEnumerable<XmlElement> Kids(XmlNode node, string localName)
        {
            if (node == null) yield break;
            foreach (XmlNode c in node.ChildNodes)
                if (c is XmlElement && c.LocalName == localName) yield return (XmlElement)c;
        }

        static XmlElement Child(XmlNode node, string localName)
        {
            foreach (var c in Kids(node, localName)) return c;
            return null;
        }

        static string Text(XmlNode node, string localName)
        {
            var c = Child(node, localName);
            return c != null ? c.InnerText : "";
        }

        static long Long(string s)
        {
            long v;
            return long.TryParse(s, out v) ? v : -1;
        }

        static string Esc(string s)
        {
            return SecurityElement.Escape(s ?? "");
        }
    }
}
