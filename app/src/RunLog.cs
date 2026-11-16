using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace Mtaalam.V2V
{
    // One log file per conversion, named after the machine and, once it ended, its result:
    //   web01.example.com - 2026-11-09 09.57 - OK.log
    // Conversions running side by side no longer share a file.
    class RunLog
    {
        // lines that belong to no conversion (connecting, Finalize) and old logs without a conversion
        public const string OtherFolder = "Other activity";

        static readonly Regex OldName = new Regex(@"^v2v-(\d{8})-(\d{6})\.log$", RegexOptions.IgnoreCase);
        static readonly Regex OldMachine = new Regex(@"^\d\d:\d\d:\d\d  \[\d+/\d+\]\s+(.+?)(?:\s{2,}\(.*)?$");
        static readonly Regex OldFinished = new Regex(@"^\d\d:\d\d:\d\d  Finished: (\d+) of (\d+) ");

        readonly string dir, stem;
        StreamWriter writer;

        public string FilePath { get; private set; }

        public static string Folder
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MtaalamV2V", "logs"); }
        }

        RunLog(string dir, string stem)
        {
            this.dir = dir;
            this.stem = stem;
        }

        // Returns null if the file cannot be created; a conversion must not fail because of its log.
        public static RunLog Start(string dir, IList<string> machines, DateTime when, string header)
        {
            try
            {
                Directory.CreateDirectory(dir);
                var log = new RunLog(dir, Stem(machines, when));
                log.FilePath = UniquePath(dir, log.stem);
                log.writer = new StreamWriter(log.FilePath) { AutoFlush = true };
                log.writer.WriteLine(header);
                log.writer.WriteLine("Started " + when.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                return log;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        public void Write(string line)
        {
            if (writer == null) return;
            try { writer.WriteLine(line); }
            catch (IOException) { }
        }

        // Closes the file and puts the result in its name.
        public void Finish(string result)
        {
            if (writer == null) return;
            try { writer.Dispose(); }
            catch (IOException) { }
            writer = null;
            try
            {
                string target = UniquePath(dir, stem + " - " + result);
                File.Move(FilePath, target);
                FilePath = target;
            }
            catch (IOException) { }                  // e.g. the file is open in another program
            catch (UnauthorizedAccessException) { }
        }

        public static string Stem(IList<string> machines, DateTime when)
        {
            string first = SafeName(machines.Count > 0 ? machines[0] : "");
            string more = machines.Count > 1 ? " +" + (machines.Count - 1) + " more" : "";
            return first + more + " - " + when.ToString("yyyy-MM-dd HH.mm", CultureInfo.InvariantCulture);
        }

        public static string Result(int ok, int total, bool cancelled)
        {
            if (ok == total) return "OK";
            if (ok > 0) return ok + " of " + total + " OK";
            return cancelled ? "CANCELLED" : "FAILED";
        }

        // Renames logs of earlier versions (v2v-20261109-095337.log) to the same form; those without
        // a conversion move to the Other activity folder. A file still in use is left for next time.
        public static void TidyOld(string dir)
        {
            string[] files;
            try
            {
                if (!Directory.Exists(dir)) return;
                files = Directory.GetFiles(dir, "v2v-*.log");
            }
            catch (IOException) { return; }
            catch (UnauthorizedAccessException) { return; }
            foreach (string file in files)
            {
                try { TidyOne(dir, file); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        static void TidyOne(string dir, string file)
        {
            Match name = OldName.Match(Path.GetFileName(file));
            DateTime when;
            if (!name.Success || !DateTime.TryParseExact(name.Groups[1].Value + name.Groups[2].Value, "yyyyMMddHHmmss",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out when)) return;
            var machines = new List<string>();
            int ok = 0, total = 0;
            bool cancelled = false;
            foreach (string line in File.ReadAllLines(file))
            {
                Match m = OldMachine.Match(line);
                if (m.Success && !machines.Contains(m.Groups[1].Value)) machines.Add(m.Groups[1].Value);
                m = OldFinished.Match(line);
                if (m.Success) { ok += int.Parse(m.Groups[1].Value); total += int.Parse(m.Groups[2].Value); }
                if (line.IndexOf("Cancel", StringComparison.Ordinal) >= 0) cancelled = true;
            }
            if (machines.Count == 0)
            {
                string other = Path.Combine(dir, OtherFolder);
                Directory.CreateDirectory(other);
                File.Move(file, UniquePath(other, Path.GetFileNameWithoutExtension(file)));
                return;
            }
            string stem = Stem(machines, when);
            if (total > 0) stem += " - " + Result(ok, total, cancelled);
            File.Move(file, UniquePath(dir, stem));
        }

        static string SafeName(string name)
        {
            char[] chars = (name ?? "").ToCharArray();
            var invalid = new List<char>(Path.GetInvalidFileNameChars());
            for (int i = 0; i < chars.Length; i++)
                if (invalid.Contains(chars[i])) chars[i] = '_';
            string safe = new string(chars).Trim(' ', '.');
            if (safe.Length > 60) safe = safe.Substring(0, 60).Trim(' ', '.');
            return safe.Length > 0 ? safe : "conversion";
        }

        static string UniquePath(string dir, string stem)
        {
            string path = Path.Combine(dir, stem + ".log");
            for (int i = 2; File.Exists(path); i++)
                path = Path.Combine(dir, stem + " (" + i + ").log");
            return path;
        }
    }
}
