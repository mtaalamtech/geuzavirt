using System;
using System.Collections.Generic;
using System.Drawing;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Mtaalam.V2V
{
    class AgentRequest
    {
        public string Host;
        public bool Linux;
        public string Key;
        public bool Install;
        public string User;
        public string Password;
        public int SshPort = 22;
    }

    // "Guest agent > Connect": where the guest is and how to reach it.
    static class AgentDialog
    {
        public static AgentRequest Show(IWin32Window owner, VmxInfo vm, bool install, Settings settings)
        {
            using (var form = new Form
            {
                Text = "Guest agent" + (vm != null ? " - " + vm.DisplayName : ""),
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
                var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true };
                var host = Field(vm != null ? settings.Get("agenthost." + vm.DisplayName, "") : "");
                var os = Theme.MakeCombo(false);
                os.Dock = DockStyle.None;
                os.Width = 300;
                os.Items.AddRange(new object[] { "Windows - GeuzaVirt agent service", "Linux - log in over SSH (nothing to install)" });
                os.SelectedIndex = install || vm == null || vm.IsWindows ? 0 : 1;
                var key = Field("");
                var doInstall = new CheckBox { Text = "Install or update the agent from this PC (needs an administrator account of the VM)", AutoSize = true, Checked = install, ForeColor = Theme.Text1, Margin = new Padding(0, 6, 0, 2) };
                var user = Field("Administrator");
                var pass = Field("");
                pass.UseSystemPasswordChar = true;
                var port = Field("22");
                var userLabel = new Label { AutoSize = true, ForeColor = Theme.Text1, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 12, 0) };

                Action fillKey = () =>
                {
                    string saved = Secret.Unprotect(settings.Get("agentkey." + host.Text.Trim(), ""));
                    if (saved.Length > 0 && key.Text.Trim().Length == 0) key.Text = saved;
                };
                fillKey();
                host.Leave += (s, e) => fillKey();

                AddRow(grid, "VM IP address", host);
                AddRow(grid, "Guest OS", os);
                AddRow(grid, "Pairing key", key);
                grid.Controls.Add(doInstall, 0, grid.RowCount);
                grid.SetColumnSpan(doInstall, 2);
                grid.RowCount++;
                int userRow = grid.RowCount++;
                grid.Controls.Add(userLabel, 0, userRow);
                grid.Controls.Add(user, 1, userRow);
                AddRow(grid, "Password", pass);
                AddRow(grid, "SSH port", port);

                var info = new Label { AutoSize = true, ForeColor = Theme.Text2, MaximumSize = new Size(520, 0), Margin = new Padding(0, 10, 0, 10) };
                grid.Controls.Add(info, 0, grid.RowCount);
                grid.SetColumnSpan(info, 2);
                grid.RowCount++;

                Action update = () =>
                {
                    bool linux = os.SelectedIndex == 1;
                    doInstall.Enabled = !linux;
                    bool inst = !linux && doInstall.Checked;
                    key.Enabled = !linux && !inst;
                    user.Enabled = pass.Enabled = linux || inst;
                    port.Enabled = linux;
                    userLabel.Text = linux ? "SSH user" : "Administrator";
                    if (linux && user.Text == "Administrator") user.Text = "root";
                    if (!linux && user.Text == "root") user.Text = "Administrator";
                    info.Text = linux
                        ? "The converter logs in over SSH, measures CPU and memory for 30 seconds and reads the disks. " +
                          "Linux volumes cannot be shrunk while the VM runs."
                        : inst
                            ? "Copies the agent to \\\\VM\\ADMIN$ and starts it as a service (like PsExec). Use the VM's built-in " +
                              "Administrator or a domain admin; file sharing (TCP 445) must be open. Otherwise copy the agent " +
                              "folder into the VM and run Install-Agent.cmd there."
                            : "Enter the key shown when the agent was installed (run MtaalamV2VAgent.exe in the VM to show it again). " +
                              "The agent listens on TCP " + GuestAgent.DefaultPort + ".";
                };
                os.SelectedIndexChanged += (s, e) => update();
                doInstall.CheckedChanged += (s, e) => update();
                update();

                var ok = Theme.MakeButton(install ? "Install" : "Connect", true);
                var cancel = Theme.MakeButton("Cancel", false);
                cancel.DialogResult = DialogResult.Cancel;
                var buttons = new FlowLayoutPanel { AutoSize = true, Anchor = AnchorStyles.Right };
                buttons.Controls.Add(ok);
                buttons.Controls.Add(cancel);
                grid.Controls.Add(buttons, 0, grid.RowCount);
                grid.SetColumnSpan(buttons, 2);
                form.Controls.Add(grid);
                form.AcceptButton = ok;
                form.CancelButton = cancel;

                AgentRequest result = null;
                ok.Click += (s, e) =>
                {
                    var r = new AgentRequest
                    {
                        Host = host.Text.Trim(),
                        Linux = os.SelectedIndex == 1,
                        Key = key.Text.Trim(),
                        User = user.Text.Trim(),
                        Password = pass.Text,
                    };
                    r.Install = !r.Linux && doInstall.Checked;
                    if (r.Host.Length == 0) { MessageBox.Show(form, "Enter the VM's IP address or host name."); return; }
                    if (r.Linux && (!int.TryParse(port.Text.Trim(), out r.SshPort) || r.SshPort <= 0)) { MessageBox.Show(form, "Enter the SSH port."); return; }
                    if (!r.Linux && !r.Install && GuestAgent.NormalizeKey(r.Key).Length < 12)
                    { MessageBox.Show(form, "Enter the pairing key shown by the agent in the VM, or tick \"Install or update the agent\"."); return; }
                    if ((r.Linux || r.Install) && r.User.Length == 0) { MessageBox.Show(form, "Enter the user name."); return; }
                    result = r;
                    form.DialogResult = DialogResult.OK;
                };
                return form.ShowDialog(owner) == DialogResult.OK ? result : null;
            }
        }

        static TextBox Field(string value)
        {
            var box = Theme.MakeTextBox();
            box.Dock = DockStyle.None;
            box.Width = 300;
            box.Text = value;
            return box;
        }

        static void AddRow(TableLayoutPanel grid, string label, Control field)
        {
            int row = grid.RowCount++;
            grid.Controls.Add(new Label { Text = label, AutoSize = true, ForeColor = Theme.Text1, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 12, 0) }, 0, row);
            grid.Controls.Add(field, 1, row);
        }
    }

    // Shows a small "working..." window while work runs on a background thread.
    static class WaitDialog
    {
        public static T Run<T>(IWin32Window owner, string text, Func<T> work)
        {
            T result = default(T);
            Exception error = null;
            using (var form = new Form
            {
                Text = Program.Product,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                ControlBox = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = Theme.Bg1,
                ForeColor = Theme.Text0,
                Font = new Font("Segoe UI", 9.5f),
                Padding = new Padding(18),
            })
            {
                var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true };
                layout.Controls.Add(new Label { Text = text, AutoSize = true, MaximumSize = new Size(460, 0), Margin = new Padding(0, 0, 0, 10) });
                layout.Controls.Add(new ProgressBar { Style = ProgressBarStyle.Marquee, Width = 440, Height = 14 });
                form.Controls.Add(layout);
                form.Shown += (s, e) =>
                {
                    var t = new Thread(() =>
                    {
                        try { result = work(); }
                        catch (Exception ex) { error = ex; }
                        try { form.BeginInvoke((Action)(() => form.Close())); }
                        catch (InvalidOperationException) { }
                    });
                    t.IsBackground = true;
                    t.Start();
                };
                form.ShowDialog(owner);
            }
            if (error != null) throw error;
            return result;
        }
    }

    // Pairing keys are stored for this Windows user only (DPAPI).
    static class Secret
    {
        public static string Protect(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(text), null, DataProtectionScope.CurrentUser));
        }

        public static string Unprotect(string data)
        {
            if (string.IsNullOrEmpty(data)) return "";
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(data), null, DataProtectionScope.CurrentUser)); }
            catch (Exception) { return ""; }
        }
    }
}
