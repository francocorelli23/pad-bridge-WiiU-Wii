using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using PadBridge.Core;

namespace PadBridge.UI
{
    internal sealed class MainForm : Form
    {
        private readonly Settings _settings = Settings.Load();
        private readonly ControllerManager _manager;
        private readonly bool _startMinimized;

        private readonly ListView _list = new ListView();
        private readonly Button _pairBtn = new Button();
        private readonly Button _offBtn = new Button();
        private readonly Button _rumbleBtn = new Button();
        private readonly Label _status = new Label();
        private readonly Label _pairStatus = new Label();
        private readonly TextBox _log = new TextBox();
        private readonly NotifyIcon _tray = new NotifyIcon();
        private readonly System.Windows.Forms.Timer _refresh = new System.Windows.Forms.Timer();
        private CancellationTokenSource _pairCts;
        private bool _exiting;
        private bool _balloonShown;

        public MainForm(bool startMinimized)
        {
            _startMinimized = startMinimized || _settings.StartMinimized;
            _manager = new ControllerManager(_settings);
            _manager.Log += AppendLog;
            _manager.Changed += () => { try { BeginInvoke((Action)RefreshList); } catch { } };

            Text = "PadBridge – Wii controllers for PC";
            Icon = MakeIcon();
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(720, 560);
            MinimumSize = new Size(620, 480);
            Font = new Font("Segoe UI", 9f);

            BuildLayout();

            _tray.Icon = Icon;
            _tray.Text = "PadBridge";
            _tray.Visible = true;
            _tray.DoubleClick += (s, e) => ShowFromTray();
            var menu = new ContextMenuStrip();
            menu.Items.Add("Open", null, (s, e) => ShowFromTray());
            menu.Items.Add("Pair controller", null, (s, e) => TogglePairing());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, (s, e) => { _exiting = true; Close(); });
            _tray.ContextMenuStrip = menu;

            _refresh.Interval = 2000;
            _refresh.Tick += (s, e) => RefreshList();
        }

