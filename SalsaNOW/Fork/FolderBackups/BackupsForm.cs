using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SalsaNOW
{
    // Fork-only: the Backups window. All the work is done by BackupEngine; this only shows it.
    internal class BackupsForm : Form
    {
        private readonly string _devRoot;
        private readonly string _logFile;
        private readonly BackupEngine _engine = new BackupEngine();

        private readonly Label _account = new Label { AutoSize = true, Margin = new Padding(3, 9, 12, 3) };
        private readonly Button _login = new Button { Text = "Log in to GitHub", AutoSize = true, Visible = false };
        private readonly LinkLabel _repoLink = new LinkLabel { Text = "Open the repo on GitHub", AutoSize = true, Margin = new Padding(3, 9, 3, 3), Visible = false };
        private readonly LinkLabel _setupLog = new LinkLabel { Text = "Setup log (Node, Git, OpenCode)", AutoSize = true, Margin = new Padding(12, 9, 3, 3) };
        private readonly ListView _list = new ListView { View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false, Dock = DockStyle.Fill };
        private readonly NumericUpDown _minutes = new NumericUpDown { Minimum = 1, Maximum = 240, Value = BackupEngine.DefaultAutoMinutes, Width = 55, Margin = new Padding(3, 6, 3, 3) };
        private readonly ProgressBar _progress = new ProgressBar { Dock = DockStyle.Fill, Height = 18 };
        private readonly Label _progressText = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
        private readonly TextBox _log = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = SystemColors.Window };
        private readonly NotifyIcon _tray = new NotifyIcon();
        private readonly Timer _autoTimer = new Timer { Interval = 60 * 1000 };

        private readonly List<Button> _needsLogin = new List<Button>();
        private readonly List<Button> _needsSlot = new List<Button>();
        private LoginForm _loginForm;
        private bool _exiting;
        private bool _refreshQueued;
        private bool _minutesLoading;

        public BackupsForm(string devRoot)
        {
            _devRoot = devRoot;
            _logFile = Path.Combine(devRoot, "backups.log");

            Text = "SalsaNOW Backups";
            Font = new Font("Segoe UI", 9f);
            ClientSize = new Size(940, 560);
            MinimumSize = new Size(700, 420);
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = BackupsApp.LoadIcon() ?? Icon.ExtractAssociatedIcon(BackupsApp.ExePath); } catch { }

            BuildLayout();
            BuildTray();

            _engine.Log += message => OnUi(() => AppendLog(message));
            _engine.Progress += (text, percent) => OnUi(() => ShowProgress(text, percent));
            _engine.Changed += () => OnUi(QueueRefresh);

            _autoTimer.Tick += async (s, e) =>
            {
                try { await Task.Run(() => _engine.AutoTickAsync()); }
                catch (Exception ex) { AppendLog("Auto backup error: " + ex.Message); }
            };
            _minutes.ValueChanged += async (s, e) =>
            {
                if (!_minutesLoading)
                    await RunAsync(() => _engine.SetAutoMinutesAsync((int)_minutes.Value));
            };

            Shown += async (s, e) => await StartupAsync();
            RefreshAll();
        }

        // ---------- layout ----------

        private void BuildLayout()
        {
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(8) };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 120));

            var accountRow = Row();
            _login.Click += async (s, e) => await LoginAsync();
            _repoLink.LinkClicked += (s, e) => OpenUrl(_engine.Api.RepoWebUrl);
            _setupLog.LinkClicked += (s, e) => OpenLog(Path.Combine(_devRoot, "devtools.log"));
            accountRow.Controls.AddRange(new Control[] { _account, _login, _repoLink, _setupLog });

            var toolbar = Row();
            toolbar.Controls.Add(MakeButton("New slot...", true, false, NewSlotAsync));
            toolbar.Controls.Add(MakeButton("Back up all", true, false, BackupAllAsync));
            toolbar.Controls.Add(MakeButton("Sync all", true, false, SyncAllAsync));
            toolbar.Controls.Add(new Label { Text = "    Auto backup every", AutoSize = true, Margin = new Padding(3, 9, 0, 3) });
            toolbar.Controls.Add(_minutes);
            toolbar.Controls.Add(new Label { Text = "minutes (slots with Auto on, only when files changed)", AutoSize = true, Margin = new Padding(0, 9, 3, 3) });

            _list.Columns.Add("Slot", 150);
            _list.Columns.Add("Folder", 300);
            _list.Columns.Add("Auto", 45);
            _list.Columns.Add("Last backup", 120);
            _list.Columns.Add("Size", 70);
            _list.Columns.Add("Status", 230);
            _list.SelectedIndexChanged += (s, e) => UpdateButtons();
            _list.DoubleClick += async (s, e) => await RunAsync(EditSlotAsync);

            var slotRow = Row();
            slotRow.Controls.Add(MakeButton("Back up now", true, true, BackupSelectedAsync));
            slotRow.Controls.Add(MakeButton("Sync (restore)...", true, true, SyncSelectedAsync));
            slotRow.Controls.Add(MakeButton("Edit...", true, true, EditSlotAsync));
            slotRow.Controls.Add(MakeButton("Open folder", false, true, () =>
            {
                BackupSlot slot = Selected;
                if (Directory.Exists(slot.FullPath))
                    Process.Start("explorer.exe", "\"" + slot.FullPath + "\"");
                else
                    MessageBox.Show(this, "The folder does not exist yet:\n" + slot.FullPath, Text);
                return Task.CompletedTask;
            }));
            slotRow.Controls.Add(MakeButton("Delete slot...", true, true, DeleteSlotAsync));

            var progressRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = new Padding(0) };
            progressRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
            progressRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            progressRow.Controls.Add(_progress, 0, 0);
            progressRow.Controls.Add(_progressText, 1, 0);

            layout.Controls.Add(accountRow, 0, 0);
            layout.Controls.Add(toolbar, 0, 1);
            layout.Controls.Add(_list, 0, 2);
            layout.Controls.Add(slotRow, 0, 3);
            layout.Controls.Add(progressRow, 0, 4);
            layout.Controls.Add(_log, 0, 5);
            Controls.Add(layout);
        }

        private static FlowLayoutPanel Row()
        {
            return new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Margin = new Padding(0) };
        }

        private Button MakeButton(string text, bool needsLogin, bool needsSlot, Func<Task> action)
        {
            var button = new Button { Text = text, AutoSize = true, Padding = new Padding(4, 0, 4, 0) };
            button.Click += async (s, e) => await RunAsync(action);
            if (needsLogin) _needsLogin.Add(button);
            if (needsSlot) _needsSlot.Add(button);
            return button;
        }

        private void BuildTray()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("Open Backups", null, (s, e) => ShowAndActivate());
            menu.Items.Add("Back up all now", null, async (s, e) => await RunAsync(BackupAllAsync));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit Backups (stops automatic backup)", null, (s, e) =>
            {
                if (MessageBox.Show(this, "Exit Backups? Automatic backups stop until you open it again from the Backups shortcut.", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                {
                    _exiting = true;
                    Close();
                }
            });

            _tray.Text = "SalsaNOW Backups";
            _tray.Icon = Icon ?? SystemIcons.Application;
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += (s, e) => ShowAndActivate();
            _tray.Visible = true;
        }

        // ---------- startup and login ----------

        private async Task StartupAsync()
        {
            _autoTimer.Start();
            _account.Text = "Getting the GitHub tool ready...";
            AppendLog("Backups started. Slots marked Auto are backed up every few minutes once you have synced or backed them up in this session.");

            try
            {
                await Task.Run(() => GhCli.InstallAsync(_devRoot, message => OnUi(() => AppendLog(message))));
                if (await Task.Run(() => GhCli.IsLoggedInAsync()))
                    await ConnectAsync();
                else
                    await LoginAsync();
            }
            catch (Exception ex)
            {
                AppendLog("Could not get the GitHub tool ready: " + ex.Message);
                _account.Text = "GitHub tool missing; restart SalsaNOW to try again.";
            }
        }

        private async Task LoginAsync()
        {
            if (_loginForm != null && !_loginForm.IsDisposed)
            {
                _loginForm.Activate();
                return;
            }

            _account.Text = "Not logged in to GitHub.";
            _login.Visible = true;

            _loginForm = new LoginForm();
            _loginForm.LoggedIn += async () => await ConnectAsync();
            _loginForm.Show(this);
            await Task.CompletedTask;
        }

        private async Task ConnectAsync()
        {
            _account.Text = "Connecting to GitHub...";
            _login.Visible = false;
            try
            {
                string token = await Task.Run(() => GhCli.GetTokenAsync());
                if (string.IsNullOrEmpty(token))
                    throw new InvalidOperationException("No GitHub login found.");

                await Task.Run(() => _engine.ConnectAsync(token));
                _account.Text = $"Logged in as {_engine.Api.Owner}  ·  repo {_engine.Api.Owner}/{BackupEngine.RepoName} (private)";
                _repoLink.Visible = true;

                _minutesLoading = true;
                _minutes.Value = _engine.AutoMinutes;
                _minutesLoading = false;

                if (_engine.Slots.Count == 0)
                    AppendLog("No slots yet. Click \"New slot...\" to pick a folder to back up.");
                else
                    AppendLog("To get your folders back after reinstalling things, select a slot and click \"Sync (restore)...\", or \"Sync all\".");
            }
            catch (Exception ex)
            {
                AppendLog("Could not connect to GitHub: " + ex.Message);
                _account.Text = "Not connected to GitHub.";
                _login.Visible = true;
            }
            RefreshAll();
        }

        // ---------- slot actions ----------

        private BackupSlot Selected
        {
            get { return _list.SelectedItems.Count == 1 ? (BackupSlot)_list.SelectedItems[0].Tag : null; }
        }

        private async Task NewSlotAsync()
        {
            using (var dialog = new SlotForm(null))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK)
                    return;

                BackupSlot slot = await Task.Run(() => _engine.AddSlotAsync(dialog.SlotName, dialog.Folder, dialog.AutoBackup));
                AppendLog($"Created slot {slot.Name} for {slot.FullPath}");
                await Task.Run(() => _engine.BackupAsync(slot));
            }
        }

        private async Task EditSlotAsync()
        {
            BackupSlot slot = Selected;
            if (slot == null || !_engine.IsConnected)
                return;

            using (var dialog = new SlotForm(slot))
            {
                if (dialog.ShowDialog(this) == DialogResult.OK)
                    await Task.Run(() => _engine.UpdateSlotAsync(slot, dialog.Folder, dialog.AutoBackup));
            }
        }

        private async Task DeleteSlotAsync()
        {
            BackupSlot slot = Selected;
            string question = $"Delete the slot \"{slot.Name}\" and ALL of its backups from GitHub?\n\nThe folder on this PC is not touched.";
            if (MessageBox.Show(this, question, Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
                await Task.Run(() => _engine.DeleteSlotAsync(slot));
        }

        private async Task BackupSelectedAsync()
        {
            BackupSlot slot = Selected;
            if (ConfirmBackup(new[] { slot }))
                await Task.Run(() => _engine.BackupAsync(slot));
        }

        private async Task BackupAllAsync()
        {
            List<BackupSlot> slots = _engine.Slots.Where(s => Directory.Exists(s.FullPath)).ToList();
            foreach (BackupSlot missing in _engine.Slots.Except(slots))
                AppendLog($"Skipping {missing.Name}: its folder does not exist ({missing.FullPath}).");
            if (slots.Count == 0 || !ConfirmBackup(slots))
                return;

            foreach (BackupSlot slot in slots)
            {
                try { await Task.Run(() => _engine.BackupAsync(slot)); }
                catch (Exception ex) { AppendLog($"Back up of {slot.Name} failed: {ex.Message}"); }
            }
        }

        // A backup becomes the newest version, which is what Sync restores. Warn before backing up a folder
        // that wasn't synced in this session (it may be a fresh install) or is empty.
        private bool ConfirmBackup(IEnumerable<BackupSlot> slots)
        {
            var risky = new List<string>();
            foreach (BackupSlot slot in slots)
            {
                if (!Directory.Exists(slot.FullPath))
                {
                    MessageBox.Show(this, $"The folder for \"{slot.Name}\" does not exist:\n{slot.FullPath}", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }
                bool empty = !Directory.EnumerateFileSystemEntries(slot.FullPath).Any();
                if (empty)
                    risky.Add($"• {slot.Name}: the folder is empty");
                else if (slot.SyncedFingerprint == null && slot.LastBackupUtc.HasValue)
                    risky.Add($"• {slot.Name}: not synced in this session (last backup {slot.LastBackupUtc.Value.ToLocalTime():g})");
            }

            if (risky.Count == 0)
                return true;

            string question = "Backing up makes what is in the folder right now the newest backup, which is what Sync restores:\n\n"
                + string.Join("\n", risky)
                + "\n\nOlder backups stay available under Sync (restore) > Version. Back up anyway?";
            return MessageBox.Show(this, question, Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        }

        private async Task SyncSelectedAsync()
        {
            BackupSlot slot = Selected;
            List<ReleaseAsset> versions = await Task.Run(() => _engine.ListVersionsAsync(slot));
            if (versions.Count == 0)
            {
                MessageBox.Show(this, $"\"{slot.Name}\" has no backups yet.", Text);
                return;
            }

            using (var dialog = new RestoreForm(slot, versions))
            {
                if (dialog.ShowDialog(this) == DialogResult.OK)
                    await Task.Run(() => _engine.RestoreAsync(slot, dialog.Version, dialog.Target));
            }
        }

        private async Task SyncAllAsync()
        {
            if (_engine.Slots.Count == 0)
                return;

            string question = "Restore the newest backup of every slot into its original folder?\n\n"
                + string.Join("\n", _engine.Slots.Select(s => $"• {s.Name} → {s.FullPath}"))
                + "\n\nReinstall the games or programs first. Any folder already there is kept as \"..._before_restore_<time>\".";
            if (MessageBox.Show(this, question, Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            foreach (BackupSlot slot in _engine.Slots.ToList())
            {
                try
                {
                    List<ReleaseAsset> versions = await Task.Run(() => _engine.ListVersionsAsync(slot));
                    if (versions.Count == 0)
                    {
                        AppendLog($"Skipping {slot.Name}: no backups yet.");
                        continue;
                    }
                    await Task.Run(() => _engine.RestoreAsync(slot, versions[0], slot.FullPath));
                }
                catch (Exception ex)
                {
                    AppendLog($"Sync of {slot.Name} failed: {ex.Message}");
                }
            }
        }

        // ---------- helpers ----------

        private async Task RunAsync(Func<Task> action)
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                AppendLog("Error: " + ex.Message);
                MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            RefreshAll();
        }

        private void QueueRefresh()
        {
            if (_refreshQueued)
                return;
            _refreshQueued = true;
            LoginForm.SafeInvoke(this, () => { _refreshQueued = false; RefreshAll(); });
        }

        private void RefreshAll()
        {
            string selectedTag = Selected?.Tag;
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (BackupSlot slot in _engine.Slots.ToList())
            {
                var item = new ListViewItem(new[]
                {
                    slot.Name,
                    slot.Folder,
                    slot.AutoBackup ? "On" : "Off",
                    slot.LastBackupUtc.HasValue ? slot.LastBackupUtc.Value.ToLocalTime().ToString("dd MMM HH:mm") : "never",
                    slot.LastBackupUtc.HasValue ? BackupEngine.FormatBytes(slot.LastBackupBytes) : "",
                    slot.Status.Length > 0 ? slot.Status : (slot.SyncedFingerprint == null ? "Not synced this session" : "")
                }) { Tag = slot, ToolTipText = slot.FullPath };
                _list.Items.Add(item);
                if (slot.Tag == selectedTag)
                    item.Selected = true;
            }
            _list.EndUpdate();
            UpdateButtons();
        }

        private void UpdateButtons()
        {
            foreach (Button button in _needsLogin)
                button.Enabled = _engine.IsConnected;
            foreach (Button button in _needsSlot)
                button.Enabled = Selected != null && (_engine.IsConnected || !_needsLogin.Contains(button));
        }

        private void ShowProgress(string text, int percent)
        {
            _progressText.Text = text;
            if (percent < 0)
            {
                _progress.Style = ProgressBarStyle.Marquee;
            }
            else
            {
                _progress.Style = ProgressBarStyle.Continuous;
                _progress.Value = Math.Max(0, Math.Min(100, percent));
            }
        }

        private void AppendLog(string message)
        {
            string line = $"[{DateTime.Now:HH:mm:ss}] {message}";
            _log.AppendText(line + Environment.NewLine);
            try { File.AppendAllText(_logFile, line + Environment.NewLine); } catch { }
        }

        private void OnUi(Action action)
        {
            if (IsDisposed || !IsHandleCreated)
                return;
            if (InvokeRequired)
                LoginForm.SafeInvoke(this, action);
            else
                action();
        }

        private void OpenLog(string file)
        {
            if (!File.Exists(file))
            {
                MessageBox.Show(this, "There is no log yet at:\n" + file, Text);
                return;
            }
            try { Process.Start("notepad.exe", "\"" + file + "\""); }
            catch (Exception ex) { MessageBox.Show(this, file + "\n\n" + ex.Message, Text); }
        }

        private static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }

        public void ShowAndActivate()
        {
            Show();
            if (WindowState == FormWindowState.Minimized)
                WindowState = FormWindowState.Normal;
            Activate();
        }

        // Closing the window only minimizes it, so automatic backups keep running in the background
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_exiting && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                WindowState = FormWindowState.Minimized;
                _tray.ShowBalloonTip(5000, "Backups is still running", "Automatic backups keep going. Open it again from the Backups shortcut on the desktop.", ToolTipIcon.Info);
                return;
            }

            _autoTimer.Stop();
            _tray.Visible = false;
            _tray.Dispose();
            base.OnFormClosing(e);
        }
    }

    // ---------- login with a one-time code ----------

    internal class LoginForm : Form
    {
        // gh's events arrive on background threads, possibly after this window closed. An exception there
        // would end the whole SalsaNOW process, so a closed window just ignores them.
        public static void SafeInvoke(Control control, Action action)
        {
            try
            {
                if (!control.IsDisposed && control.IsHandleCreated)
                    control.BeginInvoke(action);
            }
            catch (InvalidOperationException) { }
        }

        private readonly Label _code = new Label { AutoSize = true, Font = new Font("Consolas", 30f, FontStyle.Bold), Text = "....-....", Margin = new Padding(3, 10, 3, 10) };
        private readonly Label _status = new Label { AutoSize = true, MaximumSize = new Size(420, 0), Text = "Asking GitHub for a code..." };
        private readonly Button _retry = new Button { Text = "Get a new code", AutoSize = true, Visible = false };
        private Process _gh;
        private bool _done;

        public event Action LoggedIn;

        public LoginForm()
        {
            Text = "Log in to GitHub - SalsaNOW Backups";
            Font = new Font("Segoe UI", 10f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            TopMost = true;

            var panel = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Padding = new Padding(16), WrapContents = false };
            panel.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(420, 0), Text = "On your phone, open github.com/login/device and type this code:" });
            panel.Controls.Add(_code);
            panel.Controls.Add(_status);

            var buttons = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 12, 0, 0) };
            var copy = new Button { Text = "Copy code", AutoSize = true };
            copy.Click += (s, e) => { if (_code.Text.Contains("-") && !_code.Text.StartsWith(".")) Clipboard.SetText(_code.Text); };
            var later = new Button { Text = "Later", AutoSize = true };
            later.Click += (s, e) => Close();
            _retry.Click += (s, e) => StartGh();
            buttons.Controls.AddRange(new Control[] { copy, _retry, later });
            panel.Controls.Add(buttons);
            panel.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(420, 0), ForeColor = SystemColors.GrayText, Text = "Nothing is saved on this PC: the login ends with the session. \"Later\" closes this; log in from the Backups window any time." });
            Controls.Add(panel);

            Shown += (s, e) => StartGh();
        }

        private void StartGh()
        {
            _retry.Visible = false;
            _code.Text = "....-....";
            _status.Text = "Asking GitHub for a code...";

            try
            {
                _gh = GhCli.StartLogin(code => SafeInvoke(this, () =>
                {
                    _code.Text = code;
                    _status.Text = "Waiting for you to enter the code on github.com/login/device ...";
                }));
                Process gh = _gh;
                gh.Exited += (s, e) => SafeInvoke(this, () => OnGhExited(gh));
                if (gh.HasExited)
                    OnGhExited(gh);
            }
            catch (Exception ex)
            {
                _status.Text = "Could not start the login: " + ex.Message;
                _retry.Visible = true;
            }
        }

        private void OnGhExited(Process gh)
        {
            if (_done || gh != _gh || IsDisposed)
                return;

            if (gh.ExitCode == 0)
            {
                _done = true;
                LoggedIn?.Invoke();
                Close();
                return;
            }

            _status.Text = "The code expired or the login was cancelled.";
            _retry.Visible = true;
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            Process gh = _gh;
            _gh = null;
            try { if (gh != null && !gh.HasExited) gh.Kill(); } catch { }
            base.OnFormClosed(e);
        }
    }

    // ---------- new / edit slot ----------

    internal class SlotForm : Form
    {
        private readonly TextBox _name = new TextBox { Width = 420 };
        private readonly TextBox _folder = new TextBox { Width = 330 };
        private readonly CheckBox _auto = new CheckBox { AutoSize = true, Checked = true, Text = "Back up automatically (every few minutes, only when files changed)" };

        public string SlotName { get { return _name.Text.Trim(); } }
        public string Folder { get { return BackupEngine.ExpandPath(_folder.Text.Trim().Trim('"')); } }
        public bool AutoBackup { get { return _auto.Checked; } }

        public SlotForm(BackupSlot slot)
        {
            Text = slot == null ? "New slot" : "Edit slot";
            Font = new Font("Segoe UI", 9.5f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;

            var panel = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Padding = new Padding(14), WrapContents = false };
            panel.Controls.Add(new Label { AutoSize = true, Text = "Name (becomes a folder in your salsanow-backups repo)" });
            panel.Controls.Add(_name);
            panel.Controls.Add(new Label { AutoSize = true, Text = "Folder to back up (everything inside it is zipped)", Margin = new Padding(3, 10, 3, 3) });

            var folderRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0), WrapContents = false };
            var browse = new Button { Text = "Browse...", AutoSize = true };
            browse.Click += (s, e) =>
            {
                using (var picker = new FolderBrowserDialog { Description = "Folder to back up", ShowNewFolderButton = false })
                {
                    if (Directory.Exists(Folder))
                        picker.SelectedPath = Folder;
                    if (picker.ShowDialog(this) == DialogResult.OK)
                        _folder.Text = picker.SelectedPath;
                }
            };
            folderRow.Controls.Add(_folder);
            folderRow.Controls.Add(browse);
            panel.Controls.Add(folderRow);
            panel.Controls.Add(new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Text = "You can paste a path, including ones like %APPDATA%\\Some folder." });
            panel.Controls.Add(_auto);

            var buttons = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 12, 0, 0) };
            var ok = new Button { Text = slot == null ? "Create and back up now" : "Save", AutoSize = true };
            var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
            ok.Click += (s, e) => Accept(slot);
            buttons.Controls.AddRange(new Control[] { ok, cancel });
            panel.Controls.Add(buttons);
            Controls.Add(panel);
            AcceptButton = ok;
            CancelButton = cancel;

            if (slot != null)
            {
                _name.Text = slot.Name;
                _name.ReadOnly = true;
                _folder.Text = slot.FullPath;
                _auto.Checked = slot.AutoBackup;
            }
        }

        private void Accept(BackupSlot existing)
        {
            if (SlotName.Length == 0)
            {
                MessageBox.Show(this, "Give the slot a name.", Text);
                return;
            }
            if (_folder.Text.Trim().Length == 0 || !Path.IsPathRooted(Folder))
            {
                MessageBox.Show(this, "Pick the folder to back up (a full path like C:\\...).", Text);
                return;
            }
            if (Path.GetPathRoot(Folder).TrimEnd('\\') == Folder.TrimEnd('\\'))
            {
                MessageBox.Show(this, "Pick a folder, not a whole drive.", Text);
                return;
            }
            if (existing == null && !Directory.Exists(Folder))
            {
                MessageBox.Show(this, "That folder does not exist:\n" + Folder, Text);
                return;
            }
            DialogResult = DialogResult.OK;
        }
    }

    // ---------- sync (restore) ----------

    internal class RestoreForm : Form
    {
        private readonly ComboBox _versions = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 420 };
        private readonly RadioButton _original = new RadioButton { AutoSize = true, Checked = true };
        private readonly RadioButton _other = new RadioButton { AutoSize = true, Text = "Another folder:" };
        private readonly TextBox _otherPath = new TextBox { Width = 330, Enabled = false };
        private readonly List<ReleaseAsset> _assets;
        private readonly BackupSlot _slot;

        public ReleaseAsset Version { get { return _assets[_versions.SelectedIndex]; } }
        public string Target { get { return _original.Checked ? _slot.FullPath : BackupEngine.ExpandPath(_otherPath.Text.Trim().Trim('"')); } }

        public RestoreForm(BackupSlot slot, List<ReleaseAsset> versions)
        {
            _slot = slot;
            _assets = versions;
            Text = "Sync " + slot.Name;
            Font = new Font("Segoe UI", 9.5f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;

            var panel = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Padding = new Padding(14), WrapContents = false };
            panel.Controls.Add(new Label { AutoSize = true, Text = "Version" });
            foreach (ReleaseAsset asset in versions)
            {
                _versions.Items.Add($"{asset.CreatedUtc.ToLocalTime():ddd dd MMM yyyy  HH:mm}   ·   {(BackupEngine.IsAutoVersion(asset) ? "auto" : "manual")}   ·   {BackupEngine.FormatBytes(asset.Size)}"
                    + (asset == versions[0] ? "   (newest)" : ""));
            }
            _versions.SelectedIndex = 0;
            panel.Controls.Add(_versions);

            panel.Controls.Add(new Label { AutoSize = true, Text = "Put it in", Margin = new Padding(3, 10, 3, 3) });
            _original.Text = "Its original folder: " + slot.FullPath;
            panel.Controls.Add(_original);

            var otherRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0), WrapContents = false };
            var browse = new Button { Text = "Browse...", AutoSize = true, Enabled = false };
            browse.Click += (s, e) =>
            {
                using (var picker = new FolderBrowserDialog { Description = "Folder to restore into (its contents are replaced)" })
                {
                    if (picker.ShowDialog(this) == DialogResult.OK)
                        _otherPath.Text = picker.SelectedPath;
                }
            };
            _other.CheckedChanged += (s, e) => { _otherPath.Enabled = browse.Enabled = _other.Checked; };
            otherRow.Controls.AddRange(new Control[] { _other, _otherPath, browse });
            panel.Controls.Add(otherRow);

            panel.Controls.Add(new Label
            {
                AutoSize = true,
                MaximumSize = new Size(480, 0),
                ForeColor = SystemColors.GrayText,
                Margin = new Padding(3, 10, 3, 3),
                Text = "Reinstall the game or program first. If the folder already has files, they are kept as \"<folder>_before_restore_<time>\" next to it, then the backup is unzipped in its place."
            });

            var buttons = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 12, 0, 0) };
            var ok = new Button { Text = "Sync", AutoSize = true };
            var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
            ok.Click += (s, e) =>
            {
                string target = Target;
                if (!Path.IsPathRooted(target) || Path.GetPathRoot(target).TrimEnd('\\') == target.TrimEnd('\\'))
                {
                    MessageBox.Show(this, "Pick a folder (not a whole drive).", Text);
                    return;
                }
                DialogResult = DialogResult.OK;
            };
            buttons.Controls.AddRange(new Control[] { ok, cancel });
            panel.Controls.Add(buttons);
            Controls.Add(panel);
            AcceptButton = ok;
            CancelButton = cancel;
        }
    }
}
