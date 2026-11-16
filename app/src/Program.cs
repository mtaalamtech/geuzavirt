using System;
using System.Text;
using System.Windows.Forms;

namespace Mtaalam.V2V
{
    static class Program
    {
        public const string Version = "1.4.14";
        public const string Product = "GeuzaVirt";
        public const string Company = "Core Mtaalam Technologies";

        [STAThread]
        static int Main(string[] args)
        {
            // ssh.exe calls us back as its SSH_ASKPASS helper to obtain the password.
            if (Environment.GetEnvironmentVariable("MTV2V_ASKPASS") == "1")
                return AskPass(args);

            // Build step: render the application icon.
            if (args.Length == 2 && args[0] == "--write-icon")
            {
                IconWriter.Write(args[1]);
                return 0;
            }

            // "This local machine" source: install the agent service here (started elevated).
            if (args.Length == 2 && args[0] == "--install-agent-local")
                return AgentInstaller.InstallLocalElevated(args[1]);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(args)); // .vmx paths: "Open with" / drag onto the exe
            return 0;
        }

        static int AskPass(string[] args)
        {
            string prompt = string.Join(" ", args).ToLowerInvariant();
            string answer = "";
            if (prompt.Contains("yes/no"))
                answer = "no";
            else if (!prompt.Contains("passphrase"))
                answer = Environment.GetEnvironmentVariable("MTV2V_SECRET") ?? "";

            byte[] bytes = Encoding.UTF8.GetBytes(answer + "\n");
            using (var stdout = Console.OpenStandardOutput())
                stdout.Write(bytes, 0, bytes.Length);
            return 0;
        }
    }
}
