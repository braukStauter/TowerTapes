using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TowerTapes;

public sealed class MainForm : Form
{
    private readonly Config _config;
    private readonly ProcessWatcher _watcher;
    private readonly AudioRecorder _recorder;
    private readonly StorageManager _storage;
    private NotifyIcon _tray = null!;
    private readonly System.Windows.Forms.Timer _cleanupTimer;
    private readonly System.Windows.Forms.Timer _uiTimer;

    private DataGridView _grid = null!;
    private Label _statusLabel = null!;
    private Label _storageLabel = null!;
    private DateTime _recordingStart;
    private List<RecordingInfo> _recordings = [];
    private bool _suppressShow = true;
    private Bitmap? _logoBitmap;
    private bool _isTrainingRecording;
    private ToolStripMenuItem _trainingMenuItem = null!;

    public MainForm()
    {
        _config = Config.Load();
        _storage = new StorageManager(_config);
        _recorder = new AudioRecorder(_config);
        _watcher = new ProcessWatcher();

        LoadLogo();
        _storage.EnsureDirectory();
        _storage.Cleanup();

        BuildForm();
        BuildTray();

        _watcher.CrcStarted += OnCrcStarted;
        _watcher.CrcStopped += OnCrcStopped;
        _watcher.Start();

        _cleanupTimer = new System.Windows.Forms.Timer { Interval = 3600_000 };
        _cleanupTimer.Tick += (_, _) => _storage.Cleanup();
        _cleanupTimer.Start();

        _uiTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _uiTimer.Tick += UpdateTrayTooltip;
        _uiTimer.Start();
    }

    // --- Start hidden in tray ---

    protected override void SetVisibleCore(bool value)
    {
        if (_suppressShow) { _suppressShow = false; base.SetVisibleCore(false); return; }
        base.SetVisibleCore(value);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        _recorder.Dispose();
        _watcher.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        base.OnFormClosing(e);
    }

    // --- CRC.exe events ---

    private void OnCrcStarted()
    {
        if (_isTrainingRecording) return; // training takes priority
        SetTrayIcon(Color.FromArgb(46, 204, 113)); // green
        _recordingStart = DateTime.Now;
        var path = _storage.GenerateFilePath(_recordingStart);
        _recorder.StartRecording(path);
        _tray.Text = "TowerTapes - Recording";
    }

    private void OnCrcStopped()
    {
        if (_isTrainingRecording) return; // training takes priority
        _recorder.StopRecording();
        SetTrayIcon(Color.FromArgb(149, 165, 166)); // gray
        _tray.Text = "TowerTapes - Idle";
        _storage.Cleanup();
        if (Visible) RefreshRecordings();
    }

    // --- Training recording ---

    private void ToggleTrainingRecording()
    {
        if (_isTrainingRecording)
            StopTrainingRecording();
        else
            StartTrainingRecording();
    }

    private void StartTrainingRecording()
    {
        if (_recorder.IsRecording)
            _recorder.StopRecording(); // stop any CRC-triggered recording first

        _isTrainingRecording = true;
        _recorder.ForceMicOpen = true;
        _recordingStart = DateTime.Now;
        var path = _storage.GenerateFilePath(_recordingStart);
        _recorder.StartRecording(path);

        SetTrayIcon(Color.FromArgb(230, 126, 34)); // orange
        _tray.Text = "TowerTapes - Training";
        _trainingMenuItem.Text = "Stop Training Recording";
    }

    private void StopTrainingRecording()
    {
        _recorder.StopRecording();
        _recorder.ForceMicOpen = false;
        _isTrainingRecording = false;

        _trainingMenuItem.Text = "Start Training Recording";
        _storage.Cleanup();
        if (Visible) RefreshRecordings();

        // If CRC is running, resume normal recording
        if (_watcher.IsCrcRunning)
            OnCrcStarted();
        else
        {
            SetTrayIcon(Color.FromArgb(149, 165, 166)); // gray
            _tray.Text = "TowerTapes - Idle";
        }
    }

    private void UpdateTrayTooltip(object? s, EventArgs e)
    {
        if (_recorder.IsRecording)
        {
            var elapsed = DateTime.Now - _recordingStart;
            var label = _isTrainingRecording ? "Training" : "Recording";
            var text = $"TowerTapes - {label} ({elapsed:hh\\:mm\\:ss})";
            if (text.Length > 63) text = text[..63]; // NotifyIcon limit
            _tray.Text = text;
        }
    }

    // --- Logo ---

