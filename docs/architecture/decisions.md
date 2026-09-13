# Architecture decisions

Decisions made beyond what the product roadmap specified, and why.

## Desktop shell: WPF over WinUI 3 (2026-09-12)

The roadmap listed ".NET / C# with WinUI 3 or WPF" as the recommended stack without picking
one. WPF was chosen for Phase 0/1: no MSIX packaging or Windows App SDK runtime dependency
to fight with while the agent core is still unproven, mature tooling, faster edit-build-run
loop. WinUI 3 remains a reasonable choice to revisit for a polished v1.0 shell once the
underlying Observe/Plan/Act/Verify loop is stable - the UI layer only touches
`NoniPilot.Desktop`, so switching later does not require touching any other project.

## AI reasoning core: provider-agnostic, free-by-default, cost-protected (2026-09-12, revised same day)

The roadmap's Agent layer (`IIntentService` / `IPlannerService`) needed a reasoning engine
but didn't name one. The first cut used Claude directly; the same day, the user asked for a
completely free option instead. Rather than swap one hardcoded vendor for another, the
Agent layer was refactored around a vendor-neutral `IChatProvider` interface
(`NoniPilot.Agent/Providers/IChatProvider.cs`): `ChatMessage`/`ChatToolDefinition`/
`ChatToolCall` are a generic shape every backend translates to/from, and the agent loop
(`ToolCallingPlannerService`) never sees a vendor-specific type.

Three implementations exist behind that interface:

- **`OpenAiCompatibleChatProvider`** - one HTTP client for any OpenAI-compatible
  `/chat/completions` endpoint. This single class covers **both** Groq
  (`https://api.groq.com/openai/v1`, needs `GROQ_API_KEY`) and a **local Ollama** server
  (`http://localhost:11434/v1`, no key at all) - they're wire-compatible, so there was no
  reason to write two near-identical clients.
- **`ClaudeChatProvider`** - wraps the official Anthropic C# SDK behind the same interface.
  Kept as an option (not deleted) since the abstraction makes it free to keep - but demoted
  to opt-in/paid-tier, off by default.
- **`ChatProviderRouter`** - a composite `IChatProvider` that tries each configured provider
  in priority order **per request** (not just at startup), catching connectivity/rate-limit/
  auth failures and falling through to the next. This is what makes "no internet -> use
  local", "Groq quota exhausted -> fall back to local" happen live, mid-task, without a
  restart.

**Defaults, chosen so a fresh install costs nothing and never silently escalates to a paid
provider:** Local is always in the chain. Groq is in the chain too, but inert until
`GROQ_API_KEY` is set - so enabling it is just "add a free key," not a separate toggle
hunt. Claude requires **both** an explicit opt-in in the **AI Providers** window *and*
`ANTHROPIC_API_KEY` - two independent switches that must both be on, so it can never turn
itself on. NoniPilot never collects payment info anywhere in the app, so "never add a
payment method" is true by construction, not by policy.

Settings persist to `%LOCALAPPDATA%\NoniPilot\ai-providers.json`
(`NoniPilot.Agent/Providers/AiProviderSettings.cs`) and are edited from the **AI Providers**
button in `NoniPilot.Desktop` (`AiProvidersWindow.xaml`).

Two decisions carried over from the original Claude-only design, still true for whichever
provider is active:

- **Manual loop, not a provider SDK's built-in tool-runner helper.**
  `ToolCallingPlannerService` owns the entire request → tool-call → execute → tool-result
  loop itself, specifically so that the policy gate (`IPolicyService.Evaluate`) and the
  confirmation dialog (`IUserConfirmationService.ConfirmAsync`) are unconditionally on the
  only path a tool call can take to actually run, regardless of which model answered. See
  roadmap section 18: "Treat AI-generated instructions as untrusted input and enforce policy
  outside the model."
- **The `IIntentService` fast-path** (`NoniPilot.Agent/LocalIntentService.cs`) still handles
  "Stop" and similar phrases entirely locally, with zero dependency on any provider or the
  network - required by roadmap section 4/10 (emergency stop cannot wait on an LLM
  round-trip, whichever backend that round-trip would hit).

**Known limitation, accepted for this iteration:** because the router re-evaluates per
request, a single multi-step task could in principle be answered by different models across
its turns if the primary provider fails mid-task. The shared `ChatMessage` history makes
this mechanically safe, but it's a real behavioral quirk worth knowing about, not something
to be surprised by later.

**Local model choice:** `llama3.1` (8B) is the default in settings - a reasonable balance of
tool-calling reliability and CPU-only inference speed on typical hardware. It is noticeably
less reliable at complex multi-step tool orchestration than Groq's hosted models or Claude,
which is an inherent quality/cost/privacy tradeoff, not a bug to fix.

**Groq model default corrected same day (2026-09-12):** the original default,
`llama-3.3-70b-versatile`, 404'd (`model_not_found`) against the user's real account. A
second guess, `llama-3.1-8b-instant`, also 404'd. Rather than keep guessing from training-
data knowledge of Groq's catalog (evidently stale), the user checked
console.groq.com/playground directly and found `openai/gpt-oss-120b` actually selected and
working there - confirmed by the account's API key showing real successful calls. That's
now the default. Lesson: Groq's exact model lineup is not something to hardcode confidently
from memory - if this 404s again, the Playground's model dropdown is the source of truth,
and the fix is a one-field change in the AI Providers window, no rebuild required.

## Voice: hybrid STT (Groq Whisper / Windows Speech), local-only TTS (2026-09-12)

Same hybrid philosophy as the chat provider. Speech-to-text has two implementations behind
`ISpeechToTextService` (`NoniPilot.Voice/SpeechToTextRouter.cs`): Groq's Whisper endpoint
(`/audio/transcriptions`, the same well-established OpenAI-compatible wire shape used
elsewhere) when `GROQ_API_KEY` is set, falling back to the fully offline Windows Speech
Recognition engine (`System.Speech.Recognition`) otherwise. Both consume the exact same
16kHz mono WAV bytes recorded by `NAudioMicrophoneRecorder`, so no format conversion sits
between them.

