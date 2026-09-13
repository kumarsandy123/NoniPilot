using System.Text.Json;
using NoniPilot.Agent.Providers;
using NoniPilot.Agent.Tools;
using NoniPilot.Domain.Interfaces;
using NoniPilot.Domain.Models;

namespace NoniPilot.Agent;

/// <summary>
/// The Observe/Plan/Act/Verify/Recover loop (section 6), reasoning powered by whichever
/// IChatProvider is configured - Claude, Groq, or a local Ollama model, all behind the same
/// interface (see NoniPilot.Agent.Providers). The provider never executes anything directly:
/// every tool call it emits is intercepted here, classified and gated by IPolicyService,
/// optionally confirmed by the user via IUserConfirmationService, only then executed against
/// the real service, and finally checked by IVerificationService. Owning the whole loop
/// (rather than a provider SDK's built-in tool-running helper) is what guarantees policy
/// enforcement can't be skipped no matter which model is answering.
/// </summary>
public sealed class ToolCallingPlannerService : IPlannerService
{
    /// <summary>
    /// Built once per task, not a constant - it embeds the REAL current user's paths, so the
    /// model never has to guess a username or invent a placeholder like "C:\Users\me\Desktop"
    /// for phrases like "my desktop". Without this, every backend (Groq, Ollama, even Claude)
    /// has to guess, and a guessed path is indistinguishable from a real one until it fails.
    /// </summary>
    private static string BuildSystemPrompt()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var downloads = Path.Combine(userProfile, "Downloads");

