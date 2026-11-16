// Mtaalam V2V guest agent (Windows service) - Core Mtaalam Technologies, mtaalamtech.com
//
// Installed inside a Windows VM. The Mtaalam V2V Converter connects to it over
// the network (TCP 8786, every request signed with the pairing key) to:
//   - read CPU / memory use (measured continuously while the service runs),
//     disks, partitions, volumes and network settings;
//   - shrink the last volume of a disk so the disk can be made smaller on Proxmox;
//   - save the static IP settings while the VM is on VMware and put them back on
//     the new network card once it boots on Proxmox (the old VMware card is
//     removed, so its IP is not "already assigned to another adapter").
//
// Built against .NET 2.0 (C# 3) so it runs on Windows Server 2008 R2 and newer.
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("Mtaalam V2V Agent")]
[assembly: System.Reflection.AssemblyProduct("Mtaalam V2V Converter")]
[assembly: System.Reflection.AssemblyCompany("Core Mtaalam Technologies")]
[assembly: System.Reflection.AssemblyCopyright("Core Mtaalam Technologies - mtaalamtech.com")]
[assembly: System.Reflection.AssemblyVersion("1.2.0.0")]

namespace Mtaalam.V2V.Agent
{
    static class Program
    {
        public const string ServiceName = "MtaalamV2VAgent";
        public const string DisplayName = "Mtaalam V2V Agent";
        public const string Version = "1.4.4";
        public const int DefaultPort = 8786;

        public static string InstallDir
        {
            get { return Path.Combine(Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows", "MtaalamV2V"); }
        }

        [STAThread]
        static int Main(string[] args)
        {
            string cmd = args.Length > 0 ? args[0].ToLowerInvariant().TrimStart('-', '/') : "";
            try
            {
                switch (cmd)
                {
                    case "service":
                        ServiceBase.Run(new AgentService());
                        return 0;
                    case "console":
                        var agent = new Agent();
                        agent.Start();
                        Console.WriteLine("Mtaalam V2V Agent running on port " + agent.Port + ". Press Enter to stop.");
                        Console.ReadLine();
                        agent.Stop();
                        return 0;
                    case "install":
                        string key = Installer.Install(args.Length > 1 ? args[1] : null);
                        if (!Quiet(args)) Show("The Mtaalam V2V Agent is installed and running.\n\n" + KeyText(key), MessageBoxIcon.Information);
                        return 0;
                    case "uninstall":
                        Installer.Uninstall();
                        if (!Quiet(args)) Show("The Mtaalam V2V Agent was removed.", MessageBoxIcon.Information);
                        return 0;
                    case "showkey":
                        Show(KeyText(Config.Load().Key), MessageBoxIcon.Information);
                        return 0;
                    case "":
                        if (Installer.IsInstalled())
                            Show("The Mtaalam V2V Agent is installed.\n\n" + KeyText(Config.Load().Key) +
                                 "\n\nTo remove it, run Uninstall-Agent.cmd.", MessageBoxIcon.Information);
                        else if (MessageBox.Show("Install the Mtaalam V2V Agent on " + Environment.MachineName + "?\n\n" +
                                 "It lets the Mtaalam V2V Converter read this VM's CPU, memory, disk and network settings, " +
                                 "shrink its volumes on request and restore its IP settings after the move to Proxmox.\n" +
                                 "It listens on TCP port " + DefaultPort + " and only accepts requests signed with the pairing key.",
                                 DisplayName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                        {
                            string k = Installer.Install(null);
                            Show("The Mtaalam V2V Agent is installed and running.\n\n" + KeyText(k), MessageBoxIcon.Information);
                        }
                        return 0;
                    default:
                        Show("Usage: MtaalamV2VAgent.exe [/install [key]] [/uninstall] [/showkey] [/quiet]", MessageBoxIcon.Information);
                        return 1;
                }
            }
            catch (Exception ex)
            {
                if (cmd == "service") throw;
                if (Quiet(args)) Console.Error.WriteLine(ex.Message);
                else Show(ex.Message, MessageBoxIcon.Error);
                return 2;
            }
        }

        static bool Quiet(string[] args)
        {
            foreach (string a in args) if (a.ToLowerInvariant().TrimStart('-', '/') == "quiet") return true;
            return false;
        }

        static string KeyText(string key)
        {
            return "Computer:     " + Environment.MachineName + "\n" +
                   "Port:         " + DefaultPort + "\n" +
                   "Pairing key:  " + key + "\n\n" +
                   "In Mtaalam V2V Converter choose Guest agent > Connect, and enter this computer's IP address and the pairing key. " +
                   "(Press Ctrl+C to copy this message.)";
        }

        static void Show(string text, MessageBoxIcon icon)
        {
            MessageBox.Show(text, DisplayName, MessageBoxButtons.OK, icon);
        }
    }

    class AgentService : ServiceBase
    {
        Agent agent;

        public AgentService()
        {
            ServiceName = Program.ServiceName;
            CanStop = true;
            CanShutdown = true;
        }

        protected override void OnStart(string[] args)
        {
            agent = new Agent();
            agent.Start();
        }

        protected override void OnStop()
        {
            if (agent != null) agent.Stop();
        }

        protected override void OnShutdown()
        {
            if (agent != null) agent.Stop();
        }
    }

    // ------------------------------------------------------------------ config

    class Config
    {
        public string Key;
        public int Port = Program.DefaultPort;

        public static string FilePath { get { return Path.Combine(Program.InstallDir, "agent.conf"); } }

        public static Config Load()
        {
            var c = new Config();
            if (!File.Exists(FilePath)) throw new Exception("The agent is not installed (" + FilePath + " is missing).");
            foreach (string line in File.ReadAllLines(FilePath))
            {
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string k = line.Substring(0, eq).Trim().ToLowerInvariant(), v = line.Substring(eq + 1).Trim();
                if (k == "key") c.Key = v;
                else if (k == "port") int.TryParse(v, out c.Port);
            }
            if (string.IsNullOrEmpty(c.Key)) throw new Exception("No pairing key in " + FilePath);
            return c;
        }

        public void Save()
        {
            Directory.CreateDirectory(Program.InstallDir);
            File.WriteAllText(FilePath, "key=" + Key + "\r\nport=" + Port + "\r\n");
            Secure(FilePath);
        }

        // Only SYSTEM and Administrators may read the key.
        public static void Secure(string path)
        {
            try
            {
                var sec = new FileSecurity();
                sec.SetAccessRuleProtection(true, false);
                foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
                    sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl, AccessControlType.Allow));
                File.SetAccessControl(path, sec);
            }
            catch (Exception ex) { Log.Write("could not restrict access to " + path + ": " + ex.Message); }
        }

        // 20 characters from a 32-letter alphabet (100 bits), shown as XXXX-XXXX-XXXX-XXXX-XXXX.
        public static string NewKey()
        {
            const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            var bytes = new byte[20];
            new RNGCryptoServiceProvider().GetBytes(bytes);
            var sb = new StringBuilder();
            for (int i = 0; i < bytes.Length; i++)
            {
                if (i > 0 && i % 4 == 0) sb.Append('-');
                sb.Append(alphabet[bytes[i] % 32]);
            }
            return sb.ToString();
        }

        public static string Normalize(string key)
        {
            return Regex.Replace(key ?? "", "[^A-Za-z0-9]", "").ToUpperInvariant();
        }
    }

    static class Log
    {
        static readonly object gate = new object();

        public static void Write(string text)
        {
            try
            {
                lock (gate)
                {
                    string path = Path.Combine(Program.InstallDir, "agent.log");
                    if (File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024)
                        File.Copy(path, path + ".old", true);
                    File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + text + "\r\n");
                    if (File.Exists(path + ".old") && new FileInfo(path).Length > 2 * 1024 * 1024) File.Delete(path);
                }
            }
            catch (Exception) { }
        }
    }

    // --------------------------------------------------------------- installer

