namespace NoniPilot.Domain.Models;

public sealed class UserProfile
{
    public required string Id { get; init; }
    public required string DisplayName { get; set; }
    public Dictionary<string, string> Preferences { get; init; } = new();
    public VoiceSettings Voice { get; init; } = new();
    public GestureSettings Gesture { get; init; } = new();
}

public sealed class VoiceSettings
{
    public bool WakeWordEnabled { get; set; }
    public string WakeWord { get; set; } = "Hey Noni";
    public bool PushToTalkOnly { get; set; } = true;
    public string? PreferredMicrophoneId { get; set; }
    public string TtsVoice { get; set; } = "default";
    public double TtsSpeed { get; set; } = 1.0;
}

public sealed class GestureSettings
{
    public bool Enabled { get; set; }
    public string? PreferredCameraId { get; set; }
    public double PointerSensitivity { get; set; } = 1.0;
    public int DwellTimeMs { get; set; } = 400;
}
