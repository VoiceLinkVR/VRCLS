# VRCLS

Language: [简体中文](README.md) | English

VRCLS is a Windows desktop client for VRChat live captions, speech recognition, translation, text-to-speech, OSC control, and SteamVR overlay display. This repository contains the C#/.NET 8 WPF implementation.

The current desktop version is no longer just a migration skeleton: the main WPF app, settings UI, audio pipelines, backend auth, update flow, translation routing, TTS routing, OSC output, and optional SteamVR overlay integration are implemented in this codebase.

## Features

- Microphone, desktop loopback, and process-targeted audio capture.
- Silero VAD based speech segmentation.
- Local Sherpa-ONNX recognition when local models are available.
- Remote FastAPI recognition and translation through the configured VRCLS backend.
- Multi-target translation output for microphone and desktop routes.
- Translation engines through backend services, OpenAI-compatible APIs, and the packaged Python `translators` runtime.
- OSC output for VRChat chatbox and avatar parameter control.
- TTS through backend providers, Qwen-style voice aliases, and local Windows `System.Speech`.
- Edge TTS voice catalog from `音色.json`.
- SteamVR text overlay through the minimal OpenVR files committed under `ref/openvr`.
- Photo/OCR translation workflow.
- User center, website authorization flow, account status, donation/entitlement UI, and rate-limit feedback.
- NetSparkle app updates, model catalog updates, and translation profile hot update support.

## Repository Layout

```text
01.VRCLS.Core/        Shared models, interfaces, config, events, helpers
02.VRCLS.Services/    Audio, auth, recognition, translation, TTS, update, OSC, SteamVR services
03.VRCLS.UI/          WPF app, views, view models, resources, UI helpers
docs/                 Design notes and implementation notes
scripts/              Build, publish, installer, update manifest, and translator tooling
tests/                Console and backend smoke test projects
ref/openvr/           Minimal OpenVR files required by this app
ref/translators/      Optional translators submodule pointer
```

## Requirements

- Windows 10/11.
- .NET 8 SDK.
- Visual Studio 2022 or another editor that supports WPF/.NET desktop development.
- Microphone permission and VRChat OSC enabled for the full runtime flow.
- Python is only required when building the packaged translator runtime or release artifacts.
- Inno Setup is only required when building the installer.

## Build

```powershell
dotnet restore VRCLS.sln
dotnet build VRCLS.sln
```

The solution build currently emits nullable warnings from Valve's generated OpenVR C# binding in `ref/openvr/headers/openvr_api.cs`. These warnings are expected and do not block the build.

Run the desktop app:

```powershell
dotnet run --project 03.VRCLS.UI/03.VRCLS.UI.csproj
```

Run the console test project:

```powershell
dotnet run --project tests/VRCLS.ConsoleTest/VRCLS.ConsoleTest.csproj
```

The backend smoke test project is present under `tests/VRCLS.BackendSmoke/`, but it is not currently included in `VRCLS.sln`.

## Models

The repository keeps large recognition model packages out of Git. The app resolves models from several locations:

- `pre-models/` beside the repository or published app.
- `models/` beside the executable.
- configured paths in `%APPDATA%\VRCLS\config.json`.
- model update downloads from the configured update server.

A small Silero VAD model is committed for development fallback. Larger Sherpa-ONNX ASR models are intentionally excluded and should be downloaded through the update flow or placed under `pre-models/`.

## Configuration

Runtime configuration is stored at:

```text
%APPDATA%\VRCLS\config.json
```

Important defaults:

- Backend base URL: `https://www.voicelinkvr.top`
- Static update server: `https://cloudflarestorage.boyqiu001.top/`
- App update feed: `https://cloudflarestorage.boyqiu001.top/app-releases/appcast.xml`
- Default application audio target process: `VRChat`
- Default TTS provider: `qwen`

Do not commit local `.env`, publish presets, generated update buckets, local model directories, logs, or `tools/translator` output. These are ignored by `.gitignore`.

## Publishing

Standard self-contained publish:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\publish.ps1 -Configuration Release -Runtime win-x64 -Zip
```

Installer build:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-installer.ps1 -InternalVersion 0.7.0.123 -DisplayVersion 0.7.0-beta23
```

The publish script builds the Python translator runtime into `tools/translator`, publishes the WPF app, strips large model files from publish output, and optionally restores a bundled VAD model from `pre-models/silero_vad.onnx`.

## Translator Runtime

The app can route translation through a packaged Python runtime based on the `translators` package. To build it manually:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build_translator.ps1
```

Generated files land in `tools/translator/` and are intentionally ignored by Git.

## Update Assets

The update scripts generate appcast, model catalog, and translation profile artifacts for static hosting:

- `scripts/build-update-manifest.ps1`
- `scripts/generate_bucket_layout.py`
- `scripts/generate_bucket_layout_gui.py`

Generated buckets such as `bucket-generated/` are local artifacts and should not be committed.

## License

VRCLS is licensed under the Apache License, Version 2.0. See [LICENSE](LICENSE).

Third-party dependencies, OpenVR files, NuGet packages, and Python packages remain under their own licenses.
