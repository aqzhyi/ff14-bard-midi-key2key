# 更新紀錄

[English](CHANGELOG.md) | [正體中文](CHANGELOG-ZH.md)

本專案所有值得注意的變更都會記錄在這個檔案。

格式依照 [Keep a Changelog](https://keepachangelog.com/zh-TW/1.1.0/)，版本號遵循[語意化版本](https://semver.org/lang/zh-TW/)。

## [Unreleased]

## [0.2.0] - 2026-11-06

### 新增

- **視窗版** `ff14-key2key-gui.exe`（WinForms），與主控台版並存：所有功能都有按鈕，可以一鍵暫停／繼續擷取，也能縮到系統匣在背景執行；37 鍵琴鍵圖會亮起按下的鍵；只允許開一個，支援高 DPI。兩個版本共用同一份 `config.txt`。感謝 s04353022。（[#1]）
- **和弦錯開**（選用，預設關閉）：FF14 一次只能發一個音。同時按下的音會由低到高、間隔設定的毫秒數依序送出，最後持續的是最高音。在 `config.txt` 設定 `chord_spread_ms`，或在視窗版勾選「和弦錯開」。（[#1]）
- **自動跟隨八度**（預設開啟）：MIDI 鍵盤的 OCTAVE／TRANSPOSE 按鈕會讓所有音整體平移，卻不會送出任何訊息。現在只要收到落在 37 音範圍外的音，程式就會自動重新對齊，不用重新校準。在 `config.txt` 設定 `follow_octave`，或用視窗版的「自動跟隨八度」勾選框；校準時若鍵盤不是 37 鍵會自動關閉。
- **監看 MIDI 會顯示所有訊息**，不再只有音符：CC（附 Sustain、Modulation 等名稱）、Program Change、Pitch Bend、壓力、系統訊息與 SysEx（MMC 的 Play、Stop 等指令會標出名稱），並標示「（不影響按鍵）」。
- **單元測試**：`csharp\test.bat` 用 Windows 內建的 C# 編譯器編譯並執行 95 個測試；GitHub Actions 會在每次推送和每個 Pull Request 自動執行。
- `CHANGELOG.md` 與 `CHANGELOG-ZH.md`。

### 變更

- 發布流程會同時編譯並發布兩個 exe，各自附上 `.sha256` 檔。（[#1]）
- Note Off 現在只會放開「它自己的 Note On 按下的那個鍵」，即使中間鍵盤被移調也一樣。
- 校準時會一併儲存自動跟隨八度是否適用於你的鍵盤（`follow_octave`）。
- 測試音階最後的「C 大調和弦」會註明 FF14 通常只會聽到其中一個音。（[#1]）

### 修正

- **按了鍵盤上的額外按鈕後，鍵盤就沒反應了。** 很多鍵盤的按鈕會送出 SysEx（MIDIPLUS X3 mini 的 6 顆按鈕會送出 MMC 的 Play、Stop 等指令）。在使用 Windows MIDI Services 的 Windows 11 上，收到這種訊息後整個 MIDI 輸入會停住，直到重開程式（[microsoft/MIDI#957](https://github.com/microsoft/MIDI/issues/957)）。現在程式在開始接收前一定會先準備好 SysEx 緩衝區。
- 開啟和弦錯開時，同一個音的 Note On 在 Note Off 之前連續出現兩次，可能讓按鍵卡住不放。
- 視窗版：開啟 MIDI 裝置時若發生預期外的錯誤，和弦錯開的背景執行緒與 1 毫秒系統計時器不會被釋放。

## [0.1.0] - 2026-11-02

### 新增

- 第一個版本：主控台程式 `ff14-key2key.exe`。把 USB MIDI 鍵盤的 37 個琴鍵一對一對應到 FINAL FANTASY XIV 演奏模式的 37 個按鍵（C3 ~ C6），不使用 Shift / Ctrl。對照表見 [KEYMAP.md](KEYMAP.md)。
- 按鍵直接投遞給 FF14 遊戲視窗（`FFXIVGAME`），遊戲不必在前景，實體鍵盤照常使用。
- 選單：開始映射、校準最低音（第一次使用會自動帶你校準）、測試音階、監看 MIDI、列出 MIDI 裝置、顯示鍵位對照表；同樣的功能也可以用命令列指令執行（`ff14-key2key.exe help`）。
- exe 旁的 `config.txt`：`base_note`、`device`、`channel`。
- 所有結束方式都會放開按住的鍵：Ctrl+C、關閉視窗、鍵盤被拔掉、發生錯誤。
- 遊戲開啟或重開後會自動接上；鍵盤被拔掉時會偵測到。
- 要求系統管理員權限，因為 FF14 以系統管理員執行時，Windows 會擋掉一般權限程式送來的按鍵。
- 不用安裝：`build.bat` 使用 Windows 內建的 C# 編譯器。
- GitHub Actions 自動編譯 exe 並發布 Release，附 SHA256 檔；也可以手動觸發發布。
- 英文與正體中文說明文件。

[Unreleased]: https://github.com/aqzhyi/ff14-bard-midi-key2key/compare/v0.2.0...HEAD
[0.2.0]: https://github.com/aqzhyi/ff14-bard-midi-key2key/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/aqzhyi/ff14-bard-midi-key2key/releases/tag/v0.1.0
[#1]: https://github.com/aqzhyi/ff14-bard-midi-key2key/pull/1
