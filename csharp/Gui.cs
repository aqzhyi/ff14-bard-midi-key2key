// Gui.cs - FF14 key2key, window (WinForms) version.
// Built together with Key2Key.cs, which holds the shared core (Keymap, Engine, MidiIn, Ff14PostSink, Config ...):
//   csc /target:winexe /main:Ff14Key2Key.GuiProgram Key2Key.cs Gui.cs      (see build.bat)
//
// Threading: MIDI callbacks arrive on a winmm thread. They only touch the Engine (which has its own lock) and
// thread-safe queues; every UI update happens on the UI thread in the 50 ms timer tick. The key path
// (MIDI callback -> Engine -> PostMessage) never waits for the UI, so the window adds no latency.
//
// Same rules as Key2Key.cs: C# 5 only (no $"..." , no ?. , no expression-bodied members, no nameof,
// no "out var", no pattern matching, no tuples, no auto-property initializers, no local functions).
// Save as UTF-8 WITH BOM.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using Timer = System.Windows.Forms.Timer;

namespace Ff14Key2Key
{
    // ======================================================================
    // Entry point of ff14-key2key-gui.exe.
    // ======================================================================
    internal static class GuiProgram
    {
        private static MainForm mainForm;

        [STAThread]
        private static int Main(string[] args)
        {
            // Every way out must free the keys first (a key left down keeps sounding in the game).
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += OnThreadException;
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            string tableError = Keymap.Validate();
            if (tableError != null)
            {
                MessageBox.Show("內部錯誤：鍵位表不正確（" + tableError + "）。", "FF14 key2key",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }

            mainForm = new MainForm();
            Application.Run(mainForm);
            MainForm.EmergencyRelease();
            return 0;
        }

        // An exception on the UI thread: free the keys, stop the current mode, tell the user, keep running.
        private static void OnThreadException(object sender, ThreadExceptionEventArgs e)
        {
            MainForm.EmergencyRelease();
            MainForm f = mainForm;
            if (f != null && !f.IsDisposed)
            {
                f.RecoverFromError(e.Exception);
            }
            else
            {
                MessageBox.Show("發生未預期的錯誤：" + e.Exception.Message, "FF14 key2key",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // An exception on another thread ends the process: free the keys while we still can.
        private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            MainForm.EmergencyRelease();
        }

        private static void OnProcessExit(object sender, EventArgs e)
        {
            MainForm.EmergencyRelease();
        }
    }

    // ======================================================================
    // Main window.
    // ======================================================================
    internal sealed class MainForm : Form
    {
        private enum Mode
        {
            Idle,
            Mapping,
            Calibrating,
            Testing,
            Monitoring
        }

        private const string AppTitle = "FF14 key2key";

        private static readonly Color ColorIdle = Color.FromArgb(128, 128, 128);
        private static readonly Color ColorRun = Color.FromArgb(34, 139, 34);
        private static readonly Color ColorPause = Color.FromArgb(214, 140, 0);
        private static readonly Color ColorBusy = Color.FromArgb(40, 100, 200);

        private readonly Ff14PostSink sink = new Ff14PostSink();     // one sink for the whole session (mapping, test, link lamp)
        private readonly LogQueue pending = new LogQueue();          // lines from other threads, shown by the timer
        private readonly Timer timer = new Timer();
        private readonly float scale;

        // The engine that can press keys right now (mapping or test), for the emergency exit paths.
        private static volatile Engine liveEngine;

        private Mode mode = Mode.Idle;
        private Engine engine;          // mapping
        private MidiIn midi;            // mapping
        private MidiIn monitorMidi;     // monitor
        private Thread testThread;      // test
        private Engine testEngine;      // test
        private ManualResetEvent testStop;
        private volatile bool testDone;
        private volatile string testResult;
        private bool linked;
        private bool filling;
        private bool exiting;
        private bool shutDown;
        private bool balloonShown;

        private NotifyIcon tray;
        private ToolStripMenuItem trayShowItem;
        private ToolStripMenuItem trayPauseItem;
        private ToolStripMenuItem trayExitItem;
        private Icon iconApp;
        private Icon iconIdle;
        private Icon iconRun;
        private Icon iconPause;
        private Icon iconBusy;
        private List<MidiDeviceInfo> devices = new List<MidiDeviceInfo>();

        private ComboBox deviceBox;
        private Button refreshButton;
        private Label linkDot;
        private Label linkText;
        private Label configText;
        private Button runButton;
        private Button pauseButton;
        private Button calibrateButton;
        private Button testButton;
        private Button monitorButton;
        private Button keymapButton;
        private TextBox logBox;
        private Button clearButton;
        private Label statusText;
        private Button backgroundButton;
        private Button exitButton;

        public MainForm()
        {
            Text = AppTitle;
            Font = new Font("Microsoft JhengHei UI", 9.75f);
            AutoScaleMode = AutoScaleMode.None;
            using (Graphics g = CreateGraphics())
            {
                scale = g.DpiX / 96f;
            }
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(S(600), S(520));
            ClientSize = new Size(S(720), S(600));

            BuildLayout();
            BuildTray();

            timer.Interval = 50;
            timer.Tick += OnTick;
            Load += OnFormLoad;
            FormClosing += OnFormClosing;
            SystemEvents.SessionEnding += OnSessionEnding;
        }

        // ------------------------------------------------------------------
        // Exit paths
        // ------------------------------------------------------------------

        // Last-resort key release, callable from any thread (unhandled exception, process exit, logoff).
        public static void EmergencyRelease()
        {
            Engine e = liveEngine;
            if (e == null) return;
            try
            {
                e.Shutdown();
            }
            catch (Exception)
            {
            }
        }

        private void OnSessionEnding(object sender, SessionEndingEventArgs e)
        {
            EmergencyRelease();
        }

        private void OnExitClick(object sender, EventArgs e)
        {
            ExitApp();
        }

        // The exit button and the tray menu's exit: release keys, close the device, remove the tray icon, quit.
        private void ExitApp()
        {
            if (exiting) return;
            exiting = true;
            ShutdownAll();
            Application.Exit();
        }

        // The window's close button (X) also ends the program.
        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            exiting = true;
            ShutdownAll();
        }

        // Idempotent.
        private void ShutdownAll()
        {
            if (shutDown) return;
            shutDown = true;
            timer.Stop();
            SystemEvents.SessionEnding -= OnSessionEnding;   // static event: must be removed or it keeps the form alive
            StopEverything();
            if (tray != null)
            {
                tray.Visible = false;   // otherwise the icon stays in the notification area until the mouse passes over it
                tray.Dispose();
                tray = null;
            }
        }

        // Ends whatever mode is running (keys released, device closed) and returns to Idle.
        private void StopEverything()
        {
            StopMapping(null);
            AbortTest();
            StopMonitor(null);
        }

        public void RecoverFromError(Exception ex)
        {
            try
            {
                StopEverything();
            }
            catch (Exception)
            {
                mode = Mode.Idle;
            }
            if (exiting) return;
            AddLog("發生未預期的錯誤：" + ex.Message + "（已停止目前的動作，所有按鍵都已放開）");
            MessageBox.Show(this, "發生未預期的錯誤：" + ex.Message + "\r\n\r\n已停止目前的動作，所有按鍵都已放開。",
                AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
            UpdateUi();
        }

        // ------------------------------------------------------------------
        // Layout
        // ------------------------------------------------------------------
        private int S(int pixels)
        {
            return (int)Math.Round(pixels * scale);
        }

        private void BuildLayout()
        {
            SuspendLayout();

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            root.Padding = new Padding(S(10));

            // Device / link / settings
            TableLayoutPanel info = new TableLayoutPanel();
            info.Dock = DockStyle.Fill;
            info.AutoSize = true;
            info.ColumnCount = 3;
            info.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            info.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            info.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            deviceBox = new ComboBox();
            deviceBox.DropDownStyle = ComboBoxStyle.DropDownList;
            deviceBox.Dock = DockStyle.Fill;
            deviceBox.Margin = new Padding(S(3), S(6), S(3), S(3));
            deviceBox.SelectedIndexChanged += OnDeviceChanged;
            refreshButton = MakeButton("重新整理", OnRefreshClick);
            refreshButton.MinimumSize = new Size(S(90), S(30));

            linkDot = new Label();
            linkDot.AutoSize = true;
            linkDot.Text = "●";
            linkDot.Margin = new Padding(S(3), S(3), 0, S(3));
            linkText = new Label();
            linkText.AutoSize = true;
            linkText.Margin = new Padding(0, S(3), S(3), S(3));
            FlowLayoutPanel linkPanel = new FlowLayoutPanel();
            linkPanel.AutoSize = true;
            linkPanel.WrapContents = false;
            linkPanel.Margin = new Padding(0);
            linkPanel.Controls.Add(linkDot);
            linkPanel.Controls.Add(linkText);

            configText = new Label();
            configText.AutoSize = true;
            configText.Margin = new Padding(S(3));

            info.Controls.Add(MakeCaption("MIDI 裝置："), 0, 0);
            info.Controls.Add(deviceBox, 1, 0);
            info.Controls.Add(refreshButton, 2, 0);
            info.Controls.Add(MakeCaption("FF14 視窗："), 0, 1);
            info.Controls.Add(linkPanel, 1, 1);
            info.Controls.Add(MakeCaption("目前設定："), 0, 2);
            info.Controls.Add(configText, 1, 2);
            info.SetColumnSpan(configText, 2);
            AddRow(root, info, SizeType.AutoSize);

            // Main actions
            runButton = MakeButton("▶ 開始映射", OnRunClick);
            runButton.MinimumSize = new Size(S(150), S(40));
            pauseButton = MakeButton("Ⅱ 暫停擷取", OnPauseClick);
            pauseButton.MinimumSize = new Size(S(150), S(40));
            AddRow(root, MakeFlow(runButton, pauseButton), SizeType.AutoSize);

            // Other tools
            calibrateButton = MakeButton("校準最低音", OnCalibrateClick);
            testButton = MakeButton("測試音階", OnTestClick);
            monitorButton = MakeButton("監看 MIDI", OnMonitorClick);
            keymapButton = MakeButton("鍵位對照表", OnKeymapClick);
            AddRow(root, MakeFlow(calibrateButton, testButton, monitorButton, keymapButton), SizeType.AutoSize);

            // Log
            TableLayoutPanel logHeader = new TableLayoutPanel();
            logHeader.Dock = DockStyle.Fill;
            logHeader.AutoSize = true;
            logHeader.ColumnCount = 2;
            logHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            logHeader.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            Label logCaption = MakeCaption("訊息記錄");
            logCaption.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            clearButton = MakeButton("清除", OnClearClick);
            clearButton.MinimumSize = new Size(S(70), S(28));
            logHeader.Controls.Add(logCaption, 0, 0);
            logHeader.Controls.Add(clearButton, 1, 0);
            AddRow(root, logHeader, SizeType.AutoSize);

            logBox = new TextBox();
            logBox.Multiline = true;
            logBox.ReadOnly = true;
            logBox.ScrollBars = ScrollBars.Vertical;
            logBox.BackColor = SystemColors.Window;
            logBox.Dock = DockStyle.Fill;
            logBox.Margin = new Padding(S(3));
            AddRow(root, logBox, SizeType.Percent);

            // Status / background / exit
            TableLayoutPanel bottom = new TableLayoutPanel();
            bottom.Dock = DockStyle.Fill;
            bottom.AutoSize = true;
            bottom.ColumnCount = 3;
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            statusText = new Label();
            statusText.AutoSize = true;
            statusText.Anchor = AnchorStyles.Left;
            statusText.Font = new Font(Font, FontStyle.Bold);
            backgroundButton = MakeButton("背景執行", OnBackgroundClick);
            exitButton = MakeButton("離開", OnExitClick);
            bottom.Controls.Add(statusText, 0, 0);
            bottom.Controls.Add(backgroundButton, 1, 0);
            bottom.Controls.Add(exitButton, 2, 0);
            AddRow(root, bottom, SizeType.AutoSize);

            Controls.Add(root);
            ResumeLayout(true);
        }

        private static void AddRow(TableLayoutPanel panel, Control control, SizeType sizeType)
        {
            panel.RowCount = panel.RowCount + 1;
            panel.RowStyles.Add(sizeType == SizeType.Percent ? new RowStyle(SizeType.Percent, 100f) : new RowStyle(sizeType));
            panel.Controls.Add(control, 0, panel.RowCount - 1);
        }

        private Button MakeButton(string text, EventHandler onClick)
        {
            Button b = new Button();
            b.Text = text;
            b.AutoSize = true;
            b.MinimumSize = new Size(S(110), S(34));
            b.Padding = new Padding(S(6), S(2), S(6), S(2));
            b.Margin = new Padding(S(3));
            b.UseVisualStyleBackColor = true;
            b.Click += onClick;
            return b;
        }

        private Label MakeCaption(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.Anchor = AnchorStyles.Left;
            l.Margin = new Padding(S(3));
            return l;
        }

        private FlowLayoutPanel MakeFlow(params Control[] controls)
        {
            FlowLayoutPanel p = new FlowLayoutPanel();
            p.Dock = DockStyle.Fill;
            p.AutoSize = true;
            p.WrapContents = true;
            p.Margin = new Padding(0, S(4), 0, 0);
            p.Controls.AddRange(controls);
            return p;
        }

        // ------------------------------------------------------------------
        // Start-up / timer
        // ------------------------------------------------------------------
        private void OnFormLoad(object sender, EventArgs e)
        {
            AddLog("程式已啟動。");
            RefreshDevices();
            RefreshConfigText();
            SetLinked(sink.Poll(), true);
            UpdateUi();
            timer.Start();
        }

        private void OnTick(object sender, EventArgs e)
        {
            DrainPending();
            bool nowLinked = sink.Poll();   // cheap while the window is known; searches at most every 500 ms otherwise
            if (nowLinked != linked) SetLinked(nowLinked, false);
            if (mode == Mode.Mapping) TickMapping();
            if (mode == Mode.Testing && testDone) FinishTest();
            if (mode == Mode.Monitoring) TickMonitor();
        }

        private void DrainPending()
        {
            for (int i = 0; i < 200; i++)
            {
                string line = pending.TryTake();
                if (line == null) break;
                AddLog(line);
            }
        }

        // ------------------------------------------------------------------
        // FF14 link lamp
        // ------------------------------------------------------------------
        private void SetLinked(bool value, bool initial)
        {
            linked = value;
            linkDot.ForeColor = value ? ColorRun : ColorIdle;
            linkText.Text = value ? "已連線" : "找不到遊戲視窗（開啟遊戲後會自動接上）";
            if (initial) return;
            if (value)
            {
                AddLog("已連上 FF14 視窗。");
            }
            else
            {
                AddLog(mode == Mode.Mapping ? "FF14 視窗不見了，按鍵暫時被忽略。" : "FF14 視窗不見了。");
            }
        }

        // Diagnostics only: windows whose title looks like FF14 but do not match the detection rules.
        private void LogSimilarWindows()
        {
            List<WindowInfo> visible = Ff14Window.VisibleWindows();
            int shown = 0;
            for (int i = 0; i < visible.Count && shown < 10; i++)
            {
                string t = visible[i].Title;
                bool similar = t.IndexOf("FINAL FANTASY", StringComparison.OrdinalIgnoreCase) >= 0
                    || t.IndexOf("最終幻想", StringComparison.Ordinal) >= 0
                    || t.IndexOf("最终幻想", StringComparison.Ordinal) >= 0;
                if (!similar) continue;
                if (shown == 0) AddLog("以下視窗的標題看起來像 FF14，但不符合偵測條件：");
                AddLog(string.Format("  標題「{0}」  類別 {1}  PID {2}  程式 {3}",
                    t, visible[i].ClassName, visible[i].Pid, visible[i].ProcessName));
                shown++;
            }
        }

        // ------------------------------------------------------------------
        // MIDI devices / config
        // ------------------------------------------------------------------
        private static string ConfigPath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.txt"); }
        }

        private bool HasDevices
        {
            get { return devices.Count > 0; }
        }

        private MidiDeviceInfo SelectedDevice()
        {
            int i = deviceBox.SelectedIndex;
            if (!HasDevices || i < 0 || i >= devices.Count) return null;
            return devices[i];
        }

        private void RefreshDevices()
        {
            MidiDeviceInfo old = SelectedDevice();
            string keep = (old == null) ? null : old.Name;
            try
            {
                devices = MidiIn.ListDevices();
            }
            catch (Exception ex)
            {
                devices = new List<MidiDeviceInfo>();
                AddLog("無法列出 MIDI 裝置：" + ex.Message);
            }

            filling = true;
            try
            {
                deviceBox.BeginUpdate();
                deviceBox.Items.Clear();
                if (!HasDevices)
                {
                    deviceBox.Items.Add("（找不到 MIDI 裝置）");
                    deviceBox.SelectedIndex = 0;
                }
                else
                {
                    for (int i = 0; i < devices.Count; i++)
                    {
                        deviceBox.Items.Add(devices[i].Name);
                    }
                    deviceBox.SelectedIndex = PickDevice(keep);
                }
                deviceBox.EndUpdate();
            }
            finally
            {
                filling = false;
            }

            if (HasDevices)
            {
                AddLog("找到 " + devices.Count.ToString() + " 個 MIDI 裝置，使用：" + devices[deviceBox.SelectedIndex].Name);
            }
            else
            {
                AddLog("找不到任何 MIDI 輸入裝置。請確認 USB 線有接好（可以換一個 USB 孔），" +
                    "並關閉其他正在使用 MIDI 鍵盤的程式（例如 DAW），再按「重新整理」。");
            }
            UpdateUi();
        }

        // Same order as the console version: the device in use, device= from config.txt, the only device,
        // the only "x3" / "midiplus" device, else the first one.
        private int PickDevice(string keep)
        {
            if (!string.IsNullOrEmpty(keep))
            {
                for (int i = 0; i < devices.Count; i++)
                {
                    if (string.Equals(devices[i].Name, keep, StringComparison.Ordinal)) return i;
                }
            }
            Config cfg = Config.Load(ConfigPath);
            if (!string.IsNullOrEmpty(cfg.Device))
            {
                string spec = cfg.Device.Trim();
                for (int i = 0; i < devices.Count; i++)
                {
                    if (spec.Length > 0 && devices[i].Name.IndexOf(spec, StringComparison.OrdinalIgnoreCase) >= 0) return i;
                }
            }
            if (devices.Count == 1) return 0;
            int found = -1;
            int matches = 0;
            for (int i = 0; i < devices.Count; i++)
            {
                string lower = devices[i].Name.ToLowerInvariant();
                if (lower.IndexOf("x3", StringComparison.Ordinal) >= 0 || lower.IndexOf("midiplus", StringComparison.Ordinal) >= 0)
                {
                    found = i;
                    matches++;
                }
            }
            return (matches == 1) ? found : 0;
        }

        private void OnDeviceChanged(object sender, EventArgs e)
        {
            if (filling) return;
            MidiDeviceInfo dev = SelectedDevice();
            if (dev == null) return;
            AddLog("改用 MIDI 裝置：" + dev.Name);
            // Remember the choice. Only when config.txt already exists: a missing file means "not calibrated yet",
            // and calibration writes the device name together with the base note.
            if (!File.Exists(ConfigPath)) return;
            try
            {
                Config cfg = Config.Load(ConfigPath);
                cfg.Device = dev.Name;
                cfg.Save(ConfigPath);
            }
            catch (Exception ex)
            {
                AddLog("無法寫入設定檔：" + ex.Message);
            }
        }

        private void OnRefreshClick(object sender, EventArgs e)
        {
            if (mode != Mode.Idle) return;
            RefreshDevices();
        }

        private void RefreshConfigText()
        {
            if (!File.Exists(ConfigPath))
            {
                configText.Text = "尚未校準（第一次按「開始映射」會先帶你校準最低音）";
                return;
            }
            Config cfg = Config.Load(ConfigPath);
            configText.Text = string.Format("最低音 MIDI {0} ({1})　頻道：{2}　有效音域 {1} ~ {3}",
                cfg.BaseNote, Keymap.NoteName(cfg.BaseNote), ChannelText(cfg.Channel), Keymap.NoteName(cfg.BaseNote + 36));
        }

        private static string ChannelText(int channel)
        {
            return (channel == 0) ? "全部" : channel.ToString();
        }

        // ------------------------------------------------------------------
        // Mapping (run)
        // ------------------------------------------------------------------
        private void OnRunClick(object sender, EventArgs e)
        {
            if (mode == Mode.Mapping)
            {
                StopMapping("已停止映射，所有按鍵都已放開。");
            }
            else if (mode == Mode.Idle)
            {
                StartMapping();
            }
        }

        private void StartMapping()
        {
            MidiDeviceInfo dev = SelectedDevice();
            if (dev == null)
            {
                ShowWarning("找不到 MIDI 裝置。請接好 MIDI 鍵盤後按「重新整理」。");
                return;
            }
            if (!File.Exists(ConfigPath))
            {
                AddLog("第一次使用：還沒有 config.txt，先來校準最低音。");
                if (!RunCalibration(dev)) return;
            }

            Config cfg = Config.Load(ConfigPath);
            int baseNote = cfg.BaseNote;
            int channel = cfg.Channel;
            Engine eng = new Engine(sink, baseNote, channel);
            // Runs on the MIDI thread: only the engine here, never the UI.
            MidiShortHandler handler = delegate(uint raw)
            {
                eng.Handle(MidiMessage.Parse(raw));
            };
            MidiIn m;
            try
            {
                m = MidiIn.Open(dev.Index, dev.Name, handler);
            }
            catch (MidiException ex)
            {
                eng.Shutdown();
                AddLog(ex.Message);
                ShowWarning(ex.Message);
                return;
            }

            engine = eng;
            midi = m;
            liveEngine = eng;
            mode = Mode.Mapping;
            AddLog("開始映射：" + dev.Name);
            AddLog(string.Format("  最低音 MIDI {0} ({1})，頻道：{2}，有效音域 MIDI {0} ({1}) ~ {3} ({4})",
                baseNote, Keymap.NoteName(baseNote), ChannelText(channel), baseNote + 36, Keymap.NoteName(baseNote + 36)));
            AddLog("  按鍵會直接送到 FF14 視窗，不管目前是哪個視窗在前景。請讓 FF14 進入演奏模式（視窗不要最小化）。");
            if (!linked)
            {
                AddLog("  目前找不到 FF14 視窗，開啟遊戲後會自動接上。");
                LogSimilarWindows();
            }
            UpdateUi();
        }

        // Releases every key, closes the device, back to Idle. message == null: log nothing.
        private void StopMapping(string message)
        {
            Engine e = engine;
            MidiIn m = midi;
            engine = null;
            midi = null;
            if (liveEngine == e) liveEngine = null;
            if (e != null) e.Shutdown();   // free every key first
            if (m != null) m.Close();
            if (mode == Mode.Mapping) mode = Mode.Idle;
            if (message != null) AddLog(message);
            UpdateUi();
        }

        private void TickMapping()
        {
            int[] hints = engine.DrainHints();
            for (int i = 0; i < hints.Length; i++)
            {
                AddLog(Program.HintText(hints[i], engine.BaseNote));
            }
            string err = midi.TakeCallbackError();
            if (err != null) AddLog("警告：處理 MIDI 訊息時發生錯誤：" + err);
            if (midi.CheckLost())
            {
                StopMapping("MIDI 裝置中斷了（可能被拔掉或被其他程式搶走）。所有按鍵都已放開。");
                ShowWarning("MIDI 裝置中斷了（可能被拔掉或被其他程式搶走）。所有按鍵都已放開。\r\n\r\n" +
                    "請重新插好鍵盤，按「重新整理」後再開始映射。");
            }
        }

        private void ShowWarning(string text)
        {
            if (exiting) return;
            RestoreWindow();   // a message box must not pop up for a window hidden in the tray
            MessageBox.Show(this, text, AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        // ------------------------------------------------------------------
        // Background (system tray)
        // ------------------------------------------------------------------
        private void BuildTray()
        {
            iconApp = MakeIcon(ColorBusy, 32);
            int small = Math.Max(16, SystemInformation.SmallIconSize.Width);
            iconIdle = MakeIcon(ColorIdle, small);
            iconRun = MakeIcon(ColorRun, small);
            iconPause = MakeIcon(ColorPause, small);
            iconBusy = MakeIcon(ColorBusy, small);
            Icon = iconApp;

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Font = Font;
            trayShowItem = new ToolStripMenuItem("顯示視窗", null, OnTrayShowClick);
            trayShowItem.Font = new Font(Font, FontStyle.Bold);
            trayPauseItem = new ToolStripMenuItem("暫停擷取", null, OnTrayPauseClick);
            trayExitItem = new ToolStripMenuItem("離開", null, OnTrayExitClick);
            menu.Items.Add(trayShowItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(trayPauseItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(trayExitItem);

            tray = new NotifyIcon();
            tray.Icon = iconIdle;
            tray.Text = AppTitle;
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += OnTrayShowClick;
            tray.Visible = true;
        }

        // A small round status icon with a three-key piano on it, drawn at run time (no icon file needed).
        // The icon handle is kept for the whole session (only five of them), so it is never destroyed.
        private static Icon MakeIcon(Color color, int size)
        {
            using (Bitmap bmp = new Bitmap(size, size))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (SolidBrush fill = new SolidBrush(color))
                    {
                        g.FillEllipse(fill, 0, 0, size - 1, size - 1);
                    }
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
                    float left = size * 0.22f;
                    float top = size * 0.26f;
                    float keyW = (size - 2 * left) / 3f;
                    float keyH = size * 0.48f;
                    for (int i = 0; i < 3; i++)
                    {
                        g.FillRectangle(Brushes.White, left + i * keyW + 0.5f, top, keyW - 1f, keyH);
                    }
                    float blackW = keyW * 0.55f;
                    for (int i = 1; i < 3; i++)
                    {
                        g.FillRectangle(Brushes.Black, left + i * keyW - blackW / 2f, top, blackW, keyH * 0.6f);
                    }
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }

        private void OnBackgroundClick(object sender, EventArgs e)
        {
            GoBackground();
        }

        // Hide the window (no taskbar button); mapping and pause state carry on unchanged.
        private void GoBackground()
        {
            Hide();
            AddLog("已縮到系統匣，程式在背景繼續執行。雙擊右下角的圖示可以叫回視窗。");
            if (!balloonShown && tray != null)
            {
                balloonShown = true;
                tray.ShowBalloonTip(4000, AppTitle, "程式仍在背景執行。雙擊右下角的圖示可以叫回視窗。", ToolTipIcon.Info);
            }
        }

        private void RestoreWindow()
        {
            if (IsDisposed) return;
            if (!Visible) Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }

        private void OnTrayShowClick(object sender, EventArgs e)
        {
            RestoreWindow();
        }

        private void OnTrayPauseClick(object sender, EventArgs e)
        {
            TogglePause();
        }

        private void OnTrayExitClick(object sender, EventArgs e)
        {
            ExitApp();
        }

        // ------------------------------------------------------------------
        // Buttons (filled in feature by feature)
        // ------------------------------------------------------------------

        private void OnPauseClick(object sender, EventArgs e)
        {
            TogglePause();
        }

        // ------------------------------------------------------------------
        // Pause / resume (mapping only). The device stays open, so resuming is instant.
        // ------------------------------------------------------------------
        private void TogglePause()
        {
            if (mode != Mode.Mapping || engine == null) return;
            bool pause = !engine.IsPaused;
            engine.SetPaused(pause);
            AddLog(pause
                ? "已暫停擷取，所有按鍵都已放開。MIDI 訊號不會送到遊戲（再按一次「繼續擷取」恢復）。"
                : "已繼續擷取。");
            UpdateUi();
        }

        // ------------------------------------------------------------------
        // Calibration
        // ------------------------------------------------------------------
        private void OnCalibrateClick(object sender, EventArgs e)
        {
            if (mode != Mode.Idle) return;
            MidiDeviceInfo dev = SelectedDevice();
            if (dev == null)
            {
                ShowWarning("找不到 MIDI 裝置。請接好 MIDI 鍵盤後按「重新整理」。");
                return;
            }
            RunCalibration(dev);
        }

        // Modal dialog: lowest key, then highest key; saves base_note (and the device name) to config.txt.
        // True when a new base note was saved.
        private bool RunCalibration(MidiDeviceInfo dev)
        {
            Config cfg = Config.Load(ConfigPath);
            mode = Mode.Calibrating;
            UpdateUi();
            AddLog("校準最低音：" + dev.Name);
            int low;
            string error;
            DialogResult result;
            try
            {
                using (CalibrationForm f = new CalibrationForm(dev, cfg.Channel, Font, scale, Icon))
                {
                    result = f.ShowDialog(this);
                    low = f.LowNote;
                    error = f.Error;
                }
            }
            finally
            {
                mode = Mode.Idle;
                if (!exiting) UpdateUi();
            }
            if (exiting) return false;

            if (error != null)
            {
                AddLog(error);
                ShowWarning(error);
                return false;
            }
            if (result != DialogResult.OK || low < 0)
            {
                AddLog("已取消校準。");
                return false;
            }

            cfg.BaseNote = low;
            cfg.Device = dev.Name;
            try
            {
                cfg.Save(ConfigPath);
            }
            catch (Exception ex)
            {
                string msg = "無法寫入設定檔：" + ex.Message + "\r\n請把整個資料夾放到可以寫入的位置（例如桌面）再試一次。";
                AddLog(msg);
                ShowWarning(msg);
                return false;
            }
            AddLog(string.Format("校準完成，已儲存到 config.txt：最低音 = MIDI {0} ({1})，FF14 的 37 個音對應 MIDI {0} ({1}) ~ {2} ({3})。",
                low, Keymap.NoteName(low), low + 36, Keymap.NoteName(low + 36)));
            AddLog("提醒：鍵盤上的 Octave / Transpose 請保持在原本的設定，校準後不要再改。");
            RefreshConfigText();
            return true;
        }

        // ------------------------------------------------------------------
        // Test scale: presses the 37 keys in turn (no MIDI keyboard needed), on a worker thread.
        // ------------------------------------------------------------------
        private const int TestDelayMs = 250;

        private void OnTestClick(object sender, EventArgs e)
        {
            if (mode == Mode.Testing)
            {
                if (testStop != null) testStop.Set();   // the worker releases its key and ends; the timer finishes up
            }
            else if (mode == Mode.Idle)
            {
                StartTest();
            }
        }

        private void StartTest()
        {
            if (!sink.Poll())
            {
                AddLog("找不到 FF14 視窗，無法測試音階。");
                LogSimilarWindows();
                ShowWarning("找不到 FF14 視窗。\r\n請先開啟遊戲，裝備樂器並進入演奏模式，再按「測試音階」。");
                return;
            }
            Engine eng = new Engine(sink, Keymap.DefaultBase, 0);
            ManualResetEvent stop = new ManualResetEvent(false);
            testEngine = eng;
            testStop = stop;
            testDone = false;
            testResult = null;
            liveEngine = eng;
            mode = Mode.Testing;
            AddLog("測試音階：程式會依序按下 37 個音的電腦按鍵（不需要 MIDI 鍵盤）。");
            AddLog("  按鍵會直接送到 FF14 視窗；請先讓 FF14 裝備樂器並進入演奏模式。按「停止測試」可隨時停止。");
            testThread = new Thread(delegate() { RunTest(eng, stop); });
            testThread.IsBackground = true;
            testThread.Name = "test scale";
            testThread.Start();
            UpdateUi();
        }

        // Worker thread: never touches the UI (log lines go through the pending queue).
        private void RunTest(Engine eng, ManualResetEvent stop)
        {
            string result = "cancel";
            try
            {
                int before = sink.Undelivered;
                for (int s = 3; s >= 1; s--)
                {
                    pending.Add("  " + s.ToString() + " ...");
                    if (stop.WaitOne(1000)) return;
                }
                int hold = TestDelayMs * 6 / 10;
                int rest = TestDelayMs - hold;
                for (int off = 0; off < Keymap.KeyCount; off++)
                {
                    pending.Add(string.Format("  偏移 {0,2}  {1,-3}  按鍵 {2}", off, Keymap.NoteName(Keymap.DefaultBase + off), Keymap.Keys[off]));
                    eng.PressOffset(off);
                    bool cancelled = stop.WaitOne(hold);
                    eng.ReleaseOffset(off);
                    if (cancelled || stop.WaitOne(rest)) return;
                }
                pending.Add(string.Format("  C 大調和弦（按鍵 {0} {1} {2} 同時按）", Keymap.Keys[12], Keymap.Keys[16], Keymap.Keys[19]));
                eng.PressOffset(12);
                eng.PressOffset(16);
                eng.PressOffset(19);
                bool stopChord = stop.WaitOne(800);
                eng.ReleaseAll();
                if (stopChord) return;
                result = (sink.Undelivered > before) ? "undelivered" : "ok";
            }
            catch (Exception ex)
            {
                result = "error:" + ex.Message;
            }
            finally
            {
                eng.Shutdown();
                testResult = result;
                testDone = true;
            }
        }

        // UI thread, once the worker has ended.
        private void FinishTest()
        {
            Thread t = testThread;
            if (t != null) t.Join(1000);
            CleanupTest();
            string r = testResult ?? "cancel";
            if (r == "ok")
            {
                AddLog("測試完成，所有按鍵都已放開。音階由低到高、沒有跳音，就代表遊戲鍵位和程式一致。");
            }
            else if (r == "undelivered")
            {
                AddLog("警告：測試途中有按鍵沒有送出去（FF14 視窗不見了，或遊戲沒有收下訊息）。");
            }
            else if (r.StartsWith("error:", StringComparison.Ordinal))
            {
                AddLog("測試時發生錯誤：" + r.Substring(6) + "（所有按鍵都已放開）");
            }
            else
            {
                AddLog("已停止測試，所有按鍵都已放開。");
            }
            UpdateUi();
        }

        // Exit / error path: stop the worker now and make sure no key stays down.
        private void AbortTest()
        {
            if (testEngine == null) return;
            if (testStop != null) testStop.Set();
            testEngine.Shutdown();
            Thread t = testThread;
            if (t != null) t.Join(1000);
            CleanupTest();
        }

        private void CleanupTest()
        {
            if (testEngine != null && liveEngine == testEngine) liveEngine = null;
            testEngine = null;
            testThread = null;
            if (testStop != null)
            {
                testStop.Close();
                testStop = null;
            }
            testDone = false;
            if (mode == Mode.Testing) mode = Mode.Idle;
        }

        // ------------------------------------------------------------------
        // Monitor: show the incoming notes only, never send a key.
        // ------------------------------------------------------------------
        private void OnMonitorClick(object sender, EventArgs e)
        {
            if (mode == Mode.Monitoring)
            {
                StopMonitor("已結束監看。");
            }
            else if (mode == Mode.Idle)
            {
                StartMonitor();
            }
        }

        private void StartMonitor()
        {
            MidiDeviceInfo dev = SelectedDevice();
            if (dev == null)
            {
                ShowWarning("找不到 MIDI 裝置。請接好 MIDI 鍵盤後按「重新整理」。");
                return;
            }
            Config cfg = Config.Load(ConfigPath);
            int baseNote = cfg.BaseNote;
            int channel = cfg.Channel;
            LogQueue log = pending;
            // MIDI thread: only the thread-safe log queue.
            MidiShortHandler handler = delegate(uint raw)
            {
                string line = Program.Describe(MidiMessage.Parse(raw), baseNote, channel);
                if (line != null) log.Add("  " + line);
            };
            try
            {
                monitorMidi = MidiIn.Open(dev.Index, dev.Name, handler);
            }
            catch (MidiException ex)
            {
                AddLog(ex.Message);
                ShowWarning(ex.Message);
                return;
            }
            mode = Mode.Monitoring;
            AddLog("監看模式（不會送出任何按鍵）：" + dev.Name);
            AddLog(string.Format("  最低音：MIDI {0} ({1})，頻道：{2}。請按幾個琴鍵看看。",
                baseNote, Keymap.NoteName(baseNote), ChannelText(channel)));
            UpdateUi();
        }

        private void StopMonitor(string message)
        {
            MidiIn m = monitorMidi;
            if (m == null) return;
            monitorMidi = null;
            m.Close();
            DrainPending();   // the last notes before the message
            if (mode == Mode.Monitoring) mode = Mode.Idle;
            if (message != null) AddLog(message);
            UpdateUi();
        }

        private void TickMonitor()
        {
            string err = monitorMidi.TakeCallbackError();
            if (err != null) AddLog("警告：處理 MIDI 訊息時發生錯誤：" + err);
            if (monitorMidi.CheckLost())
            {
                StopMonitor("MIDI 裝置中斷了（可能被拔掉或被其他程式搶走），已結束監看。");
                ShowWarning("MIDI 裝置中斷了（可能被拔掉或被其他程式搶走）。\r\n\r\n請重新插好鍵盤，按「重新整理」後再試一次。");
            }
        }

        // ------------------------------------------------------------------
        // Keymap table (non-modal, one at a time)
        // ------------------------------------------------------------------
        private Form keymapForm;

        private void OnKeymapClick(object sender, EventArgs e)
        {
            if (keymapForm != null && !keymapForm.IsDisposed)
            {
                keymapForm.Close();   // re-open with the current base note
            }
            Config cfg = Config.Load(ConfigPath);
            keymapForm = BuildKeymapForm(cfg.BaseNote);
            keymapForm.Show(this);
        }

        private Form BuildKeymapForm(int baseNote)
        {
            Form f = new Form();
            f.Text = string.Format("鍵位對照表（最低音 = MIDI {0} {1}）", baseNote, Keymap.NoteName(baseNote));
            f.Font = Font;
            f.Icon = Icon;
            f.AutoScaleMode = AutoScaleMode.None;
            f.ShowInTaskbar = false;
            f.MinimizeBox = false;
            f.StartPosition = FormStartPosition.CenterParent;
            f.ClientSize = new Size(S(440), S(560));

            ListView list = new ListView();
            list.Dock = DockStyle.Fill;
            list.View = View.Details;
            list.FullRowSelect = true;
            list.GridLines = true;
            list.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            list.Columns.Add("偏移", S(60), HorizontalAlignment.Right);
            list.Columns.Add("MIDI", S(70), HorizontalAlignment.Right);
            list.Columns.Add("音名", S(80), HorizontalAlignment.Center);
            list.Columns.Add("簡譜", S(90), HorizontalAlignment.Center);
            list.Columns.Add("電腦鍵", S(100), HorizontalAlignment.Center);
            list.BeginUpdate();
            for (int off = 0; off < Keymap.KeyCount; off++)
            {
                ListViewItem item = new ListViewItem(off.ToString());
                item.SubItems.Add((baseNote + off).ToString());
                item.SubItems.Add(Keymap.NoteName(baseNote + off));
                item.SubItems.Add(Keymap.Degree(off));
                item.SubItems.Add(Keymap.Keys[off]);
                if (Keymap.Degree(off).Length > 1 && off != 36) item.BackColor = Color.FromArgb(236, 236, 236);   // black keys
                list.Items.Add(item);
            }
            list.EndUpdate();
            f.Controls.Add(list);
            return f;
        }

        private void OnClearClick(object sender, EventArgs e)
        {
            logBox.Clear();
        }

        // ------------------------------------------------------------------
        // UI state
        // ------------------------------------------------------------------
        private void UpdateUi()
        {
            bool idle = mode == Mode.Idle;
            bool mapping = mode == Mode.Mapping;
            bool paused = mapping && engine != null && engine.IsPaused;
            deviceBox.Enabled = idle && HasDevices;
            refreshButton.Enabled = idle;
            runButton.Text = mapping ? "■ 停止映射" : "▶ 開始映射";
            runButton.Enabled = (idle && HasDevices) || mapping;
            pauseButton.Text = paused ? "▶ 繼續擷取" : "Ⅱ 暫停擷取";
            pauseButton.Enabled = mapping;
            calibrateButton.Enabled = idle && HasDevices;
            testButton.Text = (mode == Mode.Testing) ? "■ 停止測試" : "測試音階";
            testButton.Enabled = idle || mode == Mode.Testing;
            monitorButton.Text = (mode == Mode.Monitoring) ? "■ 結束監看" : "監看 MIDI";
            monitorButton.Enabled = (idle && HasDevices) || mode == Mode.Monitoring;
            keymapButton.Enabled = true;
            backgroundButton.Enabled = mode != Mode.Calibrating;
            exitButton.Enabled = true;

            string state;
            Color color;
            Icon icon;
            if (paused)
            {
                state = "已暫停";
                statusText.Text = "狀態：已暫停（MIDI 訊號不會送到遊戲）";
                color = ColorPause;
                icon = iconPause;
            }
            else if (mapping)
            {
                state = "映射中";
                statusText.Text = "狀態：映射中";
                color = ColorRun;
                icon = iconRun;
            }
            else if (mode == Mode.Calibrating)
            {
                state = "校準中";
                statusText.Text = "狀態：校準中";
                color = ColorBusy;
                icon = iconBusy;
            }
            else if (mode == Mode.Testing)
            {
                state = "測試音階中";
                statusText.Text = "狀態：測試音階中";
                color = ColorBusy;
                icon = iconBusy;
            }
            else if (mode == Mode.Monitoring)
            {
                state = "監看中";
                statusText.Text = "狀態：監看中（不會送出按鍵）";
                color = ColorBusy;
                icon = iconBusy;
            }
            else
            {
                state = "閒置";
                statusText.Text = "狀態：閒置";
                color = ColorIdle;
                icon = iconIdle;
            }
            statusText.ForeColor = color;

            if (tray != null)
            {
                tray.Icon = icon;
                tray.Text = AppTitle + " - " + state;   // at most 63 characters
                trayPauseItem.Text = paused ? "繼續擷取" : "暫停擷取";
                trayPauseItem.Enabled = mapping;
            }
        }

        // ------------------------------------------------------------------
        // Log
        // ------------------------------------------------------------------
        private void AddLog(string text)
        {
            if (logBox.TextLength > 200000)
            {
                string all = logBox.Text;
                int cut = all.IndexOf('\n', all.Length - 100000);
                logBox.Text = (cut >= 0) ? all.Substring(cut + 1) : "";
            }
            logBox.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + text + "\r\n");
        }
    }

    // ======================================================================
    // Calibration dialog: lowest key, then highest key (the console version's DoCalibrate, as a window).
    // Opens the MIDI device itself and closes it when the dialog closes.
    // DialogResult: OK = LowNote is set; Cancel = cancelled; Abort = Error is set.
    // ======================================================================
    internal sealed class CalibrationForm : Form
    {
        private const int HintAfterMs = 20000;

        private readonly MidiDeviceInfo device;
        private readonly NoteCatcher catcher;
        private readonly Timer timer = new Timer();
        private readonly float scale;
        private MidiIn midi;
        private Label stepText;
        private Label detailText;
        private Label hintText;
        private int step;
        private int low = -1;
        private int waited;
        private int lowNote = -1;
        private string error;

        public CalibrationForm(MidiDeviceInfo device, int channel, Font font, float scale, Icon icon)
        {
            this.device = device;
            this.scale = scale;
            catcher = new NoteCatcher(channel);

            Text = "校準最低音";
            Font = font;
            Icon = icon;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(Sc(520), Sc(260));

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.Padding = new Padding(Sc(16));
            root.ColumnCount = 1;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            int textWidth = Sc(480);

            stepText = new Label();
            stepText.AutoSize = true;
            stepText.MaximumSize = new Size(textWidth, 0);
            stepText.Font = new Font(font.FontFamily, font.Size * 1.25f, FontStyle.Bold);
            stepText.Margin = new Padding(0, 0, 0, Sc(10));

            detailText = new Label();
            detailText.AutoSize = true;
            detailText.MaximumSize = new Size(textWidth, 0);
            detailText.Margin = new Padding(0, 0, 0, Sc(8));

            Label note = new Label();
            note.AutoSize = true;
            note.MaximumSize = new Size(textWidth, 0);
            note.ForeColor = SystemColors.GrayText;
            note.Text = "鍵盤不是 37 鍵時：第 1 步請按「要當作 FF14 最低音 C」的琴鍵，第 2 步按從它往上數第 36 個半音的琴鍵（高三個八度的 C）。";

            hintText = new Label();
            hintText.AutoSize = true;
            hintText.MaximumSize = new Size(textWidth, 0);
            hintText.ForeColor = Color.FromArgb(214, 140, 0);
            hintText.Margin = new Padding(0, Sc(8), 0, 0);

            Button cancel = new Button();
            cancel.Text = "取消";
            cancel.AutoSize = true;
            cancel.MinimumSize = new Size(Sc(100), Sc(32));
            cancel.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
            cancel.DialogResult = DialogResult.Cancel;
            CancelButton = cancel;

            root.RowCount = 5;
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Controls.Add(stepText, 0, 0);
            root.Controls.Add(detailText, 0, 1);
            root.Controls.Add(note, 0, 2);
            root.Controls.Add(hintText, 0, 3);
            root.Controls.Add(cancel, 0, 4);
            Controls.Add(root);

            timer.Interval = 50;
            timer.Tick += OnTick;
            Shown += OnShown;
            FormClosed += OnClosed;
        }

        public int LowNote
        {
            get { return lowNote; }
        }

        public string Error
        {
            get { return error; }
        }

        private int Sc(int pixels)
        {
            return (int)Math.Round(pixels * scale);
        }

        private void OnShown(object sender, EventArgs e)
        {
            try
            {
                midi = MidiIn.Open(device.Index, device.Name, catcher.Handle);
            }
            catch (MidiException ex)
            {
                Fail(ex.Message);
                return;
            }
            ShowStep();
            timer.Start();
        }

        private void ShowStep()
        {
            waited = 0;
            hintText.Text = "";
            if (step == 0)
            {
                stepText.Text = "第 1 步：請按下鍵盤『最左邊（最低音）』的琴鍵";
                detailText.Text = "使用裝置：" + device.Name;
            }
            else
            {
                stepText.Text = "第 2 步：請再按下『最右邊（最高音）』的琴鍵";
                detailText.Text = string.Format("已收到最低音：MIDI {0} ({1})", low, Keymap.NoteName(low));
            }
        }

        private void OnTick(object sender, EventArgs e)
        {
            int n = catcher.TryTake();
            if (n >= 0)
            {
                OnNote(n);
                return;
            }
            if (midi.CheckLost())
            {
                Fail("MIDI 裝置中斷了（可能被拔掉或被其他程式搶走），校準沒有完成。");
                return;
            }
            waited += timer.Interval;
            if (waited >= HintAfterMs && hintText.Text.Length == 0)
            {
                hintText.Text = "還沒收到琴鍵訊號：請確認有按下琴鍵、USB 有接好，沒有被其他程式佔用；" +
                    "如果 config.txt 的 channel 不是 0，也請改成 0。";
            }
        }

        private void OnNote(int note)
        {
            if (step == 0)
            {
                low = note;
                step = 1;
                catcher.Clear();
                ShowStep();
                return;
            }

            int high = note;
            if (high - low != Keymap.KeyCount - 1)
            {
                timer.Stop();
                DialogResult answer = MessageBox.Show(this, string.Format(
                    "最高音 MIDI {0} ({1}) 與最低音 MIDI {2} ({3}) 相差 {4} 個半音，不是 36。\r\n\r\n" +
                    "可能原因：鍵盤不是 37 鍵、按錯琴鍵，或兩次按鍵之間改了 Octave / Transpose。\r\n\r\n" +
                    "按「是」= 仍然儲存；按「否」= 重新校準。",
                    high, Keymap.NoteName(high), low, Keymap.NoteName(low), high - low),
                    Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (answer != DialogResult.Yes)
                {
                    step = 0;
                    low = -1;
                    catcher.Clear();
                    ShowStep();
                    timer.Start();
                    return;
                }
            }
            timer.Stop();
            lowNote = low;
            DialogResult = DialogResult.OK;   // closes the dialog
        }

        private void Fail(string message)
        {
            timer.Stop();
            error = message;
            DialogResult = DialogResult.Abort;
        }

        private void OnClosed(object sender, FormClosedEventArgs e)
        {
            timer.Stop();
            if (midi != null)
            {
                midi.Close();
                midi = null;
            }
        }
    }
}
