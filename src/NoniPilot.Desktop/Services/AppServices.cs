using System.Collections.ObjectModel;
using System.Threading;
using NoniPilot.Agent;
using NoniPilot.Agent.Providers;
using NoniPilot.Applications;
using NoniPilot.Audit;
using NoniPilot.Browser;
using NoniPilot.ComputerControl;
using NoniPilot.Domain.Interfaces;
using NoniPilot.FileSystem;
using NoniPilot.Policy;
using NoniPilot.Verification;
using NoniPilot.Voice;

namespace NoniPilot.Desktop.Services;

/// <summary>
/// The single composition root for the whole app - what MainWindow's constructor used to do
/// directly, now factored out so every page (not just the old single chat window) can share
/// the exact same service instances, chat transcript, and running planner/router.
/// </summary>
public sealed class AppServices
{
    public IFileSystemService FileSystem { get; }
    public IApplicationService Applications { get; }
    public IComputerControlService ComputerControl { get; }
    public IBrowserService Browser { get; }
    public IPolicyService Policy { get; }
    public IUserConfirmationService Confirmation { get; }
    public IAuditService Audit { get; }
    public IVerificationService Verification { get; }
    public IIntentService IntentService { get; }
    public IMicrophoneRecorder Microphone { get; }
    public ISpeechToTextService SpeechToText { get; private set; }
    public ITextToSpeechService TextToSpeech { get; }

    public IPlannerService Planner { get; private set; } = null!;
    public ChatProviderRouter Router { get; private set; } = null!;

    /// <summary>The running chat/transcript - shared by the Voice Commands page and the
    /// Dashboard's bottom command bar, so either one shows the exact same conversation.</summary>
    public ObservableCollection<ChatEntry> Chat { get; } = new();

    public PolicyGatedActionRunner ActionRunner { get; }
    public GestureEngineController GestureEngine { get; }
    public FaceRecognitionController FaceRecognition { get; }
    public CommandProcessor Commands { get; }

    /// <summary>
    /// Guards every call into Planner.ExecuteAsync - the interactive command bar/voice loop and
    /// scheduled/automation runs all share one ToolCallingPlannerService instance with a plain
    /// (non-thread-safe) conversation history list. Before real time-based scheduling existed,
    /// two callers overlapping was a remote edge case; a background scheduler firing while the
    /// user is actively chatting makes it a real, likely occurrence, so this exists specifically
    /// to serialize access rather than risk concurrent mutation of that shared list.
    /// </summary>
    public SemaphoreSlim PlannerGate { get; } = new(1, 1);

    public AutomationSequenceRunner AutomationRunner { get; }
    public TaskSchedulerService Scheduler { get; }

    /// <summary>Fired after AI Provider settings change and the planner/router are rebuilt.</summary>
    public event Action? ProviderChanged;

    /// <summary>Fired whenever a configured provider fails and the router falls back to the next one.</summary>
    public event Action<string, string>? ProviderFellBack;

    /// <summary>Set by the shell (MainWindow) so any page can ask to switch the active sidebar section.</summary>
    public Action<string>? RequestNavigate { get; set; }

    public AppServices()
    {
        FileSystem = new WindowsFileSystemService();
        Applications = new WindowsApplicationService();
        ComputerControl = new WindowsComputerControlService();
        Browser = new BrowserService();
        Policy = new PolicyService();
        Confirmation = new WpfUserConfirmationService();
        Audit = new SqliteAuditService();
        Verification = new BasicVerificationService();
        IntentService = new LocalIntentService();
        Microphone = new NAudioMicrophoneRecorder();
        TextToSpeech = VoiceServiceFactory.BuildTextToSpeech();
        SpeechToText = VoiceServiceFactory.BuildSpeechToText();

        ActionRunner = new PolicyGatedActionRunner(Policy, Confirmation, Audit);
        GestureEngine = new GestureEngineController(ComputerControl, Applications);
        FaceRecognition = new FaceRecognitionController(GestureEngine);
        Commands = new CommandProcessor(this);
        AutomationRunner = new AutomationSequenceRunner(this);
        Scheduler = new TaskSchedulerService(AutomationRunner);

        // Restores the visible chat transcript from the previous session, alongside
        // ToolCallingPlannerService separately restoring its own reasoning context from the same
        // file - added per explicit request ("save a memory in it of every task") so relaunching
        // the app doesn't look like it forgot everything, even though the underlying AI memory
        // was already being preserved.
        foreach (var exchange in ConversationMemoryStore.Load())
        {
            Chat.Add(new ChatEntry { Kind = ChatEntryKind.User, Text = exchange.Command });
            Chat.Add(new ChatEntry { Kind = ChatEntryKind.Assistant, Text = exchange.Answer });
        }

        RebuildPlanner(AiProviderSettingsStore.Load());
    }

    public void RebuildPlanner(AiProviderSettings settings)
    {
        Router = AiProviderFactory.Build(settings);
        Router.ProviderFellBack += (from, reason) => ProviderFellBack?.Invoke(from, reason);

        Planner = new ToolCallingPlannerService(Router, FileSystem, Applications, ComputerControl, Policy, Confirmation, Audit, Verification, GestureEngine, FaceRecognition);
        SpeechToText = VoiceServiceFactory.BuildSpeechToText(settings.EnableGroq, settings.SpokenLanguage);

        ProviderChanged?.Invoke();
    }
}
