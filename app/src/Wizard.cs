using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace Mtaalam.V2V
{
    enum SourceKind { Esxi, Agent, Local, Linux, Proxmox }
    enum DestKind { ProxmoxVm, ProxmoxTemplate, EsxiVm, OvfTemplate }

    // What the "Convert machine" wizard hands to the main window to run.
    class ConversionJob
    {
        public SourceKind Source;
        public DestKind Dest;
        public List<VmxInfo> Vms = new List<VmxInfo>();
        public EsxiClient SourceEsxi;
        public SshClient Ssh;               // Proxmox destinations
        public HostInfo Host;
        public ConvertOptions Proxmox;
        public VmwareOptions Vmware;        // VMware destinations

        public bool ToProxmox { get { return Dest == DestKind.ProxmoxVm || Dest == DestKind.ProxmoxTemplate; } }

        public static string DestLabel(DestKind d)
        {
            switch (d)
            {
                case DestKind.ProxmoxVm: return "Proxmox VE virtual machine";
                case DestKind.ProxmoxTemplate: return "Proxmox VE template (for redeployment)";
                case DestKind.EsxiVm: return "VMware ESXi virtual machine";
                default: return "VMware template (OVF package)";
            }
        }
    }

    // Step-by-step conversion laid out like VMware vCenter Converter Standalone:
    // Source System -> Destination System -> Destination Virtual Machine ->
    // Destination Location -> Options -> Summary.
    class ConvertWizard : Form
    {
        const int PSource = 0, PDestSystem = 1, PDestVm = 2, PDestLocation = 3, POptions = 4, PSummary = 5;
        static readonly string[] PageNames = { "Source System", "Destination System", "Destination Virtual Machine", "Destination Location", "Options", "Summary" };
        static readonly string[] PageHints =
        {
            "Select the source system you want to convert",
            "Select a host for the new virtual machine",
            "Select the destination VM name",
            "Select the location for the new virtual machine",
            "Set up the parameters for the conversion task",
            "Review the conversion parameters",
        };

        readonly Settings settings;
        readonly Panel[] pages = new Panel[6];
        FlowLayoutPanel nav;
        Label title, subtitle, hdrSource, hdrDest, status;
        Button btnBack, btnNext, btnCancel;
        int page;
        int reached;              // furthest page reached (earlier ones are links)
        bool working;

        // ---------------- source
        RadioButton rbOn, rbOff;
        ComboBox cboSrcType;
        Label lblSrcDesc;
        Panel srcOn;
        TableLayoutPanel srcEsxi, srcVmx;
        Label lblSpecify;
        ComboBox srcHost;
        TextBox srcUser, srcPass, srcKey, sEsxiHost, sEsxiUser, sEsxiPass, sEsxiSearch;
        Control srcRemoteFields;
        LinkLabel lnkDetails;
        Label lblAgentState;
        ListView esxiList;
        ListBox vmxList;
        EsxiClient sourceEsxi;
        readonly List<VmxInfo> esxiVms = new List<VmxInfo>();
        readonly List<VmxInfo> localVms = new List<VmxInfo>();
        VmxInfo agentVm;
        Dictionary<string, object> agentInfo;
        string agentFor;          // "local" or the host the agent VM was read from
        // remote Linux machine (SSH)
        Control srcLinuxFields;
        ComboBox lHost;
        TextBox lPort, lUser, lPass;
        VmxInfo linuxVm;
        string linuxFor;
        // Proxmox VE source host
        TableLayoutPanel srcPve;
        TextBox vHost, vPort, vUser, vPass, vSearch;
        ListView pveList;
        readonly List<VmxInfo> pveVms = new List<VmxInfo>();
        string pveSourceInfo = "";

        // ---------------- destination system
        ComboBox cboDestType;
        Label lblDestDesc, lblServerGroup;
        Panel dstProxmox, dstEsxi, dstOvf;
        TextBox pHost, pPort, pUser, pPass, dHost, dUser, dPass, ovfFolder;
        SshClient ssh;
        HostInfo hostInfo;
        string sshFor;
        EsxiClient targetEsxi;
        string esxiFor;
        EsxiInventory inventory;
        // existing VMs at each destination kind: name, id/state, node/host
        readonly List<string[]> proxInv = new List<string[]>(), esxiInv = new List<string[]>(), ovfInv = new List<string[]>();
        List<string[]> destInventory { get { return ToProxmox ? proxInv : Dest == DestKind.EsxiVm ? esxiInv : ovfInv; } }
        int nextVmid;

        // ---------------- destination VM
        ComboBox cboMachineVm;
        TextBox vmName, vmId, invSearch;
        Label lblVmId, lblInventory;
        ListView invList;

        // ---------------- destination location
        TableLayoutPanel locProxmox;
        Panel locEsxi, locOvf;
        ListView storageList;
        ComboBox pAlloc, dDatastore, dHw, dFormat, oHw;
        TextBox pTemp, dStage;
        Label lblNode, lblTotal, lblDsDetails, lblHost;

        // ---------------- options
        ComboBox cboMachineOpt;
        FlowLayoutPanel optTree;
        Panel optEditor;
        string optSection = "data";
        ComboBox pBridge, pCpu, dNetwork, dNic;
        CheckBox oShutdownEsxi, oShutdownSource, oRestoreIp, oVirtio, oNewMac, oStart, oCleanup, oPowerOn, oKeepFiles;
        readonly HashSet<CheckBox> shownOptions = new HashSet<CheckBox>();

        // ---------------- summary
        RichTextBox summary;

        public ConversionJob Job { get; private set; }

        public ConvertWizard(Settings settings, EsxiClient esxi, SshClient ssh, HostInfo hostInfo, IList<VmxInfo> listed)
        {
            this.settings = settings;
            sourceEsxi = esxi;
            this.ssh = ssh;
            this.hostInfo = hostInfo;
            if (ssh != null && hostInfo != null) sshFor = settings.Get("host", "");

            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = "Conversion - " + Program.Product;
            Font = new Font("Segoe UI", 9.5f);
            BackColor = Theme.Bg1;
            ForeColor = Theme.Text0;
            Size = new Size(1120, 740);
            MinimumSize = new Size(900, 620);
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            ShowIcon = false;

            BuildUi();
            LoadDefaults(listed);
            ShowPage(PSource);
        }

        // ================================================================ layout

        void BuildUi()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4, Margin = Padding.Empty };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            // page title band across the top (like Converter's white header)
            var band = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, BackColor = Theme.Bg0, Padding = new Padding(18, 12, 18, 12), Margin = Padding.Empty, ColumnCount = 1 };
            title = new Label { AutoSize = true, ForeColor = Theme.Text0, Font = new Font("Segoe UI Semibold", 12.5f), Margin = Padding.Empty };
            subtitle = new Label { AutoSize = true, ForeColor = Theme.Text2, Margin = new Padding(14, 2, 0, 0) };
            band.Controls.Add(title, 0, 0);
            band.Controls.Add(subtitle, 0, 1);
            root.Controls.Add(band, 0, 0);
            root.SetColumnSpan(band, 2);

            nav = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Theme.Bg0, Padding = new Padding(14, 12, 6, 6), Margin = new Padding(0, 1, 0, 0) };
            root.Controls.Add(nav, 0, 1);
            root.SetRowSpan(nav, 2);

            // "Source: ...   Destination: ..." bar shown on every page
            var bar = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 4, Padding = new Padding(16, 8, 16, 4), Margin = Padding.Empty };
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            var bold = new Font("Segoe UI Semibold", 9.5f);
            bar.Controls.Add(new Label { Text = "Source:", AutoSize = true, Font = bold, ForeColor = Theme.Text0, Margin = new Padding(0, 0, 6, 0) }, 0, 0);
            hdrSource = new Label { Text = "none", AutoSize = false, Dock = DockStyle.Fill, Height = 20, ForeColor = Theme.Text1, AutoEllipsis = true };
            bar.Controls.Add(hdrSource, 1, 0);
            bar.Controls.Add(new Label { Text = "Destination:", AutoSize = true, Font = bold, ForeColor = Theme.Text0, Margin = new Padding(12, 0, 6, 0) }, 2, 0);
            hdrDest = new Label { Text = "none", AutoSize = false, Dock = DockStyle.Fill, Height = 20, ForeColor = Theme.Text1, AutoEllipsis = true };
            bar.Controls.Add(hdrDest, 3, 0);
            root.Controls.Add(bar, 1, 1);

            var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16, 6, 16, 6), Margin = Padding.Empty };
            pages[PSource] = BuildSource();
            pages[PDestSystem] = BuildDestSystem();
            pages[PDestVm] = BuildDestVm();
            pages[PDestLocation] = BuildDestLocation();
            pages[POptions] = BuildOptions();
            pages[PSummary] = BuildSummary();
            foreach (var p in pages) { p.Dock = DockStyle.Fill; p.Visible = false; host.Controls.Add(p); }
            root.Controls.Add(host, 1, 2);

            var foot = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Padding = new Padding(16, 6, 16, 12), Margin = Padding.Empty };
            foot.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foot.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            status = new Label { AutoSize = true, ForeColor = Theme.Accent2, Anchor = AnchorStyles.Left, MaximumSize = new Size(560, 0) };
            var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
            btnBack = Theme.MakeButton("< Back", false);
            btnNext = Theme.MakeButton("Next >", true);
            btnCancel = Theme.MakeButton("Cancel", false);
            btnBack.Click += (s, e) => GoBack();
            btnNext.Click += (s, e) => Next();
            btnCancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            foreach (var b in new[] { btnBack, btnNext, btnCancel }) { b.Margin = new Padding(8, 0, 0, 0); b.Padding = new Padding(16, 6, 16, 6); buttons.Controls.Add(b); }
            foot.Controls.Add(status, 0, 0);
            foot.Controls.Add(buttons, 1, 0);
            root.Controls.Add(foot, 0, 3);
            root.SetColumnSpan(foot, 2);
            Controls.Add(root);
            CancelButton = btnCancel;
        }

        // ---------------------------------------------------------------- Source System

        Panel BuildSource()
        {
            var page = Rows(3);
            var typeRow = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 4, 0, 6) };
            rbOn = Radio("Powered on");
            rbOff = Radio("Powered off");
            rbOn.Margin = new Padding(0, 2, 18, 2);
            var radios = Flow(rbOn, rbOff);
            cboSrcType = Combo(330);
            lblSrcDesc = Note("");
            typeRow.Controls.Add(Lbl("Select source type:"), 0, 0);
            typeRow.Controls.Add(radios, 1, 0);
            typeRow.Controls.Add(cboSrcType, 1, 1);
            typeRow.Controls.Add(lblSrcDesc, 1, 2);
            page.Controls.Add(typeRow, 0, 0);

            lblSpecify = Group("Specify the powered on machine");
            page.Controls.Add(lblSpecify, 0, 1);
            var detail = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            page.Controls.Add(detail, 0, 2);

            // powered on: this local machine / remote Windows machine
            srcOn = new Panel();
            var g = Grid();
            srcHost = Combo(260);
            srcHost.DropDownStyle = ComboBoxStyle.DropDown;
            srcUser = Box(260);
            srcPass = Box(260);
            srcPass.UseSystemPasswordChar = true;
            srcKey = Box(260);
            AddRow(g, "IP address or name:", srcHost);
            AddRow(g, "User name:", srcUser);
            AddRow(g, "Password:", srcPass);
            AddRow(g, "Pairing key:", srcKey);
            var keyNote = Note("Optional: only for an agent that is already installed. With the password the converter deploys the agent itself.");
            AddRow(g, "", keyNote);
            srcRemoteFields = g;
            var lg = Grid();
            lHost = Combo(260);
            lHost.DropDownStyle = ComboBoxStyle.DropDown;
            lPort = Box(70); lUser = Box(260); lPass = Box(260);
            lPass.UseSystemPasswordChar = true;
            AddRow(lg, "IP address or name:", lHost);
            AddRow(lg, "SSH port:", lPort);
            AddRow(lg, "User name:", lUser);
            AddRow(lg, "Password:", lPass);
            AddRow(lg, "", Note("root, or a user that may run sudo. Leave the password empty to log in with your SSH key. " +
                                "Nothing is installed on the machine; a temporary key for root is added for the copy and removed afterwards."));
            srcLinuxFields = lg;
            lnkDetails = new LinkLabel { Text = "View source details...", AutoSize = true, LinkColor = Theme.Accent, ActiveLinkColor = Theme.Accent2, Margin = new Padding(0, 10, 0, 4) };
            lnkDetails.LinkClicked += (s, e) => ViewSourceDetails();
            lblAgentState = Note("");
            var onFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            onFlow.Controls.Add(g);
            onFlow.Controls.Add(lg);
            onFlow.Controls.Add(lnkDetails);
            onFlow.Controls.Add(lblAgentState);
            srcOn.Controls.Add(onFlow);
            srcHost.Leave += (s, e) => FillSavedKey();
            srcHost.SelectedIndexChanged += (s, e) => FillSavedKey();

            // powered off: ESXi host
            srcEsxi = Rows(3);
            sEsxiHost = Box(170); sEsxiUser = Box(110); sEsxiPass = Box(130);
            sEsxiPass.UseSystemPasswordChar = true;
            var connect = Theme.MakeButton("Connect", false);
            connect.Click += (s, e) => ConnectSourceEsxi();
            sEsxiPass.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; ConnectSourceEsxi(); } };
            srcEsxi.Controls.Add(Line("Server:", sEsxiHost, "User name:", sEsxiUser, "Password:", sEsxiPass, connect), 0, 0);
            sEsxiSearch = Box(200);
            sEsxiSearch.TextChanged += (s, e) => FillEsxiList();
            srcEsxi.Controls.Add(Line("Search for name with:", sEsxiSearch), 0, 1);
            esxiList = List(new[] { "VM name|230", "Power state|100", "Guest OS|200", "Disks|90", "Used|90" }, true);
            srcEsxi.Controls.Add(esxiList, 0, 2);

            // Proxmox VE host
            srcPve = Rows(3);
            vHost = Box(170); vPort = Box(50); vUser = Box(90); vPass = Box(130);
            vPass.UseSystemPasswordChar = true;
            var vConnect = Theme.MakeButton("Connect", false);
            vConnect.Click += (s, e) => ConnectSourcePve();
            vPass.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; ConnectSourcePve(); } };
            srcPve.Controls.Add(Line("Server:", vHost, "Port:", vPort, "User name:", vUser, "Password:", vPass, vConnect), 0, 0);
            vSearch = Box(200);
            vSearch.TextChanged += (s, e) => FillPveList();
            srcPve.Controls.Add(Line("Search for name with:", vSearch), 0, 1);
            pveList = List(new[] { "VM name|220", "ID|60", "State|110", "Guest OS|210", "Disks|110" }, true);
            srcPve.Controls.Add(pveList, 0, 2);

            // powered off: VMware Workstation / .vmx
            srcVmx = Rows(2);
            var add = Theme.MakeButton("Add .vmx files...", false);
            var remove = Theme.MakeButton("Remove", false);
            add.Click += (s, e) => AddLocal();
            remove.Click += (s, e) =>
            {
                int i = vmxList.SelectedIndex;
                if (i >= 0) { localVms.RemoveAt(i); vmxList.Items.RemoveAt(i); }
            };
            srcVmx.Controls.Add(Flow(add, remove), 0, 0);
            vmxList = new ListBox { Dock = DockStyle.Fill, BackColor = Theme.Bg2, ForeColor = Theme.Text0, BorderStyle = BorderStyle.None, IntegralHeight = false };
            srcVmx.Controls.Add(vmxList, 0, 1);

            foreach (var p in new Panel[] { srcOn, srcEsxi, srcPve, srcVmx }) { p.Dock = DockStyle.Fill; p.Visible = false; detail.Controls.Add(p); }

            EventHandler power = (s, e) => { if (((RadioButton)s).Checked) FillSourceTypes(); };
            rbOn.CheckedChanged += power;
            rbOff.CheckedChanged += power;
            cboSrcType.SelectedIndexChanged += (s, e) => SourceTypeChanged();
            return page;
        }

        void FillSourceTypes()
        {
            if (!rbOn.Checked && !rbOff.Checked) return;
            cboSrcType.Items.Clear();
            if (rbOn.Checked) cboSrcType.Items.AddRange(new object[] { "This local machine", "Remote Windows machine", "Remote Linux machine" });
            else cboSrcType.Items.AddRange(new object[] { "VMware ESXi virtual machine", "Proxmox VE virtual machine", "VMware Workstation or other VMware virtual machine" });
            cboSrcType.SelectedIndex = 0;
        }

        bool SrcLocal { get { return rbOn.Checked && cboSrcType.SelectedIndex == 0; } }
        bool SrcRemote { get { return rbOn.Checked && cboSrcType.SelectedIndex == 1; } }
        bool SrcLinux { get { return rbOn.Checked && cboSrcType.SelectedIndex == 2; } }
        bool SrcEsxi { get { return rbOff.Checked && cboSrcType.SelectedIndex == 0; } }
        bool SrcPve { get { return rbOff.Checked && cboSrcType.SelectedIndex == 1; } }
        bool SrcVmx { get { return rbOff.Checked && cboSrcType.SelectedIndex == 2; } }

        SourceKind Source
        {
            get
            {
                return SrcEsxi ? SourceKind.Esxi : SrcVmx ? SourceKind.Local : SrcPve ? SourceKind.Proxmox
                     : SrcLinux ? SourceKind.Linux : SourceKind.Agent;
            }
        }

        void SourceTypeChanged()
        {
            srcOn.Visible = rbOn.Checked;
            srcEsxi.Visible = SrcEsxi;
            srcPve.Visible = SrcPve;
            srcVmx.Visible = SrcVmx;
            srcRemoteFields.Visible = SrcRemote;
            srcLinuxFields.Visible = SrcLinux;
            lblSpecify.Text = rbOn.Checked ? "Specify the powered on machine" : SrcEsxi ? "Specify the ESXi host and the virtual machines"
                            : SrcPve ? "Specify the Proxmox VE host and the virtual machines" : "Specify the virtual machine files";
            lblSrcDesc.Text = SrcLocal ? "Convert this local machine (the PC running the converter), while it keeps running."
                            : SrcRemote ? "Convert any powered on physical or virtual Windows machine (Windows Server 2008 R2 and later)."
                            : SrcLinux ? "Convert any powered on physical or virtual Linux machine (x86-64, ext2/3/4, XFS, LVM) over SSH, to Proxmox VE."
                            : SrcEsxi ? "Convert virtual machines on an ESXi host. Running VMs are shut down for the copy."
                            : SrcPve ? "Convert virtual machines on another Proxmox VE host (or the same one). To Proxmox VE they may keep running."
                            : "Convert a VMware Workstation, Player or Fusion virtual machine (.vmx).";
            lblAgentState.Text = SrcLocal
                ? "The converter installs the GeuzaVirt agent on this PC (one Windows administrator prompt) and copies a VSS snapshot of every disk."
                : SrcRemote ? "The machine keeps running: the agent copies a VSS snapshot of every disk (used blocks only, encrypted)."
                : SrcLinux ? "The machine keeps running: the Proxmox host recreates its disks (same partitions, LVM and file system UUIDs) and copies the files with rsync over SSH. " +
                             "Changes made during the copy may be missed - stop busy databases first."
                : "";
            if (agentVm != null && agentFor != AgentKey()) { agentVm = null; agentInfo = null; }
            if (linuxVm != null && linuxFor != LinuxKey()) linuxVm = null;
            UpdateHeader();
        }

        string AgentKey() { return SrcLocal ? "local" : srcHost.Text.Trim().ToLowerInvariant(); }

        string LinuxKey() { return (lUser.Text.Trim() + "@" + lHost.Text.Trim() + ":" + lPort.Text.Trim()).ToLowerInvariant(); }

        void FillSavedKey()
        {
            string saved = Secret.Unprotect(settings.Get("agentkey." + srcHost.Text.Trim(), ""));
            if (saved.Length > 0) srcKey.Text = saved;
        }

        // ---------------------------------------------------------------- Destination System

        Panel BuildDestSystem()
        {
            var page = Rows(3);
            var typeRow = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 4, 0, 6) };
            cboDestType = Combo(330);
            lblDestDesc = Note("");
            lblDestDesc.MaximumSize = new Size(560, 0);
            typeRow.Controls.Add(Lbl("Select destination type:"), 0, 0);
            typeRow.Controls.Add(cboDestType, 1, 0);
            typeRow.Controls.Add(lblDestDesc, 1, 1);
            page.Controls.Add(typeRow, 0, 0);
            lblServerGroup = Group("");
            page.Controls.Add(lblServerGroup, 0, 1);
            var detail = new Panel { Dock = DockStyle.Fill };
            page.Controls.Add(detail, 0, 2);

            dstProxmox = new Panel();
            var p = Grid();
            pHost = Box(260); pPort = Box(70); pUser = Box(260); pPass = Box(260);
            pPass.UseSystemPasswordChar = true;
            AddRow(p, "Server:", pHost);
            AddRow(p, "SSH port:", pPort);
            AddRow(p, "User name:", pUser);
            AddRow(p, "Password:", pPass);
            AddRow(p, "", Note("Leave the password empty to log in with your SSH key. The converter uploads its import tool to /root/mtaalam-v2v/."));
            dstProxmox.Controls.Add(p);

            dstEsxi = new Panel();
            var v = Grid();
            dHost = Box(260); dUser = Box(260); dPass = Box(260);
            dPass.UseSystemPasswordChar = true;
            AddRow(v, "Server:", dHost);
            AddRow(v, "User name:", dUser);
            AddRow(v, "Password:", dPass);
            AddRow(v, "", Note("The ESXi host itself (not vCenter). The disks are prepared on this PC and uploaded to the host."));
            dstEsxi.Controls.Add(v);

            dstOvf = new Panel();
            var o = Grid();
            ovfFolder = Box(380);
            var browse = Theme.MakeButton("Browse...", false);
            browse.Click += (s, e) => Browse(ovfFolder, "Folder for the OVF package(s) - a sub-folder is made per machine");
            AddRow(o, "Folder:", Flow(ovfFolder, browse));
            AddRow(o, "", Note("Each machine gets <name>.ovf and <name>-diskN.vmdk (stream-optimized, compressed). Deploy it with " +
                               "\"Deploy OVF Template\" in ESXi or vCenter; in vCenter you can then use Convert to Template."));
            dstOvf.Controls.Add(o);

            foreach (var c in new[] { dstProxmox, dstEsxi, dstOvf }) { c.Dock = DockStyle.Fill; detail.Controls.Add(c); }
            cboDestType.SelectedIndexChanged += (s, e) => DestTypeChanged();
            return page;
        }

        readonly List<DestKind> destChoices = new List<DestKind>();

        void FillDestTypes()
        {
            DestKind keep = destChoices.Count > 0 && cboDestType.SelectedIndex >= 0 ? destChoices[cboDestType.SelectedIndex] : DestKind.ProxmoxVm;
            destChoices.Clear();
            cboDestType.Items.Clear();
            foreach (DestKind d in Enum.GetValues(typeof(DestKind)))
            {
                if ((SrcVmx || SrcLinux) && (d == DestKind.EsxiVm || d == DestKind.OvfTemplate)) continue;   // Workstation exports itself; Linux copies go to Proxmox
                destChoices.Add(d);
                cboDestType.Items.Add(ConversionJob.DestLabel(d));
            }
            int i = destChoices.IndexOf(keep);
            cboDestType.SelectedIndex = i >= 0 ? i : 0;
        }

        DestKind Dest { get { return cboDestType.SelectedIndex >= 0 && cboDestType.SelectedIndex < destChoices.Count ? destChoices[cboDestType.SelectedIndex] : DestKind.ProxmoxVm; } }
        bool ToProxmox { get { return Dest == DestKind.ProxmoxVm || Dest == DestKind.ProxmoxTemplate; } }
        bool Template { get { return Dest == DestKind.ProxmoxTemplate || Dest == DestKind.OvfTemplate; } }

        void DestTypeChanged()
        {
            if (!ToProxmox)   // volume-based resizing happens on the Proxmox host only
                foreach (var vm in Machines())
                    foreach (var d in vm.Disks)
                        if (d.VolumeResize) { foreach (var p in d.Parts) p.TargetBytes = 0; d.TargetBytes = 0; }
            dstProxmox.Visible = ToProxmox;
            dstEsxi.Visible = Dest == DestKind.EsxiVm;
            dstOvf.Visible = Dest == DestKind.OvfTemplate;
            switch (Dest)
            {
                case DestKind.ProxmoxVm:
                    lblDestDesc.Text = "Creates a new virtual machine on a Proxmox VE host.";
                    lblServerGroup.Text = "Proxmox VE server details";
                    break;
                case DestKind.ProxmoxTemplate:
                    lblDestDesc.Text = "Creates the virtual machine on Proxmox VE and turns it into a template. Right-click > Clone in Proxmox " +
                                       "deploys new VMs from it (full or linked clones). Generalise Windows with sysprep first if clones share a network.";
                    lblServerGroup.Text = "Proxmox VE server details";
                    break;
                case DestKind.EsxiVm:
                    lblDestDesc.Text = "Creates a new virtual machine for use on a VMware ESXi host.";
                    lblServerGroup.Text = "VMware ESXi server details";
                    break;
                default:
                    lblDestDesc.Text = "Creates an OVF package (VMware template) in a folder: deploy it on any ESXi host or vCenter.";
                    lblServerGroup.Text = "OVF package location";
                    break;
            }
            if (SrcVmx) lblDestDesc.Text += "\n(VMware Workstation files can only go to Proxmox VE here - Workstation exports OVF itself.)";
            if (SrcLinux) lblDestDesc.Text += "\n(Live copies of Linux machines go to Proxmox VE.)";
            if (SrcPve && !ToProxmox) lblDestDesc.Text += "\nThe VM is shut down for the export; Proxmox writes compressed VMDK files that are downloaded to this PC.";
            UpdateHeader();
        }

        // ---------------------------------------------------------------- Destination Virtual Machine

        Panel BuildDestVm()
        {
            var page = Rows(4);
            cboMachineVm = Combo(330);
            cboMachineVm.SelectedIndexChanged += (s, e) => LoadVmPage();
            page.Controls.Add(Line("Machine:", cboMachineVm), 0, 0);
            vmName = Box(300);
            vmId = Box(90);
            lblVmId = Lbl("VM ID:");
            vmName.Leave += (s, e) => SaveVmPage();
            vmId.Leave += (s, e) => SaveVmPage();
            page.Controls.Add(Line("Name:", vmName, lblVmId, vmId), 0, 1);
            lblInventory = Lbl("Inventory:");
            invSearch = Box(200);
            invSearch.TextChanged += (s, e) => FillInventory();
            var clear = new LinkLabel { Text = "Clear", AutoSize = true, LinkColor = Theme.Accent, Margin = new Padding(0, 6, 0, 0) };
            clear.LinkClicked += (s, e) => invSearch.Text = "";
            page.Controls.Add(Line(lblInventory, "Search for name with:", invSearch, clear), 0, 2);
            invList = List(new[] { "VM name|260", "ID / power state|160", "Node / host|160" }, false);
            page.Controls.Add(invList, 0, 3);
            return page;
        }

        List<VmxInfo> Machines()
        {
            var list = new List<VmxInfo>();
            if (SrcEsxi) { foreach (ListViewItem i in esxiList.CheckedItems) list.Add((VmxInfo)i.Tag); }
            else if (SrcPve) { foreach (ListViewItem i in pveList.CheckedItems) list.Add((VmxInfo)i.Tag); }
            else if (SrcVmx) list.AddRange(localVms);
            else if (SrcLinux) { if (linuxVm != null) list.Add(linuxVm); }
            else if (agentVm != null) list.Add(agentVm);
            return list;
        }

        VmxInfo CurrentVm(ComboBox picker)
        {
            var m = Machines();
            return m.Count == 0 ? null : m[Math.Max(0, Math.Min(picker.SelectedIndex, m.Count - 1))];
        }

        void FillMachinePicker(ComboBox picker)
        {
            int keep = picker.SelectedIndex;
            picker.Items.Clear();
            foreach (var vm in Machines()) picker.Items.Add(vm.DisplayName);
            if (picker.Items.Count > 0) picker.SelectedIndex = Math.Max(0, Math.Min(keep, picker.Items.Count - 1));
            picker.Parent.Visible = picker.Items.Count > 1;
        }

        void LoadVmPage()
        {
            var vm = CurrentVm(cboMachineVm);
            if (vm == null) return;
            vmName.Text = vm.ProxmoxName;
            lblVmId.Visible = vmId.Visible = ToProxmox;
            if (ToProxmox && vm.TargetVmid <= 0) vm.TargetVmid = SuggestVmid(vm);
            vmId.Text = vm.TargetVmid > 0 ? vm.TargetVmid.ToString() : "";
            lblInventory.Text = ToProxmox ? "Inventory for " + (hostInfo != null ? "node " + hostInfo.Node : pHost.Text.Trim()) + ":"
                              : Dest == DestKind.EsxiVm ? "Inventory for " + dHost.Text.Trim() + ":" : "Existing packages in " + ovfFolder.Text.Trim() + ":";
            FillInventory();
        }

        int SuggestVmid(VmxInfo vm)
        {
            var used = new HashSet<int>();
            foreach (var r in destInventory) { int id; if (int.TryParse(r[1], out id)) used.Add(id); }
            foreach (var other in Machines()) if (other != vm && other.TargetVmid > 0) used.Add(other.TargetVmid);
            int n = Math.Max(nextVmid, 100);
            while (used.Contains(n)) n++;
            return n;
        }

        void SaveVmPage()
        {
            var vm = CurrentVm(cboMachineVm);
            if (vm == null) return;
            string n = vmName.Text.Trim();
            vm.TargetName = n.Length == 0 || n == vm.DisplayName ? null : n;
            int id;
            vm.TargetVmid = ToProxmox && int.TryParse(vmId.Text.Trim(), out id) ? id : 0;
            UpdateHeader();
        }

        void FillInventory()
        {
            invList.BeginUpdate();
            invList.Items.Clear();
            string q = invSearch.Text.Trim();
            foreach (var r in destInventory)
            {
                if (q.Length > 0 && r[0].IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var item = new ListViewItem(r);
                if (string.Equals(r[0], vmName.Text.Trim(), StringComparison.OrdinalIgnoreCase)) item.ForeColor = Theme.Red;
                invList.Items.Add(item);
            }
            invList.EndUpdate();
        }

        // ---------------------------------------------------------------- Destination Location

        Panel BuildDestLocation()
        {
            var host = new Panel();

            locProxmox = Rows(3);
            lblNode = Lbl("");
            lblTotal = Note("");
            locProxmox.Controls.Add(Flow(lblNode, lblTotal), 0, 0);
            storageList = List(new[] { "Storage|180", "Type|110", "Free|120", "Content|200" }, false);
            storageList.MultiSelect = false;
            locProxmox.Controls.Add(storageList, 0, 1);
            pAlloc = Combo(260);
            pAlloc.Items.AddRange(new object[] { "Thin - only the space in use", "Thick - the whole disk size" });
            pTemp = Box(320);
            var pg = Grid();
            AddRow(pg, "Disk allocation:", pAlloc);
            AddRow(pg, "Temp folder:", pTemp);
            AddRow(pg, "", Note("Temp folder on the Proxmox host: only used for local .vmx uploads and ESXi VMs with snapshots on Proxmox older than 8.2."));
            locProxmox.Controls.Add(pg, 0, 2);
            var pr = (TableLayoutPanel)locProxmox;
            pr.RowStyles[1] = new RowStyle(SizeType.Percent, 100);
            pr.RowStyles[2] = new RowStyle(SizeType.AutoSize);

            locEsxi = new Panel();
            var e = Grid();
            lblHost = Lbl("");
            dDatastore = Combo(380);
            lblDsDetails = Note("");
            dHw = Combo(140);
            dFormat = Combo(260);
            dFormat.Items.AddRange(new object[] { "Thin provisioned", "Thick provisioned (lazy zeroed)" });
            dStage = Box(380);
            var sb = Theme.MakeButton("Browse...", false);
            sb.Click += (s, ev) => Browse(dStage, "Folder on this PC for the disks while they are prepared (needs room for the used space, compressed)");
            dDatastore.SelectedIndexChanged += (s, ev) => ShowDatastore();
            AddRow(e, "Host system:", lblHost);
            AddRow(e, "Datastore:", dDatastore);
            AddRow(e, "", lblDsDetails);
            AddRow(e, "Virtual machine version:", dHw);
            AddRow(e, "Disk format:", dFormat);
            AddRow(e, "Staging folder:", Flow(dStage, sb));
            AddRow(e, "", Note("The disks are compressed into the staging folder on this PC, then uploaded to the datastore (only used blocks travel)."));
            locEsxi.Controls.Add(e);

            locOvf = new Panel();
            var o = Grid();
            oHw = Combo(140);
            AddRow(o, "Virtual machine version:", oHw);
            AddRow(o, "", Note("vmx-13 deploys on ESXi 6.5 and newer, vmx-14 on 6.7, vmx-17 on 7.0, vmx-20 on 8.0. Pick the oldest host you will deploy on."));
            locOvf.Controls.Add(o);

            foreach (var c in new Panel[] { locProxmox, locEsxi, locOvf }) { c.Dock = DockStyle.Fill; host.Controls.Add(c); }
            return host;
        }

        void ShowDatastore()
        {
            var ds = dDatastore.SelectedItem as EsxiDatastore;
            lblDsDetails.Text = ds == null ? "" : string.Format("Capacity: {0}    Free: {1}    Type: {2}    Total source disks size: {3}",
                Theme.Bytes(ds.CapacityBytes), Theme.Bytes(ds.FreeBytes), ds.Type, Theme.Bytes(TotalSource()));
        }

        long TotalSource()
        {
            long t = 0;
            foreach (var vm in Machines()) t += vm.ProxmoxDiskBytes;
            return t;
        }

        static int MaxHardware(string product)
        {
            // "VMware ESXi 6.7.0 build-..." -> highest virtual hardware version it runs
            var m = System.Text.RegularExpressions.Regex.Match(product ?? "", @"(\d+)\.(\d+)");
            if (!m.Success) return 13;
            int major = int.Parse(m.Groups[1].Value), minor = int.Parse(m.Groups[2].Value);
            if (major >= 8) return 20;
            if (major == 7) return 17;
            if (major == 6) return minor >= 7 ? 14 : minor >= 5 ? 13 : 11;
            return 10;
        }

        static void FillHardware(ComboBox c, int max, int pick)
        {
            c.Items.Clear();
            foreach (int v in new[] { 10, 11, 13, 14, 15, 17, 18, 19, 20, 21 })
                if (v <= max) c.Items.Add("Version " + v);
            c.SelectedIndex = Math.Max(0, c.Items.IndexOf("Version " + pick));
            if (c.SelectedIndex < 0 && c.Items.Count > 0) c.SelectedIndex = c.Items.Count - 1;
        }

        static int HardwareOf(ComboBox c)
        {
            int v;
            return c.SelectedItem != null && int.TryParse(c.SelectedItem.ToString().Replace("Version ", ""), out v) ? v : 10;
        }

        // ---------------------------------------------------------------- Options

        Panel BuildOptions()
        {
            var page = Rows(3);
            cboMachineOpt = Combo(330);
            cboMachineOpt.SelectedIndexChanged += (s, e) => { RefreshTree(); ShowEditor(optSection); };
            page.Controls.Add(Line("Machine:", cboMachineOpt), 0, 0);
            page.Controls.Add(Note("Click on an option below to edit it."), 0, 1);

            var split = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 250));
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            optTree = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = Theme.Bg2, Padding = new Padding(8), Margin = new Padding(0, 0, 8, 0) };
            optEditor = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg2, Padding = new Padding(12), AutoScroll = true };
            split.Controls.Add(optTree, 0, 0);
            split.Controls.Add(optEditor, 1, 0);
            page.Controls.Add(split, 0, 2);

            pBridge = Combo(200);
            pCpu = Combo(200);
            pCpu.DropDownStyle = ComboBoxStyle.DropDown;
            pCpu.Items.AddRange(new object[] { "host", "x86-64-v2-AES", "x86-64-v3", "x86-64-v4", "kvm64" });
            dNetwork = Combo(260);
            dNic = Combo(200);
            dNic.Items.AddRange(new object[] { "Auto (E1000e, E1000 on 2008 R2)", "E1000e", "E1000", "VMXNET 3" });
            dNic.SelectedIndex = 0;
            foreach (var c in new Control[] { pBridge, pCpu, dNetwork, dNic }) c.TextChanged += (s, e) => RefreshTree();
            pBridge.SelectedIndexChanged += (s, e) => RefreshTree();
            dNetwork.SelectedIndexChanged += (s, e) => RefreshTree();
            dNic.SelectedIndexChanged += (s, e) => RefreshTree();

            oShutdownEsxi = Check("Shut down running VMs on the source ESXi host before copying");
            oShutdownSource = Check("Power off the source machine after the conversion");
            oRestoreIp = Check("Keep the source's static IP settings on the new network card");
            oVirtio = Check("Windows already has VirtIO drivers (otherwise it boots on SATA first)");
            oNewMac = Check("Give the new VM new MAC addresses");
            oStart = Check("Power on the destination VM after the conversion");
            oCleanup = Check("Delete temporary files from the Proxmox host afterwards");
            oPowerOn = Check("Power on the destination VM after the conversion");
            oKeepFiles = Check("Keep the OVF package in the staging folder after the upload");
            foreach (var c in new[] { oShutdownEsxi, oShutdownSource, oRestoreIp, oVirtio, oNewMac, oStart, oCleanup, oPowerOn, oKeepFiles })
                c.CheckedChanged += (s, e) => RefreshTree();
            return page;
        }

        void ShowOption(CheckBox c, bool show)
        {
            if (show) shownOptions.Add(c); else shownOptions.Remove(c);
        }

        bool On(CheckBox c) { return shownOptions.Contains(c) && c.Checked; }

        void UpdateOptionSet()
        {
            var src = Source;
            bool pveRunning = src == SourceKind.Proxmox && Machines().Exists(v => v.Pve != null && v.Pve.Running);
            oShutdownEsxi.Text = src == SourceKind.Proxmox ? "Shut down running VMs on the source Proxmox VE host before copying"
                                                           : "Shut down running VMs on the source ESXi host before copying";
            ShowOption(oShutdownEsxi, src == SourceKind.Esxi || (pveRunning && !ToProxmox));
            ShowOption(oShutdownSource, !Template && (src == SourceKind.Agent || src == SourceKind.Linux || (pveRunning && ToProxmox)));
            ShowOption(oRestoreIp, src == SourceKind.Agent && !Template);
            ShowOption(oVirtio, ToProxmox && src != SourceKind.Agent && src != SourceKind.Proxmox && Machines().Exists(v => v.IsWindows));
            ShowOption(oNewMac, ToProxmox);
            ShowOption(oStart, Dest == DestKind.ProxmoxVm);
            ShowOption(oCleanup, ToProxmox);
            ShowOption(oPowerOn, Dest == DestKind.EsxiVm);
            ShowOption(oKeepFiles, Dest == DestKind.EsxiVm);
        }

        void RefreshTree()
        {
            if (optTree == null) return;
            var vm = CurrentVm(cboMachineOpt);
            optTree.SuspendLayout();
            optTree.Controls.Clear();
            optTree.Controls.Add(new Label { Text = "Current settings:", AutoSize = true, ForeColor = Theme.Text2, Margin = new Padding(0, 0, 0, 6) });
            if (vm != null)
            {
                var data = new List<string>();
                bool volumes = VolumeBased(vm);
                data.Add("Copy type: " + (volumes ? (vm.Linux != null ? "file-level, volume-based (rsync over SSH)" : "volume-based (VSS snapshot)")
                    : vm.HotClone ? "used blocks of a VSS snapshot"
                    : vm.Linux != null ? "file-level (rsync over SSH)" : vm.Pve != null ? (ToProxmox ? "vzdump backup stream" : "compressed VMDK export")
                    : vm.Esxi != null ? "disk-based (from ESXi)" : "disk-based (.vmx files)"));
                foreach (var d in vm.Disks)
                {
                    data.Add(d.Slot + ": " + Sizing.Gb(d.CapacityBytes) + (d.TargetBytes > 0 && d.TargetBytes != d.CapacityBytes ? " -> " + Sizing.Gb(d.TargetBytes) : ""));
                    if (!volumes) continue;
                    foreach (var p in d.Parts)
                        if (p.Resizable)
                            data.Add("  " + Colon(p.Name) + " " + (p.TargetBytes > 0 && p.TargetBytes != p.Size ? Sizing.Gb(p.Size) + " -> " + Sizing.Gb(p.TargetBytes) : "maintain size"));
                }
                Section("data", "Data to copy", data);
                Section("devices", "Devices", new List<string>
                {
                    "vCPUs: " + vm.ProxmoxCores,
                    "Memory: " + Sizing.Gb((long)vm.ProxmoxMemoryMb << 20),
                    "Disk controller: " + (ToProxmox ? "VirtIO SCSI single" : vm.IsWindows ? "SCSI LSI Logic SAS" : "SCSI LSI Logic"),
                });
                var nets = new List<string>();
                int nics = Math.Max(1, vm.Macs.Count);
                for (int i = 0; i < nics; i++)
                    nets.Add("NIC" + (i + 1) + ": " + (ToProxmox ? (pBridge.Text.Length > 0 ? pBridge.Text : "-") : Dest == DestKind.EsxiVm ? (dNetwork.Text.Length > 0 ? dNetwork.Text : "-") : "VM Network"));
                Section("networks", "Networks", nets);
                var adv = new List<string>();
                foreach (var c in new[] { oShutdownEsxi, oShutdownSource, oRestoreIp, oVirtio, oNewMac, oStart, oCleanup, oPowerOn, oKeepFiles })
                    if (shownOptions.Contains(c)) adv.Add(ShortText(c) + ": " + (c.Checked ? "Yes" : "No"));
                if (Template) adv.Add(Dest == DestKind.ProxmoxTemplate ? "Convert to template: Yes" : "Export as OVF template: Yes");
                Section("advanced", "Advanced options", adv);
            }
            optTree.ResumeLayout();
        }

        static string Colon(string name) { return name.EndsWith(":") ? name : name + ":"; }

        static string ShortText(CheckBox c)
        {
            string t = c.Text;
            if (t.StartsWith("Shut down running")) return "Shut down running source VMs";
            if (t.StartsWith("Power off the source")) return "Power off source";
            if (t.StartsWith("Keep the source's")) return "Keep the source IP settings";
            if (t.StartsWith("Windows already")) return "VirtIO drivers present";
            if (t.StartsWith("Give the new")) return "New MAC addresses";
            if (t.StartsWith("Power on the destination")) return "Power on destination";
            if (t.StartsWith("Delete temporary")) return "Delete temp files";
            if (t.StartsWith("Keep the OVF")) return "Keep staged OVF";
            return t;
        }

        void Section(string key, string name, List<string> lines)
        {
            var head = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 0) };
            var l = new Label { Text = name, AutoSize = true, ForeColor = optSection == key ? Theme.Accent : Theme.Text0, Font = new Font("Segoe UI Semibold", 9.5f), Margin = new Padding(0, 0, 8, 0) };
            var edit = new LinkLabel { Text = "Edit", AutoSize = true, LinkColor = Theme.Accent, Margin = Padding.Empty };
            edit.LinkClicked += (s, e) => ShowEditor(key);
            l.Click += (s, e) => ShowEditor(key);
            l.Cursor = Cursors.Hand;
            head.Controls.Add(l);
            head.Controls.Add(edit);
            optTree.Controls.Add(head);
            foreach (string line in lines)
                optTree.Controls.Add(new Label { Text = line, AutoSize = true, ForeColor = Theme.Text1, Margin = new Padding(14, 1, 0, 1), MaximumSize = new Size(225, 0) });
        }

        void ShowEditor(string key)
        {
            optSection = key;
            var vm = CurrentVm(cboMachineOpt);
            optEditor.SuspendLayout();
            optEditor.Controls.Clear();
            if (vm != null)
            {
                Control c;
                switch (key)
                {
                    case "devices": c = DevicesEditor(vm); break;
                    case "networks": c = NetworksEditor(vm); break;
                    case "advanced": c = AdvancedEditor(); break;
                    default: c = DataEditor(vm); break;
                }
                c.Dock = DockStyle.Top;
                optEditor.Controls.Add(c);
                FitNotes(c, optEditor.ClientSize.Width - optEditor.Padding.Horizontal - 8);
            }
            optEditor.ResumeLayout();
            RefreshTree();
        }

        // wrap the grey notes of an editor to the width that is left for them
        static void FitNotes(Control parent, int width)
        {
            foreach (Control c in parent.Controls)
            {
                int left = 0;
                var grid = parent as TableLayoutPanel;
                if (grid != null && grid.GetColumn(c) == 1 && grid.GetColumnWidths().Length > 1) left = grid.GetColumnWidths()[0];
                if (c is Label && !(c is LinkLabel) && c.ForeColor == Theme.Text2) c.MaximumSize = new Size(Math.Max(220, width - left - 10), 0);
                else if (c.HasChildren) FitNotes(c, width - left);
            }
        }

        // Volume-based copy (VMware Converter's "Data to copy" > Select volumes to copy):
        // every volume with its own destination size. Live copies to Proxmox only.
        bool VolumeBased(VmxInfo vm)
        {
            if ((!vm.HotClone && vm.Linux == null) || !ToProxmox) return false;
            foreach (var d in vm.Disks) foreach (var p in d.Parts) if (p.Resizable) return true;
            return false;
        }

        Control VolumeEditor(VmxInfo vm)
        {
            bool linux = vm.Linux != null;
            var f = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            f.Controls.Add(Note(linux
                ? "Data copy type: file-level, volume-based. Select the destination size of each volume. Proxmox creates ONE disk per " +
                  "source disk at the size the new layout needs, creates the partitions, LVM volumes and file systems at their new sizes " +
                  "(same UUIDs) and copies the files into them. Nothing is changed on the source machine."
                : "Data copy type: volume-based. Select the destination size of each volume. Proxmox creates ONE disk per source disk " +
                  "at the size the new layout needs. Every volume that changes size is created new at that size and its files are copied " +
                  "into it from the VSS snapshot (with permissions, short names, hard links); the other partitions are copied block by " +
                  "block. Nothing is changed on the source machine."));
            var g = new TableLayoutPanel { AutoSize = true, ColumnCount = 5, Margin = new Padding(0, 8, 0, 4) };
            foreach (string h in new[] { "Volume", "Source disk", "Size", "Used", "Destination size" })
                g.Controls.Add(new Label { Text = h, AutoSize = true, ForeColor = Theme.Text2, Margin = new Padding(0, 0, 14, 4) });
            var results = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0, 4, 0, 4) };
            Action showResults = () =>
            {
                results.Controls.Clear();
                foreach (var d in vm.Disks)
                {
                    string error = null;
                    long size = d.FinalBytes;
                    if (d.Parts.Count > 0) size = d.PlanVolumes(out error);
                    var l = Lbl("Disk " + d.Slot + (d.GuestDisk >= 0 ? " (disk " + d.GuestDisk + ")" : "") + ":  " + Sizing.Gb(d.CapacityBytes) +
                                (error != null ? "  -  " + error : d.VolumeResize ? "  ->  " + Sizing.Gb(size) : "  (maintain size)"));
                    if (error != null) l.ForeColor = Theme.Accent2;
                    results.Controls.Add(l);
                }
            };
            foreach (var d in vm.Disks)
            {
                foreach (var p in d.Parts)
                {
                    if (p.Kind == "msr" || p.Kind == "extended") continue;
                    var disk = d;
                    var part = p;
                    g.Controls.Add(Lbl(p.Name + (p.Letter.Length > 0 && p.Fs.Length > 0 ? "  (" + p.Fs + ")" : "")));
                    g.Controls.Add(Lbl(d.Slot + (d.GuestDisk >= 0 ? "  (disk " + d.GuestDisk + ")" : "")));
                    g.Controls.Add(Lbl(Sizing.Gb(p.Size)));
                    g.Controls.Add(Lbl(p.Used >= 0 ? Sizing.Gb(p.Used) : "-"));
                    if (!p.Resizable)
                    {
                        var keep = Lbl("Maintain size");
                        keep.ForeColor = Theme.Text2;
                        g.Controls.Add(keep);
                        continue;
                    }
                    var size = Combo(210);
                    size.DropDownStyle = ComboBoxStyle.DropDown;
                    size.Items.Add("Maintain size (" + Sizing.Gb(p.Size) + ")");
                    if (p.MinBytes < p.Size) size.Items.Add("Min size (" + Sizing.Gb(p.MinBytes) + ")");
                    if (p.RecommendedBytes < p.Size && p.RecommendedBytes > p.MinBytes) size.Items.Add("Recommended (" + Sizing.Gb(p.RecommendedBytes) + ")");
                    size.Text = p.TargetBytes > 0 && p.TargetBytes != p.Size ? (p.TargetBytes / (double)(1L << 30)).ToString("0.#", CultureInfo.InvariantCulture) : (string)size.Items[0];
                    size.Tag = size.Text;
                    Action apply = () =>
                    {
                        if ((string)size.Tag == size.Text) return;
                        size.Tag = size.Text;
                        ApplyVolumeSize(disk, part, size);
                        showResults();
                    };
                    size.SelectionChangeCommitted += (s, e) => BeginInvoke(apply);
                    size.Leave += (s, e) => apply();
                    g.Controls.Add(size);
                }
            }
            f.Controls.Add(g);
            showResults();
            f.Controls.Add(results);
            if (linux)
            {
                f.Controls.Add(Note("Min size = used space + 3% + 0.5 GB; Recommended = used + 25% + 2 GB. A larger size grows the volume. " +
                                    "An LVM physical volume that gets smaller shrinks its file system volumes (each keeps at least its used " +
                                    "space). Disks with logical (extended) partitions keep their layout."));
                f.Controls.Add(FixedCheck("Skip swap files and virtual file systems (/proc, /sys, /dev, /run, tmpfs)"));
                return f;
            }
            f.Controls.Add(Note("Min size = used space + 3% + 0.5 GB; Recommended = used + 25% + 2 GB. A larger size grows the volume. " +
                                "On MBR disks only the last volume can change size (Windows finds its volumes by their position)."));
            f.Controls.Add(FixedCheck("Ignore page file and hibernation file"));
            f.Controls.Add(Note("Volumes that keep their size: only their used blocks are read from the VSS snapshot. In every volume " +
                                "pagefile.sys, hiberfil.sys and swapfile.sys are skipped (Windows recreates them)."));
            return f;
        }

        void ApplyVolumeSize(VmDisk d, GuestPart p, ComboBox box)
        {
            string t = box.Text.Trim();
            long size;
            if (t.StartsWith("Maintain")) size = 0;
            else if (t.StartsWith("Min size")) size = p.MinBytes;
            else if (t.StartsWith("Recommended")) size = p.RecommendedBytes;
            else
            {
                double gb;
                if (!double.TryParse(t.Replace(',', '.').Replace("GB", "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out gb) || gb <= 0)
                {
                    status.Text = "Enter the size in GB, or pick Maintain size / Min size.";
                    return;
                }
                size = (long)Math.Round(gb * (1L << 30));
            }
            if (size == p.Size) size = 0;
            if (size > 0 && size < p.MinBytes)
            {
                status.Text = p.Name + " holds " + Sizing.Gb(p.Used) + ": the smallest size is " + Sizing.Gb(p.MinBytes) + ".";
                box.Text = p.TargetBytes > 0 ? (p.TargetBytes / (double)(1L << 30)).ToString("0.#", CultureInfo.InvariantCulture) : (string)box.Items[0];
                box.Tag = box.Text;
                return;
            }
            long before = p.TargetBytes;
            p.TargetBytes = size;
            string error;
            long planned = d.PlanVolumes(out error);
            if (error != null)
            {
                p.TargetBytes = before;
                status.Text = "Disk " + d.Slot + ": " + error + ".";
                box.Text = before > 0 ? (before / (double)(1L << 30)).ToString("0.#", CultureInfo.InvariantCulture) : (string)box.Items[0];
                box.Tag = box.Text;
                return;
            }
            d.TargetBytes = !d.VolumeResize || planned == d.CapacityBytes ? 0 : planned;
            status.Text = "";
            RefreshTree();
        }

        Control DataEditor(VmxInfo vm)
        {
            if (VolumeBased(vm)) return VolumeEditor(vm);
            if (vm.Linux != null || vm.Pve != null) return FixedDiskEditor(vm);
            var f = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            f.Controls.Add(Note("Select the destination size of each disk. Larger always works (extend the volume afterwards). " +
                                "Smaller works down to the end of the last partition" +
                                (vm.Agent != null && !vm.Agent.IsLinux ? "; the agent can shrink the volume in the running machine first." : ".")));
            var g = new TableLayoutPanel { AutoSize = true, ColumnCount = 5, Margin = new Padding(0, 8, 0, 8) };
            foreach (string h in new[] { "Disk", "Size", "Used", "Min", "Destination size" })
                g.Controls.Add(new Label { Text = h, AutoSize = true, ForeColor = Theme.Text2, Margin = new Padding(0, 0, 14, 4) });
            foreach (var d in vm.Disks)
            {
                var disk = d;
                g.Controls.Add(Lbl(d.Slot + (d.GuestDisk >= 0 ? "  (disk " + d.GuestDisk + ")" : "")));
                g.Controls.Add(Lbl(Sizing.Gb(d.CapacityBytes)));
                g.Controls.Add(Lbl(d.UsedBytes >= 0 ? Sizing.Gb(d.UsedBytes) : "?"));
                g.Controls.Add(Lbl(d.MinBytes > 0 ? Sizing.Gb(Sizing.MinBytes(d)) : "?"));
                var size = Combo(210);
                size.DropDownStyle = ComboBoxStyle.DropDown;
                size.Items.Add("Maintain size (" + Sizing.Gb(d.CapacityBytes) + ")");
                if (d.MinBytes > 0 && Sizing.MinBytes(d) < d.CapacityBytes) size.Items.Add("Min size (" + Sizing.Gb(Sizing.MinBytes(d)) + ")");
                long rec = Sizing.DiskBytes(d);
                if (rec > 0 && rec != d.CapacityBytes) size.Items.Add("Recommended (" + Sizing.Gb(rec) + ")");
                size.Text = d.TargetBytes > 0 && d.TargetBytes != d.CapacityBytes ? (d.TargetBytes / (double)(1L << 30)).ToString("0.#", CultureInfo.InvariantCulture) : (string)size.Items[0];
                size.Tag = size.Text;
                Action apply = () =>
                {
                    if ((string)size.Tag == size.Text) return;   // already applied (SelectionChangeCommitted + Leave)
                    size.Tag = size.Text;
                    ApplyDiskSize(vm, disk, size);
                };
                size.SelectionChangeCommitted += (s, e) => BeginInvoke(apply);
                size.Leave += (s, e) => apply();
                g.Controls.Add(size);
            }
            f.Controls.Add(g);
            if (vm.HotClone)
            {
                f.Controls.Add(FixedCheck("Ignore page file and hibernation file"));
                f.Controls.Add(Note("Live copy: only the used blocks of each NTFS volume are read from the VSS snapshot; pagefile.sys, " +
                                    "hiberfil.sys and swapfile.sys are skipped (Windows recreates them)."));
            }
            else if (vm.Esxi != null && !ToProxmox && vm.Resized)
                f.Controls.Add(Note("Note: VMs exported from ESXi keep their disk sizes on VMware targets; resize them in the new VM."));
            return f;
        }

        // a ticked option that cannot be changed: readable (not greyed out like a disabled control)
        static CheckBox FixedCheck(string text)
        {
            var c = Check(text + "  (always)");
            c.Checked = true;
            c.AutoCheck = false;
            c.Cursor = Cursors.Default;
            return c;
        }

        // Linux and Proxmox sources: the disks keep their size.
        Control FixedDiskEditor(VmxInfo vm)
        {
            foreach (var d in vm.Disks) d.TargetBytes = 0;
            var f = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            f.Controls.Add(Note(vm.Linux != null
                ? "Data copy type: file-level. The disks are recreated with the same partitions, LVM volumes and file systems (same UUIDs, so fstab and the boot loader keep working); only the files are copied."
                : ToProxmox ? "Data copy type: vzdump backup stream. The VM is restored as it is, with its own Proxmox settings."
                            : "Data copy type: compressed stream-optimized VMDK (only the used data travels)."));
            var g = new TableLayoutPanel { AutoSize = true, ColumnCount = 4, Margin = new Padding(0, 8, 0, 8) };
            foreach (string h in new[] { "Disk", "Source", "Size", "Destination size" })
                g.Controls.Add(new Label { Text = h, AutoSize = true, ForeColor = Theme.Text2, Margin = new Padding(0, 0, 14, 4) });
            foreach (var d in vm.Disks)
            {
                g.Controls.Add(Lbl(d.Slot));
                g.Controls.Add(Lbl(string.IsNullOrEmpty(d.GuestLayout) ? "-" : d.GuestLayout.Split('\n')[0]));
                g.Controls.Add(Lbl(Sizing.Gb(d.CapacityBytes)));
                g.Controls.Add(Lbl("Maintain size"));
            }
            f.Controls.Add(g);
            f.Controls.Add(Note("Disk sizes cannot be changed for this source. Grow a disk in the new VM afterwards if needed."));
            if (vm.Linux != null)
                f.Controls.Add(FixedCheck("Skip swap files and virtual file systems (/proc, /sys, /dev, /run, tmpfs)"));
            return f;
        }

        void ApplyDiskSize(VmxInfo vm, VmDisk d, ComboBox box)
        {
            string t = box.Text.Trim();
            long size;
            if (t.StartsWith("Maintain")) size = 0;
            else if (t.StartsWith("Min size")) size = Sizing.MinBytes(d);
            else if (t.StartsWith("Recommended")) size = Sizing.DiskBytes(d);
            else
            {
                double gb;
                if (!double.TryParse(t.Replace(',', '.').Replace("GB", "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out gb) || gb <= 0)
                {
                    status.Text = "Enter the size in GB, or pick Maintain / Min size.";
                    return;
                }
                size = (long)Math.Round(gb * (1L << 30));
            }
            if (size == d.CapacityBytes) size = 0;
            if (size > 0 && size < d.CapacityBytes && d.MinBytes > 0 && size < d.MinBytes)
            {
                if (vm.Agent != null && !vm.Agent.IsLinux && d.GuestDisk >= 0)
                {
                    var dummy = new TextBox();
                    if (!VmSettingsDialog.ShrinkInGuest(this, vm, d, size, dummy))
                    {
                        status.Text = "Disk " + d.Slot + " keeps its size: its partitions reach " + Sizing.Gb(Sizing.MinBytes(d)) + ".";
                        ShowEditor("data");
                        return;
                    }
                }
                else
                {
                    status.Text = "Disk " + d.Slot + ": its partitions reach " + Sizing.Gb(Sizing.MinBytes(d)) + ". Shrink the volume in the guest first.";
                    ShowEditor("data");
                    return;
                }
            }
            if (size > 0 && size < d.CapacityBytes && d.MinBytes <= 0)
                status.Text = "Disk " + d.Slot + ": the partition layout is unknown, so a smaller size may be refused during the import.";
            else status.Text = "";
            d.TargetBytes = size;
            RefreshTree();
        }

        Control DevicesEditor(VmxInfo vm)
        {
            var g = Grid();
            var cores = new NumericUpDown { Minimum = 1, Maximum = 256, Value = Math.Max(1, Math.Min(256, vm.ProxmoxCores)), Width = 80, BackColor = Theme.Bg3, ForeColor = Theme.Text0 };
            var mem = new NumericUpDown { Minimum = 256, Maximum = 4 * 1024 * 1024, Increment = 512, Value = Math.Max(256, vm.ProxmoxMemoryMb), Width = 110, BackColor = Theme.Bg3, ForeColor = Theme.Text0, ThousandsSeparator = true };
            cores.ValueChanged += (s, e) => { vm.TargetCores = (int)cores.Value == vm.Cpus ? 0 : (int)cores.Value; RefreshTree(); };
            mem.ValueChanged += (s, e) => { vm.TargetMemoryMb = (int)mem.Value == vm.MemoryMb ? 0 : (int)mem.Value; RefreshTree(); };
            AddRow(g, "Number of vCPUs:", Flow(cores, Note("source: " + vm.Cpus)));
            AddRow(g, "Memory (MB):", Flow(mem, Note("source: " + vm.MemoryMb + " MB")));
            int rc = Sizing.Cores(vm), rm = Sizing.MemoryMb(vm);
            if (rc > 0 || rm > 0)
            {
                var use = Theme.MakeButton("Use recommended", false);
                use.Click += (s, e) => { if (rc > 0) cores.Value = rc; if (rm > 0) mem.Value = rm; };
                AddRow(g, "Measured use:", Note((rc > 0 ? rc + " vCPUs" : "") + (rc > 0 && rm > 0 ? ", " : "") + (rm > 0 ? rm + " MB" : "") + " recommended"));
                AddRow(g, "", use);
            }
            if (ToProxmox)
            {
                AddRow(g, "Disk controller:", Note("VirtIO SCSI single (recommended)" + (vm.IsWindows && !vm.HotClone ? "; Windows boots on SATA until the VirtIO drivers are installed" : "")));
                AddRow(g, "CPU type:", pCpu);
                AddRow(g, "", Note("host = fastest. In a cluster with different CPUs pick x86-64-v2-AES so live migration works."));
            }
            else
            {
                AddRow(g, "Disk controller:", Note((vm.IsWindows ? "SCSI LSI Logic SAS" : "SCSI LSI Logic") + " (recommended)"));
                AddRow(g, "Firmware:", Note(vm.Uefi ? "EFI" : "BIOS"));
            }
            AddRow(g, "", Note("Changing the vCPU count can affect the guest OS licence."));
            return g;
        }

        Control NetworksEditor(VmxInfo vm)
        {
            var g = Grid();
            AddRow(g, "Network adapters:", Note(Math.Max(1, vm.Macs.Count) + (vm.Macs.Count > 0 ? "  (" + string.Join(", ", vm.Macs) + ")" : "")));
            if (ToProxmox)
            {
                AddRow(g, "Bridge:", pBridge);
                bool sataFirst = vm.IsWindows && vm.Pve == null && (vm.HotClone ? !vm.VirtioOnCopy : !On(oVirtio));
                AddRow(g, "Controller type:", Note(sataFirst
                    ? "e1000e first, VirtIO after \"Finalize Windows VM\"" +
                      (vm.HotClone ? " (the VirtIO drivers are not installed on the machine; install virtio-win-guest-tools there first to start on VirtIO)" : "")
                    : "VirtIO (paravirtualized)"));
                AddRow(g, "", oNewMac);
            }
            else
            {
                if (Dest == DestKind.EsxiVm) AddRow(g, "Network:", dNetwork);
                else AddRow(g, "Network:", Note("\"VM Network\" (chosen when the OVF is deployed)"));
                AddRow(g, "Controller type:", dNic);
                AddRow(g, "", Note("VMXNET 3 needs VMware Tools in the guest; E1000e works without it."));
            }
            if (vm.Agent != null && agentInfo != null)
            {
                var report = AgentReport.FromJson(agentInfo);
                if (report.Network.Count > 0) AddRow(g, "Source settings:", Note(string.Join("\n", report.Network)));
            }
            return g;
        }

        Control AdvancedEditor()
        {
            var f = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            foreach (var c in new[] { oShutdownEsxi, oShutdownSource, oRestoreIp, oVirtio, oNewMac, oStart, oCleanup, oPowerOn, oKeepFiles })
                if (shownOptions.Contains(c)) f.Controls.Add(c);
            var notes = new List<string>();
            if (Source == SourceKind.Agent)
            {
                notes.Add("Live copy: changes made after the snapshot are not in the copy. If the source keeps running, the new VM has the same " +
                          "name and IP address - start only one of them on the network.");
                notes.Add("Before the snapshot the agent enables the boot drivers for the new hardware (" + (ToProxmox ? "SATA / VirtIO" : "LSI Logic SAS") + ").");
            }
            if (Source == SourceKind.Linux)
            {
                notes.Add("Live copy: files changed during the copy may be missed. If the source keeps running, the new VM has the same " +
                          "name and IP address - start only one of them on the network.");
                notes.Add("On the copy the converter adds the VirtIO drivers to the initramfs, renames en* network cards to ens18, ens19... " +
                          "and installs the EFI fallback boot loader when needed.");
            }
            if (Source == SourceKind.Proxmox)
                notes.Add(ToProxmox ? "Running VMs are copied from a vzdump snapshot backup and keep running; the copy gets the name, CPU, memory and bridge set here."
                                    : "The VM is exported powered off. Windows VMs boot on VMware only if their LSI Logic SAS driver is enabled.");
            if (Template) notes.Add("Templates: the source keeps running and its IP settings are not configured on the copy (clones get their own).");
            foreach (string n in notes) { var l = Note(n); l.MaximumSize = new Size(560, 0); l.Margin = new Padding(0, 10, 0, 0); f.Controls.Add(l); }
            return f;
        }

        // ---------------------------------------------------------------- Summary

        Panel BuildSummary()
        {
            var page = new Panel();
            summary = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Theme.Bg2, ForeColor = Theme.Text0, DetectUrls = false };
            page.Controls.Add(summary);
            return page;
        }

        void FillSummary()
        {
            summary.Clear();
            Action<string> head = t =>
            {
                summary.SelectionFont = new Font("Segoe UI Semibold", 10f);
                summary.SelectionColor = Theme.Accent;
                summary.AppendText(t + "\n");
            };
            Action<string, string> row = (k, v) =>
            {
                summary.SelectionFont = new Font("Consolas", 9.5f);
                summary.SelectionColor = Theme.Text2;
                summary.AppendText("    " + k.PadRight(34));
                summary.SelectionColor = Theme.Text0;
                summary.AppendText(v + "\n");
            };
            var vms = Machines();
            head("Source system information");
            row("Source type:", SrcLocal ? "This local machine (powered on)" : SrcRemote ? "Remote Windows machine (powered on)"
                              : SrcLinux ? "Remote Linux machine (powered on)" : SrcEsxi ? "VMware ESXi virtual machine"
                              : SrcPve ? "Proxmox VE virtual machine" : "VMware Workstation virtual machine");
            if (SrcRemote) row("Machine:", srcHost.Text.Trim());
            if (SrcLinux) row("Machine:", lHost.Text.Trim() + (linuxVm != null ? "  (" + linuxVm.Linux.Os + ", kernel " + linuxVm.Linux.Kernel + ")" : ""));
            if (SrcEsxi && sourceEsxi != null) row("ESXi host:", sourceEsxi.Host + "  (" + sourceEsxi.ProductName + ")");
            if (SrcPve) row("Proxmox VE host:", vHost.Text.Trim() + (pveSourceInfo.Length > 0 ? "  (" + pveSourceInfo + ")" : ""));
            foreach (var vm in vms)
            {
                row("Machine:", vm.DisplayName + "  -  " + vm.GuestOS);
                row("    vCPUs / memory:", vm.Cpus + " / " + vm.MemoryMb + " MB" + (vm.Uefi ? ", EFI" : ", BIOS"));
                row("    Disks:", vm.DiskCount + " disk(s), " + Theme.Bytes(vm.VirtualBytes) + (vm.TotalBytes > 0 ? ", " + Theme.Bytes(vm.TotalBytes) + " used" : ""));
            }
            if (Source == SourceKind.Agent) row("Copy:", "VSS snapshot, used blocks only, AES-256 encrypted");
            if (Source == SourceKind.Linux) row("Copy:", "files with rsync over SSH, disks recreated on the Proxmox host");
            if (Source == SourceKind.Proxmox) row("Copy:", ToProxmox ? "vzdump stream from host to host (snapshot mode)" : "stream-optimized VMDK, downloaded to this PC");
            summary.AppendText("\n");

            head("Destination system information");
            row("Destination type:", ConversionJob.DestLabel(Dest));
            if (ToProxmox)
            {
                row("Proxmox VE host:", pHost.Text.Trim() + (hostInfo != null ? "  (node " + hostInfo.Node + ", " + hostInfo.Version + ")" : ""));
                var st = SelectedStorage();
                row("Storage:", (st != null ? st.Name + " (" + st.Type + ")" : "-") + ", " + (pAlloc.SelectedIndex == 1 ? "thick" : "thin") + " disks");
                row("Network bridge / CPU type:", pBridge.Text + " / " + pCpu.Text);
            }
            else if (Dest == DestKind.EsxiVm)
            {
                row("ESXi host:", dHost.Text.Trim() + (targetEsxi != null ? "  (" + targetEsxi.ProductName + ")" : ""));
                var ds = dDatastore.SelectedItem as EsxiDatastore;
                row("Datastore:", (ds != null ? ds.Name : "-") + ", " + (dFormat.SelectedIndex == 1 ? "thick" : "thin") + " provisioned");
                row("Network:", dNetwork.Text + "  (" + dNic.Text + ")");
                row("Virtual machine version:", HardwareOf(dHw).ToString());
                row("Staging folder:", dStage.Text.Trim());
            }
            else
            {
                row("Folder:", ovfFolder.Text.Trim());
                row("Virtual machine version:", HardwareOf(oHw).ToString());
                row("Network adapter:", dNic.Text);
            }
            foreach (var vm in vms)
            {
                row("New VM:", vm.ProxmoxName + (ToProxmox && vm.TargetVmid > 0 ? "  (ID " + vm.TargetVmid + ")" : ""));
                row("    vCPUs / memory:", vm.ProxmoxCores + " / " + vm.ProxmoxMemoryMb + " MB");
                foreach (var d in vm.Disks)
                {
                    row("    " + d.Slot + ":", Sizing.Gb(d.CapacityBytes) + (d.TargetBytes > 0 && d.TargetBytes != d.CapacityBytes ? "  ->  " + Sizing.Gb(d.TargetBytes) : "  (maintain size)"));
                    if (!VolumeBased(vm)) continue;
                    foreach (var p in d.Parts)
                        if (p.Resizable && p.TargetBytes > 0 && p.TargetBytes != p.Size)
                            row("      " + Colon(p.Name), Sizing.Gb(p.Size) + "  ->  " + Sizing.Gb(p.TargetBytes) + "  (resized on the Proxmox host)");
                }
            }
            summary.AppendText("\n");

            head("Destination customization");
            foreach (var c in new[] { oShutdownEsxi, oShutdownSource, oRestoreIp, oVirtio, oNewMac, oStart, oCleanup, oPowerOn, oKeepFiles })
                if (shownOptions.Contains(c)) row(ShortText(c) + ":", c.Checked ? "Yes" : "No");
            if (Dest == DestKind.ProxmoxTemplate) row("Convert to template:", "Yes");
            summary.SelectionStart = 0;
            summary.ScrollToCaret();
        }

        // ================================================================ helpers

        static TableLayoutPanel Rows(int n)
        {
            // n rows: all sized to their content except the last, which fills the page
            var t = new TableLayoutPanel { ColumnCount = 1, RowCount = n, Margin = Padding.Empty };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < n; i++) t.RowStyles.Add(i == n - 1 ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));
            return t;
        }

        static TableLayoutPanel Grid()
        {
            var g = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Top, Margin = Padding.Empty };
            g.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            g.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            return g;
        }

        static void AddRow(TableLayoutPanel g, string label, Control field)
        {
            int row = g.RowCount++;
            g.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            g.Controls.Add(new Label { Text = label, AutoSize = true, ForeColor = Theme.Text1, Anchor = AnchorStyles.Left | AnchorStyles.Top, Margin = new Padding(0, 6, 12, 3) }, 0, row);
            field.Margin = new Padding(0, 3, 0, 3);
            g.Controls.Add(field, 1, row);
        }

        static Label Lbl(string text) { return new Label { Text = text, AutoSize = true, ForeColor = Theme.Text1, Margin = new Padding(0, 6, 10, 2) }; }

        static Label Group(string text)
        {
            return new Label { Text = text, AutoSize = true, ForeColor = Theme.Text0, Font = new Font("Segoe UI Semibold", 9.5f), Margin = new Padding(0, 8, 0, 6) };
        }

        static RadioButton Radio(string text) { return new RadioButton { Text = text, AutoSize = true, ForeColor = Theme.Text0, Margin = new Padding(0, 2, 0, 2) }; }
        static CheckBox Check(string text) { return new CheckBox { Text = text, AutoSize = true, ForeColor = Theme.Text1, Margin = new Padding(0, 3, 0, 3) }; }
        static Label Note(string text) { return new Label { Text = text, AutoSize = true, ForeColor = Theme.Text2, Margin = new Padding(0, 3, 0, 3), MaximumSize = new Size(620, 0) }; }

        static TextBox Box(int width)
        {
            var b = Theme.MakeTextBox();
            b.Dock = DockStyle.None;
            b.Width = width;
            return b;
        }

        static ComboBox Combo(int width)
        {
            var c = Theme.MakeCombo(false);
            c.Dock = DockStyle.None;
            c.Width = width;
            return c;
        }

        static FlowLayoutPanel Flow(params Control[] items)
        {
            var f = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
            foreach (var c in items) { c.Margin = new Padding(0, 2, 8, 2); f.Controls.Add(c); }
            return f;
        }

        // label/control pairs on one line ("Server: [   ]  User name: [   ]")
        static FlowLayoutPanel Line(params object[] items)
        {
            var f = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 2, 0, 4) };
            foreach (var i in items)
            {
                var s = i as string;
                Control c = s != null ? Lbl(s) : (Control)i;
                if (s == null) c.Margin = new Padding(0, 3, 12, 3);
                f.Controls.Add(c);
            }
            return f;
        }

        ListView List(string[] columns, bool checks)
        {
            var lv = new ListView
            {
                Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, CheckBoxes = checks,
                BackColor = Theme.Bg2, ForeColor = Theme.Text0, BorderStyle = BorderStyle.None, Margin = new Padding(0, 4, 0, 0),
            };
            float scale;
            using (var g = CreateGraphics()) scale = g.DpiX / 96f;
            foreach (string col in columns)
            {
                string[] p = col.Split('|');
                lv.Columns.Add(p[0], (int)(int.Parse(p[1]) * scale));
            }
            return lv;
        }

        void Browse(TextBox box, string description)
        {
            using (var dlg = new FolderBrowserDialog { Description = description, SelectedPath = box.Text })
                if (dlg.ShowDialog(this) == DialogResult.OK) box.Text = dlg.SelectedPath;
        }

        void Warn(string text) { MessageBox.Show(this, text, Program.Product, MessageBoxButtons.OK, MessageBoxIcon.Warning); }

        void Background(string message, Action work, Action done)
        {
            if (working) return;   // one connection at a time (pages stay enabled so their text stays readable)
            SetWorking(true, message);
            var t = new Thread(() =>
            {
                Exception error = null;
                try { work(); }
                catch (Exception ex) { error = ex; }
                Ui(() =>
                {
                    SetWorking(false, "");
                    if (error != null) { status.Text = ""; Warn(error.Message); }
                    else if (done != null) done();
                });
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

        void Status(string text) { Ui(() => status.Text = text); }

        void SetWorking(bool value, string message)
        {
            working = value;
            UseWaitCursor = value;
            status.Text = message;
            btnBack.Enabled = !value && page > 0;
            btnNext.Enabled = !value;
            // pages and the step list are not disabled: WinForms greys out all text of a disabled
            // panel, which was unreadable on the dark theme. Next/Back/JumpTo/Background check
            // "working" instead.
        }

        // ================================================================ defaults

        void LoadDefaults(IList<VmxInfo> listed)
        {
            foreach (string h in settings.Get("agenthosts", "").Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries)) srcHost.Items.Add(h);
            srcUser.Text = settings.Get("agentuser", "Administrator");
            sEsxiHost.Text = settings.Get("esxihost", "");
            sEsxiUser.Text = settings.Get("esxiuser", "root");
            foreach (string h in settings.Get("linuxhosts", "").Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries)) lHost.Items.Add(h);
            lPort.Text = settings.Get("linuxport", "22");
            lUser.Text = settings.Get("linuxuser", "root");
            vHost.Text = settings.Get("pvesrchost", "");
            vPort.Text = settings.Get("pvesrcport", "22");
            vUser.Text = settings.Get("pvesrcuser", "root");
            pHost.Text = settings.Get("host", "");
            pPort.Text = settings.Get("port", "22");
            pUser.Text = settings.Get("user", "root");
            pTemp.Text = settings.Get("upload", "/var/lib/vz/import/mtaalam-v2v");
            pCpu.Text = settings.Get("cpu", "host");
            pAlloc.SelectedIndex = settings.Get("alloc", "thin") == "thick" ? 1 : 0;
            dHost.Text = settings.Get("dstesxihost", "");
            dUser.Text = settings.Get("dstesxiuser", "root");
            dFormat.SelectedIndex = 0;
            string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MtaalamV2V");
            dStage.Text = settings.Get("stage", Path.Combine(data, "staging"));
            ovfFolder.Text = settings.Get("ovffolder", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "GeuzaVirt OVF"));
            FillHardware(oHw, 21, 13);
            oVirtio.Checked = settings.Get("virtio", "0") == "1";
            oNewMac.Checked = settings.Get("newmac", "0") == "1";
            oStart.Checked = settings.Get("start", "0") == "1";
            oCleanup.Checked = settings.Get("cleanup", "1") == "1";
            oShutdownEsxi.Checked = settings.Get("shutdown", "1") == "1";
            oRestoreIp.Checked = true;

            if (sourceEsxi != null)
            {
                sEsxiHost.Text = sourceEsxi.Host;
                sEsxiUser.Text = sourceEsxi.User;
                sEsxiPass.Text = sourceEsxi.Password;
                foreach (var vm in listed) if (vm.Esxi != null) esxiVms.Add(vm);
                FillEsxiList();
            }
            foreach (var vm in listed)
                if (vm.Esxi == null && !vm.HotClone && vm.State != VmState.Blocked) { localVms.Add(vm); vmxList.Items.Add(vm.DisplayName + "   " + vm.VmxPath); }

            rbOn.Checked = true;   // VMware Converter's default: powered on, this local machine
            FillSourceTypes();
            if (sourceEsxi != null) { rbOff.Checked = true; FillSourceTypes(); }
            FillDestTypes();
        }

        // .vmx files dropped on the main window: start on "VMware Workstation" with them listed
        public void StartWithFiles()
        {
            rbOff.Checked = true;
            FillSourceTypes();
            cboSrcType.SelectedIndex = 2;
        }

        // ================================================================ source connections

        void ConnectSourceEsxi()
        {
            string host = sEsxiHost.Text.Trim(), user = sEsxiUser.Text.Trim();
            if (host.Length == 0 || user.Length == 0) { Warn("Enter the ESXi host and user name (usually root)."); return; }
            var client = new EsxiClient(host, user, sEsxiPass.Text);
            List<EsxiVm> found = null;
            Background("Connecting to " + host + " ...", () =>
            {
                client.Login();
                if (client.IsVCenter) throw new Exception("That is a vCenter server. Connect to the ESXi host that runs the VMs.");
                found = client.ListVms();
            }, () =>
            {
                sourceEsxi = client;
                esxiVms.Clear();
                foreach (var e in found)
                {
                    var vm = VmxInfo.FromEsxi(e);
                    if (e.Template) vm.DisplayName += " (template)";
                    esxiVms.Add(vm);
                }
                FillEsxiList();
                status.Text = "Connected to " + client.ProductName + ": " + found.Count + " VM(s). Tick the VMs to convert.";
                settings.Set("esxihost", host);
                settings.Set("esxiuser", user);
            });
        }

        void FillEsxiList()
        {
            var ticked = new HashSet<VmxInfo>();
            foreach (ListViewItem i in esxiList.CheckedItems) ticked.Add((VmxInfo)i.Tag);
            esxiList.BeginUpdate();
            esxiList.Items.Clear();
            string q = sEsxiSearch.Text.Trim();
            foreach (var vm in esxiVms)
            {
                if (q.Length > 0 && vm.DisplayName.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0 && !ticked.Contains(vm)) continue;
                var item = new ListViewItem(new[]
                {
                    vm.DisplayName,
                    vm.Esxi != null && vm.Esxi.PowerState == "poweredOn" ? "Running" : vm.Esxi != null && vm.Esxi.PowerState == "suspended" ? "Suspended" : "Powered off",
                    vm.GuestOS ?? "", vm.DiskCount + " (" + Theme.Bytes(vm.VirtualBytes) + ")", Theme.Bytes(vm.TotalBytes),
                }) { Tag = vm };
                if (vm.State == VmState.Blocked) { item.ForeColor = Theme.Text2; item.ToolTipText = vm.StatusText; }
                item.Checked = ticked.Contains(vm);
                esxiList.Items.Add(item);
            }
            esxiList.EndUpdate();
        }

        void ConnectSourcePve()
        {
            int port;
            if (!int.TryParse(vPort.Text.Trim(), out port)) port = 22;
            var target = new SshTarget { Host = vHost.Text.Trim(), Port = port, User = vUser.Text.Trim().Length > 0 ? vUser.Text.Trim() : "root", Password = vPass.Text };
            if (target.Host.Length == 0) { Warn("Enter the Proxmox VE server name or IP address."); return; }
            List<VmxInfo> found = null;
            string node = null, version = null;
            Background("Connecting to " + target.Host + " ...", () =>
            {
                found = PveSourceVm.List(new SshClient(target), out node, out version);
            }, () =>
            {
                pveVms.Clear();
                foreach (var vm in found)
                {
                    if (vm.Pve.Template) vm.DisplayName += " (template)";
                    pveVms.Add(vm);
                }
                pveSourceInfo = "node " + node + ", " + version;
                FillPveList();
                status.Text = "Connected to Proxmox VE node " + node + ": " + found.Count + " VM(s). Tick the VMs to convert.";
                settings.Set("pvesrchost", target.Host);
                settings.Set("pvesrcport", port.ToString());
                settings.Set("pvesrcuser", target.User);
                UpdateHeader();
            });
        }

        void FillPveList()
        {
            var ticked = new HashSet<VmxInfo>();
            foreach (ListViewItem i in pveList.CheckedItems) ticked.Add((VmxInfo)i.Tag);
            pveList.BeginUpdate();
            pveList.Items.Clear();
            string q = vSearch.Text.Trim();
            foreach (var vm in pveVms)
            {
                if (q.Length > 0 && vm.DisplayName.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0 && !ticked.Contains(vm)) continue;
                var item = new ListViewItem(new[]
                {
                    vm.DisplayName, vm.Pve.Vmid.ToString(),
                    vm.Pve.Template ? "Template" : vm.Pve.Running ? "Running" : "Powered off",
                    vm.GuestOS ?? "", vm.DiskCount + " (" + Theme.Bytes(vm.VirtualBytes) + ")",
                }) { Tag = vm };
                if (vm.State == VmState.Blocked) { item.ForeColor = Theme.Text2; item.ToolTipText = vm.StatusText; }
                item.Checked = ticked.Contains(vm);
                pveList.Items.Add(item);
            }
            pveList.EndUpdate();
        }

        // Powered-on Linux machine: log in over SSH (root or sudo) and read its layout.
        void ConnectLinux(Action done)
        {
            int port;
            if (!int.TryParse(lPort.Text.Trim(), out port)) port = 22;
            var target = new SshTarget { Host = lHost.Text.Trim(), Port = port, User = lUser.Text.Trim().Length > 0 ? lUser.Text.Trim() : "root", Password = lPass.Text };
            if (target.Host.Length == 0) { Warn("Enter the IP address or name of the Linux machine."); return; }
            string key = LinuxKey();
            VmxInfo vm = null;
            Background("Connecting to " + target.Host + " ...", () =>
            {
                var src = LinuxSource.Connect(target);
                Status("Reading the disks and volumes of " + target.Host + " ...");
                vm = src.Probe();
            }, () =>
            {
                if (vm.Error != null)
                {
                    linuxVm = null;
                    Warn(target.Host + " cannot be converted: " + vm.Error);
                    return;
                }
                linuxVm = vm;
                linuxFor = key;
                var hosts = new List<string> { target.Host };
                foreach (string h in settings.Get("linuxhosts", "").Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries))
                    if (!h.Equals(target.Host, StringComparison.OrdinalIgnoreCase) && hosts.Count < 10) hosts.Add(h);
                settings.Set("linuxhosts", string.Join("|", hosts));
                settings.Set("linuxport", port.ToString());
                settings.Set("linuxuser", target.User);
                settings.Save();
                status.Text = "Connected to " + vm.DisplayName + " (" + vm.Linux.Os + ")." + (vm.Warnings.Count > 0 ? "  Note: " + vm.Warnings[0] : "");
                UpdateHeader();
                if (done != null) done();
            });
        }

        void AddLocal()
        {
            using (var dlg = new OpenFileDialog { Filter = "VMware virtual machine (*.vmx)|*.vmx", Multiselect = true, Title = "Select VMware virtual machines" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                foreach (string f in dlg.FileNames)
                {
                    string full = Path.GetFullPath(f);
                    if (localVms.Exists(v => v.VmxPath.Equals(full, StringComparison.OrdinalIgnoreCase))) continue;
                    var vm = VmxInfo.Load(full);
                    if (vm.Error != null) { Warn(vm.DisplayName + ": " + vm.Error); continue; }
                    localVms.Add(vm);
                    vmxList.Items.Add(vm.DisplayName + "   " + vm.VmxPath);
                }
            }
        }

        // Powered-on source: reach the Mtaalam V2V Agent, deploying it when needed
        // (like VMware Converter deploys its agent on the remote machine).
        void ConnectAgent(Action done)
        {
            bool local = SrcLocal;
            string host = local ? "127.0.0.1" : srcHost.Text.Trim();
            if (host.Length == 0) { Warn("Enter the IP address or name of the machine."); return; }
            string user = srcUser.Text.Trim(), pass = srcPass.Text;
            string key = local ? Secret.Unprotect(settings.Get("agentkey.local", "")) : srcKey.Text.Trim();
            if (!local && pass.Length == 0 && GuestAgent.NormalizeKey(key).Length < 12)
            {
                Warn("Enter the administrator user name and password of " + host + " (the converter deploys its agent there), " +
                     "or the pairing key of an agent that is already installed.");
                return;
            }
            GuestAgent agent = null;
            Dictionary<string, object> info = null;
            Background((local ? "Connecting to this machine" : "Connecting to " + host) + " ...", () =>
            {
                agent = new GuestAgent { Host = host, Key = key };
                string why = null;
                if (GuestAgent.NormalizeKey(key).Length >= 12)
                {
                    try
                    {
                        info = agent.Info();
                        object hot;
                        if (!info.TryGetValue("hot_clone", out hot) || !(hot is bool) || !(bool)hot) { why = "the installed agent is older than 1.2"; info = null; }
                        else if (OlderAgent(AgentReport.Str(info, "agent_version")) && (local || pass.Length > 0))
                        {
                            why = "updating the agent";
                            info = null;
                        }
                    }
                    catch (Exception ex) { why = ex.Message; }
                }
                if (info != null) return;
                if (local)
                    agent.Key = AgentInstaller.InstallLocal(msg => Status(msg));
                else
                {
                    if (pass.Length == 0)
                        throw new Exception("Cannot use the agent on " + host + (why != null ? " (" + why + ")" : "") + ". Enter the administrator password so the converter can deploy the agent.");
                    Status("Deploying the GeuzaVirt agent to " + host + " ...");
                    agent.Key = AgentInstaller.Install(host, user.Length > 0 ? user : "Administrator", pass, msg => Status(msg));
                }
                for (int i = 0; ; i++)
                {
                    try { info = agent.Info(); break; }
                    catch (Exception) { if (i >= 15) throw; Thread.Sleep(2000); }
                }
            }, () =>
            {
                var vm = VmxInfo.FromAgent(agent, info);
                if (vm.Error != null) { Warn(vm.Error); return; }
                agentVm = vm;
                agentInfo = info;
                agentFor = local ? "local" : host.ToLowerInvariant();
                settings.Set(local ? "agentkey.local" : "agentkey." + host, Secret.Protect(agent.Key));
                if (!local)
                {
                    srcKey.Text = agent.Key;
                    var hosts = new List<string> { host };
                    foreach (string h in settings.Get("agenthosts", "").Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries))
                        if (!h.Equals(host, StringComparison.OrdinalIgnoreCase) && hosts.Count < 10) hosts.Add(h);
                    settings.Set("agenthosts", string.Join("|", hosts));
                    settings.Set("agentuser", user);
                }
                settings.Save();
                status.Text = OlderAgent(AgentReport.Str(info, "agent_version"))
                    ? "Connected to an older agent on " + vm.DisplayName + ". Enter the administrator password and connect again to update it (faster copy, smaller disks when resizing, VirtIO detection)."
                    : "Connected to the agent on " + vm.DisplayName + ".";
                UpdateHeader();
                if (done != null) done();
            });
        }

        // newest agent build (bump when agent\src\Agent.cs changes)
        const string MinAgentVersion = "1.4.4";

        // agent_version is reported from 1.2.1 on; missing = older
        static bool OlderAgent(string version)
        {
            System.Version have, want;
            if (!System.Version.TryParse(version ?? "", out have)) return true;
            return System.Version.TryParse(MinAgentVersion, out want) && have < want;
        }

        void ViewSourceDetails()
        {
            if (SrcEsxi || SrcVmx || SrcPve) return;
            if (SrcLinux)
            {
                Action show = () => ShowText("Machine Details for " + linuxVm.DisplayName, linuxVm.Linux.Details(linuxVm));
                if (linuxVm == null || linuxFor != LinuxKey()) ConnectLinux(show);
                else show();
                return;
            }
            if (agentVm == null || agentFor != AgentKey()) { ConnectAgent(ShowDetailsDialog); return; }
            ShowDetailsDialog();
        }

        void ShowDetailsDialog()
        {
            var vm = agentVm;
            var report = AgentReport.FromJson(agentInfo);
            string platform = AgentReport.Str(agentInfo, "platform");
            var sb = new StringBuilder();
            Action<string, string> row = (k, v) => sb.Append(k.PadRight(22)).Append(v).Append("\r\n");
            row("Name:", vm.DisplayName);
            row("Machine type:", platform == "vmware" ? "VMware virtual machine" : platform == "kvm" ? "KVM / Proxmox virtual machine"
                               : platform == "hyperv" ? "Hyper-V virtual machine" : "Physical machine");
            row("Firmware:", vm.Uefi ? "EFI" : "BIOS");
            row("Operating system:", vm.AgentOs);
            row("Total size:", Theme.Bytes(vm.VirtualBytes));
            row("Number of vCPUs:", vm.Cpus.ToString());
            row("RAM:", vm.MemoryMb + " MB");
            sb.Append("Network:\r\n");
            foreach (string n in report.Network) sb.Append("    ").Append(n).Append("\r\n");
            sb.Append("Source disks/volumes layout:\r\n");
            foreach (var d in vm.Disks)
            {
                sb.Append("    Disk ").Append(d.GuestDisk).Append(" - ").Append(Sizing.Gb(d.CapacityBytes));
                if (d.UsedBytes >= 0) sb.Append(", ").Append(Sizing.Gb(d.UsedBytes)).Append(" used");
                sb.Append("\r\n");
                if (!string.IsNullOrEmpty(d.GuestLayout)) sb.Append("        ").Append(d.GuestLayout).Append("\r\n");
            }
            ShowText("Machine Details for " + vm.DisplayName, sb.ToString());
        }

        void ShowText(string caption, string text)
        {
            using (var f = new Form
            {
                Text = caption,
                Size = new Size(640, 480),
                StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false, MaximizeBox = false, ShowIcon = false,
                BackColor = Theme.Bg1, ForeColor = Theme.Text0, Font = Font,
            })
            {
                var box = new TextBox { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Both, WordWrap = false, Text = text,
                                        BackColor = Theme.Bg2, ForeColor = Theme.Text0, BorderStyle = BorderStyle.None, Font = new Font("Consolas", 9.5f) };
                var close = Theme.MakeButton("Close", false);
                close.Dock = DockStyle.Bottom;
                close.Click += (s, e) => f.Close();
                f.Controls.Add(box);
                f.Controls.Add(close);
                f.ShowDialog(this);
            }
        }

        // ================================================================ destination connections

        void ConnectDestination(Action done)
        {
            if (ToProxmox)
            {
                int port;
                if (!int.TryParse(pPort.Text.Trim(), out port)) port = 22;
                var target = new SshTarget { Host = pHost.Text.Trim(), Port = port, User = pUser.Text.Trim().Length > 0 ? pUser.Text.Trim() : "root", Password = pPass.Text };
                if (target.Host.Length == 0) { Warn("Enter the Proxmox VE server name or IP address."); return; }
                string id = target.User + "@" + target.Host + ":" + port;
                if (ssh != null && hostInfo != null && sshFor == id) { done(); return; }
                var client = new SshClient(target);
                HostInfo info = null;
                var inv = new List<string[]>();
                int next = 100;
                Background("Connecting to " + target.Host + " ...", () =>
                {
                    info = Converter.Probe(client);
                    Converter.InstallTool(client);
                    next = ReadProxmoxInventory(client, inv);
                }, () =>
                {
                    ssh = client;
                    hostInfo = info;
                    sshFor = id;
                    destInventory.Clear();
                    destInventory.AddRange(inv);
                    nextVmid = next;
                    foreach (var vm in Machines()) vm.TargetVmid = 0;
                    FillProxmoxLocation();
                    settings.Set("host", target.Host);
                    settings.Set("port", port.ToString());
                    settings.Set("user", target.User);
                    done();
                });
            }
            else if (Dest == DestKind.EsxiVm)
            {
                string host = dHost.Text.Trim(), user = dUser.Text.Trim();
                if (host.Length == 0 || user.Length == 0) { Warn("Enter the ESXi server and user name (usually root)."); return; }
                string id = user + "@" + host;
                if (targetEsxi != null && inventory != null && esxiFor == id) { done(); return; }
                var client = new EsxiClient(host, user, dPass.Text);
                EsxiInventory inv = null;
                List<EsxiVm> vms = null;
                Background("Connecting to " + host + " ...", () =>
                {
                    client.Login();
                    if (client.IsVCenter) throw new Exception("That is a vCenter server. Connect to an ESXi host.");
                    inv = client.Inventory();
                    vms = client.ListVms();
                }, () =>
                {
                    targetEsxi = client;
                    inventory = inv;
                    esxiFor = id;
                    destInventory.Clear();
                    foreach (var v in vms) destInventory.Add(new[] { v.Name, v.PowerState == "poweredOn" ? "Running" : "Powered off", inv.Host ?? host });
                    FillEsxiLocation();
                    settings.Set("dstesxihost", host);
                    settings.Set("dstesxiuser", user);
                    done();
                });
            }
            else
            {
                string folder = ovfFolder.Text.Trim();
                if (folder.Length == 0 || !Path.IsPathRooted(folder)) { Warn("Choose a folder on this PC (a full path)."); return; }
                try { Directory.CreateDirectory(folder); }
                catch (Exception ex) { Warn("Cannot use the folder " + folder + ": " + ex.Message); return; }
                destInventory.Clear();
                foreach (string f in Directory.GetFiles(folder, "*.ovf", SearchOption.AllDirectories))
                    destInventory.Add(new[] { Path.GetFileNameWithoutExtension(f), "OVF package", Path.GetDirectoryName(f) });
                done();
            }
        }

        // Existing VMs in the Proxmox cluster; returns the next free VM ID.
        static int ReadProxmoxInventory(SshClient client, List<string[]> inv)
        {
            var lines = new List<string>();
            client.Run("pvesh get /cluster/resources --type vm --output-format json 2>/dev/null; echo; echo NEXTID=$(pvesh get /cluster/nextid 2>/dev/null)", l => lines.Add(l));
            int next = 100;
            foreach (string l in lines)
            {
                string t = l.Trim();
                if (t.StartsWith("NEXTID="))
                {
                    int n;
                    if (int.TryParse(t.Substring(7).Trim().Trim('"'), out n)) next = n;
                }
                else if (t.StartsWith("["))
                {
                    try
                    {
                        var arr = new JavaScriptSerializer().DeserializeObject(t) as object[];
                        if (arr == null) continue;
                        foreach (var o in arr)
                        {
                            var d = o as Dictionary<string, object>;
                            if (d == null) continue;
                            bool template = AgentReport.Num(d, "template") > 0;
                            inv.Add(new[]
                            {
                                AgentReport.Str(d, "name"),
                                ((long)AgentReport.Num(d, "vmid")).ToString(),
                                AgentReport.Str(d, "node") + " · " + (template ? "template" : AgentReport.Str(d, "status")),
                            });
                        }
                    }
                    catch (Exception) { }
                }
            }
            inv.Sort((a, b) => string.Compare(a[0], b[0], StringComparison.OrdinalIgnoreCase));
            return next;
        }

        void FillProxmoxLocation()
        {
            lblNode.Text = "Node: " + hostInfo.Node + "   ";
            storageList.Items.Clear();
            string want = settings.Get("storage", "local-lvm");
            foreach (var s in hostInfo.Storages)
            {
                var item = new ListViewItem(new[] { s.Name, s.Type, Theme.Bytes(s.FreeBytes), "disk images" }) { Tag = s };
                storageList.Items.Add(item);
                if (s.Name == want) item.Selected = true;
            }
            if (storageList.SelectedItems.Count == 0 && storageList.Items.Count > 0) storageList.Items[0].Selected = true;
            pBridge.Items.Clear();
            foreach (string b in hostInfo.Bridges) pBridge.Items.Add(b);
            if (pBridge.Items.Count == 0) pBridge.Items.Add("vmbr0");
            pBridge.SelectedIndex = Math.Max(0, pBridge.Items.IndexOf(settings.Get("bridge", "vmbr0")));
        }

        void FillEsxiLocation()
        {
            lblHost.Text = (inventory.Host ?? dHost.Text.Trim()) + "  (" + targetEsxi.ProductName + ")";
            dDatastore.Items.Clear();
            int best = -1;
            for (int i = 0; i < inventory.Datastores.Count; i++)
            {
                dDatastore.Items.Add(inventory.Datastores[i]);
                if (best < 0 || inventory.Datastores[i].FreeBytes > inventory.Datastores[best].FreeBytes) best = i;
            }
            if (best >= 0) dDatastore.SelectedIndex = best;
            int max = MaxHardware(targetEsxi.ProductName);
            FillHardware(dHw, max, max);
            dNetwork.Items.Clear();
            foreach (var n in inventory.Networks) dNetwork.Items.Add(n);
            if (dNetwork.Items.Count > 0) dNetwork.SelectedIndex = 0;
            for (int i = 0; i < inventory.Networks.Count; i++) if (inventory.Networks[i].Name == "VM Network") dNetwork.SelectedIndex = i;
        }

        StorageItem SelectedStorage()
        {
            return storageList.SelectedItems.Count > 0 ? storageList.SelectedItems[0].Tag as StorageItem : null;
        }

        // ================================================================ navigation

        List<int> VisiblePages()
        {
            // like Converter: the destination pages appear once a destination is chosen
            if (reached <= PDestSystem) return new List<int> { PSource, PDestSystem, POptions, PSummary };
            return new List<int> { PSource, PDestSystem, PDestVm, PDestLocation, POptions, PSummary };
        }

        void ShowPage(int n)
        {
            page = n;
            reached = Math.Max(reached, n);
            for (int i = 0; i < pages.Length; i++) pages[i].Visible = i == n;
            title.Text = PageNames[n];
            subtitle.Text = PageHints[n];
            btnBack.Enabled = n > 0 && !working;
            btnNext.Text = n == PSummary ? "Finish" : "Next >";
            status.Text = "";

            nav.SuspendLayout();
            nav.Controls.Clear();
            foreach (int p in VisiblePages())
            {
                int target = p;
                if (p < n)   // earlier steps are links (later ones need Next and its checks)
                {
                    var link = new LinkLabel { Text = PageNames[p], AutoSize = true, LinkColor = Theme.Accent, ActiveLinkColor = Theme.Accent2, Margin = new Padding(0, 3, 0, 5) };
                    link.LinkClicked += (s, e) => JumpTo(target);
                    nav.Controls.Add(link);
                }
                else
                    nav.Controls.Add(new Label
                    {
                        Text = PageNames[p], AutoSize = true, Margin = new Padding(0, 3, 0, 5),
                        ForeColor = p == n ? Theme.Text0 : Theme.Text2,
                        Font = new Font("Segoe UI", 9.5f, p == n ? FontStyle.Bold : FontStyle.Regular),
                    });
            }
            nav.ResumeLayout();

            switch (n)
            {
                case PSource: SourceTypeChanged(); break;
                case PDestSystem: FillDestTypes(); DestTypeChanged(); break;
                case PDestVm:
                    FillMachinePicker(cboMachineVm);
                    LoadVmPage();
                    break;
                case PDestLocation:
                    locProxmox.Visible = ToProxmox;
                    locEsxi.Visible = Dest == DestKind.EsxiVm;
                    locOvf.Visible = Dest == DestKind.OvfTemplate;
                    lblTotal.Text = "Total source disks size: " + Theme.Bytes(TotalSource());
                    ShowDatastore();
                    break;
                case POptions:
                    UpdateOptionSet();
                    FillMachinePicker(cboMachineOpt);
                    ShowEditor(optSection);
                    break;
                case PSummary: FillSummary(); break;
            }
            UpdateHeader();
        }

        void JumpTo(int target)
        {
            if (working) return;
            if (page == PDestVm) SaveVmPage();
            ShowPage(target);
        }

        void GoBack()
        {
            if (working) return;
            if (page == PDestVm) SaveVmPage();
            var vis = VisiblePages();
            int i = vis.IndexOf(page);
            if (i > 0) ShowPage(vis[i - 1]);
        }

        void Forward()
        {
            var vis = VisiblePages();
            int i = vis.IndexOf(page);
            if (page == PDestSystem) { reached = Math.Max(reached, PDestVm); ShowPage(PDestVm); return; }
            if (i >= 0 && i + 1 < vis.Count) ShowPage(vis[i + 1]);
        }

        void Next()
        {
            if (working) return;
            switch (page)
            {
                case PSource:
                    if (SrcEsxi || SrcVmx || SrcPve)
                    {
                        if (Machines().Count == 0)
                        {
                            Warn(SrcEsxi ? "Connect to the ESXi host and tick the virtual machines to convert."
                               : SrcPve ? "Connect to the Proxmox VE host and tick the virtual machines to convert." : "Add at least one .vmx file.");
                            return;
                        }
                        foreach (var vm in Machines())
                            if (vm.State == VmState.Blocked) { Warn(vm.DisplayName + ": " + vm.StatusText); return; }
                        Forward();
                    }
                    else if (SrcLinux)
                    {
                        if (linuxVm != null && linuxFor == LinuxKey()) Forward();
                        else ConnectLinux(Forward);
                    }
                    else if (agentVm != null && agentFor == AgentKey()) Forward();
                    else ConnectAgent(Forward);
                    return;
                case PDestSystem:
                    ConnectDestination(Forward);
                    return;
                case PDestVm:
                    SaveVmPage();
                    if (!CheckNames()) return;
                    break;
                case PDestLocation:
                    if (!CheckLocation()) return;
                    break;
                case POptions:
                    if (!ConfirmRunning()) return;
                    break;
                case PSummary:
                    Job = BuildJob();
                    SaveSettings();
                    DialogResult = DialogResult.OK;
                    Close();
                    return;
            }
            Forward();
        }

        bool CheckNames()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ids = new HashSet<int>();
            foreach (var vm in Machines())
            {
                string name = vm.ProxmoxName;
                if (name.Length == 0) { Warn("Enter a name for the new virtual machine."); return false; }
                if (!seen.Add(name)) { Warn("Two machines would be called \"" + name + "\". Give each one its own name."); return false; }
                if (ToProxmox)
                {
                    if (vm.TargetVmid < 100) { Warn("Enter a VM ID of 100 or higher for " + name + "."); return false; }
                    if (!ids.Add(vm.TargetVmid)) { Warn("Two machines would get VM ID " + vm.TargetVmid + "."); return false; }
                    foreach (var r in destInventory)
                        if (r[1] == vm.TargetVmid.ToString()) { Warn("VM ID " + vm.TargetVmid + " is already used by \"" + r[0] + "\" on the Proxmox cluster."); return false; }
                }
                foreach (var r in destInventory)
                    if (string.Equals(r[0], name, StringComparison.OrdinalIgnoreCase))
                    {
                        if (Dest == DestKind.EsxiVm) { Warn("A virtual machine called \"" + name + "\" already exists on " + dHost.Text.Trim() + ". Choose another name."); return false; }
                        if (MessageBox.Show(this, "\"" + name + "\" already exists at the destination. Continue with the same name?", Program.Product,
                                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return false;
                    }
            }
            return true;
        }

        bool CheckLocation()
        {
            if (ToProxmox)
            {
                var storage = SelectedStorage();
                if (storage == null) { Warn("Select the storage for the disks."); return false; }
                if (Source == SourceKind.Linux && Array.IndexOf(new[] { "dir", "nfs", "cifs", "lvm", "lvmthin", "zfspool" }, storage.Type) < 0)
                { Warn("A Linux live copy needs an LVM-thin, LVM, ZFS or directory storage; " + storage.Name + " is " + storage.Type + "."); return false; }
                string t = pTemp.Text.Trim();
                if (!t.StartsWith("/") || t.Length < 4) { Warn("The temp folder must be an absolute path on the Proxmox host, e.g. /var/lib/vz/import/mtaalam-v2v"); return false; }
                long need = 0;
                foreach (var vm in Machines())
                {
                    long size = vm.ProxmoxDiskBytes;
                    // volume-based resize: the disk is copied at its old size first, then made smaller
                    foreach (var d in vm.Disks) if (d.VolumeResize && d.CapacityBytes > d.FinalBytes) size += d.CapacityBytes - d.FinalBytes;
                    need += pAlloc.SelectedIndex == 1 ? size : Math.Min(vm.TotalBytes > 0 ? vm.TotalBytes : size, size > 0 ? size : long.MaxValue);
                }
                if (need > storage.FreeBytes && MessageBox.Show(this, string.Format("{0} has {1} free; the machines need about {2}. Continue anyway?",
                        storage.Name, Theme.Bytes(storage.FreeBytes), Theme.Bytes(need)), Program.Product, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                    return false;
                return true;
            }
            if (Dest == DestKind.EsxiVm)
            {
                var ds = dDatastore.SelectedItem as EsxiDatastore;
                if (ds == null) { Warn("Choose a datastore."); return false; }
                string stage = dStage.Text.Trim();
                if (stage.Length == 0 || !Path.IsPathRooted(stage)) { Warn("Choose a staging folder on this PC (a full path)."); return false; }
                try { Directory.CreateDirectory(stage); }
                catch (Exception ex) { Warn("Cannot use the folder " + stage + ": " + ex.Message); return false; }
                long need = 0;
                foreach (var vm in Machines()) need += dFormat.SelectedIndex == 1 ? vm.ProxmoxDiskBytes : (vm.TotalBytes > 0 ? vm.TotalBytes : vm.ProxmoxDiskBytes);
                if (need > ds.FreeBytes && MessageBox.Show(this, string.Format("Datastore {0} has {1} free; the machines need about {2}. Continue anyway?",
                        ds.Name, Theme.Bytes(ds.FreeBytes), Theme.Bytes(need)), Program.Product, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                    return false;
            }
            return true;
        }

        bool ConfirmRunning()
        {
            if (Source == SourceKind.Proxmox)
            {
                if (ToProxmox) return true;   // vzdump snapshot: may keep running
                var on = new List<string>();
                foreach (var vm in Machines()) if (vm.Pve != null && vm.Pve.Running) on.Add(vm.DisplayName + "  (ID " + vm.Pve.Vmid + ")");
                if (on.Count == 0) return true;
                if (!On(oShutdownEsxi))
                {
                    Warn("These VMs are running on Proxmox VE:\n\n" + string.Join("\n", on) +
                         "\n\nShut them down, or tick \"Shut down running VMs\" under Advanced options.");
                    return false;
                }
                return MessageBox.Show(this, "These VMs will be SHUT DOWN on Proxmox VE before the export (stopped after 5 minutes if they do not react):\n\n" +
                    string.Join("\n", on) + "\n\nContinue?", Program.Product, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
            }
            if (Source != SourceKind.Esxi) return true;
            var running = new List<string>();
            var hard = new List<string>();
            foreach (var vm in Machines())
            {
                if (vm.Esxi == null || vm.Esxi.PowerState != "poweredOn") continue;
                if (vm.Esxi.ToolsStatus == "guestToolsRunning" || vm.Agent != null) running.Add(vm.DisplayName);
                else hard.Add(vm.DisplayName);
            }
            if (running.Count + hard.Count == 0) return true;
            if (!On(oShutdownEsxi))
            {
                running.AddRange(hard);
                Warn("These VMs are running and ESXi locks their disks:\n\n" + string.Join("\n", running) +
                     "\n\nShut them down, tick \"Shut down running VMs\" under Advanced options, or convert them powered on " +
                     "(source type Powered on > Remote Windows machine) for a live copy.");
                return false;
            }
            string text = "";
            if (running.Count > 0) text += "These VMs will be SHUT DOWN cleanly on ESXi before copying:\n\n" + string.Join("\n", running) + "\n\n";
            if (hard.Count > 0) text += "VMware Tools is not running in these VMs, so they will be POWERED OFF hard:\n\n" + string.Join("\n", hard) + "\n\n";
            text += "Continue?";
            return MessageBox.Show(this, text, Program.Product, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
        }

        void UpdateHeader()
        {
            var vms = Machines();
            if (SrcEsxi) hdrSource.Text = vms.Count == 0 ? "none" : vms.Count == 1 ? vms[0].DisplayName + " on " + sEsxiHost.Text.Trim() : vms.Count + " VMs on " + sEsxiHost.Text.Trim();
            else if (SrcPve) hdrSource.Text = vms.Count == 0 ? "none" : vms.Count == 1 ? vms[0].DisplayName + " on " + vHost.Text.Trim() + " (Proxmox VE)" : vms.Count + " VMs on " + vHost.Text.Trim() + " (Proxmox VE)";
            else if (SrcVmx) hdrSource.Text = vms.Count == 0 ? "none" : vms.Count == 1 ? vms[0].DisplayName + " (.vmx)" : vms.Count + " VMware Workstation VMs";
            else if (SrcLinux) hdrSource.Text = linuxVm != null && linuxFor == LinuxKey() ? lHost.Text.Trim() + " [" + linuxVm.Linux.Os + "]" : "none";
            else if (agentVm != null && agentFor == AgentKey())
                hdrSource.Text = (SrcLocal ? "This local machine" : srcHost.Text.Trim()) + " [" + agentVm.AgentOs + "]";
            else hdrSource.Text = "none";

            if (reached <= PDestSystem && page <= PDestSystem) { hdrDest.Text = "none"; return; }
            string name = vms.Count == 1 ? vms[0].ProxmoxName : vms.Count + " VMs";
            hdrDest.Text = ToProxmox ? name + " on " + (hostInfo != null ? hostInfo.Node : pHost.Text.Trim()) + " (Proxmox VE" + (Dest == DestKind.ProxmoxTemplate ? " template)" : ")")
                         : Dest == DestKind.EsxiVm ? name + " on " + dHost.Text.Trim() + " (" + (targetEsxi != null ? targetEsxi.ProductName : "VMware ESXi") + ")"
                         : name + " as OVF template";
        }

        // ================================================================ result

        static string LocalIpToward(string host)
        {
            try
            {
                using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    s.Connect(host, 9);
                    return ((IPEndPoint)s.LocalEndPoint).Address.ToString();
                }
            }
            catch (Exception) { return null; }
        }

        ConversionJob BuildJob()
        {
            var job = new ConversionJob { Source = Source, Dest = Dest, Vms = Machines(), SourceEsxi = SrcEsxi ? sourceEsxi : null };
            bool template = Template;
            if (SrcLocal && ToProxmox)
            {
                // the Proxmox host fetches the disks from this PC: use the address it can reach
                string ip = LocalIpToward(pHost.Text.Trim());
                if (ip != null) foreach (var vm in job.Vms) if (vm.Agent != null) vm.Agent.Host = ip;
            }
            if (ToProxmox)
            {
                job.Ssh = ssh;
                job.Host = hostInfo;
                var o = new ConvertOptions
                {
                    Storage = SelectedStorage().Name,
                    Bridge = pBridge.SelectedItem != null ? pBridge.SelectedItem.ToString() : "vmbr0",
                    Cpu = pCpu.Text.Trim().Length > 0 ? pCpu.Text.Trim() : "host",
                    RemoteBase = pTemp.Text.Trim(),
                    VirtioReady = On(oVirtio),
                    NewMac = On(oNewMac),
                    Start = On(oStart),
                    Cleanup = On(oCleanup),
                    ShutdownRunning = On(oShutdownEsxi),
                    Thick = pAlloc.SelectedIndex == 1,
                    Template = Dest == DestKind.ProxmoxTemplate,
                    ShutdownSource = !template && On(oShutdownSource),
                    RestoreNetwork = !template && On(oRestoreIp),
                    Esxi = job.SourceEsxi,
                    EsxiDirect = hostInfo.SupportsEsxiImport,
                    TempDirs = hostInfo.TempDirs,
                };
                foreach (var s in hostInfo.Storages) o.StorageTypes[s.Name] = s.Type;
                job.Proxmox = o;
            }
            else
            {
                bool toEsxi = Dest == DestKind.EsxiVm;
                string[] nicTypes = { null, "E1000e", "E1000", "VmxNet3" };
                job.Vmware = new VmwareOptions
                {
                    ImportToEsxi = toEsxi,
                    Target = toEsxi ? targetEsxi : null,
                    Inventory = toEsxi ? inventory : null,
                    Datastore = toEsxi ? (EsxiDatastore)dDatastore.SelectedItem : null,
                    Network = toEsxi ? dNetwork.SelectedItem as EsxiNetwork : null,
                    Thin = !toEsxi || dFormat.SelectedIndex != 1,
                    Folder = toEsxi ? dStage.Text.Trim() : ovfFolder.Text.Trim(),
                    KeepFiles = On(oKeepFiles),
                    PowerOn = On(oPowerOn),
                    ShutdownSource = !template && On(oShutdownSource),
                    RestoreNetwork = !template && On(oRestoreIp),
                    SourceEsxi = job.SourceEsxi,
                    ShutdownRunning = On(oShutdownEsxi),
                    HwVersion = HardwareOf(toEsxi ? dHw : oHw),
                    NicType = nicTypes[Math.Max(0, dNic.SelectedIndex)],
                };
            }
            return job;
        }

        void SaveSettings()
        {
            settings.Set("upload", pTemp.Text.Trim());
            settings.Set("cpu", pCpu.Text.Trim());
            settings.Set("alloc", pAlloc.SelectedIndex == 1 ? "thick" : "thin");
            var st = SelectedStorage();
            if (st != null) settings.Set("storage", st.Name);
            if (pBridge.SelectedItem != null) settings.Set("bridge", pBridge.SelectedItem.ToString());
            settings.Set("stage", dStage.Text.Trim());
            settings.Set("ovffolder", ovfFolder.Text.Trim());
            if (shownOptions.Contains(oVirtio)) settings.Set("virtio", oVirtio.Checked ? "1" : "0");
            if (shownOptions.Contains(oNewMac)) settings.Set("newmac", oNewMac.Checked ? "1" : "0");
            if (shownOptions.Contains(oStart)) settings.Set("start", oStart.Checked ? "1" : "0");
            if (shownOptions.Contains(oCleanup)) settings.Set("cleanup", oCleanup.Checked ? "1" : "0");
            if (shownOptions.Contains(oShutdownEsxi)) settings.Set("shutdown", oShutdownEsxi.Checked ? "1" : "0");
            settings.Save();
        }
    }
}
