using System.IO;
using System.Windows;
using NoniPilot.Agent.Tools;
using NoniPilot.Domain.Models;
using NoniPilot.Voice;

namespace NoniPilot.Desktop.Services;

/// <summary>
/// Everything MainWindow.ProcessCommandAsync/VoiceModeLoopAsync used to do, factored out so
/// both the dedicated Voice Commands page and the Dashboard's bottom command bar drive the
/// exact same running state (one in-flight task at a time, one continuous voice-mode loop),
/// instead of each page owning an independent, out-of-sync copy.
/// </summary>
public sealed class CommandProcessor
{
    private readonly AppServices _services;
    private readonly Dictionary<string, int> _stepEntryIndex = new();
    private CancellationTokenSource? _currentTaskCts;
    private CancellationTokenSource? _voiceModeCts;
    private CancellationTokenSource? _wakeWordCts;

    // Only one of {wake-word listening, full conversation mode} may actually be recording at
    // any instant - NAudioMicrophoneRecorder isn't designed for concurrent overlapping
    // StartRecording calls. This is the mutual-exclusion guard between the two loops.
    private readonly SemaphoreSlim _micSemaphore = new(1, 1);

    public bool IsTaskRunning { get; private set; }
    public DateTime TaskStartedAtUtc { get; private set; }
    public bool VoiceModeActive { get; private set; }
    public bool WakeWordModeActive { get; private set; }
    public string StatusMessage { get; private set; } = "Idle";

    public event Action? StatusChanged;
    public event Action? VoiceModeChanged;
    public event Action? WakeWordModeChanged;

    public CommandProcessor(AppServices services) => _services = services;

    /// <summary>
    /// Backs the "open my desktop/documents/downloads/..." fast path matched by
    /// LocalIntentService - a direct, statically-known path, never a fuzzy search. This is what
    /// fixes both the observed live bug (the model searching *inside* Desktop and opening a
    /// nested folder that happened to also be named "Desktop") and the multi-minute latency of
    /// routing such a well-defined request through a CPU-bound local model round-trip at all.
    /// </summary>
    private static readonly Dictionary<string, Func<string>> SpecialFolderResolvers = new()
    {
        ["open_desktop"] = () => Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        ["open_documents"] = () => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        ["open_downloads"] = () => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
        ["open_pictures"] = () => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        ["open_music"] = () => Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
        ["open_videos"] = () => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
    };

    private async Task HandleOpenFolderFastPathAsync(string command, string intent, Func<string> resolvePath)
    {
        _services.Chat.Add(new ChatEntry { Kind = ChatEntryKind.User, Text = command });

        var path = resolvePath();
        var (success, message) = await _services.ActionRunner.RunAsync(
            "Application", "Launch", new Dictionary<string, object?> { ["appPathOrName"] = path },
            ct => _services.Applications.LaunchAsync(path, null, ct));

        var label = intent["open_".Length..];
        var resultText = success ? $"Opened your {label} folder." : $"Couldn't open {label}: {message}";
        _services.Chat.Add(new ChatEntry { Kind = success ? ChatEntryKind.Assistant : ChatEntryKind.Error, Text = resultText });

        await _services.TextToSpeech.SpeakAsync(resultText);
    }

    private static readonly string[] OpenVerbs = ["open", "show", "khol", "kholo", "kholiye"];

    /// <summary>
    /// Words that mean this is actually more specific than a bare "open X" - e.g. creating or
    /// searching for something - and must never be short-circuited by the fast path below, or
    /// a real, specific request would silently be replaced with just opening a parent folder.
    /// </summary>
    private static readonly string[] NotJustOpenWords =
    [
        "create", "delete", "remove", "rename", "copy", "move", "search", "find", "inside",
        "named", "call it", "new folder", "file", "in the", "from the", "to the", "and then",
    ];

