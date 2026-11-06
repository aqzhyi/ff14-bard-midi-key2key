// Key2Key.cs - FF14 key2key, C# zero-install version.
// Maps a USB MIDI keyboard to the 37 performance keys of FINAL FANTASY XIV.
//
// How the keys are delivered: each note is posted with PostMessage as WM_KEYDOWN / WM_KEYUP straight to the
// FF14 game window (class FFXIVGAME). Unlike SendInput this does not go through the keyboard focus, so the notes
// reach FF14 while another window (a browser, say) is in front, and the physical keyboard keeps working normally.
// FF14 usually runs as administrator and Windows (UIPI) only lets an elevated process post messages to an
// elevated window, so app.manifest (embedded by build.bat) makes this exe start elevated as well.
//
// Compiled ON THE USER'S PC by the in-box .NET Framework csc.exe, which only speaks C# 5 (see build.bat).
// Rules for this file: no $"..." , no ?. , no expression-bodied members, no nameof, no "out var" /
// inline out declarations, no pattern matching, no tuples, no auto-property initializers, no local functions.
// Save as UTF-8 WITH BOM (build.bat passes /codepage:65001).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Ff14Key2Key
{
    // ======================================================================
    // Keymap: the 37 FFXIV notes (offset 0..36) -> computer key + scan code.
    // Mirrors KEYMAP.md. Scan codes are US-keyboard set-1 make codes.
    // ======================================================================
    internal static class Keymap
    {
        public const int KeyCount = 37;
        public const int DefaultBase = 48;   // FF14 C3 == MIDI 48 (middle C == 60)
        public const int MaxScanCode = 0x58; // every scan code in the table must be 1..MaxScanCode

        // index = offset (0..36)
        public static readonly string[] Keys = new string[]
        {
            "A", "-", "S", "=", "D", "F", "X", "G", ",", "H", ".", "J",
            "Q", "Z", "W", "M", "E", "R", "V", "T", "K", "Y", "N", "U",
            "1", "P", "2", "O", "3", "4", "B", "5", "C", "6", "L", "7",
            "8"
        };

        // Hardware scan codes (set-1 make codes, no extended flag), same order as Keys.
        public static readonly int[] ScanCodes = new int[]
        {
            0x1E, 0x0C, 0x1F, 0x0D, 0x20, 0x21, 0x2D, 0x22, 0x33, 0x23, 0x34, 0x24,
            0x10, 0x2C, 0x11, 0x32, 0x12, 0x13, 0x2F, 0x14, 0x25, 0x15, 0x31, 0x16,
            0x02, 0x19, 0x03, 0x18, 0x04, 0x05, 0x30, 0x06, 0x2E, 0x07, 0x26, 0x08,
            0x09
        };

        private static readonly string[] SemiNames = new string[]
        {
            "C", "C#", "D", "Eb", "E", "F", "F#", "G", "G#", "A", "Bb", "B"
        };

        private static readonly string[] SemiDegrees = new string[]
        {
            "1", "1#", "2", "3b", "3", "4", "4#", "5", "5#", "6", "7b", "7"
        };

        // Returns null when the table is sane, otherwise a short English description of the problem.
        public static string Validate()
        {
            if (Keys.Length != KeyCount || ScanCodes.Length != KeyCount)
            {
                return "table length is not 37";
            }
            for (int i = 0; i < KeyCount; i++)
            {
                for (int j = i + 1; j < KeyCount; j++)
                {
                    if (Keys[i] == Keys[j]) return "duplicate key " + Keys[i];
                    if (ScanCodes[i] == ScanCodes[j]) return "duplicate scan code " + ScanCodes[i].ToString("X2");
                }
                if (ScanCodes[i] <= 0 || ScanCodes[i] > MaxScanCode) return "bad scan code at offset " + i.ToString();
            }
            return null;
        }

        // MIDI note -> offset 0..36, or -1 when outside the 37-key range.
        public static int OffsetForNote(int note, int baseNote)
        {
            int off = note - baseNote;
            if (off < 0 || off >= KeyCount) return -1;
            return off;
        }

        // C4 == 60 naming, e.g. 48 -> "C3", 61 -> "C#4".
        public static string NoteName(int midiNote)
        {
            if (midiNote < 0 || midiNote > 127) return "?";
            return SemiNames[midiNote % 12] + (midiNote / 12 - 1).ToString();
        }

        // Degree label as used in the FF14 performance UI ("1", "1#", "3b", ... , top key "1(high)").
        public static string Degree(int offset)
        {
            if (offset == 36) return "1(high)";
            return SemiDegrees[offset % 12];
        }
    }

    // ======================================================================
    // Key sink abstraction: the Engine never touches Win32 directly.
    // ======================================================================
    internal interface IKeySink
    {
        // Both return true when the event was delivered, or when there is nothing left to deliver.
        // False = could not be delivered (the Engine does not mark the key as pressed then).
        bool KeyDown(int scanCode);
        bool KeyUp(int scanCode);
    }

    // ======================================================================
    // MIDI short message decoding (pure).
    // ======================================================================
    internal enum MidiKind
    {
        Other,
        NoteOn,
        NoteOff,
        AllNotesOff
    }

    internal struct MidiMessage
    {
        public MidiKind Kind;
        public int Channel;   // 1..16, or 0 for non-channel messages
        public int Data1;     // note number, or controller number
        public int Data2;     // velocity, or controller value

        // raw = status | data1 << 8 | data2 << 16 (the winmm MIM_DATA dwParam1 layout)
        public static MidiMessage Parse(uint raw)
        {
            MidiMessage m = new MidiMessage();
            int status = (int)(raw & 0xFF);
            m.Data1 = (int)((raw >> 8) & 0xFF);
            m.Data2 = (int)((raw >> 16) & 0xFF);
            m.Kind = MidiKind.Other;
            m.Channel = 0;
            if (status < 0x80 || status >= 0xF0)
            {
                return m;
            }
            m.Channel = (status & 0x0F) + 1;
            int type = status & 0xF0;
            if (type == 0x90)
            {
                m.Kind = (m.Data2 > 0) ? MidiKind.NoteOn : MidiKind.NoteOff;
            }
            else if (type == 0x80)
            {
                m.Kind = MidiKind.NoteOff;
            }
            else if (type == 0xB0 && (m.Data1 == 123 || m.Data1 == 120))
            {
                m.Kind = MidiKind.AllNotesOff;
            }
            return m;
        }
    }

    // ======================================================================
    // Engine: pure logic, guarded by a lock (MIDI callbacks arrive on another thread).
    // ======================================================================
    internal sealed class Engine
    {
        private readonly object sync = new object();
        private readonly IKeySink sink;
        private readonly bool[] down = new bool[Keymap.KeyCount];
        private readonly HashSet<int> hintSeen = new HashSet<int>();
        private readonly List<int> pendingHints = new List<int>();
        private readonly int baseNote;
        private readonly int channel;
        private bool closed;
        private bool paused;

        public Engine(IKeySink sink, int baseNote, int channel)
        {
            this.sink = sink;
            this.baseNote = baseNote;
            this.channel = channel;
        }

        public int BaseNote
        {
            get { return baseNote; }
        }

        public void HandleRaw(uint raw)
        {
            Handle(MidiMessage.Parse(raw));
        }

        public void Handle(MidiMessage m)
        {
            if (m.Kind == MidiKind.Other) return;
            if (channel != 0 && m.Channel != channel) return;
            lock (sync)
            {
                if (closed || paused) return;
                switch (m.Kind)
                {
                    case MidiKind.NoteOn:
                        NoteOnLocked(m.Data1);
                        break;
                    case MidiKind.NoteOff:
                        NoteOffLocked(m.Data1);
                        break;
                    case MidiKind.AllNotesOff:
                        ReleaseAllLocked();
                        break;
                }
            }
        }

        // Direct access by offset (used by the "test" command).
        public void PressOffset(int offset)
        {
            lock (sync)
            {
                if (closed) return;
                if (offset < 0 || offset >= Keymap.KeyCount) return;
                PressLocked(offset);
            }
        }

        public void ReleaseOffset(int offset)
        {
            lock (sync)
            {
                if (offset < 0 || offset >= Keymap.KeyCount) return;
                ReleaseLocked(offset);
            }
        }

        // Key-up for every pressed key. Safe to call at any time, any number of times.
        public void ReleaseAll()
        {
            lock (sync)
            {
                ReleaseAllLocked();
            }
        }

        // Pause: release every pressed key and ignore MIDI input until resumed. Both happen under the same lock,
        // so a Note On arriving at the same moment can never slip through and leave a key stuck.
        // A key held through the pause is not marked down, so its later Note Off is ignored (no stray key-up).
        public void SetPaused(bool value)
        {
            lock (sync)
            {
                paused = value;
                if (paused) ReleaseAllLocked();
            }
        }

        public bool IsPaused
        {
            get
            {
                lock (sync)
                {
                    return paused;
                }
            }
        }

        // Release everything AND ignore all further input (used on every exit path).
        public void Shutdown()
        {
            lock (sync)
            {
                closed = true;
                ReleaseAllLocked();
            }
        }

        // Out-of-range note numbers seen since the last call (each distinct number is reported once).
        public int[] DrainHints()
        {
            lock (sync)
            {
                int[] result = pendingHints.ToArray();
                pendingHints.Clear();
                return result;
            }
        }

        private void NoteOnLocked(int note)
        {
            int off = Keymap.OffsetForNote(note, baseNote);
            if (off < 0)
            {
                if (hintSeen.Add(note)) pendingHints.Add(note);
                return;
            }
            PressLocked(off);
        }

        private void NoteOffLocked(int note)
        {
            int off = Keymap.OffsetForNote(note, baseNote);
            if (off < 0) return;
            ReleaseLocked(off);   // does nothing when the key is not down (never a stray key-up)
        }

        // Note On: when already down, release first so FF14 re-triggers the note.
        private void PressLocked(int off)
        {
            int scan = Keymap.ScanCodes[off];
            if (down[off])
            {
                down[off] = false;
                if (!sink.KeyUp(scan)) sink.KeyUp(scan);
            }
            if (sink.KeyDown(scan))
            {
                down[off] = true;
            }
        }

        private void ReleaseLocked(int off)
        {
            if (!down[off]) return;
            down[off] = false;
            int scan = Keymap.ScanCodes[off];
            if (!sink.KeyUp(scan)) sink.KeyUp(scan);   // one retry
        }

        private void ReleaseAllLocked()
        {
            for (int i = 0; i < down.Length; i++)
            {
                ReleaseLocked(i);
            }
        }
    }

    // ======================================================================
    // ChordSpreader (optional, chord_spread_ms > 0).
    // FF14 plays one note at a time, and key-downs that reach the game at the same instant mostly collapse into one
    // note. With spreading on, Note Ons go to the engine at least SpreadMs apart, and notes pressed together (within
    // CollectMs) go out from low to high, so the highest (melody) note is the one that keeps sounding.
    // Only Note Ons the player pressed are delayed: it never creates or repeats a note. A Note Off for a note that is
    // still waiting cancels it. SpreadMs 0 = straight through, no delay at all.
    // Every engine call happens under this class's lock, so a Note Off can never overtake its own Note On
    // (which would leave the key down). The MIDI callback never waits: the delays run on a timer thread.
    // ======================================================================
    internal sealed class ChordSpreader
    {
        public const int MaxSpreadMs = 200;
        public const int CollectMs = 8;   // notes arriving within this window count as "pressed together"

        [DllImport("winmm.dll")]
        private static extern uint timeBeginPeriod(uint uPeriod);

        [DllImport("winmm.dll")]
        private static extern uint timeEndPeriod(uint uPeriod);

        private readonly object sync = new object();
        private readonly Engine engine;
        private readonly List<MidiMessage> waiting = new List<MidiMessage>();   // arrival window still open
        private readonly List<MidiMessage> ready = new List<MidiMessage>();     // sorted, going out one by one
        private readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        private readonly AutoResetEvent wake = new AutoResetEvent(false);
        private readonly Thread thread;
        private long windowEnd;
        private long lastDown = -100000;
        private int spreadMs;
        private bool stopped;
        private bool timerRaised;

        public ChordSpreader(Engine engine, int spreadMs)
        {
            this.engine = engine;
            SpreadMs = spreadMs;
            thread = new Thread(Run);
            thread.IsBackground = true;
            thread.Name = "chord spreader";
            thread.Start();
        }

        // 0 = off; otherwise clamped to 1..MaxSpreadMs. Can be changed while running.
        public int SpreadMs
        {
            get
            {
                lock (sync)
                {
                    return spreadMs;
                }
            }
            set
            {
                int v = value;
                if (v < 0) v = 0;
                if (v > MaxSpreadMs) v = MaxSpreadMs;
                lock (sync)
                {
                    spreadMs = v;
                    // Default Windows timer steps are ~15.6 ms, too coarse for a 20-30 ms spread.
                    if (v > 0 && !timerRaised && !stopped)
                    {
                        timerRaised = true;
                        try { timeBeginPeriod(1); } catch (Exception) { }
                    }
                }
                wake.Set();
            }
        }

        // Called on the MIDI thread instead of Engine.Handle.
        public void Handle(MidiMessage m)
        {
            lock (sync)
            {
                if (stopped)
                {
                    engine.Handle(m);   // the engine is shut down by then; it ignores everything
                    return;
                }
                if (m.Kind == MidiKind.NoteOn)
                {
                    if (spreadMs == 0 && waiting.Count == 0 && ready.Count == 0)
                    {
                        engine.Handle(m);
                        lastDown = clock.ElapsedMilliseconds;
                        return;
                    }
                    RemoveLocked(m);   // the same key pressed again while still waiting: keep only the newest
                    if (waiting.Count == 0) windowEnd = clock.ElapsedMilliseconds + CollectMs;
                    waiting.Add(m);
                    wake.Set();
                    return;
                }
                if (m.Kind == MidiKind.NoteOff)
                {
                    if (RemoveLocked(m)) return;   // never reached the game: nothing to release
                    engine.Handle(m);
                    return;
                }
                if (m.Kind == MidiKind.AllNotesOff)
                {
                    waiting.Clear();
                    ready.Clear();
                }
                engine.Handle(m);
            }
        }

        // Drop the notes that are still waiting (used on pause).
        public void Clear()
        {
            lock (sync)
            {
                waiting.Clear();
                ready.Clear();
            }
        }

        public void Stop()
        {
            lock (sync)
            {
                if (stopped) return;
                stopped = true;
                waiting.Clear();
                ready.Clear();
                if (timerRaised)
                {
                    timerRaised = false;
                    try { timeEndPeriod(1); } catch (Exception) { }
                }
            }
            wake.Set();
            if (Thread.CurrentThread != thread) thread.Join(500);
        }

        private bool RemoveLocked(MidiMessage m)
        {
            bool removed = false;
            for (int i = waiting.Count - 1; i >= 0; i--)
            {
                if (waiting[i].Data1 == m.Data1 && waiting[i].Channel == m.Channel)
                {
                    waiting.RemoveAt(i);
                    removed = true;
                }
            }
            for (int i = ready.Count - 1; i >= 0; i--)
            {
                if (ready[i].Data1 == m.Data1 && ready[i].Channel == m.Channel)
                {
                    ready.RemoveAt(i);
                    removed = true;
                }
            }
            return removed;
        }

        private static int CompareNotes(MidiMessage a, MidiMessage b)
        {
            return a.Data1.CompareTo(b.Data1);
        }

        private void Run()
        {
            while (true)
            {
                int waitMs = Timeout.Infinite;
                lock (sync)
                {
                    if (stopped) return;
                    long now = clock.ElapsedMilliseconds;
                    if (waiting.Count > 0 && (now >= windowEnd || spreadMs == 0))
                    {
                        waiting.Sort(CompareNotes);   // low to high: the top note sounds last and keeps sounding
                        ready.AddRange(waiting);
                        waiting.Clear();
                    }
                    while (ready.Count > 0)
                    {
                        long due = (spreadMs == 0) ? now : lastDown + spreadMs;
                        if (now < due)
                        {
                            waitMs = (int)(due - now);
                            break;
                        }
                        MidiMessage next = ready[0];
                        ready.RemoveAt(0);
                        engine.Handle(next);
                        lastDown = now;
                    }
                    if (waiting.Count > 0)
                    {
                        int w = (int)Math.Max(1, windowEnd - now);
                        waitMs = (waitMs == Timeout.Infinite) ? w : Math.Min(waitMs, w);
                    }
                }
                wake.WaitOne(waitMs);
            }
        }
    }

    // ======================================================================
    // Config (config.txt next to the exe).
    // ======================================================================
    internal sealed class Config
    {
        public int BaseNote = Keymap.DefaultBase;
        public string Device = "";
        public int Channel = 0;
        public int ChordSpreadMs = 0;

        public static Config Load(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    return Parse(File.ReadAllLines(path, Encoding.UTF8));
                }
            }
            catch (Exception)
            {
                // unreadable file -> defaults
            }
            return new Config();
        }

        public static Config Parse(string[] lines)
        {
            Config c = new Config();
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line == null) continue;
                if (line.Length > 0 && line[0] == '\uFEFF') line = line.Substring(1);
                line = line.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                string val = line.Substring(eq + 1).Trim();
                int n;
                if (key == "base_note")
                {
                    if (TryParseInt(val, out n) && n >= 0 && n <= 127) c.BaseNote = n;
                }
                else if (key == "channel")
                {
                    if (TryParseInt(val, out n) && n >= 0 && n <= 16) c.Channel = n;
                }
                else if (key == "device")
                {
                    c.Device = val;
                }
                else if (key == "chord_spread_ms")
                {
                    if (TryParseInt(val, out n) && n >= 0 && n <= ChordSpreader.MaxSpreadMs) c.ChordSpreadMs = n;
                }
                // unknown keys are ignored
            }
            return c;
        }

        // Accepts "48" and also "48   # comment".
        private static bool TryParseInt(string text, out int value)
        {
            int hash = text.IndexOf('#');
            if (hash >= 0) text = text.Substring(0, hash);
            return int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        public string Serialize()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("# FF14 key2key 設定檔（UTF-8，可用記事本編輯；存檔後重新啟動程式）\r\n");
            sb.Append("\r\n");
            sb.Append("# base_note：鍵盤「最左邊（最低音）」琴鍵的 MIDI 音符編號。FF14 的 C3 = 48（中央 C = 60）。\r\n");
            sb.Append("# 建議用選單的「校準最低音」自動設定，不用手動改。\r\n");
            sb.Append("base_note=").Append(BaseNote.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            sb.Append("\r\n");
            sb.Append("# device：MIDI 裝置名稱的一部分（不分大小寫）。留空 = 自動選擇。\r\n");
            sb.Append("device=").Append(Device ?? "").Append("\r\n");
            sb.Append("\r\n");
            sb.Append("# channel：只接受這個 MIDI 頻道（1~16）。0 = 接受全部頻道。\r\n");
            sb.Append("channel=").Append(Channel.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            sb.Append("\r\n");
            sb.Append("# chord_spread_ms：和弦錯開（毫秒）。FF14 一次只能發一個音，同時按下的鍵通常只會響一個。\r\n");
            sb.Append("# 設成 10~200：同時按下的音會由低到高、每個間隔這麼多毫秒送出（最後持續的是最高音）。0 = 關閉。\r\n");
            sb.Append("chord_spread_ms=").Append(ChordSpreadMs.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            return sb.ToString();
        }

        public void Save(string path)
        {
            File.WriteAllText(path, Serialize(), new UTF8Encoding(true));   // BOM: old Notepad shows the Chinese comments correctly
        }
    }

    // ======================================================================
    // FF14 window lookup and key messages. PostMessage (never SendMessage): a hung game must not block us.
    // ======================================================================
    internal sealed class WindowInfo
    {
        public IntPtr Hwnd;
        public string Title;
        public string ClassName;
        public uint Pid;
        public string ProcessName;
    }

    internal static class Ff14Window
    {
        private const string GameClassName = "FFXIVGAME";
        private const string GameProcessNameDx11 = "ffxiv_dx11";   // process names carry no ".exe"
        private const string GameProcessNameDx9 = "ffxiv";
        private const uint WM_KEYDOWN = 0x0100;
        private const uint WM_KEYUP = 0x0101;
        private const uint MAPVK_VSC_TO_VK_EX = 3;

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", EntryPoint = "PostMessageW", ExactSpelling = true)]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", EntryPoint = "MapVirtualKeyW", ExactSpelling = true)]
        private static extern uint MapVirtualKey(uint uCode, uint uMapType);

        public static bool IsAlive(IntPtr hwnd)
        {
            return IsWindow(hwnd);
        }

        // The FF14 game window, or IntPtr.Zero when the game is not running.
        public static IntPtr Find()
        {
            WindowInfo w = FindGame(VisibleWindows());
            return (w == null) ? IntPtr.Zero : w.Hwnd;
        }

        // Class name "FFXIVGAME" first, then a process named exactly ffxiv_dx11 or ffxiv (case-insensitive), so the
        // launchers (ffxivlaunchertc, ffxivsimplelauncher, ffxivboot) never match. A window title alone is never enough
        // (a browser tab can say "FINAL FANTASY XIV").
        private static WindowInfo FindGame(List<WindowInfo> visible)
        {
            for (int i = 0; i < visible.Count; i++)
            {
                if (visible[i].ClassName == GameClassName) return visible[i];
            }
            for (int k = 0; k < visible.Count; k++)
            {
                string name = visible[k].ProcessName;
                if (string.Equals(name, GameProcessNameDx11, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, GameProcessNameDx9, StringComparison.OrdinalIgnoreCase)) return visible[k];
            }
            return null;
        }

        // Every visible top-level window with its class name and owning process name.
        public static List<WindowInfo> VisibleWindows()
        {
            List<WindowInfo> all = new List<WindowInfo>();
            EnumWindowsProc callback = delegate(IntPtr hWnd, IntPtr lParam)
            {
                try
                {
                    if (!IsWindowVisible(hWnd)) return true;
                    StringBuilder title = new StringBuilder(512);
                    GetWindowText(hWnd, title, title.Capacity);
                    StringBuilder cls = new StringBuilder(256);
                    GetClassName(hWnd, cls, cls.Capacity);
                    uint pid;
                    GetWindowThreadProcessId(hWnd, out pid);
                    WindowInfo w = new WindowInfo();
                    w.Hwnd = hWnd;
                    w.Title = title.ToString();
                    w.ClassName = cls.ToString();
                    w.Pid = pid;
                    w.ProcessName = "?";
                    all.Add(w);
                }
                catch (Exception)
                {
                }
                return true;
            };
            EnumWindows(callback, IntPtr.Zero);
            GC.KeepAlive(callback);

            Dictionary<uint, string> names = ProcessNames();
            for (int i = 0; i < all.Count; i++)
            {
                string name;
                if (names.TryGetValue(all[i].Pid, out name)) all[i].ProcessName = name;
            }
            return all;
        }

        // One process snapshot for the whole window list (cheap, unlike asking for every window's process separately).
        private static Dictionary<uint, string> ProcessNames()
        {
            Dictionary<uint, string> names = new Dictionary<uint, string>();
            try
            {
                System.Diagnostics.Process[] processes = System.Diagnostics.Process.GetProcesses();
                for (int i = 0; i < processes.Length; i++)
                {
                    try
                    {
                        names[(uint)processes[i].Id] = processes[i].ProcessName;
                    }
                    catch (Exception)
                    {
                    }
                    finally
                    {
                        processes[i].Dispose();
                    }
                }
            }
            catch (Exception)
            {
            }
            return names;
        }

        // Posts WM_KEYDOWN / WM_KEYUP for a hardware scan code. Returns false when the message could not be posted.
        // wParam = virtual key for the scan code; lParam: bits 0-15 repeat count 1, bits 16-23 scan code,
        // and for key-up also bit 30 (key was down) and bit 31 (key is being released).
        public static bool PostKey(IntPtr hwnd, int scanCode, bool isDown)
        {
            uint vk = MapVirtualKey((uint)scanCode, MAPVK_VSC_TO_VK_EX);
            if (vk == 0) return false;
            uint lParam = 1u | ((uint)scanCode << 16);
            if (isDown)
            {
                return PostMessage(hwnd, WM_KEYDOWN, ToPtr(vk), ToPtr(lParam));
            }
            lParam = lParam | (1u << 30) | (1u << 31);
            return PostMessage(hwnd, WM_KEYUP, ToPtr(vk), ToPtr(lParam));
        }

        // A 32-bit message value as a pointer-sized one: zero-extended on x64 (never sign-extended), plain on x86.
        private static IntPtr ToPtr(uint value)
        {
            if (IntPtr.Size == 8) return new IntPtr((long)value);
            return new IntPtr(unchecked((int)value));
        }
    }

    // ======================================================================
    // Ff14PostSink: key events as messages posted to the FF14 window (whatever window has the focus).
    // The window handle is cached and checked on every event; when it is gone the sink looks again,
    // at most every 500 ms (so a closed game costs nothing per note). Events without a window are dropped
    // silently; this class never writes to the console (it runs on the MIDI thread).
    // ======================================================================
    internal sealed class Ff14PostSink : IKeySink
    {
        private const int SearchIntervalMs = 500;

        private readonly object sync = new object();
        private readonly IntPtr[] postedTo = new IntPtr[Keymap.MaxScanCode + 1];   // window each held key went down in
        private IntPtr hwnd = IntPtr.Zero;
        private int nextSearch = Environment.TickCount;
        private int undelivered;

        // Number of events that could not be delivered so far (no FF14 window, or the post failed).
        public int Undelivered
        {
            get { return Thread.VolatileRead(ref undelivered); }
        }

        // Main-thread poll: re-checks the cached window (searching again at most every 500 ms).
        // True while the FF14 window is reachable.
        public bool Poll()
        {
            lock (sync)
            {
                return TargetLocked() != IntPtr.Zero;
            }
        }

        public bool KeyDown(int scanCode)
        {
            lock (sync)
            {
                IntPtr target = TargetLocked();
                if (target == IntPtr.Zero || !Ff14Window.PostKey(target, scanCode, true))
                {
                    Interlocked.Increment(ref undelivered);
                    return false;
                }
                postedTo[scanCode] = target;
                return true;
            }
        }

        // The key-up goes to the very window that got the key-down; when that window is gone there is nothing to release.
        public bool KeyUp(int scanCode)
        {
            lock (sync)
            {
                IntPtr target = postedTo[scanCode];
                if (target == IntPtr.Zero) return true;
                if (!Ff14Window.IsAlive(target))
                {
                    postedTo[scanCode] = IntPtr.Zero;
                    return true;
                }
                if (!Ff14Window.PostKey(target, scanCode, false))
                {
                    Interlocked.Increment(ref undelivered);
                    return false;
                }
                postedTo[scanCode] = IntPtr.Zero;
                return true;
            }
        }

        private IntPtr TargetLocked()
        {
            if (hwnd != IntPtr.Zero)
            {
                if (Ff14Window.IsAlive(hwnd)) return hwnd;
                hwnd = IntPtr.Zero;   // the game window is gone (closed, or the game is restarting)
            }
            int now = Environment.TickCount;
            if (unchecked(now - nextSearch) < 0) return IntPtr.Zero;
            nextSearch = unchecked(now + SearchIntervalMs);
            hwnd = Ff14Window.Find();
            return hwnd;
        }
    }

    // ======================================================================
    // Console helpers (kernel32).
    // ======================================================================
    internal static class NativeConsole
    {
        public delegate bool CtrlHandler(uint ctrlType);

        private const int STD_INPUT_HANDLE = -10;
        private const uint ENABLE_QUICK_EDIT_MODE = 0x0040;
        private const uint ENABLE_EXTENDED_FLAGS = 0x0080;

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetConsoleCtrlHandler(CtrlHandler handler, bool add);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetConsoleProcessList(uint[] lpdwProcessList, uint dwProcessCount);

        private static IntPtr inHandle = IntPtr.Zero;
        private static uint originalMode = 0;
        private static bool modeChanged = false;

        // A click in a QuickEdit console freezes console writes and turns Ctrl+C into "copy".
        // Switch it off while a mapping mode is running; RestoreMode() puts it back.
        public static void DisableQuickEdit()
        {
            try
            {
                if (modeChanged) return;
                IntPtr h = GetStdHandle(STD_INPUT_HANDLE);
                if (h == IntPtr.Zero || h == new IntPtr(-1)) return;
                uint mode;
                if (!GetConsoleMode(h, out mode)) return;
                uint newMode = (mode | ENABLE_EXTENDED_FLAGS) & ~ENABLE_QUICK_EDIT_MODE;
                if (SetConsoleMode(h, newMode))
                {
                    inHandle = h;
                    originalMode = mode;
                    modeChanged = true;
                }
            }
            catch (Exception)
            {
            }
        }

        public static void RestoreMode()
        {
            try
            {
                if (modeChanged)
                {
                    modeChanged = false;
                    SetConsoleMode(inHandle, originalMode);
                }
            }
            catch (Exception)
            {
            }
        }

        // True when this process is the only one attached to its console (the exe was double-clicked,
        // so Windows opened a console window just for it). Then we must pause before closing on errors.
        public static bool OwnsConsole()
        {
            try
            {
                uint[] list = new uint[8];
                uint count = GetConsoleProcessList(list, (uint)list.Length);
                return count == 1;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    // ======================================================================
    // MIDI input via winmm.
    // ======================================================================
    internal sealed class MidiDeviceInfo
    {
        public int Index;
        public string Name;
    }

    internal sealed class MidiException : Exception
    {
        public MidiException(string message) : base(message)
        {
        }
    }

    internal delegate void MidiShortHandler(uint raw);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate void MidiInProc(IntPtr hMidiIn, uint wMsg, IntPtr dwInstance, IntPtr dwParam1, IntPtr dwParam2);

    internal sealed class MidiIn
    {
        private const uint MIM_CLOSE = 0x3C2;
        private const uint MIM_DATA = 0x3C3;
        private const uint CALLBACK_FUNCTION = 0x00030000;

        // typedef struct tagMIDIINCAPSW { WORD wMid; WORD wPid; MMVERSION vDriverVersion;
        //                                 WCHAR szPname[32]; DWORD dwSupport; }  => 76 bytes
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MIDIINCAPS
        {
            public ushort wMid;
            public ushort wPid;
            public uint vDriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szPname;
            public uint dwSupport;
        }

        [DllImport("winmm.dll", EntryPoint = "midiInGetNumDevs")]
        private static extern uint midiInGetNumDevs();

        [DllImport("winmm.dll", EntryPoint = "midiInGetDevCapsW", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint midiInGetDevCaps(UIntPtr uDeviceID, ref MIDIINCAPS pmic, uint cbmic);

        [DllImport("winmm.dll", EntryPoint = "midiInGetErrorTextW", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint midiInGetErrorText(uint mmrError, StringBuilder pszText, uint cchText);

        [DllImport("winmm.dll", EntryPoint = "midiInOpen")]
        private static extern uint midiInOpen(out IntPtr lphMidiIn, uint uDeviceID, MidiInProc dwCallback, IntPtr dwInstance, uint dwFlags);

        [DllImport("winmm.dll", EntryPoint = "midiInStart")]
        private static extern uint midiInStart(IntPtr hMidiIn);

        [DllImport("winmm.dll", EntryPoint = "midiInStop")]
        private static extern uint midiInStop(IntPtr hMidiIn);

        [DllImport("winmm.dll", EntryPoint = "midiInReset")]
        private static extern uint midiInReset(IntPtr hMidiIn);

        [DllImport("winmm.dll", EntryPoint = "midiInClose")]
        private static extern uint midiInClose(IntPtr hMidiIn);

        // The callback delegate lives in a static field so the GC can never collect it while winmm holds the pointer.
        private static readonly MidiInProc CallbackProc = new MidiInProc(StaticCallback);

        private readonly string name;
        private readonly MidiShortHandler handler;
        private IntPtr handle = IntPtr.Zero;
        private GCHandle gch;
        private volatile bool closing;
        private volatile bool lost;
        private volatile string callbackError;
        private bool callbackErrorSeen;
        private int nextCheck;
        private int missCount;

        private MidiIn(string name, MidiShortHandler handler)
        {
            this.name = name;
            this.handler = handler;
            this.nextCheck = Environment.TickCount;
        }

        public static List<MidiDeviceInfo> ListDevices()
        {
            List<MidiDeviceInfo> list = new List<MidiDeviceInfo>();
            uint n = midiInGetNumDevs();
            for (uint i = 0; i < n; i++)
            {
                MIDIINCAPS caps = new MIDIINCAPS();
                uint r = midiInGetDevCaps(new UIntPtr(i), ref caps, (uint)Marshal.SizeOf(typeof(MIDIINCAPS)));
                MidiDeviceInfo d = new MidiDeviceInfo();
                d.Index = (int)i;
                if (r == 0 && caps.szPname != null && caps.szPname.Length > 0)
                {
                    d.Name = caps.szPname;
                }
                else
                {
                    d.Name = "(unknown)";
                }
                list.Add(d);
            }
            return list;
        }

        // Throws MidiException (message already in Traditional Chinese) on failure.
        public static MidiIn Open(int deviceIndex, string deviceName, MidiShortHandler handler)
        {
            MidiIn m = new MidiIn(deviceName, handler);
            m.DoOpen(deviceIndex);
            return m;
        }

        private void DoOpen(int deviceIndex)
        {
            gch = GCHandle.Alloc(this);
            IntPtr h;
            uint r = midiInOpen(out h, (uint)deviceIndex, CallbackProc, GCHandle.ToIntPtr(gch), CALLBACK_FUNCTION);
            if (r != 0)
            {
                gch.Free();
                throw new MidiException(DescribeError(r, "開啟"));
            }
            handle = h;
            r = midiInStart(handle);
            if (r != 0)
            {
                IntPtr h2 = handle;
                handle = IntPtr.Zero;
                closing = true;
                uint rc = midiInClose(h2);
                if (rc == 0 && gch.IsAllocated) gch.Free();
                throw new MidiException(DescribeError(r, "啟動"));
            }
        }

        // Stop -> reset -> close. Never call this from inside the callback.
        public void Close()
        {
            IntPtr h = handle;
            if (h == IntPtr.Zero) return;
            handle = IntPtr.Zero;
            closing = true;
            try { midiInStop(h); } catch (Exception) { }
            try { midiInReset(h); } catch (Exception) { }
            uint r = 1;
            try { r = midiInClose(h); } catch (Exception) { }
            if (r == 0 && gch.IsAllocated)
            {
                gch.Free();
            }
        }

        // Main-thread poll: has the device vanished (unplugged / taken away)?
        public bool CheckLost()
        {
            if (lost) return true;
            int now = Environment.TickCount;
            if (unchecked(now - nextCheck) < 0) return false;
            nextCheck = unchecked(now + 1000);
            if (IsPresent(name))
            {
                missCount = 0;
            }
            else
            {
                missCount++;
                if (missCount >= 2) lost = true;
            }
            return lost;
        }

        // First exception thrown inside the MIDI callback (reported once, from the main thread).
        public string TakeCallbackError()
        {
            string e = callbackError;
            if (e != null) callbackError = null;
            return e;
        }

        private static bool IsPresent(string deviceName)
        {
            try
            {
                List<MidiDeviceInfo> l = ListDevices();
                for (int i = 0; i < l.Count; i++)
                {
                    if (string.Equals(l[i].Name, deviceName, StringComparison.Ordinal)) return true;
                }
                return false;
            }
            catch (Exception)
            {
                return true;   // cannot tell -> assume still there
            }
        }

        private static string DescribeError(uint code, string action)
        {
            if (code == 4)
            {
                return "無法" + action + " MIDI 裝置：這個裝置正被其他程式使用。請先關閉其他會用到 MIDI 鍵盤的程式（例如音樂軟體 DAW、其他 MIDI 工具、另一個本程式視窗），再試一次。";
            }
            StringBuilder sb = new StringBuilder(256);
            try
            {
                midiInGetErrorText(code, sb, 256);
            }
            catch (Exception)
            {
            }
            return string.Format("無法{0} MIDI 裝置（錯誤碼 {1}）：{2}", action, code, sb.ToString());
        }

        // Runs on a winmm thread. Must never throw and must never touch the console.
        private static void StaticCallback(IntPtr hMidiIn, uint wMsg, IntPtr dwInstance, IntPtr dwParam1, IntPtr dwParam2)
        {
            try
            {
                if (dwInstance == IntPtr.Zero) return;
                GCHandle g = GCHandle.FromIntPtr(dwInstance);
                MidiIn self = g.Target as MidiIn;
                if (self != null) self.OnMessage(wMsg, dwParam1);
            }
            catch (Exception)
            {
            }
        }

        private void OnMessage(uint msg, IntPtr param1)
        {
            if (msg == MIM_DATA)
            {
                uint raw = unchecked((uint)param1.ToInt64());
                MidiShortHandler h = handler;
                if (h == null || closing) return;
                try
                {
                    h(raw);
                }
                catch (Exception ex)
                {
                    if (!callbackErrorSeen)
                    {
                        callbackErrorSeen = true;
                        callbackError = ex.Message;
                    }
                }
            }
            else if (msg == MIM_CLOSE)
            {
                if (!closing) lost = true;
            }
            // MIM_LONGDATA, MIM_ERROR and the rest are ignored on purpose.
        }
    }

    // ======================================================================
    // Small helpers used by Program.
    // ======================================================================

    // Thread-safe line queue: the MIDI callback adds, the main thread prints.
    internal sealed class LogQueue
    {
        private readonly object sync = new object();
        private readonly Queue<string> queue = new Queue<string>();

        public void Add(string line)
        {
            lock (sync)
            {
                if (queue.Count < 5000) queue.Enqueue(line);
            }
        }

        // Oldest line, or null when the queue is empty (used by the window version).
        public string TryTake()
        {
            lock (sync)
            {
                if (queue.Count > 0) return queue.Dequeue();
            }
            return null;
        }

        public void FlushToConsole()
        {
            while (true)
            {
                string line = null;
                lock (sync)
                {
                    if (queue.Count > 0) line = queue.Dequeue();
                }
                if (line == null) break;
                Console.WriteLine(line);
            }
        }
    }

    // Collects Note On notes (for calibration).
    internal sealed class NoteCatcher
    {
        private readonly object sync = new object();
        private readonly Queue<int> notes = new Queue<int>();
        private readonly int channel;

        public NoteCatcher(int channel)
        {
            this.channel = channel;
        }

        public void Handle(uint raw)
        {
            MidiMessage m = MidiMessage.Parse(raw);
            if (m.Kind != MidiKind.NoteOn) return;
            if (channel != 0 && m.Channel != channel) return;
            lock (sync)
            {
                if (notes.Count < 64) notes.Enqueue(m.Data1);
            }
        }

        // Returns the oldest note, or -1 when there is none.
        public int TryTake()
        {
            lock (sync)
            {
                if (notes.Count > 0) return notes.Dequeue();
            }
            return -1;
        }

        public void Clear()
        {
            lock (sync)
            {
                notes.Clear();
            }
        }
    }

    internal sealed class Options
    {
        public string Command = "";
        public string Device = null;
        public int BaseNote = -1;     // -1 = not given
        public int Channel = -1;      // -1 = not given
        public int Delay = 250;
        public bool Verbose = false;
        public string Error = null;

        public static Options Parse(string[] args)
        {
            Options o = new Options();
            int i = 0;
            while (i < args.Length)
            {
                string a = args[i];
                i++;
                string name = a;
                string val = null;
                if (a.StartsWith("--", StringComparison.Ordinal))
                {
                    int eq = a.IndexOf('=');
                    if (eq > 0)
                    {
                        name = a.Substring(0, eq);
                        val = a.Substring(eq + 1);
                    }
                }
                string lname = name.ToLowerInvariant();

                if (lname == "--verbose" || lname == "-v")
                {
                    o.Verbose = true;
                    continue;
                }
                if (lname == "--help" || lname == "-h" || lname == "-?" || lname == "/?")
                {
                    o.Command = "help";
                    continue;
                }
                if (lname == "--device" || lname == "--base" || lname == "--channel" || lname == "--delay")
                {
                    if (val == null)
                    {
                        if (i >= args.Length)
                        {
                            o.Error = "選項 " + lname + " 後面需要一個值。";
                            return o;
                        }
                        val = args[i];
                        i++;
                    }
                    if (lname == "--device")
                    {
                        o.Device = val;
                        continue;
                    }
                    int n;
                    if (!int.TryParse(val.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
                    {
                        o.Error = "選項 " + lname + " 需要一個整數，但收到「" + val + "」。";
                        return o;
                    }
                    if (lname == "--base")
                    {
                        if (n < 0 || n > 127)
                        {
                            o.Error = "--base 必須是 0 ~ 127 的 MIDI 音符編號。";
                            return o;
                        }
                        o.BaseNote = n;
                    }
                    else if (lname == "--channel")
                    {
                        if (n < 0 || n > 16)
                        {
                            o.Error = "--channel 必須是 0 ~ 16（0 = 全部頻道）。";
                            return o;
                        }
                        o.Channel = n;
                    }
                    else
                    {
                        if (n < 10 || n > 10000)
                        {
                            o.Error = "--delay 必須是 10 ~ 10000（毫秒）。";
                            return o;
                        }
                        o.Delay = n;
                    }
                    continue;
                }
                if (a.StartsWith("-", StringComparison.Ordinal))
                {
                    o.Error = "不認得的選項：" + a;
                    return o;
                }
                if (o.Command.Length > 0 && o.Command != "help")
                {
                    o.Error = "多餘的參數：" + a;
                    return o;
                }
                if (o.Command != "help")
                {
                    o.Command = a.ToLowerInvariant();
                }
            }
            return o;
        }
    }

    // ======================================================================
    // Program: menu, commands, exit handling.
    // ======================================================================
    internal static class Program
    {
        private const int RcOk = 0;
        private const int RcError = 1;
        private const int RcUsage = 2;
        private const int RcDeviceLost = 3;
        private const int RcCancelled = 4;

        private static readonly ManualResetEvent StopEvent = new ManualResetEvent(false);
        private static volatile bool modeActive;
        private static volatile Engine currentEngine;
        private static bool justWaited;
        private static string sessionDevice;
        private static NativeConsole.CtrlHandler ctrlHandler;   // static field: keeps the delegate alive

        private static int Main(string[] args)
        {
            int rc = RcError;
            try
            {
                SetupConsole();
                InstallExitHooks();
                string tableError = Keymap.Validate();
                if (tableError != null)
                {
                    Console.WriteLine("內部錯誤：鍵位表不正確（" + tableError + "）。");
                    rc = RcError;
                }
                else
                {
                    rc = Dispatch(Options.Parse(args));
                }
            }
            catch (Exception ex)
            {
                try
                {
                    Console.WriteLine();
                    Console.WriteLine("發生未預期的錯誤：" + ex.Message);
                    Console.WriteLine(ex.ToString());
                }
                catch (Exception)
                {
                }
                rc = RcError;
            }
            finally
            {
                ReleaseCurrent();
                NativeConsole.RestoreMode();
            }

            if (rc != RcOk && NativeConsole.OwnsConsole())
            {
                // Own console window (the exe was double-clicked): keep it open so the message can be read.
                try
                {
                    Console.WriteLine();
                    Console.Write("按 Enter 關閉視窗...");
                    Console.ReadLine();
                }
                catch (Exception)
                {
                }
            }
            return rc;
        }

        // ------------------------------------------------------------------
        // Console setup / exit paths
        // ------------------------------------------------------------------
        private static void SetupConsole()
        {
            try
            {
                Console.OutputEncoding = new UTF8Encoding(false);
            }
            catch (Exception)
            {
            }
            try
            {
                Console.Title = "FF14 key2key";
            }
            catch (Exception)
            {
            }
        }

        private static void InstallExitHooks()
        {
            Console.CancelKeyPress += OnCancelKeyPress;
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            try
            {
                ctrlHandler = new NativeConsole.CtrlHandler(OnConsoleCtrl);
                NativeConsole.SetConsoleCtrlHandler(ctrlHandler, true);
            }
            catch (Exception)
            {
            }
        }

        // Release every key and refuse any further input from the active engine.
        private static void ReleaseCurrent()
        {
            Engine e = currentEngine;
            if (e != null)
            {
                try
                {
                    e.Shutdown();
                }
                catch (Exception)
                {
                }
            }
        }

        private static void OnCancelKeyPress(object sender, ConsoleCancelEventArgs e)
        {
            ReleaseCurrent();
            e.Cancel = true;          // we decide how to end: no abrupt kill, keys are already released
            if (modeActive)
            {
                StopEvent.Set();      // stay alive: the mode loop ends, then we return to the menu / exit normally
                return;
            }
            // Ctrl+C at the menu or at a prompt: leave quietly with exit code 0.
            NativeConsole.RestoreMode();
            Environment.Exit(0);
        }

        private static void OnProcessExit(object sender, EventArgs e)
        {
            ReleaseCurrent();
            NativeConsole.RestoreMode();
        }

        private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            ReleaseCurrent();
        }

        // CTRL_C_EVENT 0, CTRL_BREAK_EVENT 1, CTRL_CLOSE_EVENT 2, CTRL_LOGOFF_EVENT 5, CTRL_SHUTDOWN_EVENT 6.
        // Runs on a separate thread; for the console-window close button this is our last chance to free the keys.
        private static bool OnConsoleCtrl(uint ctrlType)
        {
            ReleaseCurrent();
            return false;   // let the normal handlers (CancelKeyPress / default) continue
        }

        private static void BeginMode(Engine engine)
        {
            StopEvent.Reset();
            currentEngine = engine;
            modeActive = true;
            NativeConsole.DisableQuickEdit();
            FlushInput();
        }

        private static void EndMode()
        {
            modeActive = false;
            currentEngine = null;
            NativeConsole.RestoreMode();
            FlushInput();
        }

        // Throw away keystrokes that were typed into the console while we were injecting keys.
        private static void FlushInput()
        {
            try
            {
                while (Console.KeyAvailable)
                {
                    Console.ReadKey(true);
                }
            }
            catch (Exception)
            {
            }
        }

        // ReadLine that does not swallow Ctrl+C (while reading, Ctrl+C just ends the program).
        private static string ReadLineSafe()
        {
            bool was = modeActive;
            modeActive = false;
            try
            {
                return Console.ReadLine();
            }
            finally
            {
                modeActive = was;
            }
        }

        private static void WaitEnter()
        {
            Console.WriteLine();
            Console.Write("按 Enter 繼續...");
            ReadLineSafe();
            justWaited = true;
        }

        // ------------------------------------------------------------------
        // Dispatch / menu
        // ------------------------------------------------------------------
        private static int Dispatch(Options opt)
        {
            if (opt.Error != null)
            {
                Console.WriteLine(opt.Error);
                PrintUsage();
                return RcUsage;
            }
            if (opt.Command.Length == 0)
            {
                return RunMenu(opt);
            }
            return ExecuteCommand(opt.Command, opt);
        }

        private static int ExecuteCommand(string cmd, Options opt)
        {
            switch (cmd)
            {
                case "run":
                    return CmdRun(opt);
                case "calibrate":
                    return CmdCalibrate(opt);
                case "test":
                    return CmdTest(opt);
                case "monitor":
                    return CmdMonitor(opt);
                case "list":
                    return CmdList();
                case "map":
                    return CmdMap(opt);
                case "help":
                    PrintUsage();
                    return RcOk;
            }
            Console.WriteLine("不認得的指令：" + cmd);
            PrintUsage();
            return RcUsage;
        }

        private static void PrintUsage()
        {
            Console.WriteLine();
            Console.WriteLine("用法：ff14-key2key [指令] [選項]");
            Console.WriteLine("  不輸入指令 = 互動選單（直接雙擊 ff14-key2key.exe 就是這樣）");
            Console.WriteLine();
            Console.WriteLine("指令：");
            Console.WriteLine("  run        開始映射");
            Console.WriteLine("  calibrate  校準最低音");
            Console.WriteLine("  test       測試音階（不需要 MIDI 鍵盤）");
            Console.WriteLine("  monitor    監看 MIDI（不會送出按鍵）");
            Console.WriteLine("  list       列出 MIDI 裝置");
            Console.WriteLine("  map        顯示鍵位對照表");
            Console.WriteLine();
            Console.WriteLine("選項：");
            Console.WriteLine("  --device <編號或名稱片段>  指定 MIDI 裝置");
            Console.WriteLine("  --base <n>                 這次改用的最低音 MIDI 編號（不會存檔）");
            Console.WriteLine("  --channel <n>              只接受這個 MIDI 頻道（0 = 全部）");
            Console.WriteLine("  --verbose                  run 時顯示每個音符");
            Console.WriteLine("  --delay <毫秒>             test 每個音的間隔（預設 250）");
        }

        private static int RunMenu(Options opt)
        {
            while (true)
            {
                Console.WriteLine();
                Console.WriteLine("==========================================");
                Console.WriteLine("  FF14 演奏小工具：MIDI 鍵盤 → 電腦按鍵");
                Console.WriteLine("==========================================");
                PrintStatusLine();
                Console.WriteLine();
                Console.WriteLine("  1  開始映射 (run)");
                Console.WriteLine("  2  校準最低音 (calibrate)");
                Console.WriteLine("  3  測試音階 (test)");
                Console.WriteLine("  4  監看 MIDI (monitor)");
                Console.WriteLine("  5  列出 MIDI 裝置 (list)");
                Console.WriteLine("  6  顯示鍵位對照表 (map)");
                Console.WriteLine("  0  離開");
                Console.WriteLine();
                Console.Write("請輸入數字後按 Enter：");
                string line = ReadLineSafe();
                if (line == null) return RcOk;
                line = line.Trim().ToLowerInvariant();

                string cmd = null;
                switch (line)
                {
                    case "1": cmd = "run"; break;
                    case "2": cmd = "calibrate"; break;
                    case "3": cmd = "test"; break;
                    case "4": cmd = "monitor"; break;
                    case "5": cmd = "list"; break;
                    case "6": cmd = "map"; break;
                    case "0":
                    case "q":
                    case "exit":
                        return RcOk;
                }
                if (cmd == null)
                {
                    Console.WriteLine("看不懂這個選項，請輸入 0 ~ 6 的數字。");
                    continue;
                }

                Console.WriteLine();
                justWaited = false;
                try
                {
                    ExecuteCommand(cmd, opt);
                }
                catch (Exception ex)
                {
                    Console.WriteLine();
                    Console.WriteLine("發生錯誤：" + ex.Message);
                }
                if (!justWaited)
                {
                    Console.WriteLine();
                    Console.Write("按 Enter 回到選單...");
                    ReadLineSafe();
                }
            }
        }

        private static void PrintStatusLine()
        {
            string path = ConfigPath();
            if (!File.Exists(path))
            {
                Console.WriteLine("  目前狀態：尚未校準（第一次請選 1，會自動帶你校準最低音）");
                return;
            }
            Config cfg = Config.Load(path);
            Console.WriteLine("  目前設定：最低音 MIDI {0} ({1})，頻道 {2}",
                cfg.BaseNote, Keymap.NoteName(cfg.BaseNote), cfg.Channel == 0 ? "全部" : cfg.Channel.ToString());
        }

        private static string ConfigPath()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.txt");
        }

        // ------------------------------------------------------------------
        // Device selection
        // ------------------------------------------------------------------
        private static void PrintDevices(List<MidiDeviceInfo> devs)
        {
            for (int i = 0; i < devs.Count; i++)
            {
                Console.WriteLine("  {0}  {1}", devs[i].Index, devs[i].Name);
            }
        }

        private static void NoDeviceMessageAndWait()
        {
            Console.WriteLine("找不到任何 MIDI 輸入裝置。");
            Console.WriteLine("  1. 請確認 MIDI 鍵盤的 USB 線有接好，換一個 USB 孔試試看。");
            Console.WriteLine("  2. 請關閉其他正在使用 MIDI 鍵盤的程式（例如音樂軟體 DAW、其他 MIDI 工具）。");
            Console.WriteLine("     Windows 的 MIDI 輸入通常一次只能給一個程式使用。");
            WaitEnter();
        }

        // spec: numeric index, else case-insensitive name substring
        private static MidiDeviceInfo FindDevice(List<MidiDeviceInfo> devs, string spec, bool allowIndex)
        {
            spec = spec.Trim();
            if (spec.Length == 0) return null;
            int idx;
            if (allowIndex && int.TryParse(spec, NumberStyles.Integer, CultureInfo.InvariantCulture, out idx))
            {
                if (idx >= 0 && idx < devs.Count) return devs[idx];
            }
            for (int i = 0; i < devs.Count; i++)
            {
                if (devs[i].Name.IndexOf(spec, StringComparison.OrdinalIgnoreCase) >= 0) return devs[i];
            }
            return null;
        }

        // Returns null when no device could be chosen (a message has been printed).
        private static MidiDeviceInfo SelectDevice(Options opt, Config cfg, List<MidiDeviceInfo> devs)
        {
            if (devs.Count == 0)
            {
                NoDeviceMessageAndWait();
                return null;
            }

            // 1. --device
            if (!string.IsNullOrEmpty(opt.Device))
            {
                MidiDeviceInfo d = FindDevice(devs, opt.Device, true);
                if (d == null)
                {
                    Console.WriteLine("找不到符合「{0}」的 MIDI 裝置。目前有這些裝置：", opt.Device);
                    PrintDevices(devs);
                }
                return d;
            }

            // 2. device= from config.txt
            if (!string.IsNullOrEmpty(cfg.Device))
            {
                MidiDeviceInfo d = FindDevice(devs, cfg.Device, false);
                if (d != null) return d;
                Console.WriteLine("（config.txt 裡的 device={0} 找不到對應的裝置，改用自動選擇。）", cfg.Device);
            }

            // 3. exactly one device
            if (devs.Count == 1) return devs[0];

            // 4. exactly one name that contains "x3" or "midiplus"
            MidiDeviceInfo found = null;
            int matches = 0;
            for (int i = 0; i < devs.Count; i++)
            {
                string lower = devs[i].Name.ToLowerInvariant();
                if (lower.IndexOf("x3", StringComparison.Ordinal) >= 0 || lower.IndexOf("midiplus", StringComparison.Ordinal) >= 0)
                {
                    found = devs[i];
                    matches++;
                }
            }
            if (matches == 1) return found;

            // 4b. the device chosen earlier in this session
            if (!string.IsNullOrEmpty(sessionDevice))
            {
                for (int i = 0; i < devs.Count; i++)
                {
                    if (string.Equals(devs[i].Name, sessionDevice, StringComparison.Ordinal)) return devs[i];
                }
            }

            // 5. ask
            Console.WriteLine("找到多個 MIDI 裝置：");
            PrintDevices(devs);
            while (true)
            {
                Console.Write("請輸入要使用的裝置編號（直接按 Enter 取消）：");
                string s = ReadLineSafe();
                if (s == null) return null;
                s = s.Trim();
                if (s.Length == 0) return null;
                int idx;
                if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out idx) && idx >= 0 && idx < devs.Count)
                {
                    sessionDevice = devs[idx].Name;
                    return devs[idx];
                }
                Console.WriteLine("編號不正確，請輸入 0 ~ {0}。", devs.Count - 1);
            }
        }

        // ------------------------------------------------------------------
        // list / map
        // ------------------------------------------------------------------
        private static int CmdList()
        {
            List<MidiDeviceInfo> devs = MidiIn.ListDevices();
            if (devs.Count == 0)
            {
                NoDeviceMessageAndWait();
                return RcError;
            }
            Console.WriteLine("MIDI 輸入裝置（編號  名稱）：");
            PrintDevices(devs);
            return RcOk;
        }

        private static int CmdMap(Options opt)
        {
            Config cfg = Config.Load(ConfigPath());
            int baseNote = (opt.BaseNote >= 0) ? opt.BaseNote : cfg.BaseNote;
            Console.WriteLine("FF14 鍵位對照表（最低音 = MIDI {0} {1}）", baseNote, Keymap.NoteName(baseNote));
            Console.WriteLine();
            Console.WriteLine("偏移  MIDI  音名  簡譜    電腦鍵");
            for (int off = 0; off < Keymap.KeyCount; off++)
            {
                Console.WriteLine("{0,-6}{1,-6}{2,-6}{3,-8}{4}",
                    off, baseNote + off, Keymap.NoteName(baseNote + off), Keymap.Degree(off), Keymap.Keys[off]);
            }
            return RcOk;
        }

        // ------------------------------------------------------------------
        // calibrate
        // ------------------------------------------------------------------
        private static int CmdCalibrate(Options opt)
        {
            Config cfg = Config.Load(ConfigPath());
            List<MidiDeviceInfo> devs = MidiIn.ListDevices();
            MidiDeviceInfo dev = SelectDevice(opt, cfg, devs);
            if (dev == null) return RcError;
            int rc = DoCalibrate(opt, cfg, dev);
            return (rc == RcCancelled) ? RcOk : rc;
        }

        // Returns RcOk when a new base note was saved.
        private static int DoCalibrate(Options opt, Config cfg, MidiDeviceInfo dev)
        {
            int channel = (opt.Channel >= 0) ? opt.Channel : cfg.Channel;
            NoteCatcher catcher = new NoteCatcher(channel);
            MidiIn midi = null;
            BeginMode(null);
            try
            {
                try
                {
                    midi = MidiIn.Open(dev.Index, dev.Name, catcher.Handle);
                }
                catch (MidiException ex)
                {
                    Console.WriteLine(ex.Message);
                    return RcError;
                }

                Console.WriteLine("使用裝置：" + dev.Name);
                while (true)
                {
                    Console.WriteLine();
                    catcher.Clear();
                    Console.WriteLine("請按下鍵盤『最左邊（最低音）』的琴鍵");
                    int low = WaitNote(catcher, midi);
                    if (low < 0) return WaitFailure(low);
                    Console.WriteLine("  收到：MIDI {0} ({1})", low, Keymap.NoteName(low));

                    catcher.Clear();
                    Console.WriteLine("請再按下『最右邊（最高音）』的琴鍵");
                    int high = WaitNote(catcher, midi);
                    if (high < 0) return WaitFailure(high);
                    Console.WriteLine("  收到：MIDI {0} ({1})", high, Keymap.NoteName(high));

                    if (high - low != Keymap.KeyCount - 1)
                    {
                        Console.WriteLine();
                        Console.WriteLine("注意：最高音與最低音相差 {0} 個半音，不是 36。", high - low);
                        Console.WriteLine("  可能原因：鍵盤不是 37 鍵、按錯琴鍵，或兩次按鍵之間改了 Octave / Transpose。");
                        Console.Write("  直接按 Enter = 仍然儲存；輸入 n 再按 Enter = 重新校準：");
                        string answer = ReadLineSafe();
                        if (answer != null && answer.Trim().ToLowerInvariant() == "n")
                        {
                            continue;
                        }
                    }

                    cfg.BaseNote = low;
                    try
                    {
                        cfg.Save(ConfigPath());
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("無法寫入設定檔：" + ex.Message);
                        Console.WriteLine("請把整個資料夾放到可以寫入的位置（例如桌面）再試一次。");
                        return RcError;
                    }
                    Console.WriteLine();
                    Console.WriteLine("校準完成，已儲存到 config.txt：");
                    Console.WriteLine("  最低音 = MIDI {0} ({1})", low, Keymap.NoteName(low));
                    Console.WriteLine("  FF14 的 37 個音對應 MIDI {0} ({1}) ~ {2} ({3})",
                        low, Keymap.NoteName(low), low + 36, Keymap.NoteName(low + 36));
                    Console.WriteLine("提醒：鍵盤上的 Octave / Transpose 請保持在原本的設定，校準後不要再改。");
                    return RcOk;
                }
            }
            finally
            {
                if (midi != null) midi.Close();
                EndMode();
            }
        }

        private static int WaitFailure(int code)
        {
            if (code == -2)
            {
                Console.WriteLine();
                Console.WriteLine("MIDI 裝置中斷了（可能被拔掉或被其他程式搶走）。");
                return RcDeviceLost;
            }
            Console.WriteLine();
            Console.WriteLine("已取消校準。");
            return RcCancelled;
        }

        // Returns a note number (0..127), -1 = Ctrl+C pressed, -2 = device lost.
        private static int WaitNote(NoteCatcher catcher, MidiIn midi)
        {
            int waited = 0;
            while (true)
            {
                int n = catcher.TryTake();
                if (n >= 0) return n;
                if (StopEvent.WaitOne(50)) return -1;
                if (midi.CheckLost()) return -2;
                waited += 50;
                if (waited >= 20000)
                {
                    waited = 0;
                    Console.WriteLine("  （還沒收到琴鍵訊號：請確認有按下琴鍵、USB 有接好，沒有被其他程式佔用；");
                    Console.WriteLine("    如果 config.txt 的 channel 不是 0，也請改成 0。按 Ctrl+C 可取消。）");
                }
            }
        }

        // ------------------------------------------------------------------
        // test
        // ------------------------------------------------------------------
        private static int CmdTest(Options opt)
        {
            int delay = opt.Delay;
            Ff14PostSink sink = new Ff14PostSink();
            if (!sink.Poll())
            {
                Console.WriteLine("找不到 FF14 視窗（請先開遊戲）");
                PrintSimilarWindows();
                return RcError;
            }
            Engine engine = new Engine(sink, Keymap.DefaultBase, 0);
            int rc = RcOk;
            BeginMode(engine);
            try
            {
                Console.WriteLine("測試音階：程式會依序按下 37 個音的電腦按鍵（不需要 MIDI 鍵盤）。");
                Console.WriteLine("按鍵會直接送到 FF14 視窗，不用切換視窗；請先讓 FF14 裝備樂器並進入演奏模式。");
                Console.WriteLine("按 Ctrl+C 可隨時停止。");
                for (int s = 3; s >= 1; s--)
                {
                    Console.WriteLine("  {0} ...", s);
                    if (StopEvent.WaitOne(1000)) return TestCancelled();
                }

                int hold = delay * 6 / 10;
                int rest = delay - hold;
                for (int off = 0; off < Keymap.KeyCount; off++)
                {
                    Console.WriteLine("  偏移 {0,2}  {1,-3}  按鍵 {2}", off, Keymap.NoteName(Keymap.DefaultBase + off), Keymap.Keys[off]);
                    engine.PressOffset(off);
                    bool stop = StopEvent.WaitOne(hold);
                    engine.ReleaseOffset(off);
                    if (stop) return TestCancelled();
                    if (StopEvent.WaitOne(rest)) return TestCancelled();
                }

                Console.WriteLine("  C 大調和弦（按鍵 {0} {1} {2} 同時按；FF14 一次只能發一個音，通常只會聽到其中一個）",
                    Keymap.Keys[12], Keymap.Keys[16], Keymap.Keys[19]);
                engine.PressOffset(12);
                engine.PressOffset(16);
                engine.PressOffset(19);
                bool stopChord = StopEvent.WaitOne(800);
                engine.ReleaseAll();
                if (stopChord) return TestCancelled();

                if (sink.Undelivered > 0)
                {
                    Console.WriteLine("警告：測試途中有按鍵沒有送出去（FF14 視窗不見了，或遊戲沒有收下訊息）。");
                    rc = RcError;
                }
                else
                {
                    Console.WriteLine("測試完成，所有按鍵都已放開。");
                }
            }
            finally
            {
                engine.Shutdown();
                EndMode();
            }
            return rc;
        }

        private static int TestCancelled()
        {
            Console.WriteLine("已停止測試，所有按鍵都已放開。");
            return RcOk;
        }

        // ------------------------------------------------------------------
        // monitor
        // ------------------------------------------------------------------
        private static int CmdMonitor(Options opt)
        {
            Config cfg = Config.Load(ConfigPath());
            List<MidiDeviceInfo> devs = MidiIn.ListDevices();
            MidiDeviceInfo dev = SelectDevice(opt, cfg, devs);
            if (dev == null) return RcError;

            int baseNote = (opt.BaseNote >= 0) ? opt.BaseNote : cfg.BaseNote;
            int channel = (opt.Channel >= 0) ? opt.Channel : cfg.Channel;
            LogQueue log = new LogQueue();
            MidiIn midi = null;
            int rc = RcOk;
            BeginMode(null);
            try
            {
                MidiShortHandler handler = delegate(uint raw)
                {
                    string line = Describe(MidiMessage.Parse(raw), baseNote, channel);
                    if (line != null) log.Add(line);
                };
                try
                {
                    midi = MidiIn.Open(dev.Index, dev.Name, handler);
                }
                catch (MidiException ex)
                {
                    Console.WriteLine(ex.Message);
                    return RcError;
                }

                Console.WriteLine("監看模式（不會送出任何按鍵）");
                Console.WriteLine("  裝置：" + dev.Name);
                Console.WriteLine("  最低音：MIDI {0} ({1})，頻道：{2}", baseNote, Keymap.NoteName(baseNote), ChannelText(channel));
                Console.WriteLine("  請按幾個琴鍵看看。按 Ctrl+C 結束。");
                Console.WriteLine();
                while (!StopEvent.WaitOne(30))
                {
                    if (!Pump(log, null, midi))
                    {
                        Console.WriteLine();
                        Console.WriteLine("MIDI 裝置中斷了（可能被拔掉或被其他程式搶走）。");
                        rc = RcDeviceLost;
                        break;
                    }
                }
            }
            finally
            {
                if (midi != null) midi.Close();
                EndMode();
            }
            log.FlushToConsole();
            Console.WriteLine("已結束監看。");
            return rc;
        }

        // One line of monitor / --verbose output, or null for messages we do not show.
        internal static string Describe(MidiMessage m, int baseNote, int channelFilter)
        {
            if (m.Kind == MidiKind.NoteOn || m.Kind == MidiKind.NoteOff)
            {
                string what = (m.Kind == MidiKind.NoteOn) ? "NoteOn" : "NoteOff";
                string s = string.Format("{0,-7} {1,2} ({2})", what, m.Data1, Keymap.NoteName(m.Data1));
                if (m.Kind == MidiKind.NoteOn) s = s + " vel " + m.Data2.ToString();
                if (channelFilter != 0 && m.Channel != channelFilter)
                {
                    return s + " -> 頻道 " + m.Channel.ToString() + " 不符，忽略";
                }
                int off = Keymap.OffsetForNote(m.Data1, baseNote);
                if (off < 0) return s + " -> 超出範圍";
                return s + " -> offset " + off.ToString() + " -> key " + Keymap.Keys[off];
            }
            if (m.Kind == MidiKind.AllNotesOff)
            {
                return "AllNotesOff (CC " + m.Data1.ToString() + ") -> 全部放開";
            }
            return null;
        }

        private static string ChannelText(int channel)
        {
            return (channel == 0) ? "全部" : channel.ToString();
        }

        // ------------------------------------------------------------------
        // run
        // ------------------------------------------------------------------
        private static int CmdRun(Options opt)
        {
            string path = ConfigPath();
            Config cfg = Config.Load(path);
            List<MidiDeviceInfo> devs = MidiIn.ListDevices();
            MidiDeviceInfo dev = SelectDevice(opt, cfg, devs);
            if (dev == null) return RcError;

            if (!File.Exists(path) && opt.BaseNote < 0)
            {
                Console.WriteLine("第一次使用：還沒有 config.txt，先來校準最低音。");
                int crc = DoCalibrate(opt, cfg, dev);
                if (crc == RcCancelled) return RcOk;
                if (crc != RcOk) return crc;
                cfg = Config.Load(path);
                Console.WriteLine();
            }

            int baseNote = (opt.BaseNote >= 0) ? opt.BaseNote : cfg.BaseNote;
            int channel = (opt.Channel >= 0) ? opt.Channel : cfg.Channel;
            return RunMapping(dev, baseNote, channel, cfg.ChordSpreadMs, opt.Verbose);
        }

        private static int RunMapping(MidiDeviceInfo dev, int baseNote, int channel, int spreadMs, bool verbose)
        {
            Ff14PostSink sink = new Ff14PostSink();
            Engine engine = new Engine(sink, baseNote, channel);
            ChordSpreader spreader = new ChordSpreader(engine, spreadMs);
            LogQueue log = new LogQueue();
            MidiIn midi = null;
            int rc = RcOk;
            BeginMode(engine);
            try
            {
                // Runs on the MIDI thread: no console output here, only the log queue (--verbose) and the engine.
                MidiShortHandler handler = delegate(uint raw)
                {
                    MidiMessage msg = MidiMessage.Parse(raw);
                    if (verbose)
                    {
                        string line = Describe(msg, baseNote, channel);
                        if (line != null) log.Add(line);
                    }
                    spreader.Handle(msg);
                };
                try
                {
                    midi = MidiIn.Open(dev.Index, dev.Name, handler);
                }
                catch (MidiException ex)
                {
                    Console.WriteLine(ex.Message);
                    return RcError;
                }

                bool linked = sink.Poll();
                PrintRunHeader(dev, baseNote, channel, spreadMs, linked);
                while (!StopEvent.WaitOne(50))
                {
                    bool nowLinked = sink.Poll();
                    if (nowLinked != linked)
                    {
                        linked = nowLinked;
                        Console.WriteLine(linked ? "已連上 FF14 視窗" : "FF14 視窗不見了，按鍵暫時被忽略");
                    }
                    if (!Pump(log, engine, midi))
                    {
                        engine.Shutdown();   // free every key first, then talk
                        Console.WriteLine();
                        Console.WriteLine("MIDI 裝置中斷了（可能被拔掉或被其他程式搶走）。所有按鍵都已放開。");
                        Console.WriteLine("請重新插好鍵盤後，再重新開始。");
                        rc = RcDeviceLost;
                        break;
                    }
                }
            }
            finally
            {
                engine.Shutdown();
                spreader.Stop();
                if (midi != null) midi.Close();
                EndMode();
            }
            log.FlushToConsole();
            if (rc == RcOk)
            {
                Console.WriteLine();
                Console.WriteLine("已停止，所有按鍵都已放開。");
            }
            return rc;
        }

        private static void PrintRunHeader(MidiDeviceInfo dev, int baseNote, int channel, int spreadMs, bool linked)
        {
            Console.WriteLine("==========================================");
            Console.WriteLine("  映射中：MIDI 鍵盤 → FF14 按鍵");
            Console.WriteLine("==========================================");
            Console.WriteLine("  裝置：" + dev.Name);
            Console.WriteLine("  最低音：MIDI {0} ({1})，頻道：{2}", baseNote, Keymap.NoteName(baseNote), ChannelText(channel));
            Console.WriteLine("  有效音域：MIDI {0} ({1}) ~ {2} ({3})",
                baseNote, Keymap.NoteName(baseNote), baseNote + 36, Keymap.NoteName(baseNote + 36));
            Console.WriteLine("  和弦錯開：{0}", spreadMs > 0
                ? spreadMs.ToString() + " 毫秒（同時按下的音由低到高錯開送出）"
                : "關閉（FF14 一次只能發一個音；可在 config.txt 設定 chord_spread_ms）");
            Console.WriteLine();
            Console.WriteLine("  按鍵會直接送到 FF14 視窗，不管目前是哪個視窗在前景（實體鍵盤不受影響）。");
            if (linked)
            {
                Console.WriteLine("  已找到 FF14 視窗");
            }
            else
            {
                Console.WriteLine("  目前找不到 FF14 視窗，開啟遊戲後會自動接上");
                PrintSimilarWindows();
            }
            Console.WriteLine("  請讓 FF14 進入演奏模式（視窗不要最小化）。");
            Console.WriteLine("  按 Ctrl+C 結束；不要用滑鼠點擊這個視窗");
            Console.WriteLine("------------------------------------------");
        }

        // Diagnostics only (nothing is ever sent to these windows): windows whose title looks like FF14,
        // shown when the game window was not found, so the detection can be adjusted if the client names differ.
        private static void PrintSimilarWindows()
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
                if (shown == 0)
                {
                    Console.WriteLine("  以下視窗的標題看起來像 FF14，但不符合偵測條件：");
                }
                Console.WriteLine("    標題「{0}」  類別 {1}  PID {2}  程式 {3}", t, visible[i].ClassName, visible[i].Pid, visible[i].ProcessName);
                shown++;
            }
        }

        // Main-thread housekeeping while a mode is running. Returns false when the MIDI device is gone.
        private static bool Pump(LogQueue log, Engine engine, MidiIn midi)
        {
            log.FlushToConsole();
            if (engine != null)
            {
                int[] hints = engine.DrainHints();
                for (int i = 0; i < hints.Length; i++)
                {
                    Console.WriteLine(HintText(hints[i], engine.BaseNote));
                }
            }
            string err = midi.TakeCallbackError();
            if (err != null)
            {
                Console.WriteLine("警告：處理 MIDI 訊息時發生錯誤：" + err);
            }
            return !midi.CheckLost();
        }

        internal static string HintText(int note, int baseNote)
        {
            return string.Format(
                "提示：收到音符 {0} ({1})，超出 FF14 的 37 個音（目前有效範圍 MIDI {2} ~ {3}），已忽略。" +
                "如果這是你最左邊的琴鍵，請用選單的「校準最低音」重新校準；也可以檢查鍵盤上的 Octave（八度）按鈕。",
                note, Keymap.NoteName(note), baseNote, baseNote + 36);
        }
    }
}