        private void BuildLayout()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(10) };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
            Controls.Add(root);

            _status.AutoSize = true;
            _status.Padding = new Padding(0, 0, 0, 6);
            root.Controls.Add(_status);

            _list.View = View.Details;
            _list.FullRowSelect = true;
            _list.HideSelection = false;
            _list.MultiSelect = false;
            _list.Dock = DockStyle.Fill;
            _list.Columns.Add("Player", 60);
            _list.Columns.Add("Controller", 230);
            _list.Columns.Add("Battery", 80);
            _list.Columns.Add("Address", 130);
            _list.Columns.Add("Status", 160);
            _list.SelectedIndexChanged += (s, e) => UpdateButtons();
            root.Controls.Add(_list);

            var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(0, 6, 0, 0) };
            _pairBtn.Text = "Pair controller";
            _pairBtn.AutoSize = true;
            _pairBtn.Click += (s, e) => TogglePairing();
            _offBtn.Text = "Turn off selected";
            _offBtn.AutoSize = true;
            _offBtn.Click += (s, e) => { if (Selected != null) _manager.PowerOff(Selected); };
            _rumbleBtn.Text = "Identify (rumble)";
            _rumbleBtn.AutoSize = true;
            _rumbleBtn.Click += (s, e) => { if (Selected != null) _manager.Rumble(Selected, 400); };
            buttons.Controls.AddRange(new Control[] { _pairBtn, _offBtn, _rumbleBtn });
            root.Controls.Add(buttons);

            _pairStatus.AutoSize = true;
            _pairStatus.ForeColor = Color.DarkBlue;
            _pairStatus.Font = new Font(Font, FontStyle.Bold);
            _pairStatus.Padding = new Padding(0, 4, 0, 4);
            root.Controls.Add(_pairStatus);

            root.Controls.Add(BuildOptions());

            _log.Multiline = true;
            _log.ReadOnly = true;
            _log.ScrollBars = ScrollBars.Vertical;
            _log.Dock = DockStyle.Fill;
            _log.BackColor = SystemColors.Window;
            _log.Font = new Font("Consolas", 8.5f);
            root.Controls.Add(_log);
        }

        private Control BuildOptions()
        {
            var g = new GroupBox { Text = "Options", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(8) };
            var f = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
            g.Controls.Add(f);

            var layout = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 230 };
            layout.Items.AddRange(new object[] { "Layout: by position (Xbox style)", "Layout: by label (A = A)" });
            layout.SelectedIndex = _settings.PositionalLayout ? 0 : 1;
            layout.SelectedIndexChanged += (s, e) => { _settings.PositionalLayout = layout.SelectedIndex == 0; SaveSettings(); };

            var rumble = new CheckBox { Text = "Rumble", Checked = _settings.Rumble, AutoSize = true, Margin = new Padding(12, 5, 3, 3) };
            rumble.CheckedChanged += (s, e) => { _settings.Rumble = rumble.Checked; SaveSettings(); };

            var dzLabel = new Label { Text = "Deadzone %", AutoSize = true, Margin = new Padding(12, 6, 3, 3) };
            var dz = new NumericUpDown { Minimum = 0, Maximum = 50, Value = _settings.DeadzonePercent, Width = 50 };
            dz.ValueChanged += (s, e) => { _settings.DeadzonePercent = (int)dz.Value; SaveSettings(); };

            var idleLabel = new Label { Text = "Auto-off after idle (min, 0 = never)", AutoSize = true, Margin = new Padding(12, 6, 3, 3) };
            var idle = new NumericUpDown { Minimum = 0, Maximum = 240, Value = _settings.IdleOffMinutes, Width = 55 };
            idle.ValueChanged += (s, e) => { _settings.IdleOffMinutes = (int)idle.Value; SaveSettings(); };

            var tray = new CheckBox { Text = "Close to tray", Checked = _settings.MinimizeToTray, AutoSize = true, Margin = new Padding(3, 5, 3, 3) };
            tray.CheckedChanged += (s, e) => { _settings.MinimizeToTray = tray.Checked; SaveSettings(); };

            var startMin = new CheckBox { Text = "Start minimized", Checked = _settings.StartMinimized, AutoSize = true, Margin = new Padding(12, 5, 3, 3) };
            startMin.CheckedChanged += (s, e) => { _settings.StartMinimized = startMin.Checked; SaveSettings(); };

            var startup = new CheckBox { Text = "Start with Windows", AutoSize = true, Margin = new Padding(12, 5, 3, 3) };
            try { startup.Checked = Settings.StartWithWindows; } catch { }
            startup.CheckedChanged += (s, e) =>
            {
                try { Settings.StartWithWindows = startup.Checked; }
                catch (Exception ex) { AppendLog("Could not change startup setting: " + ex.Message); }
            };

            f.Controls.AddRange(new Control[] { layout, rumble, dzLabel, dz, idleLabel, idle });
            f.SetFlowBreak(idle, true);
            f.Controls.AddRange(new Control[] { tray, startMin, startup });
            return g;
        }

        private void SaveSettings()
        {
            _settings.Save();
            _manager.ApplySettings();
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            AppendLog("PadBridge started. Paired controllers connect automatically when you press a button on them.");
            if (Program.OtherDriverAppRunning(out string other))
                AppendLog($"Warning: {other} is running. Close it so both apps don't fight over the controllers.");
            _manager.Start();
            _refresh.Start();
            RefreshList();
            if (_startMinimized)
            {
                WindowState = FormWindowState.Minimized;
                BeginInvoke((Action)(() => Hide()));
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (WindowState == FormWindowState.Minimized && _settings.MinimizeToTray) Hide();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_exiting && _settings.MinimizeToTray && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                if (!_balloonShown)
                {
                    _balloonShown = true;
                    _tray.ShowBalloonTip(3000, "PadBridge", "Still running in the tray. Right-click the icon to exit.", ToolTipIcon.Info);
                }
                return;
            }
            _pairCts?.Cancel();
            _refresh.Stop();
            _tray.Visible = false;
            _manager.Dispose();
            base.OnFormClosing(e);
        }

        private void ShowFromTray()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        private string Selected => _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as string : null;

        private void UpdateButtons()
        {
            bool sel = Selected != null;
            _offBtn.Enabled = sel;
            _rumbleBtn.Enabled = sel;
        }

        private void RefreshList()
        {
            if (IsDisposed) return;
            var items = _manager.Snapshot();
            string selected = Selected;
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var i in items)
            {
                var it = new ListViewItem(new[] { i.Player > 0 ? i.Player.ToString() : "-", i.Type, i.Battery, i.Address, i.Status }) { Tag = i.Key };
                _list.Items.Add(it);
                if (i.Key == selected) it.Selected = true;
            }
            if (items.Count == 0)
                _list.Items.Add(new ListViewItem(new[] { "", "No controller connected", "", "", "Press a button or use Pair" }));
            _list.EndUpdate();
            UpdateButtons();
            _status.Text = $"{_manager.BluetoothStatus}   •   {_manager.BusStatus}   •   {items.Count} controller(s) connected";
            _tray.Text = $"PadBridge – {items.Count} controller(s)";
        }

        private void AppendLog(string s)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { try { BeginInvoke((Action<string>)AppendLog, s); } catch { } return; }
            if (_log.TextLength > 60000) _log.Text = _log.Text.Substring(_log.TextLength - 30000);
            var line = $"[{DateTime.Now:HH:mm:ss}] {s}\r\n";
            _log.AppendText(line);
            try
            {
                var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PadBridge");
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "log.txt"), line);
            }
            catch { }
        }

        private void TogglePairing()
        {
            if (_pairCts != null)
            {
                _pairCts.Cancel();
                return;
            }
            ShowFromTray();
            _pairCts = new CancellationTokenSource();
            var ct = _pairCts.Token;
            _pairBtn.Text = "Stop pairing";
            _manager.PairingActive = true;
            var pairing = new Pairing();
            pairing.Log += AppendLog;
            pairing.Status += s => { try { BeginInvoke((Action)(() => _pairStatus.Text = s ?? "")); } catch { } };
            AppendLog("Pairing: press the red SYNC button on each controller now.");
            Task.Run(() =>
            {
                try { return pairing.Run(ct, 40, _manager.IsActive); }
                catch (Exception ex) { AppendLog("Pairing error: " + ex.Message); return 0; }
            }).ContinueWith(t =>
            {
                _pairCts = null;
                _manager.PairingActive = false;
                _pairBtn.Text = "Pair controller";
                _pairStatus.Text = "";
                if (t.Result > 0) AppendLog($"Pairing finished: {t.Result} controller(s). They will appear above in a few seconds.");
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private static Icon MakeIcon()
        {
            using (var bmp = new Bitmap(32, 32))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (var path = new GraphicsPath())
                    {
                        path.AddArc(2, 8, 12, 18, 90, 180);
                        path.AddArc(18, 8, 12, 18, 270, 180);
                        path.CloseFigure();
                        using (var b = new SolidBrush(Color.FromArgb(0, 150, 200))) g.FillPath(b, path);
                    }
                    using (var w = new SolidBrush(Color.White))
                    {
                        g.FillRectangle(w, 7, 15, 6, 2);
                        g.FillRectangle(w, 9, 13, 2, 6);
                        g.FillEllipse(w, 20, 12, 4, 4);
                        g.FillEllipse(w, 23, 16, 4, 4);
                    }
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }
    }
}