    /// <summary>
    /// Extracts a bare target name from a simple "open X"/"show X" command. Deliberately
    /// permissive about what counts as a target - a wrong guess here only means both attempts
    /// in TryHandleOpenTargetFastPathAsync fail harmlessly and the command falls through to the
    /// full planner exactly as before this existed, never an incorrect action.
    /// </summary>
    private static string? TryExtractOpenTarget(string command)
    {
        var trimmed = command.Trim().TrimEnd('.', '!', '?');
        var lower = trimmed.ToLowerInvariant();

        if (NotJustOpenWords.Any(lower.Contains))
        {
            return null;
        }

        if (trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 8)
        {
            return null;
        }

        foreach (var verb in OpenVerbs)
        {
            var idx = lower.IndexOf(verb, StringComparison.Ordinal);
            if (idx < 0)
            {
                continue;
            }

            var rest = trimmed[(idx + verb.Length)..].Trim();
            foreach (var filler in new[] { "my ", "the ", "mera ", "meri " })
            {
                if (rest.StartsWith(filler, StringComparison.OrdinalIgnoreCase))
                {
                    rest = rest[filler.Length..];
                }
            }

            if (rest.EndsWith(" folder", StringComparison.OrdinalIgnoreCase))
            {
                rest = rest[..^" folder".Length];
            }

            rest = rest.Trim();
            if (rest.Length > 0)
            {
                return rest;
            }
        }

        return null;
    }

    /// <summary>
    /// Tries the overwhelmingly common "open &lt;app or folder&gt;" pattern entirely locally,
    /// with no LLM call at all: first as a launchable application (covers "open chrome"/"open
    /// notepad"), then as a fuzzy-matched item on the Desktop (covers "open AMBERG IT
    /// [folder]", the single most common real request observed live). Both are cheap, and both
    /// go through the same PolicyGatedActionRunner as every other action, so nothing here
    /// bypasses auditing or risk classification - it only bypasses the slow reasoning step for
    /// a request simple enough not to need it.
    /// </summary>
    private async Task<bool> TryHandleOpenTargetFastPathAsync(string target)
    {
        var (appSuccess, _) = await _services.ActionRunner.RunAsync(
            "Application", "Launch", new Dictionary<string, object?> { ["appPathOrName"] = target },
            ct => _services.Applications.LaunchAsync(target, null, ct));

        if (appSuccess)
        {
            await RespondFastPathAsync($"Opened {target}.");
            return true;
        }

        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            var entries = await _services.FileSystem.ListDirectoryAsync(desktop);
            var best = FuzzyNameMatcher.FindBestMatch(entries, target, e => e.Name);

            if (best is not null)
            {
                var (opened, _) = await _services.ActionRunner.RunAsync(
                    "FileSystem", "OpenBestMatch",
                    new Dictionary<string, object?> { ["parentPath"] = desktop, ["nameHint"] = target },
                    ct => _services.Applications.LaunchAsync(best.FullPath, null, ct));

                if (opened)
                {
                    await RespondFastPathAsync($"Opened the {best.Name} folder.");
                    return true;
                }
            }
        }
        catch
        {
            // A Desktop listing failure shouldn't block falling through to the full planner.
        }

