# AGENTS.md - VoiceWin

Guidelines for AI agents working on this WPF speech-to-text application.

## How to work with me

- **Explain things in simple English.** No jargon unless you define it in the same sentence. Assume I want to understand what you did, not just that it works.
- **Don't assume — ask.** If my request has more than one reasonable reading, stop and ask which one I mean. A wrong guess costs more than a question.
- **Tell me when something is over-engineered.** If there's a simpler way to get the same result, say so before you build the complicated version.
- **Tell me when I'm being vague.** If I haven't given you enough to go on, say what's missing instead of filling the gap yourself.
- **Tell me when I'm contradicting myself.** If a new instruction conflicts with something I said earlier or with what's already in the code, flag the conflict and let me pick.
- **Flag destructive actions before doing them.** Deleting files, resetting branches, force-pushing, dropping unpushed commits — tell me exactly what will be lost first.

## Build Commands

```bash
# Restore + Build (Debug)
dotnet restore && dotnet build src/VoiceWin -c Debug

# Release build
dotnet build src/VoiceWin -c Release

# Self-contained single-file EXE (~560MB with ONNX/CUDA, no .NET runtime required)
dotnet publish src/VoiceWin -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

**No test framework configured.** Validate changes manually:
1. Build: `dotnet build src/VoiceWin -c Debug`
2. Run: `src/VoiceWin/bin/Debug/net8.0-windows/VoiceWin.exe`
3. Configure API keys, test recording with Right Alt key

## Project Structure

```
src/VoiceWin/
├── Assets/           # EmbeddedResource (icons, sounds)
├── Models/           # Data classes (AppSettings, TranscriptionResult)
├── Services/         # Business logic (one service per concern)
├── Views/            # WPF windows (XAML + code-behind)
├── App.xaml          # Application entry point
└── VoiceWin.csproj   # .NET 8.0 Windows WPF project
```

## Code Style

### Naming Conventions
| Type | Convention | Example |
|------|------------|---------|
| Classes, methods, properties, events | PascalCase | `TranscriptionOrchestrator` |
| Local variables, parameters | camelCase | `audioData` |
| Private fields | _camelCase | `_isProcessing` |
| Async methods | Async suffix | `TranscribeAsync` |
| Event handlers | On prefix | `OnHotkeyPressed` |

### File Organization
- One class per file (except private nested DTOs)
- Filename = classname
- Namespace = folder path (`VoiceWin.Services`)

### Imports (using statements)
```csharp
// Order: System → Third-party → Project
using System.Net.WebSockets;
using NAudio.Wave;
using VoiceWin.Models;
```
Implicit usings enabled (`<ImplicitUsings>enable</ImplicitUsings>`)—only add explicit when needed.

### Types & Nullability
- Nullable reference types enabled (`<Nullable>enable</Nullable>`)
- Use `?` for nullable types: `string? apiKey`
- **NEVER** use `!` null-forgiving operator without justification
- **NEVER** use `dynamic` or `object` when concrete types are known
- Prefer concrete types over `var` when type isn't obvious

### Error Handling
- Wrap external API calls in try-catch
- Emit errors via `ErrorOccurred` or `StatusChanged` events—don't swallow silently
- Empty catch blocks acceptable ONLY for fire-and-forget operations (audio playback)
- Return early on failure states, don't nest deeply

## Architecture Patterns

### Service Pattern (Single Responsibility)
| Service | Responsibility |
|---------|----------------|
| `AudioRecordingService` | Microphone capture via NAudio |
| `GroqTranscriptionService` | Groq Whisper API (batch) |
| `DeepgramTranscriptionService` | Deepgram batch API |
| `DeepgramStreamingService` | Deepgram WebSocket streaming |
| `VadService` | Silero VAD for silence detection |
| `TextPasteService` | Clipboard operations (STA thread) |
| `SoundService` | Audio feedback playback |
| `SettingsService` | JSON persistence to %APPDATA% |
| `GlobalHotkeyService` | Win32 hotkey registration |
| `GroqLlmService` | AI text enhancement |
| `TrayIconService` | System tray icon generation |

### Orchestrator Pattern
`TranscriptionOrchestrator` wires services together. All cross-service coordination happens here.

### Event-Driven Communication
```csharp
public event EventHandler? RecordingStarted;
public event EventHandler? RecordingStopped;
public event EventHandler<string>? StatusChanged;
public event EventHandler<float>? AudioLevelChanged;
public event EventHandler<bool>? SpeechDetected;
public event EventHandler<TranscriptionResult>? TranscriptionCompleted;
```

## Critical Implementation Details

### 1. Clipboard = STA Thread Required
```csharp
var thread = new Thread(WorkerMethod);
thread.SetApartmentState(ApartmentState.STA);
thread.Start();
```

### 2. Streaming Paste Order
Use `BlockingCollection<string>` with single consumer thread to maintain order.

### 3. Embedded Resources (Single-File EXE)
Assets must be `EmbeddedResource`, not `Content`:
```xml
<EmbeddedResource Include="Assets\sound_start.mp3" />
```
Load with:
```csharp
Assembly.GetExecutingAssembly().GetManifestResourceStream("VoiceWin.Assets.sound_start.mp3");
```

### 4. NAudio Seekable Stream
Copy embedded resource to MemoryStream first:
```csharp
using var memoryStream = new MemoryStream();
stream.CopyTo(memoryStream);
memoryStream.Position = 0;
using var audioFile = new Mp3FileReader(memoryStream);
```

### 5. WebSocket Best Practices
- Set `KeepAliveInterval` for long connections
- Use `CancellationTokenSource` with timeout
- Send `{"type":"CloseStream"}` before closing
- Handle `OperationCanceledException` and `WebSocketException` explicitly

### 6. UI Thread Access
```csharp
Dispatcher.BeginInvoke(() => { /* UI update */ });
```

### 7. Async Void Pitfalls
- Avoid `async void` except for event handlers
- Use flags (`_isAutoStopping`) to prevent duplicate async operations
- Wrap in try-catch since exceptions won't propagate

## Settings

Stored at: `%APPDATA%\VoiceWin\settings.json`

| Setting | Type | Default |
|---------|------|---------|
| `TranscriptionProvider` | string | `"groq"` |
| `HotkeyMode` | string | `"hold"` |
| `HotkeyVirtualKey` | int | `165` (Right Alt) |
| `Language` | string | `"multi"` (auto-detect) |
| `VadEnabled` | bool | `true` |
| `VadThreshold` | float | `0.5` |
| `VadStreamingSilenceTimeoutSeconds` | int | `60` |
| `AiEnhancementEnabled` | bool | `false` |
| `OverlayPosition` | string | `"bottom"` |

## Dependencies

| Package | Purpose |
|---------|---------|
| NAudio 2.2.1 | Audio recording/playback |
| Deepgram 6.6.1 | SDK types (raw WebSocket for streaming) |
| SileroVad 1.3.0 | Voice activity detection (ONNX) |
| Hardcodet.NotifyIcon.Wpf 2.0.1 | System tray icon |
| InputSimulatorPlus 1.0.7 | Keyboard simulation (Ctrl+V) |
| System.Text.Json 9.0.0 | JSON serialization |

## Common Pitfalls

1. **Clipboard in background threads** → Use Win32 API directly, not WPF Clipboard
2. **Clipboard unavailable** → Retry with backoff (10 attempts, 30ms delay)
3. **WebSocket blocking** → Always use async with cancellation tokens
4. **Undisposed services** → Implement IDisposable, unsubscribe events
5. **Assets in single-file** → Use EmbeddedResource, not Content
6. **Streaming auto-stop crash** → Use flag to prevent multiple auto-stop attempts
7. **Large EXE size** → ONNX Runtime CUDA libraries add ~400MB

## Hotkey Modes

| Mode | Behavior | Best For |
|------|----------|----------|
| Hold | Hold to record, release to stop | Batch transcription |
| Toggle | Tap to start, tap again to stop | Streaming mode |
| Hybrid | Hold ≥250ms = hold mode, quick tap = toggle | Mixed usage |

## Version Info

- .NET 8.0 Windows (WPF)
- Target: Windows 10/11 x64
- Current: v1.3.1