Text-to-speech is **not** hybridized - it's Windows' built-in `SpeechSynthesizer` only. This
was a deliberate simplification, not an oversight: it's free, offline, zero-setup, and
already good enough, so adding a cloud TTS option (Groq's PlayAI TTS or similar) would add
complexity without solving a real problem. Revisit only if voice quality becomes a
complaint.

**Input model is push-to-talk only** (a "Hold to Talk" button), not the always-listening
wake-word mode roadmap section 8.1 also describes. The reserved `ICommandInputService`
interface in `NoniPilot.Domain` is where that would eventually live - push-to-talk's simpler
request/response shape didn't fit that event-stream interface, so a new implementation
lives in `NoniPilot.Voice` directly instead of forcing a fit.

## Gesture: MediaPipe hand-landmark ONNX model, no separate palm detector (2026-09-12)

The roadmap recommends "MediaPipe Hands or equivalent" for hand tracking (section 7/8.8).
The user chose to use an actual MediaPipe-derived model via ONNX Runtime rather than a
simpler from-scratch CV approach. The specific model
(`hand_landmark_full_1x3x224x224.onnx`, ~11MB) came from
[PINTO0309's model zoo](https://github.com/PINTO0309/PINTO_model_zoo) - a well-known,
widely-used source in the embedded-ML community for MediaPipe→ONNX conversions - specifically
its "post-process merged" batch export
(`033_Hand_Detection_and_Tracking/30_batchN_post-process_marged/post_process_marged.tar.gz`).
Its actual I/O was verified directly (not assumed from documentation) with a throwaway ONNX
Runtime probe before writing the detector code: input `input [1,3,224,224]` float32 (NCHW);
four outputs `Identity [1,63]` (screen-space landmarks), `Identity_1 [1,1]` (hand presence),
`Identity_2 [1,1]` (handedness), `Identity_3 [1,63]` (world-space landmarks) - in that order.
`OnnxHandLandmarkDetector` takes the *first* output of each shape (screen landmarks and
presence), deliberately ignoring the second matches (world landmarks, handedness) - an easy
bug to introduce by accident, since both scalar outputs look identical by shape alone.

**Deliberate scope cut: no separate palm/hand-detector model.** Full MediaPipe Hands is a
two-stage pipeline (a palm detector finds and crops the hand region, then the landmark model
runs on that crop). Implementing the palm detector's SSD-style anchor-box decoding correctly
was judged not worth the added complexity and risk for this pass. Instead,
`WebcamGestureEngine` always crops a fixed central square from the camera frame and feeds
that to the landmark model directly. The practical consequence: the user must hold their
hand roughly centered in the preview, rather than anywhere in the camera's view. This is the
single biggest fidelity gap versus "real" MediaPipe Hands, and the first thing to fix if
gesture control feels unreliable in practice.

**Gesture classification is pure geometry, not ML** (`GestureClassifier.cs`): ratios of
landmark distances (fingertip-to-wrist, thumb-to-index) against calibratable thresholds,
with no camera or model dependency - this is the one piece of the gesture pipeline that is
genuinely unit-tested (`tests/Unit/GestureClassifierTests.cs`) with synthetic landmark data.

**What was and wasn't verified before calling this done:**
- Verified: the full pipeline (camera → crop → ONNX inference → landmark parsing →
  classification → UI) runs without exceptions at ~30fps against this machine's real
  webcam, with a real model file, for an 8-second smoke test. No hand was in frame during
  that test, and it correctly reported no hand detected rather than a false positive.
- Verified live in the actual app: Gesture Center opens, camera preview renders, calibration
  sliders present, start/stop toggling works.
- **Not verified: gesture recognition accuracy** - correctly distinguishing a fist from an
  open palm from a pinch, in practice, under real lighting with a real hand. That requires a
  live human tester, which this session could not provide. Treat the specific threshold
  defaults in `GestureCalibration` as a starting point to tune, not a validated result.

**Wired to real actions:** Point (cursor move), PinchStart/PinchEnd (mouse
press/move/release - click if brief, drag if held+moved), OpenPalm (force-release), Fist
(calls the exact same `IComputerControlService.EmergencyStop()` as the Desktop STOP button).
**Recognized but not wired:** ThumbsUp/ThumbsDown (intended for confirm/cancel on the
confirmation dialog - needs additional plumbing to reach `WpfUserConfirmationService`'s
MessageBox). **Not implemented at all:** scroll and zoom gestures.

## Conversation memory across commands, and a chat-transcript UI (2026-09-12)

User feedback: each command started a brand-new conversation with no memory of the last one
(e.g. "reply in Hindi" didn't carry over to the next command), and the UI only ever showed
the latest single result rather than a real chat history.

**Memory:** `ToolCallingPlannerService` now holds a `_conversationHistory` field that
persists across every `ExecuteAsync` call on that instance - one instance = one session's
memory. Only committed on a *clean* finish (a final text answer with no dangling tool call),
never mid-loop, so a cancelled/crashed task can never leave an orphaned `tool_use` with no
matching `tool_result` in history (which would break every provider's wire format on the next
call). Trimmed to the last `MaxRememberedTasks` (6) commands, cut only at User-message
boundaries - bounds token growth (relevant given how tight Groq's free-tier TPM budget can
be) at the cost of eventually forgetting the oldest exchanges. A new `ToolCallingPlannerService`
(i.e. changing AI Providers) starts fresh memory - that's the one thing that resets it now,
not each command.

Multi-language conversation ("talk to me in Hindi") was never actually a missing feature -
every backend (Claude/Groq/Ollama) already responds in whatever language it's addressed in.
What was missing was memory: without it, an instruction like "reply in Hindi" only applied
to that single command and was gone by the next one. Fixed by the same memory change above.

**UI:** `MainWindow` replaced the single steps-list + result-box with a real scrolling chat
transcript (`ChatEntry` + `ChatEntryTemplateSelector`, gradient user bubbles, card-style
assistant bubbles, inline step/system lines, red error bubbles) plus a restyled dark/neon
header and pill-shaped input bar. This is a visual-only change - the underlying task/step
data model didn't change, just how it's displayed.

**Bugs found and fixed while verifying this live:**
- The router's final error message only said "every provider failed" without saying why,
  even though each provider's specific reason was available - fixed to include every
  provider's actual failure reason in the message itself (`ChatProviderRouter.cs`), not just
  a transient status-bar event that gets overwritten.
- Local (Ollama)'s timeout was 3 minutes, measured too short for a real multi-step tool
  exchange on CPU-only 8B-model inference (a second sequential call, summarizing a tool
  result, can itself take minutes) - raised to 8 minutes. Local has no per-call cost, so a
  generous timeout only costs wall-clock time, not money.

**What was and wasn't verified:** live-tested end to end with Local (Ollama) only - my own
test shell never had `GROQ_API_KEY` (it was set via the Windows GUI in the user's own
terminal, which started after my shell did, so it never inherited the change). A real 2-turn
tool exchange (list a folder, then summarize it) completed successfully in ~170 seconds on
this CPU-only hardware once the timeout was raised. The Groq path through this exact new
memory/UI code was not independently verified by me - worth confirming on your end that a
multi-turn conversation via Groq also remembers context correctly.

## Voice: continuous conversation mode, and a real folder-opening bug (2026-09-12)

Two more user reports the same day. First: "Talk" / "Stop Talking" as two separate clicks per
turn wasn't wanted - the ask was one click to enter a continuous voice conversation (listen,
act, speak the result, listen again automatically) and one click (or a spoken "stop") to exit
it entirely. Second, a real bug: asking to open a folder on the desktop kept opening the
Documents folder instead - repeatedly, across a spelling correction from the user.

**Continuous voice mode:** `IMicrophoneRecorder` gained `RecordUntilSilenceAsync(maxDuration,
silenceDuration)` - it starts recording immediately and stops itself once amplitude tracking
(a simple RMS-over-buffer computation in `NAudioMicrophoneRecorder`, handling both IEEE-float
and 16-bit PCM WASAPI formats) sees speech followed by a period of quiet, or the max duration
elapses as a safety cap. `MainWindow.VoiceModeLoopAsync` then loops: listen -> transcribe ->
`ProcessCommandAsync` -> (loop) until `Talk`/`Stop Talking` is toggled off or a spoken "stop"
is caught by the fast-path intent matcher. The critical correctness detail:
`ProcessCommandAsync` now *awaits* `ITextToSpeechService.SpeakAsync` (it was fire-and-forget
before) - continuous mode must not start listening again while NoniPilot is still speaking,
or it will try to transcribe its own voice output.

**A hang this introduced, caught while verifying it:** switching mic capture from the legacy
`WaveInEvent` to `WasapiCapture` (to fix the earlier `waveInOpen` crash) means recordings are
now in the capture device's *native* format (commonly 48kHz stereo IEEE float), not a fixed
one. `System.Speech.Recognition`'s offline engine does not handle arbitrary WASAPI formats
well - fed that format directly, `TranscribeAsync` hung indefinitely on "Transcribing..."
rather than returning or timing out. Fixed by resampling every recording to 16kHz mono 16-bit
PCM (`NAudioMicrophoneRecorder.ResampleTo16kMonoPcm`, via `MediaFoundationResampler`) before
it's handed to either STT backend - the format both were actually designed around. Caught
this by literally watching the status text stall past 70 seconds during live verification,
not by reasoning about it in advance - a reminder that "should work" claims about audio
format compatibility are exactly the kind of thing that needs a live check.

**The folder-opening bug:** `WindowsApplicationService.LaunchAsync` passed whatever string the
model gave it straight to `Process.Start(..., UseShellExecute: true)` with no validation. When
that string didn't resolve to a real path, Windows Shell's `ShellExecute` didn't error - it
silently fell back to some default location (Documents, in the observed case). The tool
result still reported success (a process launched, a window opened - just the wrong one), so
neither the model nor `IVerificationService` had any signal that anything was wrong. Fixed
two ways: (1) `LaunchAsync` now checks `Directory.Exists`/`File.Exists` first - an existing
directory launches via `explorer.exe "<path>"` explicitly (guaranteed to open exactly that
folder, no ambiguous fallback), and a string that looks like a path but doesn't exist now
throws immediately instead of silently opening something else; (2) the system prompt now
instructs the model to confirm a folder's exact path with `filesystem_list_directory` or
`filesystem_search` *before* calling `application_launch` on it, rather than guessing a name
or spelling. Neither fix depends on the other - the throw-on-bad-path change protects against
future prompt/model mistakes too, not just this one instance.

**A concurrency bug this surfaced:** the user reported a task that "never responded" - but
`ProcessCommandAsync` always adds a chat bubble on every exit path (success, failure,
exception), so a missing response pointed at something else: no guard existed against a
second command starting while the first was still in flight (e.g. typing a spelling
correction into the text box while Local AI was still slowly working the first request -
Enter/Send had no check against an already-running task). Two concurrent `ExecuteAsync` calls
mutating the same chat transcript and step-index dictionary is exactly the kind of thing that
makes a response look "lost." Fixed with an `_isTaskRunning` guard in `ProcessCommandAsync`
(a second attempt gets a clear "still working" message instead of silently starting a
parallel task) plus disabling `CommandTextBox` itself - not just the Send button - while a
task runs, since a disabled button doesn't stop the Enter key from still submitting.

**Groq's rate limit, revisited:** confirmed with the user that their Groq account has no
pending verification step and is already fully verified, yet still gets `413 Request too
large` / TPM-limit errors even on the smaller `openai/gpt-oss-20b` model. This means the
earlier "new/unverified-account throttle" theory was wrong - 8,000 TPM is apparently just
this account's real free-tier ceiling for these models. This isn't something further model
substitution is likely to fix, and the router's fallback to Local already handles it
correctly (by design) - it's a real, expected limitation of the free tier to communicate to
the user, not a bug to keep chasing in code.

## Verbose responses, and speech that couldn't be interrupted (2026-09-12)

Two more real reports the same day: NoniPilot narrated entire tool results (a full 34-item
directory listing, field-by-field JSON explanations) instead of a short confirmation, and once
it started speaking a long response aloud, clicking STOP/Stop Talking did not stop it - it
kept talking over the user until finished.

**Verbosity** was a prompt gap, not a code bug - the system prompt had one soft line about
"a concise summary" that a weaker/local model wasn't reliably following. Replaced with an
explicit, repeated rule: the final response is frequently read aloud, so never enumerate or
restate a tool's raw result, never explain the tool's data format, and keep it to one short
sentence. Prompt-only fix, not yet re-verified against a real user session (the mechanism
that reads results aloud was the priority to fix first - see below).

**The uninterruptible speech was a real, verifiable bug**, not just a missing feature:
`WindowsTextToSpeechService.SpeakAsync` ran `Task.Run(() => _synthesizer.Speak(text),
cancellationToken)` - a blocking synchronous `Speak()` call. `Task.Run`'s cancellation token
only prevents a *not-yet-started* task from starting; once `Speak()` is actually running,
that token does nothing to it. So STOP's `_currentTaskCts.Cancel()` looked like it should
interrupt speech but structurally couldn't. Fixed by switching to the synthesizer's own
`SpeakAsync`/`SpeakCompleted` event pair plus `SpeakAsyncCancelAll()` (added as
`ITextToSpeechService.StopSpeaking()`), with a `CancellationToken.Register` callback that
calls it. `ProcessCommandAsync` now passes the same `_currentTaskCts.Token` used for the
agent task into the speech call, and `StopEverything()` also calls `StopSpeaking()` directly
for an immediate synchronous cutoff rather than waiting on the cancellation registration to
run. Verified deterministically with an isolated probe (not by timing it against the live,
slow local-model pipeline, which made end-to-end timing unreliable to test against): speaking
a ~20-second text, cancelling 1.5 seconds in, and confirming the call returned within ~30ms
of that cancellation rather than running to completion.

## The real reason voice "wasn't talking back" (2026-09-12)

Same-day follow-up: the user reported NoniPilot wasn't holding a real back-and-forth voice
conversation, and the folder-opening command still hadn't succeeded. The second half traced
to garbled speech-to-text (offline Windows Speech recognizing Hindi far less accurately than
Groq Whisper would - itself a consequence of Groq still being rate-limited on this account)
combined with the model refusing to fuzzy-match a mangled folder name against real listing
results; addressed with a prompt rule to treat a close phonetic/partial match as a match
rather than demanding an exact spelling, plus one telling symptom: the model had started
saying "I can only respond in text" - a real tell that something more fundamental was wrong,
since the model has no way to know about TTS either way and shouldn't be asserting it either
way.

**That "something more fundamental" was a real, verifiable bug, present since Voice was
first built:** `WindowsTextToSpeechService`'s `SpeechSynthesizer` was never told where to
send audio. `SetOutputToDefaultAudioDevice()` was never called - without it, the synthesizer
processes speech internally but routes it nowhere: no exception, no error, just silence.
Every "spoken" response up to this point may never have produced audio at all. Fixed by
calling it in the constructor; verified with an isolated probe that construction still
succeeds (would throw if there were genuinely no output device) and that `SpeakAsync` takes
a real few seconds to return for a short sentence, matching actual speech duration rather
than completing instantly.

Lesson for next time a "the AI seems to be ignoring me" report comes in over voice: check
whether audio is actually being produced at all before assuming the conversation logic,
transcription accuracy, or the model's reasoning is at fault - a silently-broken output path
looks identical to "not really responding" from the user's side.

**Follow-up the same day: still silent after the first response, reproducibly.** Even after
the `SetOutputToDefaultAudioDevice()` fix and a full app relaunch, speech worked exactly once
per session and then went silent again - but an isolated test of 4 back-to-back `SpeakAsync`
calls (no microphone involved) all worked perfectly. The difference: continuous voice mode
runs microphone capture (`WasapiCapture`) and speech playback on the same reused
`SpeechSynthesizer` in the same process, cycling capture → speak → capture → speak. That
combination, not repeated `SpeakAsync` calls alone, is what a clean isolated test needs to
reproduce - and once reproduced (record → speak → record → speak → record → speak, mirroring
`VoiceModeLoopAsync` exactly), the fix was to stop reusing one long-lived `SpeechSynthesizer`
and instead construct a fresh one per `SpeakAsync` call, disposed after. This forces a clean
COM/audio session each time rather than reusing one that a preceding capture session may have
left in a bad state - a known practical workaround for this class of SAPI reliability issue
in apps that also do audio capture, not something that can be root-caused further without
lower-level audio-session tracing. Re-verified with that same interleaved capture+speak
probe: 3 full record→speak cycles, all producing realistic (~5s) speech durations.

**Folder-opening reliability, addressed differently this time:** rather than continuing to
lean on prompt wording alone to get a (possibly weak, local) model to reliably chain
"list directory → fuzzy-match → launch" across two turns, added `filesystem_open_best_match`
- one tool that does all three steps together (`ToolCatalog.cs` + `FuzzyNameMatcher.cs`).
Collapsing two reasoning hops into one is more robust for a small/local model and also means
one fewer round-trip exposed to Groq's rate limit or Ollama's slowness. The matcher itself is
genuinely unit-tested (`tests/Unit/FuzzyNameMatcherTests.cs`) against realistic garbled
inputs ("amber gaiety", "ambar ai ti" -> "AMBERG IT"). Important documented limitation: the
matcher only helps if the model passes a Latin-script transliteration as the hint - a raw
Devanagari (or other non-Latin script) hint has zero character overlap with a Latin-scripted
real folder name and won't match at all (this exact case is one of the unit tests, asserting
`null`, not a passing match - a known boundary, not a silent gap). The system prompt now
explicitly tells the model to transliterate before calling the tool.

## Two more same-day reports: false Application.Launch failures, and intermittent TTS silence (2026-09-12)

Latest round of live screenshots showed: `[Failed] Application.Launch (risk: L1Normal) -
Failed to start 'ms-settings:sound'.` and the same for `'ms-settings:'`, alongside a mix of
chat turns where speech played and turns where it didn't, and folder-opens that sometimes
succeeded and sometimes stalled (the stalls tracked to Groq's TPM rate limit banner visible
in the same screenshots, not a new bug).

**`ms-settings:` failures were a real bug, now confirmed root-caused.** `Process.Start`
legitimately returns `null` for a `UseShellExecute = true` launch that the shell hands off to
an already-running singleton process instead of spawning a new one - exactly what happens for
protocol-URI activations like `ms-settings:sound` (Settings is a singleton app). The old code
did `process?.Id ?? throw new InvalidOperationException(...)`, treating that legitimate null
as a launch failure. Fixed in `WindowsApplicationService.LaunchAsync`: only a genuine
`Win32Exception`/`InvalidOperationException` thrown by `Process.Start` itself now counts as
failure; a null return is treated as success with `processId = 0`. **Verified directly**, not
just reasoned about: an isolated probe calling `Process.Start(new ProcessStartInfo
("ms-settings:sound") { UseShellExecute = true })` confirmed it returns `null` with no
exception thrown - exactly the case the fix now handles correctly.

**Intermittent TTS silence, even after the earlier fresh-synthesizer-per-call fix.** Screenshots
showed speech working on some replies and not others within the same session, ruling out the
earlier "works once then never again" pattern (already fixed) - this is a different,
lower-frequency version of the same symptom class. Working hypothesis: on this hardware, the
mic's `WasapiCapture` session closing and the next reply's playback session opening happen
back-to-back with no gap, and occasionally race at the driver level, silently dropping that
utterance's audio. Mitigated with a 400ms `Task.Delay` in `MainWindow.VoiceModeLoopAsync`,
inserted right after a turn's `ProcessCommandAsync` (which includes speaking the result) and
before the loop records again. **This is a mitigation for a suspected cause, not a confirmed
root-cause fix** - unlike the Application.Launch bug above, I could not reproduce the
intermittent-silence pattern in an isolated probe (it appears to depend on real continuous-mode
timing/hardware conditions that a standalone test doesn't recreate), so this needs the user's
own live retest across several turns to know if it actually helped.

Both fixes: `dotnet build` clean (0 errors, 2 pre-existing unrelated OpenCvSharp analyzer
warnings), `dotnet test tests/Unit` all 31 passing - no regressions from either change.

## The real "stuck in between talking" bug: a self-reinforcing hallucination loop (2026-09-12)

Next round of screenshots showed NoniPilot stuck repeating variations of "I can only respond
in text, voice output isn't available on this platform" (in Hindi) turn after turn, no matter
what the user asked - while basic tool actions (open folder, create file) kept working fine.
The Groq-unavailable banner was visible throughout, meaning every one of these replies came
from the local Ollama fallback model.

**Root cause, now understood precisely (not just "the local model is less reliable"):** the
system prompt already had an explicit rule (added in an earlier fix) telling the model never to
claim it has no voice/audio output - and the weak local model violated it anyway, producing a
generic pretrained disclaimer it likely picked up from its own base training. That single
violation then got written into `_conversationHistory` like any other assistant turn. On the
next turn, the model read its own prior claim back as established conversation fact and
reasserted/doubled down on it - a self-reinforcing loop entirely internal to the conversation
memory feature added earlier the same day. A prompt rule alone cannot fix this class of bug,
because the model was already failing to follow the prompt; the loop was being manufactured by
the app's own memory faithfully preserving the model's mistake.

**Fix: a deterministic interception, not another prompt tweak.** `ToolCallingPlannerService`
now checks the model's final (non-tool-call) response against `VoiceCapabilityDenialMarkers` -
a short, deliberately narrow list of exact phrasings actually observed in the live screenshots,
in both English and Hindi (e.g. "only respond in text", "केवल टेक्स्ट", "वॉइस आउटपुट",
"आवाज़ के रूप में आउटपुट"). A match is replaced with a fixed corrective reply
(`VoiceCapabilityCorrection`) *before* it is spoken aloud and *before* it is written into
`_conversationHistory` - so the false claim never gets a chance to seed the next turn's context,
which is what actually breaks the loop (as opposed to just hiding one bad reply from the user).
Verified the matcher itself with a standalone probe against the exact denial sentences from the
screenshots (4 real hallucinated samples, all correctly flagged) plus 3 legitimate replies
("Opened the AMBERG IT folder.", etc., all correctly left alone) - zero false positives/negatives
across that sample. Not yet verified: whether this fully eliminates the *feeling* of being
stuck for the user in a real extended session, since the underlying local model can still say
unhelpful things this specific list doesn't happen to catch - this closes the one concrete,
reproducible loop seen in the screenshots, not every way a weak local model can be unhelpful.

**Underlying trigger, unchanged and not newly fixable:** this only happens while Groq is
rate-limited and Ollama (llama3.1 8B) is answering instead - already documented as a real,
expected free-tier limitation, not something further code changes fix. If Groq's quota being
exhausted this often becomes the practical bottleneck, the next actionable lever is trying a
different/larger local model, not further prompt or interception work.

## Groq turned off by default; a hard cap on the tool-call loop (2026-09-12)

Two more issues surfaced the same day. First, an explicit policy decision from the user: they
do not want to depend on any external provider's token that can run out mid-task - Groq's free
tier repeatedly hitting its TPM ceiling mid-session (already documented above) is precisely the
failure mode they rejected. Second, a real bug: after a folder successfully opened via
`FileSystem.OpenBestMatch`, continuous voice mode stopped listening entirely - "it opened the
folder, then never heard me again."

**Groq default flipped off.** `AiProviderSettings.EnableGroq` now defaults to `false` (was
`true`) - Local (Ollama) is the only provider enabled out of the box. Groq is not removed from
the codebase; it remains a fully supported opt-in for anyone who wants its faster/more capable
hosted model and accepts that its free tier can run dry mid-session - it's simply no longer the
default, since a token running out mid-work is exactly what was asked to be eliminated. The
user's own already-running install's persisted settings
(`%LOCALAPPDATA%\NoniPilot\ai-providers.json`) were updated to match immediately, not just new
installs going forward.

**The "stuck after folder open" bug was a real, structural gap: no upper bound on the
tool-calling loop.** `ToolCallingPlannerService.ExecuteAsync`'s `while (true)` loop had no cap
on how many request/tool-call rounds a single command could take, and continuous voice mode
awaits the *entire* task before it listens again - so any model that never reaches a final,
tool-call-free response leaves voice mode silently stuck forever. Weak/local models are the
realistic way this happens: rather than concluding once an action succeeds, they can re-issue
the exact same call again. Two independent guards added:
- **`MaxToolCallRounds` (6):** a hard ceiling on rounds within one `ExecuteAsync` call. Past it,
  the task is force-completed with a clear "took more steps than expected" message instead of
  continuing indefinitely - the task (and therefore voice mode) always eventually recovers.
- **Duplicate-call short-circuit:** each tool call's exact signature (name + arguments) is
  tracked for the duration of the task; a call repeating one already executed is *not*
  re-executed (important for non-idempotent tools - launching an app or creating a file again
  is not a harmless no-op) and instead gets a synthetic tool result telling the model the action
  is already done and to give its final answer - directly targeting the exact pattern observed
  live (`FileSystem.OpenBestMatch` called twice in a row with identical arguments).

Both are defensive, provider-agnostic fixes - they bound worst-case behavior regardless of which
model is answering, not just the local one. `dotnet build` clean (0 warnings, 0 errors),
`dotnet test tests/Unit` all 31 passing. Not yet verified live: whether the round cap or the
duplicate-call guard is what actually fires in a real repeat of this scenario, since a genuine
live repro (with Groq now off, exclusively on Local) wasn't run in this session - worth watching
whether voice mode recovers on its own the next time a similar stall happens.

## Common-app name resolution: "calculator" isn't a real Windows command (2026-09-12)

Same day, a concrete new failure: `[Failed] Application.Launch - Failed to start 'calculator':
... The system cannot find the file specified.` Confirmed directly with an isolated probe
calling `Process.Start` on several candidate names: `"calculator"` and `"Calculator.exe"` both
throw `Win32Exception` (file not found); only `"calc"`/`"calc.exe"` actually resolve on this
machine. The model asked for exactly what a person would naturally call the app - "calculator" -
but that's simply not the real executable name, and no amount of prompt-wording fixes a model
guessing a name that never existed.

**Fix: a small alias table in `WindowsApplicationService`, not a prompt change.** Windows'
own PATH/App-Execution-Alias resolution already handles the common case (`"notepad"`,
`"chrome"` etc. work as typed) - `CommonAppAliases` only needs to cover the known exceptions:
calculator→calc, paint→mspaint, wordpad→write, task manager→taskmgr, control panel→control,
command prompt→cmd, file explorer→explorer, magnifier→magnify, snipping tool→snippingtool.
Looked up case-insensitively with spaces stripped, so "task manager" and "taskmanager" both
resolve. Deliberately kept small and specific rather than trying to enumerate every possible
app - it exists to patch the concrete gap between natural language and Windows' actual naming,
not to become a general app catalog.

**Verified directly, not just reasoned about:** an isolated probe called the real
`WindowsApplicationService.LaunchAsync` (not a reimplementation) for `"calculator"`,
`"notepad"`, `"paint"`, and `"task manager"` - all four launched successfully, including the
exact failing case from the screenshot and a multi-word natural phrasing. `dotnet build` clean,
`dotnet test tests/Unit` all 31 passing.

## Two more real bugs, root-caused via the audit log directly (2026-09-12)

User asked NoniPilot "Hey, what's your name?" - the chat showed several "Still working on the
previous command" rejections (expected, per the reentrancy guard - Local inference is slow), a
`[Verified] ComputerControl.TypeText` step, and finally a completely unrelated answer: "The
AMBERG IT folder is currently open on your desktop." Rather than guess, the actual audit log
(`%LOCALAPPDATA%\NoniPilot\audit.db`, queried directly with a throwaway probe using
`Microsoft.Data.Sqlite`) was read to see exactly what happened - this is the kind of report
that's easy to misdiagnose from the chat transcript alone, since the transcript doesn't show
tool call arguments.

**Bug 1: the model answered a question by literally typing onto the desktop.** The audit row
showed `ComputerControl.TypeText target={"text":"I am NoniPilot, your Windows desktop
assistant."}` - a genuinely correct, on-topic answer to "what's your name?", but delivered by
calling the tool that types text into whatever window currently has keyboard focus, instead of
just returning it as the chat response. Nothing in the tool's description or the system prompt
ever said this was wrong, so a model treating "say something" and "type something on screen" as
interchangeable wasn't actually violating any stated rule. Fixed two ways: tightened
`computercontrol_type_text`'s tool description (`ToolCatalog.cs`) to state plainly it's only for
typing into a field the user asked about, never for answering/talking to the user; and added an
explicit system prompt rule saying the same. This is a real, user-facing safety concern beyond
just "wrong answer" - it means text can land in an arbitrary focused window (a browser address
bar, a document, anything) as an unintended side effect of a simple question.

**Bug 2: the final answer itself was about an unrelated earlier task.** Nothing in this
specific command's own tool calls concerned "AMBERG IT" - that context came from several
commands earlier in the session. Root cause: `_conversationHistory` was committing the ENTIRE
`messages` list for a task on a clean finish - every intermediate tool_use/tool_result message
generated while accomplishing that one task, not just the user's command and the final answer.
A task with several tool round-trips (list folder, rename, copy, etc.) could inject many KB of
tool-call mechanics into long-term memory, and a small/weak local model - now the *only*
provider in use, per the same-day decision above - has limited ability to keep a huge tool-call
history straight from the actual current question, especially several commands later once
`TrimHistory` had folded multiple such bloated tasks together.

**Fix: `_conversationHistory` now stores only the semantic exchange.** A new
`CommitToHistory(command, finalText)` method replaces the old "commit the whole `messages` list"
logic at both exit points (clean finish, and the new `MaxToolCallRounds` cap) - it appends
exactly one User message (the command) and one Assistant message (the final answer), nothing
from in between. The full step-by-step trace remains fully available in `task.Steps` and the
audit log for anyone who wants to see exactly what happened; it's only what gets *replayed back
to the model on the next command* that's now trimmed to what's actually relevant. A useful side
effect: since tool messages are never stored in history at all anymore, the old structural
concern about a dangling tool_use with no matching tool_result (from a cancelled/crashed task)
is now impossible by construction, not just avoided by only committing at safe points.

Both fixes: `dotnet build` clean (0 errors), `dotnet test tests/Unit` all 31 passing. Not yet
verified live - this needs the user's own retest of a simple direct question ("what's your
name?", "how are you?") after a few unrelated file/folder tasks, to confirm the answer stays on
topic and no more text lands unexpectedly on the desktop.

## "Not replying for a long time" - a real memory/CPU bottleneck, plus a visibility fix (2026-09-12)

A follow-up report: a plain question ("आपका नाम क्या है") sent no reply at all, status frozen on
a bare "Running" - looking indistinguishable from a hang. Investigated directly rather than
guessed, since Groq is now off and every request goes to Local:

- `curl http://localhost:11434/api/ps` showed the loaded `llama3.1` instance's actual
  `context_length` is **4096** - Ollama's default when a request doesn't specify otherwise, not
  the 131072 the model architecture supports. Neither `OpenAiCompatibleChatProvider` nor
  anywhere else in the code ever sets `num_ctx`, and testing confirmed the OpenAI-compatible
  `/v1/chat/completions` endpoint doesn't honor a top-level `options.num_ctx` override the way
  the native `/api/chat` endpoint does - so this app has always been running against a 4096-token
  ceiling. NoniPilot's system prompt plus its 19 tool schemas alone are roughly several thousand
  tokens once serialized (`ToolCatalog.cs` is ~22KB of description/schema text) - comfortably
  enough, on top of any conversation history and the actual question, to be at or over that
  ceiling on every single request. Over-budget prompts force llama.cpp-style context
  shifting/truncation, which is slow and can silently drop the earlier part of the prompt -
  plausibly a contributing factor in this session's earlier "answered something unrelated" bug
  too, not just slowness.
- More immediately telling: `Get-CimInstance Win32_OperatingSystem` showed only **2.5GB of 15.7GB
  RAM free** at the moment of the report. Running an 8B-parameter model (even Q4 quantized,
  ~5GB on disk) with that little headroom risks real disk-swapping-level slowdown, independent
  of context size - this is very likely the dominant cause of this specific "long time, no
  reply" instance, not a code bug. **Deliberately not "fixed" by increasing the model's context
  window right now** - doing that would increase KV-cache memory usage exactly when memory is
  already the tightest resource, likely making things worse, not better. The actionable fix in
  this moment is freeing RAM (closing other apps/tabs), not a code or model change.
- What Local's actual free-tier ceiling costs was previously documented in the abstract
  ("CPU-only inference is slow"); this is the first time it's been pinned to concrete numbers
  (4096-token context ceiling, ~2.5GB free RAM) rather than a general caveat.

**Real, low-risk fix shipped regardless of root cause: visible progress instead of silence.**
`OnTaskUpdated` only fires again once something happens (a tool call, the final answer) - a
single long model call with no tool calls in between left `StatusText` frozen on a bare
"Running" for however long that call took, with zero way to tell "still working" from "actually
stuck." `MainWindow` now runs a 1-second `DispatcherTimer` that, while a task is in flight,
updates the status to `"Working... ({elapsed}s elapsed - the local model can take a few minutes
on this hardware)"` - a ticking number the user can watch, and an explicit expectation-setting
note about local hardware being genuinely slow, not silently broken. This doesn't make anything
faster; it makes the wait legible, which is the actual gap that turned a slow-but-working
request into something indistinguishable from a hang.

`dotnet build` clean, `dotnet test tests/Unit` all 31 passing. Not yet independently confirmed
whether the specific request in the screenshot ever completed - RAM pressure at the time made
it unsafe to keep probing the same shared Ollama instance further without risking making the
user's own live request even slower.

## The TypeText-as-answer bug, again - a prompt fix wasn't enough (2026-09-12)

The very next screenshot showed real progress from the memory fix above - asked "आपका नाम क्या
है?" (what's your name), the final chat answer was correctly on-topic: "मेरा नाम नोनी पायलट है."
But `[Verified] ComputerControl.TypeText` still fired for the exact same request, meaning the
earlier fix (tightening the tool description + adding a system prompt rule saying answers go in
the text response, never typed onto the desktop) did not fully stop it. This is the same class
of problem as the voice-capability-denial hallucination handled earlier in the session: a prompt
rule is not enforcement against a model that doesn't reliably follow it, and that's exactly what
was observed here.

**Fix: the same proven pattern - a deterministic guard, not more prompt wording.**
`ExecuteToolAsync` now refuses to actually execute `computercontrol_type_text` unless the user's
own current command contains some plausible typing-intent wording (English and
Hindi/Hinglish - `type`, `write`, `enter`, `fill`, `टाइप`, `लिख`, `भर`, `दर्ज`, `डाल`, `likho`,
`bharo`, `darj`, `dalo`, etc.). If none match, the call is rejected before the real keystrokes
ever happen (`TaskStepStatus.Skipped`, a tool-result message telling the model to answer in text
instead) - this is checked ahead of the policy engine, since it's a semantic precondition
("is this actually a typing request at all"), not a risk-tier decision. Deliberately biased
toward under-blocking rather than over-blocking: the keyword list is broad and includes common
Hinglish transliterations specifically because letting a real typing request through
occasionally is far less costly than refusing one falsely.

**Verified directly against real inputs, not just reasoned about:** an isolated probe ran the
exact detector logic against the literal command from this screenshot ("आपका नाम क्या है?" -
correctly blocked) plus several legitimate typing requests in English and Hinglish ("Notepad me
hello likho", "Type 'hello world' in notepad", "इस फील्ड में मेरा नाम दर्ज करो", "form bharo
mera naam se" - all correctly allowed). `dotnet build` clean, `dotnet test tests/Unit` all 31
passing.

## Full dashboard rebuild: single chat window -> 12-page shell (2026-09-13)

The user provided a reference screenshot of a complete dashboard design (dark neon theme,
custom window chrome, a 12-item sidebar, a Dashboard home page with a hero section, feature
cards, Quick Actions, Recent Tasks, an AI Providers status panel, 4 circular system-metric
gauges, a live screen-preview thumbnail, and a shared bottom command bar) and asked for the
whole app to be rebuilt to match it, with every section genuinely working, not stubbed. Given
the scale, this went through `EnterPlanMode`: two parallel research passes first (current WPF
structure/styling; real backend capability per planned page), then a dedicated planning pass,
before any code was written - see the approved plan for the full architecture rationale.

**Before:** `NoniPilot.Desktop` was a single `MainWindow` (chat transcript + command bar) plus
two popup windows (`AiProvidersWindow`, `GestureCenterWindow`). No shared theme/resource
dictionary existed anywhere - each window hardcoded its own slightly-different colors. No
navigation, no MVVM, no charting/gauge library, no system-metrics code anywhere in the repo
(confirmed by repo-wide search before writing anything).

**After - the shell:** `MainWindow` is now a custom-chrome shell (`WindowStyle="None"` +
`WindowChrome` attached properties for drag/resize, hand-built minimize/maximize/close buttons
with `IsHitTestVisibleInChrome`), a header (AI-provider status pill, STOP, a live clock, a user
greeting), a sidebar `ListBox` of 12 items, and a `ContentControl` that swaps cached
`UserControl` pages. No `Frame`/`Page` navigation stack - deliberately avoided, since sidebar
nav is absolute (not back/forward), and `UserControl` fit the codebase's existing
direct-code-behind convention (no MVVM rewrite attempted).

**`AppServices`** (`Services/AppServices.cs`) is the new single composition root - the `new
WindowsFileSystemService()` etc. block that used to live in `MainWindow`'s constructor moved
here, constructed once, shared by every page (including the chat transcript, the planner/router,
and a shared `GestureEngineController` so the Dashboard's Gesture Mode toggle and the Gesture
Control page's Start/Stop button control the *same* running camera session, not two independent
ones). `Services/CommandProcessor.cs` factors out everything `MainWindow.ProcessCommandAsync`/
`VoiceModeLoopAsync` used to do, so the Voice Commands page and the Dashboard's own command bar
drive identical, synchronized state. `Services/PolicyGatedActionRunner.cs` generalizes
`ToolCallingPlannerService.ExecuteToolAsync`'s evaluate -> confirm -> execute -> audit sequence
for every page button that calls a service directly instead of going through the AI planner -
this was a hard invariant to preserve: no new page bypasses the policy gate.

**Design system:** new `Theme/Colors.xaml` and `Theme/Controls.xaml`, merged into
`App.xaml`'s `Application.Resources` (previously empty). Promotes `MainWindow`'s original
palette/gradients/glows to shared, app-wide resources instead of three windows each hardcoding
their own hex values. New reusable controls: `Controls/CircularGauge.xaml(.cs)` (hand-built ring
gauge via `ArcSegment` geometry + polar-to-cartesian math in code-behind - no gauge/charting
NuGet exists anywhere in the solution, confirmed before building this), `Controls/StatusPill`,
`Controls/ToggleSwitch`.

**New, real backend work for the 3 previously-stub sidebar areas** (not fake UI over nothing):
- `FileSystem.CreateFile` added to `IFileSystemService`/`WindowsFileSystemService` (+ a matching
  `filesystem_create_file` `ToolCatalog` entry, so the AI agent gains the same capability, not
  just the button) - backs Dashboard's "Create New File" tile and the Files & Folders page.
- **Browser Automation**: `src/NoniPilot.Browser/BrowserService.cs` now actually implements the
  previously-empty `IBrowserService` - `Process.Start(url, UseShellExecute=true)`, the same
  mechanism already proven for protocol/URL activation in `WindowsApplicationService`.
  Deliberately not a Selenium/Playwright engine - URL-open + a persisted bookmark list
  (`Services/BrowserBookmarkStore.cs`) is the real, scoped MVP.
- **System Tools**: reuses `IApplicationService`/`IComputerControlService` (process list/close,
  common `SendKeys` shortcuts) plus one new thin `Services/SystemPowerActions.cs`
  (Shutdown/Restart/Lock/CancelPending via `shutdown.exe` and `LockWorkStation` P/Invoke).
- **Automation**: a saved "command sequence" recorder/player built directly on the existing
  `IPlannerService.ExecuteAsync` (`Services/AutomationSequenceStore.cs` +
  `Models/AutomationSequence.cs`) - explicitly not a low-level keystroke/mouse macro recorder,
  and there's no scheduling, only a manual Run button.
- **Task History**: pure new UI over `IAuditService.QueryAsync` - confirmed by grep that nothing
  else in the codebase had ever called it, even though every action was already being written to
  the SQLite audit log the whole session.
- **AI Providers**: `Services/AiProviderHealthChecker.cs` adds a real live reachability check for
  Local (Ollama) via its `/api/tags` endpoint - Groq/Claude pills stay config+env-var-inferred
  only, deliberately not pinged, to avoid spending real API quota just to render a status dot.
- **System metrics** (`Services/SystemMetricsService.cs`, new packages
  `System.Diagnostics.PerformanceCounter` + `System.Drawing.Common`): CPU via
  `PerformanceCounter`, Memory via `GlobalMemoryStatusEx` P/Invoke, Disk via `DriveInfo`, GPU via
  the `"GPU Engine"` counter category (renders "N/A" rather than throwing if no instances exist).
  Only runs while Dashboard is the active page (`INavigablePage.OnNavigatedTo/From`) given this
  machine's RAM has measured as low as 2.5GB free earlier the same day - an always-on timer
  would make that worse for no benefit while nobody's looking at the gauges.
- **Live Preview / screenshots** (`Services/ScreenCaptureService.cs`): one reusable
  `Bitmap`/`Graphics` pair, `CopyFromScreen` on a 3s timer (Dashboard-only), converted to a WPF
  `BitmapSource` with an explicit `DeleteObject` P/Invoke on the returned HBITMAP every tick
  (a well-known GDI handle leak otherwise) - shared with the "Take Screenshot" quick action.

**Bugs found and fixed while verifying this live (not just built-and-assumed-working):**
- `PolicyService.DefaultClassificationTable` added as a public read-only view of the built-in
  risk table, so the new Settings page could display it without duplicating the list - the
  underlying `DefaultClassification` dictionary stays private and is still the only thing
  actually used for policy evaluation.
- A real, reproduced bug: `AiProviderHealthChecker`'s Ollama reachability probe used a 2-second
  timeout and was reporting "Unreachable" for a genuinely healthy local server. Reproduced in
  isolation: a plain `curl` to the same URL completed in ~0.2s, but the exact same C# HTTP call
  took 2.6+ seconds and timed out at 2s - almost certainly .NET's HttpClient trying an IPv6
  (`::1`) route before falling back to IPv4 for `"localhost"`, a known Windows quirk. Fixed by
  raising the timeout to 5s (this is a background status check, not something blocking the
  user) - re-verified with the same isolated repro, then confirmed live in the running app that
  the "Local AI (Ollama)" pill correctly reads "Active".

**Verified live, end to end, not just "it builds":** launched the rebuilt app, used UI Automation
to click through all 12 sidebar items with zero crashes, confirmed Files & Folders lists the
real Desktop, created a real throwaway test folder through the UI (`Create New` -> `New Folder`)
and confirmed it landed on disk, confirmed Task History immediately showed that exact action
with a green "Success" pill alongside the full real history from earlier in the session
(including an old red "Failure" pill rendering correctly), then deleted the test folder to leave
no trace. Confirmed the 4 `CircularGauge` controls render live, correctly-proportioned arcs with
real CPU/Memory/Disk/GPU readings. `dotnet build` clean (0 errors), `dotnet test tests/Unit` all
31 passing throughout.

**Known gaps, disclosed rather than silently left:** Voice Commands/Gesture Control pages were
smoke-tested for crash-free navigation but not exhaustively re-verified turn-by-turn in this
pass (their underlying logic is a direct port of code already verified working earlier the same
day). The AI Providers page's own "Check status" probe was not independently re-clicked after
the timeout fix (only the Dashboard panel and an isolated repro were). Automation/Browser
Automation/System Tools pages were exercised via navigation only, not a full run of each button
(e.g. a saved Automation sequence was not actually executed end-to-end in this pass).

## Dashboard follow-up: a real latency/wrong-folder bug, clickable feature tiles, and a faster path for common commands (2026-09-13)

Live feedback on the rebuilt dashboard, with screenshots: "open the desktop" took ~3 minutes and
landed on the wrong folder; the 6 feature tiles (Voice/Vision/Gesture/AI Agent/Computer/
Automation) were purely decorative; Live Preview sat at the bottom of the right column instead
of the top and had no way to view it larger/on another monitor; and the AI generally "responds
very slow."

**The wrong-folder bug was real, and explains a chunk of the slowness too.** Asked to "open the
desktop," the model called `filesystem_open_best_match` with the Desktop as the search root and
"desktop" as the name hint - and because a folder literally named `Desktop` happens to exist
*inside* the real Desktop (confirmed by inspecting the actual directory listing), the fuzzy
matcher correctly found an exact name match... to the wrong thing. The user never asked to
search inside the Desktop for something called desktop; they asked to open the Desktop itself,
a request with a well-known, entirely static answer that never needed an LLM round-trip at all.

**Fix: a genuine fast path, not a smarter prompt.** `LocalIntentService` (already the home of the
"stop" fast path) gained a second, looser matching mode: `open_desktop`/`open_documents`/
`open_downloads`/`open_pictures`/`open_music`/`open_videos`, matched when the command contains an
open-verb (open/show/khol/kholo/kholiye) plus the folder's name, is six words or fewer, and
contains none of a "this is actually more specific" exclusion list (create/delete/rename/copy/
move/search/find/inside/named/"new folder"/"file"/etc.) - so "open the desktop" matches but
"create a new folder called Desktop backup" correctly does not. `CommandProcessor` now handles
these tokens by resolving the real `Environment.SpecialFolder` path and calling
`Applications.LaunchAsync` directly through the existing `PolicyGatedActionRunner` - no planner,
no LLM call, no fuzzy search. **Verified live**, not just unit-tested: a probe against the exact
matcher confirmed 11/11 cases correct (the real "open the desktop" phrasing, several
create/search/rename phrasings that must NOT be short-circuited, "stop" still intact), and then
the actual app was driven end-to-end via UI Automation - "open the desktop" now opens the real
top-level Desktop (confirmed via the Explorer window's own breadcrumb and a 112-item listing,
not the small nested folder from the bug report) and responds in about a second, not minutes.

**General latency: a real, bounded lever, not a rewrite.** `ToolRelevanceFilter`
(`NoniPilot.Agent/Tools/`) trims which tool schemas are actually sent to the model per command -
every tool's full JSON schema is real prompt-token weight on every single request, a direct,
measurable cost on CPU-only local inference. Deliberately conservative: each of the three tool
categories (FileSystem/Application/ComputerControl) is *included* unless the command clearly
matches a keyword set for a *different* category and not this one, and if a command matches no
category at all, every tool is still sent (identical to pre-filter behavior) rather than
guessing. `ToolCallingPlannerService.ExecuteAsync` uses this to build what's sent to the model,
while `toolsByName` (used to actually dispatch a call) still holds the full, unfiltered catalog.
Unit-tested (`ToolRelevanceFilterTests.cs`, 4 cases: computer-control-only, filesystem-only,
application-launch, and the no-match-sends-everything fallback).

**Dashboard fixes:**
- The 6 feature tiles are now real `Button`s (the `QuickActionTile` style, with its existing
  hover glow) wired to whichever page actually backs that capability: Voice → Voice Commands,
  Gesture → Gesture Control, AI Agent → AI Providers, Computer → Computer Control, Automation →
  Automation. **Vision** has no dedicated page (`NoniPilot.Vision` is still an unimplemented stub
  project) - rather than navigate nowhere or fake a destination, it opens the Live Preview
  pop-out (below), since "understand your screen" and a literal live screen capture are the
  closest real, honest match available today. Verified live via UI Automation: clicking the
  "Computer" tile correctly selects "Computer Control" in the sidebar.
- The right column was reordered so **Live Preview is now first** (previously last, below AI
  Providers and System Status).
- New `LivePreviewWindow.xaml(.cs)`: a real, separate, normally-chromed `Window` (deliberately
  *not* borderless like the shell) opened by a new "⛶" button next to Live Preview's header and
  by clicking the preview thumbnail itself, so it can be dragged to another monitor and maximized
  there using the OS's own window management - "extendable to other screen with full screen"
  from the request. It subscribes to the *same* `ScreenCaptureService` instance the Dashboard
  page already owns rather than starting a second capture timer. Verified live: clicking both the
  button and the thumbnail actually opens the window (confirmed via `EnumWindows`, not just UI
  Automation's own element tree, which turned out to be an unreliable way to detect a
  just-created top-level window in this test harness); the opened window visibly updates live
  (its own recursive self-capture effect - pointing the capture at a screen that now includes the
  preview window itself - incidentally proved the feed is genuinely live, not a static image).

**What was and wasn't verified:** the fast-path folder-open and the tool-relevance filter were
both verified against real, live app behavior end to end. The Dashboard click-through changes
were verified for the one tile explicitly tested (Computer) and the Live Preview pop-out;
the other four feature-tile destinations were wired identically but not each individually
re-clicked in this pass. General response latency for *non-fast-path* commands (i.e. anything
still requiring the LLM) was not re-benchmarked before/after the tool-filter change - the fix is
real and its mechanism (less prompt weight per request) is sound, but no side-by-side timing
comparison was captured.

## Live Preview singleton bug, and a much broader "open X" fast path (2026-09-13)

Live feedback, with screenshots: repeated clicks on the Live Preview button/thumbnail opened a
new window each time (visibly nested/recursive since each window's own capture included the
ones already open), and minimizing one minimized all of them together (WPF's owned-window
behavior, since every one of them shared the same `Owner`); "open the desktop" was fixed the
previous round, but "open AMBERG IT" and other non-special-folder/app requests still went
through the full LLM round-trip and were still slow; the user explicitly authorized changing
whatever code was necessary and asked for responses "exactly the same time" - i.e. treat this as
a real problem to keep solving, not a closed one.

**Live Preview: fixed to a real singleton.** `DashboardPage` now holds a nullable
`_livePreviewWindow` field. Opening it (via the "⛶" button, the thumbnail click, or the "Vision"
feature tile - all three call the same method) reuses the existing window if one is already
open (restoring it if minimized and bringing it to front) instead of constructing a new one;
the field is cleared via the window's `Closed` event so a fresh one can be created after the
user actually closes it. **Verified live:** clicked the fullscreen button 4 times in a row,
counted actual OS windows via `EnumWindows` (not UI Automation's element tree, which had already
proven unreliable for this in the prior round) - exactly 1 window existed afterward, not 4.

**A much broader local fast path for "open X" requests - the actual biggest lever for perceived
slowness.** The previous round's fast path only covered six hardcoded special folders
(Desktop/Documents/Downloads/Pictures/Music/Videos). Almost every other real request in this
session's own history was some form of "open <app>" or "open <named folder>" - both fully
LLM-free-able. `CommandProcessor` now has a second, more general layer: `TryExtractOpenTarget`
pulls a bare target out of any short "open/show/khol/kholo/kholiye X [folder]" command (skipping
anything containing words that mean it's actually more specific - create/delete/rename/search/
etc., so those still correctly go to the full planner), and
`TryHandleOpenTargetFastPathAsync` tries, entirely locally: (1) launch it as an application
(covers "open chrome"/"open notepad"/"open calculator" - anything `WindowsApplicationService`
can already resolve), then (2) fuzzy-match it against the Desktop's contents via the existing
`FuzzyNameMatcher` and open the best match (covers "open AMBERG IT [folder]", the single most
common real request pattern in this session's own audit history) - both attempts go through the
same `PolicyGatedActionRunner` as everything else, so nothing here skips auditing or risk
classification. Only if *both* attempts fail does it fall through to the full LLM-backed
planner, so nothing that used to work can start failing - a wrong guess just costs two cheap
local attempts, not an incorrect action. **Verified live, end to end:** "open AMBERG IT" now
opens the real 71-item folder in ~1.2 seconds (was: a full LLM round-trip); "open chrome"
launches Chrome in ~1.2 seconds - both fully bypassing the model.

**What this does and doesn't solve, stated plainly:** this covers the specific, extremely
common "open X" shape of command near-instantly. It does **not** speed up genuinely open-ended
requests - a conversational question, a multi-step task, anything the fast path can't
confidently resolve locally - those still go through the CPU-only local model and are bounded by
its real inference speed on this hardware. The next available lever for *that* remaining case is
trying a smaller/faster local model (a real quality-for-speed trade-off, and a multi-GB download
- a decision for the user, not made unilaterally in this pass).

## Default local model switched: llama3.1 (8B) -> qwen2.5:3b, with measured evidence (2026-09-13)

Asked directly whether to try a smaller/faster local model, the user said yes, and also asked
whether a model could be trained in-house instead of "depending on others." Answered honestly
before doing anything: training a new foundation model from scratch is not feasible on this
hardware (or on any single consumer machine) - that needs infrastructure no local setup has.
What's genuinely available is switching to a smaller *open-weights* model that still runs
100% locally forever once downloaded, with no ongoing dependency on any live service that can
rate-limit or disappear - which is the part of "depending on others" that actually caused pain
this session (Groq's quota). The base weights are still originally trained by someone else
(true of every practical local LLM); the property that matters here - no company can cut off
access mid-task - is unaffected by that.

**Pulled `qwen2.5:3b` via Ollama's `/api/pull` API** (3.1B params, 1.9GB on disk, vs
llama3.1's 8B/4.9GB) and confirmed via `/api/tags` that it declares the same `"tools"`
capability llama3.1 does, plus a much larger native context window (32768 vs llama3.1's
default-loaded 4096 - see the earlier context-ceiling finding in this file).

**Measured, not guessed, before switching anything:** wrote a probe using the actual
`OpenAiCompatibleChatProvider` class (not a reimplementation) to run the *same* two realistic
prompts against both models back-to-back:
- A plain greeting ("Hey, what's your name?"): qwen2.5:3b answered directly and correctly in
  ~11.2s; llama3.1 took ~27.0s and - consistent with an earlier-documented bug in this same
  file - tried to call a tool for it instead of just answering, producing an empty final-text
  result.
- A genuine tool-calling request ("list the files in Downloads"): qwen2.5:3b correctly called
  `filesystem_list_directory` in ~6.2s; llama3.1 also called it correctly, in ~8.6s.

qwen2.5:3b was faster in both cases and more correct in one of them - not a marginal trade-off.
`AiProviderSettings.OllamaModel` default changed to `"qwen2.5:3b"`, and the user's actual running
`%LOCALAPPDATA%\NoniPilot\ai-providers.json` was updated to match immediately. llama3.1 is left
installed (not deleted) so reverting the one setting value is enough if tool-calling reliability
on qwen2.5:3b ever regresses noticeably in real use - no re-download needed either way.

**Verified live in the actual app, not just the probe:** relaunched, asked "what is your name
and what can you help me with" via the Voice Commands page - a correct, on-topic, single-sentence
answer came back with the status already reading "Ready" (task fully complete) well before the
generic elapsed-time indicator would even suggest concern. `dotnet build` clean, `dotnet test
tests/Unit` all 35 passing (unaffected by this change - no code touched, only the default
setting).

## Four more real issues after the model switch: a tool-call regression, phantom self-listening, wake-word mode, and honest camera awareness (2026-09-13)

The qwen2.5:3b switch introduced a real regression, live feedback also surfaced a second,
independent real bug, and the user asked for two new capabilities explicitly.

**Regression: tool calls sometimes rendered as literal text instead of executing.**
Screenshots showed `Open YouTube in Google Chrome` produce a chat bubble containing literal
`{"name": "application_launch", "arguments": {...}}</tool_call>` text - never actually executed.
Root cause: qwen2.5 (Hermes-style tool-calling) sometimes emits a tool call as inline text in the
`content` field - `<tool_call>{...}</tool_call>` - instead of Ollama's OpenAI-compatibility layer
translating it into the structured `tool_calls` field `OpenAiCompatibleChatProvider` was
exclusively reading. Fixed with `ExtractInlineToolCalls`: when the structured field comes back
empty, the raw content is scanned for one or more `<tool_call>{...}</tool_call>` blocks via
regex, each parsed into a real `ChatToolCall` (handling both the case where the API's official
field would be present and the inline case), and the matched text is stripped from what's
shown/spoken. Only runs when the structured field was empty, so a model already doing it
correctly is never second-guessed. **Verified via a reflection-based probe against the exact
real text from the screenshot** (extracts both chained tool calls correctly), plus a normal-prose
passthrough case and a malformed-block case (skipped safely, doesn't throw) - 4/4 correct.

**Real bug: phantom "Thank you. Thank you." messages appearing to come from the user.**
Screenshots showed the exact same generic phrase appearing as if the user had said it, right
after NoniPilot itself finished speaking - a classic sign of the assistant's own voice bleeding
back into the microphone and being misheard as a new command (a well-documented STT failure
mode: ambiguous/quiet/echo audio regressing to a common generic phrase). Two changes:
`NAudioMicrophoneRecorder` now tracks *cumulative* loud duration, not just "was it ever loud" -
a recording under 220ms of real loud audio is now treated as having captured no speech at all
(`RecordUntilSilenceAsync` returns empty rather than sending faint echo to STT), tuned so real
short words ("stop", "haan") still clear it comfortably. `CommandProcessor`'s post-speech buffer
before the mic reopens was raised from 400ms to 900ms as defense in depth (the duration gate is
the primary fix, not this). **Not independently re-verified live** - reproducing acoustic
speaker-into-mic bleed on demand isn't something a UI-automation script can trigger; this relies
on the mechanism being sound, not a live before/after repro.

**New feature: wake-word listening ("Hey Noni" / "Jago Noni"), alongside the existing Talk
button, not replacing it.** `CommandProcessor` gained a second, independent background loop
(`WakeWordLoopAsync`) that runs short (4s) listen-and-transcribe cycles whenever full
conversation mode (`VoiceModeActive`) isn't already running, checking the transcript against a
phrase list (English + Hindi/Hinglish + Devanagari: "hey noni", "wake up noni", "sun noni",
"jago noni", "जागो नोनी", "सुनो नोनी", etc.). A match starts full voice mode automatically - and
if the user said their actual command in the same breath ("Hey Noni, open chrome"), the
remainder after stripping the wake phrase is processed immediately as that first command,
instead of waiting for a separate utterance. A new `SemaphoreSlim` (`_micSemaphore`) makes the
wake loop and the full conversation loop mutually exclusive over the one microphone, since
`NAudioMicrophoneRecorder` isn't reentrant. Defaults **on** at app startup
(`MainWindow` calls `StartWakeWordMode()`), with a visible `ToggleSwitch` on the Voice Commands
page to turn it back off, given this means the mic is now passively listening from launch - a
real, disclosed trade-off, not a silent one. **Verified the phrase-matching/stripping logic**
via an isolated probe (6 cases incl. Hindi/Devanagari, all correct); **not verified with real
spoken audio** - that requires an actual voice, which an automated pass can't produce.

**New feature: honest camera/gesture awareness for "can you see me?".** Previously the model
could only guess or hallucinate an answer to this, since it had no real way to know the camera's
state. New `IGestureAwarenessService` (`NoniPilot.Domain`) + `GestureAwarenessStatus` record,
implemented by `GestureEngineController` (tracks the real current state: is the camera running,
was a gesture recognized within the last 2 seconds, which one) - deliberately not a vision/scene-
description capability (`NoniPilot.Vision` is still an unimplemented stub); it can only ever
report real hand-tracking state, never a general description of the room or the user's activity.
New `gesture_check_visibility` tool (`Gesture.CheckVisibility` → `L0Safe`, read-only) threaded
through `ToolCatalog.Build`/`ToolCallingPlannerService`'s constructor as an optional dependency,
with a new system-prompt rule telling the model to always call it before answering any
"can you see me"/"kya tum mujhe dekh rahe ho" question rather than guessing. **Verified live in
the actual running app**: asked "can you see me right now" via the Voice Commands page with the
gesture camera off - got back "I can't see you right now. Do you mind turning on the
gesture-tracking camera..." - a correct, honest, non-hallucinated answer reflecting the real
camera state, not a canned response.

`dotnet build` clean, `dotnet test tests/Unit` all 35 passing throughout all four changes.

## Wake word wasn't responding: a real Groq-in-voice inconsistency, plus camera auto-start and a "sleep" command (2026-09-13)

User reported "Hey Noni"/"wake up" wasn't responding at all, asked for the gesture camera to
also default to on (not just the mic), and asked for a "sleep"/"so jao" voice command to
manually quiet an active conversation.

**Root cause found for the unresponsive wake word, confirmed via the registry, not guessed.**
`[Environment]::GetEnvironmentVariable("GROQ_API_KEY", "User")` showed the key is still set at
the Windows user-environment level from earlier the same day - meaning every fresh launch of
NoniPilot (a GUI process, inheriting that scope) still picks it up, even though the user
disabled Groq for chat hours ago. `VoiceServiceFactory.BuildSpeechToText` never actually checked
`AiProviderSettings.EnableGroq` - it only ever checked whether `GROQ_API_KEY` happened to be
present, completely independent of the user's chat provider choice. This meant *every single
voice listen cycle* - including the wake-word loop's short 4-second cycles - was silently trying
a Groq Whisper network call first, against an account already confirmed rate-limited/quota-
exhausted earlier this session, before ever falling back to the offline engine. This directly
contradicts the user's own explicit, foundational decision earlier the same day ("no external
token that can run out"), and plausibly explains most or all of the "not responding" symptom.
Fixed: `BuildSpeechToText` now takes an `enableGroq` parameter and only adds the Groq provider
when it's true, mirroring exactly how `AiProviderFactory.Build` already gates Groq for chat -
"Groq off" now actually means off everywhere, not just for chat. Both `AppServices` call sites
updated to pass `settings.EnableGroq`. **Not independently re-verified with real spoken audio**
(this session's own test tooling doesn't inherit the same user-environment refresh a real GUI
launch does, so the exact before/after latency difference couldn't be measured end-to-end here)
- the inconsistency itself, though, is confirmed and fixed regardless.

**Camera now auto-starts too, matching the mic.** `MainWindow`'s constructor now also calls
`_services.GestureEngine.Start(GestureCalibrationStore.Load(), ...)` right after starting
wake-word mode, so both camera and mic are active from launch, per explicit request - stopping
either is still just the existing Dashboard "Gesture Mode" toggle or the Gesture Control page's
Stop Camera button. **Verified live**: fresh launch, no manual action taken, Dashboard correctly
showed "Camera On" / "Gesture Active" / "Gesture Mode: On" immediately.

**New "sleep" voice command.** `LocalIntentService` gained a second exact-phrase fast-path
intent, `"sleep"` (phrases: sleep, go to sleep, so jao, so jaao, sula do, sona hai, सो जाओ, etc.),
handled in `CommandProcessor` by calling the existing `StopVoiceMode()` (return to passive
wake-word-only listening) rather than `StopEverything()` (which would also emergency-stop
in-flight computer control - too heavy-handed for "I just want quiet"). Responds with "Going
quiet - say \"Hey Noni\" when you need me." **Verified live end-to-end**: entered active voice
mode via Talk, sent "sleep" as text, confirmed the Talk button reverted from "Stop Talking" to
"Talk" and the exact confirmation message appeared.

`dotnet build` clean, `dotnet test tests/Unit` all 35 passing.

## Wake word still didn't fire: real root cause was dictation accuracy on an invented name, not the Groq/wiring bug (2026-09-13)

After the Groq-gating fix, the user relaunched and reported "Hey Noni" still produced no
response at all - toggle on, empty chat, "Ready" status, no error. The previous fix was real but
not sufficient: it fixed an inconsistency, not the actual failure to hear the wake word.

**Root cause, empirically measured, not guessed.** `WakeWordLoopAsync` was feeding recorded
audio through `_services.SpeechToText.TranscribeAsync` - the same general-purpose STT used for
full conversation (Windows offline `DictationGrammar` when Groq is off) - then string-matching
the transcript against `WakePhrases`. Built an isolated probe (`System.Speech.Synthesis` to
generate a "Hey Noni" WAV, fed into the exact same `WindowsSpeechToTextService` code path) and
measured what the recognizer actually produced: **"Denoting" at 0.4% confidence**, with "hey
noni" never appearing anywhere in its top 10 alternates. Open-vocabulary dictation has no
language-model path to an invented name like "Noni" - it was never going to transcribe it
correctly, no matter how clearly it was spoken. This was a pure STT-accuracy bug, not a wiring,
threading, or exception-swallowing bug (all of which were also inspected and ruled out: the mic
semaphore, the loop's cancellation handling, and the `MinimumLoudMilliseconds` gate were all
confirmed fine).

**Fix**: new `NoniPilot.Voice.WakePhraseRecognizer` - a *constrained-grammar* recognizer built
from `System.Speech.Recognition.Grammar`/`GrammarBuilder`/`Choices` containing only the exact
wake phrases, instead of free dictation. A closed-set grammar only has to do acoustic matching
against a handful of known options, not open transcription. Measured the same synthesized "Hey
Noni" clip against this constrained grammar: **93.5% confidence, exact match**. Also confirmed
it correctly returns no match at all for unrelated speech ("Open Chrome please" → null), so it
won't misfire on ordinary conversation. `WakeWordLoopAsync` now calls
`WakePhraseRecognizer.TryMatchAsync` (with a 0.5 confidence floor as defense in depth against a
forced low-confidence match on pure noise) instead of the general `SpeechToText` provider - wake
detection and full-command transcription are different problems (closed-set matching vs. open
transcription) and were always going to need different recognizers, not the same one reused.

**Traded off**: dropped the "say the wake word and the command in one breath" combo path (e.g.
"Hey Noni, open chrome") - a closed-set grammar can't also capture arbitrary trailing free text
without a hybrid grammar, which was more complexity than the reported bug justified. The
practical effect is a two-step interaction instead of one: say "Hey Noni", wait for it to start
listening (status now shows "Woke up!" then "Listening..."), then say the command - `
VoiceModeLoopAsync` picks it up immediately afterward via the normal STT path either way.

**Also added**: `WakeWordLoopAsync` now calls `SetStatus("Listening for \"Hey Noni\"...")` at
the top of every cycle. Previously it set no status at all while passively listening, so there
was no visible difference between "idle/off" and "actively listening" in the UI - itself likely
contributing to the perception that it wasn't listening, independent of the recognition bug
above.

**Verification**: `dotnet build` clean, `dotnet test tests/Unit` all 35 passing. The constrained
grammar was verified against synthesized audio (both positive: "Hey Noni" matches at 93.5%, and
negative: unrelated speech produces no match) via an isolated console probe using the same
`System.Speech` APIs as the shipped code. **Not yet verified against real live human speech in
the running app** - synthesized TTS audio is a strong proxy for testing recognizer behavior but
is not a substitute for an actual microphone/room/accent test.

## Wake word confirmed working live; local Whisper STT replaces Windows dictation for real commands (2026-09-13)

User tested "Hey Noni" for real after the constrained-grammar fix above, with a debug log
temporarily added to `WakePhraseRecognizer`/`CommandProcessor`/`NAudioMicrophoneRecorder`
(`%LOCALAPPDATA%\NoniPilot\wake-word-debug.log`) to get hard evidence instead of guessing again.
The log proved the wake phrase itself now works (`text=[hey noni] confidence=0.765`,
`confidence=0.845` on a second attempt) - the constrained-grammar fix was real. Added a spoken
"Yes?" acknowledgment on wake (previously only a status-text change, easy to miss) plus a 400ms
settle delay before the mic reopens.

**But the follow-up command consistently failed** - the same log showed two distinct real
problems: (1) many recordings measured peak RMS of only 0.007-0.02, at or under the mic-loudness
gate's 0.02 threshold, so real speech was being discarded before STT ever ran (fixed: lowered
`AmplitudeThreshold` to 0.012, and lowered the wake-grammar confidence floor from an initial 0.5
to 0.15, both justified by these live measurements, not guesses); (2) on the one attempt that did
capture clear audio, Windows' offline dictation engine transcribed it as **"The latino"** -
nothing like what was said. This is the same fundamental problem as the original wake-word bug
(SAPI dictation has no reliable path to open-ended real speech) but this time unfixable with a
closed grammar, since a real command can be anything.

**Fix, after presenting the finding and the real trade-off to the user (chosen: local Whisper
over re-enabling Groq or further threshold tuning)**: new
`NoniPilot.Voice.WhisperLocalSpeechToTextService` using Whisper.net/whisper.cpp - a downloaded
ggml "base" multilingual model (~140MB, cached at `%LOCALAPPDATA%\NoniPilot\models`), fully
offline, no account/token/rate-limit. Verified end-to-end via a synthesized "open google chrome"
clip through the exact production audio pipeline: **"Open Google Chrome."** - correct, versus the
old engine's "The latino." Wired into `VoiceServiceFactory.BuildSpeechToText` ahead of
`WindowsSpeechToTextService` (which stays as a last-resort fallback only). Whisper.net requires
16kHz input, which the recorder already produces, so no pipeline changes were needed elsewhere.

**Verification**: `dotnet build`/`dotnet test tests/Unit` (35/35) clean. Whisper transcription
verified via isolated probe against a real 16kHz synthesized clip (perfect transcription). Mic
threshold change is a direct response to measured real-hardware RMS values, not a guess.

## Three real bugs found from one live report: false "closed" success, model skipping tool calls for close, no fast path for close/YouTube (2026-09-13)

User reported, after Whisper landed: "open google chrome" worked but slow; "close google chrome"
claimed success but Chrome was still running (confirmed by its own "didn't shut down correctly"
restore-session prompt on the next launch); "open youtube in new tab and play this song" did
nothing. Investigated each with direct evidence (the audit database, `Process.GetProcessById`
probes) rather than guessing:

- **Bug 1, `WindowsApplicationService.CloseAsync`**: called `process.CloseMainWindow()` and then
  *unconditionally returned `true`*, ignoring the method's own return value (which reports
  whether a close message was even accepted, not whether the app exited) and ignoring whether the
  process actually exited afterward. Fixed: now returns `false` outright if `CloseMainWindow()`
  itself refuses (e.g. no window/message loop), and otherwise waits up to 3s for the process to
  actually exit via `WaitForExitAsync`, reporting failure honestly if it doesn't (e.g. a
  "save changes?" dialog blocking the close - reproduced live with a stuck Notepad tab from
  Windows 11's session-restore).
- **Bug 2, `BasicVerificationService`**: `Application.Close` had no dedicated verifier, so it fell
  into the generic "no dedicated verifier yet" case, which **always reports `Verified: true`** -
  meaning even after fixing bug 1, the AI-planner path would still tell the model (and the user)
  "closed successfully" regardless of what `CloseAsync` actually returned. Added
  `VerifyProcessClosed`: reads the tool's own `closed` result first, then independently
  double-checks the process itself has actually exited.
- **Bug 3, root cause of "close google chrome" doing nothing at all**: confirmed via the audit
  database that the local model (qwen2.5:3b) never called `application_close` for that request at
  all - it just replied claiming success in plain text, no tool call, no audit trail. Same
  "weak local model unreliably decides whether to call a tool" failure mode already solved once
  for "open X" via a local fast path - solved the same way here: `CommandProcessor` gained
  `TryHandleCloseTargetFastPathAsync` (fuzzy-matches the target against
  `application_list_running`, closes it directly, reports the real captured result - **not**
  `PolicyGatedActionRunner`'s own "Success" bool, which was discovered to have the identical bug
  as bug 1: it takes a bare `Func<CancellationToken, Task>` and reports success as soon as the
  delegate returns without throwing, blind to any real return value inside it. Every fast path
  using `RunAsync` for an action with a meaningful boolean result now captures it via closure
  rather than trusting `RunAsync`'s own tuple.
- **New: YouTube fast path.** The model was also treating "youtube" as a literal launchable app
  name and failing ("the system cannot find the file specified"). `TryHandleYouTubeFastPathAsync`
  recognizes "play/search/find X on YouTube" and opens a YouTube search-results URL directly
  (`https://www.youtube.com/results?search_query=...`) via `IBrowserService` - explicitly a
  search, not literal autoplay, which would need real page automation this app doesn't have (see
  `BrowserService`'s own doc comment on that scope decision).

**Verified live end-to-end** (not just built): "open calculator" -> "close calculator" actually
closes it (confirmed via `Get-Process`); "play believer on youtube" opened Chrome to a real
YouTube search results page for "believer" (confirmed via window title and the
`Browser.OpenUrl` audit entry). `dotnet build`/`dotnet test tests/Unit` (35/35) clean throughout.

## Camera corner preview window (2026-09-13)

User asked to see the actual camera feed persistently in the corner of the screen, for
visibility into what NoniPilot's camera can see, not just a status pill. New
`CameraCornerPreviewWindow` - small (220x170), borderless, always-on-top, no taskbar entry,
pinned near the top-right of the screen, subscribing to the same `GestureEngineController`
instance every other camera consumer shares. `MainWindow` shows/hides it automatically based on
`GestureEngine.StateChanged` (appears the moment the camera turns on - including at launch, since
the camera is on by default - and closes itself when the camera is turned off). Verified live via
`EnumWindows`: a window titled "NoniPilot - Camera" appears on launch alongside the main shell.

## Face recognition: "recognize it's me" (2026-09-13)

User asked for face recognition "for future coordination and chatting". Scoped with the user via
`AskUserQuestion` before building: single enrolled person (not multi-person-by-name), storage
local-only (matches the whole app's "fully local, no dependency on others" principle every other
decision this session already followed).

**No new heavy ML dependency needed.** `OpenCvSharp.CascadeClassifier` (Haar-cascade face
detection) and `OpenCvSharp.Face.LBPHFaceRecognizer` (Local Binary Patterns Histograms
recognition) are both already present in the `OpenCvSharp4`/`OpenCvSharp4.runtime.win` packages
this project already depends on for gesture tracking - verified via an isolated reflection +
live probe (Train/Predict/Write/Read all worked) before committing to this design, rather than
assuming. LBPH trains directly from a handful of the user's own captured face images, so there
was no pretrained face-embedding model to source at all (unlike gesture tracking's ONNX hand
model) - only the standard, well-known `haarcascade_frontalface_default.xml` (~900KB, part of
the official OpenCV project) needed sourcing, auto-downloaded on first use exactly like
Whisper's ggml model (`FaceModelLocator`), never committed to the repo.

**Architecture**: `NoniPilot.Gesture.FaceRecognitionEngine` (detection + LBPH, parallel to
`WebcamGestureEngine`) owned by new `NoniPilot.Desktop.Services.FaceRecognitionController`
(parallel to `GestureEngineController`), which subscribes to the SAME running
`GestureEngineController.FramePreview` event - no second, competing webcam capture session.
Recognition runs on a 700ms-throttled cadence (Haar cascade detection is real CPU work; "is this
still the same person" doesn't need per-frame checking). `IFaceRecognitionService` (Domain)
exposes `GetStatus()` (camera/enrolled/detected/recognized) and `EnrollAsync()` (captures ~4s of
live frames, trains, saves to `%LOCALAPPDATA%\NoniPilot\models\face-model.yml`).

**UI**: new "Face Recognition" card on the Gesture Control page (camera-related page, not a new
sidebar item) - status text + "Enroll My Face" button. New `face_recognize_person` tool
(`ToolCatalog`) for the AI agent, plus a system-prompt instruction to call it honestly for "do
you recognize me"/"who am I" questions instead of guessing.

**A 4th instance of the local-model-picks-the-wrong-tool problem, found and fixed the same way
as open/close**: live-tested "do you recognize me" - the model called `gesture_check_visibility`
instead of the new `face_recognize_person` tool despite the explicit system-prompt instruction.
Added `"recognize_me"` as a new exact-phrase fast-path intent in `LocalIntentService` (the same
service already handles the identical failure mode for "open X"/"close X"), handled directly in
`CommandProcessor` by reading `IFaceRecognitionService.GetStatus()` and responding
deterministically - no LLM call at all for this common question shape.

**Verified live**: asked "do you recognize me" before enrolling -> correct honest reply, "I don't
have your face enrolled yet - click \"Enroll My Face\" on the Gesture Control page first",
answered instantly with zero LLM/tool-call round-trip (confirmed via the audit log - no new
entry at all, meaning the fast path really did bypass the planner). **Not independently verified
end-to-end for the actual enroll -> recognize match** - that requires a real human face in front
of the camera, which nothing in this session's tooling can produce; the user needs to click
"Enroll My Face" themselves and then test "do you recognize me" for the positive case.

`dotnet build`/`dotnet test tests/Unit` (35/35) clean throughout.

## Real face-recognition enrollment persistence bug + persistent conversation memory + huge CPU fix (2026-09-13)

User confirmed face recognition genuinely worked live (screenshot: "[Verified] Face.Recognize" ->
"The enrolled person is currently recognized in frame"), then asked why it seemed to need
re-enrolling, asked for persistent memory of tasks/conversations, and asked to make the whole app
"more lightweight and faster" - explicitly leaving the approach to Claude's judgment.

**Face-recognition eager-load race**: `FaceRecognitionController`'s cascade+trained-model load
was fully lazy (`Lazy<Task<...>>`, only started on first frame/status check) - a status check
run very soon after launch could see `IsEnrolled: false` simply because loading hadn't finished
yet, not because the enrollment was lost. Fixed by starting the load immediately in the
constructor (`_ = _engineTask.Value;`) so it's essentially always done well before the user asks
anything.

**Persistent conversation memory across restarts** (new): previously every relaunch started with
a completely empty `_conversationHistory` and empty visible chat - the AI's memory and the chat
transcript were both purely in-process. New `NoniPilot.Agent.Providers.ConversationMemoryStore`
(same static-JSON-file convention as `AiProviderSettingsStore`) persists the same
plain-Command/Answer pairs `CommitToHistory` already builds (deliberately never tool-call traces -
same reasoning as before) to `%LOCALAPPDATA%\NoniPilot\conversation-memory.json`, capped at the
same `MaxRememberedTasks` size that already bounded the in-memory version. Loaded back into both
`ToolCallingPlannerService`'s reasoning context AND `AppServices.Chat` (the visible transcript) on
startup, so a relaunch shows the prior conversation, not just remembers it invisibly. New unit
tests (`ConversationMemoryStoreTests`, 2 new, 37 total) verify the round-trip directly rather than
relying on live UI automation, which proved flaky for this specific check. **Verified live
end-to-end anyway**: sent a question, confirmed `conversation-memory.json` was written with the
real Q&A, restarted the app, confirmed the exact same exchange re-appeared in the chat transcript.

**The actual "make it lightweight" root cause, found by measurement, not guessing**: the
Dashboard's own CPU gauge had shown 90%+ for a while; measured directly via
`Get-Process NoniPilot.Desktop | TotalProcessorTime` before touching anything: **~750% CPU (7.5
of 12 logical cores) continuously, even fully idle**, dropping to ~0.4% the instant the camera
was stopped via the UI - isolating the cost to the gesture/face camera pipeline specifically, not
Ollama, Whisper, or anything else. Two real, separate bugs found and fixed:
- `WebcamGestureEngine`'s capture loop had no frame-rate cap at all - hand-landmark ONNX
  inference ran on every single frame at the camera's native rate (commonly 30fps), all the time
  the camera is on (which is always, by default). Added a 15fps cap (`MinFrameInterval`), which
  every consumer of the shared `FramePreview` event benefits from (preview rendering, face
  recognition) since they all subscribe to the same throttled source.
- **The dominant cost, by far**: `OnnxHandLandmarkDetector` created its `InferenceSession` with
  default `SessionOptions` - ONNX Runtime auto-sizes its intra/inter-op thread pools to the
  machine's core count and, by default, has idle worker threads actively *spin-wait* (not sleep)
  between inference calls to minimize latency jitter. For a small model called only a few times a
  second, this is pure wasted background CPU that doesn't show up as "doing work" but shows up on
  every core. Fixed: `IntraOpNumThreads`/`InterOpNumThreads` = 1, `ExecutionMode.ORT_SEQUENTIAL`,
  and explicitly disabled spinning (`session.intra_op.allow_spinning`/`inter_op` config entries =
  "0"). **Measured before/after with the exact same methodology**: ~750% -> **~73% CPU** with the
  camera on - roughly a 10x reduction, all real, no functional change (correctness, not just
  speed, is what SessionOptions threading controls). Also added a defensive `Thread.Sleep(5)` in
  the frame-rate-cap's skip branch as a second, independent safeguard against a busy-spin if a
  camera backend's `VideoCapture.Read()` ever doesn't block as expected - measured to have no
  effect on its own (the ONNX fix was the real cause), but cheap insurance regardless.
- Re-verified face recognition still correctly answers "do you recognize me" -> "Yes, I recognize
  you." after the threading change - `SessionOptions` only affects performance/scheduling, not
  the model's actual output, but re-checked live anyway rather than assuming.

`dotnet build`/`dotnet test tests/Unit` (37/37) clean throughout.

## Real task scheduling + per-run reporting (2026-09-13)

User asked for the ability to schedule any task (system or web-related) for a given time, plus
complete reporting of every task. The dashboard-rebuild plan had explicitly deferred scheduling
("no triggers/scheduling in this pass") when the Automation page's saved command-sequence player
was first built - this closes that gap.

**Model**: `AutomationSequence` (already existed) gained an optional `ScheduleSpec` plus
`NextRunAt`/`LastRunAt`/`LastRunSuccess`. New `ScheduleSpec` supports four recurrence shapes
(deliberately not a full cron parser - these cover what a real scheduling request actually looks
like): Once (exact date/time), Daily (time of day), Weekly (day + time), Every N minutes (for
"check this website every 30 minutes" style tasks). `ComputeNextRun` advances the schedule
correctly across all four, including Once naturally never re-firing once its time has passed.

**Execution**: new `TaskSchedulerService` (owned by `AppServices`) polls
`AutomationSequenceStore` every 30s (re-reading from disk each tick, not caching, so an edit/save
on the Automation page is picked up on the very next tick) and runs anything due. New
`AutomationSequenceRunner` is the ONE place a sequence's commands actually execute - used by both
the Automation page's manual "Run" button and the scheduler, so a scheduled run is gated and
reported exactly like a manual one, not a second, slightly different path.

**A real concurrency bug this introduces, fixed before it could bite**: a background scheduler
firing while the user is actively chatting (now a real, likely occurrence, unlike a user-paced
manual Run click) would have two callers mutating `ToolCallingPlannerService`'s shared
(non-thread-safe) conversation history list at once. Added `AppServices.PlannerGate`
(`SemaphoreSlim`) around every call into `Planner.ExecuteAsync` - `CommandProcessor.RunPlannerAsync`
and `AutomationSequenceRunner` both acquire it, serializing interactive commands against
scheduled/automation runs.

**Reporting ("complete reporting of every task")**: every sequence run (manual or scheduled) is
recorded as a real `AuditEvent` (`Action = "AutomationSequence.Run"`, `Actor` = "user" or
"scheduler", outcome + a summary of each step's result in metadata) - the same durable SQLite
audit trail everything else in the app already flows through, not a second parallel log. New
"Task Reports" panel on the Automation page reads these back (`IAuditService.QueryAsync`,
filtered client-side to that action) and shows each run's outcome plainly; the existing Task
History page still shows the finer-grained per-tool-call detail underneath each run.

**UI**: the "New sequence" form on the Automation page gained a "Schedule (optional)" section
(recurrence dropdown + the relevant date/time/day/interval inputs, shown conditionally); the
saved-sequences list shows each sequence's schedule description, active/paused state, next run,
and last run outcome, plus a Pause/Resume toggle alongside Run and Delete.

**Verified live, full loop, not just built**: saved a sequence with a 1-minute interval schedule
via UI automation, waited for the actual 30s-polling scheduler to fire it with zero manual
intervention, confirmed `automation-sequences.json` updated with a real `LastRunAt`/
`LastRunSuccess: true` and a correctly-advanced `NextRunAt`, confirmed the audit database recorded
`actor=scheduler action=AutomationSequence.Run outcome=Success` with the real step result in
metadata, and confirmed the Task Reports panel displayed it ("SUCCESS" / "Test Scheduled Task -
Scheduled"). Test sequence deleted afterward so it doesn't keep firing in the user's real app.

`dotnet build`/`dotnet test tests/Unit` (37/37) clean throughout.