    static class Installer
    {
        public static bool IsInstalled()
        {
            foreach (var s in ServiceController.GetServices())
                if (s.ServiceName.Equals(Program.ServiceName, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // Copies the agent to %SystemRoot%\MtaalamV2V, registers and starts the service.
        // Keeps an existing pairing key unless a new one is given. Returns the key.
        public static string Install(string key)
        {
            RequireAdmin();
            string dir = Program.InstallDir;
            Directory.CreateDirectory(dir);
            string self = Application.ExecutablePath;
            string exe = Path.Combine(dir, "MtaalamV2VAgent.exe");

            Config cfg;
            try { cfg = Config.Load(); }
            catch (Exception) { cfg = new Config(); }
            if (!string.IsNullOrEmpty(key)) cfg.Key = key.Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(cfg.Key) || Config.Normalize(cfg.Key).Length < 12) cfg.Key = Config.NewKey();

            if (IsInstalled()) StopService();
            if (!self.Equals(exe, StringComparison.OrdinalIgnoreCase))
            {
                CopyWithRetry(self, exe);
                if (File.Exists(self + ".config")) CopyWithRetry(self + ".config", exe + ".config");
                // wimlib (file-level copy of resized volumes), shipped next to the agent
                string wim = Path.Combine(Path.GetDirectoryName(self), "wimlib");
                if (Directory.Exists(wim))
                {
                    string to = Path.Combine(dir, "wimlib");
                    Directory.CreateDirectory(to);
                    foreach (string f in Directory.GetFiles(wim)) CopyWithRetry(f, Path.Combine(to, Path.GetFileName(f)));
                }
            }
            cfg.Save();

            if (!IsInstalled())
            {
                Sc("create " + Program.ServiceName + " binPath= \"\\\"" + exe + "\\\" /service\" start= auto DisplayName= \"" + Program.DisplayName + "\"", true);
                Sc("description " + Program.ServiceName + " \"Mtaalam V2V Converter guest agent (mtaalamtech.com): sizing, volume shrink and network settings for the move to Proxmox VE.\"", false);
                Sc("failure " + Program.ServiceName + " reset= 86400 actions= restart/5000/restart/5000/restart/60000", false);
            }
            Sc("start " + Program.ServiceName, true);
            return cfg.Key;
        }

        public static void Uninstall()
        {
            RequireAdmin();
            if (IsInstalled())
            {
                StopService();
                Sc("delete " + Program.ServiceName, true);
            }
            Firewall.Remove();
            ScheduleDelete();
        }

        // The running exe cannot delete itself: a detached cmd removes the folder a few seconds later.
        public static void ScheduleDelete()
        {
            string dir = Program.InstallDir;
            if (!Directory.Exists(dir)) return;
            var psi = new ProcessStartInfo("cmd.exe", "/c ping -n 6 127.0.0.1 >nul & rd /s /q \"" + dir + "\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            Process.Start(psi);
        }

        static void StopService()
        {
            try
            {
                using (var sc = new ServiceController(Program.ServiceName))
                {
                    if (sc.Status != ServiceControllerStatus.Stopped)
                    {
                        sc.Stop();
                        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                    }
                }
            }
            catch (Exception) { }
        }

        static void CopyWithRetry(string from, string to)
        {
            for (int i = 0; ; i++)
            {
                try { File.Copy(from, to, true); return; }
                catch (IOException) { if (i >= 10) throw; Thread.Sleep(1000); }
            }
        }

        static void Sc(string args, bool check)
        {
            var psi = new ProcessStartInfo("sc.exe", args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using (var p = Process.Start(psi))
            {
                string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit();
                // 1056 = service already running
                if (check && p.ExitCode != 0 && p.ExitCode != 1056)
                    throw new Exception("sc " + args.Split(' ')[0] + " failed (" + p.ExitCode + "): " + output.Trim());
            }
        }

        static void RequireAdmin()
        {
            if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
                throw new Exception("Run this as Administrator (right-click > Run as administrator).");
        }
    }

    static class Firewall
    {
        const string Rule = "Mtaalam V2V Agent";

        public static void Ensure(int port)
        {
            try
            {
                if (Netsh("advfirewall firewall show rule name=\"" + Rule + "\"") == 0) return;
                Netsh("advfirewall firewall add rule name=\"" + Rule + "\" dir=in action=allow protocol=TCP localport=" + port +
                      " profile=any description=\"Mtaalam V2V Converter guest agent\"");
            }
            catch (Exception ex) { Log.Write("firewall rule: " + ex.Message); }
        }

        public static void Remove()
        {
            try { Netsh("advfirewall firewall delete rule name=\"" + Rule + "\""); }
            catch (Exception) { }
        }

        static int Netsh(string args)
        {
            var psi = new ProcessStartInfo("netsh.exe", args) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true };
            using (var p = Process.Start(psi))
            {
                p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                return p.ExitCode;
            }
        }
    }

    // ------------------------------------------------------------------- agent

    class Agent
    {
        Config config;
        byte[] key;
        HttpListener listener;
        Thread httpThread, statsThread, netThread;
        volatile bool stopping;
        readonly Stats stats = new Stats();
        readonly Dictionary<string, DateTime> nonces = new Dictionary<string, DateTime>();
        readonly object shrinkGate = new object();

        public int Port { get { return config.Port; } }

        public void Start()
        {
            config = Config.Load();
            key = Encoding.UTF8.GetBytes(Config.Normalize(config.Key));
            Config.Secure(Config.FilePath);
            Firewall.Ensure(config.Port);
            stats.Load();
            HotClone.CleanupLeftovers();

            listener = new HttpListener();
            listener.Prefixes.Add("http://+:" + config.Port + "/");
            listener.Start();
            Log.Write("agent " + Program.Version + " started on port " + config.Port + " (" + Platform.Name + ")");

            httpThread = new Thread(Serve) { IsBackground = true, Name = "http" };
            statsThread = new Thread(SampleLoop) { IsBackground = true, Name = "stats" };
            netThread = new Thread(NetworkLoop) { IsBackground = true, Name = "network" };
            httpThread.Start();
            statsThread.Start();
            netThread.Start();
        }

        public void Stop()
        {
            stopping = true;
            try { listener.Stop(); } catch (Exception) { }
            HotClone.FinishAll();
            stats.Save();
            Log.Write("agent stopped");
        }

        void SampleLoop()
        {
            DateTime lastSave = DateTime.UtcNow;
            while (!stopping)
            {
                Thread.Sleep(5000);
                try
                {
                    stats.Sample();
                    if ((DateTime.UtcNow - lastSave).TotalMinutes >= 10) { stats.Save(); lastSave = DateTime.UtcNow; }
                }
                catch (Exception ex) { Log.Write("sampling: " + ex.Message); }
            }
        }

        // On VMware: keep a copy of the network settings. Anywhere else (Proxmox):
        // put the saved static settings on the new network card(s).
        void NetworkLoop()
        {
            DateTime started = DateTime.UtcNow;
            Thread.Sleep(15000);
            while (!stopping)
            {
                try
                {
                    // A hot clone (different SMBIOS UUID than the machine it was copied
                    // from) restores the source's settings, also when it runs on VMware.
                    int clone = CloneMarker.IsClone();
                    if (clone == 1)
                    {
                        if (CloneMarker.RestoreNetwork()) Network.Restore();
                        CloneMarker.ShrinkOnce();
                    }
                    else if (Platform.IsVmware) Network.SaveIfChanged();
                    else Network.Restore();
                }
                catch (Exception ex) { Log.Write("network: " + ex.Message); }
                // Every 20 s for the first 30 minutes after boot (drivers arrive late), then every 5 minutes.
                int wait = (DateTime.UtcNow - started).TotalMinutes < 30 ? 20000 : 300000;
                for (int i = 0; i < wait / 1000 && !stopping; i++) Thread.Sleep(1000);
            }
        }

        void Serve()
        {
            while (!stopping)
            {
                HttpListenerContext ctx;
                try { ctx = listener.GetContext(); }
                catch (Exception) { if (stopping) return; Thread.Sleep(500); continue; }
                ThreadPool.QueueUserWorkItem(o => Handle((HttpListenerContext)o), ctx);
            }
        }

        void Handle(HttpListenerContext ctx)
        {
            string nonce = null;
            int status = 200;
            string body;
            try
            {
                string path = ctx.Request.Url.AbsolutePath.TrimEnd('/').ToLowerInvariant();
                if (path == "/clone-stream")
                {
                    ServeCloneStream(ctx);
                    return;
                }
                if (path == "/clone-files")
                {
                    ServeCloneFiles(ctx);
                    return;
                }
                if (path == "/hello")
                {
                    nonce = NewNonce();
                    body = Json.Write(new Dictionary<string, object>
                    {
                        { "agent", "mtaalam-v2v" }, { "version", 2 }, { "agent_version", Program.Version },
                        { "hostname", Environment.MachineName }, { "nonce", nonce },
                    });
                    nonce = null;   // hello is not signed
                }
                else
                {
                    string request;
                    using (var r = new StreamReader(ctx.Request.InputStream, Encoding.UTF8)) request = r.ReadToEnd();
                    nonce = ctx.Request.Headers["X-Mtaalam-Nonce"] ?? "";
                    string sig = ctx.Request.Headers["X-Mtaalam-Signature"] ?? "";
                    if (ctx.Request.HttpMethod != "POST" || !UseNonce(nonce) || !SigEquals(Sign(nonce + "\n" + path + "\n" + request), sig))
                    {
                        Log.Write("rejected " + path + " from " + ctx.Request.RemoteEndPoint + " (bad signature)");
                        nonce = null;
                        status = 403;
                        body = "{\"error\":\"Wrong pairing key\"}";
                    }
                    else
                    {
                        var args = Json.Parse(request.Length > 0 ? request : "{}") as Dictionary<string, object> ?? new Dictionary<string, object>();
                        body = Json.Write(Dispatch(path, args, ctx.Request.RemoteEndPoint.ToString()));
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("request failed: " + ex);
                status = 500;
                body = Json.Write(new Dictionary<string, object> { { "error", ex.Message } });
            }
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(body);
                ctx.Response.StatusCode = status;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                if (nonce != null) ctx.Response.Headers["X-Mtaalam-Signature"] = Sign(nonce + "\n" + body);
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                ctx.Response.Close();
            }
            catch (Exception) { }
        }

        object Dispatch(string path, Dictionary<string, object> args, string from)
        {
            switch (path)
            {
                case "/info":
                    return Info();
                case "/shrink":
                    lock (shrinkGate)
                    {
                        int disk = (int)Json.Num(args, "disk", -1);
                        long size = (long)Json.Num(args, "size", 0);
                        bool removeRecovery = Json.Bool(args, "remove_recovery");
                        Log.Write("shrink disk " + disk + " to " + size + " bytes requested from " + from);
                        return Shrinker.Shrink(disk, size, removeRecovery);
                    }
                case "/network-save":
                    Log.Write("network settings saved on request from " + from);
                    return new Dictionary<string, object> { { "ok", true }, { "network", Network.Save() } };
                case "/shutdown":
                    // Clean shutdown for the move (VMs without VMware Tools would otherwise be powered off hard).
                    Log.Write("shutdown requested from " + from);
                    try { if (Platform.IsVmware) Network.Save(); }
                    catch (Exception ex) { Log.Write("saving network settings before shutdown: " + ex.Message); }
                    stats.Save();
                    Process.Start(new ProcessStartInfo("shutdown.exe", "/s /t 5 /d p:4:1 /c \"Mtaalam V2V Converter: moving this VM to Proxmox VE\"")
                        { CreateNoWindow = true, UseShellExecute = false });
                    return new Dictionary<string, object> { { "ok", true } };
                case "/clone-prepare":
                {
                    var disks = new List<int>();
                    foreach (object o in Json.List(args, "disks")) disks.Add(Convert.ToInt32(o, CultureInfo.InvariantCulture));
                    string target = args.ContainsKey("target") ? args["target"] as string ?? "kvm" : "kvm";
                    bool restoreNet = !args.ContainsKey("restore_network") || Json.Bool(args, "restore_network");
                    Log.Write("hot clone of disk(s) " + string.Join(",", disks.ConvertAll(d => d.ToString()).ToArray()) + " for " + target + " requested from " + from);
                    var shrink = args.ContainsKey("shrink") ? args["shrink"] as Dictionary<string, object> : null;
                    return HotClone.Prepare(disks, target, restoreNet, shrink);
                }
                case "/clone-finish":
                    Log.Write("hot clone finished (" + from + ")");
                    HotClone.Finish(args.ContainsKey("session") ? args["session"] as string ?? "" : "");
                    return new Dictionary<string, object> { { "ok", true } };
                case "/uninstall":
                    Log.Write("uninstall requested from " + from);
                    ThreadPool.QueueUserWorkItem(o =>
                    {
                        Thread.Sleep(1500);
                        var psi = new ProcessStartInfo("cmd.exe",
                            "/c sc stop " + Program.ServiceName + " & ping -n 4 127.0.0.1 >nul & sc delete " + Program.ServiceName +
                            " & netsh advfirewall firewall delete rule name=\"Mtaalam V2V Agent\" & rd /s /q \"" + Program.InstallDir + "\"")
                        { CreateNoWindow = true, UseShellExecute = false };
                        Process.Start(psi);
                    });
                    return new Dictionary<string, object> { { "ok", true } };
                default:
                    throw new Exception("unknown request " + path);
            }
        }

        Dictionary<string, object> Info()
        {
            var info = new Dictionary<string, object>();
            info["agent"] = "mtaalam-v2v";
            info["version"] = 2;
            info["agent_version"] = Program.Version;
            info["hostname"] = Environment.MachineName;
            info["os"] = Platform.OsName;
            info["platform"] = Platform.Name;
            info["time"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            info["cpus"] = Environment.ProcessorCount;
            info["os_version"] = Environment.OSVersion.Version.ToString();
            info["firmware"] = Platform.Firmware;
            info["uuid"] = Platform.Uuid;
            info["system_disk"] = Platform.SystemDisk;
            info["hot_clone"] = true;
            info["agent_version"] = Program.Version;
            info["virtio_ready"] = Drivers.Installed("vioscsi") && Drivers.Installed("netkvm");
            info["file_copy"] = FileCopy.Available;
            stats.Fill(info);
            var disks = Disks.List();
            var dl = new List<object>();
            foreach (var d in disks) dl.Add(d.ToJson());
            info["disks"] = dl;
            var net = Network.Capture();
            var macs = new List<object>();
            foreach (var n in net) macs.Add(n["mac"]);
            info["macs"] = macs;
            info["network"] = net;
            info["network_saved"] = Network.SavedTime();
            return info;
        }

        // GET /clone-stream?s=session&d=disk&n=nonce[&x=offsets]&sig=HMAC("clone-stream\n" + s + "\n" + d + "\n" + n [+ "\n" + x])
        // The converter hands this URL to the target (Proxmox node or the converter itself);
        // the disk image is AES-256-CTR encrypted with a key derived from the pairing key.
        // x = partitions (byte offsets, comma separated, or * for all) whose contents travel
        // file by file (/clone-files) instead: only their first 8 KiB (boot sector) are sent.
        void ServeCloneStream(HttpListenerContext ctx)
        {
            var q = ctx.Request.QueryString;
            string s = q["s"] ?? "", d = q["d"] ?? "", n = q["n"] ?? "", sig = q["sig"] ?? "", x = q["x"] ?? "";
            int disk;
            CloneSession session = HotClone.Get(s);
            if (session == null || n.Length < 16 || !int.TryParse(d, out disk) ||
                !SigEquals(Sign("clone-stream\n" + s + "\n" + d + "\n" + n + (x.Length > 0 ? "\n" + x : "")), sig))
            {
                Log.Write("rejected clone stream request from " + ctx.Request.RemoteEndPoint);
                try { ctx.Response.StatusCode = 403; ctx.Response.Close(); } catch (Exception) { }
                return;
            }
            var headOnly = new List<long>();
            bool allHeads = x == "*";
            if (!allHeads)
                foreach (string o in x.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                    headOnly.Add(long.Parse(o, CultureInfo.InvariantCulture));
            byte[] aesKey, iv;
            StreamKey(s, d, n, out aesKey, out iv);
            Log.Write("streaming disk " + disk + " to " + ctx.Request.RemoteEndPoint +
                      (allHeads ? " (partition table and boot sectors only)" : headOnly.Count > 0 ? " (without the volumes copied file by file)" : ""));
            try
            {
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/octet-stream";
                ctx.Response.SendChunked = true;
                // http.sys sends every Write as its own chunk: buffer, or each 16-byte record
                // header becomes a network round trip (the first live copy ran at ~2 MB/s).
                var started = DateTime.UtcNow;
                using (var ctr = new AesCtr(aesKey, iv))
                using (var buffered = new BufferedStream(ctx.Response.OutputStream, 4 << 20))
                {
                    long sent = HotClone.Stream(session, disk, buffered, ctr, headOnly, allHeads);
                    buffered.Flush();
                    double secs = Math.Max(1, (DateTime.UtcNow - started).TotalSeconds);
                    Log.Write("disk " + disk + " streamed: " + sent + " bytes of data in " + (long)secs + " s (" +
                              (sent / secs / (1 << 20)).ToString("0.0", CultureInfo.InvariantCulture) + " MB/s)");
                }
                ctx.Response.Close();
            }
            catch (Exception ex)
            {
                Log.Write("streaming disk " + disk + " stopped: " + ex.Message);
                try { ctx.Response.Abort(); } catch (Exception) { }
            }
        }

        void StreamKey(string s, string id, string n, out byte[] aesKey, out byte[] iv)
        {
            using (var h = new HMACSHA256(key))
            {
                aesKey = h.ComputeHash(Encoding.UTF8.GetBytes("mtv2v-stream-key\n" + s + "\n" + id + "\n" + n));
                byte[] ivFull = h.ComputeHash(Encoding.UTF8.GetBytes("mtv2v-stream-iv\n" + s + "\n" + id + "\n" + n));
                iv = new byte[16];
                Array.Copy(ivFull, iv, 16);
            }
        }

        // GET /clone-files?s=session&d=disk&o=offset&n=nonce&sig=HMAC("clone-files\n" + s + "\n" + d + "\n" + o + "\n" + n)
        // The files of one volume, read from its VSS snapshot, as a pipable WIM (wimlib), AES-256-CTR
        // encrypted. The Proxmox host writes them into a new NTFS file system of the chosen size, so a
        // smaller volume never needs ntfsresize (VMware Converter also copies resized volumes file by file).
        void ServeCloneFiles(HttpListenerContext ctx)
        {
            var q = ctx.Request.QueryString;
            string s = q["s"] ?? "", d = q["d"] ?? "", o = q["o"] ?? "", n = q["n"] ?? "", sig = q["sig"] ?? "";
            int disk;
            long offset;
            CloneSession session = HotClone.Get(s);
            if (session == null || n.Length < 16 || !int.TryParse(d, out disk) || !long.TryParse(o, out offset) ||
                !SigEquals(Sign("clone-files\n" + s + "\n" + d + "\n" + o + "\n" + n), sig))
            {
                Log.Write("rejected file copy request from " + ctx.Request.RemoteEndPoint);
                try { ctx.Response.StatusCode = 403; ctx.Response.Close(); } catch (Exception) { }
                return;
            }
            byte[] aesKey, iv;
            StreamKey(s, d + "@" + o, n, out aesKey, out iv);
            try
            {
                string device, volume, name;
                HotClone.VolumeAt(session, disk, offset, out device, out volume, out name);
                Log.Write("copying the files of " + name + " to " + ctx.Request.RemoteEndPoint + " (from " + device + ")");
                List<string> special = FileCopy.FindSpecial(device, name);
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/octet-stream";
                ctx.Response.SendChunked = true;
                if (special.Count > 0) ctx.Response.AddHeader("X-Mtv2v-Special-Files", special.Count.ToString(CultureInfo.InvariantCulture));
                using (var ctr = new AesCtr(aesKey, iv))
                using (var buffered = new BufferedStream(ctx.Response.OutputStream, 4 << 20))
                {
                    FileCopy.Capture(device, volume, name, special, buffered, ctr);
                    buffered.Flush();
                }
                ctx.Response.Close();
            }
            catch (Exception ex)
            {
                Log.Write("file copy of disk " + disk + " at " + offset + " stopped: " + ex.Message);
                try { ctx.Response.Abort(); } catch (Exception) { }
            }
        }

        // ----- request signing (HMAC-SHA256 over a one-time nonce from /hello)

        string NewNonce()
        {
            var b = new byte[16];
            new RNGCryptoServiceProvider().GetBytes(b);
            string n = Hex(b);
            lock (nonces)
            {
                var old = new List<string>();
                foreach (var kv in nonces) if (kv.Value < DateTime.UtcNow) old.Add(kv.Key);
                foreach (string o in old) nonces.Remove(o);
                if (nonces.Count > 1000) nonces.Clear();
                nonces[n] = DateTime.UtcNow.AddMinutes(2);
            }
            return n;
        }

        bool UseNonce(string n)
        {
            lock (nonces)
            {
                DateTime exp;
                if (!nonces.TryGetValue(n, out exp)) return false;
                nonces.Remove(n);
                return exp >= DateTime.UtcNow;
            }
        }

        string Sign(string text)
        {
            using (var h = new HMACSHA256(key)) return Hex(h.ComputeHash(Encoding.UTF8.GetBytes(text)));
        }

        static bool SigEquals(string a, string b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= char.ToLowerInvariant(a[i]) ^ char.ToLowerInvariant(b[i]);
            return diff == 0;
        }

        static string Hex(byte[] b)
        {
            var sb = new StringBuilder(b.Length * 2);
            foreach (byte x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }
    }

    // --------------------------------------------------------------- platform

    static class Platform
    {
        static string manufacturer;

        public static string Manufacturer
        {
            get
            {
                if (manufacturer != null) return manufacturer;
                string m = "";
                try
                {
                    using (var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS"))
                        if (k != null) m = (k.GetValue("SystemManufacturer") ?? "") + " " + (k.GetValue("SystemProductName") ?? "");
                }
                catch (Exception) { }
                if (m.Trim().Length == 0)
                    try
                    {
                        using (var s = new ManagementObjectSearcher("SELECT Manufacturer, Model FROM Win32_ComputerSystem"))
                            foreach (ManagementObject o in s.Get()) m = o["Manufacturer"] + " " + o["Model"];
                    }
                    catch (Exception) { }
                return manufacturer = m.Trim();
            }
        }

        public static bool IsVmware { get { return Manufacturer.IndexOf("VMware", StringComparison.OrdinalIgnoreCase) >= 0; } }

        public static string Name
        {
            get
            {
                string m = Manufacturer;
                if (IsVmware) return "vmware";
                if (Regex.IsMatch(m, "QEMU|Proxmox|KVM|Red Hat", RegexOptions.IgnoreCase)) return "kvm";
                if (m.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) >= 0) return "hyperv";
                return m.Length > 0 ? m : "unknown";
            }
        }

        static string uuid;

        // SMBIOS system UUID: changes when the disks boot in a new VM.
        public static string Uuid
        {
            get
            {
                if (uuid != null) return uuid;
                string u = "";
                try
                {
                    using (var s = new ManagementObjectSearcher("SELECT UUID FROM Win32_ComputerSystemProduct"))
                        foreach (ManagementObject o in s.Get()) u = (o["UUID"] as string ?? "").Trim().ToUpperInvariant();
                }
                catch (Exception) { }
                return uuid = u;
            }
        }

        // "efi" or "bios" - the firmware Windows booted with.
        public static string Firmware
        {
            get
            {
                try
                {
                    using (var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control"))
                    {
                        object v = k != null ? k.GetValue("PEFirmwareType") : null;   // Windows 8 / 2012 and newer
                        if (v is int) return (int)v == 2 ? "efi" : "bios";
                    }
                }
                catch (Exception) { }
                try
                {
                    int disk = SystemDisk;
                    if (disk >= 0)
                        foreach (var d in Disks.List())
                            if (d.Number == disk)
                                foreach (var p in d.Partitions) if (p.Kind == "system") return "efi";
                }
                catch (Exception) { }
                return "bios";
            }
        }

        // Number of the disk that holds the Windows volume, -1 if unknown.
        public static int SystemDisk
        {
            get
            {
                string letter = (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:").Substring(0, 2).ToUpperInvariant();
                try
                {
                    foreach (var d in Disks.List())
                        foreach (var p in d.Partitions)
                            if (p.Letter.ToUpperInvariant() == letter) return d.Number;
                }
                catch (Exception) { }
                return -1;
            }
        }

        public static string OsName
        {
            get
            {
                try
                {
                    using (var s = new ManagementObjectSearcher("SELECT Caption, CSDVersion FROM Win32_OperatingSystem"))
                        foreach (ManagementObject o in s.Get())
                            return ((o["Caption"] ?? "") + " " + (o["CSDVersion"] ?? "")).Trim();
                }
                catch (Exception) { }
                return Environment.OSVersion.ToString();
            }
        }
    }

    // ------------------------------------------------------------------ stats

    // CPU and memory measured every 5 s, kept as one-minute averages for 30 days.
    class Stats
    {
        [StructLayout(LayoutKind.Sequential)]
        struct MEMORYSTATUSEX
        {
            public uint dwLength, dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

        const int MaxMinutes = 30 * 24 * 60;
        readonly object gate = new object();
        readonly List<float[]> minutes = new List<float[]>();   // cpu %, memory used MB
        long lastIdle = -1, lastTotal;
        double cpuSum, memSum;
        int count;
        DateTime started = DateTime.UtcNow;

        static string FilePath { get { return Path.Combine(Program.InstallDir, "stats.dat"); } }

        public static long MemTotalMb()
        {
            var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)) };
            return GlobalMemoryStatusEx(ref m) ? (long)(m.ullTotalPhys >> 20) : -1;
        }

        public void Sample()
        {
            long idle, kernel, user;
            if (!GetSystemTimes(out idle, out kernel, out user)) return;
            long total = kernel + user;   // kernel time includes idle time
            var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)) };
            GlobalMemoryStatusEx(ref m);
            double memUsed = (double)(m.ullTotalPhys - m.ullAvailPhys) / (1 << 20);
            lock (gate)
            {
                if (lastIdle >= 0 && total > lastTotal)
                {
                    double cpu = 100.0 * (1 - (double)(idle - lastIdle) / (total - lastTotal));
                    cpuSum += Math.Max(0, Math.Min(100, cpu));
                    memSum += memUsed;
                    if (++count >= 12)
                    {
                        minutes.Add(new[] { (float)(cpuSum / count), (float)(memSum / count) });
                        if (minutes.Count > MaxMinutes) minutes.RemoveRange(0, minutes.Count - MaxMinutes);
                        cpuSum = memSum = 0;
                        count = 0;
                    }
                }
                lastIdle = idle;
                lastTotal = total;
            }
        }

        public void Fill(Dictionary<string, object> info)
        {
            lock (gate)
            {
                var list = new List<float[]>(minutes);
                if (count > 0) list.Add(new[] { (float)(cpuSum / count), (float)(memSum / count) });
                info["mem_total_mb"] = MemTotalMb();
                info["seconds"] = minutes.Count * 60 + count * 5;
                info["since"] = started.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
                if (list.Count == 0)
                {
                    info["cpu_avg"] = -1; info["cpu_peak"] = -1; info["cpu_p95"] = -1;
                    info["mem_used_peak_mb"] = -1; info["mem_used_p95_mb"] = -1;
                    return;
                }
                var cpu = new List<float>(); var mem = new List<float>();
                double cpuTotal = 0;
                foreach (var s in list) { cpu.Add(s[0]); mem.Add(s[1]); cpuTotal += s[0]; }
                cpu.Sort(); mem.Sort();
                info["cpu_avg"] = Math.Round(cpuTotal / list.Count, 1);
                info["cpu_peak"] = Math.Round(cpu[cpu.Count - 1], 1);
                info["cpu_p95"] = Math.Round(cpu[(int)((cpu.Count - 1) * 0.95)], 1);
                info["mem_used_peak_mb"] = (long)mem[mem.Count - 1];
                info["mem_used_p95_mb"] = (long)mem[(int)((mem.Count - 1) * 0.95)];
            }
        }

        public void Save()
        {
            try
            {
                var sb = new StringBuilder();
                lock (gate)
                {
                    sb.Append("since ").Append(started.Ticks).Append('\n');
                    foreach (var s in minutes)
                        sb.Append(s[0].ToString("0.#", CultureInfo.InvariantCulture)).Append(' ')
                          .Append(s[1].ToString("0", CultureInfo.InvariantCulture)).Append('\n');
                }
                File.WriteAllText(FilePath + ".tmp", sb.ToString());
                if (File.Exists(FilePath)) File.Delete(FilePath);
                File.Move(FilePath + ".tmp", FilePath);
            }
            catch (Exception ex) { Log.Write("saving stats: " + ex.Message); }
        }

        public void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                // Measurements from the old hypervisor are kept: they describe the workload.
                foreach (string line in File.ReadAllLines(FilePath))
                {
                    string[] p = line.Split(' ');
                    if (p.Length != 2) continue;
                    if (p[0] == "since") { started = new DateTime(long.Parse(p[1]), DateTimeKind.Utc); continue; }
                    float c, m;
                    if (float.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out c) &&
                        float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out m))
                        minutes.Add(new[] { c, m });
                }
                if (minutes.Count > MaxMinutes) minutes.RemoveRange(0, minutes.Count - MaxMinutes);
            }
            catch (Exception ex) { Log.Write("loading stats: " + ex.Message); }
        }
    }

