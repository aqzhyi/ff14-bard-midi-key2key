# ff14-bard-midi-key2key

[English](README.md) | [正體中文](README-ZH.md)

Play FINAL FANTASY XIV instruments with a MIDI keyboard.

This tool needs no extra software. It does not change the game. It changes the keys of your MIDI keyboard into normal computer keys. It sends the keys to the game in the background. So the game window does not need to be in front. You can read sheet music in another window and play at the same time.

The tool works with a USB MIDI keyboard that has 37 keys. The author uses a MIDIPLUS X3 mini. Each MIDI key becomes one key for FF14 performance mode. An FF14 instrument has exactly 37 notes (C3 to C6, with the black keys). So a 37-key MIDI keyboard fits it one to one. Windows only.

![Key settings in the game (top) and ff14-key2key running (bottom)](docs/screenshot.png)

## Features

- **One MIDI key is one computer key.** All 37 notes have their own keys. The tool does not use Shift or Ctrl. The full list is in [KEYMAP.md](KEYMAP.md).
- **It works when the game is not in front.** The key messages go straight to the FF14 window. You can use your browser while you play. Your normal keyboard still works as usual.
- **The first run helps you set up.** You do not need to know MIDI note numbers.
- **You do not need to install anything.** It uses the C# compiler that is already in Windows. Double-click `build.bat` to make the program.

## USAGE

### What you need

- Windows 10 or 11
- FINAL FANTASY XIV, Traditional Chinese version (the game program is `ffxiv_dx11.exe`)
- A USB MIDI keyboard. A 37-key keyboard is best. Other sizes also work. See "4. First time: calibrate" below.

### 1. Set the keys in the game

This tool only changes "MIDI key to computer key". The keys in the game must be the same as this table. They are also in the screenshot above.

Start performance mode in the game. Open "Instrument Performance Settings" (樂器演奏操作設定), then the "Keyboard" tab (鍵盤操作). **Turn on** "Show and set all scales together" (全音階一同顯示、設定按鍵). Then set the keys like this table:

| Octave | White keys 1 2 3 4 5 6 7 | Black keys 1# 3b 4# 5# 7b |
|---|---|---|
| High | `1` `2` `3` `4` `5` `6` `7` (highest note i = `8`) | `P` `O` `B` `C` `L` |
| Middle | `Q` `W` `E` `R` `T` `Y` `U` | `Z` `M` `V` `K` `N` |
| Low | `A` `S` `D` `F` `G` `H` `J` | `-` `=` `X` `,` `.` |

Keep the "high octave" and "low octave" keys (Shift and Ctrl) as the default. This tool does not use them.

### 2. Get the program

