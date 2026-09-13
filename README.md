# NoniPilot

> "Your Voice. Your Vision. Your Computer."

NoniPilot is a personal AI agent for Windows: voice commands, screen vision, and hand
gestures, operating the desktop through the same Observe → Plan → Act → Verify → Recover
loop a careful human operator would use. Full product spec:
`NoniPilot_Complete_Product_Roadmap_and_Requirements (1).docx` in this folder.

## Status

Phase 0 (foundation) + a working Phase 1 vertical slice, plus a first cut of Phase 1's voice
input/output and Phase 4's gesture control. What exists right now:

- The full solution/project skeleton from the roadmap's recommended repository structure.
- Domain models and every service interface (section 13/14) the later phases build against.
- **A real, working agent loop**: type a command, Claude (Opus 5) plans it, calls tools
  mapped 1:1 onto real Windows services (file system, applications, mouse/keyboard), every
  tool call is risk-classified and policy-gated *before* it runs, and results are verified
  and audited.
- Real (not stubbed) implementations of: file/folder operations, mouse/keyboard/window
  control via Win32 SendInput, application launch/focus/close, SQLite audit log, the
  4-tier risk/policy engine, and a WPF shell with a command bar and a hard Emergency Stop.
- **Voice**: push-to-talk ("Hold to Talk" button) mic capture, speech-to-text (Groq Whisper
  if `GROQ_API_KEY` is set, else the offline Windows Speech engine), and spoken responses via
  Windows' built-in offline text-to-speech. No wake-word mode yet - push-to-talk only.
- **Gesture**: real webcam capture + a MediaPipe hand-landmark model (ONNX Runtime) +
  geometry-based gesture classification, opened from the "Gesture Center" button. Point
  (cursor), pinch (click/drag), open palm (release), and fist (emergency stop) are wired to
  real mouse actions through the exact same `IComputerControlService` as everything else.
  Thumbs up/down are recognized but not yet wired to the confirmation dialog; scroll/zoom
  gestures aren't implemented yet. **Important scope note:** there is no separate palm/hand
  ROI detector model - you hold your hand in a fixed central region of the camera view
  rather than anywhere in frame. See `docs/architecture/decisions.md` for why, and for what
  I could and couldn't verify myself (I confirmed the full pipeline runs without errors at
  ~30fps with a real camera; I could not verify gesture *recognition accuracy* since that
  needs a live human hand in front of the camera during testing - please test this yourself).
- Interfaces only (no implementation yet) for Vision, Browser, and Plugins - these are
  Phase 2/3/5 per the roadmap.

## Architecture decisions made so far (beyond what the roadmap specified)

The roadmap left some things open; see `docs/architecture/decisions.md` for the full reasoning:

1. **Desktop shell: WPF, not WinUI 3**, for now - faster iteration, no MSIX packaging
   overhead during Phase 0/1. Revisit WinUI 3 once the agent core is proven.
2. **AI reasoning core: hybrid, free-by-default, cost-protected.** NoniPilot's agent loop
   talks to an `IChatProvider` abstraction, not to any specific AI vendor. Three
   implementations exist behind it, tried in order with automatic fallback:
   - **Local (Ollama)** - always available, zero cost, fully offline. **On by default.**
   - **Groq free tier** - fast cloud inference (`openai/gpt-oss-120b` by default - Groq's
     lineup shifts over time, check console.groq.com/playground if this 404s), free API key,
     used automatically once you set `GROQ_API_KEY`. On by default, inert until keyed.
   - **Claude (paid)** - off by default, only used if you both enable it in
     **AI Providers** and set `ANTHROPIC_API_KEY`. NoniPilot never adds a payment method
     or upgrades itself to a paid tier on its own.

   If the active provider errors, hits a rate limit, or there's no internet, the next one
   in the list is tried automatically for that same request - see
   `NoniPilot.Agent/Providers/ChatProviderRouter.cs`. Configure this from the **AI
   Providers** button in the app.

## Repository layout

```
src/
  NoniPilot.Domain/          Models + every service interface (no implementations)
  NoniPilot.Policy/          4-tier risk classification + admin policy overrides
  NoniPilot.ComputerControl/ Mouse/keyboard/window control (Win32 SendInput) + EmergencyStop
  NoniPilot.FileSystem/      File/folder operations, Recycle-Bin-aware deletes
  NoniPilot.Applications/    Launch/focus/close/list running applications
  NoniPilot.Audit/           SQLite-backed, append-only audit log
  NoniPilot.Verification/    Post-condition checks (file/process level for now)
  NoniPilot.Agent/           The provider-agnostic planner: tool catalog + agent loop + IChatProvider (Claude/Groq/Ollama)
  NoniPilot.Voice/           Interface only - Phase 1 (voice input/output)
  NoniPilot.Vision/          Interface only - Phase 2 (screen perception)
  NoniPilot.Gesture/         Interface only - Phase 4 (hand tracking)
  NoniPilot.Browser/         Interface only - Phase 3 (browser automation)
  NoniPilot.PluginSdk/       Interface only - Phase 5 (capability plugins)
  NoniPilot.Desktop/         WPF shell: command bar, task timeline, Emergency Stop
plugins/                     Browser/Office/Enterprise adapters (empty - Phase 3+)
tests/
  Unit/                      xunit - policy classification + filesystem safety guards
  Integration/Vision/Gesture/UI/  Empty for now - later phases
docs/                        architecture/security/api/operations notes
installer/                   Empty - packaging comes later
```