    // ------------------------------------------------------------------ disks

    class Partition
    {
        public int Number;
        public long Offset, Length;
        public string Kind = "";        // data, system, msr, recovery, extended, dynamic, other
        public string Type = "";        // GPT type GUID or MBR type byte
        public string Letter = "", FileSystem = "", Label = "", Volume = "";
        public long VolumeSize = -1, Free = -1;
        public long End { get { return Offset + Length; } }

        public Dictionary<string, object> ToJson()
        {
            return new Dictionary<string, object>
            {
                { "number", Number }, { "offset", Offset }, { "size", Length }, { "kind", Kind }, { "type", Type },
                { "letter", Letter }, { "fs", FileSystem }, { "label", Label },
                { "used", VolumeSize >= 0 ? VolumeSize - Free : -1 }, { "free", Free },
            };
        }
    }

    class DiskInfo
    {
        public int Number;
        public long Size;
        public string Style = "";       // mbr, gpt, raw
        public readonly List<Partition> Partitions = new List<Partition>();

        public long PartEnd
        {
            get
            {
                long end = 0;
                foreach (var p in Partitions) if (p.End > end) end = p.End;
                return end;
            }
        }

        public long Used
        {
            get
            {
                long used = 0;
                foreach (var p in Partitions)
                {
                    if (p.Kind == "extended") continue;
                    used += p.VolumeSize > 0 ? p.VolumeSize - p.Free : p.Length;
                }
                return used;
            }
        }

        public Dictionary<string, object> ToJson()
        {
            var parts = new List<object>();
            foreach (var p in Partitions) parts.Add(p.ToJson());
            long end = PartEnd;
            return new Dictionary<string, object>
            {
                { "name", "Disk " + Number }, { "number", Number }, { "size", Size }, { "style", Style },
                { "used", Used },
                // +1 MiB keeps room for the backup GPT behind the last partition
                { "part_end", end > 0 ? end + (1L << 20) : 0 },
                { "partitions", parts },
            };
        }
    }

    static class Disks
    {
        const uint GENERIC_READ = 0x80000000, FILE_SHARE_RW = 3, OPEN_EXISTING = 3;
        const uint IOCTL_DISK_GET_DRIVE_GEOMETRY = 0x70000, IOCTL_DISK_GET_DRIVE_LAYOUT_EX = 0x70050,
                   IOCTL_DISK_GET_LENGTH_INFO = 0x7405C, IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x560000;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(IntPtr h, uint code, IntPtr inBuf, int inSize, IntPtr outBuf, int outSize, out int returned, IntPtr overlapped);

        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr h);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr FindFirstVolume(StringBuilder name, int size);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool FindNextVolume(IntPtr h, StringBuilder name, int size);

        [DllImport("kernel32.dll")]
        static extern bool FindVolumeClose(IntPtr h);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool GetVolumePathNamesForVolumeName(string volume, char[] names, int size, out int returned);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool GetDiskFreeSpaceEx(string path, out long freeToCaller, out long total, out long free);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool GetVolumeInformation(string root, StringBuilder label, int labelSize, out uint serial, out uint maxComponent,
            out uint flags, StringBuilder fs, int fsSize);

        static readonly IntPtr Invalid = new IntPtr(-1);

        public static List<DiskInfo> List()
        {
            var disks = new List<DiskInfo>();
            for (int n = 0; n < 64; n++)
            {
                var d = Read(n);
                if (d != null) disks.Add(d);
            }
            AttachVolumes(disks);
            return disks;
        }

        public static DiskInfo Get(int number)
        {
            var d = Read(number);
            if (d == null) throw new Exception("Disk " + number + " not found");
            AttachVolumes(new List<DiskInfo> { d });
            return d;
        }

        static DiskInfo Read(int n)
        {
            IntPtr h = CreateFile(@"\\.\PhysicalDrive" + n, GENERIC_READ, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h == Invalid) return null;
            IntPtr buf = Marshal.AllocHGlobal(65536);
            try
            {
                int ret;
                // skip removable media (USB sticks, card readers)
                if (DeviceIoControl(h, IOCTL_DISK_GET_DRIVE_GEOMETRY, IntPtr.Zero, 0, buf, 65536, out ret, IntPtr.Zero) &&
                    Marshal.ReadInt32(buf, 8) == 11) return null;
                if (!DeviceIoControl(h, IOCTL_DISK_GET_LENGTH_INFO, IntPtr.Zero, 0, buf, 65536, out ret, IntPtr.Zero)) return null;
                var disk = new DiskInfo { Number = n, Size = Marshal.ReadInt64(buf) };
                if (!DeviceIoControl(h, IOCTL_DISK_GET_DRIVE_LAYOUT_EX, IntPtr.Zero, 0, buf, 65536, out ret, IntPtr.Zero)) return disk;
                int style = Marshal.ReadInt32(buf, 0), count = Marshal.ReadInt32(buf, 4);
                disk.Style = style == 0 ? "mbr" : style == 1 ? "gpt" : "raw";
                for (int i = 0; i < count; i++)
                {
                    int o = 48 + i * 144;
                    if (o + 144 > ret) break;
                    var p = new Partition
                    {
                        Offset = Marshal.ReadInt64(buf, o + 8),
                        Length = Marshal.ReadInt64(buf, o + 16),
                        Number = Marshal.ReadInt32(buf, o + 24),
                    };
                    if (p.Length <= 0) continue;
                    if (Marshal.ReadInt32(buf, o) == 0)
                    {
                        byte type = Marshal.ReadByte(buf, o + 32);
                        if (type == 0) continue;
                        p.Type = "0x" + type.ToString("x2");
                        p.Kind = type == 0x05 || type == 0x0f || type == 0x85 ? "extended"
                               : type == 0x27 ? "recovery"
                               : type == 0x42 ? "dynamic"
                               : type == 0x07 || type == 0x06 || type == 0x0b || type == 0x0c || type == 0x0e ? "data" : "other";
                    }
                    else
                    {
                        var g = new byte[16];
                        Marshal.Copy(new IntPtr(buf.ToInt64() + o + 32), g, 0, 16);
                        var guid = new Guid(g);
                        p.Type = guid.ToString();
                        switch (p.Type)
                        {
                            case "ebd0a0a2-b9e5-4433-87c0-68b6b72699c7": p.Kind = "data"; break;
                            case "c12a7328-f81f-11d2-ba4b-00a0c93ec93b": p.Kind = "system"; break;
                            case "e3c9e316-0b5c-4db8-817d-f92df00215ae": p.Kind = "msr"; break;
                            case "de94bba4-06d1-4d40-a16a-bfd50179d6ac": p.Kind = "recovery"; break;
                            case "5808c8aa-7e8f-42e0-85d2-e1e90434cfb3":
                            case "af9b60a0-1431-4f62-bc68-3311714a69ad": p.Kind = "dynamic"; break;
                            default: p.Kind = "other"; break;
                        }
                    }
                    disk.Partitions.Add(p);
                }
                disk.Partitions.Sort((a, b) => a.Offset.CompareTo(b.Offset));
                return disk;
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
                CloseHandle(h);
            }
        }

