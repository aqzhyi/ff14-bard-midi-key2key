# Changelog

[English](CHANGELOG.md) | [正體中文](CHANGELOG-ZH.md)

All notable changes to this project are written in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project uses [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- **Window version** `ff14-key2key-gui.exe` (WinForms), next to the console version. Every function has a button. One button pauses and resumes. The program can hide in the system tray and keep running. A picture of the 37 keys lights up the keys you press. Only one copy can run at a time, and it supports high DPI. Both versions use the same `config.txt`. Thanks to s04353022. ([#1])
- **Chord spread** (optional, off by default). FF14 plays one note at a time. Notes you press together are sent from low to high, a set number of milliseconds apart, so the highest note keeps sounding. Set `chord_spread_ms` in `config.txt`, or tick 「和弦錯開」 in the window version. ([#1])
- **Octave follow** (on by default). The OCTAVE and TRANSPOSE buttons of a MIDI keyboard move every note but send no message. When a note falls outside the 37-note window, the program moves the window to fit, so you do not need to calibrate again. Set `follow_octave` in `config.txt`, or use the 「自動跟隨八度」 checkbox in the window version. The calibration turns it off for keyboards that are not 37 keys wide.
- **Monitor MIDI shows every message**, not only notes: CC (with names such as Sustain and Modulation), Program Change, Pitch Bend, pressure, system messages and SysEx (MMC commands such as Play and Stop are named). Each one is marked 「（不影響按鍵）」 ("does not press a key").
- **Unit tests**: `csharp\test.bat` builds and runs 95 tests with the in-box C# compiler. A GitHub Actions workflow runs them for every push and pull request.
- `CHANGELOG.md` and `CHANGELOG-ZH.md`.

### Changed

- The release workflow builds and publishes both exe files, each with its own `.sha256` file. ([#1])
- A Note Off now lets go of exactly the key that its own Note On pressed, even if the keyboard was shifted in between.
- The calibration saves whether octave follow fits your keyboard (`follow_octave`).
- The "C major chord" step of the test scale now says that FF14 usually plays only one of the notes. ([#1])

### Fixed

- **The keyboard stopped working after an extra button was pressed.** Many keyboards send SysEx from their buttons (the six buttons of the MIDIPLUS X3 mini send MMC Play, Stop and so on). On Windows 11 with Windows MIDI Services, MIDI input stopped completely after such a message, until the program was restarted ([microsoft/MIDI#957](https://github.com/microsoft/MIDI/issues/957)). The program now always gets SysEx buffers ready before it starts listening.
- With chord spread on, a key could stay down when the same Note On arrived twice before its Note Off.
- Window version: when opening the MIDI device failed with an unexpected error, the chord spread thread and the 1 ms system timer were not released.

## [0.1.0] - 2026-11-02

### Added

- First release: the console program `ff14-key2key.exe`. It maps the 37 keys of a USB MIDI keyboard one to one to the 37 performance keys of FINAL FANTASY XIV (C3 to C6). It does not use Shift or Ctrl. See [KEYMAP.md](KEYMAP.md).
- Keys are posted straight to the FF14 game window (`FFXIVGAME`), so the game does not need to be in front, and your normal keyboard keeps working.
- Menu: start mapping, calibrate the lowest note (the first run guides you), test scale, monitor MIDI, list MIDI devices, show the key table. The same functions are also command-line commands (`ff14-key2key.exe help`).
- `config.txt` next to the exe: `base_note`, `device`, `channel`.
- Every held key is let go on every way out: Ctrl+C, closing the window, an unplugged keyboard, errors.
- Finds the game again by itself when it is opened or restarted, and notices when the keyboard is unplugged.
- Asks for administrator rights, because Windows blocks keys sent to an FF14 that runs as administrator.
- No install needed: `build.bat` uses the C# compiler that is already in Windows.
- GitHub Actions builds the exe and publishes the release with a SHA256 file. A release can also be started by hand.
- README in English and Traditional Chinese.

[Unreleased]: https://github.com/aqzhyi/ff14-bard-midi-key2key/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/aqzhyi/ff14-bard-midi-key2key/releases/tag/v0.1.0
[#1]: https://github.com/aqzhyi/ff14-bard-midi-key2key/pull/1
