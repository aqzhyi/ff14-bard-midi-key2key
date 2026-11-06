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
        private bool linked;
        private bool filling;
        private bool exiting;
        private bool shutDown;
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
        }

        // Ends whatever mode is running (keys released, device closed) and returns to Idle.
        private void StopEverything()
        {
            StopMapping(null);
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
                AddLog("尚未校準，暫時使用預設最低音 MIDI " + Keymap.DefaultBase.ToString() + "。");
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
            MessageBox.Show(this, text, AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
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

        private void OnCalibrateClick(object sender, EventArgs e)
        {
        }

        private void OnTestClick(object sender, EventArgs e)
        {
        }

        private void OnMonitorClick(object sender, EventArgs e)
        {
        }

        private void OnKeymapClick(object sender, EventArgs e)
        {
        }

        private void OnBackgroundClick(object sender, EventArgs e)
        {
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
            calibrateButton.Enabled = false;
            testButton.Enabled = false;
            monitorButton.Enabled = false;
            keymapButton.Enabled = false;
            backgroundButton.Enabled = false;
            exitButton.Enabled = true;

            if (paused)
            {
                statusText.Text = "狀態：已暫停（MIDI 訊號不會送到遊戲）";
                statusText.ForeColor = ColorPause;
            }
            else if (mapping)
            {
                statusText.Text = "狀態：映射中";
                statusText.ForeColor = ColorRun;
            }
            else
            {
                statusText.Text = "狀態：閒置";
                statusText.ForeColor = ColorIdle;
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
}
