using NoniPilot.Agent.Providers;
using NoniPilot.Domain.Interfaces;

namespace NoniPilot.Agent.Tools;

/// <summary>Builds the fixed, provider-agnostic set of tools for Phase 1: filesystem, application and computer-control actions.</summary>
public static class ToolCatalog
{
    public static IReadOnlyList<ToolSpec> Build(
        IFileSystemService fileSystem,
        IApplicationService applications,
        IComputerControlService computerControl,
        IGestureAwarenessService? gestureAwareness = null,
        IFaceRecognitionService? faceRecognition = null)
    {
        return new List<ToolSpec>
        {
            new()
            {
                ClaudeName = "face_recognize_person",
                PolicyTool = "Face",
                PolicyAction = "Recognize",
                Definition = new ChatToolDefinition(
                    "face_recognize_person",
                    "Reports whether the camera currently sees the enrolled person's face - use this " +
                    "for questions like 'do you recognize me', 'who am I', or 'is that me' instead of " +
                    "guessing or assuming. Distinct from gesture_check_visibility (hand gestures) - this " +
                    "is specifically about recognizing WHO is in frame, not hand movement.",
                    ToolSpec.Schema(new { type = "object", properties = new { } })),
                ExecuteAsync = (_, _) =>
                {
                    var status = faceRecognition?.GetStatus() ?? new FaceRecognitionStatus(false, false, false, false);
                    var note = !status.CameraActive
                        ? "The camera is currently off."
                        : !status.IsEnrolled
                            ? "No one has enrolled their face yet - the user needs to enroll first (Gesture Control page) before recognition can work."
                            : !status.PersonDetected
                                ? "The camera is on but no face is currently in view."
                                : status.IsKnownPerson
                                    ? "The enrolled person is currently recognized in frame."
                                    : "A face is in view but it does not match the enrolled person.";
                    return Task.FromResult<object?>(new
                    {
                        cameraActive = status.CameraActive,
                        isEnrolled = status.IsEnrolled,
                        personDetected = status.PersonDetected,
                        isKnownPerson = status.IsKnownPerson,
                        note,
                    });
                },
            },
            new()
            {
                ClaudeName = "gesture_check_visibility",
                PolicyTool = "Gesture",
                PolicyAction = "CheckVisibility",
                Definition = new ChatToolDefinition(
                    "gesture_check_visibility",
                    "Reports whether the gesture-tracking camera is currently on, whether a hand is " +
                    "currently detected in frame, and (if so) what hand gesture was just recognized " +
                    "(e.g. Point, Fist, OpenPalm). Call this whenever the user asks something like " +
                    "'can you see me', 'are you watching', or asks what movement/gesture you can see - " +
                    "never guess or claim to see something without calling this first. This only " +
                    "reports real hand-tracking state, not a general description of the room or the " +
                    "user's activity beyond their hand gesture.",
                    ToolSpec.Schema(new { type = "object", properties = new { } })),
                ExecuteAsync = (_, _) =>
                {
                    var status = gestureAwareness?.GetStatus() ?? new GestureAwarenessStatus(false, false, null);
                    return Task.FromResult<object?>(new
                    {
                        cameraActive = status.CameraActive,
                        handDetected = status.HandDetected,
                        currentGesture = status.CurrentGesture,
                        note = status.CameraActive
                            ? (status.HandDetected ? null : "Camera is on but no hand is currently in view.")
                            : "The gesture camera is currently off - offer to turn on Gesture Control if the user wants this.",
                    });
                },
            },
            new()
            {
                ClaudeName = "filesystem_list_directory",
                PolicyTool = "FileSystem",
                PolicyAction = "ListDirectory",
                Definition = new ChatToolDefinition(
                    "filesystem_list_directory",
                    "Lists files and subfolders directly inside a folder.",
                    ToolSpec.Schema(new
                    {
                        type = "object",
                        properties = new { path = new { type = "string", description = "Absolute folder path, e.g. C:\\Users\\me\\Downloads" } },
                        required = new[] { "path" },
                    })),
                ExecuteAsync = async (input, ct) =>
                    await fileSystem.ListDirectoryAsync(input.GetRequiredString("path"), ct),
            },
            new()
            {
                ClaudeName = "filesystem_search",
                PolicyTool = "FileSystem",
                PolicyAction = "Search",
                Definition = new ChatToolDefinition(
                    "filesystem_search",
                    "Searches a folder tree for files matching a name pattern, extension and/or size range.",
                    ToolSpec.Schema(new
                    {
                        type = "object",
                        properties = new
                        {
                            rootPath = new { type = "string", description = "Folder to search under" },
                            namePattern = new { type = "string", description = "Wildcard pattern, e.g. *.pdf" },
                            extension = new { type = "string", description = "File extension without the dot, e.g. pdf" },
                            minSizeBytes = new { type = "integer" },
                            maxSizeBytes = new { type = "integer" },
                            recursive = new { type = "boolean", description = "Defaults to true" },
                        },
                        required = new[] { "rootPath" },
                    })),
                ExecuteAsync = async (input, ct) => await fileSystem.SearchAsync(
                    input.GetRequiredString("rootPath"),
                    new FileSearchCriteria
                    {
                        NamePattern = input.GetOptionalString("namePattern"),
                        Extension = input.GetOptionalString("extension"),
                        MinSizeBytes = input.GetOptionalLong("minSizeBytes"),
                        MaxSizeBytes = input.GetOptionalLong("maxSizeBytes"),
                        Recursive = input.GetOptionalBool("recursive", true),
                    },
                    ct),
            },
            new()
            {
                ClaudeName = "filesystem_open_best_match",
                PolicyTool = "FileSystem",
                PolicyAction = "OpenBestMatch",
                Definition = new ChatToolDefinition(
                    "filesystem_open_best_match",
                    "Finds the folder or file inside parentPath whose name most closely matches nameHint - " +
                    "tolerant of typos, spelling variants, and speech-to-text mangling - and opens it in one " +
                    "step. Prefer this over separately listing then launching whenever the user's spoken or " +
                    "typed name might not be exactly right (which voice input often isn't).",
                    ToolSpec.Schema(new
                    {
                        type = "object",
                        properties = new
                        {
                            parentPath = new { type = "string", description = "Folder to search in, e.g. the Desktop path" },
                            nameHint = new { type = "string", description = "The name (best guess/transliteration) the user gave for the item" },
                        },
                        required = new[] { "parentPath", "nameHint" },
                    })),
                ExecuteAsync = async (input, ct) =>
                {
                    var parentPath = input.GetRequiredString("parentPath");
                    var nameHint = input.GetRequiredString("nameHint");

                    var entries = await fileSystem.ListDirectoryAsync(parentPath, ct);
                    var best = FuzzyNameMatcher.FindBestMatch(entries, nameHint, e => e.Name);

                    if (best is null)
                    {
                        return new
                        {
                            found = false,
                            message = $"Nothing in '{parentPath}' looked like a close match for '{nameHint}'.",
                            candidates = entries.Select(e => e.Name).ToList(),
                        };
                    }

                    var processId = await applications.LaunchAsync(best.FullPath, null, ct);
                    return new { found = true, opened = best.Name, fullPath = best.FullPath, processId };
                },
            },
            new()
            {
                ClaudeName = "filesystem_create_directory",
                PolicyTool = "FileSystem",
                PolicyAction = "CreateDirectory",
                Definition = new ChatToolDefinition(
                    "filesystem_create_directory",
                    "Creates a folder, including any missing parent folders.",
                    ToolSpec.Schema(new
                    {
                        type = "object",
                        properties = new { path = new { type = "string" } },
                        required = new[] { "path" },
                    })),
                ExecuteAsync = async (input, ct) =>
                    new { path = await fileSystem.CreateDirectoryAsync(input.GetRequiredString("path"), ct) },
            },
            new()
            {
                ClaudeName = "filesystem_create_file",
                PolicyTool = "FileSystem",
                PolicyAction = "CreateFile",
                Definition = new ChatToolDefinition(
                    "filesystem_create_file",
                    "Creates a new, empty file (including any missing parent folders). Fails if the file already exists.",
                    ToolSpec.Schema(new
                    {
                        type = "object",
                        properties = new { path = new { type = "string" } },
                        required = new[] { "path" },
                    })),
                ExecuteAsync = async (input, ct) =>
                    new { path = await fileSystem.CreateFileAsync(input.GetRequiredString("path"), ct) },
            },
            new()
            {
                ClaudeName = "filesystem_copy",
                PolicyTool = "FileSystem",
                PolicyAction = "Copy",
                Definition = new ChatToolDefinition(
                    "filesystem_copy",
                    "Copies a file or folder to a new location, leaving the original in place.",
                    ToolSpec.Schema(new
                    {
                        type = "object",
                        properties = new { sourcePath = new { type = "string" }, destinationPath = new { type = "string" } },
                        required = new[] { "sourcePath", "destinationPath" },
                    })),
                ExecuteAsync = async (input, ct) =>
                {
                    await fileSystem.CopyAsync(input.GetRequiredString("sourcePath"), input.GetRequiredString("destinationPath"), ct);
                    return new { copied = true, destinationPath = input.GetRequiredString("destinationPath") };
                },
            },
            new()
            {
                ClaudeName = "filesystem_move",
                PolicyTool = "FileSystem",
                PolicyAction = "Move",
                Definition = new ChatToolDefinition(
                    "filesystem_move",
                    "Moves a file or folder to a new location.",
                    ToolSpec.Schema(new
                    {
                        type = "object",
                        properties = new { sourcePath = new { type = "string" }, destinationPath = new { type = "string" } },
                        required = new[] { "sourcePath", "destinationPath" },
                    })),
                ExecuteAsync = async (input, ct) =>
                {
                    await fileSystem.MoveAsync(input.GetRequiredString("sourcePath"), input.GetRequiredString("destinationPath"), ct);
                    return new { moved = true, destinationPath = input.GetRequiredString("destinationPath") };
                },
            },
            new()
            {
                ClaudeName = "filesystem_rename",
                PolicyTool = "FileSystem",
                PolicyAction = "Rename",
                Definition = new ChatToolDefinition(
                    "filesystem_rename",
                    "Renames a file or folder in place.",
                    ToolSpec.Schema(new
                    {
                        type = "object",
                        properties = new { path = new { type = "string" }, newName = new { type = "string", description = "New name only, not a full path" } },
                        required = new[] { "path", "newName" },
                    })),
                ExecuteAsync = async (input, ct) =>
                {
                    await fileSystem.RenameAsync(input.GetRequiredString("path"), input.GetRequiredString("newName"), ct);
                    return new { renamed = true };
                },
            },
            new()
            {
                ClaudeName = "filesystem_delete",
                PolicyTool = "FileSystem",
                PolicyAction = "Delete",
                Definition = new ChatToolDefinition(
                    "filesystem_delete",
                    "Deletes a file or folder. Sends it to the Recycle Bin unless permanentlyDelete is true.",
                    ToolSpec.Schema(new
                    {
                        type = "object",
                        properties = new
                        {
                            path = new { type = "string" },
                            permanentlyDelete = new { type = "boolean", description = "Defaults to false - prefer the Recycle Bin" },
                        },
                        required = new[] { "path" },
                    })),
                ExecuteAsync = async (input, ct) =>
                {
                    await fileSystem.DeleteAsync(input.GetRequiredString("path"), useRecycleBin: !input.GetOptionalBool("permanentlyDelete", false), ct);
                    return new { deleted = true };
                },
            },
            new()
            {
                ClaudeName = "application_launch",
                PolicyTool = "Application",
                PolicyAction = "Launch",
                Definition = new ChatToolDefinition(
                    "application_launch",
                    "Launches an application by name or full path, e.g. 'chrome' or 'notepad.exe'.",
                    ToolSpec.Schema(new
                    {
                        type = "object",
                        properties = new { appPathOrName = new { type = "string" }, arguments = new { type = "string" } },
                        required = new[] { "appPathOrName" },
                    })),
                ExecuteAsync = async (input, ct) => new
                {
                    processId = await applications.LaunchAsync(input.GetRequiredString("appPathOrName"), input.GetOptionalString("arguments"), ct),
                },
            },
            new()
            {
                ClaudeName = "application_focus",
                PolicyTool = "Application",
                PolicyAction = "Focus",
                Definition = new ChatToolDefinition(
                    "application_focus",
                    "Brings an already-running application's window to the foreground, matched by process name or window title.",
                    ToolSpec.Schema(new
                    {
                        type = "object",
                        properties = new { processNameOrWindowTitle = new { type = "string" } },
                        required = new[] { "processNameOrWindowTitle" },
                    })),
                ExecuteAsync = async (input, ct) => new
                {
                    focused = await applications.FocusAsync(input.GetRequiredString("processNameOrWindowTitle"), ct),
                },
            },
            new()
            {
                ClaudeName = "application_close",
                PolicyTool = "Application",
                PolicyAction = "Close",
                Definition = new ChatToolDefinition(
                    "application_close",
                    "Closes a running application by its process id (see application_list_running).",
                    ToolSpec.Schema(new
                    {
                        type = "object",
                        properties = new { processId = new { type = "integer" } },
                        required = new[] { "processId" },
                    })),
                ExecuteAsync = async (input, ct) => new
                {
                    closed = await applications.CloseAsync(input.GetRequiredInt("processId"), ct),
                },
            },
            new()
            {
                ClaudeName = "application_list_running",
                PolicyTool = "Application",
                PolicyAction = "ListRunning",
                Definition = new ChatToolDefinition(
                    "application_list_running",
                    "Lists currently running applications that have a visible window, with their process id and window title.",
                    ToolSpec.Schema(new { type = "object", properties = new { } })),
                ExecuteAsync = async (_, ct) => await applications.ListRunningAsync(ct),
            },
            new()
            {
                ClaudeName = "computercontrol_move_mouse",
                PolicyTool = "ComputerControl",
                PolicyAction = "MoveMouse",
                Definition = new ChatToolDefinition(
                    "computercontrol_move_mouse",
                    "Moves the mouse cursor to absolute screen coordinates without clicking.",
                    ToolSpec.Schema(new
                    {
                        type = "object",
                        properties = new { x = new { type = "integer" }, y = new { type = "integer" } },
                        required = new[] { "x", "y" },
                    })),
                ExecuteAsync = async (input, ct) =>
                {
                    await computerControl.MoveMouseAsync(input.GetRequiredInt("x"), input.GetRequiredInt("y"), ct);
                    return new { moved = true };
                },
            },
            new()
            {
                ClaudeName = "computercontrol_click",
                PolicyTool = "ComputerControl",
                PolicyAction = "Click",
                Definition = new ChatToolDefinition(
                    "computercontrol_click",
                    "Moves the mouse to the given screen coordinates and clicks.",
                    ToolSpec.Schema(new
                    {
                        type = "object",
                        properties = new
                        {
                            x = new { type = "integer" },
                            y = new { type = "integer" },
                            button = new { type = "string", @enum = new[] { "Left", "Right", "Middle" }, description = "Defaults to Left" },
                            clickCount = new { type = "integer", description = "1 for single click, 2 for double click. Defaults to 1" },
                        },
                        required = new[] { "x", "y" },
                    })),
                ExecuteAsync = async (input, ct) =>
                {
                    var button = Enum.TryParse<MouseButton>(input.GetOptionalString("button"), ignoreCase: true, out var b) ? b : MouseButton.Left;
                    await computerControl.ClickAsync(input.GetRequiredInt("x"), input.GetRequiredInt("y"), button, input.GetOptionalInt("clickCount", 1), ct);
                    return new { clicked = true };
                },
            },
            new()
            {
                ClaudeName = "computercontrol_drag",
                PolicyTool = "ComputerControl",
                PolicyAction = "Drag",
                Definition = new ChatToolDefinition(
                    "computercontrol_drag",
                    "Presses the left mouse button at the start point, drags to the end point, and releases.",
                    ToolSpec.Schema(new
                    {
                        type = "object",
                        properties = new
                        {
                            fromX = new { type = "integer" },
                            fromY = new { type = "integer" },
                            toX = new { type = "integer" },
                            toY = new { type = "integer" },
                        },
                        required = new[] { "fromX", "fromY", "toX", "toY" },
                    })),
                ExecuteAsync = async (input, ct) =>
                {
                    await computerControl.DragAsync(
                        input.GetRequiredInt("fromX"), input.GetRequiredInt("fromY"),
                        input.GetRequiredInt("toX"), input.GetRequiredInt("toY"), ct);
                    return new { dragged = true };
                },
            },
            new()
            {
                ClaudeName = "computercontrol_scroll",
                PolicyTool = "ComputerControl",
                PolicyAction = "Scroll",
                Definition = new ChatToolDefinition(
                    "computercontrol_scroll",
                    "Scrolls the mouse wheel. Positive scrolls up, negative scrolls down.",
                    ToolSpec.Schema(new
                    {
                        type = "object",
                        properties = new { deltaWheelClicks = new { type = "integer" } },
                        required = new[] { "deltaWheelClicks" },
                    })),
                ExecuteAsync = async (input, ct) =>
                {
                    await computerControl.ScrollAsync(input.GetRequiredInt("deltaWheelClicks"), ct);
                    return new { scrolled = true };
                },
            },
            new()
            {
                ClaudeName = "computercontrol_type_text",
                PolicyTool = "ComputerControl",
                PolicyAction = "TypeText",
                Definition = new ChatToolDefinition(
                    "computercontrol_type_text",
                    "Types literal text into whatever control currently has keyboard focus - e.g. " +
                    "into Notepad, a browser's address bar, or a form field the user is looking at. " +
                    "Only call this when the user explicitly asked you to type/enter/fill in " +
                    "something on screen. NEVER call this to answer a question or say something to " +
                    "the user (e.g. your own name, a status update, a fact) - that always goes in " +
                    "your normal text response instead, never typed onto the desktop.",
                    ToolSpec.Schema(new
                    {
                        type = "object",
                        properties = new { text = new { type = "string" } },
                        required = new[] { "text" },
                    })),
                ExecuteAsync = async (input, ct) =>
                {
                    await computerControl.TypeTextAsync(input.GetRequiredString("text"), ct);
                    return new { typed = true };
                },
            },
            new()
            {
                ClaudeName = "computercontrol_send_keys",
                PolicyTool = "ComputerControl",
                PolicyAction = "SendKeys",
                Definition = new ChatToolDefinition(
                    "computercontrol_send_keys",
                    "Sends a keyboard shortcut using SendKeys-style syntax: ^ is Ctrl, % is Alt, + is Shift, and {NAME} for special keys (e.g. ^c, %{F4}, {ENTER}).",
                    ToolSpec.Schema(new
                    {
                        type = "object",
                        properties = new { keys = new { type = "string" } },
                        required = new[] { "keys" },
                    })),
                ExecuteAsync = async (input, ct) =>
                {
                    await computerControl.SendKeysAsync(input.GetRequiredString("keys"), ct);
                    return new { sent = true };
                },
            },
            new()
            {
                ClaudeName = "computercontrol_focus_window",
                PolicyTool = "ComputerControl",
                PolicyAction = "FocusWindow",
                Definition = new ChatToolDefinition(
                    "computercontrol_focus_window",
                    "Brings a window whose title contains the given text to the foreground.",
                    ToolSpec.Schema(new
                    {
                        type = "object",
                        properties = new { windowTitleContains = new { type = "string" } },
                        required = new[] { "windowTitleContains" },
                    })),
                ExecuteAsync = async (input, ct) => new
                {
                    focused = await computerControl.FocusWindowAsync(input.GetRequiredString("windowTitleContains"), ct),
                },
            },
        };
    }
}