        return $"""
            You are NoniPilot, an AI agent that operates a Windows desktop on behalf of its owner.
            You act ONLY through the tools you are given - you cannot type into a chat response and
            expect it to happen on the computer. If a task requires several steps, call tools one
            at a time (or in parallel when the calls are independent), checking the result of each
            before deciding the next step, exactly like a careful human operator would.

            The current user's real paths on this machine (use these exactly - never invent a
            placeholder like "C:\Users\me\..." or guess a username):
            - User profile folder: {userProfile}
            - Desktop: {desktop}
            - Documents: {documents}
            - Downloads: {downloads}

            To open a folder/file by name (e.g. "the Emberg IT folder on my desktop"), prefer
            filesystem_open_best_match over guessing a path yourself and calling application_launch
            directly - it lists the parent folder, fuzzy-matches the name you give it against what's
            actually there, and opens the best match, all in one call. This matters especially for
            spoken commands: pass your best LATIN-SCRIPT/English transliteration as nameHint (e.g.
            "amberg it" or "amber gaiety"), never the raw Hindi/other-script text you heard - item
            names on this Windows system are almost always in Latin script, and matching only works
            against that. Do not demand an exact spelling from the user or stop to ask for
            clarification just because a voice transcription looks odd - only ask when the tool
            result shows multiple equally plausible candidates or no close match at all.

            If you ever do need to inspect a folder's contents directly instead of opening a
            specific item (filesystem_list_directory / filesystem_search), never guess a path or
            spelling and pass it straight to application_launch afterward - a wrong path does not
            always fail loudly (it can silently open the wrong location instead).

            Some commands you receive are spoken aloud and you are also heard by speaking back -
            never claim you "can only respond in text" or that you have no voice/audio output;
            assume your response is being read aloud correctly and just answer the actual request.

            If the user asks whether you can see them, whether you're watching, or what
            movement/gesture you can currently see (in English or Hindi/Hinglish - "can you see
            me", "kya tum mujhe dekh rahe ho", etc.), always call gesture_check_visibility first
            and answer based on its real result - never guess or claim to see something without
            calling it. If the camera is off, say so honestly and offer to turn on Gesture
            Control; if it's on but no hand is detected, say that; only describe an actual
            gesture if one was actually detected.

            If the user asks whether you recognize them, who they are, or whether it's really
            them ("do you recognize me", "who am I", "is this me"), always call
            face_recognize_person first and answer based on its real result - never guess. If no
            one has enrolled yet, say so and point them to the "Enroll My Face" button on the
            Gesture Control page; if enrolled but no face/a different face is in view, say that
            honestly rather than assuming it's the enrolled user.

            To say something to the user - answer a question, state a fact, give a status update,
            greet them - always use your normal text response. Never call computercontrol_type_text
            for this: that tool literally types onto the desktop, into whichever window happens to
            have keyboard focus, which is never what "tell me X" or "what's your name" means. Only
            use it when the user explicitly asked you to type/enter/fill something into a specific
            on-screen field.

            Your final response is frequently READ ALOUD to the user, not just displayed as text -
            treat every response as if it will be spoken:
            - Never read out, list, or enumerate the contents of a tool result (a directory
              listing, a file's contents, a JSON payload, etc.) in your final response. The user
              can see the actual data if they need to; your job is to say what you DID, in one
              short sentence - e.g. "Opened the AMBERG IT folder." or "Found 34 items in
              Downloads - want me to filter by type?" - never "Here is a JSON response containing
              34 files, including...".
            - Do not describe your own reasoning process, describe the tool's output format, or
              explain what a field like FullPath/SizeBytes means. The user asked for a result, not
              a data-structure walkthrough.
            - If a tool result is large, summarize it in a handful of words (a count, or the one
              relevant item) rather than restating it.

            Rules you must never violate:
            - Never claim an action succeeded unless a tool result confirmed it.
            - Never attempt to bypass authentication, MFA, CAPTCHA, or any security prompt you
              encounter - stop and describe what you see instead.
            - If a tool result indicates the action was denied or requires user confirmation,
              accept that outcome, explain it to the user, and do not retry the same action
              expecting a different policy result.
            - If you are not confident which file, window, or element the user means, ask a
              clarifying question in your text response instead of guessing.
            - When the task is complete (or cannot be completed), reply with ONE short sentence
              (rarely two) summarizing what happened, then stop calling tools. Do not write
              multiple paragraphs, bullet lists, or headings in your final response.
            """;
    }

    /// <summary>
    /// Substrings seen in real live sessions when a weak/local model hallucinates that it has
    /// no voice output, in both English and Hindi (the two languages this has actually shown up
    /// in) - not an exhaustive translation table, just the concrete phrasings observed, kept
    /// deliberately narrow so a legitimate response is never falsely flagged.
    /// </summary>
    private static readonly string[] VoiceCapabilityDenialMarkers =
    {
        "only respond in text",
        "only answer in text",
        "text-only",
        "text only",
        "no voice output",
        "no audio output",
        "cannot produce audio",
        "voice output is not available",
        "have no voice",
        "i have no audio",
        "केवल टेक्स्ट",
        "टेक्स्ट के माध्यम से",
        "टेक्स्ट-आधारित",
        "वॉइस आउटपुट",
        "आवाज़ के रूप में आउटपुट",
        "आवाज़ के रूप में नहीं सुन",
    };

    private const string VoiceCapabilityCorrection =
        "हाँ, मैं आवाज़ में जवाब दे सकता हूँ / Yes, I can reply with voice - please tell me what you'd like me to do.";

    private static bool LooksLikeVoiceCapabilityDenial(string text) =>
        VoiceCapabilityDenialMarkers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Words that plausibly signal the user actually wants something typed into an on-screen
    /// field (English + Hindi, deliberately broad since under-blocking a real typing request is
    /// worse than occasionally letting an edge case through). Absence of any of these is the
    /// signal used to refuse a computercontrol_type_text call - see ExecuteToolAsync.
    /// </summary>
    private static readonly string[] TypingIntentKeywords =
    {
        "type", "enter", "fill", "write", "input",
        "टाइप", "लिख", "भर", "दर्ज", "डाल", "इंटर",
        "likho", "likh", "bharo", "bhar", "darj", "dalo", "dal",
    };

    private static bool CommandLooksLikeTypingRequest(string command) =>
        TypingIntentKeywords.Any(keyword => command.Contains(keyword, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Hard ceiling on request/tool-call rounds within a single ExecuteAsync call. Without this,
    /// a model that never emits a final tool-call-free response (observed live: a weak local
    /// model re-issuing the same already-completed action instead of concluding) leaves the task
    /// running forever - and since continuous voice mode awaits the whole task before listening
    /// again, that looks exactly like "it opened the folder, then never heard me again."
    /// </summary>
    private const int MaxToolCallRounds = 6;

    /// <summary>
    /// Keep at most this many past commands' worth of conversation in memory. Bounds token
    /// growth (relevant given how tight a free-tier TPM budget can be) at the cost of
    /// eventually forgetting the oldest exchanges - a deliberate, disclosed tradeoff, not
    /// unlimited memory. Trimmed only at User-message boundaries (see TrimHistory) so a
    /// tool_call is never separated from its tool_result.
    /// </summary>
    private const int MaxRememberedTasks = 6;

    private readonly IChatProvider _chatProvider;
    private readonly IFileSystemService _fileSystem;
    private readonly IApplicationService _applications;
    private readonly IComputerControlService _computerControl;
    private readonly IPolicyService _policy;
    private readonly IUserConfirmationService _confirmation;
    private readonly IAuditService _audit;
    private readonly IVerificationService _verification;
    private readonly IGestureAwarenessService? _gestureAwareness;
    private readonly IFaceRecognitionService? _faceRecognition;

    /// <summary>
    /// The running conversation across every command this instance has handled - this is
    /// what makes "reply in Hindi" on one command still apply on the next. One
    /// ToolCallingPlannerService instance = one conversation session; MainWindow creates a
    /// new one (fresh memory) only when you change AI Providers.
    /// </summary>
    private readonly List<ChatMessage> _conversationHistory = new();

    public ToolCallingPlannerService(
        IChatProvider chatProvider,
        IFileSystemService fileSystem,
        IApplicationService applications,
        IComputerControlService computerControl,
        IPolicyService policy,
        IUserConfirmationService confirmation,
        IAuditService audit,
        IVerificationService verification,
        IGestureAwarenessService? gestureAwareness = null,
        IFaceRecognitionService? faceRecognition = null)
    {
        _chatProvider = chatProvider;
        _fileSystem = fileSystem;
        _applications = applications;
        _computerControl = computerControl;
        _policy = policy;
        _confirmation = confirmation;
        _audit = audit;
        _verification = verification;
        _gestureAwareness = gestureAwareness;
        _faceRecognition = faceRecognition;

        // Restores the tail of the previous session's conversation so a relaunch doesn't start
        // completely blank - added per explicit request ("save a memory in it of every task").
        // Only ever contains plain User/Assistant text pairs (see CommitToHistory's own doc
        // comment on why tool traces are deliberately excluded), so replaying these into a fresh
        // ChatMessage list is exactly as safe as the in-memory-only version always was.
        foreach (var exchange in ConversationMemoryStore.Load())
        {
            _conversationHistory.Add(new ChatMessage { Role = ChatRole.User, Text = exchange.Command });
            _conversationHistory.Add(new ChatMessage { Role = ChatRole.Assistant, Text = exchange.Answer });
        }
    }

    public async Task<AgentTask> ExecuteAsync(
        string naturalLanguageCommand,
        Action<AgentTask>? onTaskUpdated = null,
        CancellationToken cancellationToken = default)
    {
        var task = new AgentTask
        {
            Id = Guid.NewGuid().ToString("N"),
            Command = naturalLanguageCommand,
            Status = AgentTaskStatus.Planning,
        };
        onTaskUpdated?.Invoke(task);

        await _audit.RecordAsync(new AuditEvent
        {
            Id = Guid.NewGuid().ToString("N"),
            TaskId = task.Id,
            Actor = "user",
            Action = "IssueCommand",
            Target = naturalLanguageCommand,
            Outcome = AuditOutcome.Success,
        }, cancellationToken).ConfigureAwait(false);

        var tools = ToolCatalog.Build(_fileSystem, _applications, _computerControl, _gestureAwareness, _faceRecognition);
        var toolsByName = tools.ToDictionary(t => t.ClaudeName);

        // Speed lever, not a correctness feature: only the tool schemas relevant to this
        // specific command are sent to the model. Every tool's full JSON schema is real prompt
        // weight on every request, and that's a direct, measurable latency cost on CPU-only
        // local inference. toolsByName above stays the FULL catalog regardless, so a tool call
        // is still dispatched correctly even in the (should-be-impossible) case the model
        // somehow names one it wasn't shown.
        var toolDefinitions = ToolRelevanceFilter.SelectRelevantTools(naturalLanguageCommand, tools)
            .Select(t => t.Definition)
            .ToList();

        var systemPrompt = BuildSystemPrompt();

        List<ChatMessage> messages = new(_conversationHistory)
        {
            new ChatMessage { Role = ChatRole.User, Text = naturalLanguageCommand },
        };

        var sequence = 0;

        // Both guards below exist for the same real, live symptom: a task that never reaches a
        // final text response leaves _isTaskRunning permanently true, which means continuous
        // voice mode never listens again - "stuck after folder open, doesn't hear the next
        // command" is exactly what an unbounded tool-calling loop looks like from the user's
        // side. A weak/local model is the realistic way this happens: it re-issues the same
        // already-successful call instead of concluding, or never stops calling tools at all.
        var round = 0;
        var executedCallSignatures = new HashSet<string>();

        try
        {
            task.Status = AgentTaskStatus.Running;
            onTaskUpdated?.Invoke(task);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (++round > MaxToolCallRounds)
                {
                    task.Result = "That took more steps than expected, so I stopped instead of continuing indefinitely - please try again or break it into a smaller request.";
                    CommitToHistory(naturalLanguageCommand, task.Result);
                    break;
                }

                var response = await _chatProvider.CompleteAsync(systemPrompt, messages, toolDefinitions, cancellationToken)
                    .ConfigureAwait(false);

                if (response.Refused)
                {
                    task.Status = AgentTaskStatus.Failed;
                    task.ErrorMessage = $"The AI provider declined this request: {response.RefusalReason}";
                    break;
                }

                if (!response.HasToolCalls)
                {
                    var finalText = response.FinalText ?? "(no response text)";

                    // A weak/local model (seen specifically when Groq's quota is exhausted and
                    // Ollama takes over) sometimes falls back on a generic pretrained disclaimer
                    // ("I'm text-only, I have no voice output") despite the system prompt
                    // explicitly forbidding it. Left uncorrected this is doubly harmful: it gets
                    // spoken aloud as if true, and it gets written into conversation memory below
                    // - where the model then treats its OWN past denial as an established fact
                    // and repeats/escalates it every following turn, which is exactly the "stuck
                    // in a loop" symptom reported live. Intercepting the claim here, before it
                    // reaches speech or memory, is what actually breaks that loop - a prompt rule
                    // alone couldn't, since the model was already violating one.
                    if (LooksLikeVoiceCapabilityDenial(finalText))
                    {
                        finalText = VoiceCapabilityCorrection;
                    }

                    task.Result = finalText;
                    CommitToHistory(naturalLanguageCommand, task.Result);
                    break;
                }

                messages.Add(new ChatMessage
                {
                    Role = ChatRole.Assistant,
                    Text = response.FinalText,
                    ToolCalls = response.ToolCalls.ToList(),
                });

                foreach (var call in response.ToolCalls)
                {
                    // A weak model re-issuing the exact same call (name + arguments) it already
                    // ran successfully is the concrete pattern observed live (the same
                    // FileSystem.OpenBestMatch call twice in a row) - re-running a non-idempotent
                    // tool (e.g. launching an app again, creating a file again) is also just
                    // wasted work at best. Detected here and short-circuited without re-executing
                    // the real action, with a tool result that nudges the model to stop looping
                    // instead of silently repeating it forever.
                    var signature = call.Name + "|" + call.Arguments.GetRawText();
                    if (!executedCallSignatures.Add(signature))
                    {
                        var skipStep = new TaskStep
                        {
                            Id = Guid.NewGuid().ToString("N"),
                            TaskId = task.Id,
                            Sequence = sequence++,
                            Tool = toolsByName.TryGetValue(call.Name, out var knownSkip) ? knownSkip.PolicyTool : call.Name,
                            Action = knownSkip?.PolicyAction ?? "Unknown",
                            ParametersJson = call.Arguments.GetRawText(),
                            Status = TaskStepStatus.Skipped,
                        };
                        task.Steps.Add(skipStep);
                        onTaskUpdated?.Invoke(task);

                        messages.Add(new ChatMessage
                        {
                            Role = ChatRole.Tool,
                            ToolCallId = call.Id,
                            Text = "Already done earlier in this task with the exact same arguments - do not repeat this call, give your final answer now.",
                        });
                        continue;
                    }

                    var outcomeText = await ExecuteToolAsync(task, toolsByName, call, sequence++, onTaskUpdated, cancellationToken)
                        .ConfigureAwait(false);

                    messages.Add(new ChatMessage { Role = ChatRole.Tool, ToolCallId = call.Id, Text = outcomeText });
                }
            }

            var anyStepFailed = task.Steps.Any(s => s.Status == TaskStepStatus.Failed);
            task.Status = anyStepFailed ? AgentTaskStatus.Failed : AgentTaskStatus.Verified;
        }
        catch (OperationCanceledException)
        {
            task.Status = AgentTaskStatus.Cancelled;
            task.ErrorMessage = "Task was cancelled or stopped by the user.";
        }
        catch (ChatProviderUnavailableException ex)
        {
            task.Status = AgentTaskStatus.Failed;
            task.ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            task.Status = AgentTaskStatus.Failed;
            task.ErrorMessage = ex.Message;
        }
        finally
        {
            task.CompletedAt = DateTimeOffset.UtcNow;
            onTaskUpdated?.Invoke(task);
        }

        return task;
    }

    /// <summary>
    /// Long-term memory keeps only the semantic exchange - the user's command and the
    /// assistant's final answer - never the tool_use/tool_result mechanics used to reach it.
    /// Keeping a whole task's multi-step trace in history bloats every later turn's context
    /// with information a model doesn't need to see again, and in practice increases the
    /// chance a weak/local model answers based on stale task minutiae instead of the actual
    /// new question - observed live: asked "what's your name", answered about an unrelated
    /// folder from several commands earlier. The full step-by-step trace still lives in
    /// task.Steps and the audit log for anyone who wants to see exactly what happened; this
    /// only trims what gets replayed back to the model on the next command. A useful side
    /// effect of never storing tool messages in history at all: the once-real risk of a
    /// dangling tool_use with no matching tool_result (from a cancelled/crashed task) is now
    /// structurally impossible, not just avoided by committing only at safe points.
    /// </summary>
    private void CommitToHistory(string command, string finalText)
    {
        _conversationHistory.Add(new ChatMessage { Role = ChatRole.User, Text = command });
        _conversationHistory.Add(new ChatMessage { Role = ChatRole.Assistant, Text = finalText });
        TrimHistory();
        PersistHistory();
    }

    /// <summary>
    /// Converts the (already-trimmed) in-memory history back into the plain Command/Answer pairs
    /// ConversationMemoryStore persists - _conversationHistory only ever contains alternating
    /// User/Assistant messages (see CommitToHistory), so pairing them up two-at-a-time is safe.
    /// </summary>
    private void PersistHistory()
    {
        var exchanges = new List<RememberedExchange>();
        for (var i = 0; i + 1 < _conversationHistory.Count; i += 2)
        {
            exchanges.Add(new RememberedExchange(
                _conversationHistory[i].Text ?? string.Empty,
                _conversationHistory[i + 1].Text ?? string.Empty));
        }

        ConversationMemoryStore.Save(exchanges);
    }

    /// <summary>
    /// Keeps only the last MaxRememberedTasks commands' worth of history, cutting only at
    /// User-message boundaries (each command starts with exactly one) so a tool_use is never
    /// separated from its tool_result.
    /// </summary>
    private void TrimHistory()
    {
        var userIndices = new List<int>();
        for (var i = 0; i < _conversationHistory.Count; i++)
        {
            if (_conversationHistory[i].Role == ChatRole.User)
            {
                userIndices.Add(i);
            }
        }

        if (userIndices.Count > MaxRememberedTasks)
        {
            var cutIndex = userIndices[userIndices.Count - MaxRememberedTasks];
            _conversationHistory.RemoveRange(0, cutIndex);
        }
    }

    private async Task<string> ExecuteToolAsync(
        AgentTask task,
        IReadOnlyDictionary<string, ToolSpec> toolsByName,
        ChatToolCall call,
        int sequence,
        Action<AgentTask>? onTaskUpdated,
        CancellationToken cancellationToken)
    {
        var input = call.Arguments.AsDictionary();

        var step = new TaskStep
        {
            Id = Guid.NewGuid().ToString("N"),
            TaskId = task.Id,
            Sequence = sequence,
            Tool = toolsByName.TryGetValue(call.Name, out var known) ? known.PolicyTool : call.Name,
            Action = known?.PolicyAction ?? "Unknown",
            ParametersJson = JsonSerializer.Serialize(input),
            Status = TaskStepStatus.Pending,
        };
        task.Steps.Add(step);
        onTaskUpdated?.Invoke(task);

        if (known is null)
        {
            step.Status = TaskStepStatus.Failed;
            step.ErrorMessage = $"Unknown tool '{call.Name}'.";
            return $"Error: unknown tool '{call.Name}'.";
        }

        // Observed live even after tightening the tool description and adding an explicit
        // system prompt rule: a weak local model sometimes "answers" a plain question (e.g.
        // "what's your name?") by typing the answer onto the desktop into whatever control
        // currently has keyboard focus, instead of just returning it as a normal chat response
        // - a real unwanted side effect (text can land in an arbitrary focused window), not
        // merely a wrong answer. A prompt rule alone didn't fully suppress it (same class of
        // non-compliance as the voice-capability-denial hallucination handled elsewhere in this
        // file), so this is a deterministic guard instead: if the user's own command has no
        // typing-intent wording at all, refuse to actually execute the keystrokes.
        if (call.Name == "computercontrol_type_text" && !CommandLooksLikeTypingRequest(task.Command))
        {
            step.Status = TaskStepStatus.Skipped;
            step.ErrorMessage = "Refused: the user's command had no typing intent - this looked like an attempt to answer via keystrokes instead of a normal response.";
            onTaskUpdated?.Invoke(task);
            return "Not executed: this looked like an attempt to answer the user by typing onto the desktop rather than typing into something they actually asked about. Give your answer in your normal text response instead - do not call this tool again for this.";
        }

        var decision = _policy.Evaluate(known.PolicyTool, known.PolicyAction, input.ToPolicyParameters());
        step.RiskLevel = decision.RiskLevel;

        if (!decision.Allowed)
        {
            step.Status = TaskStepStatus.Failed;
            step.ErrorMessage = decision.Reason;
            await AuditAsync(task.Id, known, input, AuditOutcome.Denied, decision.Reason, cancellationToken).ConfigureAwait(false);
            onTaskUpdated?.Invoke(task);
            return $"Denied by policy: {decision.Reason}";
        }

        if (decision.ApprovalMode == ApprovalMode.AlwaysConfirm)
        {
            step.Status = TaskStepStatus.WaitingForUser;
            onTaskUpdated?.Invoke(task);

            var summary = $"{known.PolicyTool}.{known.PolicyAction} with parameters {step.ParametersJson}";
            var confirmed = await _confirmation.ConfirmAsync(known.PolicyTool, known.PolicyAction, summary, decision.RiskLevel, cancellationToken)
                .ConfigureAwait(false);

            if (!confirmed)
            {
                step.Status = TaskStepStatus.Failed;
                step.ErrorMessage = "User declined confirmation.";
                await AuditAsync(task.Id, known, input, AuditOutcome.Denied, "User declined confirmation.", cancellationToken).ConfigureAwait(false);
                onTaskUpdated?.Invoke(task);
                return "The user declined to confirm this action. Do not retry it.";
            }
        }

        step.Status = TaskStepStatus.Running;
        onTaskUpdated?.Invoke(task);

        try
        {
            var result = await known.ExecuteAsync(input, cancellationToken).ConfigureAwait(false);
            var resultJson = JsonSerializer.Serialize(result);
            step.ResultJson = resultJson;

            var verification = await _verification.VerifyAsync(step, cancellationToken).ConfigureAwait(false);
            step.Status = verification.Verified ? TaskStepStatus.Verified : TaskStepStatus.Failed;
            step.Confidence = verification.Confidence;
            if (!verification.Verified)
            {
                step.ErrorMessage = verification.Reason;
            }

            await AuditAsync(task.Id, known, input, verification.Verified ? AuditOutcome.Success : AuditOutcome.Failure, verification.Reason, cancellationToken)
                .ConfigureAwait(false);
            onTaskUpdated?.Invoke(task);

            return verification.Verified
                ? $"Success: {resultJson}. Verification: {verification.Reason}"
                : $"Ran, but verification failed: {verification.Reason}. Raw result: {resultJson}";
        }
        catch (Exception ex)
        {
            step.Status = TaskStepStatus.Failed;
            step.ErrorMessage = ex.Message;
            await AuditAsync(task.Id, known, input, AuditOutcome.Failure, ex.Message, cancellationToken).ConfigureAwait(false);
            onTaskUpdated?.Invoke(task);
            return $"Error: {ex.Message}";
        }
    }

    private Task AuditAsync(
        string taskId,
        ToolSpec tool,
        IReadOnlyDictionary<string, JsonElement> input,
        AuditOutcome outcome,
        string reason,
        CancellationToken cancellationToken) =>
        _audit.RecordAsync(new AuditEvent
        {
            Id = Guid.NewGuid().ToString("N"),
            TaskId = taskId,
            Actor = "agent",
            Action = $"{tool.PolicyTool}.{tool.PolicyAction}",
            Target = JsonSerializer.Serialize(input),
            Outcome = outcome,
            MetadataJson = JsonSerializer.Serialize(new { reason }),
        }, cancellationToken);
}
