# BPM Measurer

A tool for measuring and editing **BPM timing** of audio files, with waveform/spectrogram visualization and a built-in metronome.

> Run into issues, need a hand, want to report bugs, share suggestions, or talk development? Join our QQ group chat **`868888361`**

▶️ [**Demo Video**](https://www.bilibili.com/video/BV1fD786hE3M)

## Command-line Arguments

```
Bpm Measurer.exe [--language=<lang>] [--audio=<path>] [--notify=<path>] [--parse_config=<path>]
```

| Argument           | Description |
|--------------------|-------------|
| `--audio=<path>`   | Path to an audio file to load on startup |
| `--language=<lang>` | UI language:<br>`en-US` — English<br>`zh-CN` — Chinese<br>Defaults to `zh-CN`. |
| `--notify=<path>`  | See **HachimiDX Integration** below. |
| `--parse_config=<path>` | See **HachimiDX Integration** below. |

## HachimiDX Integration

Both modes below are intended for the host **[HachimiDX](https://github.com/ck2739046/HachimiDX)** and write a JSON file to the `--notify=` path, communicating the result through the process exit code.

### Single `--notify=` (interactive embed mode)

```
Bpm Measurer.exe --audio=<song.wav> --notify=<manifest.json>
```

Launches the GUI so the user can edit timing. On a successful **Config Export**, writes a manifest `{ "config_path": ..., "audio_path": ... }` to the notify path and exits `0`. Closing without exporting exits `1`; a write failure exits `2`.

### `--notify=` + `--parse_config=` (headless export)

```
Bpm Measurer.exe --parse_config=<config.txt> --notify=<out.json>
```

Skips the GUI entirely: parses the config file and immediately writes `{ "global_offset": ..., "timing_points": [ { "beat_index": ..., "bpm": ..., "beats_per_bar": ... } ] }` to the notify path. If `--notify=` is missing, `--parse_config=` is silently ignored and the GUI starts normally.

Exit codes: `0` = parsed and written successfully · `1` = config read/parse failed · `2` = notify write failed · `3` = other unexpected error.