    private void LoadLogo()
    {
        try
        {
            using var stream = typeof(MainForm).Assembly
                .GetManifestResourceStream("TowerTapes.logo.png");
            if (stream != null)
            {
                _logoBitmap = new Bitmap(stream);
                Icon = Icon.FromHandle(new Bitmap(_logoBitmap, 32, 32).GetHicon());
            }
        }
        catch { }
    }

    // --- Tray icon ---

    private void BuildTray()
    {
        _tray = new NotifyIcon { Visible = true };
        SetTrayIcon(Color.FromArgb(149, 165, 166)); // gray = idle
        _tray.Text = "TowerTapes - Idle";

        var menu = new ContextMenuStrip();
        _trainingMenuItem = new ToolStripMenuItem("Start Training Recording");
        _trainingMenuItem.Click += (_, _) => ToggleTrainingRecording();
        menu.Items.Add(_trainingMenuItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Show Recordings", null, (_, _) => ShowForm());
        menu.Items.Add("Settings", null, (_, _) => ShowSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open Recordings Folder", null, (_, _) => OpenFolder());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowForm();
    }

    private void SetTrayIcon(Color statusColor)
    {
        IntPtr hicon;
        using (var bmp = new Bitmap(16, 16))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using var brush = new SolidBrush(statusColor);
                g.FillEllipse(brush, 1, 1, 13, 13);
                using var pen = new Pen(Color.FromArgb(80, 0, 0, 0), 1f);
                g.DrawEllipse(pen, 1, 1, 13, 13);
            }
            hicon = bmp.GetHicon();
        }
        var oldIcon = _tray.Icon;
        _tray.Icon = Icon.FromHandle(hicon);
        oldIcon?.Dispose();
    }

    // --- Main form UI ---

    private void BuildForm()
    {
        Text = "TowerTapes";
        Size = new Size(720, 480);
        MinimumSize = new Size(500, 300);
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;

        var toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden };
        toolbar.Items.Add("Refresh", null, (_, _) => RefreshRecordings());
        toolbar.Items.Add(new ToolStripSeparator());
        toolbar.Items.Add("Open Folder", null, (_, _) => OpenFolder());
        toolbar.Items.Add(new ToolStripSeparator());
        toolbar.Items.Add("Settings", null, (_, _) => ShowSettings());

        _statusLabel = new Label
        {
            Dock = DockStyle.Bottom, Height = 24,
            TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(4, 0, 0, 0)
        };
        _storageLabel = new Label
        {
            Dock = DockStyle.Bottom, Height = 24,
            TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(4, 0, 0, 0)
        };

