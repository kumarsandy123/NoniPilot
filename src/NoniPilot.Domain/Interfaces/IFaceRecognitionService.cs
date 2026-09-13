namespace NoniPilot.Domain.Interfaces;

/// <summary>
/// Backs "do you recognize me?"/"who am I?" style questions honestly - reports the REAL current
/// state (camera on/off, whether anyone has been enrolled yet, whether a face is currently in
/// frame, whether it matches the enrolled person) rather than the model guessing. Same pattern
/// as IGestureAwarenessService.
/// </summary>
public sealed record FaceRecognitionStatus(
    bool CameraActive,
    bool IsEnrolled,
    bool PersonDetected,
    bool IsKnownPerson);

public interface IFaceRecognitionService
{
    FaceRecognitionStatus GetStatus();

    /// <summary>
    /// Captures a short burst of live frames from the already-running camera and trains a new
    /// face model from them, replacing any previous enrollment. Returns false if no face was
    /// found in enough frames to enroll reliably (e.g. camera off, or no one in frame).
    /// </summary>
    Task<bool> EnrollAsync(CancellationToken cancellationToken = default);
}
