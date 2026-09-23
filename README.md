# Giga Pisar for Windows

Push-to-talk Russian dictation for Windows 10/11, powered by Sber's open
[GigaAM v3](https://github.com/salute-developers/GigaAM) model. Everything runs
on the CPU, offline; no audio ever leaves the machine.

Hold the dictation key (right Ctrl by default; right Alt, right Shift, Caps
Lock, Scroll Lock or Insert can be chosen in Settings), speak, release: the
text lands where the caret is. By default it is inserted through the clipboard
(Ctrl+V, previous clipboard content restored, nothing goes into Clipboard
History or the cloud clipboard); "type like a keyboard" is available as an
alternative in Settings.

## What the app does on your machine

- Installs a global low-level keyboard hook to see the dictation key before
  other apps do. It compares every key event with the configured key and
  swallows only that key; nothing is stored or logged.
- Captures the default microphone only while the key is held.
- Downloads the model once (about 200 MB) from the
  [giga-pisar-cli](https://github.com/moznoazachem/giga-pisar-cli) releases
  into `%LOCALAPPDATA%\GigaPisar\model` and verifies each file's SHA-256.
- Keeps settings in `%APPDATA%\GigaPisar\settings.json` and a small log
  (take lengths, levels, errors; never text or audio) in
  `%LOCALAPPDATA%\GigaPisar\pisar.log`.
- Optionally keeps the last take as `last.wav` in the same folder for
  troubleshooting (off by default; deleted when the option is turned off).
- Starts with Windows if you left that box checked in the installer; the
  tray menu and Settings can change it. Uninstalling removes the model,
  settings and log.

## Layout

- `src/Core`: recognition engine: log-mel features, ONNX Runtime sessions,
  greedy RNN-T decoding, SentencePiece decoding. A port of `giga_core.py`
  from giga-pisar-cli.
- `src/App`: tray application (WPF + WinForms tray icon): microphone capture,
  keyboard hook on its own message-loop thread, text insertion, overlay,
  settings, model download.
- `installer/setup.iss`: Inno Setup script (Russian and English wizard).
- `tools/make_icon.py`: builds `app.ico` from the macOS iconset.
- `build.sh`: cross-build from macOS over SSH to a Windows machine
  (settings in an untracked `build.local`, see the script header).

## Build

Requires the .NET 10 SDK and Inno Setup 6 on Windows.

```
cd src
dotnet publish -c Release -o ..\dist\app
cd ..\installer
ISCC.exe setup.iss
```

Headless check of the engine against a 16 kHz mono WAV file:

```
set PISAR_MODEL_DIR=C:\path\to\model
GigaPisar.exe --transcribe input.wav result.txt
```

`PISAR_MODEL_DIR` is honoured by this headless mode only; the tray app always
uses `%LOCALAPPDATA%\GigaPisar\model`.

## License

MIT.