**Easy way: download it.** Open the [Releases page](https://github.com/aqzhyi/ff14-bard-midi-key2key/releases). Download `ff14-key2key.exe`. GitHub builds this file by itself from the source code in this repository. Put it in any folder.

Windows may show a warning ("Windows protected your PC"), because the file has no digital signature. You can check the file: download `ff14-key2key.exe.sha256` from the same page. Then run `certutil -hashfile ff14-key2key.exe SHA256` and compare the result with the number in that file.

**Or build it yourself.**

1. Download this repository (Code, then Download ZIP, then unzip it). Or use `git clone`.
2. Double-click `csharp\build.bat`. If it works, you see `SUCCESS`. The file `csharp\ff14-key2key.exe` is made.

The build uses the C# compiler (`csc.exe`) of .NET Framework. It is already in Windows. You do not need Visual Studio or an SDK.

### 3. Start the program

1. Connect your MIDI keyboard. Close other programs that use MIDI, for example a DAW. In Windows, usually only one program can use a MIDI input at one time.
2. Double-click `ff14-key2key.exe`. Windows may ask "Do you want to allow this app to make changes?". Click "Yes".

   The program always asks for administrator rights. If FF14 runs as administrator, Windows blocks keys from a normal program. It does not show an error.
3. You see a menu. The words in the program are Chinese:

   | Option | What it does |
   |---|---|
   | 1 Start mapping (開始映射) | Use the tool |
   | 2 Calibrate the lowest note (校準最低音) | Set again which MIDI note is the far left key |
   | 3 Test scale (測試音階) | The program presses all 37 keys by itself. Use it to check the keys in the game. You do not need a MIDI keyboard. |
   | 4 Monitor MIDI (監看 MIDI) | It only shows the notes it gets. It does **not** press any key. Use it to find problems. |
   | 5 List MIDI devices (列出 MIDI 裝置) | See if the PC finds your keyboard |
   | 6 Show the key table (顯示鍵位對照表) | Print the table of 37 keys |
   | 0 Exit (離開) | Close the program |

### 4. First time: calibrate

The first time you choose `1`, the program helps you set up. Follow the screen:

1. Press the **lowest** key (the far left key) on your keyboard.
2. Press the **highest** key (the far right key).

The two keys must be exactly 36 semitones apart (a 37-key keyboard). The program checks this. The result is saved in `config.txt` next to the exe. You do not need to do it again. After this, do not change the Octave or Transpose buttons on your keyboard.

If your keyboard does not have 37 keys: press the key you want to use as the lowest C of FF14. Then press the key that is 36 semitones higher.

### 5. Play

1. Type `1` in the menu and press Enter. The screen shows "已找到 FF14 視窗" ("FF14 window found").
2. In FF14, equip an instrument and start performance mode.
3. Play. Keep the program window open. It does not matter which window is in front.
4. To stop, press `Ctrl+C` or close the window. The program lets go of all keys by itself.

Tip: do not click the black program window with the mouse while you play.

### 6. Check (we suggest this)

- Choose `3` (test scale). The program plays from the lowest note to the highest note by itself. If the scale goes up and there are no wrong notes, the keys in the game are the same as the keys in the program.
- Choose `4` (monitor MIDI). When you press a key, the program shows the note and the computer key. It does not send any key.

## Config file `config.txt`

The calibration makes this file. It is next to `ff14-key2key.exe`. Most people do not need to change it.

```
base_note=48   # MIDI note number of the far left key (C3 = 48)
device=        # Part of the MIDI device name. Empty = choose automatically
channel=0      # 0 = all channels
```

## Troubleshooting

- **Nothing happens in the game.** Look at the program window. Does it show "已找到 FF14 視窗" ("FF14 window found")? The program only finds the game itself (`ffxiv_dx11.exe`, window class `FFXIVGAME`). It does not send keys to the launcher or other windows. If it cannot find the game, it shows a list of windows with a title like FF14. Please paste the list in an Issue.
- **The window is found, but there is no sound.** First run option 3 (test scale). Then check these things: the game is in performance mode, you equipped an instrument, and the keys in the game are the same as the table above.
- **No MIDI device is found.** Use another USB port. Unplug the keyboard and plug it in again. Close other programs that use MIDI.
- **The program says "超出範圍" (out of range) when you press a key.** The Octave or Transpose button on the keyboard was changed. Set it back to the setting you used in the calibration, or calibrate again (option 2). If your keyboard has a "smart scale" function, turn it off.
- **The test scale is right, but your playing has wrong notes.** Calibrate again.
- **Zhuyin symbols appear when you press keys.** Change your input method to English mode.
- **The build fails.** Check that `Key2Key.cs`, `app.manifest` and `build.bat` are all in the `csharp\` folder. Paste the screen in an Issue.

## How it works

- The tool reads MIDI input with the Windows `winmm` library. It sends "key down" and "key up" messages to the FF14 window with `PostMessage`. So the window does not need to be in front.
- The tool only sends normal Windows key messages to the game window. It does not read or change the game memory. It does not put code into the game process. It does not play music by itself.
- If the game is not open, the tool ignores the keys. When you open or restart the game, the tool connects again by itself.

## Files

| Path | What it is |
|---|---|
| `README.md` | English README |
| `README-ZH.md` | Traditional Chinese README (正體中文) |
| `csharp\Key2Key.cs` | The source code (one file, C# 5) |
| `csharp\app.manifest` | Asks for administrator rights |
| `csharp\build.bat` | The build script |
| `.github\workflows\release.yml` | Builds the exe and makes the release on GitHub |
| `KEYMAP.md` | The table of 37 keys |
| `docs\screenshot.png` | The screenshot in this README |

The built file `ff14-key2key.exe` and your calibration file `config.txt` are not in version control (see `.gitignore`).

## Disclaimer

Using other software with a game may break the rules of the game. You use this tool at your own risk. This tool only changes keys, but the author cannot promise that the game company will accept it. The author only tested it with a MIDIPLUS X3 mini and the FF14 Traditional Chinese version.

## License

This project uses the [BSD 3-Clause License](LICENSE).
