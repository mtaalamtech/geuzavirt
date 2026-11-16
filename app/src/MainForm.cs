using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Mtaalam.V2V
{
    // Main window: the conversion tasks, live progress (speed, time left) and the log.
    // Everything about a conversion is set in the "Convert machine" wizard.
    class MainForm : Form
    {
        const string Website = "https://mtaalamtech.com";

        readonly Settings settings = Settings.Load();
        readonly List<VmxInfo> tasks = new List<VmxInfo>();
        readonly Dictionary<VmxInfo, string> destOf = new Dictionary<VmxInfo, string>();
        readonly Dictionary<VmxInfo, double> progressOf = new Dictionary<VmxInfo, double>();
        readonly List<string> pendingFiles = new List<string>();
        readonly Dictionary<VmxInfo, ConversionJob> jobOf = new Dictionary<VmxInfo, ConversionJob>();   // for Retry
        readonly Dictionary<VmxInfo, Job> runOf = new Dictionary<VmxInfo, Job>();                       // its latest run
        readonly Dictionary<VmxInfo, double> durationOf = new Dictionary<VmxInfo, double>();            // seconds, once it ended

        ListView list;
        Label emptyHint, lblCurrent, lblOverall, lblLogTitle, lastCardTitle;
        Label vSpeed, vMoved, vElapsed, vLeftVm, vLeftAll;
        FlatProgress barCurrent, barOverall;
        Button btnWizard, btnCancel, btnRetry, btnFinalize, btnPrep, btnLogs, btnClear;
        RichTextBox log;

        SshClient ssh;            // last Proxmox host (reused by the wizard and Finalize)
        HostInfo hostInfo;
        bool busy;                // Finalize Windows VM is running
        StreamWriter logFile;     // lines that belong to no conversion (each conversion has its own RunLog)
        bool startupLines = true; // the greeting is not worth a file

        // One "Convert machine" run. Several can run side by side. It is kept after it ended, so
        // clicking its task shows what happened.
        class Job
        {
            public Converter Proxmox;
            public VmwareConverter Vmware;
            public readonly List<VmxInfo> Vms = new List<VmxInfo>();
            public VmxInfo Current;
            public double CurFrac, AllFrac;
            public string Label = "";
            public readonly Stopwatch VmClock = new Stopwatch();
            public readonly Stopwatch Clock = new Stopwatch();
            public DateTime Started, EndedAt;
            public bool Ended;
            public RunLog LogFile;
            public long Moved;   // bytes, added from worker threads
            public string Speed = "-", LeftVm = "-";
            public readonly List<KeyValuePair<double, long>> ByteSamples = new List<KeyValuePair<double, long>>();
            public readonly List<KeyValuePair<double, double>> CurSamples = new List<KeyValuePair<double, double>>();
            public void Cancel()
            {
                if (Proxmox != null) Proxmox.Cancel();
                if (Vmware != null) Vmware.Cancel();
            }
        }
        readonly List<Job> jobs = new List<Job>();   // the running ones
        Job lastRun;                                 // the one that ended last

        // every line of this session; the log box shows all of them or those of the selected task's run
        class LogLine
        {
            public string Text;
            public LogKind Kind;
            public Job Run;
        }
        readonly List<LogLine> logLines = new List<LogLine>();
        Job logView;
        bool syncPending;

        // live statistics
        readonly Stopwatch jobClock = new Stopwatch();
        readonly List<KeyValuePair<double, double>> allSamples = new List<KeyValuePair<double, double>>();
        string leftAll = "-";
        readonly System.Windows.Forms.Timer statsTimer = new System.Windows.Forms.Timer { Interval = 1000 };

        bool Running { get { return jobs.Count > 0; } }

        VmxInfo SelectedVm()
        {
            return list != null && list.SelectedItems.Count == 1 ? list.SelectedItems[0].Tag as VmxInfo : null;
        }

        // the running conversion Cancel applies to: the selected task's, else the newest
        Job ShownJob()
        {
            if (jobs.Count == 0) return null;
            var vm = SelectedVm();
            if (vm != null) foreach (var j in jobs) if (j.Vms.Contains(vm)) return j;
            return jobs[jobs.Count - 1];
        }

        // the run the progress card shows: the selected task's (running or ended), else the newest
        Job ShownRun()
        {
            var vm = SelectedVm();
            Job r;
            if (vm != null && runOf.TryGetValue(vm, out r)) return r;
            return jobs.Count > 0 ? jobs[jobs.Count - 1] : lastRun;
        }

        // all running conversions together, weighted by their number of machines
        double OverallFrac()
        {
            double sum = 0;
            int n = 0;
            foreach (var j in jobs) { sum += j.AllFrac * j.Vms.Count; n += j.Vms.Count; }
            return n > 0 ? sum / n : 0;
        }

        public MainForm(string[] files)
        {
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = Program.Product + " - " + Program.Company;
            Font = new Font("Segoe UI", 9.5f);
            BackColor = Theme.Bg0;
            ForeColor = Theme.Text1;
            Size = new Size(1300, 860);
            MinimumSize = new Size(1000, 700);
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch (Exception) { }

            BuildUi();
            statsTimer.Tick += (s, e) => UpdateStats();
            UpdateUi();
            Log(Program.Product + " " + Program.Version + "  |  " + Website, LogKind.Step);
            Log("Click \"Convert machine...\" to pick a source (running Windows or Linux machine, ESXi VM, Proxmox VE VM or VMware files) " +
                "and a destination (Proxmox VE or VMware ESXi).", LogKind.Info);
            startupLines = false;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { RunLog.TidyOld(RunLog.Folder); }
                catch (Exception) { }
            });
            if (SshClient.FindSsh() == null)
                Log("OpenSSH client is missing: install it under Settings > System > Optional features > OpenSSH Client.", LogKind.Error);
            foreach (string f in files)
            {
                if (Directory.Exists(f)) pendingFiles.AddRange(FindVmx(f));
                else if (f.EndsWith(".vmx", StringComparison.OrdinalIgnoreCase) && File.Exists(f)) pendingFiles.Add(Path.GetFullPath(f));
            }
        }

        // ---------------------------------------------------------------- layout

        void BuildUi()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var header = new HeaderPanel();
            header.Font = Font;
            root.Controls.Add(header, 0, 0);

            var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(10, 8, 10, 4) };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 42));
            body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 58));
            body.Controls.Add(Card("01", "Conversion tasks", BuildTasks(), true), 0, 0);
            body.Controls.Add(Card("02", "Progress", BuildProgress(), false), 0, 1);
            body.Controls.Add(Card("", "Activity log", BuildLog(), true), 0, 2);
            lblLogTitle = lastCardTitle;
            root.Controls.Add(body, 0, 1);
            root.Controls.Add(BuildFooter(), 0, 2);
            Controls.Add(root);
        }

        Control Card(string kicker, string title, Control content, bool fill)
        {
            var card = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Bg1,
                Padding = new Padding(14, 10, 14, 14),
                Margin = new Padding(6),
                ColumnCount = 1,
                RowCount = 2,
                AutoSize = !fill,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
            };
            card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            card.RowStyles.Add(fill ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));
            card.Paint += (s, e) =>
            {
                using (var pen = new Pen(Theme.Line))
                    e.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1);
            };
            var head = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 0, 0, 8), WrapContents = false };
            if (kicker.Length > 0)
                head.Controls.Add(new Label { Text = kicker, AutoSize = true, ForeColor = Theme.Accent, Font = new Font("Consolas", 10f, FontStyle.Bold), Margin = new Padding(0, 1, 6, 0) });
            lastCardTitle = new Label { Text = title, AutoSize = true, ForeColor = Theme.Text0, Font = new Font("Segoe UI Semibold", 11f), Margin = Padding.Empty };
            head.Controls.Add(lastCardTitle);
            content.Dock = DockStyle.Fill;
            card.Controls.Add(head, 0, 0);
            card.Controls.Add(content, 0, 1);
            return card;
        }

        Control BuildTasks()
        {
            var panel = new TableLayoutPanel { ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var host = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 10), BackColor = Theme.Bg2 };
            list = new ListView
            {
                Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, BorderStyle = BorderStyle.None,
                BackColor = Theme.Bg2, ForeColor = Theme.Text0, OwnerDraw = true, AllowDrop = true, ShowItemToolTips = true,
            };
            float scale;
            using (var g = CreateGraphics()) scale = g.DpiX / 96f;
            foreach (var col in new[] { "Machine|190", "Source|220", "Destination|240", "Progress|80", "Status|200" })
            {
                string[] p = col.Split('|');
                list.Columns.Add(p[0], (int)(int.Parse(p[1]) * scale));
            }
            list.ClientSizeChanged += (s, e) => FitStatusColumn();
            list.DrawColumnHeader += (s, e) =>
            {
                using (var b = new SolidBrush(Theme.Bg3)) e.Graphics.FillRectangle(b, e.Bounds);
                var r = e.Bounds;
                r.Inflate(-6, 0);
                TextRenderer.DrawText(e.Graphics, e.Header.Text, list.Font, r, Theme.Text1,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            };
            list.DrawItem += (s, e) => e.DrawDefault = true;
            list.DrawSubItem += (s, e) => e.DrawDefault = true;
            list.SelectedIndexChanged += (s, e) => { UpdateUi(); SyncSelectionLater(); };
            list.DoubleClick += (s, e) => OpenWizard();
            WireDrop(list);

            emptyHint = new Label
            {
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Theme.Text1, BackColor = Theme.Bg2,
                Text = "No conversions yet.\r\nClick \"Convert machine...\" below, or drop VMware .vmx files / VM folders here.",
                AllowDrop = true, Cursor = Cursors.Hand,
            };
            emptyHint.Click += (s, e) => OpenWizard();
            WireDrop(emptyHint);
            host.Controls.Add(list);
            host.Controls.Add(emptyHint);
            emptyHint.BringToFront();

            var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = Padding.Empty };
            btnWizard = Theme.MakeButton("Convert machine...", true);
            btnWizard.Padding = new Padding(18, 6, 18, 6);
            btnCancel = Theme.MakeButton("Cancel", false);
            btnRetry = Theme.MakeButton("Retry", false);
            btnFinalize = Theme.MakeButton("Finalize Windows VM", false);
            btnPrep = Theme.MakeButton("Guest prep scripts", false);
            btnLogs = Theme.MakeButton("Open logs", false);
            btnClear = Theme.MakeButton("Clear finished", false);
            btnWizard.Click += (s, e) => OpenWizard();
            btnCancel.Click += (s, e) => CancelJobs();
            btnRetry.Click += (s, e) => RetryTasks();
            btnFinalize.Visible = false;   // only when a converted VM needs it
            btnFinalize.Click += (s, e) => FinalizeSelected();
            btnPrep.Click += (s, e) => ShowGuestPrep();
            btnLogs.Click += (s, e) => OpenLogs();
            btnClear.Click += (s, e) => ClearFinished();
            var tips = new ToolTip();
            tips.SetToolTip(btnWizard, "Step by step: the source (this PC, a running Windows or Linux machine, an ESXi VM, a Proxmox VE VM or VMware files)\n" +
                "and the destination (Proxmox VE VM or template, VMware ESXi VM, or VMware OVF template).");
            tips.SetToolTip(btnFinalize, "Shown only when a converted Windows VM still runs on SATA or its disk still has to be cut.\n" +
                "After installing the VirtIO drivers in it and shutting it down, this switches the disks and network card to VirtIO.");
            tips.SetToolTip(btnPrep, "What the preparation scripts do, when you need them and how to run them.");
            tips.SetToolTip(btnRetry, "Converts the selected failed, cancelled or interrupted tasks again with the same settings\n" +
                "(all of them if none is selected). A VM the failed attempt left on Proxmox is removed first.");
            buttons.Controls.AddRange(new Control[] { btnWizard, btnCancel, btnRetry, btnFinalize, btnPrep, btnLogs, btnClear });
            foreach (Control b in buttons.Controls) b.Margin = new Padding(0, 0, 8, 0);

            panel.Controls.Add(host, 0, 0);
            panel.Controls.Add(buttons, 0, 1);
            return panel;
        }

        Control BuildProgress()
        {
            var grid = new TableLayoutPanel { ColumnCount = 1, RowCount = 5, AutoSize = true, Margin = Padding.Empty };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            lblCurrent = new Label { AutoSize = true, ForeColor = Theme.Text0, Text = "Idle", Margin = new Padding(0, 0, 0, 3), AutoEllipsis = true };
            barCurrent = new FlatProgress { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 8) };
            lblOverall = new Label { AutoSize = true, ForeColor = Theme.Text1, Text = "Overall", Margin = new Padding(0, 0, 0, 3) };
            barOverall = new FlatProgress { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 10) };

            var stats = new TableLayoutPanel { ColumnCount = 5, RowCount = 2, AutoSize = true, Dock = DockStyle.Fill, Margin = Padding.Empty };
            for (int i = 0; i < 5; i++) stats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
            Func<string, Label> stat = caption =>
            {
                int col = stats.Controls.Count / 2;
                stats.Controls.Add(new Label { Text = caption, AutoSize = true, ForeColor = Theme.Text1, Margin = new Padding(0, 0, 8, 1) }, col, 0);
                var v = new Label { Text = "-", AutoSize = true, ForeColor = Theme.Text0, Font = new Font("Segoe UI Semibold", 12.5f), Margin = new Padding(0, 0, 8, 0) };
                stats.Controls.Add(v, col, 1);
                return v;
            };
            vSpeed = stat("Network speed");
            vMoved = stat("Transferred");
            vElapsed = stat("Elapsed");
            vLeftVm = stat("Time left (this machine)");
            vLeftAll = stat("Time left (all)");

            grid.Controls.Add(lblCurrent, 0, 0);
            grid.Controls.Add(barCurrent, 0, 1);
            grid.Controls.Add(lblOverall, 0, 2);
            grid.Controls.Add(barOverall, 0, 3);
            grid.Controls.Add(stats, 0, 4);
            barCurrent.Height = barOverall.Height = 10;
            return grid;
        }

        Control BuildLog()
        {
            log = new RichTextBox
            {
                ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Theme.Bg0, ForeColor = Theme.Text1,
                Font = new Font("Consolas", 9f), WordWrap = true, ScrollBars = RichTextBoxScrollBars.Vertical, DetectUrls = false,
            };
            return log;
        }

        Control BuildFooter()
        {
            var bar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, BackColor = Theme.Bg1, Padding = new Padding(16, 6, 16, 6), Margin = Padding.Empty };
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            string text = "\u00A9 " + DateTime.Now.Year + " " + Program.Company + "  \u00B7  mtaalamtech.com  \u00B7  info@mtaalamtech.com";
            var link = new LinkLabel
            {
                Text = text, AutoSize = true, ForeColor = Theme.Text1, LinkColor = Theme.Accent, ActiveLinkColor = Theme.Accent2,
                VisitedLinkColor = Theme.Accent, LinkBehavior = LinkBehavior.HoverUnderline, Margin = Padding.Empty,
            };
            link.LinkArea = new LinkArea(text.IndexOf("mtaalamtech.com"), "mtaalamtech.com".Length);
            link.LinkClicked += (s, e) => OpenUrl(Website);
            bar.Controls.Add(link, 0, 0);
            bar.Controls.Add(new Label { Text = "v" + Program.Version, AutoSize = true, ForeColor = Theme.Text1, Margin = Padding.Empty }, 1, 0);
            return bar;
        }

        void FitStatusColumn()
        {
            if (list.Columns.Count == 0) return;
            int used = 0;
            for (int i = 0; i < list.Columns.Count - 1; i++) used += list.Columns[i].Width;
            list.Columns[list.Columns.Count - 1].Width = Math.Max(120, list.ClientSize.Width - used - 2);
        }

        // Size the window for the screen's DPI (125%/150% displays), never larger than the screen.
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            float s;
            using (var g = CreateGraphics()) s = g.DpiX / 96f;
            var area = Screen.FromControl(this).WorkingArea;
            MinimumSize = new Size(Math.Min((int)(960 * s), area.Width), Math.Min((int)(640 * s), area.Height));
            Size = new Size(Math.Min((int)(1300 * s), area.Width), Math.Min((int)(880 * s), area.Height));
            Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            FitStatusColumn();
            if (pendingFiles.Count > 0) BeginInvoke(new Action(OpenWizard));
        }

        // ---------------------------------------------------------------- .vmx files dropped on the window

        void WireDrop(Control c)
        {
            c.DragEnter += (s, e) => { e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; };
            c.DragDrop += (s, e) =>
            {
                var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (paths == null) return;
                foreach (string p in paths)
                {
                    if (Directory.Exists(p)) pendingFiles.AddRange(FindVmx(p));
                    else if (p.EndsWith(".vmx", StringComparison.OrdinalIgnoreCase)) pendingFiles.Add(Path.GetFullPath(p));
                }
                if (pendingFiles.Count == 0) { Log("No .vmx files found there.", LogKind.Warn); return; }
                BeginInvoke(new Action(OpenWizard));
            };
        }

        static List<string> FindVmx(string root)
        {
            var found = new List<string>();
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                string dir = pending.Pop();
                try
                {
                    found.AddRange(Directory.GetFiles(dir, "*.vmx"));
                    foreach (string sub in Directory.GetDirectories(dir)) pending.Push(sub);
                }
                catch (Exception) { }
            }
            return found;
        }

        // ---------------------------------------------------------------- wizard and jobs

        void OpenWizard()
        {
            var listed = new List<VmxInfo>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string f in pendingFiles)
            {
                if (!seen.Add(f)) continue;
                var vm = VmxInfo.Load(f);
                if (vm.Error != null) { Log(vm.DisplayName + ": " + vm.Error, LogKind.Error); continue; }
                foreach (string w in vm.Warnings) Log(vm.DisplayName + ": " + w, LogKind.Warn);
                listed.Add(vm);
            }
            bool files = listed.Count > 0;
            pendingFiles.Clear();
            ConversionJob job;
            using (var w = new ConvertWizard(settings, null, ssh, hostInfo, listed))
            {
                if (files) w.StartWithFiles();
                if (w.ShowDialog(this) != DialogResult.OK || w.Job == null) return;
                job = w.Job;
            }
            RunJob(job);
        }

        static string DestText(ConversionJob job, VmxInfo vm)
        {
            switch (job.Dest)
            {
                case DestKind.ProxmoxVm:
                case DestKind.ProxmoxTemplate:
                    return "Proxmox VE " + (job.Host != null ? job.Host.Node : "") + (vm.TargetVmid > 0 ? " · ID " + vm.TargetVmid : "") +
                           (job.Dest == DestKind.ProxmoxTemplate ? " (template)" : "");
                case DestKind.EsxiVm:
                    return "ESXi " + (job.Vmware.Target != null ? job.Vmware.Target.Host : "");
                default:
                    return "OVF package · " + job.Vmware.Folder;
            }
        }

        void RunJob(ConversionJob job)
        {
            if (job.Ssh != null) { ssh = job.Ssh; hostInfo = job.Host; }
            foreach (var vm in job.Vms)
            {
                foreach (var other in jobs)
                    if (other.Vms.Contains(vm))
                    {
                        Warn(vm.DisplayName + " is still being converted. Wait until that conversion has finished, or cancel it first.");
                        return;
                    }
            }
            var run = new Job { Started = DateTime.Now };
            run.Clock.Start();
            run.Vms.AddRange(job.Vms);
            foreach (var vm in job.Vms)
            {
                jobOf[vm] = job;
                runOf[vm] = run;
                durationOf.Remove(vm);
                if (!tasks.Contains(vm)) tasks.Add(vm);   // a retry keeps its place in the list
                destOf[vm] = DestText(job, vm);
                progressOf[vm] = 0;
                vm.State = VmState.Ready;
                vm.StatusText = "Waiting";
                vm.NeedsFinalize = false;
            }
            RebuildList();

            var machineNames = job.Vms.ConvertAll(v => v.DisplayName);
            run.LogFile = RunLog.Start(RunLog.Folder, machineNames, run.Started, Program.Product + " " + Program.Version + "  |  " + Website);
            string machines = machineNames.Count == 1 ? machineNames[0] : machineNames.Count + " machines (" + string.Join(", ", machineNames.ToArray()) + ")";
            Log("Conversion: " + machines + " -> " + ConversionJob.DestLabel(job.Dest) +
                (jobs.Count > 0 ? "  (runs alongside " + jobs.Count + " other conversion" + (jobs.Count > 1 ? "s" : "") + ")" : ""), LogKind.Step, run);
            if (jobs.Count == 0) StartStats();
            jobs.Add(run);
            Action<string, double, double> progress = (label, current, overall) =>
            {
                run.CurFrac = current;
                run.AllFrac = overall;
                run.Label = label;
                Ui(() =>
                {
                    if (run.Current != null) { progressOf[run.Current] = current; RefreshItem(run.Current); }
                    ShowProgress();
                });
            };
            Action<VmxInfo> changed = vm => Ui(() =>
            {
                if (vm.State == VmState.Running && vm != run.Current) { run.Current = vm; run.CurFrac = 0; run.VmClock.Restart(); run.CurSamples.Clear(); }
                if (vm.State == VmState.Done) progressOf[vm] = 1;
                if (vm == run.Current && (vm.State == VmState.Done || vm.State == VmState.Failed || vm.State == VmState.Cancelled))
                    NoteDuration(run, vm);
                RefreshItem(vm);
                UpdateUi();
            });
            Action<long> onMoved = n => Interlocked.Add(ref run.Moved, n);
            Action finished = () => Ui(() =>
            {
                jobs.Remove(run);
                run.Clock.Stop();
                run.Ended = true;
                run.EndedAt = DateTime.Now;
                lastRun = run;
                if (run.Current != null) NoteDuration(run, run.Current);
                run.VmClock.Stop();
                if (run.Vms.Count > 1)
                    Log("The whole conversion took " + Theme.Duration(run.Clock.Elapsed.TotalSeconds) + ".", LogKind.Step, run);
                if (run.LogFile != null)
                    run.LogFile.Finish(RunLog.Result(run.Vms.FindAll(v => v.State == VmState.Done).Count, run.Vms.Count,
                                                     run.Vms.Exists(v => v.State == VmState.Cancelled) && !run.Vms.Exists(v => v.State == VmState.Failed)));
                if (jobs.Count == 0) StopStats();
                else ShowProgress();
                UpdateUi();
            });

            Action work;
            if (job.ToProxmox)
            {
                if (job.Vms.Exists(v => v.IsWindows && !v.HotClone && v.Pve == null) && !job.Proxmox.VirtioReady)
                    Log("Windows VMs will boot on SATA first. After installing the VirtIO drivers inside them, shut them down and click \"Finalize Windows VM\".", LogKind.Info, run);
                // own SSH session state: cancelling this run must not stop the commands of another one
                var conv = new Converter(job.Ssh.Clone());
                conv.Log += (text, kind) => Ui(() => Log(text, kind, run));
                conv.VmChanged += changed;
                conv.Progress += progress;
                conv.Moved += onMoved;
                run.Proxmox = conv;
                work = () => conv.Run(job.Vms, job.Proxmox);
            }
            else
            {
                var conv = new VmwareConverter();
                conv.Log += (text, kind) => Ui(() => Log(text, kind, run));
                conv.VmChanged += changed;
                conv.Progress += progress;
                conv.Moved += onMoved;
                run.Vmware = conv;
                work = () => conv.Run(job.Vms, job.Vmware);
            }
            var t = new Thread(() =>
            {
                try { work(); }
                catch (Exception ex) { Ui(() => Log(ex.Message, LogKind.Error, run)); }
                finally { finished(); }
            });
            t.IsBackground = true;
            t.Start();
            UpdateUi();
        }

        // Remembers how long a machine took and says so in the log (once per run).
        void NoteDuration(Job run, VmxInfo vm)
        {
            if (durationOf.ContainsKey(vm)) return;
            double secs = run.VmClock.Elapsed.TotalSeconds;
            run.VmClock.Stop();
            durationOf[vm] = secs;
            string what = vm.State == VmState.Done ? "Conversion took " : vm.State == VmState.Cancelled ? "Cancelled after " : "Stopped after ";
            Log(vm.DisplayName + ": " + what + Theme.Duration(secs) + ".", vm.State == VmState.Done ? LogKind.Ok : LogKind.Warn, run);
        }

        // Clicking a task changes selection twice (old one off, new one on): act once, afterwards.
        void SyncSelectionLater()
        {
            if (syncPending || !IsHandleCreated) return;
            syncPending = true;
            BeginInvoke(new Action(() => { syncPending = false; SyncSelection(); }));
        }

        // The selected task decides what the progress card and the activity log show.
        void SyncSelection()
        {
            var vm = SelectedVm();
            Job r = null;
            if (vm != null) runOf.TryGetValue(vm, out r);
            if (r != logView) { logView = r; RedrawLog(); }
            lblLogTitle.Text = r != null ? "Activity log  -  " + vm.DisplayName + "  (click an empty part of the list to see all)" : "Activity log";
            ShowProgress();
        }

        // The progress card: the selected task (running or ended), else the newest conversion.
        void ShowProgress()
        {
            var r = ShownRun();
            if (r == null)
            {
                lblCurrent.Text = "Idle";
                lblOverall.Text = "Overall";
                barCurrent.Value = barOverall.Value = 0;
                vSpeed.Text = vMoved.Text = vElapsed.Text = vLeftVm.Text = vLeftAll.Text = "-";
                return;
            }
            var vm = SelectedVm();
            if (vm == null || !r.Vms.Contains(vm)) vm = r.Current;
            long bytes = Interlocked.Read(ref r.Moved);
            vMoved.Text = bytes > 0 ? Theme.Bytes(bytes) : "-";
            if (r.Ended || (vm != null && durationOf.ContainsKey(vm))) { ShowEnded(r, vm, bytes); return; }

            string who = jobs.Count > 1 && r.Current != null ? r.Current.DisplayName + ":  " : "";
            lblCurrent.Text = who + (r.Label.Length > 0 ? r.Label : "Starting...");
            barCurrent.Value = r.CurFrac;
            double all = OverallFrac();
            barOverall.Value = all;
            lblOverall.Text = "Overall  " + (int)(all * 100) + "%" + (jobs.Count > 1 ? "  (" + jobs.Count + " conversions running)" : "");
            double t = r.Clock.Elapsed.TotalSeconds;
            double vmT = r.Current != null ? r.VmClock.Elapsed.TotalSeconds : 0;
            vSpeed.Text = r.Speed;
            vElapsed.Text = Theme.Duration(t) + (vmT > 0 && vmT + 2 < t ? "  (this machine " + Theme.Duration(vmT) + ")" : "");
            vLeftVm.Text = r.LeftVm;
            vLeftAll.Text = leftAll;
        }

        // A machine or run that has ended: its result, how long it took and how much it moved.
        void ShowEnded(Job r, VmxInfo vm, long bytes)
        {
            if (vm == null && r.Vms.Count > 0) vm = r.Vms[r.Vms.Count - 1];
            double p = 0, secs;
            if (vm != null) progressOf.TryGetValue(vm, out p);
            if (vm != null && vm.State == VmState.Done) p = 1;
            lblCurrent.Text = vm != null ? vm.DisplayName + ":  " + vm.StatusText : r.Label;
            barCurrent.Value = barOverall.Value = p;
            lblOverall.Text = "Started " + r.Started.ToString("d MMM HH:mm:ss") + (r.Ended ? ",  ended " + r.EndedAt.ToString("d MMM HH:mm:ss") : "") +
                              (jobs.Count > 0 ? "  (" + jobs.Count + " conversion" + (jobs.Count > 1 ? "s" : "") + " running)" : "");
            double total = r.Clock.Elapsed.TotalSeconds;
            if (vm == null || !durationOf.TryGetValue(vm, out secs)) secs = total;
            vElapsed.Text = Theme.Duration(secs) + (r.Vms.Count > 1 && r.Ended ? "  (whole run " + Theme.Duration(total) + ")" : "");
            vSpeed.Text = bytes > 0 && total >= 1 ? Theme.Bytes((long)(bytes / total)) + "/s average" : "-";
            vLeftVm.Text = "-";
            vLeftAll.Text = jobs.Count > 0 ? leftAll : "-";
        }

        void CancelJobs()
        {
            if (jobs.Count == 0) return;
            var f = ShownJob();
            if (jobs.Count > 1)
            {
                var answer = MessageBox.Show(this,
                    jobs.Count + " conversions are running.\n\nYes: cancel only the one of the selected task" +
                    (f != null && f.Current != null ? " (" + f.Current.DisplayName + ")" : "") + ".\nNo: cancel all of them.",
                    Program.Product, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (answer == DialogResult.Cancel) return;
                if (answer == DialogResult.No)
                {
                    foreach (var j in jobs) { j.Cancel(); Log("Cancelling" + (j.Current != null ? " " + j.Current.DisplayName : "") + "...", LogKind.Warn, j); }
                    return;
                }
            }
            if (f != null) { f.Cancel(); Log("Cancelling" + (f.Current != null ? " " + f.Current.DisplayName : "") + "...", LogKind.Warn, f); }
        }

        void RebuildList()
        {
            var selected = new HashSet<VmxInfo>();
            foreach (ListViewItem item in list.SelectedItems) selected.Add((VmxInfo)item.Tag);
            list.BeginUpdate();
            list.Items.Clear();
            foreach (var vm in tasks)
            {
                var item = new ListViewItem(vm.DisplayName) { Tag = vm };
                for (int c = 1; c < list.Columns.Count; c++) item.SubItems.Add("");
                list.Items.Add(item);
                item.Selected = selected.Contains(vm);
            }
            list.EndUpdate();
            foreach (var vm in tasks) RefreshItem(vm);
            FitStatusColumn();
            UpdateUi();
        }

        void RefreshItem(VmxInfo vm)
        {
            foreach (ListViewItem item in list.Items)
            {
                if (item.Tag != vm) continue;
                item.Text = vm.ProxmoxName == vm.DisplayName ? vm.DisplayName : vm.DisplayName + " → " + vm.ProxmoxName;
                item.SubItems[1].Text = vm.SourceLabel;
                string dest;
                item.SubItems[2].Text = destOf.TryGetValue(vm, out dest) ? dest : "";
                double p;
                progressOf.TryGetValue(vm, out p);
                item.SubItems[3].Text = vm.State == VmState.Done ? "100%" : vm.State == VmState.Running || p > 0 ? (int)(p * 100) + "%" : "";
                item.SubItems[4].Text = vm.StatusText;
                item.ToolTipText = vm.PowerLabel + "\n" + vm.StatusText;
                switch (vm.State)
                {
                    case VmState.Done: item.ForeColor = Theme.Accent; break;
                    case VmState.Running: item.ForeColor = Theme.Blue; break;
                    case VmState.Warning:
                    case VmState.Cancelled: item.ForeColor = Theme.Accent2; break;
                    case VmState.Blocked:
                    case VmState.Failed: item.ForeColor = Theme.Red; break;
                    default: item.ForeColor = Theme.Text0; break;
                }
            }
        }

        // failed, cancelled or never started (its run was cancelled or the connection dropped) - and not running now
        bool Retryable(VmxInfo vm)
        {
            return jobOf.ContainsKey(vm) && !jobs.Exists(j => j.Vms.Contains(vm)) &&
                   (vm.State == VmState.Failed || vm.State == VmState.Cancelled || vm.State == VmState.Blocked || vm.State == VmState.Ready);
        }

        // Runs the selected failed tasks (else all of them) again with the settings of their
        // conversion; a live copy takes new snapshots. A VM a failed attempt left on Proxmox is removed first.
        void RetryTasks()
        {
            var pick = new List<VmxInfo>();
            foreach (ListViewItem item in list.SelectedItems)
            {
                var vm = (VmxInfo)item.Tag;
                if (Retryable(vm)) pick.Add(vm);
            }
            if (pick.Count == 0) pick = tasks.FindAll(Retryable);
            if (pick.Count == 0) return;

            var text = new StringBuilder();
            text.Append("Convert again with the same settings:\n");
            foreach (var vm in pick) text.Append("    ").Append(vm.DisplayName).Append("\n");
            var partial = pick.FindAll(v => v.PartialVmid > 0);
            if (partial.Count > 0)
            {
                text.Append("\nThe failed attempt left ");
                text.Append(string.Join(", ", partial.ConvertAll(v => "VM " + v.PartialVmid + " (" + v.PartialName + ")").ToArray()));
                text.Append(" on Proxmox. It holds no usable data and is removed first.\n");
            }
            if (pick.Exists(v => v.HotClone || v.Linux != null))
                text.Append("\nRunning machines are copied from new snapshots (the state they are in now).\n");
            if (MessageBox.Show(this, text.ToString(), Program.Product, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                return;

            // one run per original conversion (same destination and options)
            var groups = new List<KeyValuePair<ConversionJob, List<VmxInfo>>>();
            foreach (var vm in pick)
            {
                var src = jobOf[vm];
                int g = groups.FindIndex(p => p.Key == src);
                if (g < 0) { groups.Add(new KeyValuePair<ConversionJob, List<VmxInfo>>(src, new List<VmxInfo>())); g = groups.Count - 1; }
                groups[g].Value.Add(vm);
            }
            foreach (var g in groups)
            {
                var src = g.Key;
                var again = new ConversionJob
                {
                    Source = src.Source, Dest = src.Dest, SourceEsxi = src.SourceEsxi, Ssh = src.Ssh, Host = src.Host,
                    Proxmox = src.Proxmox, Vmware = src.Vmware,
                };
                again.Vms.AddRange(g.Value);
                Log("Retrying " + string.Join(", ", g.Value.ConvertAll(v => v.DisplayName).ToArray()) + "...", LogKind.Step);
                RunJob(again);
            }
        }

        void ClearFinished()
        {
            tasks.RemoveAll(v => (v.State == VmState.Done || v.State == VmState.Failed || v.State == VmState.Cancelled) &&
                                 !jobs.Exists(j => j.Vms.Contains(v)));
            foreach (var vm in new List<VmxInfo>(jobOf.Keys)) if (!tasks.Contains(vm)) jobOf.Remove(vm);
            foreach (var vm in new List<VmxInfo>(runOf.Keys)) if (!tasks.Contains(vm)) { runOf.Remove(vm); durationOf.Remove(vm); }
            if (lastRun != null && !runOf.ContainsValue(lastRun)) lastRun = null;
            RebuildList();
            SyncSelectionLater();
        }

        // ---------------------------------------------------------------- speed and time left

        void StartStats()
        {
            allSamples.Clear();
            jobClock.Restart();
            statsTimer.Start();
        }

        void StopStats()
        {
            statsTimer.Stop();
            jobClock.Stop();
            leftAll = "-";
            ShowProgress();
        }

        // remaining time from how fast the fraction grew over the last few minutes
        static string TimeLeft(List<KeyValuePair<double, double>> samples, double t, double f)
        {
            if (f >= 0.999) return "0 s";
            if (samples.Count > 0 && f < samples[samples.Count - 1].Value - 0.02) samples.Clear();   // went back: new step
            samples.Add(new KeyValuePair<double, double>(t, f));
            while (samples.Count > 2 && t - samples[0].Key > 180) samples.RemoveAt(0);
            var first = samples[0];
            double span = t - first.Key, grown = f - first.Value;
            if (span < 10 || grown <= 0.0005) return "calculating...";
            return Theme.Duration((1 - f) * span / grown);
        }

        void UpdateStats()
        {
            // every running conversion is sampled, so clicking another task shows its figures at once
            foreach (var j in jobs)
            {
                double t = j.Clock.Elapsed.TotalSeconds;
                long bytes = Interlocked.Read(ref j.Moved);
                j.ByteSamples.Add(new KeyValuePair<double, long>(t, bytes));
                while (j.ByteSamples.Count > 2 && t - j.ByteSamples[0].Key > 15) j.ByteSamples.RemoveAt(0);
                var b0 = j.ByteSamples[0];
                double span = t - b0.Key;
                j.Speed = span >= 2 ? Theme.Bytes((long)((bytes - b0.Value) / span)) + "/s" : "-";
                double vmT = j.Current != null ? j.VmClock.Elapsed.TotalSeconds : 0;
                j.LeftVm = vmT > 0 ? TimeLeft(j.CurSamples, vmT, j.CurFrac) : "-";
            }
            leftAll = jobs.Count > 0 ? TimeLeft(allSamples, jobClock.Elapsed.TotalSeconds, OverallFrac()) : "-";
            ShowProgress();
        }

        // ---------------------------------------------------------------- tools

        // The button is only there when a task needs it: that task, or the selected one of several.
        void FinalizeSelected()
        {
            VmxInfo task = SelectedVm();
            if (task == null || !task.NeedsFinalize)
            {
                var need = tasks.FindAll(v => v.NeedsFinalize);
                if (need.Count > 1) { Warn("Several machines need this. Select one of them in the list first."); return; }
                task = need.Count == 1 ? need[0] : null;
            }
            if (task != null) FinalizeVm(task);
        }

        // task == null: asks for the VM ID (a VM from an earlier session; from the Guest prep help).
        void FinalizeVm(VmxInfo task)
        {
            if (busy) return;
            int vmid = 0;
            bool shrink = false;
            if (task != null && jobs.Exists(j => j.Vms.Contains(task)))
            {
                Warn(task.DisplayName + " is still being converted. Finalize it after the conversion has finished.");
                return;
            }
            if (task != null) { vmid = task.Vmid; shrink = task.ShrinkOnBoot; }
            if (vmid == 0)
            {
                string answer = InputBox.Show(this, "Finalize Windows VM", "Proxmox VM ID to finalize (SATA/e1000e -> VirtIO):");
                if (answer == null || !int.TryParse(answer.Trim(), out vmid) || vmid <= 0) return;
                shrink = MessageBox.Show(this,
                    "Also make the disks of VM " + vmid + " smaller?\n\nAfter a live copy whose volumes the Proxmox host could not resize, " +
                    "Windows shrinks them itself at the first start. Yes cuts each disk behind its last partition (unpartitioned space at the " +
                    "end of a disk is removed).", Program.Product, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
            }
            SshTarget login = null;
            if (ssh == null)
            {
                login = ProxmoxLoginDialog.Show(this, settings);
                if (login == null) return;
            }
            if (MessageBox.Show(this,
                    "Before finalizing VM " + vmid + ":\n\n" +
                    "1. Inside Windows, run virtio-win-guest-tools.exe from the VirtIO CD (not needed if it already runs on VirtIO).\n" +
                    (shrink ? "2. Wait until Windows has shrunk the volumes (C:\\Windows\\MtaalamV2V\\agent.log shows \"shrink C:\"; Disk Management shows the free space).\n" +
                              "3. Shut the VM down in Proxmox.\n\n" +
                              "Then this cuts each disk behind its last partition, removes the temporary 1 GB disk and switches to VirtIO. Continue?"
                            : "2. Shut the VM down in Proxmox.\n\n" +
                              "Then this switches its disks to VirtIO SCSI and its network to VirtIO. Continue?"),
                    Program.Product, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            SetBusy(true);
            Log("Finalizing VM " + vmid + " ...", LogKind.Step);
            RunInBackground(() =>
            {
                if (login != null)
                {
                    var client = new SshClient(login);
                    var info = Converter.Probe(client);
                    Converter.InstallTool(client);
                    Ui(() => { ssh = client; hostInfo = info; Log("Connected to Proxmox node " + info.Node + ".", LogKind.Ok); });
                    ssh = client;
                }
                var conv = new Converter(ssh);
                int code = conv.Finalize(vmid, shrink, line => Ui(() => Log("  " + line, line.StartsWith("error") ? LogKind.Error : LogKind.Remote)));
                Ui(() =>
                {
                    if (code == 0)
                    {
                        Log("VM " + vmid + " now runs on VirtIO. Boot it and run windows-post.ps1 inside Windows.", LogKind.Ok);
                        foreach (var vm in tasks)
                            if (vm.Vmid == vmid) { vm.NeedsFinalize = false; vm.StatusText = "Imported as VM " + vmid + " (VirtIO)"; RefreshItem(vm); }
                        UpdateUi();
                    }
                    else Log("Finalize failed - see the messages above.", LogKind.Error);
                });
            }, ex => Ui(() => { Log(ex.Message, LogKind.Error); Warn(ex.Message); }));
        }

        void ShowGuestPrep()
        {
            string dir = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "guest-prep");
            bool finalize = false;
            using (var form = new Form
            {
                AutoScaleDimensions = new SizeF(96F, 96F), AutoScaleMode = AutoScaleMode.Dpi,
                Text = "Guest prep scripts - help", StartPosition = FormStartPosition.CenterParent, ShowIcon = false, ShowInTaskbar = false,
                MinimizeBox = false, MaximizeBox = false, BackColor = Theme.Bg1, ForeColor = Theme.Text0, Font = Font,
                ClientSize = new Size(820, 600), MinimumSize = new Size(560, 360),
            })
            {
                var text = new RichTextBox
                {
                    Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Theme.Bg0, ForeColor = Theme.Text0,
                    Font = new Font("Consolas", 9.5f), DetectUrls = false, ScrollBars = RichTextBoxScrollBars.Vertical, Text = GuestPrepHelp,
                };
                var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
                var close = Theme.MakeButton("Close", true);
                var open = Theme.MakeButton("Open scripts folder", false);
                var byId = Theme.MakeButton("Finalize a VM by its ID...", false);
                close.DialogResult = DialogResult.Cancel;
                open.Enabled = Directory.Exists(dir);
                open.Click += (s, e) => Process.Start("explorer.exe", "\"" + dir + "\"");
                byId.Click += (s, e) => { finalize = true; form.Close(); };
                buttons.Controls.AddRange(new Control[] { close, open, byId });
                form.Controls.Add(text);
                form.Controls.Add(buttons);
                form.CancelButton = close;
                form.ShowDialog(this);
            }
            if (finalize) FinalizeVm(null);
        }

        const string GuestPrepHelp =
            "WHEN YOU NEED THESE SCRIPTS\r\n" +
            "  Only for machines copied from VMware files (.vmx) or from a powered-off ESXi VM.\r\n" +
            "  Not needed for running Windows or Linux machines (live copy) or Proxmox VE VMs:\r\n" +
            "  the converter prepares those itself.\r\n" +
            "\r\n" +
            "HOW TO USE THEM\r\n" +
            "  The scripts are in the guest-prep folder next to this program (\"Open scripts folder\").\r\n" +
            "  Copy the script into the VM and run it there, while the VM is still on VMware.\r\n" +
            "\r\n" +
            "LINUX: linux-prep.sh (before the conversion, on VMware, as root)\r\n" +
            "    sudo bash linux-prep.sh                     add --remove-vmtools to remove VMware Tools\r\n" +
            "  - Adds the VirtIO drivers to the initramfs and installs qemu-guest-agent.\r\n" +
            "  - Keeps the network card names by tying them to their MAC addresses.\r\n" +
            "  - Makes UEFI systems bootable on Proxmox; warns about fstab entries that will change.\r\n" +
            "  Then shut the VM down and convert it.\r\n" +
            "\r\n" +
            "WINDOWS: windows-prep.ps1 (before the conversion, on VMware, PowerShell as Administrator)\r\n" +
            "  Mount virtio-win.iso in the VM first (E: below is its drive letter). Download:\r\n" +
            "  https://fedorapeople.org/groups/virt/virtio-win/direct-downloads/stable-virtio/virtio-win.iso\r\n" +
            "    powershell -ExecutionPolicy Bypass -File .\\windows-prep.ps1 -VirtioInstaller E:\\virtio-win-guest-tools.exe -RemoveVmwareTools -Shutdown\r\n" +
            "  - Saves the static IP settings for windows-post.ps1.\r\n" +
            "  - Suspends BitLocker and prints the recovery key. Write the key down.\r\n" +
            "  - -VirtioInstaller   installs the VirtIO drivers and the QEMU guest agent\r\n" +
            "  - -RemoveVmwareTools uninstalls VMware Tools (it only uninstalls cleanly on VMware)\r\n" +
            "  - -Shutdown          shuts the VM down at the end\r\n" +
            "  In the wizard, tick \"Windows already has VirtIO drivers\" if you used -VirtioInstaller.\r\n" +
            "\r\n" +
            "WINDOWS: windows-post.ps1 (after the conversion, in the VM on Proxmox, as Administrator)\r\n" +
            "    powershell -ExecutionPolicy Bypass -File .\\windows-post.ps1\r\n" +
            "  - Removes leftover VMware devices, restores the saved static IPs (matched by MAC address),\r\n" +
            "    resumes BitLocker and checks the QEMU guest agent.\r\n" +
            "\r\n" +
            "FINALIZE WINDOWS VM\r\n" +
            "  A Windows VM without VirtIO drivers starts on SATA. Install virtio-win-guest-tools.exe in it\r\n" +
            "  and shut it down; Finalize then moves it to VirtIO, which is faster. The button appears in\r\n" +
            "  the main window only when a machine needs it. For a VM from an earlier session of this\r\n" +
            "  program use \"Finalize a VM by its ID...\" below.\r\n";

        void OpenLogs()
        {
            string dir = RunLog.Folder;
            try
            {
                Directory.CreateDirectory(dir);
                // the selected task's log, else the newest conversion's
                Job run = ShownRun();
                string file = run != null && run.LogFile != null ? run.LogFile.FilePath : null;
                if (file != null && File.Exists(file)) Process.Start("explorer.exe", "/select,\"" + file + "\"");
                else Process.Start("explorer.exe", "\"" + dir + "\"");
            }
            catch (Exception ex) { Warn("Cannot open " + dir + ": " + ex.Message); }
        }

        // ---------------------------------------------------------------- plumbing

        void RunInBackground(Action work, Action<Exception> onError)
        {
            var t = new Thread(() =>
            {
                try { work(); }
                catch (Exception ex) { onError(ex); }
                finally { Ui(() => SetBusy(false)); }
            });
            t.IsBackground = true;
            t.Start();
        }

        void Ui(Action a)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(a); }
            catch (InvalidOperationException) { }
        }

        void SetBusy(bool value)
        {
            busy = value;
            UseWaitCursor = false;
            UpdateUi();
        }

        void UpdateUi()
        {
            emptyHint.Visible = tasks.Count == 0;
            btnWizard.Enabled = true;   // more conversions can run side by side
            btnFinalize.Visible = tasks.Exists(v => v.NeedsFinalize);
            btnFinalize.Enabled = !busy;
            btnClear.Enabled = tasks.Exists(v => (v.State == VmState.Done || v.State == VmState.Failed || v.State == VmState.Cancelled) &&
                                                 !jobs.Exists(j => j.Vms.Contains(v)));
            btnCancel.Enabled = Running;
            btnRetry.Enabled = tasks.Exists(Retryable);
        }

        void Log(string text, LogKind kind) { Log(text, kind, null); }

        void Log(string text, LogKind kind, Job run)
        {
            var entry = new LogLine { Text = DateTime.Now.ToString("HH:mm:ss") + "  " + text, Kind = kind, Run = run };
            logLines.Add(entry);
            if (logLines.Count > 40000) logLines.RemoveRange(0, 10000);
            if (logView == null || logView == run)
            {
                if (log.TextLength > 2000000) log.Clear();
                AppendToLog(entry);
                log.ScrollToCaret();
            }
            WriteLogFile(entry.Text, run);
        }

        void AppendToLog(LogLine entry)
        {
            Color color;
            switch (entry.Kind)
            {
                case LogKind.Step: color = Theme.Text0; break;
                case LogKind.Ok: color = Theme.Accent; break;
                case LogKind.Warn: color = Theme.Accent2; break;
                case LogKind.Error: color = Theme.Red; break;
                case LogKind.Remote: color = Theme.Text2; break;
                default: color = Theme.Text1; break;
            }
            log.SelectionStart = log.TextLength;
            log.SelectionLength = 0;
            log.SelectionColor = color;
            log.AppendText(entry.Text + "\n");
        }

        // Shows the lines of the selected task's run, or everything; the newest 5000 at most.
        void RedrawLog()
        {
            var show = logView == null ? logLines : logLines.FindAll(l => l.Run == logView);
            log.Clear();
            for (int i = Math.Max(0, show.Count - 5000); i < show.Count; i++) AppendToLog(show[i]);
            log.ScrollToCaret();
        }

        void WriteLogFile(string line, Job run)
        {
            if (run != null && run.LogFile != null) { run.LogFile.Write(line); return; }
            if (startupLines) return;
            try
            {
                if (logFile == null)
                {
                    string dir = Path.Combine(RunLog.Folder, RunLog.OtherFolder);
                    Directory.CreateDirectory(dir);
                    string path = Path.Combine(dir, DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + ".log");
                    logFile = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
                }
                logFile.WriteLine(line);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        void Warn(string message)
        {
            MessageBox.Show(this, message, Program.Product, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception) { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (Running &&
                MessageBox.Show(this, "A conversion is running. Cancel it and exit?", Program.Product,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            foreach (var j in jobs)
            {
                j.Cancel();
                if (j.LogFile != null) j.LogFile.Finish("INTERRUPTED");
            }
            settings.Save();
            if (logFile != null) logFile.Dispose();
            base.OnFormClosing(e);
        }
    }

    // Proxmox VE login for tools that need a host (Finalize Windows VM) before any conversion ran.
    static class ProxmoxLoginDialog
    {
        public static SshTarget Show(IWin32Window owner, Settings settings)
        {
            using (var form = new Form
            {
                Text = "Proxmox VE host", FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false, ShowIcon = false,
                StartPosition = FormStartPosition.CenterParent, BackColor = Theme.Bg1, ForeColor = Theme.Text0, Font = new Font("Segoe UI", 9.5f),
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(16),
            })
            {
                var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
                Func<string, string, bool, TextBox> row = (label, value, secret) =>
                {
                    var box = Theme.MakeTextBox();
                    box.Dock = DockStyle.None;
                    box.Width = 240;
                    box.Text = value;
                    box.UseSystemPasswordChar = secret;
                    grid.Controls.Add(new Label { Text = label, AutoSize = true, ForeColor = Theme.Text1, Margin = new Padding(0, 6, 12, 3) });
                    grid.Controls.Add(box);
                    return box;
                };
                var host = row("Server:", settings.Get("host", ""), false);
                var port = row("SSH port:", settings.Get("port", "22"), false);
                var user = row("User name:", settings.Get("user", "root"), false);
                var pass = row("Password:", "", true);
                var ok = Theme.MakeButton("Connect", true);
                var cancel = Theme.MakeButton("Cancel", false);
                ok.DialogResult = DialogResult.OK;
                cancel.DialogResult = DialogResult.Cancel;
                var buttons = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 10, 0, 0) };
                buttons.Controls.Add(ok);
                buttons.Controls.Add(cancel);
                grid.Controls.Add(new Label { AutoSize = true });
                grid.Controls.Add(buttons);
                form.Controls.Add(grid);
                form.AcceptButton = ok;
                form.CancelButton = cancel;
                if (form.ShowDialog(owner) != DialogResult.OK || host.Text.Trim().Length == 0) return null;
                int p;
                if (!int.TryParse(port.Text.Trim(), out p)) p = 22;
                settings.Set("host", host.Text.Trim());
                settings.Set("port", p.ToString());
                settings.Set("user", user.Text.Trim());
                settings.Save();
                return new SshTarget { Host = host.Text.Trim(), Port = p, User = user.Text.Trim().Length > 0 ? user.Text.Trim() : "root", Password = pass.Text };
            }
        }
    }

    class Settings
    {
        readonly Dictionary<string, string> values = new Dictionary<string, string>();
        static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MtaalamV2V", "settings.ini");

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (File.Exists(FilePath))
                    foreach (string line in File.ReadAllLines(FilePath))
                    {
                        int eq = line.IndexOf('=');
                        if (eq > 0) s.values[line.Substring(0, eq)] = line.Substring(eq + 1);
                    }
            }
            catch (IOException) { }
            return s;
        }

        public string Get(string key, string fallback)
        {
            string v;
            return values.TryGetValue(key, out v) && v.Length > 0 ? v : fallback;
        }

        public void Set(string key, string value)
        {
            values[key] = value;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                var lines = new List<string>();
                foreach (var kv in values) lines.Add(kv.Key + "=" + kv.Value);
                File.WriteAllLines(FilePath, lines);
            }
            catch (IOException) { }
        }
    }

    // Per-VM target settings (like StarWind's "Check settings of the new virtual machine").
    // With several VMs selected, empty fields keep each VM's own value.
    static class VmSettingsDialog
    {
        const string Default = "Default";

        public static bool Show(IWin32Window owner, List<VmxInfo> vms, List<StorageItem> storages, string defaultStorage, bool defaultThick)
        {
            var vm = vms[0];
            bool single = vms.Count == 1;
            using (var form = new Form
            {
                Text = single ? "Proxmox settings - " + vm.DisplayName : "Proxmox settings - " + vms.Count + " VMs",
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false,
                MaximizeBox = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = Theme.Bg1,
                ForeColor = Theme.Text0,
                Font = new Font("Segoe UI", 9.5f),
                Padding = new Padding(16),
            })
            {
                var grid = new TableLayoutPanel { ColumnCount = 3, AutoSize = true };
                var name = Field(vm.ProxmoxName);
                var vmid = Field(vm.TargetVmid > 0 ? vm.TargetVmid.ToString() : "");
                var cores = Field(single ? vm.ProxmoxCores.ToString() : "");
                var memory = Field(single ? vm.ProxmoxMemoryMb.ToString() : "");
                long used = 0, size = 0;
                foreach (var v in vms) { used += v.TotalBytes; size += v.VirtualBytes; }

                // Storage: [keep (several VMs)], default, then every storage of the host.
                var storage = Combo();
                if (!single) storage.Items.Add("(keep each VM's setting)");
                storage.Items.Add(Default + (defaultStorage != null ? " (" + defaultStorage + ")" : ""));
                foreach (var s in storages) storage.Items.Add(s);
                storage.SelectedIndex = 0;
                if (single && vm.TargetStorage != null)
                    for (int i = 0; i < storages.Count; i++)
                        if (storages[i].Name == vm.TargetStorage) storage.SelectedIndex = i + 1;

                var alloc = Combo();
                if (!single) alloc.Items.Add("(keep each VM's setting)");
                alloc.Items.Add(Default + " (" + (defaultThick ? "thick" : "thin") + ")");
                alloc.Items.Add("Thin - only the space in use (" + Theme.Bytes(used) + ")");
                alloc.Items.Add("Thick - the whole disk size (" + Theme.Bytes(size) + ")");
                alloc.SelectedIndex = !single || vm.TargetThick == null ? 0 : vm.TargetThick.Value ? 2 : 1;

                if (single)
                {
                    AddRow(grid, "Name", name, "");
                    AddRow(grid, "VM ID", vmid, "empty = next free");
                    AddRow(grid, "CPU cores", cores, "VMware: " + vm.Cpus);
                    AddRow(grid, "Memory (MB)", memory, "VMware: " + vm.MemoryMb);
                }
                else
                {
                    AddRow(grid, "CPU cores", cores, "empty = keep");
                    AddRow(grid, "Memory (MB)", memory, "empty = keep");
                }
                AddRow(grid, "Disk storage", storage, "");
                AddRow(grid, "Disk allocation", alloc, "");

                // Per-disk size (one VM only).
                var sizeBoxes = new List<TextBox>();
                if (single)
                    foreach (var d in vm.Disks)
                    {
                        var box = Field(((d.FinalBytes) / (double)(1L << 30)).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
                        string hint = "VMware: " + Sizing.Gb(d.CapacityBytes);
                        if (d.UsedBytes >= 0) hint += ", used " + Sizing.Gb(Sizing.CeilGiB(d.UsedBytes));
                        if (d.MinBytes > 0) hint += ", min " + Sizing.Gb(Sizing.MinBytes(d));
                        box.Tag = box.Text;   // untouched text = keep the exact current size
                        AddRow(grid, "Disk " + d.Slot + " (GB)", box, hint);
                        sizeBoxes.Add(box);
                    }

                if (single)
                {
                    var usage = new Label
                    {
                        AutoSize = true,
                        ForeColor = vm.Usage != null ? Theme.Accent : Theme.Text2,
                        MaximumSize = new Size(560, 0),
                        Margin = new Padding(0, 10, 0, 0),
                        Font = new Font("Consolas", 9f),
                        Text = Sizing.Describe(vm),
                    };
                    grid.Controls.Add(usage, 0, grid.RowCount);
                    grid.SetColumnSpan(usage, 3);
                    grid.RowCount++;
                    if (vm.Usage != null)
                    {
                        var recommend = Theme.MakeButton("Use recommended", false);
                        recommend.Margin = new Padding(0, 6, 0, 0);
                        recommend.Click += (s, e) =>
                        {
                            int rc = Sizing.Cores(vm), rm = Sizing.MemoryMb(vm);
                            if (rc > 0) cores.Text = rc.ToString();
                            if (rm > 0) memory.Text = rm.ToString();
                            for (int i = 0; i < vm.Disks.Count; i++)
                            {
                                long rd = Sizing.DiskBytes(vm.Disks[i]);
                                if (rd > 0) sizeBoxes[i].Text = (rd / (double)(1L << 30)).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
                            }
                        };
                        grid.Controls.Add(recommend, 0, grid.RowCount);
                        grid.SetColumnSpan(recommend, 3);
                        grid.RowCount++;
                    }
                }

                var info = new Label
                {
                    AutoSize = true,
                    ForeColor = Theme.Text2,
                    MaximumSize = new Size(460, 0),
                    Margin = new Padding(0, 10, 0, 10),
                    Text = (storages.Count == 0 ? "Connect to the Proxmox host to choose a storage per VM.\n\n" : "") +
                        "Thin: the Proxmox disk only takes the space in use and grows as the guest writes. " +
                        "Thick: the whole disk size is reserved on the storage now (slower to copy; on ZFS it stays thin). " +
                        (single && vm.Disks.Count > 0
                            ? "Disks can be made larger (extend the volume in the guest afterwards) or smaller down to \"min\" - " +
                              "the end of the last partition; to go lower, shrink the volume inside the guest first. Proxmox " +
                              "checks this again and keeps the original size if the new one would cut a partition. "
                            : "") +
                        "Disk bus, network card and OS type are chosen automatically for best performance on Proxmox.",
                };
                grid.Controls.Add(info, 0, grid.RowCount);
                grid.SetColumnSpan(info, 3);
                grid.RowCount++;

                var ok = Theme.MakeButton("Save", true);
                var cancel = Theme.MakeButton("Cancel", false);
                cancel.DialogResult = DialogResult.Cancel;
                var buttons = new FlowLayoutPanel { AutoSize = true, Anchor = AnchorStyles.Right };
                buttons.Controls.Add(ok);
                buttons.Controls.Add(cancel);
                grid.Controls.Add(buttons, 0, grid.RowCount);
                grid.SetColumnSpan(buttons, 3);
                form.Controls.Add(grid);
                form.AcceptButton = ok;
                form.CancelButton = cancel;

                ok.Click += (s, e) =>
                {
                    int id = 0, c = 0, m = 0;
                    if (single && vmid.Text.Trim().Length > 0 && (!int.TryParse(vmid.Text.Trim(), out id) || id < 100))
                    { MessageBox.Show(form, "VM ID must be a number of 100 or more."); return; }
                    bool keepCores = !single && cores.Text.Trim().Length == 0;
                    bool keepMemory = !single && memory.Text.Trim().Length == 0;
                    if (!keepCores && (!int.TryParse(cores.Text.Trim(), out c) || c < 1 || c > 512))
                    { MessageBox.Show(form, "CPU cores must be between 1 and 512."); return; }
                    if (!keepMemory && (!int.TryParse(memory.Text.Trim(), out m) || m < 64))
                    { MessageBox.Show(form, "Memory must be at least 64 MB."); return; }
                    var sizes = new long[sizeBoxes.Count];
                    for (int i = 0; i < sizeBoxes.Count; i++)
                    {
                        double gb;
                        var d = vm.Disks[i];
                        if (sizeBoxes[i].Text.Trim() == (string)sizeBoxes[i].Tag) { sizes[i] = d.TargetBytes; continue; }
                        if (!double.TryParse(sizeBoxes[i].Text.Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out gb) || gb < 1 || gb > 65536)
                        { MessageBox.Show(form, "Disk " + d.Slot + ": enter a size in GB (1 - 65536)."); return; }
                        sizes[i] = (long)Math.Ceiling(gb * 1024) << 20;   // whole MiB
                        if (Math.Abs(sizes[i] - d.CapacityBytes) < (1L << 20)) sizes[i] = 0;   // unchanged
                        if (sizes[i] > 0 && sizes[i] < d.CapacityBytes && d.MinBytes > 0 && sizes[i] < d.MinBytes)
                        {
                            if (vm.Agent != null && !vm.Agent.IsLinux && d.GuestDisk >= 0)
                            {
                                if (!ShrinkInGuest(form, vm, d, sizes[i], sizeBoxes[i])) return;
                            }
                            else
                            {
                                MessageBox.Show(form, "Disk " + d.Slot + ": its partitions need " + Sizing.Gb(Sizing.MinBytes(d)) + ". " +
                                    (vm.IsWindows
                                        ? "Connect the guest agent (Guest agent > Connect) and save this size again - the agent shrinks the volume inside the VM - or choose a larger size."
                                        : "Shrink the file system and partition inside the guest first (offline for most Linux file systems), or choose a larger size."));
                                return;
                            }
                        }
                        if (sizes[i] > 0 && sizes[i] < d.CapacityBytes && d.MinBytes <= 0 &&
                            MessageBox.Show(form, "Disk " + d.Slot + ": the partition layout is unknown (run the sizing agent to check it). " +
                                "If the new size would cut a partition, Proxmox keeps the original size. Continue?",
                                Program.Product, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                    }
                    for (int i = 0; i < sizes.Length; i++) vm.Disks[i].TargetBytes = sizes[i];

                    int keep = single ? -1 : 0;   // index of "(keep each VM's setting)"
                    foreach (var v in vms)
                    {
                        if (single)
                        {
                            string n = name.Text.Trim();
                            v.TargetName = n.Length == 0 || n == v.DisplayName ? null : n;
                            v.TargetVmid = id;
                        }
                        if (!keepCores) v.TargetCores = c == v.Cpus ? 0 : c;
                        if (!keepMemory) v.TargetMemoryMb = m == v.MemoryMb ? 0 : m;
                        if (storage.SelectedIndex != keep)
                        {
                            var picked = storage.SelectedItem as StorageItem;
                            v.TargetStorage = picked != null ? picked.Name : null;
                        }
                        int a = alloc.SelectedIndex - (keep + 1);   // 0 default, 1 thin, 2 thick
                        if (alloc.SelectedIndex != keep)
                            v.TargetThick = a == 0 ? (bool?)null : a == 2;
                    }
                    form.DialogResult = DialogResult.OK;
                };
                return form.ShowDialog(owner) == DialogResult.OK;
            }
        }

        // Asks the agent in the running VM to shrink the last volume of that disk.
        public static bool ShrinkInGuest(Form form, VmxInfo vm, VmDisk d, long size, TextBox box)
        {
            if (MessageBox.Show(form, "Disk " + d.Slot + " (disk " + d.GuestDisk + " in Windows): its partitions reach " + Sizing.Gb(Sizing.MinBytes(d)) + ".\n" +
                    (string.IsNullOrEmpty(d.GuestLayout) ? "" : d.GuestLayout + "\n") +
                    "\nShrink the last volume inside the running VM now, so the disk fits in " + Sizing.Gb(size) + "?\n\n" +
                    "Windows moves data to the front of the volume and shrinks it while the VM keeps running; no files are deleted. " +
                    "It can take a few minutes on a large volume.",
                    Program.Product, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return false;
            bool removeRecovery = false;
            while (true)
            {
                Dictionary<string, object> r;
                try
                {
                    bool rr = removeRecovery;
                    r = WaitDialog.Run(form, "Shrinking the volume on disk " + d.GuestDisk + " in " + vm.Agent.Label + " ...",
                        () => vm.Agent.Shrink(d.GuestDisk, size, rr));
                }
                catch (Exception ex)
                {
                    MessageBox.Show(form, ex.Message, Program.Product, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return false;
                }
                object disk;
                if (r.TryGetValue("disk", out disk) && disk is Dictionary<string, object>)
                {
                    var g = AgentReport.FromJson(new Dictionary<string, object> { { "disks", new object[] { disk } } }).Disks[0];
                    if (g.PartEnd > 0) d.MinBytes = g.PartEnd;
                    if (g.Used >= 0) d.UsedBytes = g.Used;
                    d.GuestLayout = g.Layout;
                }
                object ok;
                if (r.TryGetValue("ok", out ok) && ok is bool && (bool)ok)
                {
                    MessageBox.Show(form, AgentReport.Str(r, "message") + "\n\n" + d.GuestLayout, Program.Product, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return d.MinBytes <= size;
                }
                if (AgentReport.Str(r, "code") == "recovery" && !removeRecovery &&
                    MessageBox.Show(form, AgentReport.Str(r, "error") + "\n\nMove WinRE and delete the recovery partition?",
                        Program.Product, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                {
                    removeRecovery = true;
                    continue;
                }
                long min = (long)AgentReport.Num(r, "min_size");
                if (min > 0 && min < d.CapacityBytes)
                    box.Text = (Sizing.CeilGiB(min) / (double)(1L << 30)).ToString("0", System.Globalization.CultureInfo.InvariantCulture);
                MessageBox.Show(form, AgentReport.Str(r, "error") + (min > 0 && min < d.CapacityBytes ? "\n\nThe size box now holds the smallest size that works." : ""),
                    Program.Product, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
        }

        static TextBox Field(string value)
        {
            var box = Theme.MakeTextBox();
            box.Dock = DockStyle.None;
            box.Width = 280;
            box.Text = value;
            return box;
        }

        static ComboBox Combo()
        {
            var box = Theme.MakeCombo(false);
            box.Dock = DockStyle.None;
            box.Width = 280;
            return box;
        }

        static void AddRow(TableLayoutPanel grid, string label, Control field, string hint)
        {
            int row = grid.RowCount++;
            grid.Controls.Add(new Label { Text = label, AutoSize = true, ForeColor = Theme.Text1, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 12, 0) }, 0, row);
            grid.Controls.Add(field, 1, row);
            grid.Controls.Add(new Label { Text = hint, AutoSize = true, ForeColor = Theme.Text2, Anchor = AnchorStyles.Left, Margin = new Padding(8, 6, 0, 0) }, 2, row);
        }
    }

    static class InputBox
    {
        public static string Show(IWin32Window owner, string title, string prompt)
        {
            using (var form = new Form
            {
                Text = title,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false,
                MaximizeBox = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = Theme.Bg1,
                ForeColor = Theme.Text0,
                Font = new Font("Segoe UI", 9.5f),
                Padding = new Padding(14),
            })
            {
                var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true };
                var box = Theme.MakeTextBox();
                box.Dock = DockStyle.None;
                box.Width = 260;
                var ok = Theme.MakeButton("OK", true);
                ok.DialogResult = DialogResult.OK;
                layout.Controls.Add(new Label { Text = prompt, AutoSize = true, Margin = new Padding(0, 0, 0, 6) });
                layout.Controls.Add(box);
                layout.Controls.Add(ok);
                form.Controls.Add(layout);
                form.AcceptButton = ok;
                return form.ShowDialog(owner) == DialogResult.OK ? box.Text : null;
            }
        }
    }
}
