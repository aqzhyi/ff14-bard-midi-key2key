// Tests.cs - tiny self-contained unit tests for Key2Key.cs (no test framework, no NuGet, no SDK).
// Built and run by test.bat with the in-box csc.exe: C# 5 only (same rules as Key2Key.cs).
// Save as UTF-8 WITH BOM, CRLF line endings.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace Ff14Key2Key
{
    // Records every delivered key event as "down 1E" / "up 1E"; can be told to fail the next N calls.
    internal sealed class FakeSink : IKeySink
    {
        private readonly object sync = new object();
        private readonly List<string> events = new List<string>();
        private readonly List<long> stamps = new List<long>();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private int failDowns;
        private int failUps;
        private int downAttempts;
        private int upAttempts;

        public void FailNextDowns(int n) { lock (sync) { failDowns = n; } }
        public void FailNextUps(int n) { lock (sync) { failUps = n; } }
        public int DownAttempts { get { lock (sync) { return downAttempts; } } }
        public int UpAttempts { get { lock (sync) { return upAttempts; } } }

        public bool KeyDown(int scanCode)
        {
            lock (sync)
            {
                downAttempts++;
                if (failDowns > 0) { failDowns--; return false; }
                events.Add("down " + scanCode.ToString("X2"));
                stamps.Add(clock.ElapsedMilliseconds);
                return true;
            }
        }

        public bool KeyUp(int scanCode)
        {
            lock (sync)
            {
                upAttempts++;
                if (failUps > 0) { failUps--; return false; }
                events.Add("up " + scanCode.ToString("X2"));
                stamps.Add(clock.ElapsedMilliseconds);
                return true;
            }
        }

        public List<string> Events()
        {
            lock (sync) { return new List<string>(events); }
        }

        public List<long> Stamps()
        {
            lock (sync) { return new List<long>(stamps); }
        }

        public int Count
        {
            get { lock (sync) { return events.Count; } }
        }

        public void Clear()
        {
            lock (sync) { events.Clear(); stamps.Clear(); downAttempts = 0; upAttempts = 0; }
        }
    }

    internal static class Assert
    {
        public static void True(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        public static void Equal<T>(T expected, T actual, string what)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
            {
                throw new Exception(what + ": expected <" + Show(expected) + "> but was <" + Show(actual) + ">");
            }
        }

        public static void Seq<T>(IList<T> expected, IList<T> actual, string what)
        {
            string e = Join(expected);
            string a = Join(actual);
            if (e != a) throw new Exception(what + ": expected [" + e + "] but was [" + a + "]");
        }

        private static string Show(object o)
        {
            return o == null ? "null" : o.ToString();
        }

        private static string Join<T>(IList<T> list)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < list.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(Show(list[i]));
            }
            return sb.ToString();
        }
    }

    internal static class TestProgram
    {
        private sealed class TestCase
        {
            public string Name;
            public Action Body;
        }

        private static readonly List<TestCase> Tests = new List<TestCase>();

        private static void Add(string name, Action body)
        {
            TestCase t = new TestCase();
            t.Name = name;
            t.Body = body;
            Tests.Add(t);
        }

        // ------------------------------------------------------------------ helpers
        private const int Base = 48;
        private static int Scan(int offset) { return Keymap.ScanCodes[offset]; }
        private static string Down(int offset) { return "down " + Scan(offset).ToString("X2"); }
        private static string Up(int offset) { return "up " + Scan(offset).ToString("X2"); }

        private static uint Raw(int status, int d1, int d2)
        {
            return (uint)(status | (d1 << 8) | (d2 << 16));
        }

        private static MidiMessage Msg(MidiKind kind, int channel, int note)
        {
            MidiMessage m = new MidiMessage();
            m.Kind = kind;
            m.Channel = channel;
            m.Data1 = note;
            m.Data2 = kind == MidiKind.NoteOn ? 100 : 0;
            return m;
        }

        private static MidiMessage On(int note) { return Msg(MidiKind.NoteOn, 1, note); }
        private static MidiMessage Off(int note) { return Msg(MidiKind.NoteOff, 1, note); }
        private static MidiMessage AllOff() { return Msg(MidiKind.AllNotesOff, 1, 123); }

        private static bool WaitFor(Func<bool> condition, int timeoutMs)
        {
            Stopwatch sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (condition()) return true;
                Thread.Sleep(2);
            }
            return condition();
        }

        private static List<T> L<T>(params T[] items)
        {
            return new List<T>(items);
        }

        private static string[] SplitLines(string text)
        {
            return text.Split(new string[] { "\r\n" }, StringSplitOptions.None);
        }

        // Checks that per scan code the events strictly alternate down/up and end released.
        private static void CheckBalanced(List<string> ev, bool strictAlternation)
        {
            Dictionary<string, int> state = new Dictionary<string, int>();
            for (int i = 0; i < ev.Count; i++)
            {
                string[] p = ev[i].Split(' ');
                int cur;
                state.TryGetValue(p[1], out cur);
                int next = (p[0] == "down") ? cur + 1 : cur - 1;
                if (strictAlternation && (next < 0 || next > 1))
                {
                    throw new Exception("key " + p[1] + " out of sequence at event " + i + " (" + ev[i] + ")");
                }
                state[p[1]] = next;
            }
            foreach (KeyValuePair<string, int> kv in state)
            {
                Assert.Equal(0, kv.Value, "key " + kv.Key + " down/up balance");
            }
        }

        // ------------------------------------------------------------------ entry point
        private static int Main(string[] args)
        {
            RegisterKeymap();
            RegisterMidiParse();
            RegisterEngine();
            RegisterChordSpreader();
            RegisterConfig();
            RegisterOptions();
            RegisterThreads();
            RegisterFollow();
            RegisterMore();
            RegisterSysex();

            int passed = 0;
            int failed = 0;
            for (int i = 0; i < Tests.Count; i++)
            {
                TestCase t = Tests[i];
                try
                {
                    t.Body();
                    passed++;
                    Console.WriteLine("PASS " + t.Name);
                }
                catch (Exception ex)
                {
                    failed++;
                    Console.WriteLine("FAIL " + t.Name + ": " + ex.Message);
                }
            }
            Console.WriteLine();
            Console.WriteLine("Total " + Tests.Count + ", passed " + passed + ", failed " + failed);
            return failed == 0 ? 0 : 1;
        }

        // ------------------------------------------------------------------ Keymap
        // Expected computer key per offset, hard-coded from KEYMAP.md.
        private static readonly string[] ExpectedKeys = new string[]
        {
            "A", "-", "S", "=", "D", "F", "X", "G", ",", "H", ".", "J",
            "Q", "Z", "W", "M", "E", "R", "V", "T", "K", "Y", "N", "U",
            "1", "P", "2", "O", "3", "4", "B", "5", "C", "6", "L", "7",
            "8"
        };

        // Independent US-layout set-1 make codes.
        private static int UsScanCode(string key)
        {
            switch (key)
            {
                case "Q": return 0x10; case "W": return 0x11; case "E": return 0x12; case "R": return 0x13;
                case "T": return 0x14; case "Y": return 0x15; case "U": return 0x16; case "O": return 0x18;
                case "P": return 0x19; case "A": return 0x1E; case "S": return 0x1F; case "D": return 0x20;
                case "F": return 0x21; case "G": return 0x22; case "H": return 0x23; case "J": return 0x24;
                case "K": return 0x25; case "L": return 0x26; case "Z": return 0x2C; case "X": return 0x2D;
                case "C": return 0x2E; case "V": return 0x2F; case "B": return 0x30; case "N": return 0x31;
                case "M": return 0x32; case "-": return 0x0C; case "=": return 0x0D; case ",": return 0x33;
                case ".": return 0x34;
                case "1": return 0x02; case "2": return 0x03; case "3": return 0x04; case "4": return 0x05;
                case "5": return 0x06; case "6": return 0x07; case "7": return 0x08; case "8": return 0x09;
            }
            return -1;
        }

        private static void RegisterKeymap()
        {
            Add("Keymap.Validate returns null", delegate { Assert.True(Keymap.Validate() == null, "Validate: " + Keymap.Validate()); });
            Add("Keymap has 37 entries", delegate
            {
                Assert.Equal(37, Keymap.KeyCount, "KeyCount");
                Assert.Equal(37, Keymap.Keys.Length, "Keys.Length");
                Assert.Equal(37, Keymap.ScanCodes.Length, "ScanCodes.Length");
            });
            Add("Keymap.Keys match KEYMAP.md", delegate { Assert.Seq(ExpectedKeys, Keymap.Keys, "Keys"); });
            Add("Keymap.ScanCodes match US-layout scan codes of KEYMAP.md keys", delegate
            {
                for (int i = 0; i < ExpectedKeys.Length; i++)
                {
                    Assert.Equal(UsScanCode(ExpectedKeys[i]), Keymap.ScanCodes[i], "scan code of offset " + i + " (" + ExpectedKeys[i] + ")");
                }
            });
            Add("Keymap.OffsetForNote boundaries", delegate
            {
                Assert.Equal(-1, Keymap.OffsetForNote(47, 48), "base-1");
                Assert.Equal(0, Keymap.OffsetForNote(48, 48), "base");
                Assert.Equal(36, Keymap.OffsetForNote(84, 48), "base+36");
                Assert.Equal(-1, Keymap.OffsetForNote(85, 48), "base+37");
            });
            Add("Keymap.NoteName", delegate
            {
                Assert.Equal("C-1", Keymap.NoteName(0), "0");
                Assert.Equal("C3", Keymap.NoteName(48), "48");
                Assert.Equal("C4", Keymap.NoteName(60), "60");
                Assert.Equal("C#4", Keymap.NoteName(61), "61");
                Assert.Equal("G9", Keymap.NoteName(127), "127");
                Assert.Equal("?", Keymap.NoteName(-1), "-1");
                Assert.Equal("?", Keymap.NoteName(128), "128");
            });
            Add("Keymap.Degree", delegate
            {
                Assert.Equal("1", Keymap.Degree(0), "0");
                Assert.Equal("1#", Keymap.Degree(1), "1");
                Assert.Equal("3b", Keymap.Degree(3), "3");
                Assert.Equal("7", Keymap.Degree(11), "11");
                Assert.Equal("1", Keymap.Degree(12), "12");
                Assert.Equal("7b", Keymap.Degree(34), "34");
                Assert.Equal("1(high)", Keymap.Degree(36), "36");
            });
        }

        // ------------------------------------------------------------------ MidiMessage.Parse
        private static void CheckParse(uint raw, MidiKind kind, int channel, int d1, int d2, string what)
        {
            MidiMessage m = MidiMessage.Parse(raw);
            Assert.Equal(kind, m.Kind, what + " kind");
            Assert.Equal(channel, m.Channel, what + " channel");
            Assert.Equal(d1, m.Data1, what + " data1");
            Assert.Equal(d2, m.Data2, what + " data2");
        }

        private static void RegisterMidiParse()
        {
            Add("Midi: note on", delegate { CheckParse(Raw(0x90, 60, 100), MidiKind.NoteOn, 1, 60, 100, "90 3C 64"); });
            Add("Midi: note on velocity 0 is note off", delegate { CheckParse(Raw(0x90, 60, 0), MidiKind.NoteOff, 1, 60, 0, "90 3C 00"); });
            Add("Midi: note off", delegate { CheckParse(Raw(0x80, 60, 64), MidiKind.NoteOff, 1, 60, 64, "80 3C 40"); });
            Add("Midi: channel 1 and 16", delegate
            {
                Assert.Equal(1, MidiMessage.Parse(Raw(0x90, 60, 1)).Channel, "status 90");
                Assert.Equal(16, MidiMessage.Parse(Raw(0x9F, 60, 1)).Channel, "status 9F");
                Assert.Equal(16, MidiMessage.Parse(Raw(0x8F, 60, 1)).Channel, "status 8F");
            });
            Add("Midi: CC 123 and CC 120 are AllNotesOff", delegate
            {
                Assert.Equal(MidiKind.AllNotesOff, MidiMessage.Parse(Raw(0xB0, 123, 0)).Kind, "CC123");
                Assert.Equal(MidiKind.AllNotesOff, MidiMessage.Parse(Raw(0xB0, 120, 0)).Kind, "CC120");
                Assert.Equal(3, MidiMessage.Parse(Raw(0xB2, 123, 0)).Channel, "CC123 channel");
            });
            Add("Midi: other CC is Other", delegate
            {
                Assert.Equal(MidiKind.Other, MidiMessage.Parse(Raw(0xB0, 1, 50)).Kind, "CC1");
                Assert.Equal(MidiKind.Other, MidiMessage.Parse(Raw(0xB0, 64, 127)).Kind, "CC64");
                Assert.Equal(MidiKind.Other, MidiMessage.Parse(Raw(0xB0, 121, 0)).Kind, "CC121");
            });
            Add("Midi: program change, pitch bend, channel pressure are Other", delegate
            {
                Assert.Equal(MidiKind.Other, MidiMessage.Parse(Raw(0xC0, 5, 0)).Kind, "program change");
                Assert.Equal(MidiKind.Other, MidiMessage.Parse(Raw(0xE0, 0, 64)).Kind, "pitch bend");
                Assert.Equal(MidiKind.Other, MidiMessage.Parse(Raw(0xD0, 30, 0)).Kind, "channel pressure");
                Assert.Equal(MidiKind.Other, MidiMessage.Parse(Raw(0xA0, 60, 30)).Kind, "poly pressure");
            });
            Add("Midi: system messages are Other with channel 0", delegate
            {
                int[] statuses = new int[] { 0xF0, 0xF8, 0xFE, 0xFF };
                for (int i = 0; i < statuses.Length; i++)
                {
                    MidiMessage m = MidiMessage.Parse(Raw(statuses[i], 60, 100));
                    Assert.Equal(MidiKind.Other, m.Kind, "kind of " + statuses[i].ToString("X2"));
                    Assert.Equal(0, m.Channel, "channel of " + statuses[i].ToString("X2"));
                }
            });
            Add("Midi: data byte as status is Other", delegate
            {
                MidiMessage m = MidiMessage.Parse(Raw(0x40, 60, 100));
                Assert.Equal(MidiKind.Other, m.Kind, "kind");
                Assert.Equal(0, m.Channel, "channel");
            });
        }

        // ------------------------------------------------------------------ Engine
        private static void RegisterEngine()
        {
            Add("Engine: note on/off sends the right scan codes", delegate
            {
                FakeSink s = new FakeSink();
                Engine e = new Engine(s, Base, 0);
                e.Handle(On(48));
                e.Handle(On(60));
                e.Handle(Off(48));
                e.Handle(Off(60));
                e.Handle(On(84));
                e.Handle(Off(84));
                Assert.Seq(L("down 1E", "down 10", "up 1E", "up 10", "down 09", "up 09"), s.Events(), "events");
            });
            Add("Engine: HandleRaw decodes and dispatches", delegate
            {
                FakeSink s = new FakeSink();
                Engine e = new Engine(s, Base, 0);
                e.HandleRaw(Raw(0x90, 48, 100));
                e.HandleRaw(Raw(0x90, 48, 0));
                Assert.Seq(L(Down(0), Up(0)), s.Events(), "events");
            });
            Add("Engine: BaseNote property", delegate { Assert.Equal(40, new Engine(new FakeSink(), 40, 0).BaseNote, "BaseNote"); });
            Add("Engine: note on while held retriggers (up then down)", delegate
            {
                FakeSink s = new FakeSink();
                Engine e = new Engine(s, Base, 0);
                e.Handle(On(50));
                e.Handle(On(50));
                Assert.Seq(L(Down(2), Up(2), Down(2)), s.Events(), "events");
                e.Handle(Off(50));
                Assert.Seq(L(Down(2), Up(2), Down(2), Up(2)), s.Events(), "events after off");
            });
            Add("Engine: note off for a key not down sends nothing", delegate
            {
                FakeSink s = new FakeSink();
                Engine e = new Engine(s, Base, 0);
                e.Handle(Off(50));
                Assert.Equal(0, s.Count, "events");
            });
            Add("Engine: out-of-range notes ignored, hints reported once per note", delegate
            {
                FakeSink s = new FakeSink();
                Engine e = new Engine(s, Base, 0);
                e.Handle(On(47));
                e.Handle(On(47));
                e.Handle(On(85));
                e.Handle(Off(47));
                Assert.Equal(0, s.Count, "events");
                Assert.Seq(L(47, 85), L<int>(e.DrainHints()), "hints");
                Assert.Equal(0, e.DrainHints().Length, "second drain");
                e.Handle(On(47));
                Assert.Equal(0, e.DrainHints().Length, "47 already reported");
                e.Handle(On(10));
                Assert.Seq(L(10), L<int>(e.DrainHints()), "new note");
            });
            Add("Engine: channel filter", delegate
            {
                FakeSink s = new FakeSink();
                Engine e = new Engine(s, Base, 3);
                e.Handle(Msg(MidiKind.NoteOn, 1, 48));
                e.Handle(Msg(MidiKind.NoteOn, 3, 49));
                e.Handle(Msg(MidiKind.NoteOff, 3, 49));
                e.Handle(Msg(MidiKind.NoteOff, 1, 48));
                Assert.Seq(L(Down(1), Up(1)), s.Events(), "events");
            });
            Add("Engine: channel 0 accepts all channels", delegate
            {
                FakeSink s = new FakeSink();
                Engine e = new Engine(s, Base, 0);
                e.Handle(Msg(MidiKind.NoteOn, 16, 48));
                Assert.Seq(L(Down(0)), s.Events(), "events");
            });
            Add("Engine: Other messages ignored", delegate
            {
                FakeSink s = new FakeSink();
                Engine e = new Engine(s, Base, 0);
                e.HandleRaw(Raw(0xB0, 1, 50));
                e.HandleRaw(Raw(0xE0, 0, 64));
                e.HandleRaw(Raw(0xFE, 0, 0));
                Assert.Equal(0, s.Count, "events");
            });
            Add("Engine: AllNotesOff releases everything held", delegate
            {
                FakeSink s = new FakeSink();
                Engine e = new Engine(s, Base, 0);
                e.Handle(On(48));
                e.Handle(On(52));
                s.Clear();
                e.Handle(AllOff());
                Assert.Seq(L(Up(0), Up(4)), s.Events(), "events");
                s.Clear();
                e.Handle(Off(48));
                e.Handle(AllOff());
                Assert.Equal(0, s.Count, "nothing left to release");
            });
            Add("Engine: ReleaseAll is idempotent", delegate
            {
                FakeSink s = new FakeSink();
                Engine e = new Engine(s, Base, 0);
                e.Handle(On(48));
                e.Handle(On(72));
                e.ReleaseAll();
                e.ReleaseAll();
                Assert.Seq(L(Down(0), Down(24), Up(0), Up(24)), s.Events(), "events");
            });
            Add("Engine: Shutdown releases and ignores later input", delegate
            {
                FakeSink s = new FakeSink();
                Engine e = new Engine(s, Base, 0);
                e.Handle(On(48));
                e.Shutdown();
                Assert.Seq(L(Down(0), Up(0)), s.Events(), "after shutdown");
                e.Handle(On(50));
                e.PressOffset(5);
                e.Handle(Off(50));
                Assert.Equal(2, s.Count, "no more events");
            });
            Add("Engine: failed KeyDown leaves key not down, later NoteOff sends nothing", delegate
            {
                FakeSink s = new FakeSink();
                Engine e = new Engine(s, Base, 0);
                s.FailNextDowns(1);
                e.Handle(On(48));
                e.Handle(Off(48));
                Assert.Equal(0, s.Count, "events");
                Assert.Equal(1, s.DownAttempts, "down attempts");
                Assert.Equal(0, s.UpAttempts, "up attempts");
            });
            Add("Engine: failed KeyUp is retried exactly once", delegate
            {
                FakeSink s = new FakeSink();
                Engine e = new Engine(s, Base, 0);
                e.Handle(On(48));
                s.FailNextUps(1);
                e.Handle(Off(48));
                Assert.Equal(2, s.UpAttempts, "up attempts (1 fail + 1 retry)");
                Assert.Seq(L(Down(0), Up(0)), s.Events(), "events");
            });
            Add("Engine: KeyUp failing twice gives up, key is considered released", delegate
            {
                FakeSink s = new FakeSink();
                Engine e = new Engine(s, Base, 0);
                e.Handle(On(48));
                s.FailNextUps(5);
                e.Handle(Off(48));
                Assert.Equal(2, s.UpAttempts, "up attempts");
                e.Handle(Off(48));
                e.ReleaseAll();
                Assert.Equal(2, s.UpAttempts, "no further attempts");
            });
            Add("Engine: retrigger with failing KeyUp retries once then presses", delegate
            {
                FakeSink s = new FakeSink();
                Engine e = new Engine(s, Base, 0);
                e.Handle(On(48));
                s.FailNextUps(1);
                e.Handle(On(48));
                Assert.Equal(2, s.UpAttempts, "up attempts");
                Assert.Seq(L(Down(0), Up(0), Down(0)), s.Events(), "events");
            });
            Add("Engine: PressOffset / ReleaseOffset bounds", delegate
            {
                FakeSink s = new FakeSink();
                Engine e = new Engine(s, Base, 0);
                e.PressOffset(-1);
                e.PressOffset(37);
                e.ReleaseOffset(-1);
                e.ReleaseOffset(37);
                Assert.Equal(0, s.Count, "out of range ignored");
                e.PressOffset(0);
                e.PressOffset(36);
                e.ReleaseOffset(0);
                e.ReleaseOffset(36);
                Assert.Seq(L(Down(0), Down(36), Up(0), Up(36)), s.Events(), "events");
            });
            Add("Engine: SetPaused(true) releases held keys and ignores input", delegate
            {
                FakeSink s = new FakeSink();
                Engine e = new Engine(s, Base, 0);
                e.Handle(On(48));
                e.SetPaused(true);
                Assert.True(e.IsPaused, "IsPaused");
                Assert.Seq(L(Down(0), Up(0)), s.Events(), "release on pause");
                e.Handle(On(50));
                Assert.Equal(2, s.Count, "input ignored while paused");
            });
            Add("Engine: SetPaused(false) resumes; held-through-pause key sends no stray up", delegate
            {
                FakeSink s = new FakeSink();
                Engine e = new Engine(s, Base, 0);
                e.Handle(On(48));
                e.SetPaused(true);
                e.SetPaused(false);
                Assert.True(!e.IsPaused, "IsPaused");
                s.Clear();
                e.Handle(Off(48));
                Assert.Equal(0, s.Count, "stray key-up");
                e.Handle(On(50));
                e.Handle(Off(50));
                Assert.Seq(L(Down(2), Up(2)), s.Events(), "resumed events");
            });
        }

        // ------------------------------------------------------------------ ChordSpreader
        private static void RegisterChordSpreader()
        {
            Add("ChordSpreader: spread 0 passes note on synchronously", delegate
            {
                FakeSink s = new FakeSink();
                Engine e = new Engine(s, Base, 0);
                ChordSpreader c = new ChordSpreader(e, 0);
                try
                {
                    c.Handle(On(48));
                    Assert.Seq(L(Down(0)), s.Events(), "right after Handle");
                    c.Handle(Off(48));
                    Assert.Seq(L(Down(0), Up(0)), s.Events(), "after off");
                }
                finally { c.Stop(); }
            });
            Add("ChordSpreader: SpreadMs is clamped", delegate
            {
                ChordSpreader c = new ChordSpreader(new Engine(new FakeSink(), Base, 0), 0);
                try
                {
                    c.SpreadMs = 5000;
                    Assert.Equal(ChordSpreader.MaxSpreadMs, c.SpreadMs, "upper clamp");
                    c.SpreadMs = -3;
                    Assert.Equal(0, c.SpreadMs, "lower clamp");
                }
                finally { c.Stop(); }
            });
            Add("ChordSpreader: chord comes out low to high with spacing", delegate
            {
                FakeSink s = new FakeSink();
                Engine e = new Engine(s, Base, 0);
                ChordSpreader c = new ChordSpreader(e, 30);
                try
                {
                    c.Handle(On(55));
                    c.Handle(On(48));
                    c.Handle(On(52));
                    Assert.True(WaitFor(delegate { return s.Count >= 3; }, 2000), "timed out, events: " + string.Join(", ", s.Events().ToArray()));
                    Assert.Seq(L(Down(0), Down(4), Down(7)), s.Events(), "order");
                    List<long> t = s.Stamps();
                    Assert.True(t[1] - t[0] >= 25, "spacing 1: " + (t[1] - t[0]) + " ms");
                    Assert.True(t[2] - t[1] >= 25, "spacing 2: " + (t[2] - t[1]) + " ms");
                }
                finally { c.Stop(); }
            });
            Add("ChordSpreader: note off before its note on was sent cancels it", delegate
            {
                FakeSink s = new FakeSink();
                Engine e = new Engine(s, Base, 0);
                ChordSpreader c = new ChordSpreader(e, 200);
                try
                {
                    c.Handle(On(48));
                    Assert.True(WaitFor(delegate { return s.Count >= 1; }, 2000), "first note never sounded");
                    c.Handle(On(52));    // due 200 ms after the first one
                    c.Handle(Off(52));   // cancelled while still waiting
                    Thread.Sleep(450);
                    Assert.Seq(L(Down(0)), s.Events(), "events (no down and no up for the cancelled note)");
                }
                finally { c.Stop(); }
            });
            Add("ChordSpreader: AllNotesOff clears pending notes", delegate
            {
                FakeSink s = new FakeSink();
                Engine e = new Engine(s, Base, 0);
                ChordSpreader c = new ChordSpreader(e, 200);
                try
                {
                    c.Handle(On(48));
                    Assert.True(WaitFor(delegate { return s.Count >= 1; }, 2000), "first note never sounded");
                    c.Handle(On(52));
                    c.Handle(AllOff());
                    Thread.Sleep(450);
                    Assert.Seq(L(Down(0), Up(0)), s.Events(), "events");
                }
                finally { c.Stop(); }
            });
            Add("ChordSpreader: Clear drops pending notes", delegate
            {
                FakeSink s = new FakeSink();
                Engine e = new Engine(s, Base, 0);
                ChordSpreader c = new ChordSpreader(e, 200);
                try
                {
                    c.Handle(On(48));
                    Assert.True(WaitFor(delegate { return s.Count >= 1; }, 2000), "first note never sounded");
                    c.Handle(On(52));
                    c.Clear();
                    Thread.Sleep(450);
                    Assert.Seq(L(Down(0)), s.Events(), "events");
                }
                finally { c.Stop(); }
            });
            Add("ChordSpreader: Stop is idempotent and drops pending notes", delegate
            {
                FakeSink s = new FakeSink();
                Engine e = new Engine(s, Base, 0);
                ChordSpreader c = new ChordSpreader(e, 200);
                c.Handle(On(48));
                Assert.True(WaitFor(delegate { return s.Count >= 1; }, 2000), "first note never sounded");
                c.Handle(On(52));
                c.Stop();
                c.Stop();
                Thread.Sleep(450);
                Assert.Seq(L(Down(0)), s.Events(), "events");
            });
        }

        // ------------------------------------------------------------------ Config
        private static void RegisterConfig()
        {
            Add("Config.Parse: defaults", delegate
            {
                Config c = Config.Parse(new string[0]);
                Assert.Equal(48, c.BaseNote, "BaseNote");
                Assert.Equal("", c.Device, "Device");
                Assert.Equal(0, c.Channel, "Channel");
                Assert.Equal(0, c.ChordSpreadMs, "ChordSpreadMs");
            });
            Add("Config.Parse: all keys", delegate
            {
                Config c = Config.Parse(new string[] { "base_note=36", "device=My Keyboard", "channel=5", "chord_spread_ms=40" });
                Assert.Equal(36, c.BaseNote, "BaseNote");
                Assert.Equal("My Keyboard", c.Device, "Device");
                Assert.Equal(5, c.Channel, "Channel");
                Assert.Equal(40, c.ChordSpreadMs, "ChordSpreadMs");
            });
            Add("Config.Parse: BOM on first line", delegate
            {
                Config c = Config.Parse(new string[] { "﻿base_note=40", "channel=2" });
                Assert.Equal(40, c.BaseNote, "BaseNote");
                Assert.Equal(2, c.Channel, "Channel");
            });
            Add("Config.Parse: comments, blanks, spaces, null lines", delegate
            {
                Config c = Config.Parse(new string[] { "# base_note=1", "", "   ", null, "  base_note = 41  " });
                Assert.Equal(41, c.BaseNote, "BaseNote");
            });
            Add("Config.Parse: inline comment after a number", delegate
            {
                Config c = Config.Parse(new string[] { "base_note=50   # lowest key", "channel=3#x", "chord_spread_ms = 25 # ms" });
                Assert.Equal(50, c.BaseNote, "BaseNote");
                Assert.Equal(3, c.Channel, "Channel");
                Assert.Equal(25, c.ChordSpreadMs, "ChordSpreadMs");
            });
            Add("Config.Parse: invalid and out-of-range values ignored", delegate
            {
                Config c = Config.Parse(new string[]
                {
                    "base_note=128", "base_note=-1", "base_note=abc", "base_note=",
                    "channel=17", "channel=-1", "channel=x",
                    "chord_spread_ms=201", "chord_spread_ms=-5", "chord_spread_ms=1.5"
                });
                Assert.Equal(48, c.BaseNote, "BaseNote");
                Assert.Equal(0, c.Channel, "Channel");
                Assert.Equal(0, c.ChordSpreadMs, "ChordSpreadMs");
            });
            Add("Config.Parse: boundary values accepted", delegate
            {
                Config c = Config.Parse(new string[] { "base_note=0", "channel=16", "chord_spread_ms=200" });
                Assert.Equal(0, c.BaseNote, "BaseNote 0");
                Assert.Equal(16, c.Channel, "Channel 16");
                Assert.Equal(200, c.ChordSpreadMs, "spread 200");
                Config d = Config.Parse(new string[] { "base_note=127" });
                Assert.Equal(127, d.BaseNote, "BaseNote 127");
            });
            Add("Config.Parse: invalid value keeps earlier valid one", delegate
            {
                Config c = Config.Parse(new string[] { "base_note=40", "base_note=999" });
                Assert.Equal(40, c.BaseNote, "BaseNote");
            });
            Add("Config.Parse: unknown keys and lines without '=' ignored", delegate
            {
                Config c = Config.Parse(new string[] { "volume=11", "garbage", "=5", "channel=4" });
                Assert.Equal(4, c.Channel, "Channel");
                Assert.Equal(48, c.BaseNote, "BaseNote");
            });
            Add("Config.Parse: keys are case-insensitive", delegate
            {
                Config c = Config.Parse(new string[] { "BASE_NOTE=30", "Channel=2", "CHORD_SPREAD_MS=20", "Device=abc" });
                Assert.Equal(30, c.BaseNote, "BaseNote");
                Assert.Equal(2, c.Channel, "Channel");
                Assert.Equal(20, c.ChordSpreadMs, "ChordSpreadMs");
                Assert.Equal("abc", c.Device, "Device");
            });
            Add("Config.Serialize -> Parse round trip", delegate
            {
                Config c = new Config();
                c.BaseNote = 36;
                c.Device = "USB 鍵盤 MIDI";
                c.Channel = 9;
                c.ChordSpreadMs = 30;
                Config d = Config.Parse(SplitLines(c.Serialize()));
                Assert.Equal(36, d.BaseNote, "BaseNote");
                Assert.Equal("USB 鍵盤 MIDI", d.Device, "Device");
                Assert.Equal(9, d.Channel, "Channel");
                Assert.Equal(30, d.ChordSpreadMs, "ChordSpreadMs");
            });
            Add("Config.Serialize defaults round trip", delegate
            {
                Config d = Config.Parse(SplitLines(new Config().Serialize()));
                Assert.Equal(48, d.BaseNote, "BaseNote");
                Assert.Equal("", d.Device, "Device");
                Assert.Equal(0, d.Channel, "Channel");
                Assert.Equal(0, d.ChordSpreadMs, "ChordSpreadMs");
            });
            Add("Config.Save / Load round trip through a file (BOM)", delegate
            {
                string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ff14k2k-test-" + Guid.NewGuid().ToString("N") + ".txt");
                try
                {
                    Config c = new Config();
                    c.BaseNote = 45;
                    c.Channel = 2;
                    c.ChordSpreadMs = 15;
                    c.Device = "Dev";
                    c.Save(path);
                    byte[] bytes = System.IO.File.ReadAllBytes(path);
                    Assert.True(bytes.Length > 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "file starts with a UTF-8 BOM");
                    Config d = Config.Load(path);
                    Assert.Equal(45, d.BaseNote, "BaseNote");
                    Assert.Equal(2, d.Channel, "Channel");
                    Assert.Equal(15, d.ChordSpreadMs, "ChordSpreadMs");
                    Assert.Equal("Dev", d.Device, "Device");
                }
                finally
                {
                    try { System.IO.File.Delete(path); } catch (Exception) { }
                }
            });
            Add("Config.Load: missing file gives defaults", delegate
            {
                Config c = Config.Load(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ff14k2k-does-not-exist-" + Guid.NewGuid().ToString("N")));
                Assert.Equal(48, c.BaseNote, "BaseNote");
            });
        }

        // ------------------------------------------------------------------ Options
        private static void RegisterOptions()
        {
            Add("Options.Parse: no arguments", delegate
            {
                Options o = Options.Parse(new string[0]);
                Assert.Equal("", o.Command, "Command");
                Assert.True(o.Error == null, "Error");
                Assert.Equal(-1, o.BaseNote, "BaseNote");
                Assert.Equal(-1, o.Channel, "Channel");
                Assert.Equal(250, o.Delay, "Delay");
                Assert.True(!o.Verbose, "Verbose");
                Assert.True(o.Device == null, "Device");
            });
            Add("Options.Parse: command is lower-cased", delegate
            {
                Options o = Options.Parse(new string[] { "TEST" });
                Assert.Equal("test", o.Command, "Command");
                Assert.True(o.Error == null, "Error");
            });
            Add("Options.Parse: value options, separate and = forms", delegate
            {
                Options o = Options.Parse(new string[] { "run", "--base", "40", "--channel=3", "--delay", "500", "--device=My Kbd", "-v" });
                Assert.True(o.Error == null, "Error: " + o.Error);
                Assert.Equal("run", o.Command, "Command");
                Assert.Equal(40, o.BaseNote, "BaseNote");
                Assert.Equal(3, o.Channel, "Channel");
                Assert.Equal(500, o.Delay, "Delay");
                Assert.Equal("My Kbd", o.Device, "Device");
                Assert.True(o.Verbose, "Verbose");
            });
            Add("Options.Parse: option names are case-insensitive, --verbose", delegate
            {
                Options o = Options.Parse(new string[] { "--BASE=41", "--Verbose" });
                Assert.Equal(41, o.BaseNote, "BaseNote");
                Assert.True(o.Verbose, "Verbose");
            });
            Add("Options.Parse: help flags", delegate
            {
                string[] flags = new string[] { "--help", "-h", "-?", "/?" };
                for (int i = 0; i < flags.Length; i++)
                {
                    Assert.Equal("help", Options.Parse(new string[] { flags[i] }).Command, "Command for " + flags[i]);
                }
            });
            Add("Options.Parse: error cases", delegate
            {
                Assert.True(Options.Parse(new string[] { "--base", "128" }).Error != null, "--base 128");
                Assert.True(Options.Parse(new string[] { "--base=-1" }).Error != null, "--base=-1");
                Assert.True(Options.Parse(new string[] { "--channel", "17" }).Error != null, "--channel 17");
                Assert.True(Options.Parse(new string[] { "--delay", "5" }).Error != null, "--delay 5");
                Assert.True(Options.Parse(new string[] { "--delay", "10001" }).Error != null, "--delay 10001");
                Assert.True(Options.Parse(new string[] { "--base", "abc" }).Error != null, "--base abc");
                Assert.True(Options.Parse(new string[] { "--base" }).Error != null, "--base without value");
                Assert.True(Options.Parse(new string[] { "--bogus" }).Error != null, "unknown --bogus");
                Assert.True(Options.Parse(new string[] { "-x" }).Error != null, "unknown -x");
                Assert.True(Options.Parse(new string[] { "run", "extra" }).Error != null, "two commands");
            });
            Add("Options.Parse: valid boundaries", delegate
            {
                Options o = Options.Parse(new string[] { "--base=0", "--channel=0", "--delay=10" });
                Assert.True(o.Error == null, "Error: " + o.Error);
                Assert.Equal(0, o.BaseNote, "BaseNote");
                Assert.Equal(0, o.Channel, "Channel");
                Assert.Equal(10, o.Delay, "Delay");
                Assert.Equal(127, Options.Parse(new string[] { "--base=127" }).BaseNote, "base 127");
                Assert.Equal(10000, Options.Parse(new string[] { "--delay=10000" }).Delay, "delay 10000");
            });
        }

        // ------------------------------------------------------------------ threads
        private static void RegisterThreads()
        {
            Add("Threads: concurrent Handle then ReleaseAll leaves nothing down, events alternate per key", delegate
            {
                FakeSink s = new FakeSink();
                Engine e = new Engine(s, Base, 0);
                int threadCount = 8;
                Thread[] threads = new Thread[threadCount];
                for (int t = 0; t < threadCount; t++)
                {
                    int seed = t + 1;
                    threads[t] = new Thread(delegate()
                    {
                        Random r = new Random(seed);
                        for (int i = 0; i < 3000; i++)
                        {
                            int note = 46 + r.Next(42);   // includes out-of-range notes on both sides
                            int k = r.Next(100);
                            if (k < 50) e.Handle(On(note));
                            else if (k < 97) e.Handle(Off(note));
                            else e.Handle(AllOff());
                        }
                    });
                    threads[t].Start();
                }
                for (int t = 0; t < threadCount; t++) threads[t].Join();
                e.ReleaseAll();
                List<string> ev = s.Events();
                Assert.True(ev.Count > 0, "no events were produced");
                CheckBalanced(ev, true);
            });
            Add("Threads: SetPaused racing with note on leaves nothing down", delegate
            {
                FakeSink s = new FakeSink();
                Engine e = new Engine(s, Base, 0);
                Thread feeder = new Thread(delegate()
                {
                    for (int i = 0; i < 5000; i++) e.Handle(On(48 + (i % 37)));
                });
                feeder.Start();
                for (int i = 0; i < 200; i++)
                {
                    e.SetPaused(true);
                    e.SetPaused(false);
                }
                feeder.Join();
                e.SetPaused(true);   // releases whatever is still held
                CheckBalanced(s.Events(), true);
            });
        }

        // ------------------------------------------------------------------ octave follow
        private static Engine FollowEngine(FakeSink s)
        {
            return new Engine(s, Base, 0, true);
        }

        private static int Downs(List<string> ev)
        {
            int n = 0;
            for (int i = 0; i < ev.Count; i++) if (ev[i].StartsWith("down ")) n++;
            return n;
        }

        private static void RegisterFollow()
        {
            Add("Follow: 3-arg constructor has follow off, out-of-range gives hint and no key", delegate
            {
                FakeSink s = new FakeSink();
                Engine e = new Engine(s, Base, 0);
                Assert.True(!e.FollowOctave, "FollowOctave");
                e.Handle(On(90));
                Assert.Equal(0, s.Count, "events");
                Assert.Seq(L(90), L<int>(e.DrainHints()), "hints");
                Assert.Equal(0, e.DrainMoves().Length, "moves");
                Assert.Equal(48, e.CurrentLow, "CurrentLow");
            });
            Add("Follow: keyboard shifted +12 moves low to 60", delegate
            {
                FakeSink s = new FakeSink();
                Engine e = FollowEngine(s);
                Assert.True(e.FollowOctave, "FollowOctave");
                e.Handle(On(90));
                Assert.Equal(60, e.CurrentLow, "CurrentLow");
                Assert.Seq(L(60), L<int>(e.DrainMoves()), "moves");
                Assert.Seq(L(Down(30)), s.Events(), "events");
                e.Handle(Off(90));
                e.Handle(On(60));
                Assert.Seq(L(Down(30), Up(30), Down(0)), s.Events(), "events after 60");
                Assert.Equal(0, e.OffsetNow(60), "OffsetNow(60)");
                Assert.Equal(-1, e.OffsetNow(48), "OffsetNow(48)");
            });
            Add("Follow: +24 in two steps, then back down to 48", delegate
            {
                FakeSink s = new FakeSink();
                Engine e = FollowEngine(s);
                e.Handle(On(90));
                Assert.Equal(60, e.CurrentLow, "after 90");
                e.Handle(On(100));
                Assert.Equal(72, e.CurrentLow, "after 100");
                e.Handle(On(50));
                Assert.Equal(48, e.CurrentLow, "after 50");
                Assert.Seq(L(60, 72, 48), L<int>(e.DrainMoves()), "moves");
                Assert.Equal(2, e.OffsetNow(50), "OffsetNow(50)");
                Assert.Equal(3, Downs(s.Events()), "one key-down per note on");
            });
            Add("Follow: no hints while follow is on", delegate
            {
                FakeSink s = new FakeSink();
                Engine e = FollowEngine(s);
                int[] notes = new int[] { 0, 127, 90, 10, 60 };
                for (int i = 0; i < notes.Length; i++) e.Handle(On(notes[i]));
                Assert.Equal(0, e.DrainHints().Length, "hints");
                Assert.Equal(notes.Length, Downs(s.Events()), "key-downs");
            });
            Add("Follow: a move releases held keys before the new key-down", delegate
            {
                FakeSink s = new FakeSink();
                Engine e = FollowEngine(s);
                e.Handle(On(48));
                e.Handle(On(52));
                e.Handle(On(90));
                Assert.Seq(L(Down(0), Down(4), Up(0), Up(4), Down(30)), s.Events(), "events");
            });
            Add("Follow: note off of a key held before the move sends nothing and spares later keys", delegate
            {
                FakeSink s = new FakeSink();
                Engine e = FollowEngine(s);
                e.Handle(On(50));           // offset 2 (low 48)
                e.Handle(On(90));           // move to 60, releases offset 2
                e.Handle(On(62));           // offset 2 again, new key
                List<string> before = s.Events();
                e.Handle(Off(50));
                Assert.Seq(before, s.Events(), "Off(50) must send nothing");
                e.Handle(Off(62));
                Assert.Seq(L(Down(2), Up(2), Down(30), Down(2), Up(2)), s.Events(), "events");
            });
            Add("Follow: note off releases the key its own note on pressed", delegate
            {
                FakeSink s = new FakeSink();
                Engine e = FollowEngine(s);
                e.Handle(On(50));
                e.Handle(Off(50));
                Assert.Seq(L(Down(2), Up(2)), s.Events(), "without a move");
                e.Handle(On(50));
                e.Handle(On(90));
                s.Clear();
                e.Handle(Off(50));
                Assert.Equal(0, s.Count, "after a move");
            });
            Add("Follow: transpose +1 ends at 49", delegate
            {
                FakeSink s = new FakeSink();
                Engine e = FollowEngine(s);
                e.Handle(On(85));
                e.Handle(On(49));
                Assert.Equal(49, e.CurrentLow, "CurrentLow");
                Assert.Equal(2, Downs(s.Events()), "key-downs");
                Assert.Equal(0, e.DrainHints().Length, "hints");
            });
            Add("Follow: notes 127 and 0 keep low within 0..MaxLow", delegate
            {
                Assert.Equal(91, Engine.MaxLow, "MaxLow");
                FakeSink s = new FakeSink();
                Engine e = FollowEngine(s);
                e.Handle(On(127));
                Assert.True(e.CurrentLow >= 0 && e.CurrentLow <= Engine.MaxLow, "low after 127: " + e.CurrentLow);
                Assert.Equal(36, e.OffsetNow(127), "127 is the top key");
                e.Handle(On(0));
                Assert.True(e.CurrentLow >= 0 && e.CurrentLow <= Engine.MaxLow, "low after 0: " + e.CurrentLow);
                Assert.Equal(0, e.OffsetNow(0), "0 is the bottom key");
                Assert.Equal(2, Downs(s.Events()), "key-downs");
                Assert.Equal(0, e.DrainHints().Length, "hints");
            });
            Add("Follow: sweeping every note keeps low in range", delegate
            {
                FakeSink s = new FakeSink();
                Engine e = FollowEngine(s);
                for (int n = 0; n < 128; n++)
                {
                    e.Handle(On(n));
                    Assert.True(e.CurrentLow >= 0 && e.CurrentLow <= Engine.MaxLow, "low " + e.CurrentLow + " after note " + n);
                    Assert.True(e.OffsetNow(n) >= 0, "note " + n + " has no key");
                }
                for (int n = 127; n >= 0; n--)
                {
                    e.Handle(On(n));
                    Assert.True(e.CurrentLow >= 0 && e.CurrentLow <= Engine.MaxLow, "low " + e.CurrentLow + " after note " + n);
                    Assert.True(e.OffsetNow(n) >= 0, "note " + n + " has no key");
                }
                Assert.Equal(0, e.DrainHints().Length, "hints");
            });
            Add("Follow: refused when baseNote > MaxLow", delegate
            {
                Assert.True(!new Engine(new FakeSink(), 92, 0, true).FollowOctave, "base 92");
                Assert.True(new Engine(new FakeSink(), 91, 0, true).FollowOctave, "base 91");
                Assert.True(!new Engine(new FakeSink(), 92, 0).FollowOctave, "base 92 3-arg");
                Engine e = new Engine(new FakeSink(), 40, 0);
                e.SetFollowOctave(true);
                Assert.True(e.FollowOctave, "SetFollowOctave(true) at base 40");
                Engine f = new Engine(new FakeSink(), 100, 0);
                f.SetFollowOctave(true);
                Assert.True(!f.FollowOctave, "SetFollowOctave(true) at base 100");
            });
            Add("Follow: SetFollowOctave(false) snaps back to base and releases keys", delegate
            {
                FakeSink s = new FakeSink();
                Engine e = FollowEngine(s);
                e.Handle(On(90));
                e.DrainMoves();
                e.SetFollowOctave(false);
                Assert.True(!e.FollowOctave, "FollowOctave");
                Assert.Equal(48, e.CurrentLow, "CurrentLow");
                Assert.Seq(L(Down(30), Up(30)), s.Events(), "events");
                Assert.Seq(L(48), L<int>(e.DrainMoves()), "moves");
                e.Handle(On(90));
                Assert.Equal(2, s.Count, "no key when follow is off");
                Assert.Seq(L(90), L<int>(e.DrainHints()), "hint again");
            });
            Add("Follow: SetFollowOctave(true) starts following", delegate
            {
                FakeSink s = new FakeSink();
                Engine e = new Engine(s, Base, 0);
                e.SetFollowOctave(true);
                Assert.True(e.FollowOctave, "FollowOctave");
                e.Handle(On(90));
                Assert.Equal(60, e.CurrentLow, "CurrentLow");
            });
            Add("Engine.PickLow table", delegate
            {
                Assert.Equal(60, Engine.PickLow(52, 88, 48, 48), "(52,88,48,48)");
                Assert.Equal(49, Engine.PickLow(49, 49, 60, 48), "(49,49,60,48)");
                Assert.Equal(57, Engine.PickLow(53, 57, 60, 48), "(53,57,60,48)");
                Assert.Equal(0, Engine.PickLow(0, 10, 48, 48), "(0,10,48,48)");
                // tie: 60 and 72 are equally far from 66; the lower one wins
                Assert.Equal(60, Engine.PickLow(56, 76, 66, 48), "tie (56,76,66,48)");
                // no octave multiple in range and current inside it: stay
                Assert.Equal(50, Engine.PickLow(49, 51, 50, 48), "current inside, no octave");
            });
            Add("Threads: follow on, random notes 0..127, ReleaseAll leaves nothing down", delegate
            {
                FakeSink s = new FakeSink();
                Engine e = FollowEngine(s);
                int threadCount = 8;
                Thread[] threads = new Thread[threadCount];
                for (int t = 0; t < threadCount; t++)
                {
                    int seed = 100 + t;
                    threads[t] = new Thread(delegate()
                    {
                        Random r = new Random(seed);
                        for (int i = 0; i < 3000; i++)
                        {
                            int note = r.Next(128);
                            int k = r.Next(100);
                            if (k < 50) e.Handle(On(note));
                            else if (k < 97) e.Handle(Off(note));
                            else e.Handle(AllOff());
                        }
                    });
                    threads[t].Start();
                }
                for (int t = 0; t < threadCount; t++) threads[t].Join();
                e.ReleaseAll();
                List<string> ev = s.Events();
                Assert.True(ev.Count > 0, "no events");
                CheckBalanced(ev, true);
                Assert.True(e.CurrentLow >= 0 && e.CurrentLow <= Engine.MaxLow, "low " + e.CurrentLow);
            });
        }

        // ------------------------------------------------------------------ config follow / Program texts
        private static void RegisterMore()
        {
            Add("Config: follow_octave default true", delegate
            {
                Assert.True(Config.Parse(new string[0]).FollowOctave, "default");
            });
            Add("Config: follow_octave 0 / 1 / invalid", delegate
            {
                Assert.True(!Config.Parse(new string[] { "follow_octave=0" }).FollowOctave, "0");
                Assert.True(Config.Parse(new string[] { "follow_octave=1" }).FollowOctave, "1");
                Assert.True(Config.Parse(new string[] { "follow_octave=2" }).FollowOctave, "2 ignored");
                Assert.True(Config.Parse(new string[] { "follow_octave=abc" }).FollowOctave, "abc ignored");
                Assert.True(!Config.Parse(new string[] { "follow_octave=0", "follow_octave=2" }).FollowOctave, "later invalid keeps 0");
                Assert.True(!Config.Parse(new string[] { "FOLLOW_OCTAVE = 0 # off" }).FollowOctave, "case and comment");
            });
            Add("Config: follow_octave serialize round trip", delegate
            {
                Config c = new Config();
                c.FollowOctave = false;
                Assert.True(!Config.Parse(SplitLines(c.Serialize())).FollowOctave, "false");
                c.FollowOctave = true;
                Assert.True(Config.Parse(SplitLines(c.Serialize())).FollowOctave, "true");
            });
            Add("Config.FollowFor", delegate
            {
                Assert.True(Config.FollowFor(48, 84), "48..84");
                Assert.True(!Config.FollowFor(48, 96), "48..96");
                Assert.True(!Config.FollowFor(100, 136), "100..136");
                Assert.True(Config.FollowFor(91, 127), "91..127");
                Assert.True(!Config.FollowFor(92, 128), "92..128");
                Assert.True(Config.FollowFor(0, 36), "0..36");
            });
            Add("MidiMessage.Parse sets Status", delegate
            {
                Assert.Equal(0x93, MidiMessage.Parse(Raw(0x93, 60, 100)).Status, "93");
                Assert.Equal(0xB0, MidiMessage.Parse(Raw(0xB0, 64, 127)).Status, "B0");
                Assert.Equal(0xF8, MidiMessage.Parse(Raw(0xF8, 0, 0)).Status, "F8");
            });
            Add("Program.DescribeOther: control change", delegate
            {
                string s = Program.DescribeOther(MidiMessage.Parse(Raw(0xB0, 64, 127)));
                Assert.True(s != null && s.Contains("CC 64") && s.Contains("Sustain"), "got: " + s);
                Assert.True(s.Contains("127"), "value missing: " + s);
            });
            Add("Program.DescribeOther: program change, pitch bend, pressure", delegate
            {
                string pc = Program.DescribeOther(MidiMessage.Parse(Raw(0xC0, 5, 0)));
                Assert.True(pc != null && pc.Contains("ProgramChange 5"), "program change: " + pc);
                string pb = Program.DescribeOther(MidiMessage.Parse(Raw(0xE0, 0, 0x40)));
                Assert.True(pb != null && pb.Contains("PitchBend 0"), "pitch bend centre: " + pb);
                string cp = Program.DescribeOther(MidiMessage.Parse(Raw(0xD0, 30, 0)));
                Assert.True(cp != null && cp.Contains("ChannelPressure 30"), "channel pressure: " + cp);
            });
            Add("Program.DescribeOther: system messages", delegate
            {
                Assert.True(Program.DescribeOther(MidiMessage.Parse(Raw(0xF8, 0, 0))) == null, "clock");
                Assert.True(Program.DescribeOther(MidiMessage.Parse(Raw(0xFE, 0, 0))) == null, "active sensing");
                string r = Program.DescribeOther(MidiMessage.Parse(Raw(0xFF, 0, 0)));
                Assert.True(r != null && r.Contains("System 0xFF"), "reset: " + r);
                Assert.True(Program.DescribeOther(MidiMessage.Parse(Raw(0x40, 0, 0))) == null, "data byte");
            });
            Add("Program.Describe: note shows offset and key", delegate
            {
                string s = Program.Describe(MidiMessage.Parse(Raw(0x90, 48, 100)), 48, 0);
                Assert.True(s.Contains("NoteOn") && s.Contains("offset 0") && s.Contains("key A"), "got: " + s);
                string t = Program.Describe(MidiMessage.Parse(Raw(0x90, 49, 100)), 48, 0);
                Assert.True(t.Contains("offset 1") && t.Contains("key -"), "got: " + t);
            });
            Add("Program.MoveText", delegate
            {
                string back = Program.MoveText(48, 48);
                Assert.True(back.Contains("回到校準時的位置"), "back to base: " + back);
                string oct = Program.MoveText(60, 48);
                Assert.True(oct.Contains("八度"), "octave: " + oct);
                string semi = Program.MoveText(49, 48);
                Assert.True(semi.Contains("半音"), "semitone: " + semi);
            });
        }

        // ------------------------------------------------------------------ SysEx description
        private static string Mmc(byte command)
        {
            return Program.DescribeSysex(new byte[] { 0xF0, 0x7F, 0x74, 0x06, command, 0xF7 });
        }

        private static void RegisterSysex()
        {
            Add("Program.DescribeSysex: MIDIPLUS X3 mini Rewind message", delegate
            {
                string s = Mmc(0x05);
                Assert.True(s != null, "null");
                Assert.True(s.Contains("SysEx F0 7F 74 06 05 F7"), "bytes: " + s);
                Assert.True(s.Contains("Rewind"), "name: " + s);
                Assert.True(s.Contains("（不影響按鍵）"), "no-key note: " + s);
            });
            Add("Program.DescribeSysex: MMC command names", delegate
            {
                Assert.True(Mmc(0x04).Contains("Fast Forward"), "04");
                Assert.True(Mmc(0x01).Contains("Stop"), "01");
                Assert.True(Mmc(0x02).Contains("Play"), "02");
                Assert.True(Mmc(0x06).Contains("Record"), "06");
                Assert.True(Mmc(0x37).Contains("0x37"), "37");
            });
            Add("Program.DescribeSysex: null and empty give null", delegate
            {
                Assert.True(Program.DescribeSysex(null) == null, "null");
                Assert.True(Program.DescribeSysex(new byte[0]) == null, "empty");
            });
            Add("Program.DescribeSysex: long non-MMC message is truncated to 16 bytes", delegate
            {
                byte[] d = new byte[20];
                d[0] = 0xF0;
                d[1] = 0x43;
                for (int i = 2; i < 19; i++) d[i] = (byte)(0x20 + i);
                d[19] = 0xF7;
                string s = Program.DescribeSysex(d);
                Assert.True(s.StartsWith("SysEx F0 43 22 23"), "start: " + s);
                Assert.True(s.Contains("2F"), "16th byte shown: " + s);
                Assert.True(!s.Contains(" 30"), "17th byte must not be shown: " + s);
                Assert.True(!s.Contains("F7"), "last byte must not be shown: " + s);
                Assert.True(s.Contains("共 20 bytes"), "total: " + s);
                Assert.True(!s.Contains("MMC"), "not MMC: " + s);
                Assert.True(s.Contains("（不影響按鍵）"), "no-key note: " + s);
            });
        }
    }
}