## Running it

Requires the .NET 8 SDK and Windows (the execution engines are Win32-specific).

```powershell
dotnet build NoniPilot.sln
dotnet test tests/Unit
dotnet run --project src/NoniPilot.Desktop
```

That's it for a completely free setup - **Local (Ollama) is enabled by default** and needs
no API key. To actually get a response you also need Ollama installed and a model pulled:

```powershell
# Install Ollama from https://ollama.com, then:
ollama pull llama3.1
```

Local inference on CPU-only hardware is slow (expect tens of seconds per response) - to get
faster/better answers for free, add a Groq key instead (or as well - it's tried first when
present):

```powershell
[System.Environment]::SetEnvironmentVariable("GROQ_API_KEY", "gsk_...", "User")
```

Get a free key at [console.groq.com](https://console.groq.com/keys) (no credit card
required). Restart your terminal after setting it, then open **AI Providers** in the app to
confirm which providers are active.

Claude is also supported as an opt-in paid provider (see `ANTHROPIC_API_KEY` and the **AI
Providers** window) but is off by default - see `docs/architecture/decisions.md`.

Type a command like "list the files in my Downloads folder" and press Enter. Anything
above L0/L1 risk (delete, install, etc.) will pop a confirmation dialog before it runs -
that's the policy engine working as designed, not a bug.

### Voice setup

Text-to-speech works out of the box (Windows' built-in synthesizer, no setup). Speech-to-text
works out of the box too via the offline Windows Speech engine, but it's noticeably less
accurate - set `GROQ_API_KEY` (see above) to use Groq's Whisper API instead, automatically.
Hold the green **"Hold to Talk"** button, speak, release - the transcribed text is sent like
a typed command.

### Gesture setup

Gesture control needs a hand-landmark ONNX model that isn't checked into this repo (it's a
~11MB binary). Place it at:

```
%LOCALAPPDATA%\NoniPilot\models\hand_landmark.onnx
```

The model used during development was `hand_landmark_full_1x3x224x224.onnx` from
[PINTO0309's model zoo](https://github.com/PINTO0309/PINTO_model_zoo) (folder
`033_Hand_Detection_and_Tracking/30_batchN_post-process_marged/post_process_marged.tar.gz`) -
a community ONNX conversion of Google's MediaPipe hand-landmark model. Any ONNX export with
a `[1,3,224,224]` input and a `[1,63]` landmark output (MediaPipe's standard 21-point,
x/y/z scheme) should work as a drop-in replacement. Without the file at that path, the
**Gesture Center**'s "Start Camera" button stays disabled with an explanatory message.

Once in place, open **Gesture Center**, click **Start Camera**, and hold your hand roughly
centered in the preview (see the scope note above - there's no separate hand-finder model,
so it won't find your hand anywhere in frame the way full MediaPipe Hands does).

## Safety model (already implemented, not just documented)

Every action Claude, Groq, or a local model wants to take goes through
`IPolicyService.Evaluate` before it executes (`NoniPilot.Agent/ToolCallingPlannerService.cs`).
An action NoniPilot has never classified before
fails closed to **L2 Sensitive** (always-confirm) rather than defaulting to safe. The
Emergency Stop button calls `IComputerControlService.EmergencyStop()` (synchronous, releases
any held mouse button immediately) *and* cancels the in-flight agent loop's
`CancellationToken` - both halves matter.

## Next steps (see the roadmap's section 15 for the full phase plan)

1. Screen capture + Windows UI Automation + OCR (`NoniPilot.Vision`), then upgrade
   `IVerificationService` to check actual screen state instead of just files/processes.
2. A real palm/hand ROI detector model, so gesture control finds your hand anywhere in frame
   instead of a fixed central region.
3. Wire ThumbsUp/ThumbsDown to the confirmation dialog; add scroll/zoom gestures.
4. Wake-word voice mode (continuous listening), as an alternative to push-to-talk.
5. Expand the tool catalog as new services come online (browser, vision) - the pattern in
   `NoniPilot.Agent/Tools/ToolCatalog.cs` is designed to scale to that.