        // Matches every mounted volume to its partition (disk number + offset).
        static void AttachVolumes(List<DiskInfo> disks)
        {
            var name = new StringBuilder(1024);
            IntPtr find = FindFirstVolume(name, name.Capacity);
            if (find == Invalid) return;
            IntPtr buf = Marshal.AllocHGlobal(4096);
            try
            {
                do
                {
                    string vol = name.ToString();   // \\?\Volume{guid}\
                    IntPtr h = CreateFile(vol.TrimEnd('\\'), 0, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                    if (h == Invalid) continue;
                    int ret;
                    bool ok = DeviceIoControl(h, IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, IntPtr.Zero, 0, buf, 4096, out ret, IntPtr.Zero);
                    CloseHandle(h);
                    if (!ok || Marshal.ReadInt32(buf, 0) != 1) continue;   // spanned volumes: skip
                    int diskNo = Marshal.ReadInt32(buf, 8);
                    long offset = Marshal.ReadInt64(buf, 16);
                    Partition part = null;
                    foreach (var d in disks)
                        if (d.Number == diskNo)
                            foreach (var p in d.Partitions)
                                if (p.Offset == offset) part = p;
                    if (part == null) continue;
                    part.Volume = vol;
                    var paths = new char[1024];
                    int len;
                    if (GetVolumePathNamesForVolumeName(vol, paths, paths.Length, out len))
                        foreach (string mp in new string(paths, 0, Math.Max(0, len)).Split('\0'))
                            if (mp.Length == 3 && mp[1] == ':') { part.Letter = mp.Substring(0, 2); break; }
                    long freeCaller, total, free;
                    if (GetDiskFreeSpaceEx(vol, out freeCaller, out total, out free)) { part.VolumeSize = total; part.Free = free; }
                    var label = new StringBuilder(261);
                    var fs = new StringBuilder(261);
                    uint serial, maxc, flags;
                    if (GetVolumeInformation(vol, label, label.Capacity, out serial, out maxc, out flags, fs, fs.Capacity))
                    {
                        part.Label = label.ToString();
                        part.FileSystem = fs.ToString();
                    }
                } while (FindNextVolume(find, name, name.Capacity));
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
                FindVolumeClose(find);
            }
        }
    }

    // ---------------------------------------------------------------- shrink

    static class Shrinker
    {
        const long MiB = 1L << 20;

        // Shrinks the last volume on the disk so every partition ends within
        // `size` bytes (minus 1 MiB for the backup GPT). Returns the result for the converter.
        public static Dictionary<string, object> Shrink(int number, long size, bool removeRecovery)
        {
            var disk = Disks.Get(number);
            long limit = (size - MiB) / MiB * MiB;   // where the last partition may end
            var result = new Dictionary<string, object>();
            if (size <= 0 || limit <= 0) return Fail(result, disk, "invalid size");
            if (disk.PartEnd <= limit) return Done(result, disk, "Disk " + number + " already fits in " + Gb(size) + ".");

            Partition last = Last(disk);
            if (last.Kind == "recovery" && Before(disk, last) != null && Before(disk, last).Kind == "data")
            {
                if (!removeRecovery)
                {
                    result["code"] = "recovery";
                    return Fail(result, disk, "A Windows recovery partition (" + Gb(last.Length) + ") sits at the end of disk " + number +
                        ", behind " + Describe(Before(disk, last)) + ", so the disk cannot get smaller. Allow the agent to move the " +
                        "recovery environment (WinRE) into the Windows folder and delete that partition.");
                }
                RemoveRecovery(disk, last);
                disk = Disks.Get(number);
                if (disk.PartEnd <= limit) return Done(result, disk, "Recovery partition removed; disk " + number + " now fits in " + Gb(size) + ".");
                last = Last(disk);
            }
            if (last.Kind == "extended")
                return Fail(result, disk, "Disk " + number + " ends with an extended partition (logical drives); shrink and move it with a partition tool first.");
            if (last.Kind == "dynamic")
                return Fail(result, disk, "Disk " + number + " is a dynamic disk; it cannot be shrunk by the agent.");
            if (last.Volume.Length == 0)
                return Fail(result, disk, "The last partition on disk " + number + " (" + last.Kind + ", " + Gb(last.Length) + ") has no volume the agent can shrink.");
            if (!last.FileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
                return Fail(result, disk, Describe(last) + " uses " + (last.FileSystem.Length > 0 ? last.FileSystem : "an unknown file system") +
                    "; Windows can only shrink NTFS volumes.");

            long newLength = limit - last.Offset;
            if (newLength < 64 * MiB) return Fail(result, disk, "The requested size leaves no room for " + Describe(last) + ".");

            long minLength;
            string error = Resize(disk, last, newLength, out minLength);
            if (error != null)
            {
                if (minLength > 0)
                {
                    long minSize = (last.Offset + minLength + MiB + MiB - 1) / MiB * MiB + MiB;
                    result["min_size"] = minSize;
                    error += " The smallest possible disk size right now is " + Gb(minSize) + ".";
                }
                return Fail(result, disk, error);
            }
            disk = Disks.Get(number);
            Log.Write("shrunk " + Describe(last) + " on disk " + number + " to " + newLength + " bytes");
            return Done(result, disk, Describe(last) + " shrunk to " + Gb(newLength) + "; disk " + number + " now fits in " + Gb(size) + ".");
        }

        // Shrinks the volume with this drive letter to `target` bytes, or as far as Windows
        // can move its files. Returns a one-line result.
        public static string ShrinkVolume(string letter, long target)
        {
            letter = letter.Substring(0, 1).ToUpperInvariant() + ":";
            foreach (var d in Disks.List())
                foreach (var p in d.Partitions)
                {
                    if (!p.Letter.TrimEnd('\\').Equals(letter, StringComparison.OrdinalIgnoreCase)) continue;
                    target = (target + MiB - 1) / MiB * MiB;
                    if (p.Length <= target) return "already " + Gb(p.Length);
                    long min;
                    string error = Resize(d, p, target, out min);
                    if (error == null) return "shrunk from " + Gb(p.Length) + " to " + Gb(target);
                    if (min > 0 && min < p.Length)
                    {
                        long next = Math.Min(p.Length, (min + (1L << 30) - 1) / (1L << 30) * (1L << 30) + (1L << 30));
                        if (next < p.Length && Resize(d, p, next, out min) == null)
                            return "shrunk from " + Gb(p.Length) + " to " + Gb(next) + " (as far as Windows could move the files; " + Gb(target) + " was asked)";
                    }
                    return "not shrunk: " + error;
                }
            return "not shrunk: no volume " + letter;
        }

        static Partition Last(DiskInfo d)
        {
            Partition last = null;
            foreach (var p in d.Partitions) if (last == null || p.End > last.End) last = p;
            if (last == null) throw new Exception("Disk " + d.Number + " has no partitions");
            return last;
        }

        static Partition Before(DiskInfo d, Partition p)
        {
            Partition best = null;
            foreach (var q in d.Partitions)
                if (q != p && q.Kind != "extended" && q.End <= p.Offset && (best == null || q.End > best.End)) best = q;
            return best;
        }

        // Windows 8 / 2012 and newer: the Storage WMI provider. Older: diskpart.
        static string Resize(DiskInfo disk, Partition part, long newLength, out long minLength)
        {
            minLength = 0;
            ManagementObject msft = null;
            try
            {
                var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
                scope.Connect();
                using (var s = new ManagementObjectSearcher(scope, new ObjectQuery(
                    "SELECT * FROM MSFT_Partition WHERE DiskNumber=" + disk.Number + " AND Offset=" + part.Offset)))
                    foreach (ManagementObject o in s.Get()) msft = o;
            }
            catch (Exception) { msft = null; }

            if (msft != null)
            {
                var sup = msft.InvokeMethod("GetSupportedSize", null, null);
                uint rc = Convert.ToUInt32(sup["ReturnValue"]);
                if (rc != 0) return "Windows could not work out how far " + Describe(part) + " can shrink (error " + rc + ").";
                minLength = Convert.ToInt64(sup["SizeMin"]);
                if (newLength < minLength)
                    return Describe(part) + " can only shrink to " + Gb(minLength) + " (" + Gb(part.VolumeSize - part.Free) + " used): files that " +
                        "cannot be moved (page file, hibernation file, shadow copies, MFT) sit near its end.";
                var inp = msft.GetMethodParameters("Resize");
                inp["Size"] = (ulong)newLength;
                var outp = msft.InvokeMethod("Resize", inp, null);
                rc = Convert.ToUInt32(outp["ReturnValue"]);
                if (rc != 0)
                {
                    object status = null;
                    try { status = outp["ExtendedStatus"]; } catch (Exception) { }
                    return "Windows refused to shrink " + Describe(part) + " (error " + rc + (status != null ? ", " + status : "") + ").";
                }
                return null;
            }

            // diskpart: shrink is given as the amount to give back, in MB.
            string select = part.Letter.Length > 0
                ? "select volume " + part.Letter.Substring(0, 1)
                : "select disk " + disk.Number + "\r\nselect partition " + part.Number;
            string query = DiskPart(select + "\r\nshrink querymax");
            var m = Regex.Match(query, @"\((\d+)\s*MB\)", RegexOptions.IgnoreCase);
            long reduceMb = (part.Length - newLength + MiB - 1) / MiB;
            if (m.Success)
            {
                long maxMb = long.Parse(m.Groups[1].Value);
                minLength = part.Length - maxMb * MiB;
                if (reduceMb > maxMb)
                    return Describe(part) + " can only shrink to " + Gb(minLength) + ": files that cannot be moved (page file, " +
                        "hibernation file, shadow copies, MFT) sit near its end.";
            }
            string output = DiskPart(select + "\r\nshrink desired=" + reduceMb + " minimum=" + reduceMb);
            var after = Disks.Get(disk.Number);
            foreach (var p in after.Partitions)
                if (p.Offset == part.Offset && p.Length <= newLength) return null;
            return "diskpart could not shrink " + Describe(part) + ": " + LastLines(output);
        }

        // reagentc /disable copies WinRE into C:\Windows\System32\Recovery; after the
        // partition is deleted, /enable registers that copy.
        static void RemoveRecovery(DiskInfo disk, Partition rec)
        {
            Log.Write("moving WinRE off the recovery partition on disk " + disk.Number);
            Run("reagentc.exe", "/disable");
            string output = DiskPart("select disk " + disk.Number + "\r\nselect partition " + rec.Number + "\r\ndelete partition override");
            Run("reagentc.exe", "/enable");
            var after = Disks.Get(disk.Number);
            foreach (var p in after.Partitions)
                if (p.Offset == rec.Offset) throw new Exception("Could not delete the recovery partition: " + LastLines(output));
            Log.Write("recovery partition removed from disk " + disk.Number);
        }

        static string DiskPart(string script)
        {
            string file = Path.Combine(Path.GetTempPath(), "mtaalam-v2v-diskpart.txt");
            File.WriteAllText(file, script + "\r\nexit\r\n", Encoding.ASCII);
            try { return Run("diskpart.exe", "/s \"" + file + "\""); }
            finally { try { File.Delete(file); } catch (Exception) { } }
        }

        static string Run(string exe, string args)
        {
            var psi = new ProcessStartInfo(exe, args) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            using (var p = Process.Start(psi))
            {
                string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit();
                Log.Write(exe + " " + args + " -> " + p.ExitCode);
                return output;
            }
        }

        static string LastLines(string s)
        {
            var lines = new List<string>();
            foreach (string l in s.Split('\n')) if (l.Trim().Length > 0) lines.Add(l.Trim());
            return string.Join(" ", lines.GetRange(Math.Max(0, lines.Count - 3), Math.Min(3, lines.Count)).ToArray());
        }

        static Dictionary<string, object> Done(Dictionary<string, object> r, DiskInfo d, string message)
        {
            r["ok"] = true;
            r["message"] = message;
            r["disk"] = d.ToJson();
            return r;
        }

        static Dictionary<string, object> Fail(Dictionary<string, object> r, DiskInfo d, string error)
        {
            r["ok"] = false;
            r["error"] = error;
            r["disk"] = d.ToJson();
            return r;
        }

        static string Describe(Partition p)
        {
            return p.Letter.Length > 0 ? "volume " + p.Letter : "partition " + p.Number;
        }

        public static string Gb(long bytes)
        {
            return (bytes / (double)(1L << 30)).ToString("0.0", CultureInfo.InvariantCulture) + " GB";
        }
    }

    // -------------------------------------------------------------- hot clone
    //
    // Copies the disks of the running machine (physical or virtual) without
    // shutting it down, like VMware Converter's "powered-on machine" source:
    //   1. /clone-prepare enables the storage drivers the target boots from, saves the
    //      network settings and a clone marker, then takes a VSS snapshot of every
    //      NTFS/ReFS volume on the chosen disks;
    //   2. /clone-stream sends one disk as an encrypted stream of (offset, data)
    //      records: boot area and partition table, used clusters only of each
    //      snapshotted volume (page/hibernation file contents skipped), other
    //      partitions (EFI, recovery) raw, empty blocks never sent;
    //   3. /clone-finish deletes the snapshots.
    //
    // Stream format (before AES-256-CTR): "MTV2VDSK" u32 version=1 u32 0 u64 disk size,
    // then records: u64 offset, u32 length, u32 flags=0, data; ends with a record of length 0.

    class CloneSession
    {
        public string Id;
        public DateTime Expires;
        public string Target;
        public readonly Dictionary<int, DiskInfo> Disks = new Dictionary<int, DiskInfo>();
        public readonly Dictionary<string, string> ShadowIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, string> ShadowDevices = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, List<long[]>> Skip = new Dictionary<string, List<long[]>>(StringComparer.OrdinalIgnoreCase);
    }

    static class HotClone
    {
        const long MiB = 1L << 20;
        static readonly Dictionary<string, CloneSession> sessions = new Dictionary<string, CloneSession>();
        static readonly object gate = new object();

        static string LeftoverPath { get { return Path.Combine(Program.InstallDir, "snapshots.txt"); } }

        public static CloneSession Get(string id)
        {
            lock (gate)
            {
                CloneSession s;
                if (string.IsNullOrEmpty(id) || !sessions.TryGetValue(id, out s)) return null;
                if (s.Expires >= DateTime.UtcNow) return s;
                sessions.Remove(id);
                Release(s);
                return null;
            }
        }

        // A partition without a drive letter up to this size may be copied without a snapshot.
        const long MaxUnsnappedVolume = 2048 * MiB;