        return false;
    }

    /// <summary>
    /// Words that are pure sentence structure/filler around a YouTube request, not part of the
    /// actual search query - stripped out so "open youtube in a new tab and play this song
    /// believer" reduces to a clean query ("this song believer") rather than searching for the
    /// whole sentence verbatim. Content words like "this"/"song" are deliberately left in -
    /// YouTube's own search is tolerant of a little extra padding, and being too aggressive here
    /// risks cutting real words out of the actual song/video name instead.
    /// </summary>
    private static readonly string[] YouTubeFillerWords = ["open", "youtube", "in", "a", "new", "tab", "and", "on", "please"];

    /// <summary>
    /// Handles "play/search/find X on YouTube" (opens a YouTube search results page for X - not
    /// literal autoplay, which would need real page automation this app deliberately doesn't
    /// have, see BrowserService's own doc comment) and bare "open youtube" (opens the homepage)
    /// entirely locally. Added after a live, repeatable failure: the local model treated
    /// "youtube" as a literal app name and tried to launch it as a program, which always failed
    /// ("the system cannot find the file specified").
    /// </summary>
    private static (bool IsYouTubeRequest, string? Query) TryExtractYouTubeRequest(string command)
    {
        var lower = command.ToLowerInvariant();
        if (!lower.Contains("youtube"))
        {
            return (false, null);
        }

        foreach (var cue in new[] { "play", "search for", "search", "find" })
        {
            var idx = lower.IndexOf(cue, StringComparison.Ordinal);
            if (idx < 0)
            {
                continue;
            }

            var rest = command[(idx + cue.Length)..];
            var words = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(w => !YouTubeFillerWords.Contains(w.ToLowerInvariant()))
                .ToArray();

            var query = string.Join(' ', words).Trim(' ', '.', '!', '?');
            if (query.Length > 0)
            {
                return (true, query);
            }
        }

        // No play/search cue found, but "youtube" was mentioned - treat as "just open it".
        return (true, null);
    }

    private async Task<bool> TryHandleYouTubeFastPathAsync(string command)
    {
        var (isYouTubeRequest, query) = TryExtractYouTubeRequest(command);
        if (!isYouTubeRequest)
        {
            return false;
        }

        var url = query is null
            ? "https://www.youtube.com"
            : $"https://www.youtube.com/results?search_query={Uri.EscapeDataString(query)}";

        // Same closure-capture reasoning as the close-app fast path above - RunAsync's own
        // "Success" bool only means "didn't throw", not "OpenUrlAsync's own result was true".
        var actuallyOpened = false;
        await _services.ActionRunner.RunAsync(
            "Browser", "OpenUrl", new Dictionary<string, object?> { ["url"] = url },
            async ct => actuallyOpened = await _services.Browser.OpenUrlAsync(url, ct));

        var opened = actuallyOpened;
        var message = "Could not launch a browser for this URL.";
        var resultText = opened
            ? (query is null ? "Opened YouTube." : $"Opened YouTube search results for \"{query}\".")
            : $"Couldn't open YouTube: {message}";
        await RespondFastPathAsync(resultText);
        return true;
    }

    private static readonly string[] CloseVerbs = ["close", "quit", "exit", "band karo", "band kar do", "band kardo"];

    /// <summary>
    /// Extracts a bare target name from a simple "close X"/"quit X"/"exit X" command. Same
    /// deliberately-permissive shape as TryExtractOpenTarget - a wrong guess only means the fast
    /// path below finds no matching running app and falls through to the full planner.
    /// </summary>
    private static string? TryExtractCloseTarget(string command)
    {
        var trimmed = command.Trim().TrimEnd('.', '!', '?');
        var lower = trimmed.ToLowerInvariant();

        if (trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 6)
        {
            return null;
        }

        foreach (var verb in CloseVerbs)
        {
            var idx = lower.IndexOf(verb, StringComparison.Ordinal);
            if (idx < 0)
            {
                continue;
            }

            var rest = trimmed[(idx + verb.Length)..].Trim();
            foreach (var filler in new[] { "my ", "the ", "mera ", "meri " })
            {
                if (rest.StartsWith(filler, StringComparison.OrdinalIgnoreCase))
                {
                    rest = rest[filler.Length..];
                }
            }

            rest = rest.Trim();
            if (rest.Length > 0)
            {
                return rest;
            }
        }

        return null;
    }

    /// <summary>
    /// Handles "close X"/"quit X" entirely locally, with no LLM call at all - added after a
    /// live, repeatable bug (2026-09-13): asked to close Google Chrome, the local model
    /// (qwen2.5:3b) never actually called application_close at all, it just replied claiming
    /// success in plain text with no tool call and no audit trail whatsoever. A weak local model
    /// unreliably choosing whether to call a tool for a common, well-defined request is exactly
    /// the same class of problem the "open X" fast path above already solves for launching -
    /// this mirrors it for closing: look up the actual running process by fuzzy name match, close
    /// it directly through the same policy-gated path every other action uses, and report the
    /// real (verified) result - never the model's own unverified claim.
    /// </summary>
    private async Task<bool> TryHandleCloseTargetFastPathAsync(string target)
    {
        IReadOnlyList<NoniPilot.Domain.Interfaces.RunningApplication> running;
        try
        {
            running = await _services.Applications.ListRunningAsync();
        }
        catch
        {
            return false;
        }

        var best = FuzzyNameMatcher.FindBestMatch(running, target, a => a.ProcessName)
            ?? FuzzyNameMatcher.FindBestMatch(running, target, a => a.MainWindowTitle ?? string.Empty);

        if (best is null)
        {
            // Nothing resembling this name is currently running - let the full planner take a
            // shot (it may know an alias/relationship a plain fuzzy name match can't see).
            return false;
        }

        // PolicyGatedActionRunner.RunAsync takes a bare Func<CancellationToken, Task> and
        // reports (true, "Done.") as soon as that delegate returns without throwing - it has no
        // way to see CloseAsync's own honest true/false result. Capturing it via closure here
        // (rather than trusting RunAsync's own "Success" bool) is what actually makes a genuine
        // close failure visible instead of being silently reported as success a second time,
        // this time at the fast-path layer instead of the AI-planner/verification layer.
        var actuallyClosed = false;
        var (ran, message) = await _services.ActionRunner.RunAsync(
            "Application", "Close", new Dictionary<string, object?> { ["processId"] = best.ProcessId },
            async ct => actuallyClosed = await _services.Applications.CloseAsync(best.ProcessId, ct));

        var resultText = ran && actuallyClosed
            ? $"Closed {best.ProcessName}."
            : $"Tried to close {best.ProcessName}, but it didn't actually close{(ran ? "." : $" - {message}")}";
        await RespondFastPathAsync(resultText);
        return true;
    }

    private async Task RespondFastPathAsync(string resultText)
    {
        _services.Chat.Add(new ChatEntry { Kind = ChatEntryKind.Assistant, Text = resultText });
        await _services.TextToSpeech.SpeakAsync(resultText);
    }

    public async Task ProcessCommandAsync(string command, CancellationToken externalToken = default)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return;
        }

        var fastPathIntent = _services.IntentService.TryMatchFastPathIntent(command);
        if (fastPathIntent == "stop")
        {
            StopEverything();
            return;
        }

        if (fastPathIntent == "sleep")
        {
            // A softer stop than StopEverything(): just stops actively listening/responding and
            // returns to passive wake-word-only mode (if it's on) - no emergency stop of
            // in-flight computer control, since the user just wants quiet, not a kill switch.
            // Saying "Hey Noni"/"Jago Noni" wakes it back up.
            StopVoiceMode();
            _services.Chat.Add(new ChatEntry { Kind = ChatEntryKind.Assistant, Text = "Going quiet - say \"Hey Noni\" when you need me." });
            return;
        }

        if (fastPathIntent == "recognize_me")
        {
            _services.Chat.Add(new ChatEntry { Kind = ChatEntryKind.User, Text = command });
            var status = _services.FaceRecognition.GetStatus();
            var resultText = !status.CameraActive
                ? "The camera is currently off - turn it on in Gesture Control if you want me to recognize you."
                : !status.IsEnrolled
                    ? "I don't have your face enrolled yet - click \"Enroll My Face\" on the Gesture Control page first."
                    : !status.PersonDetected
                        ? "I don't see a face in view right now."
                        : status.IsKnownPerson
                            ? "Yes, I recognize you."
                            : "I see a face, but it doesn't match who I have enrolled.";
            await RespondFastPathAsync(resultText);
            return;
        }

        if (IsTaskRunning)
        {
            // Without this guard a second command (typed, or a fast follow-up spoken while
            // Local AI was still slowly working the first one) would start a second,
            // completely independent ExecuteAsync running concurrently with the first - both
            // mutating the same chat transcript and step-index dictionary.
            _services.Chat.Add(new ChatEntry
            {
                Kind = ChatEntryKind.Error,
                Text = "Still working on the previous command - wait for it to finish, or click STOP, before sending another.",
            });
            return;
        }

        if (fastPathIntent is not null && SpecialFolderResolvers.TryGetValue(fastPathIntent, out var resolvePath))
        {
            await HandleOpenFolderFastPathAsync(command, fastPathIntent, resolvePath);
            return;
        }

        IsTaskRunning = true;
        TaskStartedAtUtc = DateTime.UtcNow;
        _services.Chat.Add(new ChatEntry { Kind = ChatEntryKind.User, Text = command });
        SetStatus("Thinking...");

        // Checked before the generic open-target fast path below, since "open youtube..." would
        // otherwise be extracted as a plain app-launch target named "youtube" and fail there
        // first (an actual observed bug - "the system cannot find the file specified").
        if (await TryHandleYouTubeFastPathAsync(command))
        {
            IsTaskRunning = false;
            SetStatus($"Ready - {_services.Router.Name}");
            return;
        }

        var openTarget = TryExtractOpenTarget(command);
        if (openTarget is not null && await TryHandleOpenTargetFastPathAsync(openTarget))
        {
            IsTaskRunning = false;
            SetStatus($"Ready - {_services.Router.Name}");
            return;
        }

        var closeTarget = TryExtractCloseTarget(command);
        if (closeTarget is not null && await TryHandleCloseTargetFastPathAsync(closeTarget))
        {
            IsTaskRunning = false;
            SetStatus($"Ready - {_services.Router.Name}");
            return;
        }

        await RunPlannerAsync(command);
    }

    private async Task RunPlannerAsync(string command)
    {
        _currentTaskCts = new CancellationTokenSource();

        // Serializes against TaskSchedulerService's own runs - see AppServices.PlannerGate's
        // doc comment. A scheduled automation firing at the exact moment the user is mid-chat
        // would otherwise both mutate ToolCallingPlannerService's shared conversation history at
        // once.
        await _services.PlannerGate.WaitAsync(_currentTaskCts.Token);
        try
        {
            var task = await _services.Planner.ExecuteAsync(command, OnTaskUpdated, _currentTaskCts.Token);
            var resultText = task.Result ?? task.ErrorMessage ?? "(no result)";

            // A step can fail mid-task while the model still produces a normal explanatory
            // final answer - only treat this as an error bubble when there's no final answer
            // at all (crash/cancel/refusal), not merely because some step along the way failed.
            var kind = task.Result is not null ? ChatEntryKind.Assistant : ChatEntryKind.Error;
            _services.Chat.Add(new ChatEntry { Kind = kind, Text = resultText });

            await _services.TextToSpeech.SpeakAsync(resultText, _currentTaskCts.Token);
        }
        catch (Exception ex)
        {
            _services.Chat.Add(new ChatEntry { Kind = ChatEntryKind.Error, Text = $"Unexpected error: {ex.Message}" });
        }
        finally
        {
            _services.PlannerGate.Release();
            IsTaskRunning = false;
            SetStatus($"Ready - {_services.Router.Name}");
        }
    }

    private void OnTaskUpdated(AgentTask task)
    {
        // The planner reports progress from a background thread - marshal to the UI thread.
        Application.Current.Dispatcher.Invoke(() =>
        {
            SetStatus($"{task.Status}");

            foreach (var step in task.Steps)
            {
                var text = $"[{step.Status}] {step.Tool}.{step.Action}  (risk: {step.RiskLevel})" +
                    (step.ErrorMessage is null ? string.Empty : $"  - {step.ErrorMessage}");
                var entry = new ChatEntry { Kind = ChatEntryKind.SystemStep, Text = text };

                if (_stepEntryIndex.TryGetValue(step.Id, out var index))
                {
                    _services.Chat[index] = entry;
                }
                else
                {
                    _services.Chat.Add(entry);
                    _stepEntryIndex[step.Id] = _services.Chat.Count - 1;
                }
            }
        });
    }

    public void ToggleVoiceMode()
    {
        if (!VoiceModeActive)
        {
            StartVoiceMode();
        }
        else
        {
            StopVoiceMode();
        }
    }

    private void StartVoiceMode()
    {
        VoiceModeActive = true;
        VoiceModeChanged?.Invoke();
        _voiceModeCts = new CancellationTokenSource();
        _ = VoiceModeLoopAsync(_voiceModeCts.Token);
    }

    public void StopVoiceMode()
    {
        if (!VoiceModeActive)
        {
            return;
        }

        VoiceModeActive = false;
        VoiceModeChanged?.Invoke();
        _voiceModeCts?.Cancel();
        SetStatus("Stopped listening.");
    }

    /// <summary>
    /// Continuous voice conversation: listen (auto-stops on trailing silence) -> transcribe ->
    /// run the command -> speak the result -> listen again, repeating until Stop Talking is
    /// clicked (or "stop" is spoken/typed, via StopEverything).
    /// </summary>
    private async Task VoiceModeLoopAsync(CancellationToken token)
    {
        while (VoiceModeActive && !token.IsCancellationRequested)
        {
            SetStatus("Listening...");

            byte[] audio;
            await _micSemaphore.WaitAsync(token);
            try
            {
                audio = await _services.Microphone.RecordUntilSilenceAsync(
                    maxDuration: TimeSpan.FromSeconds(20),
                    silenceDuration: TimeSpan.FromSeconds(1.5),
                    cancellationToken: token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _services.Chat.Add(new ChatEntry { Kind = ChatEntryKind.Error, Text = $"Could not record: {ex.Message}" });
                break;
            }
            finally
            {
                _micSemaphore.Release();
            }

            if (!VoiceModeActive || token.IsCancellationRequested)
            {
                break;
            }

            if (audio.Length == 0)
            {
                // The recorder itself decided there wasn't enough real speech to be worth
                // transcribing at all (most commonly NoniPilot's own voice trailing off through
                // the speakers right after it just spoke) - skip STT entirely rather than risk
                // it hallucinating a short generic phrase from near-silent/echo audio.
                WakePhraseRecognizer.AppendLog("[voice mode] recorder returned 0 bytes (no speech loud/long enough detected)");
                SetStatus("Didn't catch that - listening again...");
                continue;
            }

            SetStatus("Transcribing...");

            string? text;
            try
            {
                text = await _services.SpeechToText.TranscribeAsync(audio, token);
            }
            catch (Exception ex)
            {
                WakePhraseRecognizer.AppendLog($"[voice mode] TranscribeAsync EXCEPTION: {ex.GetType().Name}: {ex.Message}");
                _services.Chat.Add(new ChatEntry { Kind = ChatEntryKind.Error, Text = $"Transcription failed: {ex.Message}" });
                continue;
            }

            WakePhraseRecognizer.AppendLog($"[voice mode] transcribed text=[{text}]");

            if (string.IsNullOrWhiteSpace(text))
            {
                SetStatus("Didn't catch that - listening again...");
                continue;
            }

            await ProcessCommandAsync(text, token);

            // A buffer before the mic reopens, so NoniPilot's own voice has time to actually
            // finish playing/decaying acoustically before listening resumes - see
            // docs/architecture/decisions.md. Raised from 400ms after a live, repeatable
            // self-echo bug (paired with the recorder's own minimum-loud-duration gate above,
            // which is the primary fix - this delay is defense in depth, not the whole fix).
            try
            {
                await Task.Delay(900, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        VoiceModeActive = false;
        VoiceModeChanged?.Invoke();
    }

    private static readonly string[] WakePhrases =
    [
        "hey noni", "wake up noni", "wakeup noni", "sun noni", "suno noni", "jago noni", "jaago noni",
        "जागो नोनी", "सुनो नोनी", "अरे नोनी",
    ];

    // Lazy so the (slow-ish, ~100-300ms) SpeechRecognitionEngine construction only happens the
    // first time wake-word mode is actually turned on, not on every app launch.
    private readonly Lazy<WakePhraseRecognizer> _wakePhraseRecognizer = new(() => new WakePhraseRecognizer(WakePhrases));

    public void ToggleWakeWordMode()
    {
        if (!WakeWordModeActive)
        {
            StartWakeWordMode();
        }
        else
        {
            StopWakeWordMode();
        }
    }

    public void StartWakeWordMode()
    {
        if (WakeWordModeActive)
        {
            return;
        }

        WakeWordModeActive = true;
        WakeWordModeChanged?.Invoke();
        _wakeWordCts = new CancellationTokenSource();
        _ = WakeWordLoopAsync(_wakeWordCts.Token);
    }

    public void StopWakeWordMode()
    {
        if (!WakeWordModeActive)
        {
            return;
        }

        WakeWordModeActive = false;
        WakeWordModeChanged?.Invoke();
        _wakeWordCts?.Cancel();
    }

    /// <summary>
    /// Passive background listening for a wake phrase ("Hey Noni", "Jago Noni", etc.) - the
    /// "it automatically listens for me" option requested alongside the manual Talk button, not
    /// a replacement for it. Uses short (4s) listen cycles so it's cheap while idle, and
    /// deliberately steps aside (via VoiceModeActive/_micSemaphore) whenever full conversation
    /// mode is already running, rather than competing with it for the microphone.
    /// </summary>
    private async Task WakeWordLoopAsync(CancellationToken token)
    {
        while (WakeWordModeActive && !token.IsCancellationRequested)
        {
            if (VoiceModeActive)
            {
                try
                {
                    await Task.Delay(500, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                continue;
            }

            SetStatus("Listening for \"Hey Noni\"...");

            byte[] audio;
            await _micSemaphore.WaitAsync(token);
            try
            {
                audio = await _services.Microphone.RecordUntilSilenceAsync(
                    maxDuration: TimeSpan.FromSeconds(4),
                    silenceDuration: TimeSpan.FromSeconds(1),
                    cancellationToken: token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // A transient mic/driver hiccup while passively listening shouldn't stop the
                // whole wake-word loop - just try again next cycle. Logged (not silently
                // swallowed) since this exact silent catch was one of the suspects for wake
                // word never firing.
                WakePhraseRecognizer.AppendLog($"RecordUntilSilenceAsync EXCEPTION: {ex.GetType().Name}: {ex.Message}");
                audio = Array.Empty<byte>();
            }
            finally
            {
                _micSemaphore.Release();
            }

            if (audio.Length == 0)
            {
                // Logged because this is the mic's own RMS/loudness gate (NAudioMicrophoneRecorder's
                // MinimumLoudMilliseconds) deciding there was no real speech at all - if this
                // fires on every real "Hey Noni" attempt, the gate (tuned against a different
                // scenario - filtering acoustic self-echo) is the actual blocker, not STT/grammar
                // matching downstream.
                WakePhraseRecognizer.AppendLog("recorder returned 0 bytes (no speech loud/long enough detected)");
                continue;
            }

            if (!WakeWordModeActive || token.IsCancellationRequested)
            {
                continue;
            }

            // Deliberately NOT using _services.SpeechToText here (Groq or Windows free
            // dictation) - measured live: dictation's open-vocabulary language model has no path
            // to an invented name like "Noni" and reliably mis-transcribes "Hey Noni" as things
            // like "Denoting" at <1% confidence, so it never once produced a string containing
            // "noni" to match against WakePhrases. A closed-set grammar constrained to exactly
            // the phrases below only has to do acoustic matching against a handful of known
            // options, which measured at 93%+ confidence for the same audio. See
            // WakePhraseRecognizer's doc comment for the full measurement.
            string? matchedPhrase;
            try
            {
                matchedPhrase = await _wakePhraseRecognizer.Value.TryMatchAsync(audio, token);
            }
            catch (Exception ex)
            {
                WakePhraseRecognizer.AppendLog($"TryMatchAsync EXCEPTION (outer): {ex.GetType().Name}: {ex.Message}");
                continue;
            }

            if (matchedPhrase is null)
            {
                continue;
            }

            // The status-text change alone ("Listening for 'Hey Noni'..." -> "Listening...") is
            // easy to miss - measured live: the recognizer genuinely did match "hey noni" at
            // 0.765 confidence, but with no audible signal it looked indistinguishable from not
            // having worked at all. A short spoken acknowledgment closes that gap the same way
            // Alexa/Google Assistant's wake chime does.
            SetStatus("Woke up!");
            await _services.TextToSpeech.SpeakAsync("Yes?", token);

            // Same acoustic-bleed concern as VoiceModeLoopAsync's own post-turn delay - without
            // this, "Yes?" is often still trailing off through the speakers exactly as the mic
            // reopens below.
            try
            {
                await Task.Delay(400, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            StartVoiceMode();
        }

        WakeWordModeActive = false;
        WakeWordModeChanged?.Invoke();
    }

    public void StopEverything()
    {
        _services.ComputerControl.EmergencyStop();
        _services.TextToSpeech.StopSpeaking(); // immediate, synchronous cutoff
        _currentTaskCts?.Cancel();
        StopVoiceMode();
        SetStatus("Stopped.");
    }

    private void SetStatus(string message)
    {
        StatusMessage = message;
        StatusChanged?.Invoke();
    }
}
