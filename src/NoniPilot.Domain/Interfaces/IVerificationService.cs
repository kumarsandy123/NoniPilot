using NoniPilot.Domain.Models;

namespace NoniPilot.Domain.Interfaces;

public sealed record VerificationResult(bool Verified, string Reason, double Confidence);

/// <summary>
/// Checks the post-condition of a completed TaskStep (section 8.3/10: "never assume an
/// action succeeded"). The Phase-1 implementation only checks file/process-level
/// post-conditions; screen-state verification via UI Automation/OCR/vision lands in Phase 2.
/// </summary>
public interface IVerificationService
{
    Task<VerificationResult> VerifyAsync(TaskStep step, CancellationToken cancellationToken = default);
}