        public static Dictionary<string, object> Prepare(List<int> numbers, string target, bool restoreNetwork, Dictionary<string, object> shrink)
        {
            if (IntPtr.Size == 4 && Environment.GetEnvironmentVariable("PROCESSOR_ARCHITEW6432") != null)
                throw new Exception("The agent runs as a 32-bit process on 64-bit Windows; VSS snapshots need it to run as 64-bit.");
            lock (gate)
            {
                foreach (var old in new List<CloneSession>(sessions.Values))
                    if (old.Expires < DateTime.UtcNow) { sessions.Remove(old.Id); Release(old); }
            }
            if (numbers.Count == 0) foreach (var d in Disks.List()) numbers.Add(d.Number);

            bool virtioReady = Drivers.Prepare(target);
            try { Network.Save(); }
            catch (Exception ex) { Log.Write("saving network settings before the clone: " + ex.Message); }
            CloneMarker.Write(target, restoreNetwork, shrink);

            var b = new byte[12];
            new RNGCryptoServiceProvider().GetBytes(b);
            var session = new CloneSession { Expires = DateTime.UtcNow.AddHours(48), Target = target };
            var sb = new StringBuilder();
            foreach (byte x in b) sb.Append(x.ToString("x2"));
            session.Id = sb.ToString();

            var snapped = new List<object>();
            var unsnapped = new List<object>();
            try
            {
                foreach (int n in numbers)
                {
                    var d = Disks.Get(n);
                    session.Disks[n] = d;
                    foreach (var p in d.Partitions)
                    {
                        if (p.Volume.Length == 0 || session.ShadowIds.ContainsKey(p.Volume)) continue;
                        string fs = p.FileSystem.ToUpperInvariant();
                        if (fs != "NTFS" && fs != "REFS") continue;
                        string id = null, device = null;
                        try { Shadow.Create(p.Volume, p.Letter, out id, out device); }
                        catch (Exception ex)
                        {
                            // System Reserved and the like: no snapshot often fits on them. Stream()
                            // copies a partition without a snapshot block by block, as it does for
                            // EFI; nothing writes to these while Windows runs, apart from rare boot
                            // configuration changes. Volumes with a letter hold live data: they stop.
                            if (p.Letter.Length > 0 || p.Length > MaxUnsnappedVolume) throw;
                            if (id != null) try { Shadow.Delete(id); } catch (Exception) { }
                            Log.Write("no snapshot of " + p.Volume + ", copying it block by block: " + ex.Message);
                            unsnapped.Add("disk " + n + " partition " + p.Number + (p.Label.Length > 0 ? " (" + p.Label + ")" : ""));
                            continue;
                        }
                        session.ShadowIds[p.Volume] = id;
                        session.ShadowDevices[p.Volume] = device;
                        lock (gate) SaveLeftovers(session);
                        if (fs == "NTFS") session.Skip[p.Volume] = Volumes.SystemFileRuns(p.Volume);
                        snapped.Add(p.Letter.Length > 0 ? p.Letter : "disk " + n + " partition " + p.Number);
                        Log.Write("snapshot of " + (p.Letter.Length > 0 ? p.Letter : p.Volume) + ": " + device);
                    }
                }
            }
            catch (Exception)
            {
                Release(session);
                throw;
            }
            lock (gate) sessions[session.Id] = session;

            var disks = new List<object>();
            foreach (var d in session.Disks.Values) disks.Add(d.ToJson());
            return new Dictionary<string, object>
            {
                { "ok", true }, { "session", session.Id }, { "expires_hours", 48 }, { "disks", disks }, { "snapshots", snapped }, { "no_snapshot", unsnapped },
                { "firmware", Platform.Firmware }, { "uuid", Platform.Uuid }, { "os", Platform.OsName },
                { "os_version", Environment.OSVersion.Version.ToString() }, { "system_disk", Platform.SystemDisk },
                { "virtio_ready", virtioReady }, { "agent_version", Program.Version }, { "file_copy", FileCopy.Available },
            };
        }

        public static void Finish(string id)
        {
            CloneSession s;
            lock (gate)
            {
                if (!sessions.TryGetValue(id ?? "", out s)) return;
                sessions.Remove(id);
            }
            Release(s);
        }

        public static void FinishAll()
        {
            List<CloneSession> all;
            lock (gate) { all = new List<CloneSession>(sessions.Values); sessions.Clear(); }
            foreach (var s in all) Release(s);
        }

        static void Release(CloneSession s)
        {
            foreach (string id in s.ShadowIds.Values)
            {
                try { Shadow.Delete(id); Log.Write("snapshot " + id + " deleted"); }
                catch (Exception ex) { Log.Write("could not delete snapshot " + id + ": " + ex.Message); }
            }
            s.ShadowIds.Clear();
            lock (gate) SaveLeftovers(null);
        }

        // Snapshot IDs survive an agent crash or reboot in snapshots.txt and are deleted at the next start.
        static void SaveLeftovers(CloneSession pending)
        {
            try
            {
                var sb = new StringBuilder();
                foreach (var s in sessions.Values) foreach (string id in s.ShadowIds.Values) sb.Append(id).Append("\r\n");
                if (pending != null && !sessions.ContainsKey(pending.Id ?? ""))
                    foreach (string id in pending.ShadowIds.Values) sb.Append(id).Append("\r\n");
                File.WriteAllText(LeftoverPath, sb.ToString());
            }
            catch (Exception) { }
        }

        public static void CleanupLeftovers()
        {
            try
            {
                if (!File.Exists(LeftoverPath)) return;
                foreach (string line in File.ReadAllLines(LeftoverPath))
                {
                    string id = line.Trim();
                    if (id.Length == 0) continue;
                    try { Shadow.Delete(id); Log.Write("deleted leftover snapshot " + id); }
                    catch (Exception) { }
                }
                File.Delete(LeftoverPath);
            }
            catch (Exception ex) { Log.Write("leftover snapshots: " + ex.Message); }
        }

        // Writes the disk image records; returns the number of data bytes sent.
        // The snapshot device and a display name of the volume at `offset` on disk `number`.
        public static void VolumeAt(CloneSession session, int number, long offset, out string device, out string volume, out string name)
        {
            DiskInfo d;
            if (!session.Disks.TryGetValue(number, out d)) throw new Exception("disk " + number + " is not part of this clone");
            foreach (var p in d.Partitions)
            {
                if (p.Offset != offset) continue;
                name = p.Letter.Length > 0 ? p.Letter : "disk " + number + " partition " + p.Number;
                volume = p.Letter.Length > 0 ? p.Letter.TrimEnd('\\') + "\\" : p.Volume;
                if (p.Volume.Length > 0 && session.ShadowDevices.TryGetValue(p.Volume, out device) &&
                    p.FileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
                    return;
                throw new Exception(name + " has no NTFS snapshot");
            }
            throw new Exception("no partition starts at byte " + offset + " of disk " + number);
        }

        public static long Stream(CloneSession session, int number, Stream output, AesCtr ctr)
        {
            return Stream(session, number, output, ctr, new List<long>(), false);
        }

        // headOnly: partitions (offsets) of which only the boot sector area (8 KiB) is sent, because
        // their files travel through /clone-files; allHeads: that for every partition.
        public static long Stream(CloneSession session, int number, Stream output, AesCtr ctr, List<long> headOnly, bool allHeads)
        {
            DiskInfo d;
            if (!session.Disks.TryGetValue(number, out d)) throw new Exception("disk " + number + " is not part of this clone");
            var w = new RecordWriter(output, ctr);
            w.Header(d.Size);
            var parts = new List<Partition>();
            foreach (var p in d.Partitions) if (p.Kind != "extended") parts.Add(p);
            parts.Sort((a, c) => a.Offset.CompareTo(c.Offset));

            using (var raw = RawDevice.Open(@"\\.\PhysicalDrive" + number))
            {
                long pos = parts.Count > 0 ? Math.Min(parts[0].Offset, d.Size) : Math.Min(d.Size, MiB);
                CopyRaw(raw, w, 0, pos);   // MBR / GPT, boot loader gap
                foreach (var p in parts)
                {
                    if (p.Offset > pos)
                    {
                        // gaps hold EBRs of logical drives at their edges; the middle is free space
                        long gap = p.Offset - pos;
                        if (gap <= 2 * MiB) CopyRaw(raw, w, pos, gap);
                        else
                        {
                            CopyRaw(raw, w, pos, MiB);
                            CopyRaw(raw, w, p.Offset - MiB, MiB);
                        }
                    }
                    string device;
                    if (allHeads || headOnly.Contains(p.Offset))
                        CopyHead(raw, session, p, w);   // boot code + BPB for the new file system
                    else if (p.Volume.Length > 0 && session.ShadowDevices.TryGetValue(p.Volume, out device))
                    {
                        List<long[]> skip;
                        session.Skip.TryGetValue(p.Volume, out skip);
                        CopyVolume(raw, device, p, skip, w);
                    }
                    else if (p.Kind != "msr")
                        CopyRaw(raw, w, p.Offset, p.Length);   // EFI system, recovery, other file systems
                    pos = Math.Max(pos, p.End);
                }
                if (pos < d.Size)
                {
                    long t = Math.Max(pos, d.Size - MiB);   // backup GPT / dynamic disk database
                    CopyRaw(raw, w, t, d.Size - t);
                }
            }
            w.End();
            w.Report();
            return w.DataBytes;
        }

        // The first 8 KiB of a partition. A volume with a snapshot is read through the snapshot:
        // on the raw disk a BitLocker volume shows "-FVE-FS-" instead of its NTFS boot sector
        // (the snapshot, like the file copy, sees the decrypted volume).
        static void CopyHead(RawDevice raw, CloneSession session, Partition p, RecordWriter w)
        {
            long n = Math.Min(8192, p.Length);
            string device;
            if (p.Volume.Length > 0 && session.ShadowDevices.TryGetValue(p.Volume, out device))
            {
                try
                {
                    using (var vol = RawDevice.Open(device)) CopyRange(vol, 0, w, p.Offset, n);
                    return;
                }
                catch (Exception ex)
                {
                    Log.Write("boot sector of " + (p.Letter.Length > 0 ? p.Letter : p.Volume) + " from the snapshot: " + ex.Message + " - reading the disk");
                }
            }
            CopyRaw(raw, w, p.Offset, n);
        }

        static void CopyRaw(RawDevice dev, RecordWriter w, long offset, long length)
        {
            CopyRange(dev, 0, w, offset, length);
        }

        // Copies `length` bytes at `from` on `dev` to disk offset `to`; all-zero blocks are not sent.
        static void CopyRange(RawDevice dev, long from, RecordWriter w, long to, long length)
        {
            var buf = new byte[MiB];
            long done = 0;
            while (done < length)
            {
                int n = (int)Math.Min(MiB, length - done);
                w.ReadClock.Start();
                int got = dev.Read(from + done, buf, n);
                w.ReadClock.Stop();
                if (got < n) Array.Clear(buf, got, n - got);
                if (!IsZero(buf, n)) w.Data(to + done, buf, n);
                done += n;
            }
        }

        static void CopyVolume(RawDevice disk, string devicePath, Partition p, List<long[]> skip, RecordWriter w)
        {
            using (var vol = RawDevice.Open(devicePath))
            {
                long clusterSize, clusters;
                byte[] bitmap = null;
                if (Volumes.NtfsData(vol, out clusterSize, out clusters))
                    bitmap = Volumes.Bitmap(vol, clusters);
                if (bitmap == null)
                {
                    Log.Write("no cluster bitmap for " + (p.Letter.Length > 0 ? p.Letter : p.Volume) + " - copying the whole volume");
                    long len = vol.Length > 0 ? Math.Min(vol.Length, p.Length) : p.Length;
                    CopyRange(vol, 0, w, p.Offset, len);
                    if (len < p.Length) CopyRaw(disk, w, p.Offset + len, p.Length - len);
                    return;
                }
                if (skip != null)
                    foreach (var run in skip)
                        for (long c = run[0]; c < run[0] + run[1] && c < clusters; c++)
                            bitmap[c >> 3] &= (byte)~(1 << (int)(c & 7));

                // One read per 1 MiB window (from its first to its last used cluster), then a
                // record per used run inside it. Reading each run on its own made fragmented
                // volumes crawl: thousands of small random reads on the snapshot device.
                long per = Math.Max(1, MiB / clusterSize);
                long mergeGap = Math.Max(1, 32768 / clusterSize);   // small free gaps are sent along
                var buf = new byte[per * clusterSize];
                var rec = new byte[per * clusterSize];
                long lcn = 0;
                while (lcn < clusters)
                {
                    if (!Used(bitmap, lcn))
                    {
                        if ((lcn & 7) == 0 && bitmap[lcn >> 3] == 0) lcn += 8; else lcn++;
                        continue;
                    }
                    long winStart = lcn, winEnd = Math.Min(clusters, lcn + per), last = lcn;
                    for (long c = lcn; c < winEnd; c++) if (Used(bitmap, c)) last = c;
                    int bytes = (int)((last - winStart + 1) * clusterSize);
                    w.ReadClock.Start();
                    int got = vol.Read(winStart * clusterSize, buf, bytes);
                    w.ReadClock.Stop();
                    if (got < bytes) Array.Clear(buf, Math.Max(got, 0), bytes - Math.Max(got, 0));
                    long c2 = winStart;
                    while (c2 <= last)
                    {
                        if (!Used(bitmap, c2)) { c2++; continue; }
                        long runStart = c2, runEnd = c2 + 1;   // exclusive
                        long scan = runEnd;
                        while (scan <= last)
                        {
                            if (Used(bitmap, scan)) { runEnd = scan + 1; scan++; continue; }
                            if (scan - runEnd + 1 > mergeGap) break;
                            scan++;
                        }
                        int at = (int)((runStart - winStart) * clusterSize), len = (int)((runEnd - runStart) * clusterSize);
                        Array.Copy(buf, at, rec, 0, len);   // the writer encrypts in place
                        if (!IsZero(rec, len)) w.Data(p.Offset + runStart * clusterSize, rec, len);
                        c2 = runEnd;
                    }
                    lcn = last + 1;
                }
                long fsEnd = clusters * clusterSize;   // backup boot sector lives behind the last cluster
                if (fsEnd < p.Length) CopyRaw(disk, w, p.Offset + fsEnd, p.Length - fsEnd);
            }
        }

        static bool Used(byte[] bitmap, long lcn)
        {
            return (bitmap[lcn >> 3] & (1 << (int)(lcn & 7))) != 0;
        }

        static bool IsZero(byte[] b, int n)
        {
            int i = 0;
            for (; i + 8 <= n; i += 8) if (BitConverter.ToInt64(b, i) != 0) return false;
            for (; i < n; i++) if (b[i] != 0) return false;
            return true;
        }
    }

    // File-level copy of one volume with wimlib (wimlib-imagex.exe + libwim-15.dll in the
    // agent's wimlib folder, deployed by the converter). It reads the files from the VSS
    // snapshot with all NTFS metadata (security descriptors, alternate data streams, short
    // names, hard links, reparse points, object IDs) and writes a pipable WIM to stdout.
    static class FileCopy
    {
        static string Dir { get { return Path.Combine(Program.InstallDir, "wimlib"); } }
        static string Exe { get { return Path.Combine(Dir, "wimlib-imagex.exe"); } }

        public static bool Available
        {
            get { return IntPtr.Size == 8 && File.Exists(Exe) && File.Exists(Path.Combine(Dir, "libwim-15.dll")); }
        }