        var btnPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = 36,
            FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(4, 4, 0, 0)
        };
        var playBtn = new Button { Text = "Play", Width = 80 };
        var deleteBtn = new Button { Text = "Delete", Width = 80 };
        playBtn.Click += (_, _) => PlaySelected();
        deleteBtn.Click += (_, _) => DeleteSelected();
        btnPanel.Controls.AddRange([playBtn, deleteBtn]);

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToResizeRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            RowHeadersVisible = false,
            BackgroundColor = SystemColors.Window,
            BorderStyle = BorderStyle.None,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
        };
        _grid.Columns.Add("Date", "Date");
        _grid.Columns.Add("Time", "Start Time");
        _grid.Columns.Add("Duration", "Duration (est.)");
        _grid.Columns.Add(new DataGridViewColumn
        {
            Name = "Size", HeaderText = "Size",
            CellTemplate = new DataGridViewTextBoxCell(),
            FillWeight = 60
        });
        _grid.CellDoubleClick += (_, _) => PlaySelected();

        // Dock order matters: last added Fill expands
        Controls.Add(_grid);
        Controls.Add(btnPanel);
        Controls.Add(_storageLabel);
        Controls.Add(_statusLabel);
        Controls.Add(toolbar);
    }

    private void ShowForm()
    {
        RefreshRecordings();
        Show();
        if (WindowState == FormWindowState.Minimized)
            WindowState = FormWindowState.Normal;
        BringToFront();
        Activate();
    }

    private void RefreshRecordings()
    {
        _recordings = _storage.GetRecordings();
        _grid.Rows.Clear();
        foreach (var r in _recordings)
        {
            _grid.Rows.Add(
                r.StartTime.ToString("yyyy-MM-dd"),
                r.StartTime.ToString("HH:mm:ss"),
                FormatDuration(r.EstimatedDuration),
                FormatSize(r.SizeBytes));
        }

        long total = _storage.GetTotalSizeBytes();
        long max = (long)_config.MaxStorageMB * 1024 * 1024;
        _storageLabel.Text = $"Storage: {FormatSize(total)} / {FormatSize(max)}";
        _statusLabel.Text = _isTrainingRecording
            ? "Status: Training Recording" : _recorder.IsRecording
            ? "Status: Recording" : _watcher.IsCrcRunning
            ? "Status: CRC detected" : "Status: Idle — waiting for CRC.exe";
    }

    private void PlaySelected()
    {
        if (_grid.CurrentRow == null) return;
        int idx = _grid.CurrentRow.Index;
        if (idx < 0 || idx >= _recordings.Count) return;
        var path = _recordings[idx].FilePath;
        if (!File.Exists(path)) return;
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, $"Cannot play: {ex.Message}", "TowerTapes"); }
    }

    private void DeleteSelected()
    {
        if (_grid.CurrentRow == null) return;
        int idx = _grid.CurrentRow.Index;
        if (idx < 0 || idx >= _recordings.Count) return;

        var r = _recordings[idx];
        if (MessageBox.Show(this,
            $"Delete recording from {r.StartTime:g}?",
            "TowerTapes", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        try { File.Delete(r.FilePath); } catch { }
        RefreshRecordings();
    }

    private void OpenFolder()
    {
        _storage.EnsureDirectory();
        try { Process.Start(new ProcessStartInfo(_config.RecordingsPath) { UseShellExecute = true }); }
        catch { }
    }

    // --- Settings dialog ---

    private void ShowSettings()
    {
        using var dlg = new Form
        {
            Text = "TowerTapes — Settings",
            Size = new Size(420, 400),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false
        };

        int y = 16;
        const int labelX = 16, controlX = 160, w = 220;

        // --- Launch at startup ---
        AddLabel(dlg, "Startup:", labelX, y);
        var startupCheck = new CheckBox
        {
            Text = "Launch at Windows startup",
            Left = controlX, Top = y, Width = w,
            Checked = _config.LaunchAtStartup
        };
        dlg.Controls.Add(startupCheck);
        y += 32;

        // --- PTT enable ---
        AddLabel(dlg, "Push-to-Talk:", labelX, y);
        var pttCheck = new CheckBox { Text = "Enable PTT", Left = controlX, Top = y, Width = w, Checked = _config.PttEnabled };
        dlg.Controls.Add(pttCheck);
        y += 32;

        // --- PTT key detector ---
        AddLabel(dlg, "PTT Key:", labelX, y);
        string capturedKey = _config.PttKey;
        var pttKeyBtn = new Button
        {
            Left = controlX, Top = y, Width = w, Height = 28,
            Text = FormatKeyName(capturedKey),
            FlatStyle = FlatStyle.System
        };
        bool listening = false;

        pttKeyBtn.Click += (_, _) =>
        {
            listening = true;
            pttKeyBtn.Text = "[ Press any key or mouse button... ]";
            pttKeyBtn.BackColor = Color.FromArgb(255, 255, 200);
        };

        // Keyboard detection
        pttKeyBtn.KeyDown += (_, ke) =>
        {
            if (!listening) return;
            ke.Handled = true;
            ke.SuppressKeyPress = true;
            capturedKey = ke.KeyCode.ToString();
            pttKeyBtn.Text = FormatKeyName(capturedKey);
            pttKeyBtn.BackColor = SystemColors.Control;
            listening = false;
        };

        pttKeyBtn.PreviewKeyDown += (_, ke) =>
        {
            // Allow Tab, arrows, etc. to be captured instead of navigating
            if (listening) ke.IsInputKey = true;
        };

        // Mouse button detection via low-level hook during listen mode
        pttKeyBtn.MouseDown += (_, me) =>
        {
            if (!listening) return;
            if (me.Button == MouseButtons.Left) return; // left click starts listen
            string? mouseKey = me.Button switch
            {
                MouseButtons.Right => "RButton",
                MouseButtons.Middle => "MButton",
                MouseButtons.XButton1 => "XButton1",
                MouseButtons.XButton2 => "XButton2",
                _ => null
            };
            if (mouseKey != null)
            {
                capturedKey = mouseKey;
                pttKeyBtn.Text = FormatKeyName(capturedKey);
                pttKeyBtn.BackColor = SystemColors.Control;
                listening = false;
            }
        };

        // Cancel listening if focus lost
        pttKeyBtn.LostFocus += (_, _) =>
        {
            if (listening)
            {
                pttKeyBtn.Text = FormatKeyName(capturedKey);
                pttKeyBtn.BackColor = SystemColors.Control;
                listening = false;
            }
        };

        dlg.Controls.Add(pttKeyBtn);
        y += 36;

        // --- Mic device ---
        AddLabel(dlg, "Microphone:", labelX, y);
        var micCombo = new ComboBox { Left = controlX, Top = y, Width = w, DropDownStyle = ComboBoxStyle.DropDownList };
        micCombo.Items.Add("(System Default)");
        var devices = AudioRecorder.GetMicDevices();
        int selIdx = 0;
        for (int i = 0; i < devices.Count; i++)
        {
            micCombo.Items.Add(devices[i].Name);
            if (devices[i].Id == _config.MicDeviceId) selIdx = i + 1;
        }
        micCombo.SelectedIndex = selIdx;
        dlg.Controls.Add(micCombo);
        y += 32;

        // --- Bitrate ---
        AddLabel(dlg, "Opus Bitrate (kbps):", labelX, y);
        var bitrate = new NumericUpDown
        {
            Left = controlX, Top = y, Width = 80,
            Minimum = 8, Maximum = 64, Value = _config.OpusBitrateKbps
        };
        dlg.Controls.Add(bitrate);
        y += 32;

        // --- Max storage ---
        AddLabel(dlg, "Max Storage (MB):", labelX, y);
        var maxStorage = new NumericUpDown
        {
            Left = controlX, Top = y, Width = 80,
            Minimum = 100, Maximum = 10000, Value = _config.MaxStorageMB
        };
        dlg.Controls.Add(maxStorage);
        y += 32;

        // --- Retention ---
        AddLabel(dlg, "Retention (days):", labelX, y);
        var retention = new NumericUpDown
        {
            Left = controlX, Top = y, Width = 80,
            Minimum = 1, Maximum = 365, Value = _config.RetentionDays
        };
        dlg.Controls.Add(retention);
        y += 40;

        // --- Save / Cancel ---
        var saveBtn = new Button { Text = "Save", Left = controlX, Top = y, Width = 80, DialogResult = DialogResult.OK };
        var cancelBtn = new Button { Text = "Cancel", Left = controlX + 90, Top = y, Width = 80, DialogResult = DialogResult.Cancel };
        dlg.Controls.AddRange([saveBtn, cancelBtn]);
        dlg.AcceptButton = saveBtn;
        dlg.CancelButton = cancelBtn;

        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _config.LaunchAtStartup = startupCheck.Checked;
            _config.PttEnabled = pttCheck.Checked;
            _config.PttKey = capturedKey;
            _config.MicDeviceId = micCombo.SelectedIndex > 0 ? devices[micCombo.SelectedIndex - 1].Id : null;
            _config.OpusBitrateKbps = (int)bitrate.Value;
            _config.MaxStorageMB = (int)maxStorage.Value;
            _config.RetentionDays = (int)retention.Value;
            _config.Save();

            Program.SetStartupEnabled(_config.LaunchAtStartup);
        }
    }

    private static void AddLabel(Form parent, string text, int x, int y)
    {
        parent.Controls.Add(new Label
        {
            Text = text, Left = x, Top = y + 2, Width = 140,
            TextAlign = ContentAlignment.MiddleRight
        });
    }

    private static string FormatKeyName(string key) => key switch
    {
        "Oemtilde" => "~ (Tilde)",
        "CapsLock" or "Capital" => "Caps Lock",
        "LMenu" or "LeftAlt" => "Left Alt",
        "RMenu" or "RightAlt" => "Right Alt",
        "LControlKey" or "LeftControl" => "Left Ctrl",
        "RControlKey" or "RightControl" => "Right Ctrl",
        "LShiftKey" or "LeftShift" => "Left Shift",
        "RShiftKey" or "RightShift" => "Right Shift",
        "RButton" => "Mouse Right",
        "MButton" => "Mouse Middle",
        "XButton1" => "Mouse 4",
        "XButton2" => "Mouse 5",
        "Scroll" => "Scroll Lock",
        _ => key
    };

    private void ExitApp()
    {
        if (_isTrainingRecording) _isTrainingRecording = false;
        _recorder.ForceMicOpen = false;
        _recorder.StopRecording();
        _tray.Visible = false;
        Application.Exit();
    }

    // --- Helpers ---

    private static string FormatDuration(TimeSpan ts) =>
        ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss");

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes / (1024.0 * 1024.0):F1} MB"
    };
}
