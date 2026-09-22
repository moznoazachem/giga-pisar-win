# Giga Pisar for Windows

Push-to-talk Russian dictation for Windows, powered by Sber's open
[GigaAM v3](https://github.com/salute-developers/GigaAM) model. Everything runs
on the CPU, offline; no audio ever leaves the machine.

Hold the dictation key (right Ctrl by default), speak, release: the text is
typed into whatever has the caret.

## Layout

- `src/Core` — recognition engine: log-mel features, ONNX Runtime sessions,
  greedy RNN-T decoding, SentencePiece decoding. A port of `giga_core.py`
  from [giga-pisar-cli](https://github.com/moznoazachem/giga-pisar-cli).
- `src/App` — tray application (WPF + WinForms tray icon): microphone capture,
  low-level keyboard hook, text insertion, overlay, settings, model download.
- `installer/setup.iss` — Inno Setup script.
- `tools/make_icon.py` — builds `app.ico` from the macOS iconset.

## Build

Requires the .NET 10 SDK and Inno Setup 6 on Windows.

```
cd src
dotnet publish -c Release -r win-x64 --self-contained true -o ..\dist\app
cd ..\installer
ISCC.exe setup.iss
```

Headless check of the engine against a WAV file:

```
GigaPisar.exe --transcribe input.wav result.txt
```

The model is downloaded on first run into `%LOCALAPPDATA%\GigaPisar\model`;
set `PISAR_MODEL_DIR` to point elsewhere.

## License

MIT.