        // Windows recreates these; the shadow copy storage would only be dead weight.
        const string Config = "[ExclusionList]\r\n\\$ntfs.log\r\n\\hiberfil.sys\r\n\\pagefile.sys\r\n\\swapfile.sys\r\n" +
                              "\\System Volume Information\\*{3808876b-c176-4e48-b7ae-04046e6cc752}\r\n";

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct FindData
        {
            public uint Attributes;
            public uint Created1, Created2, Accessed1, Accessed2, Written1, Written2;
            public uint SizeHigh, SizeLow, ReparseTag, Reserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string ShortName;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr FindFirstFile(string pattern, out FindData data);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool FindNextFile(IntPtr handle, out FindData data);
        [DllImport("kernel32.dll")]
        static extern bool FindClose(IntPtr handle);

        // AF_UNIX socket and WSL 1 pipe, character and block device: empty placeholders that ntfs-3g
        // refuses to create (it wants a flag wimlib does not set), which stops the whole copy.
        // WSL 1 symbolic links (0xA000001D) are accepted.
        static bool Unrestorable(uint tag)
        {
            return tag == 0x80000023 || tag == 0x80000024 || tag == 0x80000025 || tag == 0x80000026;
        }

        // Paths (from the volume root, "\dir\file") of the files that cannot be restored. Only reads
        // the directories; a failure just means nothing is left out.
        public static List<string> FindSpecial(string device, string name)
        {
            var found = new List<string>();
            try
            {
                var clock = Stopwatch.StartNew();
                string root = device.TrimEnd('\\');
                var dirs = new Stack<string>();
                dirs.Push("");
                var invalid = new IntPtr(-1);
                while (dirs.Count > 0)
                {
                    string dir = dirs.Pop();
                    FindData d;
                    IntPtr h = FindFirstFile(root + dir + "\\*", out d);
                    if (h == invalid) continue;
                    try
                    {
                        do
                        {
                            if (d.Name == "." || d.Name == "..") continue;
                            string path = dir + "\\" + d.Name;
                            bool reparse = (d.Attributes & 0x400) != 0;
                            if (reparse && Unrestorable(d.ReparseTag)) found.Add(path);
                            else if ((d.Attributes & 0x10) != 0 && !reparse) dirs.Push(path);
                        } while (FindNextFile(h, out d));
                    }
                    finally { FindClose(h); }
                }
                Log.Write(string.Format(CultureInfo.InvariantCulture, "{0}: {1} special files (WSL 1 devices, pipes, sockets) left out, found in {2:0} s",
                    name, found.Count, clock.Elapsed.TotalSeconds));
            }
            catch (Exception ex) { Log.Write("looking for special files on " + name + ": " + ex.Message); }
            return found;
        }

        // Reads the files from our VSS snapshot (device); if wimlib cannot open that path, it takes
        // its own snapshot of the volume (volume = "C:\" or "\\?\Volume{...}\") with --snapshot.
        // ASCII config: a character outside it becomes '?', which wimlib reads as "any character".
        public static void Capture(string device, string volume, string name, List<string> special, Stream output, AesCtr ctr)
        {
            if (!Available) throw new Exception("wimlib is missing in " + Dir + " (connect the converter with the administrator password to update the agent)");
            string config = Path.Combine(Program.InstallDir, "wimcapture-" + Guid.NewGuid().ToString("N") + ".ini");
            var text = new StringBuilder(Config);
            foreach (string path in special) text.Append(path).Append("\r\n");
            File.WriteAllText(config, text.ToString(), Encoding.ASCII);
            try
            {
                long sent;
                string error = Run(device, false, config, name, output, ctr, out sent);
                if (error == null) return;
                if (sent > 0 || string.IsNullOrEmpty(volume)) throw new Exception(error);
                Log.Write("wimlib could not read the snapshot device of " + name + " (" + error + ") - retrying with its own snapshot of " + volume);
                error = Run(volume, true, config, name, output, ctr, out sent);
                if (error != null) throw new Exception(error);
            }
            finally
            {
                try { File.Delete(config); } catch (Exception) { }
            }
        }

        static string Run(string source, bool snapshot, string config, string name, Stream output, AesCtr ctr, out long sent)
        {
            // a quoted argument must not end in a single backslash (\" escapes the quote)
            string src = source.TrimEnd('\\') + "\\\\";
            int threads = Math.Max(1, Environment.ProcessorCount / 2);   // the machine keeps serving its users
            var psi = new ProcessStartInfo(Exe, "capture \"" + src + "\" - mtv2v --compress=XPRESS --norpfix --threads=" + threads +
                                                (snapshot ? " --snapshot" : "") + " \"--config=" + config + "\"")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Dir,
            };
            var tail = new Queue<string>();
            string progress = "";
            int logged = 0;
            var scanClock = Stopwatch.StartNew();
            TimeSpan lastScanLog = TimeSpan.Zero;
            using (var p = Process.Start(psi))
            {
                try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception) { }
                var errThread = new Thread(() =>
                {
                    string line;
                    try
                    {
                        while ((line = p.StandardError.ReadLine()) != null)
                        {
                            line = line.Trim();
                            if (line.Length == 0) continue;
                            lock (tail)
                            {
                                if (line.IndexOf("% done", StringComparison.Ordinal) >= 0) { progress = line; continue; }
                                // the scan comes first and sends nothing: show that it moves
                                if (line.IndexOf(" scanned (", StringComparison.Ordinal) >= 0)
                                {
                                    if (scanClock.Elapsed - lastScanLog >= TimeSpan.FromMinutes(2))
                                    {
                                        lastScanLog = scanClock.Elapsed;
                                        Log.Write("scanning the files of " + name + ": " + line);
                                    }
                                    continue;
                                }
                                tail.Enqueue(line);
                                while (tail.Count > 20) tail.Dequeue();
                            }
                            if ((line.StartsWith("[ERROR]") || line.StartsWith("[WARNING]") || line.StartsWith("ERROR")) && logged++ < 100)
                                Log.Write("wimlib: " + line);
                        }
                    }
                    catch (Exception) { }
                }) { IsBackground = true };
                errThread.Start();

                var input = p.StandardOutput.BaseStream;
                var buf = new byte[1 << 20];
                long lastSent = 0;
                sent = 0;
                Stopwatch total = Stopwatch.StartNew(), wait = new Stopwatch(), send = new Stopwatch();
                TimeSpan lastReport = TimeSpan.Zero, lastWait = TimeSpan.Zero, lastSend = TimeSpan.Zero;
                try
                {
                    while (true)
                    {
                        wait.Start();
                        int n = input.Read(buf, 0, buf.Length);
                        wait.Stop();
                        if (n <= 0) break;
                        ctr.Transform(buf, 0, n);
                        send.Start();
                        output.Write(buf, 0, n);
                        send.Stop();
                        sent += n;
                        TimeSpan now = total.Elapsed;
                        if (now - lastReport >= TimeSpan.FromMinutes(5))
                        {
                            double s = (now - lastReport).TotalSeconds;
                            string pr;
                            lock (tail) pr = progress;
                            Log.Write(string.Format(CultureInfo.InvariantCulture,
                                "files of {0}: {1:0.0} GB sent (compressed), {2:0.0} MB/s in the last {3:0} min - reading and compressing {4:0}%, " +
                                "waiting for the network {5:0}%{6}", name, sent / 1e9, (sent - lastSent) / 1e6 / s, s / 60,
                                100 * (wait.Elapsed - lastWait).TotalSeconds / s, 100 * (send.Elapsed - lastSend).TotalSeconds / s,
                                pr.Length > 0 ? " - " + pr : ""));
                            lastReport = now; lastSent = sent; lastWait = wait.Elapsed; lastSend = send.Elapsed;
                        }
                    }
                }
                catch (Exception)
                {
                    try { if (!p.HasExited) p.Kill(); } catch (Exception) { }
                    throw;
                }
                p.WaitForExit();
                errThread.Join(5000);
                string last;
                lock (tail) last = string.Join(" | ", tail.ToArray());
                if (p.ExitCode != 0)
                    return "wimlib-imagex failed (exit code " + p.ExitCode + "): " + last;
                Log.Write(string.Format(CultureInfo.InvariantCulture, "files of {0} sent: {1:0.0} GB (compressed) in {2:0} min ({3:0.0} MB/s)",
                    name, sent / 1e9, total.Elapsed.TotalMinutes, sent / 1e6 / Math.Max(1, total.Elapsed.TotalSeconds)));
                return null;
            }
        }
    }

    class RecordWriter
    {
        readonly Stream output;
        readonly AesCtr ctr;
        readonly byte[] head = new byte[16];
        public long DataBytes;

        public RecordWriter(Stream output, AesCtr ctr)
        {
            this.output = output;
            this.ctr = ctr;
        }

        public void Header(long size)
        {
            var h = new byte[24];
            Encoding.ASCII.GetBytes("MTV2VDSK", 0, 8, h, 0);
            BitConverter.GetBytes(1).CopyTo(h, 8);
            BitConverter.GetBytes(size).CopyTo(h, 16);
            Write(h, 0, h.Length);
        }

        // Where the time goes, logged every 5 minutes: reading the snapshot, encrypting, or
        // waiting for the network (the Proxmox host not taking the data fast enough).
        public readonly Stopwatch ReadClock = new Stopwatch();
        readonly Stopwatch cryptClock = new Stopwatch(), sendClock = new Stopwatch(), total = Stopwatch.StartNew();
        long lastBytes;
        TimeSpan lastReport, lastRead, lastCrypt, lastSend;
        public long Offset;

        public void Data(long offset, byte[] buf, int count)
        {
            BitConverter.GetBytes(offset).CopyTo(head, 0);
            BitConverter.GetBytes(count).CopyTo(head, 8);
            BitConverter.GetBytes(0).CopyTo(head, 12);
            Write(head, 0, 16);
            Write(buf, 0, count);   // encrypts buf in place; callers refill it
            DataBytes += count;
            Offset = offset;
            if (total.Elapsed - lastReport >= TimeSpan.FromMinutes(5)) Report();
        }

        public void Report()
        {
            TimeSpan now = total.Elapsed, span = now - lastReport;
            if (span.TotalSeconds < 1) return;
            double s = span.TotalSeconds;
            Log.Write(string.Format(CultureInfo.InvariantCulture,
                "streaming: {0:0.0} GB sent, at {1:0.0} GB of the disk, {2:0.0} MB/s in the last {3:0} min - reading the snapshot {4:0}%, encrypting {5:0}%, waiting for the network {6:0}%",
                DataBytes / 1e9, Offset / 1e9, (DataBytes - lastBytes) / 1e6 / s, s / 60,
                100 * (ReadClock.Elapsed - lastRead).TotalSeconds / s, 100 * (cryptClock.Elapsed - lastCrypt).TotalSeconds / s,
                100 * (sendClock.Elapsed - lastSend).TotalSeconds / s));
            lastReport = now; lastBytes = DataBytes;
            lastRead = ReadClock.Elapsed; lastCrypt = cryptClock.Elapsed; lastSend = sendClock.Elapsed;
        }

        public void End()
        {
            Array.Clear(head, 0, 16);
            BitConverter.GetBytes(-1L).CopyTo(head, 0);
            Write(head, 0, 16);
            output.Flush();
        }

        void Write(byte[] b, int o, int n)
        {
            cryptClock.Start();
            ctr.Transform(b, o, n);
            cryptClock.Stop();
            sendClock.Start();
            output.Write(b, o, n);
            sendClock.Stop();
        }
    }

    // AES-256 in counter mode (128-bit big-endian counter, as "openssl enc -aes-256-ctr").
    // Uses Windows CNG (AES-NI) when available, else the managed implementation.
    class AesCtr : IDisposable
    {
        [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)]
        static extern int BCryptOpenAlgorithmProvider(out IntPtr alg, string id, string impl, int flags);
        [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)]
        static extern int BCryptSetProperty(IntPtr h, string prop, byte[] input, int size, int flags);
        [DllImport("bcrypt.dll")]
        static extern int BCryptGenerateSymmetricKey(IntPtr alg, out IntPtr key, IntPtr obj, int objSize, byte[] secret, int secretSize, int flags);
        [DllImport("bcrypt.dll")]
        static extern int BCryptEncrypt(IntPtr key, byte[] input, int inSize, IntPtr padding, IntPtr iv, int ivSize, byte[] output, int outSize, out int result, int flags);
        [DllImport("bcrypt.dll")]
        static extern int BCryptDestroyKey(IntPtr key);
        [DllImport("bcrypt.dll")]
        static extern int BCryptCloseAlgorithmProvider(IntPtr alg, int flags);

        readonly byte[] counter = new byte[16];
        readonly byte[] blocks = new byte[65536];
        readonly byte[] stream = new byte[65536];
        int pos = 65536;
        IntPtr alg, key;
        ICryptoTransform managed;

        public AesCtr(byte[] secret, byte[] iv)
        {
            Array.Copy(iv, counter, 16);
            try
            {
                if (BCryptOpenAlgorithmProvider(out alg, "AES", null, 0) != 0) throw new Exception("AES");
                byte[] mode = Encoding.Unicode.GetBytes("ChainingModeECB\0");
                if (BCryptSetProperty(alg, "ChainingMode", mode, mode.Length, 0) != 0) throw new Exception("ECB");
                if (BCryptGenerateSymmetricKey(alg, out key, IntPtr.Zero, 0, secret, secret.Length, 0) != 0) throw new Exception("key");
            }
            catch (Exception)
            {
                Close();
                var r = new RijndaelManaged { Mode = CipherMode.ECB, Padding = PaddingMode.None, KeySize = 256, BlockSize = 128 };
                managed = r.CreateEncryptor(secret, new byte[16]);
            }
        }

        public void Transform(byte[] data, int offset, int count)
        {
            for (int i = 0; i < count; i++)
            {
                if (pos == stream.Length) Refill();
                data[offset + i] ^= stream[pos++];
            }
        }

        void Refill()
        {
            for (int b = 0; b < blocks.Length; b += 16)
            {
                Array.Copy(counter, 0, blocks, b, 16);
                for (int i = 15; i >= 0; i--) if (++counter[i] != 0) break;
            }
            if (managed != null) managed.TransformBlock(blocks, 0, blocks.Length, stream, 0);
            else
            {
                int got;
                int rc = BCryptEncrypt(key, blocks, blocks.Length, IntPtr.Zero, IntPtr.Zero, 0, stream, stream.Length, out got, 0);
                if (rc != 0) throw new Exception("BCryptEncrypt failed: 0x" + rc.ToString("x8"));
            }
            pos = 0;
        }

        void Close()
        {
            if (key != IntPtr.Zero) { BCryptDestroyKey(key); key = IntPtr.Zero; }
            if (alg != IntPtr.Zero) { BCryptCloseAlgorithmProvider(alg, 0); alg = IntPtr.Zero; }
        }

        public void Dispose()
        {
            Close();
            if (managed != null) managed.Dispose();
        }
    }

    // Read-only handle on a disk, volume or snapshot device.
    class RawDevice : IDisposable
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetFilePointerEx(IntPtr h, long distance, out long newPos, int method);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool ReadFile(IntPtr h, [Out] byte[] buf, int count, out int read, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool DeviceIoControl(IntPtr h, uint code, byte[] inBuf, int inSize, [Out] byte[] outBuf, int outSize, out int returned, IntPtr overlapped);
        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr h);

        public IntPtr Handle;
        public string Path;

