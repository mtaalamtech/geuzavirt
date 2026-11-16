using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace Mtaalam.V2V
{
    class CloneStream
    {
        public string Url;
        public byte[] Key, Iv;

        static string Hex(byte[] b)
        {
            var sb = new StringBuilder();
            foreach (byte x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }

        // Third field of an --esxi-urls line for vmware2pve.sh.
        public string ScriptSpec { get { return "mtv2v:" + Hex(Key) + ":" + Hex(Iv); } }
    }

    // A live connection to a guest: the Mtaalam V2V Agent service inside a Windows
    // VM (HTTP on port 8786, signed with the pairing key), or SSH into a Linux VM.
    class GuestAgent
    {
        public const int DefaultPort = 8786;

        public string Host;
        public int Port = DefaultPort;
        public string Key;               // Windows: pairing key
        public SshTarget Ssh;            // Linux: SSH login
        public string Hostname = "";

        public bool IsLinux { get { return Ssh != null; } }
        public string Label { get { return (Hostname.Length > 0 ? Hostname + " (" + Host + ")" : Host); } }

        // ---------------------------------------------------------------- Windows

        public Dictionary<string, object> Info()
        {
            if (IsLinux) return LinuxInfo();
            var info = Call("/info", null, 120000);
            Hostname = AgentReport.Str(info, "hostname");
            return info;
        }

        // Shrinks the guest's last volume on that disk so the disk fits in `size` bytes.
        public Dictionary<string, object> Shrink(int disk, long size, bool removeRecovery)
        {
            if (IsLinux)
                throw new Exception("Linux file systems cannot be shrunk while the VM runs. Shrink the partition offline (e.g. GParted live CD) " +
                                    "or keep the disk size.");
            var args = new Dictionary<string, object> { { "disk", disk }, { "size", size }, { "remove_recovery", removeRecovery } };
            return Call("/shrink", args, 3600000);
        }

        public void SaveNetwork()
        {
            if (!IsLinux) Call("/network-save", null, 60000);
        }

        public void Shutdown()
        {
            if (IsLinux)
            {
                int code;
                new SshClient(Ssh).Capture("(sleep 2; shutdown -h now || poweroff) >/dev/null 2>&1 &", out code);
                return;
            }
            Call("/shutdown", null, 30000);
        }

        public void Uninstall()
        {
            if (!IsLinux) Call("/uninstall", null, 30000);
        }

        // ---------------------------------------------------------- hot clone

        // Log line for small partitions the agent could not snapshot (agent 1.4.4+), or null.
        public static string NoSnapshotNote(Dictionary<string, object> prep)
        {
            object v;
            var items = prep.TryGetValue("no_snapshot", out v) ? v as System.Collections.IEnumerable : null;
            if (items == null) return null;
            var names = new List<string>();
            foreach (object s in items) names.Add(s.ToString());
            return names.Count == 0 ? null : "  Windows could not take a snapshot of " + string.Join(", ", names) +
                ": it has no drive letter and is small, so it is copied directly from the disk. All other volumes use snapshots.";
        }

        // Prepares a live copy: boot drivers for the target, network settings + clone
        // marker, VSS snapshots of the disks' volumes. Returns the agent's answer (session, disks).
        // shrink: "C:" -> bytes; the copy's agent shrinks these volumes with Windows at its first
        // boot when the Proxmox host could not (agent 1.3.1+).
        public Dictionary<string, object> ClonePrepare(IList<int> disks, string target, bool restoreNetwork, IDictionary<string, long> shrink = null)
        {
            if (IsLinux) throw new Exception("Live copies of Linux machines are not supported yet - shut the machine down and convert its VM files.");
            var list = new List<object>();
            foreach (int d in disks) list.Add(d);
            var args = new Dictionary<string, object> { { "disks", list }, { "target", target }, { "restore_network", restoreNetwork } };
            if (shrink != null && shrink.Count > 0)
            {
                var s = new Dictionary<string, object>();
                foreach (var kv in shrink) s[kv.Key] = kv.Value;
                args["shrink"] = s;
            }
            var r = Call("/clone-prepare", args, 1800000);
            if (AgentReport.Str(r, "session").Length == 0) throw new Exception("The agent on " + Host + " did not start the live copy (update the agent: it needs version 1.2 or newer).");
            return r;
        }

        public void CloneFinish(string session)
        {
            if (!IsLinux && !string.IsNullOrEmpty(session))
                Call("/clone-finish", new Dictionary<string, object> { { "session", session } }, 300000);
        }

        // URL + AES key/IV of one disk's stream. Each call uses a fresh nonce, so a
        // retried copy never reuses a key stream.
        // headOnly: partitions (byte offsets) copied file by file, or "*" for the partition
        // table and boot sectors only (agent 1.4+).
        public CloneStream StreamFor(string session, int disk, string headOnly = "")
        {
            string d = disk.ToString(CultureInfo.InvariantCulture);
            string x = headOnly ?? "";
            return Signed(session, d, (n, h) =>
                "/clone-stream?s=" + session + "&d=" + d + "&n=" + n + (x.Length > 0 ? "&x=" + x : "") + "&sig=" +
                Hex(h.ComputeHash(Encoding.UTF8.GetBytes("clone-stream\n" + session + "\n" + d + "\n" + n + (x.Length > 0 ? "\n" + x : "")))));
        }

        // The files of the volume at `offset` as a pipable WIM (agent 1.4+ with wimlib).
        public CloneStream FilesFor(string session, int disk, long offset)
        {
            string d = disk.ToString(CultureInfo.InvariantCulture), o = offset.ToString(CultureInfo.InvariantCulture);
            return Signed(session, d + "@" + o, (n, h) =>
                "/clone-files?s=" + session + "&d=" + d + "&o=" + o + "&n=" + n + "&sig=" +
                Hex(h.ComputeHash(Encoding.UTF8.GetBytes("clone-files\n" + session + "\n" + d + "\n" + o + "\n" + n))));
        }

        CloneStream Signed(string session, string id, Func<string, HMACSHA256, string> path)
        {
            var nb = new byte[16];
            using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(nb);
            string n = Hex(nb);
            byte[] k = Encoding.UTF8.GetBytes(NormalizeKey(Key));
            using (var h = new HMACSHA256(k))
            {
                byte[] key = h.ComputeHash(Encoding.UTF8.GetBytes("mtv2v-stream-key\n" + session + "\n" + id + "\n" + n));
                byte[] iv = new byte[16];
                Array.Copy(h.ComputeHash(Encoding.UTF8.GetBytes("mtv2v-stream-iv\n" + session + "\n" + id + "\n" + n)), iv, 16);
                return new CloneStream { Url = "http://" + Host + ":" + Port + path(n, h), Key = key, Iv = iv };
            }
        }

        static string Hex(byte[] b)
        {
            var sb = new StringBuilder(b.Length * 2);
            foreach (byte x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }

        Dictionary<string, object> Call(string path, Dictionary<string, object> args, int timeoutMs)
        {
            var ser = new JavaScriptSerializer { MaxJsonLength = 64 << 20 };
            var hello = ser.DeserializeObject(Http("GET", "/hello", null, null, 15000).Body) as Dictionary<string, object>;
            if (hello == null || AgentReport.Str(hello, "agent") != "mtaalam-v2v")
                throw new Exception(Host + ":" + Port + " is not a GeuzaVirt agent");
            string nonce = AgentReport.Str(hello, "nonce");
            string body = ser.Serialize(args ?? new Dictionary<string, object>());
            var headers = new Dictionary<string, string>
            {
                { "X-Mtaalam-Nonce", nonce },
                { "X-Mtaalam-Signature", Sign(nonce + "\n" + path + "\n" + body) },
            };
            var resp = Http("POST", path, body, headers, timeoutMs);
            if (resp.Status == 403) throw new Exception("The agent on " + Host + " refused the pairing key. Check the key (run MtaalamV2VAgent.exe in the VM to show it).");
            if (resp.Status == 200 && !SigEquals(Sign(nonce + "\n" + resp.Body), resp.Signature))
                throw new Exception("The answer from " + Host + " was not signed with the pairing key - is something else listening on port " + Port + "?");
            var result = ser.DeserializeObject(resp.Body) as Dictionary<string, object>;
            if (result == null) throw new Exception("Unexpected answer from the agent on " + Host);
            if (resp.Status != 200) throw new Exception("Agent on " + Host + ": " + AgentReport.Str(result, "error"));
            return result;
        }

        class Response { public int Status; public string Body; public string Signature; }

        Response Http(string method, string path, string body, Dictionary<string, string> headers, int timeoutMs)
        {
            var req = (HttpWebRequest)WebRequest.Create("http://" + Host + ":" + Port + path);
            req.Method = method;
            req.Proxy = null;   // guests are on the LAN
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.KeepAlive = false;
            if (headers != null) foreach (var h in headers) req.Headers[h.Key] = h.Value;
            try
            {
                if (body != null)
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(body);
                    req.ContentType = "application/json; charset=utf-8";
                    req.ContentLength = bytes.Length;
                    using (var s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
                }
                using (var resp = (HttpWebResponse)req.GetResponse())
                    return Read(resp);
            }
            catch (WebException ex)
            {
                var resp = ex.Response as HttpWebResponse;
                if (resp != null) using (resp) return Read(resp);
                if (ex.Status == WebExceptionStatus.ConnectFailure || ex.Status == WebExceptionStatus.Timeout || ex.Status == WebExceptionStatus.NameResolutionFailure)
                    throw new Exception("Cannot reach the GeuzaVirt agent on " + Host + ":" + Port + " (" + ex.Message + "). Is the agent installed and " +
                                        "running in the VM, and is the VM reachable from this PC?");
                throw;
            }
        }

        static Response Read(HttpWebResponse resp)
        {
            using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return new Response { Status = (int)resp.StatusCode, Body = r.ReadToEnd(), Signature = resp.Headers["X-Mtaalam-Signature"] ?? "" };
        }

        string Sign(string text)
        {
            byte[] k = Encoding.UTF8.GetBytes(NormalizeKey(Key));
            using (var h = new HMACSHA256(k))
            {
                var sb = new StringBuilder();
                foreach (byte b in h.ComputeHash(Encoding.UTF8.GetBytes(text))) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        static bool SigEquals(string a, string b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= char.ToLowerInvariant(a[i]) ^ char.ToLowerInvariant(b[i]);
            return diff == 0;
        }

        public static string NormalizeKey(string key)
        {
            return Regex.Replace(key ?? "", "[^A-Za-z0-9]", "").ToUpperInvariant();
        }

        public static string NewKey()
        {
            const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            var bytes = new byte[20];
            using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(bytes);
            var sb = new StringBuilder();
            for (int i = 0; i < bytes.Length; i++)
            {
                if (i > 0 && i % 4 == 0) sb.Append('-');
                sb.Append(alphabet[bytes[i] % 32]);
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------ Linux

        // Runs the sizing agent script over SSH for a short sample and returns its report.
        Dictionary<string, object> LinuxInfo()
        {
            string agent;
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("mtaalam-v2v-agent.sh"))
            using (var r = new StreamReader(s)) agent = r.ReadToEnd().Replace("\r\n", "\n");
            string script =
                "d=$(mktemp -d) || exit 1\n" +
                "cat > \"$d/agent.sh\" <<'MTAALAM_AGENT_EOF'\n" + agent + "\nMTAALAM_AGENT_EOF\n" +
                "sh \"$d/agent.sh\" 0.5 \"$d\" >/dev/null 2>&1\n" +
                "echo \"##JSON $(cat \"$d\"/*.v2v.json 2>/dev/null)\"\n" +
                "rm -rf \"$d\"\n";
            var ssh = new SshClient(Ssh);
            int code;
            string output = ssh.Capture(script, out code);
            if (code != 0 && output.IndexOf("##JSON {", StringComparison.Ordinal) < 0)
                throw new Exception("SSH to " + Ssh.Host + " failed: " + ssh.Explain(code).Replace("on the Proxmox host", "on the VM"));
            foreach (string line in output.Split('\n'))
                if (line.StartsWith("##JSON {"))
                {
                    var info = new JavaScriptSerializer().DeserializeObject(line.Substring(7).Trim()) as Dictionary<string, object>;
                    if (info != null)
                    {
                        Hostname = AgentReport.Str(info, "hostname");
                        return info;
                    }
                }
            throw new Exception("The sizing script did not produce a report on " + Ssh.Host + ". Output: " + output.Trim());
        }
    }

    // Installs the agent service on a Windows VM from this PC, like PsExec: copies
    // it to \\vm\ADMIN$\MtaalamV2V and registers the service through the remote
    // service manager. Needs an administrator account of the VM and file sharing (SMB).
    static class AgentInstaller
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        class NETRESOURCE
        {
            public int dwScope, dwType, dwDisplayType, dwUsage;
            public string lpLocalName, lpRemoteName, lpComment, lpProvider;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct SERVICE_STATUS
        {
            public int dwServiceType, dwCurrentState, dwControlsAccepted, dwWin32ExitCode, dwServiceSpecificExitCode, dwCheckPoint, dwWaitHint;
        }

        [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
        static extern int WNetAddConnection2(NETRESOURCE res, string password, string user, int flags);

        [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
        static extern int WNetCancelConnection2(string name, int flags, bool force);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr OpenSCManager(string machine, string database, uint access);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateService(IntPtr scm, string name, string display, uint access, uint type, uint start, uint error,
            string binary, string group, IntPtr tag, string deps, string user, string password);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr OpenService(IntPtr scm, string name, uint access);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool StartService(IntPtr svc, int argc, IntPtr argv);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool ControlService(IntPtr svc, int control, ref SERVICE_STATUS status);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool QueryServiceStatus(IntPtr svc, ref SERVICE_STATUS status);

        [DllImport("advapi32.dll")]
        static extern bool CloseServiceHandle(IntPtr h);

        const uint SC_MANAGER_ALL = 0xF003F, SERVICE_ALL = 0xF01FF;
        const uint SERVICE_WIN32_OWN_PROCESS = 0x10, SERVICE_AUTO_START = 2, SERVICE_ERROR_NORMAL = 1;
        const int SERVICE_STOPPED = 1, SERVICE_RUNNING = 4;

        public static string AgentFolder
        {
            get { return Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "agent"); }
        }

        // Returns the pairing key (an existing one is kept).
        public static string Install(string host, string user, string password, Action<string> log)
        {
            string exe = Path.Combine(AgentFolder, "MtaalamV2VAgent.exe");
            if (!File.Exists(exe)) throw new Exception("The agent is missing from this package: " + exe);
            bool local = host == null;
            string share = local ? Environment.GetFolderPath(Environment.SpecialFolder.Windows) : @"\\" + host + @"\ADMIN$";
            if (!local)
            {
                log("Connecting to " + share + " as " + user + " ...");
                int rc = WNetAddConnection2(new NETRESOURCE { dwType = 1, lpRemoteName = share }, password, user, 0);
                if (rc == 1219)   // already connected with other credentials
                {
                    WNetCancelConnection2(share, 0, true);
                    rc = WNetAddConnection2(new NETRESOURCE { dwType = 1, lpRemoteName = share }, password, user, 0);
                }
                if (rc != 0) throw new Exception(ShareError(host, rc));
            }
            host = host ?? Environment.MachineName;
            try
            {
                string dir = Path.Combine(share, "MtaalamV2V");
                Directory.CreateDirectory(dir);
                string conf = Path.Combine(dir, "agent.conf");
                string key = null;
                if (File.Exists(conf))
                    foreach (string line in File.ReadAllLines(conf))
                        if (line.StartsWith("key=")) key = line.Substring(4).Trim();
                if (string.IsNullOrEmpty(key)) key = GuestAgent.NewKey();

                IntPtr scm = OpenSCManager(local ? null : host, null, SC_MANAGER_ALL);
                if (scm == IntPtr.Zero) throw new Exception("Cannot open the service manager on " + host + ": " + new Win32Exception().Message);
                try
                {
                    IntPtr svc = OpenService(scm, "MtaalamV2VAgent", SERVICE_ALL);
                    if (svc != IntPtr.Zero)
                    {
                        log("Stopping the installed agent ...");
                        var st = new SERVICE_STATUS();
                        ControlService(svc, 1, ref st);
                        Wait(svc, SERVICE_STOPPED, 30);
                    }
                    log("Copying the agent to " + dir + " ...");
                    Copy(exe, Path.Combine(dir, "MtaalamV2VAgent.exe"));
                    if (File.Exists(exe + ".config")) Copy(exe + ".config", Path.Combine(dir, "MtaalamV2VAgent.exe.config"));
                    // wimlib: file-level copy of resized volumes
                    string wim = Path.Combine(AgentFolder, "wimlib");
                    if (Directory.Exists(wim))
                    {
                        string to = Path.Combine(dir, "wimlib");
                        Directory.CreateDirectory(to);
                        foreach (string f in Directory.GetFiles(wim)) Copy(f, Path.Combine(to, Path.GetFileName(f)));
                    }
                    File.WriteAllText(conf, "key=" + key + "\r\nport=" + GuestAgent.DefaultPort + "\r\n");

                    if (svc == IntPtr.Zero)
                    {
                        log("Registering the service ...");
                        svc = CreateService(scm, "MtaalamV2VAgent", "Mtaalam V2V Agent", SERVICE_ALL, SERVICE_WIN32_OWN_PROCESS, SERVICE_AUTO_START,
                            SERVICE_ERROR_NORMAL, "\"%SystemRoot%\\MtaalamV2V\\MtaalamV2VAgent.exe\" /service", null, IntPtr.Zero, null, null, null);
                        if (svc == IntPtr.Zero) throw new Exception("Cannot create the service on " + host + ": " + new Win32Exception().Message);
                    }
                    try
                    {
                        log("Starting the agent ...");
                        if (!StartService(svc, 0, IntPtr.Zero) && Marshal.GetLastWin32Error() != 1056)
                            throw new Exception("The agent service did not start on " + host + ": " + new Win32Exception().Message);
                        if (!Wait(svc, SERVICE_RUNNING, 30))
                            throw new Exception("The agent service did not start on " + host + " (needs .NET Framework 2.0/3.5 or 4.x in the VM).");
                    }
                    finally { CloseServiceHandle(svc); }
                }
                finally { CloseServiceHandle(scm); }
                return key;
            }
            finally { if (!local) WNetCancelConnection2(share, 0, true); }
        }

        // "This local machine": installs the agent on this PC. The app restarts itself
        // elevated (one UAC prompt) with --install-agent-local; the key comes back in a file.
        public static string InstallLocal(Action<string> log)
        {
            string file = Path.Combine(Path.GetTempPath(), "mtv2v-" + Guid.NewGuid().ToString("N") + ".key");
            log("Installing the agent on this PC (confirm the Windows administrator prompt) ...");
            var psi = new System.Diagnostics.ProcessStartInfo(Application.ExecutablePath, "--install-agent-local \"" + file + "\"")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            };
            System.Diagnostics.Process p;
            try { p = System.Diagnostics.Process.Start(psi); }
            catch (Win32Exception) { throw new Exception("The agent was not installed: the administrator prompt was cancelled."); }
            p.WaitForExit();
            try
            {
                string text = File.Exists(file) ? File.ReadAllText(file).Trim() : "";
                if (p.ExitCode != 0 || !text.StartsWith("key="))
                    throw new Exception("Installing the agent on this PC failed: " + (text.Length > 0 ? text : "exit code " + p.ExitCode));
                return text.Substring(4);
            }
            finally { try { File.Delete(file); } catch (IOException) { } }
        }

        // Runs in the elevated copy of the app.
        public static int InstallLocalElevated(string resultFile)
        {
            try
            {
                string key = Install(null, null, null, msg => { });
                File.WriteAllText(resultFile, "key=" + key);
                return 0;
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(resultFile, ex.Message); } catch (IOException) { }
                return 1;
            }
        }

        static bool Wait(IntPtr svc, int state, int seconds)
        {
            var st = new SERVICE_STATUS();
            for (int i = 0; i < seconds * 2; i++)
            {
                if (QueryServiceStatus(svc, ref st) && st.dwCurrentState == state) return true;
                Thread.Sleep(500);
            }
            return false;
        }

        static void Copy(string from, string to)
        {
            for (int i = 0; ; i++)
            {
                try { File.Copy(from, to, true); return; }
                catch (IOException) { if (i >= 10) throw; Thread.Sleep(1000); }
            }
        }

        static string ShareError(string host, int rc)
        {
            switch (rc)
            {
                case 5:
                case 1326:
                    return "Access to \\\\" + host + "\\ADMIN$ was refused (" + new Win32Exception(rc).Message.TrimEnd('.') + "). Use the VM's built-in " +
                           "Administrator account or a domain admin. Other local admin accounts are blocked from remote installs by UAC - " +
                           "in that case copy the agent folder into the VM and run Install-Agent.cmd there.";
                case 53:
                case 67:
                case 1231:
                    return "\\\\" + host + "\\ADMIN$ cannot be reached (" + new Win32Exception(rc).Message.TrimEnd('.') + "). File and printer sharing " +
                           "(SMB, TCP 445) must be allowed in the VM's firewall - or copy the agent folder into the VM and run Install-Agent.cmd there.";
                default:
                    return "Cannot connect to \\\\" + host + "\\ADMIN$: " + new Win32Exception(rc).Message;
            }
        }
    }
}
