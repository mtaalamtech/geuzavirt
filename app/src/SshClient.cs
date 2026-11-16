using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace Mtaalam.V2V
{
    class SshTarget
    {
        public string Host;
        public int Port = 22;
        public string User = "root";
        public string Password;
    }

    // Drives the Windows OpenSSH client (ssh.exe). Passwords are handed over
    // through SSH_ASKPASS, which points back at this executable.
    class SshClient
    {
        readonly SshTarget target;
        volatile Process current;
        volatile bool killed;

        public string LastError { get; private set; }

        // Remote command that runs the scripts: "bash -s", or "sudo -n bash -s" / "sudo -S ..."
        // for a Linux source where the login user is not root.
        public string Shell = "bash -s";
        public string StdinPrefix;          // sent before the script (the sudo password)

        public SshTarget Target { get { return target; } }

        public SshClient(SshTarget target)
        {
            this.target = target;
        }

        public static string FindSsh()
        {
            string appDir = Path.GetDirectoryName(Application.ExecutablePath);
            string windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string[] candidates =
            {
                Path.Combine(appDir, "openssh", "ssh.exe"),
                Path.Combine(windir, "System32", "OpenSSH", "ssh.exe"),
                Path.Combine(windir, "Sysnative", "OpenSSH", "ssh.exe"),
            };
            foreach (string c in candidates)
                if (File.Exists(c)) return c;
            foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                try
                {
                    string c = Path.Combine(dir.Trim(), "ssh.exe");
                    if (dir.Trim().Length > 0 && File.Exists(c)) return c;
                }
                catch (ArgumentException) { }
            }
            return null;
        }

        static string KnownHostsFile()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MtaalamV2V");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "known_hosts");
        }

        // POSIX shell single-quoting.
        public static string Q(string s)
        {
            return "'" + s.Replace("'", "'\"'\"'") + "'";
        }

        // Windows command-line quoting (CommandLineToArgvW rules).
        static string QuoteArg(string s)
        {
            if (s.Length > 0 && s.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return s;
            var sb = new StringBuilder("\"");
            int backslashes = 0;
            foreach (char c in s)
            {
                if (c == '\\') { backslashes++; continue; }
                if (c == '"') sb.Append('\\', backslashes * 2 + 1);
                else sb.Append('\\', backslashes);
                backslashes = 0;
                sb.Append(c);
            }
            sb.Append('\\', backslashes * 2).Append('"');
            return sb.ToString();
        }

        ProcessStartInfo StartInfo(string command)
        {
            string ssh = FindSsh();
            if (ssh == null)
                throw new Exception("OpenSSH client not found. Install it under Settings > System > Optional features > OpenSSH Client.");

            var args = new StringBuilder("-T -x -p " + target.Port);
            string[] options =
            {
                "StrictHostKeyChecking=accept-new",
                "UserKnownHostsFile=\"" + KnownHostsFile().Replace('\\', '/') + "\"",
                "ConnectTimeout=15",
                "ServerAliveInterval=15",
                "ServerAliveCountMax=4",
                "NumberOfPasswordPrompts=1",
                string.IsNullOrEmpty(target.Password) ? "BatchMode=yes" : "BatchMode=no",
            };
            foreach (string o in options) args.Append(" -o ").Append(QuoteArg(o));
            args.Append(' ').Append(QuoteArg(target.User + "@" + target.Host));
            args.Append(' ').Append(QuoteArg(command));

            var psi = new ProcessStartInfo(ssh, args.ToString())
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            if (!string.IsNullOrEmpty(target.Password))
            {
                psi.EnvironmentVariables["SSH_ASKPASS"] = Application.ExecutablePath;
                psi.EnvironmentVariables["SSH_ASKPASS_REQUIRE"] = "force";
                psi.EnvironmentVariables["DISPLAY"] = "localhost:0";
                psi.EnvironmentVariables["MTV2V_ASKPASS"] = "1";
                psi.EnvironmentVariables["MTV2V_SECRET"] = target.Password;
            }
            return psi;
        }

        Process Start(string command, StringBuilder stderr, Action<string> onLine)
        {
            if (killed) throw new OperationCanceledException();
            var p = new Process { StartInfo = StartInfo(command) };
            p.OutputDataReceived += (s, e) => { if (e.Data != null && onLine != null) onLine(e.Data); };
            p.ErrorDataReceived += (s, e) =>
            {
                if (e.Data == null || e.Data.StartsWith("Warning: Permanently added")) return;
                lock (stderr) stderr.AppendLine(e.Data);
                if (onLine != null) onLine(e.Data);
            };
            p.Start();
            current = p;
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            return p;
        }

        int Finish(Process p, StringBuilder stderr)
        {
            p.WaitForExit();
            int code = p.ExitCode;
            current = null;
            p.Dispose();
            lock (stderr) LastError = stderr.ToString().Trim();
            if (killed) throw new OperationCanceledException();
            return code;
        }

        // Runs a bash script on the host; every output line goes to onLine.
        public int Run(string script, Action<string> onLine)
        {
            var stderr = new StringBuilder();
            var p = Start(Shell, stderr, onLine);
            byte[] bytes = new UTF8Encoding(false).GetBytes((StdinPrefix ?? "") + script.Replace("\r\n", "\n") + "\n");
            try
            {
                p.StandardInput.BaseStream.Write(bytes, 0, bytes.Length);
                p.StandardInput.Close();
            }
            catch (IOException) { }
            return Finish(p, stderr);
        }

        public string Capture(string script, out int code)
        {
            var output = new StringBuilder();
            code = Run(script, line => { lock (output) output.AppendLine(line); });
            return output.ToString();
        }

        // Streams a local file to the host; the target appears only once complete.
        public void Upload(Stream source, string remotePath, Action<long> onProgress)
        {
            string part = remotePath + ".part";
            var stderr = new StringBuilder();
            var p = Start("cat > " + Q(part) + " && mv -f " + Q(part) + " " + Q(remotePath), stderr, null);
            var dest = p.StandardInput.BaseStream;
            byte[] buffer = new byte[1 << 20];
            long done = 0;
            try
            {
                int n;
                while ((n = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    dest.Write(buffer, 0, n);
                    done += n;
                    if (onProgress != null) onProgress(done);
                }
                dest.Flush();
                p.StandardInput.Close();
            }
            catch (IOException) { }
            int code = Finish(p, stderr);
            if (code != 0)
                throw new Exception("Upload of " + Path.GetFileName(remotePath) + " failed: " + Explain(code));
        }

        // Runs a command and copies its binary output into dest (e.g. "cat file").
        public void Download(string command, Stream dest, Action<long> onProgress)
        {
            if (killed) throw new OperationCanceledException();
            var psi = StartInfo(command);
            psi.StandardOutputEncoding = null;
            var stderr = new StringBuilder();
            var p = new Process { StartInfo = psi };
            p.ErrorDataReceived += (s, e) =>
            {
                if (e.Data == null || e.Data.StartsWith("Warning: Permanently added")) return;
                lock (stderr) stderr.AppendLine(e.Data);
            };
            p.Start();
            current = p;
            p.BeginErrorReadLine();
            try { p.StandardInput.Close(); }
            catch (IOException) { }
            var src = p.StandardOutput.BaseStream;
            byte[] buffer = new byte[1 << 20];
            long done = 0;
            int n;
            while ((n = src.Read(buffer, 0, buffer.Length)) > 0)
            {
                dest.Write(buffer, 0, n);
                done += n;
                if (onProgress != null) onProgress(done);
            }
            int code = Finish(p, stderr);
            if (code != 0) throw new Exception("Download failed: " + Explain(code));
        }

        public string Explain(int code)
        {
            string err = LastError ?? "";
            if (err.IndexOf("Permission denied", StringComparison.OrdinalIgnoreCase) >= 0)
                return "login refused - check the user name and password, and that SSH login is allowed for that user on " + target.Host;
            if (err.Length > 0) return err;
            return "ssh exited with code " + code;
        }

        public void Reset()
        {
            killed = false;
        }

        // Same login, own process state: conversions running side by side each cancel only their own commands.
        public SshClient Clone()
        {
            return new SshClient(target) { Shell = Shell, StdinPrefix = StdinPrefix };
        }

        public void Kill()
        {
            killed = true;
            var p = current;
            try { if (p != null && !p.HasExited) p.Kill(); }
            catch (Exception) { }
        }
    }
}