        public static RawDevice Open(string path)
        {
            IntPtr h = CreateFile(path, 0x80000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (h == new IntPtr(-1)) throw new Exception("Cannot open " + path + ": " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
            return new RawDevice { Handle = h, Path = path };
        }

        public long Length
        {
            get
            {
                var o = new byte[8];
                int ret;
                return DeviceIoControl(Handle, 0x7405C, null, 0, o, 8, out ret, IntPtr.Zero) ? BitConverter.ToInt64(o, 0) : -1;
            }
        }

        // Reads at a sector-aligned offset; returns the bytes read (fewer at the end of the device).
        public int Read(long offset, byte[] buf, int count)
        {
            long np;
            if (!SetFilePointerEx(Handle, offset, out np, 0))
                throw new Exception("seek on " + Path + " failed: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
            int total = 0;
            while (total < count)
            {
                int read;
                byte[] target = total == 0 ? buf : new byte[count - total];
                if (!ReadFile(Handle, target, count - total, out read, IntPtr.Zero))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err == 38) break;   // ERROR_HANDLE_EOF: end of the device
                    throw new Exception("read at " + offset + " on " + Path + " failed: " + new Win32Exception(err).Message);
                }
                if (read == 0) break;
                if (target != buf) Array.Copy(target, 0, buf, total, read);
                total += read;
            }
            return total;
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero) { CloseHandle(Handle); Handle = IntPtr.Zero; }
        }
    }

    static class Volumes
    {
        const uint FSCTL_GET_NTFS_VOLUME_DATA = 0x90064, FSCTL_GET_VOLUME_BITMAP = 0x9006F, FSCTL_GET_RETRIEVAL_POINTERS = 0x90073;
        const int ERROR_MORE_DATA = 234;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr h);

        public static bool NtfsData(RawDevice vol, out long clusterSize, out long clusters)
        {
            clusterSize = clusters = 0;
            var o = new byte[128];
            int ret;
            if (!RawDevice.DeviceIoControl(vol.Handle, FSCTL_GET_NTFS_VOLUME_DATA, null, 0, o, o.Length, out ret, IntPtr.Zero)) return false;
            clusters = BitConverter.ToInt64(o, 16);
            clusterSize = BitConverter.ToUInt32(o, 44);
            return clusters > 0 && clusterSize >= 512;
        }

        // One bit per cluster, set = in use. Null if the file system does not provide it.
        public static byte[] Bitmap(RawDevice vol, long clusters)
        {
            var bitmap = new byte[(clusters + 7) / 8];
            var outBuf = new byte[16 + (8 << 20)];
            long lcn = 0;
            while (lcn < clusters)
            {
                int ret;
                bool ok = RawDevice.DeviceIoControl(vol.Handle, FSCTL_GET_VOLUME_BITMAP, BitConverter.GetBytes(lcn), 8, outBuf, outBuf.Length, out ret, IntPtr.Zero);
                int err = ok ? 0 : Marshal.GetLastWin32Error();
                if (!ok && err != ERROR_MORE_DATA) { Log.Write("volume bitmap failed: " + new Win32Exception(err).Message); return null; }
                long start = BitConverter.ToInt64(outBuf, 0);
                long size = BitConverter.ToInt64(outBuf, 8);   // clusters from start to the end of the volume
                long bytes = Math.Min((size + 7) / 8, ret - 16);
                if (start % 8 != 0 || bytes <= 0) return null;
                Array.Copy(outBuf, 16, bitmap, start / 8, Math.Min(bytes, bitmap.Length - start / 8));
                lcn = start + bytes * 8;
                if (ok) break;
            }
            return bitmap;
        }

        // Cluster runs of pagefile.sys, hiberfil.sys and swapfile.sys: their contents are
        // worthless on the new machine, so they are not copied (they read as zeros).
        public static List<long[]> SystemFileRuns(string volume)
        {
            var runs = new List<long[]>();
            foreach (string name in new[] { "pagefile.sys", "hiberfil.sys", "swapfile.sys" })
            {
                IntPtr h = CreateFile(volume + name, 0x80, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);   // FILE_READ_ATTRIBUTES, backup semantics
                if (h == new IntPtr(-1)) continue;
                try
                {
                    long vcn = 0, total = 0;
                    var o = new byte[16 + 16 * 4096];
                    while (true)
                    {
                        int ret;
                        bool ok = RawDevice.DeviceIoControl(h, FSCTL_GET_RETRIEVAL_POINTERS, BitConverter.GetBytes(vcn), 8, o, o.Length, out ret, IntPtr.Zero);
                        int err = ok ? 0 : Marshal.GetLastWin32Error();
                        if (!ok && err != ERROR_MORE_DATA) break;
                        int count = BitConverter.ToInt32(o, 0);
                        long prev = BitConverter.ToInt64(o, 8);
                        for (int i = 0; i < count; i++)
                        {
                            long next = BitConverter.ToInt64(o, 16 + i * 16), lcn = BitConverter.ToInt64(o, 24 + i * 16);
                            if (lcn >= 0) { runs.Add(new[] { lcn, next - prev }); total += next - prev; }
                            prev = next;
                        }
                        if (ok || count == 0) break;
                        vcn = prev;
                    }
                    if (total > 0) Log.Write("not copying " + name + " on " + volume + " (" + total + " clusters)");
                }
                finally { CloseHandle(h); }
            }
            return runs;
        }
    }

    // VSS snapshots through WMI (Win32_ShadowCopy), available on Windows Server 2008 R2 and newer.
    static class Shadow
    {
        public static void Create(string volume, string letter, out string id, out string device)
        {
            string detail;
            uint rc = TryCreate(volume, out id, out detail);
            if (rc != 0 && letter.Length > 0) rc = TryCreate(letter + "\\", out id, out detail);
            if (rc != 0)
                throw new Exception("Windows could not take a VSS snapshot of " + (letter.Length > 0 ? letter : volume) + ": " + Explain(rc) +
                    (detail.Length > 0 ? " (" + detail + ")" : "") +
                    ". Check that the \"Volume Shadow Copy\" service can start and that the volume has free space for the snapshot; " +
                    "the Application event log (source VSS) on that machine has the exact reason.");
            device = null;
            using (var s = new ManagementObjectSearcher("SELECT DeviceObject FROM Win32_ShadowCopy WHERE ID='" + id.Replace("'", "") + "'"))
                foreach (ManagementObject o in s.Get()) device = o["DeviceObject"] as string;
            if (string.IsNullOrEmpty(device)) throw new Exception("The VSS snapshot " + id + " has no device");
        }

        static uint TryCreate(string volume, out string id, out string detail)
        {
            id = null;
            detail = "";
            try
            {
                using (var cls = new ManagementClass(@"\\.\root\cimv2", "Win32_ShadowCopy", null))
                {
                    var inp = cls.GetMethodParameters("Create");
                    inp["Volume"] = volume;
                    inp["Context"] = "ClientAccessible";
                    var outp = cls.InvokeMethod("Create", inp, null);
                    uint rc = Convert.ToUInt32(outp["ReturnValue"]);
                    if (rc == 0) id = outp["ShadowID"] as string;
                    return rc == 0 && id == null ? 12 : rc;
                }
            }
            catch (Exception ex)
            {
                Log.Write("Win32_ShadowCopy.Create(" + volume + "): " + ex.Message);
                detail = ex.Message.Trim();
                return 12;
            }
        }

        public static void Delete(string id)
        {
            using (var s = new ManagementObjectSearcher("SELECT * FROM Win32_ShadowCopy WHERE ID='" + id.Replace("'", "") + "'"))
                foreach (ManagementObject o in s.Get()) o.Delete();
        }

        static string Explain(uint rc)
        {
            switch (rc)
            {
                case 1: return "access denied";
                case 2: return "invalid argument";
                case 3: return "volume not found";
                case 4: return "volume not supported";
                case 5: return "unsupported shadow copy context";
                case 6: return "not enough storage space for the snapshot";
                case 7: return "volume is in use";
                case 8: return "maximum number of snapshots reached";
                case 9: return "another snapshot operation is already running";
                case 10: return "a VSS provider vetoed the snapshot";
                case 11: return "VSS provider not registered";
                case 12: return "the VSS provider failed (code 12)";
                default: return "VSS error " + rc;
            }
        }
    }

    // Makes the copy bootable on the new controller: the storage drivers of the target
    // hypervisor become boot-start drivers (only drivers that are already installed).
    // Returns true for "kvm" when the VirtIO SCSI and network drivers are installed, so the
    // copy can start on VirtIO directly (one disk, no SATA phase).
    static class Drivers
    {
        public static bool Installed(string service)
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + service))
                {
                    if (k == null) return false;
                    string image = k.GetValue("ImagePath") as string;
                    if (string.IsNullOrEmpty(image)) return false;
                    if (image.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase)) image = "%SystemRoot%" + image.Substring(11);
                    if (image.StartsWith(@"\??\")) image = image.Substring(4);
                    image = Environment.ExpandEnvironmentVariables(image);
                    if (!Path.IsPathRooted(image)) image = Path.Combine(Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows", image);
                    return File.Exists(image);
                }
            }
            catch (Exception) { return false; }
        }

        public static bool Prepare(string target)
        {
            DoPrepare(target);
            if (target == "vmware") return false;
            bool ready = Installed("vioscsi") && Installed("netkvm");
            Log.Write("VirtIO drivers " + (ready ? "installed: the copy starts on VirtIO SCSI and VirtIO network"
                                                  : "not installed (vioscsi/netkvm): the copy starts on SATA and e1000e"));
            return ready;
        }

        static void DoPrepare(string target)
        {
            var names = new List<string> { "storahci", "msahci", "intelide", "pciide", "atapi" };
            if (target == "vmware") names.AddRange(new[] { "LSI_SAS", "LSI_SAS2", "LSI_SCSI", "pvscsi" });
            else names.AddRange(new[] { "vioscsi", "viostor" });
            foreach (string n in names)
            {
                try
                {
                    using (var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + n, true))
                    {
                        if (k == null) continue;
                        object start = k.GetValue("Start");
                        if (start is int && (int)start != 0)
                        {
                            k.SetValue("Start", 0, RegistryValueKind.DWord);
                            Log.Write("driver " + n + " set to start at boot (was " + start + ")");
                        }
                        using (var o = k.OpenSubKey("StartOverride", true))
                            if (o != null)
                                foreach (string v in o.GetValueNames())
                                    if (o.GetValue(v) is int && (int)o.GetValue(v) != 0) o.SetValue(v, 0, RegistryValueKind.DWord);
                    }
                }
                catch (Exception ex) { Log.Write("driver " + n + ": " + ex.Message); }
            }
        }
    }

    // clone.json, written into the snapshot: the copy knows it is a copy (different
    // SMBIOS UUID) and restores the source's network settings on its new cards.
    static class CloneMarker
    {
        static string FilePath { get { return Path.Combine(Program.InstallDir, "clone.json"); } }

        public static void Write(string target, bool restoreNetwork, Dictionary<string, object> shrink)
        {
            var doc = new Dictionary<string, object>
            {
                { "time", DateTime.Now.ToString("yyyy-MM-dd HH:mm") }, { "source_uuid", Platform.Uuid },
                { "source_host", Environment.MachineName }, { "target", target }, { "restore_network", restoreNetwork },
            };
            if (shrink != null && shrink.Count > 0) doc["shrink"] = shrink;
            File.WriteAllText(FilePath, Json.Write(doc));
        }

        // First boot of a copy whose volumes the Proxmox host could not resize: Windows
        // shrinks them itself (moving files as needed); "Finalize Windows VM" on the host
        // then cuts the disk behind the last partition. Runs once per copy.
        public static void ShrinkOnce()
        {
            var doc = Load();
            if (doc == null || !doc.ContainsKey("shrink") || doc.ContainsKey("shrink_done")) return;
            var want = doc["shrink"] as Dictionary<string, object>;
            if (want == null) return;
            var results = new List<object>();
            try
            {
                // shadow copies taken on the source travel with the copy and cannot be moved
                try
                {
                    var p = Process.Start(new ProcessStartInfo("vssadmin.exe", "delete shadows /all /quiet") { CreateNoWindow = true, UseShellExecute = false });
                    p.WaitForExit(300000);
                }
                catch (Exception ex) { Log.Write("shrink: deleting shadow copies: " + ex.Message); }
                foreach (var kv in want)
                {
                    string letter = kv.Key.TrimEnd('\\').ToUpperInvariant();
                    long target = Convert.ToInt64(kv.Value, CultureInfo.InvariantCulture);
                    string msg = Shrinker.ShrinkVolume(letter, target);
                    Log.Write("shrink " + letter + ": " + msg);
                    results.Add(letter + " " + msg);
                }
            }
            catch (Exception ex) { Log.Write("shrink: " + ex.Message); results.Add(ex.Message); }
            doc["shrink_done"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            doc["shrink_result"] = results;
            try { File.WriteAllText(FilePath, Json.Write(doc)); } catch (Exception ex) { Log.Write("shrink: " + ex.Message); }
        }

        static Dictionary<string, object> Load()
        {
            try { return File.Exists(FilePath) ? Json.Parse(File.ReadAllText(FilePath)) as Dictionary<string, object> : null; }
            catch (Exception) { return null; }
        }

        // 1 = this machine is a hot clone of the machine that wrote the marker, 0 = not.
        public static int IsClone()
        {
            var doc = Load();
            if (doc == null) return 0;
            string src = doc.ContainsKey("source_uuid") ? doc["source_uuid"] as string ?? "" : "";
            string now = Platform.Uuid;
            return src.Length > 0 && now.Length > 0 && src != now ? 1 : 0;
        }

        public static bool RestoreNetwork()
        {
            var doc = Load();
            return doc == null || !doc.ContainsKey("restore_network") || Json.Bool(doc, "restore_network");
        }
    }

    // ---------------------------------------------------------------- network

    // Static IP settings are tied to the network card. On Proxmox the VM gets a new
    // card (same MAC), so the agent re-applies what it saved on VMware and removes
    // the old, now hidden card that still holds the address.
    static class Network
    {
        static string SavedPath { get { return Path.Combine(Program.InstallDir, "network.json"); } }
        static string StatePath { get { return Path.Combine(Program.InstallDir, "network-state.json"); } }

        public static List<Dictionary<string, object>> Capture()
        {
            var list = new List<Dictionary<string, object>>();
            var adapters = new Dictionary<uint, ManagementObject>();
            using (var s = new ManagementObjectSearcher("SELECT * FROM Win32_NetworkAdapter WHERE MACAddress IS NOT NULL AND NetConnectionID IS NOT NULL"))
                foreach (ManagementObject a in s.Get()) adapters[Convert.ToUInt32(a["Index"])] = a;
            using (var s = new ManagementObjectSearcher("SELECT * FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = TRUE"))
                foreach (ManagementObject c in s.Get())
                {
                    ManagementObject a;
                    if (!adapters.TryGetValue(Convert.ToUInt32(c["Index"]), out a)) continue;
                    var ips = new List<object>(); var masks = new List<object>();
                    var ip = c["IPAddress"] as string[]; var sub = c["IPSubnet"] as string[];
                    if (ip != null)
                        for (int i = 0; i < ip.Length; i++)
                            if (ip[i].IndexOf('.') > 0) { ips.Add(ip[i]); masks.Add(sub != null && i < sub.Length ? sub[i] : "255.255.255.0"); }
                    var gws = new List<object>(); var metrics = new List<object>();
                    var gw = c["DefaultIPGateway"] as string[]; var gm = c["GatewayCostMetric"] as ushort[];
                    if (gw != null)
                        for (int i = 0; i < gw.Length; i++)
                            if (gw[i].IndexOf('.') > 0) { gws.Add(gw[i]); metrics.Add(gm != null && i < gm.Length ? (int)gm[i] : 1); }
                    var dns = new List<object>();
                    var ds = c["DNSServerSearchOrder"] as string[];
                    if (ds != null) foreach (string d in ds) dns.Add(d);
                    list.Add(new Dictionary<string, object>
                    {
                        { "mac", ((string)a["MACAddress"]).ToLowerInvariant().Replace('-', ':') },
                        { "name", a["NetConnectionID"] as string ?? "" },
                        { "device", a["Name"] as string ?? "" },
                        { "pnp", a["PNPDeviceID"] as string ?? "" },
                        { "dhcp", c["DHCPEnabled"] is bool && (bool)c["DHCPEnabled"] },
                        { "ips", ips }, { "masks", masks }, { "gateways", gws }, { "metrics", metrics },
                        { "dns", dns }, { "dns_domain", c["DNSDomain"] as string ?? "" },
                    });
                }
            return list;
        }

        public static List<Dictionary<string, object>> Save()
        {
            var net = Capture();
            var doc = new Dictionary<string, object> { { "time", DateTime.Now.ToString("yyyy-MM-dd HH:mm") }, { "platform", Platform.Name }, { "adapters", net } };
            File.WriteAllText(SavedPath, Json.Write(doc));
            return net;
        }

        public static void SaveIfChanged()
        {
            var net = Capture();
            if (net.Count == 0) return;
            string adapters = Json.Write(net);
            var old = Load(SavedPath);
            if (old != null && old.ContainsKey("adapters") && Json.Write(old["adapters"]) == adapters) return;
            Save();
            Log.Write("network settings saved (" + net.Count + " adapter(s))");
        }

        public static string SavedTime()
        {
            var doc = Load(SavedPath);
            return doc != null && doc.ContainsKey("time") ? doc["time"] as string ?? "" : "";
        }

        static Dictionary<string, object> Load(string path)
        {
            try { return File.Exists(path) ? Json.Parse(File.ReadAllText(path)) as Dictionary<string, object> : null; }
            catch (Exception) { return null; }
        }

        public static void Restore()
        {
            var doc = Load(SavedPath);
            if (doc == null || !(doc["adapters"] is List<object>)) return;
            var state = Load(StatePath) ?? new Dictionary<string, object>();
            var configured = state.ContainsKey("configured") ? state["configured"] as List<object> ?? new List<object>() : new List<object>();

            var saved = new List<Dictionary<string, object>>();
            foreach (object o in (List<object>)doc["adapters"])
            {
                var a = o as Dictionary<string, object>;
                if (a != null && !Json.Bool(a, "dhcp") && Json.List(a, "ips").Count > 0) saved.Add(a);
            }
            if (saved.Count == 0) return;

            var present = Capture();
            var known = new List<string>();
            foreach (var s in saved) known.Add(((string)s["pnp"]).ToUpperInvariant());
            foreach (object c in configured) known.Add(c.ToString().ToUpperInvariant());

            foreach (var s in saved)
            {
                string mac = (string)s["mac"];
                Dictionary<string, object> now = null;
                foreach (var p in present) if ((string)p["mac"] == mac) now = p;
                if (now == null && saved.Count == 1)
                {
                    // new MAC address: use the only card that has no static address yet
                    var dhcp = present.FindAll(p => Json.Bool(p, "dhcp"));
                    if (present.Count == 1 && dhcp.Count == 1) now = dhcp[0];
                }
                if (now == null) continue;
                string pnp = (string)now["pnp"];
                if (pnp.Equals((string)s["pnp"], StringComparison.OrdinalIgnoreCase)) continue;   // same card as on VMware
                if (configured.Exists(c => c.ToString().Equals(pnp, StringComparison.OrdinalIgnoreCase))) continue;
                var nowIps = Json.List(now, "ips");
                if (!Json.Bool(now, "dhcp") && nowIps.Count > 0 && nowIps[0].ToString() != Json.List(s, "ips")[0].ToString())
                {
                    // someone set a different static address by hand: leave it alone
                    configured.Add(pnp);
                    continue;
                }

                Log.Write("new network card " + now["device"] + " (" + mac + ") - restoring the settings of " + s["name"]);
                Ghosts.Remove(known);
                Apply(now, s);
                string oldName = (string)s["name"], curName = (string)now["name"];
                if (oldName.Length > 0 && curName != oldName && !present.Exists(p => (string)p["name"] == oldName))
                    Netsh("interface set interface name=\"" + curName + "\" newname=\"" + oldName + "\"");
                configured.Add(pnp);
                state["configured"] = configured;
                File.WriteAllText(StatePath, Json.Write(state));
            }
        }

        static void Apply(Dictionary<string, object> adapter, Dictionary<string, object> s)
        {
            ManagementObject cfg = null;
            using (var q = new ManagementObjectSearcher("SELECT * FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = TRUE"))
                foreach (ManagementObject c in q.Get())
                {
                    string m = c["MACAddress"] as string;
                    if (m != null && m.ToLowerInvariant().Replace('-', ':') == (string)adapter["mac"]) cfg = c;
                }
            if (cfg == null) throw new Exception("adapter " + adapter["name"] + " is not ready yet");

            var p = cfg.GetMethodParameters("EnableStatic");
            p["IPAddress"] = Strings(Json.List(s, "ips"));
            p["SubnetMask"] = Strings(Json.List(s, "masks"));
            Check("EnableStatic", cfg.InvokeMethod("EnableStatic", p, null));

            var gws = Strings(Json.List(s, "gateways"));
            if (gws.Length > 0)
            {
                p = cfg.GetMethodParameters("SetGateways");
                p["DefaultIPGateway"] = gws;
                var metrics = Json.List(s, "metrics");
                if (metrics.Count == gws.Length && !metrics.Exists(x => Convert.ToInt32(x) < 1))   // 0 = automatic metric
                {
                    var m = new ushort[metrics.Count];
                    for (int i = 0; i < m.Length; i++) m[i] = Convert.ToUInt16(metrics[i]);
                    p["GatewayCostMetric"] = m;
                }
                Check("SetGateways", cfg.InvokeMethod("SetGateways", p, null));
            }
            var dns = Strings(Json.List(s, "dns"));
            if (dns.Length > 0)
            {
                p = cfg.GetMethodParameters("SetDNSServerSearchOrder");
                p["DNSServerSearchOrder"] = dns;
                Check("SetDNSServerSearchOrder", cfg.InvokeMethod("SetDNSServerSearchOrder", p, null));
            }
            string domain = s.ContainsKey("dns_domain") ? s["dns_domain"] as string ?? "" : "";
            if (domain.Length > 0)
            {
                p = cfg.GetMethodParameters("SetDNSDomain");
                p["DNSDomain"] = domain;
                cfg.InvokeMethod("SetDNSDomain", p, null);
            }
            Log.Write("applied " + string.Join(", ", Strings(Json.List(s, "ips"))) + " gw " + string.Join(", ", gws) + " dns " + string.Join(", ", dns));
        }

        static void Check(string what, ManagementBaseObject result)
        {
            uint rc = Convert.ToUInt32(result["ReturnValue"]);
            if (rc != 0 && rc != 1) throw new Exception(what + " failed with code " + rc);   // 1 = reboot required
        }

        static string[] Strings(List<object> list)
        {
            var a = new string[list.Count];
            for (int i = 0; i < a.Length; i++) a[i] = list[i].ToString();
            return a;
        }

        static void Netsh(string args)
        {
            var psi = new ProcessStartInfo("netsh.exe", args) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true };
            using (var p = Process.Start(psi)) { p.StandardOutput.ReadToEnd(); p.WaitForExit(); }
        }
    }

    // Removes network cards that are no longer present (the old VMware vmxnet3/e1000
    // and the temporary e1000e), but only the ones this agent knows about.
    static class Ghosts
    {
        [StructLayout(LayoutKind.Sequential)]
        struct SP_DEVINFO_DATA
        {
            public int cbSize;
            public Guid ClassGuid;
            public int DevInst;
            public IntPtr Reserved;
        }

        [DllImport("setupapi.dll", SetLastError = true)]
        static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr parent, int flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        static extern bool SetupDiEnumDeviceInfo(IntPtr set, int index, ref SP_DEVINFO_DATA data);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool SetupDiGetDeviceInstanceId(IntPtr set, ref SP_DEVINFO_DATA data, StringBuilder id, int size, out int required);

        [DllImport("setupapi.dll", SetLastError = true)]
        static extern bool SetupDiRemoveDevice(IntPtr set, ref SP_DEVINFO_DATA data);

        [DllImport("setupapi.dll")]
        static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        [DllImport("cfgmgr32.dll")]
        static extern int CM_Get_DevNode_Status(out int status, out int problem, int devInst, int flags);

        const int CR_NO_SUCH_DEVNINST = 0x0D;

        public static void Remove(List<string> instanceIds)
        {
            var net = new Guid("4d36e972-e325-11ce-bfc1-08002be10318");
            IntPtr set = SetupDiGetClassDevs(ref net, IntPtr.Zero, IntPtr.Zero, 0);   // all, including not present
            if (set == new IntPtr(-1)) return;
            try
            {
                var data = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf(typeof(SP_DEVINFO_DATA)) };
                for (int i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
                {
                    var id = new StringBuilder(512);
                    int req;
                    if (!SetupDiGetDeviceInstanceId(set, ref data, id, id.Capacity, out req)) continue;
                    string instance = id.ToString().ToUpperInvariant();
                    if (!instanceIds.Contains(instance)) continue;
                    int status, problem;
                    if (CM_Get_DevNode_Status(out status, out problem, data.DevInst, 0) != CR_NO_SUCH_DEVNINST) continue;   // still present
                    bool ok = SetupDiRemoveDevice(set, ref data);
                    Log.Write((ok ? "removed old network card " : "could not remove old network card ") + instance +
                        (ok ? "" : " (error " + Marshal.GetLastWin32Error() + ")"));
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
        }
    }

    // ------------------------------------------------------------------- JSON

    static class Json
    {
        public static string Write(object o)
        {
            var sb = new StringBuilder();
            Write(sb, o);
            return sb.ToString();
        }

        static void Write(StringBuilder sb, object o)
        {
            if (o == null) { sb.Append("null"); return; }
            if (o is string) { Str(sb, (string)o); return; }
            if (o is bool) { sb.Append((bool)o ? "true" : "false"); return; }
            if (o is double || o is float)
            {
                double d = Convert.ToDouble(o);
                sb.Append(double.IsNaN(d) || double.IsInfinity(d) ? "0" : d.ToString("0.###", CultureInfo.InvariantCulture));
                return;
            }
            if (o is int || o is long || o is uint || o is ulong || o is short || o is ushort || o is byte)
            {
                sb.Append(Convert.ToString(o, CultureInfo.InvariantCulture));
                return;
            }
            var dict = o as IDictionary;
            if (dict != null)
            {
                sb.Append('{');
                bool first = true;
                foreach (DictionaryEntry e in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    Str(sb, e.Key.ToString());
                    sb.Append(':');
                    Write(sb, e.Value);
                }
                sb.Append('}');
                return;
            }
            var list = o as IEnumerable;
            if (list != null)
            {
                sb.Append('[');
                bool first = true;
                foreach (object x in list)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    Write(sb, x);
                }
                sb.Append(']');
                return;
            }
            Str(sb, o.ToString());
        }

        static void Str(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // Objects -> Dictionary<string, object>, arrays -> List<object>, numbers -> double.
        public static object Parse(string s)
        {
            int i = 0;
            object v = Value(s, ref i);
            Skip(s, ref i);
            if (i != s.Length) throw new FormatException("trailing characters in JSON");
            return v;
        }

        static void Skip(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }

        static object Value(string s, ref int i)
        {
            Skip(s, ref i);
            if (i >= s.Length) throw new FormatException("unexpected end of JSON");
            char c = s[i];
            if (c == '{')
            {
                var d = new Dictionary<string, object>();
                i++;
                Skip(s, ref i);
                if (s[i] == '}') { i++; return d; }
                while (true)
                {
                    Skip(s, ref i);
                    string k = (string)Value(s, ref i);
                    Skip(s, ref i);
                    if (s[i++] != ':') throw new FormatException("expected :");
                    d[k] = Value(s, ref i);
                    Skip(s, ref i);
                    if (s[i] == ',') { i++; continue; }
                    if (s[i++] == '}') return d;
                    throw new FormatException("expected , or }");
                }
            }
            if (c == '[')
            {
                var l = new List<object>();
                i++;
                Skip(s, ref i);
                if (s[i] == ']') { i++; return l; }
                while (true)
                {
                    l.Add(Value(s, ref i));
                    Skip(s, ref i);
                    if (s[i] == ',') { i++; continue; }
                    if (s[i++] == ']') return l;
                    throw new FormatException("expected , or ]");
                }
            }
            if (c == '"')
            {
                var sb = new StringBuilder();
                i++;
                while (s[i] != '"')
                {
                    if (s[i] == '\\')
                    {
                        char e = s[++i];
                        switch (e)
                        {
                            case 'n': sb.Append('\n'); break;
                            case 'r': sb.Append('\r'); break;
                            case 't': sb.Append('\t'); break;
                            case 'b': sb.Append('\b'); break;
                            case 'f': sb.Append('\f'); break;
                            case 'u': sb.Append((char)Convert.ToInt32(s.Substring(i + 1, 4), 16)); i += 4; break;
                            default: sb.Append(e); break;
                        }
                        i++;
                    }
                    else sb.Append(s[i++]);
                }
                i++;
                return sb.ToString();
            }
            if (s.Substring(i).StartsWith("true")) { i += 4; return true; }
            if (s.Substring(i).StartsWith("false")) { i += 5; return false; }
            if (s.Substring(i).StartsWith("null")) { i += 4; return null; }
            int start = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            if (i == start) throw new FormatException("unexpected character in JSON");
            return double.Parse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        public static double Num(Dictionary<string, object> d, string k, double fallback)
        {
            object v;
            if (!d.TryGetValue(k, out v) || v == null) return fallback;
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); }
            catch (Exception) { return fallback; }
        }

        public static bool Bool(Dictionary<string, object> d, string k)
        {
            object v;
            return d.TryGetValue(k, out v) && v is bool && (bool)v;
        }

        public static List<object> List(Dictionary<string, object> d, string k)
        {
            object v;
            if (d.TryGetValue(k, out v))
            {
                var l = v as List<object>;
                if (l != null) return l;
                var e = v as IEnumerable;
                if (e != null && !(v is string)) { l = new List<object>(); foreach (object x in e) l.Add(x); return l; }
            }
            return new List<object>();
        }
    }
}
