# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

VRCLS-CSharp is a C# .NET 8 WPF application that provides real-time speech recognition, translation, and OSC control for VRChat. It's a complete rewrite of the Python-based VRChat LinguaSync tool, designed for better performance and lower latency.

## Build Commands

```bash
# Build entire solution
dotnet build VRCLS.sln

# Run WPF UI application
dotnet run --project 03.VRCLS.UI/03.VRCLS.UI.csproj

# Run console test application (for audio pipeline testing)
dotnet run --project tests/VRCLS.ConsoleTest/VRCLS.ConsoleTest.csproj

# Clean and restore
dotnet clean VRCLS.sln
dotnet restore VRCLS.sln
```

## Architecture

The solution follows Clean Architecture with MVVM pattern:

### Project Dependencies
```
03.VRCLS.UI (WPF App)
    └── 02.VRCLS.Services (Business Logic)
            └── 01.VRCLS.Core (Interfaces & Models)
```
All projects target .NET 8 and are Windows-only (WASAPI, WPF).

### Key Service Interfaces (01.VRCLS.Core/Interfaces/)
- `IAudioService` - Audio capture from microphone/desktop
- `IVadService` - Voice Activity Detection using Silero VAD
- `IRecognitionService` - Speech recognition (SherpaOnnx)
- `IOscService` - VRChat OSC communication
- `ITranslationService` - Multi-engine translation (Server + Python local)
- `ITtsService` - Text-to-speech synthesis (System.Speech + Server)
- `IHotkeyService` - Global hotkeys and push-to-talk
- `ICommandService` - Voice command processing
- `ISteamVRService` - SteamVR overlay display
- `IBackendAuthService` - Backend API authentication

### Event-Driven Architecture
Services communicate through events defined in `01.VRCLS.Core/Events/`:
- `AudioDataAvailableEvent` - Raw audio data from capture
- `VadStateChangedEvent` - Voice activity state changes
- `TranscriptionResultEvent` - Recognition results
- `TranslationResultEvent` - Translation results

### Audio Pipeline Flow
1. `AudioCaptureService` captures audio via NAudio (WASAPI)
2. `VadService` processes audio through Silero VAD ONNX model
3. `AudioPipelineService` orchestrates the flow
4. `SherpaAsrService` performs speech recognition on voice segments
5. Results are published via events to subscribers

### Service Manager Pattern
Some services use a manager pattern to support multiple backends:
- `TranslationServiceManager` - Routes to `ServerTranslationService` or `PythonTranslationService`
- `TtsServiceManager` - Routes to `SystemSpeechTtsService` or `ServerTtsService`

## Development Guidelines

### Model Files Required
Before running, download these models to a `models/` directory:
- Silero VAD: `silero_vad.onnx` from https://github.com/snakers4/silero-vad
- SherpaOnnx Chinese model: Streaming zipformer from https://github.com/k2-fsa/sherpa-onnx/releases

### Configuration
- Config stored as JSON at `%APPDATA%/VRCLS/config.json`
- Logs stored at `%APPDATA%/VRCLS/logs/`
- Model defined in `01.VRCLS.Core/Models/AppConfig.cs`
- Contains 40+ settings for audio, OSC, translation, VR, and UI

### Threading Model
- Audio capture runs on background threads
- VAD processing uses ThreadPool
- UI updates must use WPF Dispatcher
- OSC communication has its own thread

### Logging
- Uses Serilog with console and file sinks
- Configured in `App.xaml.cs`
- Log levels: Debug for development, Information for production

## Current Migration Status

Last updated: 2025-01-27

### Completed

**Core Audio Pipeline:**
- Audio capture (`AudioCaptureService`) - Microphone and desktop WASAPI capture
- VAD (`VadService`) - Silero VAD ONNX model
- Audio orchestration (`AudioPipelineService`) - Async buffering and event flow
- Opus codec (`OpusCodecService`) - Audio compression

**Speech Recognition:**
- Local recognition (`SherpaAsrService`) - SherpaOnnx streaming
- Remote recognition (`FastApiRecognitionService`) - HTTP API + Bearer Token

**Translation & TTS:**
- Translation (`ServerTranslationService`, `PythonTranslationService`) - Multi-backend
- TTS (`SystemSpeechTtsService`, `ServerTtsService`) - Local + server synthesis

**OSC & Input:**
- OSC client (`OscService`) - Chatbox and Avatar parameter control
- Hotkey service (`HotkeyService`) - Global hotkeys and push-to-talk
- Command service (`CommandService`) - Voice command processing

**VR Integration:**
- SteamVR overlay (`SteamVROverlayService`) - VR text overlay display

**UI:**
- Main window with dual audio monitoring and real-time transcription
- Settings window with full configuration interface
- MVVM ViewModels with CommunityToolkit.Mvvm

### Pending

- VRCBitmapLed display - LED matrix display for VRChat
- OSC server listener - Receive OSC messages from VRChat
- CopyBox clipboard functionality
- OpenAI/LLM integration
- Dark/light theme switching
- Token auto-refresh and re-login
- Usage statistics

## Common Tasks

### Adding a New Service
1. Define interface in `01.VRCLS.Core/Interfaces/`
2. Implement service in `02.VRCLS.Services/` (organize by subdirectory: Audio/, Translation/, Tts/, etc.)
3. Register in DI container (`03.VRCLS.UI/App.xaml.cs` in `ConfigureServices()`)
4. Inject into ViewModel constructor if needed

### Audio Pipeline Modifications
- Capture changes: `02.VRCLS.Services/Audio/AudioCaptureService.cs`
- VAD changes: `02.VRCLS.Services/Audio/VadService.cs`
- Orchestration: `02.VRCLS.Services/Audio/AudioPipelineService.cs`
- Always maintain the event-based communication pattern

### UI Development
- Views: `03.VRCLS.UI/Views/` (XAML)
- ViewModels: `03.VRCLS.UI/ViewModels/`
- Use CommunityToolkit.Mvvm (ObservableObject, RelayCommand)
- Follow Material Design styling

## Important Notes

- Windows-only application (uses WASAPI, WPF)
- All audio processing must be 16kHz, 16-bit, mono for SherpaOnnx
- VAD model expects 512-sample frames
- OSC communication defaults to localhost:9000 (VRChat)